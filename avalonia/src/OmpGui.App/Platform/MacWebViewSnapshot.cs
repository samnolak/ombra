using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OmpGui.ClientCore;

namespace OmpGui.App.Platform;

/// <summary>
/// <c>-[WKWebView takeSnapshotWithConfiguration:nil completionHandler:]</c> (macOS 10.13+) through the Objective-C
/// runtime: the visible page as an <c>NSImage</c>, encoded to PNG by <c>NSBitmapImageRep</c>. WebKit renders its own
/// layers for this, so it needs no screen recording permission.
/// </summary>
/// <remarks>
/// The completion handler is a block (Apple's block ABI): a stack block literal holding a GCHandle to the
/// TaskCompletionSource is copied to the heap with <c>_Block_copy</c>; WebKit copies it again for itself, and the copy
/// made here is released as soon as the call returns. The handler runs on the main thread; it frees the GCHandle and
/// never lets an exception unwind into WebKit. Call on the main thread (Avalonia's UI thread).
/// </remarks>
[SupportedOSPlatform("macos")]
internal static unsafe class MacWebViewSnapshot
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    private const int BlockHasSignature = 1 << 30;
    private const nint NSBitmapImageFileTypePng = 4;

    /// <summary>Apple's block literal; <see cref="State"/> is the variable this block captured (a GCHandle).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public BlockDescriptor* Descriptor;
        public nint State;
    }

    /// <summary>The descriptor of a block without copy/dispose helpers: its size (what _Block_copy copies) and signature.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
        public nint Signature;
    }

    private static readonly Lock Gate = new();
    private static Runtime? _runtime;

    /// <summary>Selectors, classes and the block descriptor, looked up once.</summary>
    private sealed class Runtime
    {
        public readonly nint StackBlockClass;
        public readonly BlockDescriptor* Descriptor;
        public readonly nint NSBitmapImageRep = objc_getClass("NSBitmapImageRep");
        public readonly nint NSDictionary = objc_getClass("NSDictionary");
        public readonly nint TakeSnapshot = sel_registerName("takeSnapshotWithConfiguration:completionHandler:");
        public readonly nint RespondsToSelector = sel_registerName("respondsToSelector:");
        public readonly nint CGImageForProposedRect = sel_registerName("CGImageForProposedRect:context:hints:");
        public readonly nint Alloc = sel_registerName("alloc");
        public readonly nint InitWithCGImage = sel_registerName("initWithCGImage:");
        public readonly nint RepresentationUsingType = sel_registerName("representationUsingType:properties:");
        public readonly nint Dictionary = sel_registerName("dictionary");
        public readonly nint Release = sel_registerName("release");
        public readonly nint Length = sel_registerName("length");
        public readonly nint Bytes = sel_registerName("bytes");
        public readonly nint LocalizedDescription = sel_registerName("localizedDescription");
        public readonly nint Utf8String = sel_registerName("UTF8String");

        public Runtime()
        {
            if (NSBitmapImageRep == 0 || NSDictionary == 0)
                throw new InvalidOperationException("AppKit is not loaded.");
            StackBlockClass = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_NSConcreteStackBlock");
            // Lives as long as the app: every block made here points at it
            Descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
            Descriptor->Size = (nuint)sizeof(BlockLiteral);
            // void (^)(NSImage *, NSError *)
            Descriptor->Signature = Marshal.StringToCoTaskMemUTF8("v24@?0@\"NSImage\"8@\"NSError\"16");
        }
    }

    private static Runtime Objc
    {
        get
        {
            lock (Gate) return _runtime ??= new Runtime();
        }
    }

    /// <summary>Starts the snapshot of <paramref name="webView"/> (a <c>WKWebView*</c>); the task ends on the main thread.</summary>
    public static Task<AgentScreenshot> Capture(nint webView)
    {
        if (webView == 0)
            throw new AgentBrowserException("no_page", "The preview's web view is not ready yet: wait for the page to load, then try again.");
        var rt = Objc;
        if (bool_objc_msgSend(webView, rt.RespondsToSelector, rt.TakeSnapshot) == 0)
            throw new AgentBrowserException("not_supported", "Screenshots of the preview need macOS 10.13 or later.");

        var done = new TaskCompletionSource<AgentScreenshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = GCHandle.Alloc(done);
        var literal = new BlockLiteral
        {
            Isa = rt.StackBlockClass,
            Flags = BlockHasSignature,
            Invoke = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnSnapshot,
            Descriptor = rt.Descriptor,
            State = GCHandle.ToIntPtr(state),
        };
        var block = _Block_copy(&literal);
        if (block == 0)
        {
            state.Free();
            throw new InvalidOperationException("The completion block could not be created.");
        }
        try
        {
            // nil configuration: the web view's visible bounds at the screen's scale
            void_objc_msgSend(webView, rt.TakeSnapshot, 0, block);
        }
        finally
        {
            _Block_release(block);
        }
        return done.Task;
    }

    /// <summary>The completion handler: <c>(block, NSImage *snapshotImage, NSError *error)</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSnapshot(nint block, nint image, nint error)
    {
        try
        {
            var state = GCHandle.FromIntPtr(((BlockLiteral*)block)->State);
            var target = state.Target;
            state.Free();
            if (target is not TaskCompletionSource<AgentScreenshot> done) return;
            try
            {
                done.TrySetResult(Encode(image, error));
            }
            catch (Exception e)
            {
                done.TrySetException(e is AgentBrowserException ? e : WebViewSnapshot.Failed(e));
            }
        }
        catch
        {
            // Nothing may unwind into WebKit
        }
    }

    private static AgentScreenshot Encode(nint image, nint error)
    {
        var rt = Objc;
        var pool = objc_autoreleasePoolPush();
        try
        {
            if (image == 0)
            {
                var why = error != 0 ? Describe(rt, error) : null;
                throw new AgentBrowserException("screenshot_failed",
                    "WebKit could not take the screenshot" + (string.IsNullOrEmpty(why) ? "." : ": " + why) + " Is the preview panel showing the page?");
            }
            var cgImage = intptr_objc_msgSend(image, rt.CGImageForProposedRect, 0, 0, 0); // owned by the image
            if (cgImage == 0)
                throw new AgentBrowserException("screenshot_failed", "The preview has no visible area to capture: is the preview panel open and showing the page?");
            var rep = intptr_objc_msgSend(intptr_objc_msgSend(rt.NSBitmapImageRep, rt.Alloc), rt.InitWithCGImage, cgImage);
            if (rep == 0) throw new InvalidOperationException("The snapshot could not be converted.");
            try
            {
                var properties = intptr_objc_msgSend(rt.NSDictionary, rt.Dictionary); // autoreleased
                var data = intptr_objc_msgSend(rep, rt.RepresentationUsingType, NSBitmapImageFileTypePng, properties); // autoreleased
                if (data == 0) throw new InvalidOperationException("The snapshot could not be encoded as PNG.");
                var length = checked((int)nuint_objc_msgSend(data, rt.Length));
                var bytes = intptr_objc_msgSend(data, rt.Bytes);
                if (length == 0 || bytes == 0) throw new InvalidOperationException("The snapshot is empty.");
                var png = new byte[length];
                Marshal.Copy(bytes, png, 0, length);
                return WebViewSnapshot.FromPng(png);
            }
            finally
            {
                void_objc_msgSend(rep, rt.Release);
            }
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static string? Describe(Runtime rt, nint error)
    {
        var text = intptr_objc_msgSend(error, rt.LocalizedDescription);
        return text == 0 ? null : Marshal.PtrToStringUTF8(intptr_objc_msgSend(text, rt.Utf8String));
    }

    // ── Objective-C runtime (objc_msgSend is called with the exact argument types of each method: arm64 needs that) ──

    [DllImport(ObjC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint intptr_objc_msgSend(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint intptr_objc_msgSend(nint receiver, nint selector, nint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint intptr_objc_msgSend(nint receiver, nint selector, nint a, nint b);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint intptr_objc_msgSend(nint receiver, nint selector, nint a, nint b, nint c);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nuint nuint_objc_msgSend(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte bool_objc_msgSend(nint receiver, nint selector, nint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend(nint receiver, nint selector, nint a, nint b);

    [DllImport(LibSystem)]
    private static extern nint _Block_copy(BlockLiteral* block);

    [DllImport(LibSystem)]
    private static extern void _Block_release(nint block);
}
