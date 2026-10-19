using System.Runtime.InteropServices;

namespace Ombra.App.Nightly;

// The team link (NightlyTeamLink): who sent a request, by the kernel's audit token, and the reply right the request
// carries. Whether the sender's code is the pinned Nightly: NightlyMach.Trust.cs.
internal static unsafe partial class NightlyMach
{
    public const int AuditTokenSize = 32;
    /// <summary>Header bits of a reply sent on a received send-once right (MACH_MSG_TYPE_MOVE_SEND_ONCE).</summary>
    public const uint ReplyBits = 18;

    /// <summary>The audit token of a message <see cref="Receive"/> filled (from its trailer).</summary>
    public static byte[] AuditToken(ReadOnlySpan<byte> message)
    {
        var size = BitConverter.ToInt32(message[4..]);
        var token = ((size + 3) & ~3) + 20; // trailer: type, size, seqno, sender security token (8), then the audit token
        return message.Slice(token, AuditTokenSize).ToArray();
    }

    /// <summary>The reply right a request carries (the sender's local port, received as the remote port).</summary>
    public static uint ReplyPort(ReadOnlySpan<byte> message) => BitConverter.ToUInt32(message[8..]);

    /// <summary>A send right to the bootstrap service <paramref name="name"/>; 0 when nobody has it (the client side of the team link, as Nightly).</summary>
    public static uint LookUp(string name) => bootstrap_look_up(BootstrapPort, name, out var port) == KernSuccess ? port : 0;

    /// <summary>A fresh receive right (a reply port); 0 on failure.</summary>
    public static uint NewReceivePort() => mach_port_allocate(TaskSelf, PortRightReceive, out var port) == KernSuccess ? port : 0;

    /// <summary>Sends a request (header bits copy-send | make-send-once) to <paramref name="service"/> with <paramref name="reply"/> as its reply right.</summary>
    public static int SendRequest(uint service, uint reply, byte[] message, uint timeoutMs)
    {
        BitConverter.GetBytes(service).CopyTo(message, 8);
        BitConverter.GetBytes(reply).CopyTo(message, 12);
        fixed (byte* b = message)
            return mach_msg(b, MachSendMsg | MachSendTimeout, (uint)message.Length, 0, 0, timeoutMs, 0);
    }

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int bootstrap_look_up(uint bootstrapPort, string serviceName, out uint port);

    [LibraryImport(LibSystem)]
    private static partial int mach_port_allocate(uint task, int right, out uint name);
}
