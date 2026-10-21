using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ombra.App.Platform.Mac;

/// <summary>
/// Whether any part of an NSWindow can be seen (AppKit's <c>occlusionState</c>: not when it is covered whole by other
/// windows, minimized, on another Space, hidden with its app or on a display that sleeps), kept current by its
/// <c>NSWindowDidChangeOcclusionStateNotification</c>. Avalonia reports none of this. Main thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacWindowOcclusion : IDisposable
{
    private const nuint OcclusionStateVisible = 1 << 1; // NSWindowOcclusionStateVisible
    private static readonly ConcurrentDictionary<nint, MacWindowOcclusion> Observers = new();
    private readonly nint _window;
    private readonly Action _changed;
    private nint _observer;

    /// <param name="changed">Called (on the main thread) after <see cref="Visible"/> was read again.</param>
    public MacWindowOcclusion(nint nsWindow, Action changed)
    {
        if (!MacObjC.IsMainThread) throw new InvalidOperationException("Window occlusion must be observed on the main thread.");
        _window = nsWindow;
        _changed = changed;
        var pool = MacObjC.PoolPush();
        try
        {
            var cls = MacObjC.DefineClass("OmbraOcclusionObserver", null,
                ("occlusionChanged:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OcclusionChanged, "v@:@"));
            _observer = MacObjC.Send(MacObjC.Send(cls, "alloc"), "init");
            if (_observer == 0) throw new InvalidOperationException("Could not create the window occlusion observer.");
            Observers[_observer] = this;
            ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)MacObjC.MsgSend)(
                Center, MacObjC.Sel("addObserver:selector:name:object:"), _observer, MacObjC.Sel("occlusionChanged:"),
                MacObjC.Str("NSWindowDidChangeOcclusionStateNotification"), nsWindow);
            Read();
        }
        catch { Dispose(); throw; }
        finally { MacObjC.PoolPop(pool); }
    }

    /// <summary>Some of the window is on screen.</summary>
    public bool Visible { get; private set; } = true;

    private static nint Center => MacObjC.Send(MacObjC.Cls("NSNotificationCenter"), "defaultCenter");

    private void Read() => Visible = (MacObjC.SendNUInt(_window, "occlusionState") & OcclusionStateVisible) != 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OcclusionChanged(nint self, nint cmd, nint notification)
    {
        try
        {
            if (!Observers.TryGetValue(self, out var owner)) return;
            owner.Read();
            owner._changed();
        }
        catch (Exception error) { Console.Error.WriteLine("Window occlusion callback failed: " + error); }
    }

    public void Dispose()
    {
        var observer = Interlocked.Exchange(ref _observer, 0);
        if (observer == 0) return;
        Observers.TryRemove(observer, out _);
        MacObjC.SendVoid(Center, "removeObserver:", observer);
        MacObjC.Release(observer);
    }
}
