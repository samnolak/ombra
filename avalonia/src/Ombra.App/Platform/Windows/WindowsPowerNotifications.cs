using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ombra.App.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed class WindowsPowerNotifications : IDisposable
{
    // Native code owns the function pointer until unregister succeeds, including an exceptional failed unregister.
    private static readonly ConcurrentDictionary<nint, WindowsPowerNotifications> Registrations = new();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint PowerCallback(nint context, uint type, nint setting);
    [StructLayout(LayoutKind.Sequential)]
    private struct Subscription { public nint Callback; public nint Context; }
    private readonly PowerCallback _callback;
    private nint _handle;
    private int _disposed;

    public WindowsPowerNotifications(Action<bool> changed)
    {
        _callback = (_, type, _) =>
        {
            try
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    if (type == 4) changed(true); // PBT_APMSUSPEND
                    else if (type is 7 or 18) changed(false); // PBT_APMRESUMESUSPEND / AUTOMATIC
                }
            }
            catch (Exception error) { Console.Error.WriteLine("Power notification callback failed: " + error); }
            return 0;
        };
        var subscription = new Subscription { Callback = Marshal.GetFunctionPointerForDelegate(_callback) };
        var error = PowerRegisterSuspendResumeNotification(2, ref subscription, out _handle); // DEVICE_NOTIFY_CALLBACK
        if (error != 0) throw new Win32Exception((int)error, "PowerRegisterSuspendResumeNotification failed.");
        if (_handle == 0) throw new InvalidOperationException("Windows returned no power subscription handle.");
        Registrations[_handle] = this;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            var error = PowerUnregisterSuspendResumeNotification(handle);
            if (error != 0) Console.Error.WriteLine("PowerUnregisterSuspendResumeNotification failed: " + error);
            else Registrations.TryRemove(handle, out _);
        }
        GC.KeepAlive(_callback);
    }

    [DllImport("PowrProf.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref Subscription recipient, out nint handle);
    [DllImport("PowrProf.dll")]
    private static extern uint PowerUnregisterSuspendResumeNotification(nint handle);
}
