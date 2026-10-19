using System.Text.Json.Nodes;
using Ombra.ClientCore;
using Ombra.ClientCore.Teams;
using Ombra.Core.Interop;
using Ombra.Server.Contracts;
using Ombra.Teams.Tests.Infra;

namespace Ombra.Teams.Tests;

/// <summary>
/// Slice S3 (SERVER_DESIGN §9 matrix): team genesis, invites and join with admin approval, SD-01 (tampered JoinRequest,
/// multi-use invite, secret never on the server), R2-06 (no <c>#k=</c> in logs), SD-03 (foreign team), R2-01 (stale
/// prefix), SD-05 (admin cannot seize ownership; two offers → equivocation), ownership transfer, equivocation
/// resolution with the undone-changes list, heartbeat confirmation, removal with RemovalNotice and local wipe (D-5).
/// </summary>
public sealed class TeamLifecycleTests(ServerStack pg)
{
    private static async Task<(Client Owner, string TeamId)> OwnerWithTeamAsync(TeamServerHost host)
    {
        var owner = new Client(host, "owner");
        owner.InitFresh();
        await owner.Server.BootstrapAsync(await host.BootstrapCodeAsync());
        var team = await owner.Teams.CreateTeamAsync();
        Assert.Equal(52 + 12, team.RecoveryKeyText.Length); // 32 bytes → 52 base32 chars in groups of 4
        return (owner, team.TeamId);
    }

    /// <summary>A fresh device joins through <paramref name="by"/>'s personal invite and is approved; it then has the Welcome.</summary>
    private static async Task<Client> JoinAsync(TeamServerHost host, Client by, string teamId, string name, string role = "member")
    {
        var link = await by.Teams.CreateInviteAsync(teamId, role);
        var c = new Client(host, name);
        c.InitFresh();
        await c.Teams.JoinAsync(InviteLink.Parse(link.ToShareableString()));
        var review = (await by.Teams.PendingJoinsAsync()).Single(j => j.MemberId == c.Id.MemberId);
        Assert.True(review.Valid, review.Problem);
        await by.Teams.ApproveJoinAsync(review);
        await c.Server.HelloAsync();
        var sync = await c.Teams.SyncAsync();
        Assert.Equal(1, sync.Welcomes);
        return c;
    }

    [Fact]
    public async Task Genesis_invite_join_and_approval_and_the_secret_never_reaches_server_or_logs()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;

        var link = await owner.Teams.CreateInviteAsync(teamId);
        var shared = link.ToShareableString();
        Assert.Contains("#k=", shared);
        Assert.DoesNotContain("#k=", link.ToString()); // R2-06: what goes to logs/UI lists
        var logLine = new SecretRedactor().Redact("opening " + shared);
        Assert.DoesNotContain(InviteLinkSecret(shared), logLine);

        using var ivy = new Client(host, "ivy");
        ivy.InitFresh();
        var pending = await ivy.Teams.JoinAsync(InviteLink.Parse(shared));
        Assert.Equal(teamId, pending.TeamId);
        Assert.False(ivy.Teams.Status(teamId)["joined"]!.GetValue<bool>());

        var review = (await owner.Teams.PendingJoinsAsync()).Single();
        Assert.True(review.Valid, review.Problem);
        Assert.True(review.AutoAccept); // personal invite (maxUses = 1) with a valid bind
        await owner.Teams.ApproveJoinAsync(review);
        await ivy.Server.HelloAsync();
        var sync = await ivy.Teams.SyncAsync();
        Assert.Equal(1, sync.Welcomes);
        Assert.Empty(sync.Rejected);
        Assert.True(ivy.Teams.Status(teamId)["joined"]!.GetValue<bool>());
        Assert.Equal(owner.Teams.SafetyNumber(teamId, ivy.Id.MemberId), ivy.Teams.SafetyNumber(teamId, ivy.Id.MemberId));
        Assert.Equal(owner.Teams.SafetyNumber(teamId, owner.Id.MemberId), ivy.Teams.SafetyNumber(teamId, owner.Id.MemberId));
        Assert.Equal(60, owner.Teams.SafetyNumber(teamId, ivy.Id.MemberId).Count(char.IsDigit));

        // SD-01: neither k, k_srv nor k_adm is anywhere in the server's database.
        var keys = OmbraPublic.Call("invite.keys", new() { ["k"] = TeamsHex.Of(link.Secret), ["inviteId"] = TeamsHex.Of(link.InviteId) });
        var dump = await host.DumpAsync();
        Assert.Contains(keys["verifier"]!.GetValue<string>(), dump); // sanity: the dump does show bytea as hex
        Assert.DoesNotContain(TeamsHex.Of(link.Secret), dump);
        Assert.DoesNotContain(keys["kAdm"]!.GetValue<string>(), dump); // k_srv itself is the join proof the server checks; k and k_adm never travel
    }

    private static string InviteLinkSecret(string link) => link[(link.IndexOf("#k=", StringComparison.Ordinal) + 3)..];

    [Fact]
    public async Task Tampered_join_request_is_not_added_and_a_multi_use_invite_needs_manual_confirmation()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;

        var link = await owner.Teams.CreateInviteAsync(teamId);
        using var ivy = new Client(host, "ivy");
        ivy.InitFresh();
        var pending = await ivy.Teams.JoinAsync(InviteLink.Parse(link.ToShareableString()));

        // A malicious server swaps the device keys and KeyPackage for its own (re-signing the request with that key).
        using var mallory = new Client(host, "mallory");
        mallory.Device.Initialize(Guid.CreateVersion7(), ivy.Id.MemberId);
        var original = await host.ScalarAsync<byte[]>("SELECT join_envelope FROM join_requests WHERE request_id = @r", ("r", pending.RequestId));
        var body = JoinRequestBody.Parse(Envelope.Decode(original).BodyMap());
        var sid = TeamsHex.Of(await mallory.Server.ServerIdAsync());
        var kp = TeamsHex.Bytes(mallory.Device.Call("key_package")["keyPackage"]!);
        var cert = TeamsHex.Bytes(mallory.Device.Call("device.certificate", new() { ["self"] = true, ["serverId"] = sid, ["issuedAt"] = mallory.Server.Now })["cert"]!);
        var forged = body with { Devices = [new JoinDevice(mallory.Id.DeviceId, "gui", mallory.Id.IdentityKey, mallory.Id.MlsSignatureKey, kp, cert)] };
        var (env, sig) = mallory.Server.SignEnvelope(EnvelopeKinds.JoinRequest, Convert.FromHexString(teamId), forged.ToCbor());
        await host.ExecAsync("UPDATE join_requests SET join_envelope = @e, join_signature = @s WHERE request_id = @r", ("e", env), ("s", sig), ("r", pending.RequestId));

        var review = (await owner.Teams.PendingJoinsAsync()).Single();
        Assert.False(review.Valid);
        Assert.Contains("changed on the way", review.Problem);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.Teams.ApproveJoinAsync(review, safetyNumberConfirmed: true));
        Assert.Equal(1UL, owner.Teams.Head(teamId)["version"]!.GetValue<ulong>()); // no Add

        // Multi-use link: a valid bind is still not enough without the safety-number check.
        var multi = await owner.Teams.CreateInviteAsync(teamId, maxUses: 5);
        using var bob = new Client(host, "bob");
        bob.InitFresh();
        await bob.Teams.JoinAsync(InviteLink.Parse(multi.ToShareableString()));
        var bobReview = (await owner.Teams.PendingJoinsAsync()).Single(j => j.MemberId == bob.Id.MemberId);
        Assert.True(bobReview.Valid, bobReview.Problem);
        Assert.False(bobReview.AutoAccept);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.Teams.ApproveJoinAsync(bobReview));
        await owner.Teams.ApproveJoinAsync(bobReview, safetyNumberConfirmed: true);
        await bob.Server.HelloAsync();
        Assert.Equal(1, (await bob.Teams.SyncAsync()).Welcomes);
    }

    [Fact]
    public async Task Invitee_refuses_a_foreign_team_a_forged_inviter_and_a_stale_roster_prefix()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;
        var otherTeam = (await owner.Teams.CreateTeamAsync()).TeamId;
        var link = await owner.Teams.CreateInviteAsync(teamId);

        // SD-03: the link names team t, the server answers with another team.
        using var a = new Client(host, "a");
        a.InitFresh();
        var foreign = new InviteLink(link.Server, link.Pin, otherTeam, link.HeadVersion, link.HeadHash, link.InviterFingerprint, link.InviteId, link.Secret);
        var e1 = await Assert.ThrowsAsync<TeamsServerException>(() => a.Teams.JoinAsync(foreign));
        Assert.Equal(Reasons.RosterConflict, e1.Reason);

        // The Invite is not signed by the key of fingerprint `a`.
        var forgedA = new InviteLink(link.Server, link.Pin, teamId, link.HeadVersion, link.HeadHash, TeamsHex.Of(new byte[32]), link.InviteId, link.Secret);
        var e2 = await Assert.ThrowsAsync<OmbraException>(() => a.Teams.JoinAsync(forgedA));
        Assert.Equal("roster-rejected", e2.Code);

        // R2-01: the link pins rosterHead v2, the server serves only the prefix up to v1.
        var v2 = owner.Device.Call("roster.update", new() { ["teamId"] = teamId, ["now"] = owner.Server.Now, ["apply"] = true, ["privateCommit"] = TeamsHex.Of(new byte[32].Select((_, i) => (byte)i).ToArray()) });
        await owner.Server.PostBytesAsync<RosterAppend>($"v1/teams/{teamId}/roster", TeamsHex.Bytes(v2["update"]!), TeamClient.RosterContentType);
        var link2 = await owner.Teams.CreateInviteAsync(teamId);
        Assert.Equal(2UL, link2.HeadVersion);
        await host.ExecAsync("DELETE FROM roster_versions WHERE team_id = @t AND version = 2", ("t", Convert.FromHexString(teamId)));
        using var b = new Client(host, "b");
        b.InitFresh();
        var e3 = await Assert.ThrowsAsync<OmbraException>(() => b.Teams.JoinAsync(InviteLink.Parse(link2.ToShareableString())));
        Assert.Contains("rosterHead", e3.Detail);
    }

    [Fact]
    public async Task Admin_cannot_seize_ownership_two_offers_freeze_the_team_and_an_accepted_offer_transfers_it()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;
        using var alice = await JoinAsync(host, owner, teamId, "alice", "admin");
        using var bob = await JoinAsync(host, owner, teamId, "bob", "admin");
        using var carol = await JoinAsync(host, alice, teamId, "carol");
        foreach (var c in new[] { alice, bob, carol }) await c.Teams.SyncRosterAsync(teamId);

        // SD-05: an admin signs owner = self / removes the owner / demotes another admin → refused by the server.
        async Task RefusedAsync(JsonObject change)
        {
            change["teamId"] = teamId;
            change["now"] = alice.Server.Now;
            change["apply"] = false;
            var u = TeamsHex.Bytes(alice.Device.Call("roster.update", change)["update"]!);
            var e = await Assert.ThrowsAsync<TeamsServerException>(() => alice.Server.PostBytesAsync<RosterAppend>($"v1/teams/{teamId}/roster", u, TeamClient.RosterContentType));
            Assert.Equal(Reasons.RosterRejected, e.Reason);
        }
        await RefusedAsync(new JsonObject { ["setOwner"] = TeamsHex.Of(alice.Id.MemberId) });
        await RefusedAsync(new JsonObject { ["removeMembers"] = new JsonArray(TeamsHex.Of(owner.Id.MemberId)) });
        await RefusedAsync(new JsonObject { ["setRoles"] = new JsonArray(new JsonObject { ["memberId"] = TeamsHex.Of(bob.Id.MemberId), ["role"] = "member" }) });

        // §4.3: the owner offers ownership to Alice; only Alice accepts it, with both signatures.
        await owner.Teams.OfferOwnershipAsync(teamId, alice.Id.MemberId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.Teams.AcceptOwnershipAsync(teamId));
        await alice.Teams.AcceptOwnershipAsync(teamId);
        await owner.Teams.SyncRosterAsync(teamId);
        var head = owner.Teams.Head(teamId)["projection"]!;
        Assert.Equal(TeamsHex.Of(alice.Id.MemberId), head["owner"]!.GetValue<string>());
        Assert.True(owner.Teams.IsAdmin(teamId)); // the former owner became admin

        // SD-05: Alice (now owner) signs two offers for the same version; Bob and the former owner each accept theirs → equivocation.
        await bob.Teams.SyncRosterAsync(teamId);
        await owner.Teams.SyncRosterAsync(teamId);
        var exp = alice.Server.Now + 3600;
        var toBob = alice.Device.Call("ownership.offer", new() { ["teamId"] = teamId, ["to"] = TeamsHex.Of(bob.Id.MemberId), ["expiresAt"] = exp });
        var toOwner = alice.Device.Call("ownership.offer", new() { ["teamId"] = teamId, ["to"] = TeamsHex.Of(owner.Id.MemberId), ["expiresAt"] = exp });
        byte[] Accept(Client who, JsonNode offer)
        {
            var acceptSig = who.Device.Call("ownership.accept", new() { ["offer"] = offer["offer"]!.DeepClone() })["acceptSig"]!;
            var ownership = new JsonObject { ["offer"] = offer["offer"]!.DeepClone(), ["offerSig"] = offer["offerSig"]!.DeepClone(), ["acceptSig"] = acceptSig.DeepClone() };
            return TeamsHex.Bytes(who.Device.Call("roster.update", new() { ["teamId"] = teamId, ["now"] = who.Server.Now, ["ownership"] = ownership })["update"]!);
        }
        await bob.Server.PostBytesAsync<RosterAppend>($"v1/teams/{teamId}/roster", Accept(bob, toBob), TeamClient.RosterContentType);
        var eq = await Assert.ThrowsAsync<TeamsServerException>(() =>
            owner.Server.PostBytesAsync<RosterAppend>($"v1/teams/{teamId}/roster", Accept(owner, toOwner), TeamClient.RosterContentType));
        Assert.Equal(Reasons.Equivocation, eq.Reason);
        var review = await owner.Teams.EquivocationAsync(teamId);
        Assert.NotNull(review);
        Assert.NotEqual(review!.Known, review.Conflicting);
        Assert.Equal("equivocation", await host.ScalarAsync<string>("SELECT frozen_reason FROM teams WHERE team_id = @t", ("t", Convert.FromHexString(teamId))));
    }

    [Fact]
    public async Task Owner_resolves_an_equivocation_only_after_confirming_what_the_choice_undoes()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;
        using var admin = await JoinAsync(host, owner, teamId, "admin", "admin");
        using var member = await JoinAsync(host, owner, teamId, "member");
        foreach (var c in new[] { admin, member }) await c.Teams.SyncRosterAsync(teamId);

        // Known v(n+1): the admin removes the member. Conflicting v(n+1): the owner changes pins.
        var removal = TeamsHex.Bytes(admin.Device.Call("roster.update", new()
        {
            ["teamId"] = teamId, ["now"] = admin.Server.Now, ["apply"] = true, ["removeMembers"] = new JsonArray(TeamsHex.Of(member.Id.MemberId)),
        })["update"]!);
        var pins = TeamsHex.Bytes(owner.Device.Call("roster.update", new()
        {
            ["teamId"] = teamId, ["now"] = owner.Server.Now, ["pins"] = new JsonArray(host.Pin, TeamsHex.Of(new byte[32])),
        })["update"]!);
        await admin.Server.PostBytesAsync<RosterAppend>($"v1/teams/{teamId}/roster", removal, TeamClient.RosterContentType);
        var eq = await Assert.ThrowsAsync<TeamsServerException>(() => owner.Server.PostBytesAsync<RosterAppend>($"v1/teams/{teamId}/roster", pins, TeamClient.RosterContentType));
        Assert.Equal(Reasons.Equivocation, eq.Reason);

        var review = (await owner.Teams.EquivocationAsync(teamId))!;
        Assert.Contains(TeamsHex.Of(admin.Id.DeviceId), review.KnownSigners);
        Assert.Contains(TeamsHex.Of(owner.Id.DeviceId), review.ConflictingSigners);
        var undone = review.UndoneIfSwitch.Single();
        Assert.Equal("removeMember", undone["kind"]!.GetValue<string>());
        Assert.Equal(TeamsHex.Of(member.Id.MemberId), undone["memberId"]!.GetValue<string>());
        Assert.Contains(review.UndoneIfKeep, x => x["kind"]!.GetValue<string>() == "pins");

        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.Teams.ResolveEquivocationAsync(review, keepKnown: false, undoneConfirmed: false));
        await owner.Teams.ResolveEquivocationAsync(review, keepKnown: true, undoneConfirmed: true);
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM teams WHERE frozen_reason IS NOT NULL"));
        await admin.Teams.ApplyResolutionsAsync(teamId);
        Assert.False(admin.Teams.Status(teamId)["frozen"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Heartbeat_monitor_confirms_the_team_only_with_a_verifiable_owner_heartbeat()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;
        using var ivy = await JoinAsync(host, owner, teamId, "ivy");

        var monitor = new HeartbeatMonitor(ivy.Teams, teamId);
        var before = await monitor.TickAsync();
        Assert.False(before.Confirmed);
        Assert.False(monitor.MaySend);

        var ownerMonitor = new HeartbeatMonitor(owner.Teams, teamId);
        await ownerMonitor.TickAsync(); // the owner device publishes (due) and counts its own
        var after = await monitor.TickAsync();
        Assert.True(after.Confirmed);
        Assert.True(after.ByOwner);
        Assert.True(monitor.MaySend);
    }

    [Fact]
    public async Task Removed_member_wipes_the_team_on_403_with_notice_and_ignores_a_forged_notice()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;
        using var ivy = await JoinAsync(host, owner, teamId, "ivy");
        using var mike = await JoinAsync(host, owner, teamId, "mike");
        await ivy.Teams.SyncRosterAsync(teamId);
        await mike.Teams.SyncRosterAsync(teamId);

        // A RemovalNotice about Ivy signed by a plain member: no wipe.
        var forged = TeamsHex.Bytes(mike.Device.Call("removal.create", new() { ["teamId"] = teamId, ["memberId"] = TeamsHex.Of(ivy.Id.MemberId), ["time"] = mike.Server.Now })["notice"]!);
        Assert.False(ivy.Teams.ProcessRemovalNotice(forged));
        Assert.True(ivy.Teams.Status(teamId)["joined"]!.GetValue<bool>());

        var wiped = new List<string>();
        ivy.Teams.TeamWiped += wiped.Add;
        await owner.Teams.RemoveMemberAsync(teamId, ivy.Id.MemberId);
        var e = await Assert.ThrowsAsync<TeamsServerException>(() => ivy.Server.HelloAsync());
        Assert.Equal(Reasons.Removed, e.Reason);
        Assert.Single(e.RemovalNotices);
        // The client verifies the notice against its chain once it has the version it references; it is about itself.
        Assert.Equal([teamId], ivy.Teams.HandleRemoved(e, [teamId]));
        Assert.Equal([teamId], wiped);
        var gone = ivy.Device.RequireState().CallRaw("team.status", new() { ["teamId"] = teamId, ["now"] = ivy.Server.Now });
        Assert.Equal("not-found", gone["code"]!.GetValue<string>());
        Assert.Null(ivy.Teams.TeamGroupId(teamId));

        // Mike stays: commit applied, still in the team.
        await mike.Teams.SyncAsync();
        Assert.True(mike.Teams.Status(teamId)["joined"]!.GetValue<bool>());
    }
}
