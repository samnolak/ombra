using System.Text;
using Ombra.ClientCore.Teams;
using Ombra.Teams.Tests.Infra;

namespace Ombra.Teams.Tests;

/// <summary>
/// P5 resources over the real server and ombra-core (TeamItems): a file, a skill and a link the owner shares with Ivy
/// arrive end-to-end encrypted, wait in her Inbox untouched, land only after Accept (the declined one never does), and
/// the owner sees Ivy's status on each.
/// </summary>
public sealed class TeamShareServerTests(ServerStack pg)
{
    private static string Root(TeamServerHost host, string name) => Path.Combine(host.Root, "accounts", name);

    private static async Task<IReadOnlyList<TeamItem>> WaitFor(ITeamItems items, string team, Func<IReadOnlyList<TeamItem>, bool> done)
    {
        for (var i = 0; i < 50; i++)
        {
            await items.DeliverAsync();
            var list = items.List(team);
            if (done(list)) return list;
            await Task.Delay(200);
        }
        throw new TimeoutException("the items did not arrive");
    }

    [Fact]
    public async Task File_skill_and_link_arrive_wait_for_accept_and_the_sender_sees_each_status()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        using var owner = await TeamSession.ConnectAsync(Root(host, "owner"), host.Address, host.Pin, new MemoryKeyStore(), new FixedMachine("owner"));
        var created = await owner.CreateTeamAsync(await host.BootstrapCodeAsync());
        await owner.SetNameAsync("Olga");
        using var ivy = await TeamSession.JoinAsync(Root(host, "ivy"), null, await owner.InviteAsync(created.TeamId, "member"), new MemoryKeyStore(), new FixedMachine("ivy"));
        await ivy.SetNameAsync("Ivy");
        await owner.ApproveAsync(Assert.Single((await owner.RefreshAsync()).Teams[0].PendingJoins));
        var ivySnap = await ivy.RefreshAsync();
        Assert.Single(ivySnap.Teams);
        var team = created.TeamId;
        var o = TeamsHex.Of((await owner.RefreshAsync()).MemberId);
        var me = TeamsHex.Of(ivySnap.MemberId);

        var ownerItems = owner.Items;
        var ivyItems = ivy.Items;

        var skillDir = Path.Combine(host.Root, "owner-skills", "deploy-check");
        Directory.CreateDirectory(Path.Combine(skillDir, "scripts"));
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: deploy-check\ndescription: Checks a deploy\n---\nRun scripts/check.sh\n");
        File.WriteAllText(Path.Combine(skillDir, "scripts", "check.sh"), "#!/bin/sh\necho ok\n");
        var bytes = Encoding.UTF8.GetBytes(new string('x', 200_000) + "\nend\n"); // several AEAD chunks

        await TeamShare.File("notes.md", bytes).SendAsync(ownerItems, team, [me], null);
        await TeamShare.SkillFrom(skillDir).SendAsync(ownerItems, team, [me], "for deploys");
        await TeamShare.Link("https://example.com/spec", "Spec").SendAsync(ownerItems, team, [me], null);
        await TeamShare.Edit("Changes", "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-a\n+b\n",
                new RepoIdentity("00112233445566778899aabbccddeeff", "0123456789abcdef0123456789abcdef01234567"))
            .SendAsync(ownerItems, team, [me], null);
        await TeamShare.Collection("2 pages", [new SharedLink("https://a.example/", "A"), new SharedLink("https://b.example/", "B")]).SendAsync(ownerItems, team, [me], null);
        await ownerItems.DeliverAsync();
        Assert.Equal(5, TeamShare.Sent(ownerItems.List(team), o).Count);

        var all = await WaitFor(ivyItems, team, l => TeamShare.Incoming(l, me).Count == 5 && l.Count(i => i.Kind == TeamItemKinds.Link) == 3);
        var inbox = TeamShare.Incoming(all, me);
        Assert.All(inbox, i => Assert.Equal(o, i.AuthorMemberId));
        Assert.All(inbox, i => Assert.True(TeamShare.IsPending(i, me)));
        var file = inbox.Single(i => i.Kind == TeamItemKinds.File);
        var skill = inbox.Single(i => i.Kind == TeamItemKinds.Skill);
        var link = inbox.Single(i => i.Kind == TeamItemKinds.Link);
        var edit = inbox.Single(i => i.Kind == TeamItemKinds.Edit);
        var collection = inbox.Single(i => i.Kind == TeamItemKinds.Collection);
        Assert.Equal("for deploys", TeamShare.Comment(skill));
        Assert.Equal("Checks a deploy", TeamShare.Description(skill));
        Assert.Equal("https://example.com/spec", TeamShare.Url(link));
        Assert.Equal(["a.txt"], TeamShare.PatchFiles(edit));
        // The repository and base commit travel signed with the patch, through the core's schema and the server
        Assert.Equal("00112233445566778899aabbccddeeff", TeamShare.RepoFingerprint(edit));
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", TeamShare.BaseCommit(edit));
        Assert.Equal(["https://a.example/", "https://b.example/"], TeamShare.Links(collection, all).Select(l => l.Url));

        // Nothing is handed out before Accept
        await Assert.ThrowsAsync<InvalidOperationException>(() => ivyItems.DownloadAsync(file));

        // Accept the file (downloaded, verified, saved), decline the skill (nothing fetched or installed), accept the link
        var project = Path.Combine(host.Root, "ivy-project");
        var skills = Path.Combine(host.Root, "ivy-skills");
        await ivyItems.SetStatusAsync(team, o, file.ItemId, TeamItemStatuses.Accepted);
        var data = await ivyItems.DownloadAsync(ivyItems.Get(team, o, file.ItemId)!);
        var saved = TeamShare.SaveFile(file, data, project);
        Assert.Equal(bytes, File.ReadAllBytes(saved));
        await ivyItems.SetStatusAsync(team, o, skill.ItemId, TeamItemStatuses.Declined);
        await ivyItems.SetStatusAsync(team, o, link.ItemId, TeamItemStatuses.Accepted);
        await ivyItems.DeliverAsync();
        Assert.False(Directory.Exists(skills));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ivyItems.DownloadAsync(ivyItems.Get(team, o, skill.ItemId)!));

        // A skill accepted later installs whole (sent in a later second: Sent tells shares apart by kind, time and title)
        while ((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= skill.CreatedAt) await Task.Delay(100);
        await TeamShare.SkillFrom(skillDir).SendAsync(ownerItems, team, [me], null);
        await ownerItems.DeliverAsync();
        var again = TeamShare.Incoming(await WaitFor(ivyItems, team, l => TeamShare.Incoming(l, me).Count(i => i.Kind == TeamItemKinds.Skill) == 2), me)
            .First(i => i.Kind == TeamItemKinds.Skill && TeamShare.IsPending(i, me));
        await ivyItems.SetStatusAsync(team, o, again.ItemId, TeamItemStatuses.Accepted);
        var dir = TeamShare.InstallSkill(again, await ivyItems.DownloadAsync(ivyItems.Get(team, o, again.ItemId)!), skills);
        Assert.Equal("#!/bin/sh\necho ok\n", File.ReadAllText(Path.Combine(dir, "scripts", "check.sh")));
        await ivyItems.DeliverAsync();

        var sent = TeamShare.Sent(await WaitFor(ownerItems, team, l => TeamShare.Sent(l, o).Count(s => s.Copies.All(c => c.Status is not null)) >= 4), o);
        Assert.Equal(TeamItemStatuses.Accepted, sent.Single(s => s.Kind == TeamItemKinds.File).Copies.Single().Status);
        Assert.Contains(sent, s => s.Kind == TeamItemKinds.Skill && s.Copies.Single().Status == TeamItemStatuses.Declined);
        Assert.Contains(sent, s => s.Kind == TeamItemKinds.Skill && s.Copies.Single().Status == TeamItemStatuses.Accepted);
        Assert.Equal(TeamItemStatuses.Accepted, sent.Single(s => s.Kind == TeamItemKinds.Link).Copies.Single().Status);
        Assert.All(sent, s => Assert.Equal(me, s.Copies.Single().RecipientMemberId));
    }
}
