using System.Text.Json.Nodes;
using Ombra.Core.Interop;

namespace Ombra.ClientCore.Teams;

/// <summary>
/// <see cref="ITeamItems"/> over <see cref="TeamClient"/> and ombra-core. Every own operation is applied locally and queued
/// in the core outbox inside one core transaction; the network part (<see cref="DeliverAsync"/>) only moves queued bytes
/// and runs the sync loop, so it may fail or be interrupted at any point without losing or duplicating anything.
/// </summary>
public sealed partial class TeamItems : ITeamItems
{
    private readonly TeamClient _client;
    private readonly SemaphoreSlim _deliver = new(1, 1);

    public TeamItems(TeamClient client)
    {
        _client = client;
        _client.OpsReceived += groups =>
        {
            foreach (var t in groups.Select(TeamOfGroup).OfType<string>().Distinct()) ItemsChanged?.Invoke(t);
        };
    }

    public event Action<string>? ItemsChanged;

    private TeamsDevice Device => _client.Device;
    private string Me => TeamsHex.Of((Device.Identity ?? throw new InvalidOperationException("device not initialised")).MemberId);
    private static ulong Now => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public async Task<IReadOnlyList<TeamItemCopy>> SendAsync(string teamId, TeamItemDraft draft, IReadOnlyList<string> recipientMemberIds,
        CancellationToken ct = default)
    {
        if (recipientMemberIds.Count == 0) throw new ArgumentException("at least one recipient", nameof(recipientMemberIds));
        var copies = new List<TeamItemCopy>();
        foreach (var r in recipientMemberIds.Distinct())
        {
            if (r == Me) continue;
            await _client.EnsurePairGroupAsync(teamId, r, ct);
            var a = new JsonObject { ["teamId"] = teamId, ["kind"] = draft.Kind, ["recipients"] = new JsonArray(r), ["now"] = Now };
            Put(a, draft);
            var c = Device.Call("item.send_addressed", a)["copies"]![0]!;
            copies.Add(new TeamItemCopy(r, c["groupId"]!.GetValue<string>(), c["itemId"]!.GetValue<string>(), c["opId"]!.GetValue<string>()));
        }
        ItemsChanged?.Invoke(teamId);
        await TryFlushAsync(ct);
        return copies;
    }

    public async Task EditAsync(string teamId, string itemId, ulong baseRev, TeamItemDraft delta, CancellationToken ct = default)
    {
        var it = Get(teamId, Me, itemId) ?? throw new InvalidOperationException("unknown item");
        var d = new JsonObject();
        Put(d, delta);
        d.Remove("kind");
        await EnqueueAsync(teamId, GroupOf(it), new JsonObject
        {
            ["opType"] = "item.edit", ["itemId"] = itemId, ["rev"] = baseRev + 1,
            ["payload"] = new JsonObject { ["baseRev"] = baseRev, ["delta"] = d },
        }, ct);
    }

    public async Task DeleteAsync(string teamId, string authorMemberId, string itemId, CancellationToken ct = default)
    {
        var it = Get(teamId, authorMemberId, itemId) ?? throw new InvalidOperationException("unknown item");
        await EnqueueAsync(teamId, GroupOf(it), new JsonObject { ["opType"] = "item.delete", ["itemId"] = itemId, ["author"] = authorMemberId, ["payload"] = new JsonObject() }, ct);
    }

    public async Task SetStatusAsync(string teamId, string authorMemberId, string itemId, string status, string? note = null, CancellationToken ct = default)
    {
        var gid = _client.PairGroupId(teamId, authorMemberId) ?? await _client.EnsurePairGroupAsync(teamId, authorMemberId, ct);
        var p = new JsonObject { ["status"] = status };
        if (note is not null) p["note"] = note;
        await EnqueueAsync(teamId, gid, new JsonObject
        {
            ["opType"] = "item.status", ["itemId"] = itemId, ["author"] = authorMemberId, ["recipient"] = Me, ["rev"] = 0, ["payload"] = p,
        }, ct);
    }

    public IReadOnlyList<TeamItem> List(string teamId, int limit = 200, ulong? before = null)
    {
        var a = new JsonObject { ["teamId"] = teamId, ["limit"] = limit };
        if (before is { } b) a["before"] = b;
        return Device.Call("item.list", a)["items"]!.AsArray().Select(i => Parse(teamId, i!)).ToList();
    }

    public TeamItem? Get(string teamId, string authorMemberId, string itemId)
    {
        var r = Device.Call("item.get", new() { ["teamId"] = teamId, ["author"] = authorMemberId, ["itemId"] = itemId });
        return r is JsonObject o && o.Count > 0 ? Parse(teamId, o) : null;
    }

    public int PendingCount(string? teamId = null) => _client.OutboxCount(teamId);

    public async Task<TeamDeliveryResult> DeliverAsync(CancellationToken ct = default)
    {
        await _deliver.WaitAsync(ct);
        try
        {
            var first = await _client.FlushOutboxAsync(ct);
            var sync = await _client.SyncAsync(ct);
            var second = await _client.FlushOutboxAsync(ct);
            return new TeamDeliveryResult(sync.Processed, first.Sent + second.Sent, sync.Rejected.Count + first.Dropped + second.Dropped, sync.Rejected);
        }
        finally { _deliver.Release(); }
    }

    private async Task EnqueueAsync(string teamId, string gid, JsonObject a, CancellationToken ct)
    {
        a["groupId"] = gid;
        a["now"] = Now;
        Device.Call("outbox.enqueue", a);
        ItemsChanged?.Invoke(teamId);
        await TryFlushAsync(ct);
    }

    /// <summary>Best effort right after a local change; offline the op simply stays queued for <see cref="DeliverAsync"/>.</summary>
    private async Task TryFlushAsync(CancellationToken ct)
    {
        if (!await _deliver.WaitAsync(0, ct)) return;
        try { await _client.FlushOutboxAsync(ct); }
        catch (Exception e) when (e is HttpRequestException or TeamsServerException or TaskCanceledException) { }
        finally { _deliver.Release(); }
    }

    private string GroupOf(TeamItem it)
    {
        if (it.RecipientMemberId is { } r)
            return _client.PairGroupId(it.TeamId, it.AuthorMemberId == Me ? r : it.AuthorMemberId)
                   ?? throw new InvalidOperationException("no pair group for this item");
        if (it.SpaceId == it.TeamId) return _client.TeamGroupId(it.TeamId) ?? throw new InvalidOperationException("no team group");
        return Device.Call("group.list")["groups"]!.AsArray()
                   .Where(g => g!["teamId"]!.GetValue<string>() == it.TeamId && !g["closed"]!.GetValue<bool>())
                   .FirstOrDefault(g => g!["descriptor"]?["projectId"]?.GetValue<string>() == it.SpaceId)?["groupId"]?.GetValue<string>()
               ?? throw new InvalidOperationException("no project group for this item");
    }

    private string? TeamOfGroup(string gid) =>
        Device.Call("group.list")["groups"]!.AsArray().FirstOrDefault(g => g!["groupId"]!.GetValue<string>() == gid)?["teamId"]?.GetValue<string>();

    private static void Put(JsonObject a, TeamItemDraft d)
    {
        a["kind"] = d.Kind;
        if (d.Body is not null) a["body"] = d.Body.DeepClone();
        if (d.Anchor is not null) a["anchor"] = d.Anchor.DeepClone();
        if (d.Context is not null) a["context"] = d.Context.DeepClone();
        if (d.Attachments is not null) a["attachments"] = d.Attachments.DeepClone();
    }

    private static string? Str(JsonNode? n) => n?.GetValue<string>();

    internal static TeamItem Parse(string teamId, JsonNode j)
    {
        var rep = j["replacedByOtherDevice"];
        return new TeamItem(teamId, Str(j["author"])!, Str(j["itemId"])!, Str(j["kind"]) ?? "", j["rev"]!.GetValue<ulong>(), j["createdAt"]!.GetValue<ulong>(),
            Str(j["space"]), Str(j["recipient"]), j["deleted"]!.GetValue<bool>(),
            j["anchor"]?.DeepClone(), j["context"]?.DeepClone(), j["body"]?.DeepClone(), j["attachments"]?.DeepClone(),
            j["statuses"]!.AsArray().Select(s => new TeamItemStatus(Str(s!["recipient"])!, s["seen"] is not null, Str(s["status"]), Str(s["note"]))).ToList(),
            rep is null ? null : new TeamItemReplaced(rep["rev"]!.GetValue<ulong>(), Str(rep["byDevice"])!, rep["anchor"]?.DeepClone(), rep["context"]?.DeepClone(), rep["body"]?.DeepClone()));
    }
}
