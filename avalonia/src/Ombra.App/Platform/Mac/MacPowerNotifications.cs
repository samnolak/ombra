using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ombra.App.Platform.Mac;

/// <summary>NSWorkspace's real sleep/wake notifications, owned by one window.</summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacPowerNotifications : IDisposable
{
    private static readonly ConcurrentDictionary<nint, MacPowerNotifications> Observers = new();
    private readonly Action<bool> _changed;
    private readonly nint _center;
    private nint _observer;

    public MacPowerNotifications(Action<bool> changed)
    {
        if (!MacObjC.IsMainThread) throw new InvalidOperationException("NSWorkspace power notifications must be subscribed on the main thread.");
        _changed = changed;
        var pool = MacObjC.PoolPush();
        try
        {
            var workspace = MacObjC.Send(MacObjC.Cls("NSWorkspace"), "sharedWorkspace");
            _center = MacObjC.Send(workspace, "notificationCenter");
            if (_center == 0) throw new PlatformNotSupportedException("NSWorkspace.notificationCenter is unavailable; AppKit must be loaded.");
            var cls = MacObjC.DefineClass("OmbraPowerObserver", null,
                ("willSleep:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&WillSleep, "v@:@"),
                ("didWake:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DidWake, "v@:@"));
            _observer = MacObjC.Send(MacObjC.Send(cls, "alloc"), "init");
            if (_observer == 0) throw new InvalidOperationException("Could not create the NSWorkspace power observer.");
            Observers[_observer] = this;
            Add("willSleep:", "NSWorkspaceWillSleepNotification");
            Add("didWake:", "NSWorkspaceDidWakeNotification");
        }
        catch { Dispose(); throw; }
        finally { MacObjC.PoolPop(pool); }
    }

    private void Add(string selector, string name) =>
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)MacObjC.MsgSend)(
            _center, MacObjC.Sel("addObserver:selector:name:object:"), _observer, MacObjC.Sel(selector), MacObjC.Str(name), 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WillSleep(nint self, nint cmd, nint notification) => Changed(self, true);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidWake(nint self, nint cmd, nint notification) => Changed(self, false);

    private static void Changed(nint self, bool sleeping)
    {
        try { if (Observers.TryGetValue(self, out var owner)) owner._changed(sleeping); }
        catch (Exception error) { Console.Error.WriteLine("Power notification callback failed: " + error); }
    }

    public void Dispose()
    {
        var observer = Interlocked.Exchange(ref _observer, 0);
        if (observer == 0) return;
        Observers.TryRemove(observer, out _);
        MacObjC.SendVoid(_center, "removeObserver:", observer);
        MacObjC.Release(observer);
    }
}
