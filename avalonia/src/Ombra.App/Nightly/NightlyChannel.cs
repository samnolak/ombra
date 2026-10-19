using System.Runtime.Versioning;

namespace Ombra.App.Nightly;

/// <summary>
/// Ombra's end of the embed channel: a Mach bootstrap service (<see cref="ServiceName"/>, given to Nightly as
/// <c>NIGHTLY_EMBED_SERVICE</c>) and a thread that receives on it. One per runtime and app lifetime: a restarted Nightly
/// looks the same name up again. <see cref="Received"/> runs on the receive thread with the message (header first)
/// and its sender from the kernel's audit trailer; deciding whom to trust is the runtime's.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class NightlyChannel : IDisposable
{
    /// <summary>Messages carry at most <see cref="NightlyProtocol.MaxText"/> bytes of text inline plus a few fields, then
    /// the trailer; larger text (script results) comes out of line.</summary>
    private const int BufferSize = NightlyProtocol.MaxText + 1024;

    private readonly uint _port;
    private readonly Thread _thread;
    private volatile bool _disposed;

    /// <param name="Payload">The out-of-line bytes of a complex message (unmapped already); null when it had none.</param>
    /// <param name="AuditToken">The sender's audit token, for a Hello only (its code signature is checked).</param>
    public readonly record struct Message(byte[] Bytes, int SenderPid, uint SenderEuid, byte[]? Payload = null, byte[]? AuditToken = null);

    public NightlyChannel(Action<Message> received)
    {
        ServiceName = $"com.ombra.nightly-embed.{Environment.ProcessId}.{Guid.NewGuid():N}";
        // Frames and pointer moves arrive in bursts; the kernel queues up to this many before Nightly's sends time out.
        _port = NightlyMach.CheckIn(ServiceName, 64);
        if (_port == 0) throw new InvalidOperationException("Could not register the Nightly embed service (bootstrap_check_in).");
        _thread = new Thread(() => Run(received)) { IsBackground = true, Name = "nightly-embed" };
        _thread.Start();
    }

    public string ServiceName { get; }

    private void Run(Action<Message> received)
    {
        var buffer = new byte[BufferSize];
        while (!_disposed)
        {
            var kr = NightlyMach.Receive(_port, buffer);
            if (kr != NightlyMach.KernSuccess)
            {
                if (_disposed) return;
                // MACH_RCV_TOO_LARGE: the kernel dropped it; anything else means the port is gone.
                if (kr == 0x10004004) continue;
                Services.ClientLog.Shared.Write("nightly", $"embed receive failed: 0x{kr:x}");
                return;
            }
            var size = NightlyProtocol.Size(buffer);
            var (pid, euid, _) = NightlyMach.Sender(buffer);
            var copy = buffer.AsSpan(0, size).ToArray();
            var payload = NightlyMach.TakeOutOfLine(copy, NightlyProtocol.MaxScript);
            var token = NightlyProtocol.Id(copy) == NightlyProtocol.Hello ? NightlyMach.AuditToken(buffer) : null;
            try { received(new Message(copy, pid, euid, payload, token)); }
            catch (Exception e) { Services.ClientLog.Shared.Write("nightly", e); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NightlyMach.DestroyReceiveRight(_port);
    }
}
