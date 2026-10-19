using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ombra.ClientCore.Teams;
using Ombra.Server.Contracts;
using Ombra.Teams.Tests.Infra;

namespace Ombra.Teams.Tests;

/// <summary>
/// R2-05 (k_adm travels only in the admin group; a demoted admin's invites stop working) and §2.3 п.4 (a paired
/// device follows its member into teams created after pairing, and into their groups).
/// </summary>
public sealed class AdminKeysAndOwnDevicesTests(ServerStack pg)
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private static async Task<(Client Owner, string TeamId)> OwnerWithTeamAsync(TeamServerHost host)
    {
        var owner = new Client(host, "owner");
        owner.InitFresh();
        await owner.Server.BootstrapAsync(await host.BootstrapCodeAsync());
        return (owner, (await owner.Teams.CreateTeamAsync()).TeamId);
    }

    private static async Task<Client> JoinAsync(TeamServerHost host, Client by, string teamId, string name, string role = "member")
    {
        var link = await by.Teams.CreateInviteAsync(teamId, role);
        var c = new Client(host, name);
        c.InitFresh();
        await c.Teams.JoinAsync(InviteLink.Parse(link.ToShareableString()));
        await by.Teams.ApproveJoinAsync((await by.Teams.PendingJoinsAsync()).Single(j => j.MemberId == c.Id.MemberId));
        await c.Server.HelloAsync();
        Assert.Equal(1, (await c.Teams.SyncAsync()).Welcomes);
        return c;
    }

    [Fact]
    public async Task Invite_secret_reaches_other_admins_only_through_the_admin_group_and_a_demoted_admins_invites_die()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;
        using var alice = await JoinAsync(host, owner, teamId, "alice", "admin");
        using var bob = await JoinAsync(host, owner, teamId, "bob");

        // The owner's admin group gets Alice's device (not Bob's); the owner's invite secrets follow.
        await alice.Teams.UploadKeyPackageAsync();
        await bob.Teams.UploadKeyPackageAsync();
        Assert.Equal(1, await owner.Teams.SyncAdminGroupAsync(teamId));
        var link = await owner.Teams.CreateInviteAsync(teamId);
        await owner.Teams.ShareInviteKeysAsync(teamId);
        var aliceSync = await alice.Teams.SyncAsync();
        Assert.Equal(1, aliceSync.Welcomes);
        Assert.NotNull(alice.Teams.AdminGroupId(teamId));
        await bob.Teams.SyncAsync();
        Assert.Null(bob.Teams.AdminGroupId(teamId));

        // Carol joins with the owner's link; Alice (not the inviter) can verify bind with the k_adm she received.
        using var carol = new Client(host, "carol");
        carol.InitFresh();
        await carol.Teams.JoinAsync(InviteLink.Parse(link.ToShareableString()));
        var seenByAlice = (await alice.Teams.PendingJoinsAsync()).Single(j => j.MemberId == carol.Id.MemberId);
        Assert.True(seenByAlice.Valid, seenByAlice.Problem);
        var e = await Assert.ThrowsAsync<TeamsServerException>(() => bob.Teams.PendingJoinsAsync());
        Assert.Equal(Reasons.NotAdmin, e.Reason);

        // Alice issues her own link, then the owner demotes her: her link dies, she leaves the admin group.
        var aliceLink = await alice.Teams.CreateInviteAsync(teamId);
        await owner.Teams.SyncRosterAsync(teamId);
        var demote = owner.Device.Call("roster.update", new()
        {
            ["teamId"] = teamId, ["now"] = owner.Server.Now, ["apply"] = true,
            ["setRoles"] = new JsonArray(new JsonObject { ["memberId"] = TeamsHex.Of(alice.Id.MemberId), ["role"] = "member" }),
        });
        await owner.Server.PostBytesAsync<RosterAppend>($"v1/teams/{teamId}/roster", TeamsHex.Bytes(demote["update"]!), TeamClient.RosterContentType);
        await owner.Teams.SyncAdminGroupAsync(teamId);
        var gone = await Assert.ThrowsAsync<TeamsServerException>(() => owner.Server.GetAnonymousAsync<InviteResponse>($"v1/invites/{aliceLink.InviteId:D}"));
        Assert.Equal(404, gone.Status);
        await alice.Teams.SyncAsync();
        Assert.Null(alice.Teams.AdminGroupId(teamId)); // removed from the admin group (evicted)
        Assert.True(alice.Teams.Status(teamId)["joined"]!.GetValue<bool>()); // still a member of the team
    }

    [Fact]
    public async Task A_paired_device_joins_a_team_created_after_pairing_and_its_group()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, first) = await OwnerWithTeamAsync(host);
        using var _ = owner;
        using var laptop = new Client(host, "laptop");
        var side = new NewDevicePairing(laptop.Server, laptop.Device);
        var cert = await ExistingDevicePairing.CertifyFromQrAsync(owner.Server, owner.Device, side.QrPayload(), [first]);
        await owner.Teams.AddOwnDeviceAsync(first, cert);
        Assert.Equal([first], await side.CompleteAsync(Poll));

        // A team created after pairing: the member's device lists the laptop and refreshes its grant.
        var second = (await owner.Teams.CreateTeamAsync()).TeamId;
        Assert.Equal([laptop.Id.DeviceId], await owner.Teams.SyncOwnDevicesAsync(second));
        var granted = await laptop.Teams.JoinGrantedTeamsAsync();
        Assert.Equal(new[] { first, second }.Order(), granted.Select(g => g.TeamId).Order());

        // ...and is added to both team groups by the admin (the owner), the Welcomes are accepted.
        Assert.Equal(1, await owner.Teams.AddMissingDevicesAsync(first));
        Assert.Equal(1, await owner.Teams.AddMissingDevicesAsync(second));
        var sync = await laptop.Teams.SyncAsync();
        Assert.Equal(2, sync.Welcomes);
        Assert.Empty(sync.Rejected);
        Assert.NotNull(laptop.Teams.TeamGroupId(second));
        Assert.True(laptop.Teams.Status(second)["joined"]!.GetValue<bool>());

        // A grant signed by a device of another member is refused.
        using var stranger = new Client(host, "stranger");
        stranger.InitFresh();
        await stranger.Server.BootstrapAsync(await host.BootstrapCodeAsync());
        var (env, sig) = stranger.Server.SignEnvelope(EnvelopeKinds.DeviceGrant, laptop.Id.DeviceId.ToByteArray(bigEndian: true),
            new DeviceGrantBody([Convert.FromHexString(second)]).ToCbor());
        var refused = await Assert.ThrowsAsync<TeamsServerException>(() =>
            stranger.Server.PostJsonAsync<NoContent>($"v1/devices/{laptop.Id.DeviceId:D}/grant", new DeviceGrantRequest(env, sig)));
        Assert.Equal(404, refused.Status);
    }
}
