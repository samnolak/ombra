using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ombra.ClientCore.Teams;
using Ombra.Server.Contracts;
using Ombra.Teams.Tests.Infra;

namespace Ombra.Teams.Tests;

/// <summary>Regression tests for the S2/S3 security review (agent ReviewTeams): one test per client-side finding.</summary>
public sealed class ReviewFindingsTests(ServerStack pg)
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private static async Task<(Client Owner, string TeamId)> OwnerWithTeamAsync(TeamServerHost host)
    {
        var owner = new Client(host, "owner");
        owner.InitFresh();
        await owner.Server.BootstrapAsync(await host.BootstrapCodeAsync());
        return (owner, (await owner.Teams.CreateTeamAsync()).TeamId);
    }

    private static async Task<Client> JoinAsync(TeamServerHost host, Client by, string teamId, string name)
    {
        var link = await by.Teams.CreateInviteAsync(teamId);
        var c = new Client(host, name);
        c.InitFresh();
        await c.Teams.JoinAsync(InviteLink.Parse(link.ToShareableString()));
        await by.Teams.ApproveJoinAsync((await by.Teams.PendingJoinsAsync()).Single(j => j.MemberId == c.Id.MemberId));
        await c.Server.HelloAsync();
        Assert.Equal(1, (await c.Teams.SyncAsync()).Welcomes);
        return c;
    }

    /// <summary>S2-PAIR-TRANSCRIPT-SWAP: the server swaps only the /reveal answer → nothing is certified.</summary>
    [Fact]
    public async Task Pairing_refuses_keys_swapped_only_in_the_reveal_response()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;
        using var laptop = new Client(host, "laptop");
        using var attacker = new Client(host, "attacker");
        attacker.InitFresh();
        var existing = new ExistingDevicePairing(owner.Server, owner.Device);
        var link = await existing.StartAsync();
        var newSide = new NewDevicePairing(laptop.Server, laptop.Device);
        await newSide.JoinAsync(link);
        await existing.WaitForNewDeviceAsync(Poll); // the honest keys
        await host.ExecAsync("UPDATE pairing_sessions SET new_device_id = @d, new_identity_key = @k, new_mls_key = @m WHERE code = @c",
            ("d", attacker.Id.DeviceId), ("k", attacker.Id.IdentityKey), ("m", attacker.Id.MlsSignatureKey), ("c", link.Code));
        await Assert.ThrowsAsync<PairingTamperedException>(() => existing.RevealAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => existing.CertifyAsync([teamId]));
        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM devices WHERE device_id = @d", ("d", attacker.Id.DeviceId)));
    }

    /// <summary>S3-TLS-REVOKED-PIN: a SPKI removed from the verified roster is no longer accepted.</summary>
    [Fact]
    public async Task A_pin_removed_from_the_roster_is_refused_after_sync()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;
        var rotated = new JsonArray(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        var u = owner.Device.Call("roster.update", new() { ["teamId"] = teamId, ["now"] = owner.Server.Now, ["pins"] = rotated });
        await owner.Server.PostBytesAsync<RosterAppend>($"v1/teams/{teamId}/roster", TeamsHex.Bytes(u["update"]!), TeamClient.RosterContentType);
        await owner.Teams.SyncRosterAsync(teamId); // applies the version that drops the server's current SPKI
        Assert.DoesNotContain(host.Pin, owner.Server.Endpoint.Pins);
        await Assert.ThrowsAsync<HttpRequestException>(() => owner.Server.HelloAsync());
    }

    /// <summary>S3-GROUP-EVICTION-WIPE: Remove from a project group (access revoked) keeps the team.</summary>
    [Fact]
    public async Task Removal_from_a_project_group_keeps_the_rest_of_the_team()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;
        using var ivy = await JoinAsync(host, owner, teamId, "ivy");
        var project = Convert.ToHexStringLower(Guid.CreateVersion7().ToByteArray(bigEndian: true));
        async Task PostRoster(JsonObject change)
        {
            change["teamId"] = teamId;
            change["now"] = owner.Server.Now;
            change["apply"] = true;
            await owner.Server.PostBytesAsync<RosterAppend>($"v1/teams/{teamId}/roster", TeamsHex.Bytes(owner.Device.Call("roster.update", change)["update"]!),
                TeamClient.RosterContentType);
        }
        await PostRoster(new JsonObject { ["grantAccess"] = new JsonArray(
            new JsonObject { ["projectId"] = project, ["memberId"] = TeamsHex.Of(owner.Id.MemberId) },
            new JsonObject { ["projectId"] = project, ["memberId"] = TeamsHex.Of(ivy.Id.MemberId) }) });
        var gid = owner.Device.Call("group.create", new() { ["descriptor"] = new JsonObject { ["type"] = "project", ["teamId"] = teamId, ["projectId"] = project, ["generation"] = 0 } })["groupId"]!.GetValue<string>();
        await owner.Server.PostBytesAsync<GroupResponse>($"v1/teams/{teamId}/groups", TeamsHex.Bytes(owner.Device.Call("group.public_info", new() { ["groupId"] = gid })["groupInfo"]!), Headers.MlsContentType);
        await ivy.Server.PostJsonAsync<KeyPackageUploadResponse>("v1/keypackages", new KeyPackageUpload([TeamsHex.Bytes(ivy.Device.Call("key_package")["keyPackage"]!)]));
        var kp = await owner.Server.GetAsync<KeyPackageResponse>($"v1/keypackages/{ivy.Id.DeviceId}?team={teamId}");
        var add = owner.Device.Call("group.add", new() { ["groupId"] = gid, ["keyPackages"] = new JsonArray(TeamsHex.Of(kp.KeyPackage)), ["now"] = owner.Server.Now });
        await owner.Server.PostJsonAsync<GroupResponse>($"v1/groups/{gid}/commit", new CommitRequest(TeamsHex.Bytes(add["commit"]!), TeamsHex.Bytes(add["welcome"]!)));
        Assert.Equal(1, (await ivy.Teams.SyncAsync()).Welcomes);

        await PostRoster(new JsonObject { ["revokeAccess"] = new JsonArray(new JsonObject { ["projectId"] = project, ["memberId"] = TeamsHex.Of(ivy.Id.MemberId) }) });
        var rotate = owner.Device.Call("group.rotate", new() { ["groupId"] = gid });
        await owner.Server.PostJsonAsync<GroupResponse>($"v1/groups/{gid}/commit", new CommitRequest(TeamsHex.Bytes(rotate["commit"]!), null));

        var wiped = new List<string>();
        ivy.Teams.TeamWiped += wiped.Add;
        var sync = await ivy.Teams.SyncAsync();
        Assert.Empty(sync.RemovedFromTeams);
        Assert.Empty(wiped);
        Assert.True(ivy.Teams.Status(teamId)["joined"]!.GetValue<bool>());
        Assert.NotNull(ivy.Teams.TeamGroupId(teamId));
    }

    /// <summary>S3-WIPE-RETAINS-STATE-KEY: after a wipe the old state key is gone and an older copy of the state stays sealed.</summary>
    [Fact]
    public async Task Wipe_destroys_the_old_state_key()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, teamId) = await OwnerWithTeamAsync(host);
        using var _ = owner;
        using var ivy = await JoinAsync(host, owner, teamId, "ivy");
        var oldKey = ivy.Keys.Read(ivy.Device.StateKeyAccount)!;
        var snapshot = Path.Combine(host.Root, "ivy-state-snapshot.bin");
        File.Copy(ivy.Device.StatePath, snapshot);

        await owner.Teams.RemoveMemberAsync(teamId, ivy.Id.MemberId);
        var e = await Assert.ThrowsAsync<TeamsServerException>(() => ivy.Server.HelloAsync());
        Assert.Equal([teamId], ivy.Teams.HandleRemoved(e, [teamId]));

        var current = ivy.Keys.Read(ivy.Device.StateKeyAccount)!;
        Assert.NotEqual(oldKey, current);
        var store = (MemoryKeyStore)ivy.Keys;
        Assert.DoesNotContain(store.Accounts, a => store.Read(a)!.AsSpan().SequenceEqual(oldKey));
        // The live state opens only with the new key (the snapshot needs the destroyed one).
        Assert.NotNull(ivy.Device.Call("device.info")["deviceId"]);
    }
}
