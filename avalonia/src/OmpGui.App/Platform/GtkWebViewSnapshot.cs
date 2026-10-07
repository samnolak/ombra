using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.X11.Interop;
using OmpGui.ClientCore;

namespace OmpGui.App.Platform;

/// <summary>
/// <c>webkit_web_view_get_snapshot(VISIBLE, NONE)</c> (WebKitGTK 4.0/4.1, the library Avalonia's web view loads): the
/// visible page as a cairo surface, written to PNG with <c>cairo_surface_write_to_png_stream</c>. WebKitGTK lives on
/// Avalonia's GLib thread, so the call is made there (<see cref="GtkInteropHelper.RunOnGlibThread{T}"/>, the same path
/// Avalonia's adapter uses) and the callback runs there too.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class GtkWebViewSnapshot
{
    private const int RegionVisible = 0; // WEBKIT_SNAPSHOT_REGION_VISIBLE
    private const int OptionsNone = 0; // WEBKIT_SNAPSHOT_OPTIONS_NONE
    private const int CairoSuccess = 0; // CAIRO_STATUS_SUCCESS
    private const int CairoWriteError = 11; // CAIRO_STATUS_WRITE_ERROR

    private static readonly string[] WebKitNames = ["libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.1.so", "libwebkit2gtk-4.0.so.37", "libwebkit2gtk-4.0.so"];

    private static readonly Lock Gate = new();
    private static Library? _library;

    /// <summary>The functions used, resolved once from the loaded libraries.</summary>
    private sealed unsafe class Library
    {
        public readonly delegate* unmanaged[Cdecl]<nint, int, int, nint, delegate* unmanaged[Cdecl]<nint, nint, nint, void>, nint, void> GetSnapshot;
        public readonly delegate* unmanaged[Cdecl]<nint, nint, nint*, nint> GetSnapshotFinish;
        public readonly delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, byte*, uint, int>, nint, int> WriteToPngStream;
        public readonly delegate* unmanaged[Cdecl]<nint, void> SurfaceDestroy;
        public readonly delegate* unmanaged[Cdecl]<nint, void> ErrorFree;

        public Library()
        {
            var webkit = WebKitNames.Select(n => NativeLibrary.TryLoad(n, out var h) ? h : 0).FirstOrDefault(h => h != 0);
            if (webkit == 0) throw new InvalidOperationException("WebKitGTK (libwebkit2gtk-4.1) is not loaded.");
            var cairo = NativeLibrary.Load("libcairo.so.2");
            var glib = NativeLibrary.Load("libglib-2.0.so.0");
            GetSnapshot = (delegate* unmanaged[Cdecl]<nint, int, int, nint, delegate* unmanaged[Cdecl]<nint, nint, nint, void>, nint, void>)
                NativeLibrary.GetExport(webkit, "webkit_web_view_get_snapshot");
            GetSnapshotFinish = (delegate* unmanaged[Cdecl]<nint, nint, nint*, nint>)NativeLibrary.GetExport(webkit, "webkit_web_view_get_snapshot_finish");
            WriteToPngStream = (delegate* unmanaged[Cdecl]<nint, delegate* unmanaged[Cdecl]<nint, byte*, uint, int>, nint, int>)
                NativeLibrary.GetExport(cairo, "cairo_surface_write_to_png_stream");
            SurfaceDestroy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(cairo, "cairo_surface_destroy");
            ErrorFree = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(glib, "g_error_free");
        }
    }

    private static Library Lib
    {
        get
        {
            lock (Gate) return _library ??= new Library();
        }
    }

    /// <summary>Starts the snapshot of <paramref name="webView"/> (a <c>WebKitWebView*</c>).</summary>
    public static async Task<AgentScreenshot> CaptureAsync(nint webView)
    {
        if (webView == 0)
            throw new AgentBrowserException("no_page", "The preview's web view is not ready yet: wait for the page to load, then try again.");
        var lib = Lib;
        var done = new TaskCompletionSource<AgentScreenshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = GCHandle.Alloc(done);
        try
        {
            await GtkInteropHelper.RunOnGlibThread(() => Start(lib, webView, GCHandle.ToIntPtr(state)));
        }
        catch
        {
            state.Free(); // the call was not made: no callback will come
            throw;
        }
        return await done.Task;
    }

    private static unsafe bool Start(Library lib, nint webView, nint state)
    {
        lib.GetSnapshot(webView, RegionVisible, OptionsNone, 0, &OnSnapshot, state);
        return true;
    }

    /// <summary>The GAsyncReadyCallback: <c>(GObject *source, GAsyncResult *result, gpointer user_data)</c>, on the GLib thread.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnSnapshot(nint source, nint result, nint userData)
    {
        try
        {
            var state = GCHandle.FromIntPtr(userData);
            var target = state.Target;
            state.Free();
            if (target is not TaskCompletionSource<AgentScreenshot> done) return;
            try
            {
                done.TrySetResult(Finish(source, result));
            }
            catch (Exception e)
            {
                done.TrySetException(e is AgentBrowserException ? e : WebViewSnapshot.Failed(e));
            }
        }
        catch
        {
            // Nothing may unwind into GLib
        }
    }

    private static unsafe AgentScreenshot Finish(nint webView, nint result)
    {
        var lib = Lib;
        nint error = 0;
        var surface = lib.GetSnapshotFinish(webView, result, &error);
        if (surface == 0)
        {
            string? why = null;
            if (error != 0)
            {
                // GError { GQuark domain; gint code; gchar *message; }
                why = Marshal.PtrToStringUTF8(*(nint*)((byte*)error + 8));
                lib.ErrorFree(error);
            }
            throw new AgentBrowserException("screenshot_failed",
                "WebKitGTK could not take the screenshot" + (string.IsNullOrEmpty(why) ? "." : ": " + why) + " Is the preview panel showing the page?");
        }
        try
        {
            using var png = new MemoryStream();
            var sink = GCHandle.Alloc(png);
            int status;
            try
            {
                status = lib.WriteToPngStream(surface, &Write, GCHandle.ToIntPtr(sink));
            }
            finally
            {
                sink.Free();
            }
            if (status != CairoSuccess) throw new InvalidOperationException($"cairo could not write the PNG (status {status}).");
            return WebViewSnapshot.FromPng(png.ToArray());
        }
        finally
        {
            lib.SurfaceDestroy(surface);
        }
    }

    /// <summary>cairo_write_func_t: <c>(void *closure, const unsigned char *data, unsigned int length)</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Write(nint closure, byte* data, uint length)
    {
        try
        {
            if (GCHandle.FromIntPtr(closure).Target is not MemoryStream sink) return CairoWriteError;
            sink.Write(new ReadOnlySpan<byte>(data, checked((int)length)));
            return CairoSuccess;
        }
        catch
        {
            return CairoWriteError;
        }
    }
}
