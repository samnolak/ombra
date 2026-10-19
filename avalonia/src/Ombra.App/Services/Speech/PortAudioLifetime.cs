using PortAudioSharp;

namespace Ombra.App.Services.Speech;

/// <summary>
/// Every capture and device probe owns a matching native initialize/terminate pair. PortAudio reference-counts these
/// pairs: overlapping users share the device table, and the next user after the last release enumerates it afresh.
/// All control calls use the same lock; audio callbacks must not take it.
/// </summary>
internal sealed class PortAudioLifetime(Action initialize, Action terminate)
{
    internal static PortAudioLifetime Shared { get; } = new(PortAudio.Initialize, PortAudio.Terminate);
    internal object Gate { get; } = new();
    private readonly Action _terminate = terminate;

    internal static (int Count, int DefaultInput) ProbeDevices()
    {
        lock (Shared.Gate)
        {
            using var audio = Shared.Acquire();
            var count = PortAudio.DeviceCount;
            if (count < 0) throw new PortAudioException((ErrorCode)count, "Error enumerating PortAudio devices");
            return (count, PortAudio.DefaultInputDevice);
        }
    }

    internal IDisposable Acquire()
    {
        lock (Gate)
        {
            initialize(); // A failed initialization must not be matched by Terminate.
            return new Lease(this);
        }
    }

    private sealed class Lease(PortAudioLifetime owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            lock (owner.Gate)
            {
                if (_released) return;
                owner._terminate();
                _released = true;
            }
        }
    }
}
