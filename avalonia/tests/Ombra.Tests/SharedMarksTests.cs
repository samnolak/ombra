using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.ClientCore.Teams;

namespace Ombra.Tests;

/// <summary>K4 shared marks (P5 §4): send to everyone or members, toast per author and object, open → seen, done/dismissed with a short note, statuses for the sender.</summary>
public sealed class SharedMarksTests
{
    internal const string Team = "7e4a";
    internal static readonly Guid AliceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid BobId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid CarolId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    internal static LocalMark CodeMark(string note = "Rename this") =>
        new("c1", note, Code: new CodeAnchor("src/app.py", 41, false, "def run():", CodeMarkOrigin.Editor, EndLine: 43, Before: ["import os"], After: ["  pass"]));

    internal static LocalMark PageMark(string note = "Make it blue") =>
        new("p1", note, Page: new PageAnchor("http://localhost:5173/about#top", "About", new PageMark(PageMarkKind.Area, 10, 20, 100, 40, 10, 1220, -6, -4, 1280, 800),
            new PageElement("#login > button", "button", "<button>", "Sign in", "src/Login.tsx:42", 0, 0, 120, 40), [new PageInsideElement("span", "span", "Go")]));

    internal static (SharedMarksViewModel Vm, FakeTeam Team, List<SharedMark> Shown, List<string> Toasts) Client(FakeHub hub, Guid me)
    {
        var team = new FakeTeam(new FakeTeamItems(hub, TeamsHex.Of(me)), me);
        var shown = new List<SharedMark>();
        var toasts = new List<string>();
        var vm = new SharedMarksViewModel(team, shown.Add) { Toast = (text, _) => toasts.Add(text) };
        return (vm, team, shown, toasts);
    }

    [Fact]
    public void A_mark_round_trips_through_a_team_item_with_negative_offsets_and_hex_looking_text()
    {
        var page = PageMark("hex:00 is not bytes");
        var draft = SharedMarks.ToDraft(page, "b1");
        Assert.Equal(TeamItemKinds.Mark, draft.Kind);
        // The core's mark schema: no body; the anchor is web|code; the note is the context's text, never "hex:…" (bytes)
        Assert.Null(draft.Body);
        Assert.Equal("#login > button", draft.Anchor!["web"]!["selector"]!.GetValue<string>());
        Assert.False(draft.Context!["text"]!.GetValue<string>().StartsWith("hex:", StringComparison.Ordinal));
        var item = FakeTeamItems.Item(TeamsHex.Of(AliceId), "i1", draft, TeamsHex.Of(BobId));
        var back = SharedMarks.FromItem(item)!;
        Assert.Equal("hex:00 is not bytes", back.Mark.Note);
        Assert.Equal(-6, back.Mark.Page!.Mark.OffsetX);
        Assert.Equal("#login > button", back.Mark.Page.Element.Selector);
        Assert.Equal("page:http://localhost:5173/about", back.ObjectKey);
        Assert.Equal("About", back.ObjectTitle);
        Assert.Equal("b1", back.Batch);
        var code = SharedMarks.FromItem(FakeTeamItems.Item("a", "i2", SharedMarks.ToDraft(CodeMark(), "b1"), "b"))!;
        Assert.Equal(CodeMark().Code!.Before, code.Mark.Code!.Before);
        Assert.Equal(43, code.Mark.Code.EndLine);
        Assert.Equal("file:src/app.py", code.ObjectKey);
        Assert.Throws<ArgumentException>(() => SharedMarks.ToDraft(new LocalMark("l", "", Legacy: "old block"), "b"));
        Assert.Null(SharedMarks.FromItem(item with { Kind = TeamItemKinds.Link }));
        Assert.Null(SharedMarks.FromItem(item with { Context = new JsonObject { ["text"] = "x", ["selection"] = new JsonObject { ["anchor"] = "{" } } }));
        Assert.Null(SharedMarks.FromItem(item with { Anchor = new JsonObject { ["page"] = new JsonObject() } }));
        // From another client (only the core's anchor fields): still opens at its place
        var bare = SharedMarks.FromItem(item with
        {
            Anchor = new JsonObject { ["code"] = new JsonObject { ["repoFingerprint"] = "", ["commit"] = "", ["path"] = "a.py", ["range"] = "3-5", ["excerpt"] = "x = 1" } },
            Context = new JsonObject { ["text"] = "Look" },
        })!;
        Assert.Equal(("a.py", 3, 5, "Look"), (bare.Mark.Code!.Path, bare.Mark.Code.Line!.Value, bare.Mark.Code.EndLine!.Value, bare.Mark.Note));
        var web = SharedMarks.FromItem(item with
        {
            Anchor = new JsonObject { ["web"] = new JsonObject { ["url"] = "https://example.com/a", ["selector"] = "#b", ["textQuote"] = "Buy", ["viewport"] = "1280x800" } },
            Context = null,
        })!;
        Assert.Equal(("#b", "example.com/a", 1280), (web.Mark.Page!.Element.Selector, web.ObjectTitle, web.Mark.Page.Mark.ViewportWidth));
        Assert.Equal(280, SharedMarks.StatusNote(new string('x', 300))!.Length);
        Assert.Null(SharedMarks.StatusNote("   "));
    }

    [AvaloniaFact]
    public async Task Everyone_gets_one_toast_per_author_and_object_and_the_sender_sees_each_status()
    {
        var hub = new FakeHub();
        var alice = Client(hub, AliceId);
        var bob = Client(hub, BobId);
        var carol = Client(hub, CarolId);
        Assert.True(alice.Vm.CanShare);
        Assert.Equal([Loc.T("Everyone"), "Bob", "Carol"], alice.Vm.Recipients.Select(r => r.Name));
        Assert.True(alice.Vm.Recipients[0].IsChecked);

        // Two code marks on one file, one page mark, a legacy block that cannot go
        var second = CodeMark("And this") with { Id = "c2" };
        Assert.Equal(3, await alice.Vm.SendAsync([CodeMark(), second, PageMark(), new LocalMark("l", "", Legacy: "old")]));
        Assert.Equal(6, hub.Items.Count);
        Assert.Equal(Loc.F("Sent to {0} members", 2), alice.Vm.Message);
        Assert.Equal(3, alice.Vm.Sent.Count);
        Assert.All(alice.Vm.Sent, s => Assert.Equal([Loc.T("Not opened"), Loc.T("Not opened")], s.Recipients.Select(r => r.StatusText)));
        Assert.Equal(Loc.T("Not seen yet"), alice.Vm.Sent[0].Summary);

        bob.Vm.Refresh();
        Assert.Equal(3, bob.Vm.Received.Count);
        Assert.Equal(3, bob.Vm.UnseenCount);
        Assert.Equal(new[] { Loc.F("Comments from {0} in {1}", "Alice", "About"), Loc.F("Comments from {0} in {1}", "Alice", "src/app.py") }.Order(), bob.Toasts.Order());
        bob.Vm.Refresh();
        Assert.Equal(2, bob.Toasts.Count); // toasted once

        // Opening goes to the place and marks the author's marks on that file seen
        var code = bob.Vm.Received.First(r => r.IsCode);
        Assert.Equal("src/app.py:41-43", code.Where);
        await code.OpenCommand.ExecuteAsync(null);
        Assert.Equal("src/app.py", Assert.Single(bob.Shown).Mark.Code!.Path);
        Assert.Equal(new[] { Loc.T("Not opened"), Loc.T("Seen"), Loc.T("Seen") }.Order(), bob.Vm.Received.Select(r => r.StatusText).Order());
        alice.Vm.Refresh();
        Assert.Equal(Loc.F("{0} of {1} seen", 1, 2), alice.Vm.Sent.First(s => s.IsCode).Summary);
        Assert.Equal(new[] { Loc.T("Not opened"), Loc.T("Seen") }.Order(), alice.Vm.Sent.First(s => s.IsCode).Recipients.Select(r => r.StatusText).Order());

        // Done with a note (capped at 280), dismissed without one
        code = bob.Vm.Received.First(r => r.IsCode);
        code.ReplyNote = new string('n', 300);
        Assert.Equal(280, code.ReplyNote.Length);
        await code.DoneCommand.ExecuteAsync(null);
        var page = bob.Vm.Received.First(r => !r.IsCode);
        await page.DismissCommand.ExecuteAsync(null);
        alice.Vm.Refresh();
        var sentCode = alice.Vm.Sent.First(s => s.Sent.Recipients.Any(r => r.Status == TeamItemStatuses.Done));
        Assert.Equal(Loc.F("{0} of {1} done", 1, 2), sentCode.Summary);
        var bobRow = sentCode.Recipients.Single(r => r.StatusText == Loc.T("Done"));
        Assert.Equal("Bob", bobRow.Name);
        Assert.Equal(280, bobRow.Note!.Length);
        Assert.Contains(alice.Vm.Sent.SelectMany(s => s.Recipients), r => r.StatusText == Loc.T("Dismissed") && !r.HasNote);

        // Carol got her own copies, untouched by Bob's statuses (no one sees the others)
        carol.Vm.Refresh();
        Assert.All(carol.Vm.Received, r => Assert.Equal(Loc.T("Not opened"), r.StatusText));
        // No reply action exists: rows offer open, done and dismiss only
        Assert.DoesNotContain(typeof(ReceivedMarkRow).GetProperties(), p => p.Name.Contains("Reply", StringComparison.Ordinal) && p.PropertyType.Name.Contains("Command", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Members_only_reach_the_chosen_member_and_page_marks_become_pins_until_done()
    {
        var hub = new FakeHub();
        var alice = Client(hub, AliceId);
        var bob = Client(hub, BobId);
        var carol = Client(hub, CarolId);
        alice.Vm.Recipients[0].IsChecked = false;
        Assert.Equal(0, await alice.Vm.SendAsync([PageMark()]));
        Assert.Equal(Loc.T("Choose who gets the comments."), alice.Vm.Message);
        alice.Vm.Recipients.Single(r => r.Name == "Carol").IsChecked = true;
        Assert.Equal(1, await alice.Vm.SendAsync([PageMark()]));
        Assert.Equal(Loc.F("Sent to {0}", "Carol"), alice.Vm.Message);
        bob.Vm.Refresh();
        Assert.Empty(bob.Vm.Received);

        IReadOnlyList<SharedMark> pins = [];
        carol.Vm.PagePinsChanged += p => pins = p;
        carol.Vm.Refresh();
        var row = Assert.Single(carol.Vm.Received);
        Assert.Equal("localhost:5173/about", row.Where);
        Assert.Single(pins);
        var preview = new PreviewViewModel();
        preview.OnNavigationCompleted(new Uri("http://localhost:5173/about"), true, false, false);
        preview.SetSharedPins(pins);
        var json = JsonDocument.Parse(preview.PinsJson(preview.CurrentUrl)).RootElement;
        var pin = Assert.Single(json.EnumerateArray());
        Assert.True(pin.GetProperty("shared").GetBoolean());
        Assert.Equal("#login > button", pin.GetProperty("selector").GetString());
        preview.RevealShared(pins[0]);
        Assert.Equal(PreviewViewModel.SharedPinId(pins[0]), preview.PendingReveal);

        await row.DoneCommand.ExecuteAsync(null);
        Assert.Empty(pins);
    }

    [AvaloniaFact]
    public async Task A_received_mark_goes_to_my_agent_and_one_on_another_commit_may_have_moved()
    {
        var hub = new FakeHub();
        var alice = Client(hub, AliceId);
        var bob = Client(hub, BobId);
        var mine = new RepoIdentity("f00dfeedf00dfeedf00dfeedf00dfeed", "1111111111111111111111111111111111111111");
        alice.Vm.LocalRepo = mine;
        alice.Vm.Recipients[0].IsChecked = false;
        alice.Vm.Recipients.Single(r => r.Name == "Bob").IsChecked = true;
        await alice.Vm.SendAsync([CodeMark(), PageMark()]);
        // Code marks carry the sender's repository and commit in the core's anchor fields; page marks have none
        var code = SharedMarks.Received(hub.Items, TeamsHex.Of(BobId)).Single(s => s.Mark.Code is not null);
        Assert.Equal((mine.Fingerprint, mine.Commit), (code.RepoFingerprint, code.Commit));
        Assert.Equal(mine.Commit, code.Item.Anchor!["code"]!["commit"]!.GetValue<string>());

        // Bob on the same commit: nothing moved; on another commit of the same repository: the line may have moved
        bob.Vm.LocalRepo = mine;
        Assert.All(bob.Vm.Received, r => Assert.False(r.MayHaveMoved));
        bob.Vm.LocalRepo = mine with { Commit = "2222222222222222222222222222222222222222" };
        var moved = bob.Vm.Received.Single(r => r.IsCode);
        Assert.True(moved.MayHaveMoved);
        Assert.Equal(Loc.F("Left on commit {0}: the line may have moved.", "1111111"), moved.MovedText);
        Assert.False(bob.Vm.Received.Single(r => !r.IsCode).MayHaveMoved);
        bob.Vm.LocalRepo = new RepoIdentity("0ther0ther0ther0ther0ther0ther00", "2222222222222222222222222222222222222222");
        Assert.False(bob.Vm.Received.Single(r => r.IsCode).MayHaveMoved); // another repository: commits are not comparable

        // «В запрос агенту»: the mark with its anchor joins my message box; the author's status does not change
        var composer = new MarkCollection();
        bob.Vm.ToComposer = composer.Add;
        var row = bob.Vm.Received.Single(r => r.IsCode);
        row.ToAgentCommand.Execute(null);
        var added = Assert.Single(composer.Items);
        Assert.Equal("Rename this (left by Alice)", added.Note);
        Assert.Equal(CodeMark().Code!.Path, added.Code!.Path);
        Assert.Equal(43, added.Code.EndLine);
        Assert.NotEqual(CodeMark().Id, added.Id);
        Assert.Equal(Loc.T("Added to your message for the agent."), bob.Vm.Message);
        Assert.Contains("1. src/app.py, lines 41-43 (in the editor): Rename this (left by Alice)", LocalMarks.BuildRequest("", composer.Models()));
        bob.Vm.Received.Single(r => !r.IsCode).ToAgentCommand.Execute(null);
        Assert.Equal("#login > button", composer.Items[1].Page!.Element.Selector);
        Assert.All(bob.Vm.Received, r => Assert.Equal(Loc.T("Not opened"), r.StatusText));
    }

    [AvaloniaFact]
    public void Without_a_team_nothing_can_be_sent()
    {
        var team = new FakeTeam(null, AliceId);
        var vm = new SharedMarksViewModel(team, _ => { });
        Assert.False(vm.CanShare);
        Assert.True(vm.IsEmpty);
        Assert.Equal(0, vm.SendAsync([CodeMark()]).Result);
        Assert.Equal(Loc.T("Join a team to send comments to its members."), vm.Message);
    }

    [AvaloniaFact]
    public async Task Toast_and_statuses_are_russian_in_russian()
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("shared-marks-ru"), "settings.json");
        var previous = Loc.Current;
        Loc.Load("ru", settingsPath);
        try
        {
            var hub = new FakeHub();
            var alice = Client(hub, AliceId);
            var bob = Client(hub, BobId);
            Assert.Equal("Все", alice.Vm.Recipients[0].Name);
            await alice.Vm.SendAsync([CodeMark()]);
            Assert.Equal("Отправлено участникам: 2", alice.Vm.Message);
            bob.Vm.Refresh();
            Assert.Equal("Комментарии от Alice в src/app.py", Assert.Single(bob.Toasts));
            Assert.Equal("Не открыта", bob.Vm.Received[0].StatusText);
            Assert.Equal("Ещё не просмотрено", alice.Vm.Sent[0].Summary);
        }
        finally { Loc.Load(previous, settingsPath); }
    }

    /// <summary>The Team page's Comments card: received with open/done/dismiss and a 280-character note box, sent with each recipient's status.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task The_comments_card_shows_received_and_sent_marks(string language)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("shared-marks-view-" + language), "settings.json");
        var previous = Loc.Current;
        Loc.Load(language, settingsPath);
        try
        {
            var hub = new FakeHub();
            var alice = Client(hub, AliceId);
            var bob = Client(hub, BobId);
            await alice.Vm.SendAsync([CodeMark()]);
            await bob.Vm.SendAsync([PageMark("Too small")]);
            await bob.Vm.Received[0].DoneCommand.ExecuteAsync(null);
            alice.Vm.Refresh();
            var view = new Ombra.App.Views.Team.SharedMarksView { DataContext = alice.Vm };
            var w = new Avalonia.Controls.Window { Content = view, Width = 760, Height = 700 };
            w.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var texts = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Too small", texts);
            Assert.Contains("Rename this", texts);
            Assert.Contains("localhost:5173/about", texts);
            Assert.Contains(Loc.T("For you"), texts);
            Assert.Contains(Loc.T("Sent by you"), texts);
            // A recipient's status is on their line: "Bob · Done"
            Assert.Contains(texts, t => t?.EndsWith(" · " + Loc.T("Done"), StringComparison.Ordinal) == true);
            Assert.Contains(Loc.F("{0} of {1} done", 1, 2), texts);
            if (language == "ru")
            {
                Assert.DoesNotContain("For you", texts);
                Assert.DoesNotContain("Sent by you", texts);
                Assert.DoesNotContain(texts, t => t?.Contains("Not opened", StringComparison.Ordinal) == true);
            }
            var note = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Avalonia.Controls.TextBox>().Single();
            Assert.Equal(280, note.MaxLength);
            var buttons = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Avalonia.Controls.Button>().Select(b => b.Content as string).ToList();
            Assert.Equal([Loc.T("To my agent"), Loc.T("Open"), Loc.T("Mark as done"), Loc.T("Dismiss comment")], buttons);
            w.Close();
        }
        finally { Loc.Load(previous, settingsPath); }
    }
}

/// <summary>The team as shared marks see it.</summary>
internal sealed class FakeTeam(ITeamItems? items, Guid me) : ITeamContext
{
    public string? SelectedTeamId => SharedMarksTests.Team;
    public Guid? MyMemberId => me;
    public IReadOnlyList<TeamMember> Members { get; } =
    [
        new(SharedMarksTests.AliceId, "owner", "Alice", [], me == SharedMarksTests.AliceId, ""),
        new(SharedMarksTests.BobId, "member", "Bob", [], me == SharedMarksTests.BobId, ""),
        new(SharedMarksTests.CarolId, "member", "Carol", [], me == SharedMarksTests.CarolId, ""),
    ];
    public ITeamItems? Items => items;
    public event Action? Changed { add { } remove { } }
}

/// <summary>One replica every fake client reads (as if delivery were instant).</summary>
internal sealed class FakeHub
{
    public List<TeamItem> Items { get; } = [];
    public event Action<string>? Changed;
    public void Raise() => Changed?.Invoke(SharedMarksTests.Team);
}

/// <summary>In-memory <see cref="ITeamItems"/>: one copy per recipient, statuses by the recipient.</summary>
internal sealed class FakeTeamItems : ITeamItems
{
    private readonly FakeHub _hub;
    private readonly string _me;
    private int _next;

    public FakeTeamItems(FakeHub hub, string me)
    {
        _hub = hub;
        _me = me;
        hub.Changed += t => ItemsChanged?.Invoke(t);
    }

    public event Action<string>? ItemsChanged;

    internal static TeamItem Item(string author, string id, TeamItemDraft d, string recipient) =>
        new(SharedMarksTests.Team, author, id, d.Kind, 1, 0, null, recipient, false, d.Anchor, d.Context, d.Body, d.Attachments, [], null);

    public Task<IReadOnlyList<TeamItemCopy>> SendAsync(string teamId, TeamItemDraft draft, IReadOnlyList<string> recipientMemberIds, CancellationToken ct = default)
    {
        var copies = new List<TeamItemCopy>();
        foreach (var r in recipientMemberIds)
        {
            var id = $"{_me[..4]}{++_next:x4}";
            _hub.Items.Insert(0, Item(_me, id, draft with { Body = draft.Body?.DeepClone(), Anchor = draft.Anchor?.DeepClone(), Context = draft.Context?.DeepClone() }, r));
            copies.Add(new TeamItemCopy(r, "g", id, "op"));
        }
        _hub.Raise();
        return Task.FromResult<IReadOnlyList<TeamItemCopy>>(copies);
    }

    public Task SetStatusAsync(string teamId, string authorMemberId, string itemId, string status, string? note = null, CancellationToken ct = default)
    {
        var i = _hub.Items.FindIndex(x => x.AuthorMemberId == authorMemberId && x.ItemId == itemId);
        if (i < 0 || _hub.Items[i].RecipientMemberId != _me) throw new InvalidOperationException("not addressed to me");
        var old = _hub.Items[i].Statuses.FirstOrDefault(s => s.RecipientMemberId == _me);
        var next = status == TeamItemStatuses.Seen
            ? new TeamItemStatus(_me, true, old?.Status, old?.Note)
            : new TeamItemStatus(_me, true, status, note);
        _hub.Items[i] = _hub.Items[i] with { Statuses = [.. _hub.Items[i].Statuses.Where(s => s.RecipientMemberId != _me), next] };
        _hub.Raise();
        return Task.CompletedTask;
    }

    public IReadOnlyList<TeamItem> List(string teamId, int limit = 200, ulong? before = null) =>
        [.. _hub.Items.Where(x => x.TeamId == teamId && (x.AuthorMemberId == _me || x.RecipientMemberId == _me)).Take(limit)];

    public TeamItem? Get(string teamId, string authorMemberId, string itemId) => _hub.Items.FirstOrDefault(x => x.AuthorMemberId == authorMemberId && x.ItemId == itemId);
    public Task EditAsync(string teamId, string itemId, ulong baseRev, TeamItemDraft delta, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteAsync(string teamId, string authorMemberId, string itemId, CancellationToken ct = default) => throw new NotSupportedException();
    public int PendingCount(string? teamId = null) => 0;
    public Task<TeamDeliveryResult> DeliverAsync(CancellationToken ct = default) => Task.FromResult(new TeamDeliveryResult(0, 0, 0, []));

    private static readonly Dictionary<string, byte[]> Blobs = [];

    public async Task<TeamBlob> UploadAsync(string teamId, string recipientMemberId, Stream content, CancellationToken ct = default)
    {
        using var m = new MemoryStream();
        await content.CopyToAsync(m, ct);
        var id = Guid.NewGuid().ToString("D");
        lock (Blobs) Blobs[id] = m.ToArray();
        return new TeamBlob(id, (ulong)m.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(m.ToArray())), "00");
    }

    public Task<byte[]> DownloadAsync(TeamItem item, CancellationToken ct = default)
    {
        if (item.Statuses.FirstOrDefault(s => s.RecipientMemberId == _me)?.Status != TeamItemStatuses.Accepted) throw new InvalidOperationException("accept first");
        lock (Blobs) return Task.FromResult(Blobs[item.Body!["blobRef"]!.GetValue<string>()]);
    }
}
