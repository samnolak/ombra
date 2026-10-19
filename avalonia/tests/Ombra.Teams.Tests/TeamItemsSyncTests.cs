using System.Text.Json.Nodes;
using Ombra.ClientCore.Teams;
using Ombra.Server.Contracts;
using Ombra.Teams.Tests.Infra;

namespace Ombra.Teams.Tests;

/// <summary>
/// C0 against the real server (design-sync-decision §1–§6): addressed item over a freshly created pair group, apply on
/// enqueue (offline), outbox flush, sync loop with persistent cursor and ack-after-commit, statuses (resource: first
/// final wins), restart with per-team keys from the key store, 409 epoch-conflict reseal with the same opId/seq/rev.
/// </summary>
public sealed class TeamItemsSyncTests(ServerStack pg)
{
    private static async Task<(Client Owner, Client Ivy, string TeamId)> TeamOfTwoAsync(TeamServerHost host)
    {
        var owner = new Client(host, "owner");
        owner.InitFresh();
        await owner.Server.BootstrapAsync(await host.BootstrapCodeAsync());
        var teamId = (await owner.Teams.CreateTeamAsync()).TeamId;
        var link = await owner.Teams.CreateInviteAsync(teamId);
        var ivy = new Client(host, "ivy");
        ivy.InitFresh();
        await ivy.Teams.JoinAsync(InviteLink.Parse(link.ToShareableString()));
        await owner.Teams.ApproveJoinAsync((await owner.Teams.PendingJoinsAsync()).Single());
        await ivy.Server.HelloAsync();
        Assert.Equal(1, (await ivy.Teams.SyncAsync()).Welcomes);
        await ivy.Teams.UploadKeyPackageAsync();
        await owner.Teams.SyncAsync();
        return (owner, ivy, teamId);
    }

    private static string Hex(Guid g) => TeamsHex.Of(g);

    private static JsonObject Anchor(string q) => new()
    {
        ["web"] = new JsonObject { ["url"] = "https://example.org/doc", ["selector"] = "#intro", ["textQuote"] = q },
    };

    [Fact]
    public async Task Addressed_link_status_restart_and_redelivery()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, ivy0, teamId) = await TeamOfTwoAsync(host);
        using var _o = owner;
        var ivyId = Hex(ivy0.Id.MemberId);
        var ownerId = Hex(owner.Id.MemberId);
        var oi = new TeamItems(owner.Teams);
        var changed = 0;
        oi.ItemsChanged += _ => changed++;

        var copies = await oi.SendAsync(teamId, new TeamItemDraft(TeamItemKinds.Link, new JsonObject { ["url"] = "https://example.org", ["title"] = "Docs" }), [ivyId]);
        var copy = Assert.Single(copies);
        Assert.True(changed > 0);
        Assert.Equal(0, oi.PendingCount()); // flushed right away
        var mine = Assert.Single(oi.List(teamId));
        Assert.Equal(ivyId, mine.RecipientMemberId);

        var ii = new TeamItems(ivy0.Teams);
        var d = await ii.DeliverAsync();
        Assert.Empty(d.SecurityEvents);
        var got = Assert.Single(ii.List(teamId));
        Assert.Equal(ownerId, got.AuthorMemberId);
        Assert.Equal("https://example.org", got.Body!["url"]!.GetValue<string>());

        await ii.SetStatusAsync(teamId, ownerId, copy.ItemId, TeamItemStatuses.Accepted);
        // Resource: the first final status never changes — refused locally, nothing queued.
        await Assert.ThrowsAnyAsync<Exception>(() => ii.SetStatusAsync(teamId, ownerId, copy.ItemId, TeamItemStatuses.Declined));
        Assert.Equal(0, ii.PendingCount());
        await oi.DeliverAsync();
        Assert.Equal("accepted", Assert.Single(oi.Get(teamId, ownerId, copy.ItemId)!.Statuses).Status);

        // Restart ivy: the replica is in state.db, the team key comes back from the key store, the cursor persists.
        var root = ivy0.Root;
        var keys = ivy0.Keys;
        var machine = ivy0.Machine;
        ivy0.Dispose();
        using var ivy = new Client(host, "ivy", keys, machine, root);
        Assert.Empty(ivy.Device.LockedTeams);
        var ii2 = new TeamItems(ivy.Teams);
        Assert.Equal("accepted", Assert.Single(ii2.Get(teamId, ownerId, copy.ItemId)!.Statuses).Status);
        Assert.NotNull(ivy.Device.Call("sync.cursor")["cursor"]);
        var again = await ii2.DeliverAsync();
        Assert.Equal(0, again.Received);
        Assert.Single(ii2.List(teamId));

        // A message redelivered after its commit (lost ack) is acked as a duplicate without decrypting.
        var gid = copy.GroupId;
        await oi.SendAsync(teamId, new TeamItemDraft(TeamItemKinds.Link, new JsonObject { ["url"] = "https://example.org/2" }), [ivyId]);
        var sync = await ivy.Server.GetAsync<SyncResponse>("v1/sync");
        var op = sync.Ops.Single(o => o.GroupId == gid && o.Kind != "welcome");
        var first = ivy.Device.RequireState().CallRaw("group.process", new() { ["groupId"] = gid, ["message"] = TeamsHex.Of(op.Ciphertext), ["now"] = 1u });
        Assert.True(first["ack"]!.GetValue<bool>());
        var dup = ivy.Device.RequireState().CallRaw("group.process", new() { ["groupId"] = gid, ["message"] = TeamsHex.Of(op.Ciphertext), ["now"] = 1u });
        Assert.Equal("duplicate", dup["result"]!["kind"]!.GetValue<string>());
        await ii2.DeliverAsync();
        Assert.Equal(2, ii2.List(teamId).Count);
    }

    [Fact]
    public async Task Offline_enqueue_then_epoch_conflict_reseals_with_the_same_op()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, ivy, teamId) = await TeamOfTwoAsync(host);
        using var _o = owner;
        using var _i = ivy;
        var ownerId = Hex(owner.Id.MemberId);
        var ivyId = Hex(ivy.Id.MemberId);
        var oi = new TeamItems(owner.Teams);
        var ii = new TeamItems(ivy.Teams);
        var copy = (await oi.SendAsync(teamId, new TeamItemDraft(TeamItemKinds.Mark, Anchor: Anchor("v1"), Context: new JsonObject { ["text"] = "v1" }), [ivyId])).Single();
        await ii.DeliverAsync();

        // The pair moves to a new epoch (ivy commits) while the owner's edit is queued against the old one.
        var upd = ivy.Device.Call("group.update", new() { ["groupId"] = copy.GroupId });
        await ivy.Server.PostJsonAsync<GroupResponse>($"v1/groups/{copy.GroupId}/commit", new CommitRequest(TeamsHex.Bytes(upd["commit"]!), null));
        var enq = owner.Device.Call("outbox.enqueue", new()
        {
            ["groupId"] = copy.GroupId, ["opType"] = "item.edit", ["itemId"] = copy.ItemId, ["rev"] = 2, ["now"] = 1u,
            ["payload"] = new JsonObject { ["baseRev"] = 1, ["delta"] = new JsonObject { ["context"] = new JsonObject { ["text"] = "v2" } } },
        });
        Assert.Equal(2UL, oi.Get(teamId, ownerId, copy.ItemId)!.Rev); // applied before any network
        var flush = await owner.Teams.FlushOutboxAsync();
        Assert.Equal(1, flush.Sent);
        Assert.Equal(0, flush.Pending);

        await ii.DeliverAsync();
        var it = ii.Get(teamId, ownerId, copy.ItemId)!;
        Assert.Equal(2UL, it.Rev);
        Assert.Equal("v2", it.Context!["text"]!.GetValue<string>());

        // Marks: the last status wins.
        await ii.SetStatusAsync(teamId, ownerId, copy.ItemId, TeamItemStatuses.Done);
        await ii.SetStatusAsync(teamId, ownerId, copy.ItemId, TeamItemStatuses.Dismissed);
        await oi.DeliverAsync();
        Assert.Equal("dismissed", Assert.Single(oi.Get(teamId, ownerId, copy.ItemId)!.Statuses).Status);
    }
}
