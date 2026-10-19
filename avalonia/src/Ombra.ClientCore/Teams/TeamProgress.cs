using System.Text.Json;
using System.Text.Json.Nodes;
using Ombra.Core.Interop;
using Ombra.Server.Contracts;

namespace Ombra.ClientCore.Teams;

public enum ProgressStatus { Working, Waiting, Idle }

/// <summary>One device's progress on one team project, as published (<see cref="IsMine"/>) or received and verified.</summary>
public sealed record ProgressEntry(string TeamId, string ProjectId, string ProjectName, Guid MemberId, Guid DeviceId, ProgressStatus Status,
    string? Task, DateTimeOffset Updated, bool IsMine);

/// <summary>A local project (omp workspace) bound to a team project, with its opt-in switches (both off by default).</summary>
public sealed record ProgressBinding(string TeamId, string ProjectId, string GroupId, string ProjectName, bool Share = false, bool ShareTask = false);

/// <summary>progress.json next to account.json: local project path → binding. No secrets, never sent anywhere.</summary>
public sealed class ProgressSettings
{
    public const string FileName = "progress.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public Dictionary<string, ProgressBinding> Projects { get; set; } = [];

    public static ProgressSettings Load(string root)
    {
        var path = Path.Combine(root, FileName);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ProgressSettings>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

/// <summary>
/// P3 project progress (decision §8): ephemeral messages sealed by ombra-core (<c>progress.seal</c>: key from the project
/// group's MLS exporter, device signature) relayed by the server's ephemeral channel to online leaves only. Nothing is
/// stored on either side: received entries live in memory, an offline device's entries are dropped, and this device's
/// current progress is sent again whenever another device comes online (and after each reconnect). Replays: each send is
/// sealed with a fresh, strictly increasing <c>updated</c>; a receiver keeps every sender's newest one (also after the
/// sender goes offline or the stream reconnects) and drops anything not newer, and the core refuses messages older
/// than its freshness window (progress.rs MAX_AGE).
/// </summary>
public sealed class TeamProgress(TeamClient client) : IAsyncDisposable
{
    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly Dictionary<string, Mine> _mine = []; // groupId →
    private readonly Dictionary<(string ProjectId, Guid DeviceId), ProgressEntry> _peers = [];
    private readonly Dictionary<(string GroupId, Guid DeviceId), ulong> _seen = []; // newest `updated` accepted from each sender, kept while entries come and go
    private readonly Dictionary<string, ulong> _sent = []; // groupId → newest `updated` this device sealed
    private CancellationTokenSource? _loop;
    private Task? _run;

    private sealed record Mine(TeamProject Project, string Name, ProgressStatus Status, string? Task, ulong Updated);

    public TeamClient Client { get; } = client;

    /// <summary>Entries changed (on a background thread).</summary>
    public event Action? Changed;

    /// <summary>Connected to the stream (receiving).</summary>
    public bool IsLive { get; private set; }

    public IReadOnlyList<ProgressEntry> Entries
    {
        get
        {
            var me = Client.Device.Identity!;
            lock (_lock)
                return [.. _mine.Values.Select(m => new ProgressEntry(m.Project.TeamId, m.Project.ProjectId, m.Name, me.MemberId, me.DeviceId, m.Status,
                    m.Task, DateTimeOffset.FromUnixTimeSeconds((long)m.Updated), true)), .. _peers.Values];
        }
    }

    /// <summary>
    /// Publishes this device's progress on <paramref name="project"/>; unchanged status and task send nothing.
    /// <paramref name="task"/> is null unless the user opted in to sharing task titles.
    /// </summary>
    public async Task PublishAsync(TeamProject project, string projectName, ProgressStatus status, string? task, CancellationToken ct = default)
    {
        Mine m;
        lock (_lock)
        {
            if (_mine.TryGetValue(project.GroupId, out var old) && old.Status == status && old.Task == task && old.Name == projectName) return;
            _mine[project.GroupId] = m = new Mine(project, projectName, status, task, Client.Server.Now);
        }
        Changed?.Invoke();
        await SendAsync(m, ct);
    }

    /// <summary>Opt-out: a last "idle" without a task, then nothing more is sent for the project.</summary>
    public async Task StopAsync(TeamProject project, CancellationToken ct = default)
    {
        Mine? m;
        lock (_lock)
        {
            if (!_mine.Remove(project.GroupId, out m)) return;
        }
        Changed?.Invoke();
        await SendAsync(m with { Status = ProgressStatus.Idle, Task = null, Updated = Client.Server.Now }, ct);
    }

    /// <summary>This device's current progress on every published project, sealed again for the current epochs.</summary>
    public async Task ResendAsync(CancellationToken ct = default)
    {
        List<Mine> all;
        lock (_lock) all = [.. _mine.Values];
        foreach (var m in all) await SendAsync(m, ct);
    }

    private async Task SendAsync(Mine m, CancellationToken ct)
    {
        ulong at;
        lock (_lock)
        {
            // Sealed fresh each time (never an old `updated`), strictly after the last one sent: receivers drop anything not newer
            at = Math.Max(Client.Server.Now, _sent.GetValueOrDefault(m.Project.GroupId) + 1);
            _sent[m.Project.GroupId] = at;
        }
        var args = new JsonObject { ["groupId"] = m.Project.GroupId, ["status"] = Wire(m.Status), ["projectName"] = m.Name, ["now"] = at };
        if (m.Task is { Length: > 0 } t) args["task"] = t;
        JsonNode sealedMsg;
        try { sealedMsg = Client.Device.Call("progress.seal", args); }
        catch (OmbraException) { return; } // the group closed or we left it: nothing to publish
        try
        {
            await Client.Server.PostEnvelopeAsync<OpPutResponse>($"v1/groups/{m.Project.GroupId}/ops", EnvelopeKinds.OpPut, Convert.FromHexString(m.Project.GroupId),
                new OpPutBody(sealedMsg["epoch"]!.GetValue<ulong>(), TeamsHex.Bytes(sealedMsg["ciphertext"]!), true).ToCbor(), ct: ct);
        }
        catch (TeamsServerException e) when (e.Status is 409 or 429) { } // epoch moved on or rate limit: the next change or re-send carries it
        catch (HttpRequestException) { } // offline: re-sent after reconnecting
    }

    /// <summary>A stream event: progress of another device, or presence (re-send on "online", forget on "offline").</summary>
    public async Task HandleAsync(StreamEvent e, CancellationToken ct = default)
    {
        var me = Client.Device.Identity!.DeviceId;
        if (e.Type == StreamEventTypes.Ephemeral && e is { GroupId: { } gid, SenderDeviceId: { } sender, Ciphertext: { } c } && sender != me)
        {
            if (Open(gid, sender, c) is not { } entry) return;
            lock (_lock)
            {
                // The watermark outlives the entry (offline, reconnect): a message replayed after the entry is gone stays dropped
                var mark = (gid, entry.DeviceId);
                var at = (ulong)entry.Updated.ToUnixTimeSeconds();
                if (_seen.TryGetValue(mark, out var last) && at <= last) return; // an older (or the same) message replayed
                _seen[mark] = at;
                _peers[(entry.ProjectId, entry.DeviceId)] = entry;
            }
            Changed?.Invoke();
        }
        else if (e.Type == StreamEventTypes.Presence && e.DeviceId is { } d && d != me)
        {
            if (e.State == "offline")
            {
                bool removed;
                lock (_lock) removed = _peers.Keys.Where(k => k.DeviceId == d).ToList().Count(k => _peers.Remove(k)) > 0;
                if (removed) Changed?.Invoke();
            }
            else if (e.State == "online") await ResendAsync(ct);
        }
    }

    private ProgressEntry? Open(string groupId, Guid sender, byte[] ciphertext)
    {
        JsonNode p;
        try
        {
            p = Client.Device.Call("progress.open", new()
            {
                ["groupId"] = groupId, ["ciphertext"] = TeamsHex.Of(ciphertext), ["senderDeviceId"] = TeamsHex.Of(sender), ["now"] = Client.Server.Now,
            });
        }
        catch (OmbraException) { return null; } // not ours to read, another epoch, forged or replayed: dropped
        var status = p["status"]!.GetValue<string>() switch { "working" => ProgressStatus.Working, "waiting" => ProgressStatus.Waiting, _ => ProgressStatus.Idle };
        var teamId = Client.ProjectGroups().FirstOrDefault(g => g.GroupId == groupId)?.TeamId ?? "";
        return new ProgressEntry(teamId, p["projectId"]!.GetValue<string>(), p["projectName"]!.GetValue<string>(), TeamsHex.ToGuid(p["memberId"]!.GetValue<string>()),
            TeamsHex.ToGuid(p["deviceId"]!.GetValue<string>()), status, p["task"]?.GetValue<string>(),
            DateTimeOffset.FromUnixTimeSeconds((long)p["updated"]!.GetValue<ulong>()), false);
    }

    internal static string Wire(ProgressStatus s) => s switch { ProgressStatus.Working => "working", ProgressStatus.Waiting => "waiting", _ => "idle" };

    /// <summary>Listens on /v1/stream until <see cref="DisposeAsync"/>, reconnecting after <see cref="ReconnectDelay"/>.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        var cts = _loop = new CancellationTokenSource();
        _run = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await using var stream = await TeamStream.ConnectAsync(Client.Server, cts.Token);
                    IsLive = true;
                    await ResendAsync(cts.Token);
                    await stream.ReadAsync(e => HandleAsync(e, cts.Token), cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
                catch (Exception) { } // network or server: retry below
                finally
                {
                    IsLive = false;
                    bool had;
                    lock (_lock) { had = _peers.Count > 0; _peers.Clear(); } // presence is unknown while disconnected
                    if (had) Changed?.Invoke();
                }
                try { await Task.Delay(ReconnectDelay, cts.Token); } catch (OperationCanceledException) { return; }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_loop is { } cts)
        {
            _loop = null;
            await cts.CancelAsync();
            if (_run is { } r) { try { await r; } catch (OperationCanceledException) { } }
            cts.Dispose();
        }
    }
}
