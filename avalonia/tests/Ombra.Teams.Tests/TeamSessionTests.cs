using Ombra.ClientCore.Teams;
using Ombra.Teams.Tests.Infra;

namespace Ombra.Teams.Tests;

/// <summary>
/// The Team page's account (TeamSession) against the real server: sign in by address + pin, create a team (recovery key
/// once), names, invite → join → approval, members with names and roles, ownership transfer, removal, pairing a second
/// device of the same member, revoking it, and a changed member key freezing the pair until confirmed.
/// </summary>
public sealed class TeamSessionTests(ServerStack pg)
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    private static string Root(TeamServerHost host, string name) => Path.Combine(host.Root, "accounts", name);

    [Fact]
    public async Task Create_invite_join_names_ownership_remove_pair_and_revoke()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var ownerKeys = new MemoryKeyStore();
        var ownerMachine = new FixedMachine("owner");
        using var owner = await TeamSession.ConnectAsync(Root(host, "owner"), host.Address, host.Pin, ownerKeys, ownerMachine);
        Assert.Empty((await owner.RefreshAsync()).Teams); // nothing on the server yet: no hello, no error

        var created = await owner.CreateTeamAsync(await host.BootstrapCodeAsync());
        Assert.Equal(64, created.RecoveryKeyText.Length);
        await owner.SetNameAsync("Olga");
        var snap = await owner.RefreshAsync();
        var team = Assert.Single(snap.Teams);
        Assert.True(team.IsOwner);
        Assert.Equal("Olga", Assert.Single(team.Roster.Members).Name);

        // The account survives a restart of the app.
        owner.Dispose();
        using var reopened = TeamSession.Load(Root(host, "owner"), ownerKeys, ownerMachine)!;
        Assert.Equal([created.TeamId], reopened.Account.Teams);

        // Invite → join → admin approval → member listed with its name and role.
        var link = await reopened.InviteAsync(created.TeamId, "member");
        using var ivy = await TeamSession.JoinAsync(Root(host, "ivy"), null, link, new MemoryKeyStore(), new FixedMachine("ivy"));
        await ivy.SetNameAsync("Ivy"); // kept until the team admits her
        Assert.Equal([created.TeamId], (await ivy.RefreshAsync()).WaitingForApproval);
        var join = Assert.Single((await reopened.RefreshAsync()).Teams[0].PendingJoins);
        await reopened.ApproveAsync(join);
        var ivySnap = await ivy.RefreshAsync();
        Assert.Empty(ivySnap.WaitingForApproval);
        Assert.Single(ivySnap.Teams);
        var view = (await reopened.RefreshAsync()).Teams[0];
        var ivyMember = view.Roster.Members.Single(m => !m.IsMe);
        Assert.Equal("member", ivyMember.Role);
        Assert.Equal("Ivy", ivyMember.Name);
        Assert.Equal(reopened.SafetyNumber(created.TeamId, ivyMember.MemberId), ivy.SafetyNumber(created.TeamId, ivyMember.MemberId));
        Assert.Empty(view.KeyChanged);

        // Ownership: offered by the owner, accepted by Ivy.
        await reopened.OfferOwnershipAsync(created.TeamId, ivyMember.MemberId);
        var offered = (await ivy.RefreshAsync()).Teams[0];
        Assert.NotNull(offered.OfferToMe);
        await ivy.AcceptOwnershipAsync(created.TeamId);
        Assert.True((await ivy.RefreshAsync()).Teams[0].IsOwner);
        var after = (await reopened.RefreshAsync()).Teams[0];
        Assert.False(after.IsOwner);
        Assert.True(after.IsAdmin);

        // A second device of Ivy: SAS pairing, then the team admits it; Ivy revokes it again.
        var (pairing, pairLink) = await ivy.StartPairingAsync();
        var (laptop, newSide) = await TeamSession.PairNewDeviceAsync(Root(host, "ivy-laptop"), pairLink.ToString(), new MemoryKeyStore(), new FixedMachine("ivy-laptop"));
        using var _ = laptop;
        await pairing.WaitForNewDeviceAsync(Poll);
        var words = await pairing.RevealAsync();
        Assert.Equal(words, await newSide.WaitForWordsAsync(Poll));
        await ivy.CertifyAsync(pairing);
        await laptop.FinishPairingAsync(newSide);
        var devices = (await ivy.RefreshAsync()).Teams[0].Roster.Members.Single(m => m.IsMe).Devices;
        Assert.Equal(2, devices.Count(d => !d.Revoked));
        var laptopDevice = devices.Single(d => !d.IsThisDevice);
        await ivy.RevokeDeviceAsync(laptopDevice.DeviceId);
        Assert.Contains((await ivy.RefreshAsync()).Teams[0].Roster.Members.Single(m => m.IsMe).Devices, d => d.DeviceId == laptopDevice.DeviceId && d.Revoked);

        // Removal (by the new owner): the old owner's data is wiped and the session reports it.
        await ivy.RemoveMemberAsync(created.TeamId, after.Roster.Members.Single(m => m.IsMe).MemberId);
        var removed = await reopened.RefreshAsync();
        Assert.Equal([created.TeamId], removed.Removed);
        Assert.Empty(removed.Teams);
        Assert.Empty(reopened.Account.Teams);
    }

    [Fact]
    public async Task A_changed_member_key_freezes_the_pair_until_confirmed()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        using var owner = await TeamSession.ConnectAsync(Root(host, "owner"), host.Address, host.Pin, new MemoryKeyStore(), new FixedMachine("owner"));
        var created = await owner.CreateTeamAsync(await host.BootstrapCodeAsync());
        using var ivy = await TeamSession.JoinAsync(Root(host, "ivy"), null, await owner.InviteAsync(created.TeamId, "member"), new MemoryKeyStore(), new FixedMachine("ivy"));
        await owner.ApproveAsync(Assert.Single((await owner.RefreshAsync()).Teams[0].PendingJoins));
        await ivy.RefreshAsync();
        var ivyId = ivy.Device.Identity!.MemberId;
        Assert.Empty((await owner.RefreshAsync()).Teams[0].KeyChanged);

        // What the owner accepted before differs from the roster now (as after a root key change).
        owner.Account.Fingerprints[created.TeamId][TeamsHex.Of(ivyId)] = "00";
        var changed = (await owner.RefreshAsync()).Teams[0];
        Assert.Equal([ivyId], changed.KeyChanged);
        Assert.Contains(TeamsHex.Of(ivyId), owner.Client.Status(created.TeamId)["frozenPeers"]!.AsArray().Select(x => x!.GetValue<string>()));

        owner.ConfirmKey(created.TeamId, changed.Roster.Members.Single(m => m.MemberId == ivyId));
        Assert.Empty((await owner.RefreshAsync()).Teams[0].KeyChanged);
        Assert.Empty(owner.Client.Status(created.TeamId)["frozenPeers"]!.AsArray());
    }
}
