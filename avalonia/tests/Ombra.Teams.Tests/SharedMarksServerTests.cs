using Ombra.ClientCore;
using Ombra.ClientCore.Teams;
using Ombra.Teams.Tests.Infra;

namespace Ombra.Teams.Tests;

/// <summary>
/// K4 shared marks (P5 §4) over the real server and ombra-core: the owner's marks on code and on a page reach Ivy with
/// their anchors intact, make one toast per object, become seen when opened, and her done/dismissed with a short note
/// is what the owner sees for her.
/// </summary>
public sealed class SharedMarksServerTests(ServerStack pg)
{
    private static string Root(TeamServerHost host, string name) => Path.Combine(host.Root, "accounts", name);

    private static async Task<IReadOnlyList<TeamItem>> WaitFor(TeamItems items, string team, Func<IReadOnlyList<TeamItem>, bool> done)
    {
        for (var i = 0; i < 50; i++)
        {
            await items.DeliverAsync();
            var list = items.List(team);
            if (done(list)) return list;
            await Task.Delay(200);
        }
        throw new TimeoutException("the marks did not arrive");
    }

    [Fact]
    public async Task Marks_open_at_their_anchor_become_seen_and_the_author_sees_done_and_dismissed_with_notes()
    {
        await using var host = await TeamServerHost.StartAsync(pg);
        using var owner = await TeamSession.ConnectAsync(Root(host, "owner"), host.Address, host.Pin, new MemoryKeyStore(), new FixedMachine("owner"));
        var created = await owner.CreateTeamAsync(await host.BootstrapCodeAsync());
        await owner.SetNameAsync("Olga");
        using var ivy = await TeamSession.JoinAsync(Root(host, "ivy"), null, await owner.InviteAsync(created.TeamId, "member"), new MemoryKeyStore(), new FixedMachine("ivy"));
        await ivy.SetNameAsync("Ivy");
        await owner.ApproveAsync(Assert.Single((await owner.RefreshAsync()).Teams[0].PendingJoins));
        var ivySnap = await ivy.RefreshAsync();
        var team = created.TeamId;
        var o = TeamsHex.Of((await owner.RefreshAsync()).MemberId);
        var me = TeamsHex.Of(ivySnap.MemberId);
        var ownerItems = new TeamItems(owner.Client);
        var ivyItems = new TeamItems(ivy.Client);

        var code = new LocalMark("c1", "Rename this", Code: new CodeAnchor("src/app.py", 41, false, "def run():", CodeMarkOrigin.Editor, EndLine: 43, Before: ["import os"], After: ["  pass"]));
        var code2 = new LocalMark("c2", "And this", Code: new CodeAnchor("src/app.py", 50, false, "x = 1"));
        var page = new LocalMark("p1", "hex:not bytes, make it blue", Page: new PageAnchor("http://localhost:5173/about#top", "About",
            new PageMark(PageMarkKind.Area, 10, 20, 100, 40, 10, 1220, -6, -4, 1280, 800),
            new PageElement("#login > button", "button", "<button>", "Sign in", "src/Login.tsx:42", 0, 0, 120, 40), [new PageInsideElement("span", "span", "Go")]));
        var repo = new RepoIdentity("f00dfeedf00dfeedf00dfeedf00dfeed", "1111111111111111111111111111111111111111");
        foreach (var m in new[] { code, code2, page }) await ownerItems.SendAsync(team, SharedMarks.ToDraft(m, "b1", repo), [me]);
        await ownerItems.DeliverAsync();

        var received = SharedMarks.Received(await WaitFor(ivyItems, team, l => SharedMarks.Received(l, me).Count == 3), me);
        var gotPage = received.Single(r => r.Mark.Page is not null).Mark;
        Assert.Equal(page.Note, gotPage.Note);
        Assert.Equal(page.Page!.Element.Selector, gotPage.Page!.Element.Selector);
        Assert.Equal(-6, gotPage.Page.Mark.OffsetX);
        var gotCode = received.Single(r => r.Mark.Id == "c1").Mark.Code!;
        Assert.Equal((41, 43, "src/app.py"), (gotCode.Line!.Value, gotCode.EndLine!.Value, gotCode.Path));
        Assert.Equal(["import os"], gotCode.Before!);
        Assert.Equal((repo.Fingerprint, repo.Commit), (received.Single(r => r.Mark.Id == "c1").RepoFingerprint, received.Single(r => r.Mark.Id == "c1").Commit));
        Assert.Null(received.Single(r => r.Mark.Page is not null).Commit);
        var toasted = new HashSet<string>();
        var toasts = SharedMarks.NewToasts(received, me, toasted);
        Assert.Equal(["About", "src/app.py"], toasts.Select(t => t.ObjectTitle).Order());
        Assert.Equal(2, toasts.Single(t => t.ObjectTitle == "src/app.py").Marks.Count);
        Assert.Empty(SharedMarks.NewToasts(received, me, toasted));

        // Opened → seen; then done with a note (capped at 280) and dismissed without one
        foreach (var r in received) await ivyItems.SetStatusAsync(team, o, r.Item.ItemId, TeamItemStatuses.Seen);
        var c1 = received.Single(r => r.Mark.Id == "c1");
        await ivyItems.SetStatusAsync(team, o, c1.Item.ItemId, TeamItemStatuses.Done, SharedMarks.StatusNote(new string('n', 300)));
        await ivyItems.SetStatusAsync(team, o, received.Single(r => r.Mark.Page is not null).Item.ItemId, TeamItemStatuses.Dismissed);
        await ivyItems.DeliverAsync();

        var sent = SharedMarks.Sent(await WaitFor(ownerItems, team, l => SharedMarks.Sent(l, o) is { Count: 3 } s
            && s.Count(x => x.Recipients.Single().Status is not null) == 2 && s.All(x => x.Recipients.Single().Seen)), o);
        var done = sent.Single(s => s.Mark.Mark.Id == "c1").Recipients.Single();
        Assert.Equal((me, TeamItemStatuses.Done, 280), (done.RecipientMemberId, done.Status, done.Note!.Length));
        Assert.Equal(TeamItemStatuses.Dismissed, sent.Single(s => s.Mark.Mark.Page is not null).Recipients.Single().Status);
        var seenOnly = sent.Single(s => s.Mark.Mark.Id == "c2").Recipients.Single();
        Assert.True(seenOnly.Seen);
        Assert.Null(seenOnly.Status);
    }
}
