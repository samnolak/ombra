using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OmpGui.ClientCore;

namespace OmpGui.App.Platform;

/// <summary>
/// <c>ICoreWebView2::CapturePreview(PNG, stream, handler)</c>: the visible page as PNG into a memory stream
/// (<c>CreateStreamOnHGlobal</c>), read back when the completion handler is called. The handler is a minimal COM object
/// made here (IUnknown + Invoke) holding a GCHandle to the TaskCompletionSource. Call on the UI thread (WebView2's).
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WebView2Snapshot
{
    /// <summary>ICoreWebView2 vtable slot of CapturePreview (IUnknown's 3, then ICoreWebView2's methods in order).</summary>
    private const int CapturePreviewSlot = 30;
    private const int PngFormat = 0; // COREWEBVIEW2_CAPTURE_PREVIEW_IMAGE_FORMAT_PNG
    private const int SOk = 0;
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int StreamSeekSet = 0;
    private const int StreamSeekEnd = 2;
    // IStream vtable slots
    private const int ReleaseSlot = 2;
    private const int ReadSlot = 3;
    private const int SeekSlot = 5;

    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidCompletedHandler = new("697E05E9-3D8F-45FA-96F4-8FFE1EDEDAF5");

    /// <summary>The handler object: a vtable pointer first, as COM expects.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Handler
    {
        public nint* Vtable;
        public int References;
        public nint State; // GCHandle to the Pending capture
    }

    private sealed record Pending(TaskCompletionSource<AgentScreenshot> Done, nint Stream);

    private static readonly nint* HandlerVtable = CreateVtable();

    private static nint* CreateVtable()
    {
        // Lives as long as the app
        var vtable = (nint*)NativeMemory.AllocZeroed((nuint)(4 * sizeof(nint)));
        vtable[0] = (nint)(delegate* unmanaged[Stdcall]<Handler*, Guid*, nint*, int>)&QueryInterface;
        vtable[1] = (nint)(delegate* unmanaged[Stdcall]<Handler*, uint>)&AddRef;
        vtable[2] = (nint)(delegate* unmanaged[Stdcall]<Handler*, uint>)&ReleaseExport;
        vtable[3] = (nint)(delegate* unmanaged[Stdcall]<Handler*, int, int>)&Invoke;
        return vtable;
    }

    /// <summary>Starts the capture on <paramref name="coreWebView2"/> (an AddRef'd <c>ICoreWebView2*</c>, released here).</summary>
    public static Task<AgentScreenshot> Capture(nint coreWebView2)
    {
        if (coreWebView2 == 0)
            throw new AgentBrowserException("no_page", "The preview's web view is not ready yet: wait for the page to load, then try again.");
        try
        {
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(0, 1, out var stream));
            var done = new TaskCompletionSource<AgentScreenshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = (Handler*)NativeMemory.AllocZeroed((nuint)sizeof(Handler));
            handler->Vtable = HandlerVtable;
            handler->References = 1;
            handler->State = GCHandle.ToIntPtr(GCHandle.Alloc(new Pending(done, stream))); // the stream is released with it
            try
            {
                var capture = (delegate* unmanaged[Stdcall]<nint, int, nint, Handler*, int>)(*(nint**)coreWebView2)[CapturePreviewSlot];
                var hr = capture(coreWebView2, PngFormat, stream, handler);
                // Observed by the caller's await (a thrown exception would leave the task unobserved)
                if (hr < 0) done.TrySetException(WebViewSnapshot.Failed(Marshal.GetExceptionForHR(hr) ?? new COMException("CapturePreview failed", hr)));
            }
            finally
            {
                ReleaseHandler(handler); // WebView2 holds its own reference until it calls back
            }
            return done.Task;
        }
        finally
        {
            Marshal.Release(coreWebView2);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(Handler* self, Guid* iid, nint* result)
    {
        if (*iid == IidUnknown || *iid == IidCompletedHandler)
        {
            Interlocked.Increment(ref self->References);
            *result = (nint)self;
            return SOk;
        }
        *result = 0;
        return ENoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(Handler* self) => (uint)Interlocked.Increment(ref self->References);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseExport(Handler* self) => ReleaseHandler(self);

    private static uint ReleaseHandler(Handler* self)
    {
        var left = Interlocked.Decrement(ref self->References);
        if (left == 0)
        {
            // Never called back (the web view went away): the capture fails, the stream and the handle are freed
            var state = GCHandle.FromIntPtr(self->State);
            if (state.Target is Pending pending)
            {
                pending.Done.TrySetException(new AgentBrowserException("screenshot_failed", "The preview closed before the screenshot was taken."));
                ComRelease(pending.Stream);
            }
            state.Free();
            NativeMemory.Free(self);
        }
        return (uint)left;
    }

    /// <summary><c>ICoreWebView2CapturePreviewCompletedHandler::Invoke(HRESULT errorCode)</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Invoke(Handler* self, int errorCode)
    {
        try
        {
            if (GCHandle.FromIntPtr(self->State).Target is not Pending pending) return SOk;
            try
            {
                if (errorCode < 0)
                    throw new AgentBrowserException("screenshot_failed",
                        $"WebView2 could not take the screenshot (0x{errorCode:X8}). Is the preview panel showing the page?");
                pending.Done.TrySetResult(WebViewSnapshot.FromPng(ReadAll(pending.Stream)));
            }
            catch (Exception e)
            {
                pending.Done.TrySetException(e is AgentBrowserException ? e : WebViewSnapshot.Failed(e));
            }
        }
        catch
        {
            // Nothing may unwind into WebView2
        }
        return SOk;
    }

    private static byte[] ReadAll(nint stream)
    {
        var vtable = *(nint**)stream;
        var seek = (delegate* unmanaged[Stdcall]<nint, long, int, ulong*, int>)vtable[SeekSlot];
        var read = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)vtable[ReadSlot];
        ulong size;
        Marshal.ThrowExceptionForHR(seek(stream, 0, StreamSeekEnd, &size));
        Marshal.ThrowExceptionForHR(seek(stream, 0, StreamSeekSet, null));
        var png = new byte[checked((int)size)];
        var total = 0;
        fixed (byte* start = png)
        {
            while (total < png.Length)
            {
                uint got;
                Marshal.ThrowExceptionForHR(read(stream, start + total, (uint)(png.Length - total), &got));
                if (got == 0) break;
                total += (int)got;
            }
        }
        return total == png.Length ? png : png[..total];
    }

    private static void ComRelease(nint unknown)
    {
        if (unknown != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[ReleaseSlot])(unknown);
    }

    [DllImport("ole32.dll")]
    private static extern int CreateStreamOnHGlobal(nint hGlobal, int deleteOnRelease, out nint stream);
}
