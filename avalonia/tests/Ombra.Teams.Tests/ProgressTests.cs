using Ombra.ClientCore.Teams;
using Ombra.Server.Contracts;
using Ombra.Teams.Tests.Infra;

namespace Ombra.Teams.Tests;

/// <summary>P3 project progress (decision §8) end to end on the real server: exporter key + device signature, the ephemeral channel, memory only.</summary>
public sealed class ProgressTests(ServerStack pg)
{
    private static async Task<(Client Owner, Client Ivy, string TeamId, TeamProject Project)> ProjectAsync(TeamServerHost host)
    {
        var owner = new Client(host, "owner");
        owner.InitFresh();
        await owner.Server.BootstrapAsync(await host.BootstrapCodeAsync());
        var teamId = (await owner.Teams.CreateTeamAsync()).TeamId;
        var link = await owner.Teams.CreateInviteAsync(teamId);
        var ivy = new Client(host, "ivy");
        ivy.InitFresh();
        await ivy.Teams.JoinAsync(InviteLink.Parse(link.ToShareableString()));
        await owner.Teams.ApproveJoinAsync((await owner.Teams.PendingJoinsAsync()).Single(j => j.MemberId == ivy.Id.MemberId));
        await ivy.Server.HelloAsync();
        Assert.Equal(1, (await ivy.Teams.SyncAsync()).Welcomes);
        await ivy.Teams.UploadKeyPackageAsync();
        var project = await owner.Teams.CreateProjectAsync(teamId);
        Assert.Equal(1, (await ivy.Teams.SyncAsync()).Welcomes);
        Assert.Equal([project], ivy.Teams.ProjectGroups(teamId));
        return (owner, ivy, teamId, project);
    }

    private static async Task<ProgressEntry> UntilAsync(TeamProgress p, Func<ProgressEntry, bool> match)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (p.Entries.FirstOrDefault(e => !e.IsMine && match(e)) is { } e) return e;
            await Task.Delay(50);
        }
        throw new TimeoutException("progress entry not received");
    }

    private static async Task LiveAsync(TeamProgress p)
    {
        p.Start();
        for (var i = 0; i < 300 && !p.IsLive; i++) await Task.Delay(50);
        Assert.True(p.IsLive);
    }

    [Fact]
    public async Task Progress_reaches_project_members_and_the_server_keeps_nothing()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, ivy, _, project) = await ProjectAsync(host);
        using var _o = owner;
        using var _i = ivy;
        await using var mine = new TeamProgress(owner.Teams);
        await using var theirs = new TeamProgress(ivy.Teams);
        await LiveAsync(mine);
        await LiveAsync(theirs);

        await mine.PublishAsync(project, "Ombra", ProgressStatus.Working, "Fix the login form");
        var e = await UntilAsync(theirs, x => x.Status == ProgressStatus.Working);
        Assert.Equal((project.ProjectId, "Ombra", owner.Id.MemberId, owner.Id.DeviceId, "Fix the login form"), (e.ProjectId, e.ProjectName, e.MemberId, e.DeviceId, e.Task));

        // Task title opt-out: only the status travels
        await mine.PublishAsync(project, "Ombra", ProgressStatus.Waiting, null);
        e = await UntilAsync(theirs, x => x.Status == ProgressStatus.Waiting);
        Assert.Null(e.Task);
        Assert.Single(theirs.Entries);

        Assert.Equal(0L, await host.ScalarAsync<long>("SELECT count(*) FROM delivery"));
    }

    [Fact]
    public async Task Progress_is_sent_again_when_a_member_comes_online()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, ivy, _, project) = await ProjectAsync(host);
        using var _o = owner;
        using var _i = ivy;
        await using var mine = new TeamProgress(owner.Teams);
        await LiveAsync(mine);
        await mine.PublishAsync(project, "Ombra", ProgressStatus.Working, "Step 2"); // ivy offline: relayed to nobody

        await using var theirs = new TeamProgress(ivy.Teams);
        await LiveAsync(theirs);
        var e = await UntilAsync(theirs, x => x.Task == "Step 2");
        Assert.Equal(ProgressStatus.Working, e.Status);

        // opting out sends a final idle; then nothing is re-sent
        await mine.StopAsync(project);
        await UntilAsync(theirs, x => x.Status == ProgressStatus.Idle);
        Assert.DoesNotContain(mine.Entries, x => x.IsMine);
    }

    [Fact]
    public async Task Forged_or_misattributed_progress_is_dropped()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, ivy, _, project) = await ProjectAsync(host);
        using var _o = owner;
        using var _i = ivy;
        await using var theirs = new TeamProgress(ivy.Teams);
        var sealedMsg = owner.Device.Call("progress.seal", new() { ["groupId"] = project.GroupId, ["status"] = "working", ["projectName"] = "Ombra", ["now"] = owner.Server.Now });
        var c = TeamsHex.Bytes(sealedMsg["ciphertext"]!);

        // relayed as if from another device (ivy's own): the signer must be the relaying device
        await theirs.HandleAsync(new StreamEvent(StreamEventTypes.Ephemeral, GroupId: project.GroupId, SenderDeviceId: Guid.CreateVersion7(), Ciphertext: c));
        // a flipped ciphertext byte
        var bad = (byte[])c.Clone();
        bad[^1] ^= 1;
        await theirs.HandleAsync(new StreamEvent(StreamEventTypes.Ephemeral, GroupId: project.GroupId, SenderDeviceId: owner.Id.DeviceId, Ciphertext: bad));
        Assert.Empty(theirs.Entries);

        await theirs.HandleAsync(new StreamEvent(StreamEventTypes.Ephemeral, GroupId: project.GroupId, SenderDeviceId: owner.Id.DeviceId, Ciphertext: c));
        Assert.Single(theirs.Entries);
        // the sender went offline: its progress is forgotten
        await theirs.HandleAsync(new StreamEvent(StreamEventTypes.Presence, DeviceId: owner.Id.DeviceId, State: "offline"));
        Assert.Empty(theirs.Entries);
    }

    [Fact]
    public async Task Replayed_progress_is_not_resurrected_after_offline_or_reconnect()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        var (owner, ivy, _, project) = await ProjectAsync(host);
        using var _o = owner;
        using var _i = ivy;
        await using var theirs = new TeamProgress(ivy.Teams);
        byte[] Seal(string status, ulong now) => TeamsHex.Bytes(owner.Device.Call("progress.seal",
            new() { ["groupId"] = project.GroupId, ["status"] = status, ["projectName"] = "Ombra", ["now"] = now })["ciphertext"]!);
        StreamEvent From(byte[] c) => new(StreamEventTypes.Ephemeral, GroupId: project.GroupId, SenderDeviceId: owner.Id.DeviceId, Ciphertext: c);
        var now = owner.Server.Now;
        var working = Seal("working", now - 2);
        var idle = Seal("idle", now - 1);

        await theirs.HandleAsync(From(working));
        await theirs.HandleAsync(From(idle));
        await theirs.HandleAsync(From(working)); // older than the last one seen
        Assert.Equal(ProgressStatus.Idle, Assert.Single(theirs.Entries).Status);

        // offline drops the entry but not the watermark: neither old message brings it back
        await theirs.HandleAsync(new StreamEvent(StreamEventTypes.Presence, DeviceId: owner.Id.DeviceId, State: "offline"));
        await theirs.HandleAsync(From(idle));
        await theirs.HandleAsync(From(working));
        Assert.Empty(theirs.Entries);

        // older than the freshness window: refused by the core even without a watermark (a fresh receiver)
        await using var fresh = new TeamProgress(ivy.Teams);
        await fresh.HandleAsync(From(Seal("working", now - 301)));
        Assert.Empty(fresh.Entries);

        // a newer publication is shown again
        await theirs.HandleAsync(From(Seal("waiting", now)));
        Assert.Equal(ProgressStatus.Waiting, Assert.Single(theirs.Entries).Status);
    }
}
