using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ombra.ClientCore.Teams;

namespace Ombra.App.Nightly;

/// <summary>
/// The local channel a standalone Nightly on this Mac uses to share through this Ombra's team account
/// (<see cref="NightlyTeamShare"/>; SERVER_DESIGN §7, decision 2026-10-18: Nightly has no team device of its own).
/// <para>Transport: the Mach bootstrap service <see cref="ServiceName"/> in the user's namespace. A request is a plain
/// message (header, then <c>u32 version, u32 length, UTF-8 JSON</c>) whose local port is a send-once reply right; the
/// answer goes back on that right with the same layout. Nightly blocks a worker thread on the reply (the <c>inbox</c>
/// long poll as well). Nightly (desktop <c>src/zen/teams/ZenTeamLinkWorker.js</c>) checks that the answer comes from
/// Ombra by its audit token — same user, Ombra's code requirement (<c>zen.embed.peer-requirement</c>, else the cdhash
/// Ombra writes as <c>zen.embed.peer-cdhash</c>; as for the embed channel) — and Ombra checks each request's sender
/// here: same user, the executable of a Nightly it knows (<see cref="KnownNightlyBinaries"/>), and the pinned runtime's
/// code directory hash (<see cref="NightlyRuntimePackage.Manifest.Cdhash"/>; skipped only with
/// <see cref="NightlyRuntimeOptions.AllowUnsignedVariable"/>=1, the embed channel's development switch).</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class NightlyTeamLink : IDisposable
{
    public const string ServiceName = "com.ombra.nightly-teams";
    public const uint Version = 1;
    public const int RequestId = 0x4F540001, ReplyId = 0x4F540002;
    public const int MaxJson = 768 * 1024;
    private const int BufferSize = MaxJson + 1024;

    private readonly NightlyTeamShare _share;
    private readonly Func<int, byte[], bool> _trusted;
    private readonly uint _port;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _disposed;

    /// <param name="trusted">Whether a sender (pid, audit token) may use the link; <see cref="IsKnownNightly"/> in the app.</param>
    public NightlyTeamLink(NightlyTeamShare share, string serviceName, Func<int, byte[], bool> trusted)
    {
        _share = share;
        _trusted = trusted;
        _port = NightlyMach.CheckIn(serviceName, 16);
        if (_port == 0) throw new InvalidOperationException($"Could not register {serviceName} (another Ombra has it?).");
        _thread = new Thread(Run) { IsBackground = true, Name = "nightly-teams" };
        _thread.Start();
    }

    private static NightlyTeamLink? _app;

    /// <summary>The app's link, serving <paramref name="source"/>; logged and skipped when the name is taken.</summary>
    public static void Start(INightlyTeamSource source)
    {
        if (_app is not null) return;
        try
        {
            _app = new NightlyTeamLink(new NightlyTeamShare(source, (op, e) => Services.ClientLog.Shared.Write("nightly-teams", $"{op}: {e}")),
                ServiceName, IsKnownNightly);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _app?.Dispose();
        }
        catch (InvalidOperationException e)
        {
            Services.ClientLog.Shared.Write("nightly-teams", e.Message);
        }
    }

    /// <summary>The Nightly executables Ombra lets use its team account: the runtime it runs (or <c>OMBRA_NIGHTLY_APP</c>) and an installed Nightly.</summary>
    public static IEnumerable<string> KnownNightlyBinaries()
    {
        var apps = new List<string>();
        if (Environment.GetEnvironmentVariable(NightlyRuntimeOptions.AppVariable) is { Length: > 0 } dev) apps.Add(dev);
        if (NightlyRuntimePackage.Find().App is { } installed) apps.Add(installed);
        apps.Add("/Applications/Nightly.app");
        foreach (var app in apps)
        {
            var binary = app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || Directory.Exists(app) ? Path.Combine(app, "Contents", "MacOS", "nightly") : app;
            if (File.Exists(binary)) yield return NightlyMach.RealPath(binary);
        }
    }

    public static bool IsKnownNightly(int pid, byte[] auditToken)
    {
        if (NightlyMach.ExecutableOf(pid) is not { } exe || !KnownNightlyBinaries().Contains(exe, StringComparer.Ordinal))
        {
            Services.ClientLog.Shared.Write("nightly-teams", $"refused pid {pid}: {NightlyMach.ExecutableOf(pid) ?? "gone"} is not a known Nightly");
            return false;
        }
        if (NightlyRuntimeOptions.DevelopmentUnsigned) return true;
        if (NightlyRuntimePackage.Pinned?.Cdhash is { } cdhash && NightlyMach.HasCdhash(auditToken, cdhash)) return true;
        Services.ClientLog.Shared.Write("nightly-teams", $"refused pid {pid}: not the pinned Nightly (cdhash {NightlyRuntimePackage.Pinned?.Cdhash ?? "none pinned"})");
        return false;
    }

    private void Run()
    {
        var buffer = new byte[BufferSize];
        while (!_disposed)
        {
            var kr = NightlyMach.Receive(_port, buffer);
            if (kr != NightlyMach.KernSuccess)
            {
                if (_disposed) return;
                if (kr == 0x10004004) continue; // MACH_RCV_TOO_LARGE: the kernel dropped it
                Services.ClientLog.Shared.Write("nightly-teams", $"receive failed: 0x{kr:x}");
                return;
            }
            var reply = NightlyMach.ReplyPort(buffer);
            var (pid, euid, _) = NightlyMach.Sender(buffer);
            var request = Parse(buffer);
            if (reply == 0) continue;
            if (euid != NightlyMach.EffectiveUserId || !_trusted(pid, NightlyMach.AuditToken(buffer)) || request is null)
            {
                NightlyMach.Deallocate(reply); // the sender's wait ends with "port deleted"
                continue;
            }
            _ = Task.Run(() => AnswerAsync(request, reply)); // the receive loop stays free (the inbox long poll waits)
        }
    }

    /// <summary>The JSON of a request message; null when it is not one.</summary>
    internal static JsonObject? Parse(ReadOnlySpan<byte> m)
    {
        var size = BinaryPrimitives.ReadInt32LittleEndian(m[4..]);
        if (size < 32 || BinaryPrimitives.ReadInt32LittleEndian(m[20..]) != RequestId || BinaryPrimitives.ReadUInt32LittleEndian(m[24..]) != Version) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(m[28..]);
        if (length < 0 || length > MaxJson || 32 + length > size) return null;
        try { return JsonNode.Parse(m.Slice(32, length)) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>A message of <paramref name="id"/> carrying <paramref name="json"/> (header size and id set; ports by the sender).</summary>
    internal static byte[] Message(int id, uint bits, string json)
    {
        var text = Encoding.UTF8.GetBytes(json);
        var m = new byte[32 + ((text.Length + 3) & ~3)];
        BinaryPrimitives.WriteUInt32LittleEndian(m, bits);
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(4), m.Length);
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(20), id);
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(24), Version);
        BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(28), text.Length);
        text.CopyTo(m, 32);
        return m;
    }

    private async Task AnswerAsync(JsonObject request, uint reply)
    {
        JsonObject answer;
        try { answer = await _share.HandleAsync(request, _stop.Token); }
        catch (OperationCanceledException) { NightlyMach.Deallocate(reply); return; }
        var json = answer.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) > MaxJson) json = new JsonObject { ["ok"] = false, ["error"] = "too-large" }.ToJsonString();
        var kr = NightlyMach.Send(reply, Message(ReplyId, NightlyMach.ReplyBits, json), 2000);
        if (kr != NightlyMach.KernSuccess)
        {
            NightlyMach.Deallocate(reply);
            Services.ClientLog.Shared.Write("nightly-teams", $"reply failed: 0x{kr:x}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        NightlyMach.DestroyReceiveRight(_port);
    }
}
