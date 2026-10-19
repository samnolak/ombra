using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.ClientCore.Teams;

namespace Ombra.Tests;

/// <summary>
/// P5 resources (C1 + C3 UI): Share on a link, file, skill, patch and collection → recipients (everyone or members by
/// name) → toast and Inbox → Accept / Decline (nothing lands before Accept) → the sender sees each recipient's status.
/// </summary>
public sealed class TeamShareTests
{
    private const string Team = SharedMarksTests.Team;
    private static readonly string A = TeamsHex.Of(SharedMarksTests.AliceId);
    private static readonly string B = TeamsHex.Of(SharedMarksTests.BobId);
    private static readonly string C = TeamsHex.Of(SharedMarksTests.CarolId);

    private static string SkillFolder(string root, string name = "deploy-check")
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "scripts"));
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: Checks a deploy\n---\nRun scripts/check.sh\n");
        File.WriteAllText(Path.Combine(dir, "scripts", "check.sh"), "#!/bin/sh\necho ok\n");
        File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), "ref: x\n");
        return dir;
    }

    [Fact]
    public async Task Every_resource_kind_round_trips_and_the_sender_sees_one_row_per_share()
    {
        var hub = new FakeHub();
        var alice = new FakeTeamItems(hub, A);
        var bob = new FakeTeamItems(hub, B);
        var tmp = TestProcesses.TempDir("team-share-kinds");
        var skill = SkillFolder(tmp);

        Task Send(ShareContent c, params string[] to) => c.SendAsync(alice, Team, to, "see this");
        await Send(TeamShare.Link("https://example.dev/docs", "Docs"), B, C);
        await Send(TeamShare.File("notes.md", Encoding.UTF8.GetBytes("# hi\n")), B);
        await Send(TeamShare.SkillFrom(skill), B);
        await Send(TeamShare.Edit("Changes in app", "diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -1 +1 @@\n-a\n+b\n", new RepoIdentity("00112233445566778899aabbccddeeff", "0123456789abcdef")), B);
        await Send(TeamShare.Collection("2 pages", [new SharedLink("https://a.dev/", "A"), new SharedLink("https://b.dev/", null)]), B);
        await Send(TeamShare.Link("hex:not-bytes"), B);

        // Only the core's fields: a comment is context text, content a blob named in the body with its key beside it
        Assert.All(hub.Items, i => Assert.Equal("see this", TeamShare.Comment(i) ?? "see this"));
        var all = bob.List(Team);
        var inbox = TeamShare.Incoming(all, B);
        Assert.Equal(6, inbox.Count); // the collection's two links show as the collection
        Assert.All(inbox, i => Assert.True(TeamShare.IsPending(i, B)));
        var link = inbox.Single(i => i.Kind == TeamItemKinds.Link && TeamShare.Url(i) == "https://example.dev/docs");
        Assert.Equal("Docs", TeamShare.Title(link));
        Assert.Equal(["url", "title"], link.Body!.AsObject().Select(p => p.Key));
        var file = inbox.Single(i => i.Kind == TeamItemKinds.File);
        Assert.Equal(["name", "blobRef", "size", "sha256"], file.Body!.AsObject().Select(p => p.Key));
        Assert.Equal(5UL, TeamShare.Size(file));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.DownloadAsync(file)); // not before Accept
        await bob.SetStatusAsync(Team, A, file.ItemId, TeamItemStatuses.Accepted);
        Assert.Equal("# hi\n", Encoding.UTF8.GetString(await bob.DownloadAsync(bob.Get(Team, A, file.ItemId)!)));
        var sk = inbox.Single(i => i.Kind == TeamItemKinds.Skill);
        Assert.Equal("deploy-check", TeamShare.Title(sk));
        Assert.Equal("Checks a deploy", TeamShare.Description(sk));
        var edit = inbox.Single(i => i.Kind == TeamItemKinds.Edit);
        Assert.StartsWith("diff --git", TeamShare.PatchText(edit));
        Assert.Equal("0123456789abcdef", TeamShare.BaseCommit(edit));
        Assert.Equal("00112233445566778899aabbccddeeff", TeamShare.RepoFingerprint(edit));
        Assert.Equal(["x"], TeamShare.PatchFiles(edit));
        Assert.Equal(["https://a.dev/", "https://b.dev/"], TeamShare.Links(inbox.Single(i => i.Kind == TeamItemKinds.Collection), all).Select(l => l.Url));
        // Text that looks like the core's byte marker stays text
        Assert.Contains(inbox, i => TeamShare.Url(i)?.EndsWith("hex:not-bytes") == true);

        await bob.SetStatusAsync(Team, A, link.ItemId, TeamItemStatuses.Declined);
        var sent = TeamShare.Sent(alice.List(Team), A);
        var linkRow = sent.Single(s => s.Title == "Docs");
        Assert.Equal(2, linkRow.Copies.Count);
        Assert.Equal(TeamItemStatuses.Declined, linkRow.Copies.Single(c => c.RecipientMemberId == B).Status);
        Assert.Null(linkRow.Copies.Single(c => c.RecipientMemberId == C).Status);
        Assert.DoesNotContain(sent, s => s.Title == "https://a.dev/"); // links inside the collection are not rows of their own
    }

    [Fact]
    public void Accepting_writes_only_inside_its_folder_under_a_free_name_and_large_content_is_refused()
    {
        var tmp = TestProcesses.TempDir("team-share-accept");
        TeamItem Item(string kind, string name) => new(Team, A, "1", kind, 1, 0, null, B, false, null, null, new System.Text.Json.Nodes.JsonObject { ["name"] = name }, null, [], null);

        var project = Path.Combine(tmp, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "notes.md"), "mine");
        var saved = TeamShare.SaveFile(Item(TeamItemKinds.File, "../notes.md"), [1, 2], project);
        Assert.Equal(Path.Combine(project, "notes (2).md"), saved);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(project, "notes.md")));

        using var zip = new MemoryStream();
        using (var a = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            a.CreateEntry("SKILL.md").Open().Dispose();
            using var s = a.CreateEntry("../../escape.txt").Open();
            s.WriteByte(1);
        }
        var skills = Path.Combine(tmp, "skills");
        Assert.Throws<InvalidDataException>(() => TeamShare.InstallSkill(Item(TeamItemKinds.Skill, "evil"), zip.ToArray(), skills));
        Assert.False(File.Exists(Path.Combine(tmp, "escape.txt")));
        Assert.False(Directory.Exists(Path.Combine(skills, "evil")));

        var big = Assert.Throws<TeamShareTooLargeException>(() => TeamShare.File("big.bin", new byte[TeamShare.MaxContentBytes + 1]));
        Assert.Equal(TeamShare.MaxContentBytes + 1, big.Bytes);
    }

    private static byte[] Zip(Action<System.IO.Compression.ZipArchive> fill)
    {
        using var zip = new MemoryStream();
        using (var a = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, true)) fill(a);
        return zip.ToArray();
    }

    [Fact]
    public void A_skill_zip_over_the_entry_or_unpacked_limit_or_failing_midway_installs_nothing_and_leaves_no_staging()
    {
        var tmp = TestProcesses.TempDir("team-share-zip-limits");
        var skills = Path.Combine(tmp, "skills");
        TeamItem Item(string name) => new(Team, A, "1", TeamItemKinds.Skill, 1, 0, null, B, false, null, null, new System.Text.Json.Nodes.JsonObject { ["name"] = name }, null, [], null);
        void Refused(string name, byte[] zip)
        {
            var e = Assert.ThrowsAny<Exception>(() => TeamShare.InstallSkill(Item(name), zip, skills));
            Assert.True(e is InvalidDataException or IOException, e.ToString());
            Assert.False(Directory.Exists(Path.Combine(skills, name)));
            Assert.False(Directory.Exists(Path.Combine(skills, name + ".part")));
        }

        // more entries than allowed (empty files: nothing large anywhere)
        Refused("many", Zip(a =>
        {
            a.CreateEntry("SKILL.md").Open().Dispose();
            for (var i = 0; i < TeamShare.MaxSkillEntries; i++) a.CreateEntry($"f/{i}.txt").Open().Dispose();
        }));
        // a small zip of zeros that unpacks past the limit
        var bomb = Zip(a =>
        {
            a.CreateEntry("SKILL.md").Open().Dispose();
            using var s = a.CreateEntry("zeros.bin", System.IO.Compression.CompressionLevel.SmallestSize).Open();
            var chunk = new byte[1 << 20];
            for (long left = TeamShare.MaxSkillUnpackedBytes + 1; left > 0; left -= chunk.Length) s.Write(chunk, 0, (int)Math.Min(chunk.Length, left));
        });
        Assert.True(bomb.Length < TeamShare.MaxContentBytes / 50);
        Refused("bomb", bomb);
        // a failure after files were written (the same path twice): the staging folder goes too
        Refused("twice", Zip(a =>
        {
            using (var s = a.CreateEntry("SKILL.md").Open()) s.WriteByte(1);
            using (var s = a.CreateEntry("a.txt").Open()) s.WriteByte(1);
            using (var s = a.CreateEntry("a.txt").Open()) s.WriteByte(2);
        }));
    }

    [Fact]
    public void A_blob_opens_only_whole_in_order_with_its_key_and_id()
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var id = Guid.NewGuid();
        var plain = System.Security.Cryptography.RandomNumberGenerator.GetBytes(TeamItems.ChunkBytes * 2 + 100);
        var sealedBytes = TeamItems.Seal(plain, key, id);
        Assert.Equal(plain, TeamItems.Open(sealedBytes, key, id));
        Assert.Throws<InvalidDataException>(() => TeamItems.Open(sealedBytes.AsSpan(0, sealedBytes.Length - 116), key, id)); // cut at a chunk
        Assert.Throws<InvalidDataException>(() => TeamItems.Open(sealedBytes, key, Guid.NewGuid())); // another blob's id
        var flipped = (byte[])sealedBytes.Clone();
        flipped[10] ^= 1;
        Assert.Throws<InvalidDataException>(() => TeamItems.Open(flipped, key, id));
    }

    // ───────────── The app: two members, each with its own window model ─────────────

    private sealed record Member(MainViewModel Vm, FakeTeamItems Items, string Project, string Skills);

    private static async Task<Member> Open(FakeHub hub, Guid id, string root)
    {
        Environment.SetEnvironmentVariable("OMBRA_TEAMS_DIR", Path.Combine(root, "teams"));
        var me = TeamsHex.Of(id);
        var project = Path.Combine(root, me[..4], "project");
        var skills = Path.Combine(root, me[..4], "skills");
        Directory.CreateDirectory(project);
        var vm = new MainViewModel(new SessionController(TestProcesses.FakeFactory("normal", Path.Combine(root, me[..4], "sessions")), new LaunchRequest(project)), new AppArgs())
        {
            UserSkillsFolderAsync = () => Task.FromResult(skills),
        };
        var items = new FakeTeamItems(hub, me);
        vm.Share.Attach(new FakeTeam(items, id));
        await vm.Share.Refreshed;
        return new Member(vm, items, project, skills);
    }

    /// <summary>Lets every member's posted refresh run and finish (the fake hub tells all of them at once).</summary>
    private static async Task Settle(params Member[] members)
    {
        for (var round = 0; round < 3; round++)
        {
            Dispatcher.UIThread.RunJobs();
            foreach (var m in members) await m.Vm.Share.Refreshed;
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task A_file_to_one_member_found_by_name_toasts_waits_in_the_inbox_and_lands_only_after_accept(string language)
    {
        var tmp = TestProcesses.TempDir("team-share-file-" + language);
        var previous = Loc.Current;
        Loc.Load(language, Path.Combine(tmp, "settings.json"));
        var hub = new FakeHub();
        var alice = await Open(hub, SharedMarksTests.AliceId, tmp);
        var bob = await Open(hub, SharedMarksTests.BobId, tmp);
        var carol = await Open(hub, SharedMarksTests.CarolId, tmp);
        Member[] all = [alice, bob, carol];
        try
        {
            var file = Path.Combine(alice.Project, "notes.md");
            File.WriteAllText(file, "shared text");

            alice.Vm.ShareFile(file);
            var share = alice.Vm.Share;
            Assert.True(share.IsDialogOpen);
            Assert.Equal(Loc.F("Share {0} “{1}”", Loc.T("a file"), "notes.md"), share.DialogTitle);
            Assert.True(share.Recipients.Everyone);
            Assert.Equal(["Bob", "Carol"], share.Recipients.Members.Select(m => m.Name)); // not me
            share.Recipients.IsSpecific = true;
            Assert.False(share.Recipients.CanSend);
            share.Recipients.Filter = "bo";
            Assert.Equal(["Bob"], share.Recipients.Members.Select(m => m.Name));
            share.Recipients.Members[0].IsSelected = true;
            share.Comment = "for the review";
            await share.SendCommand.ExecuteAsync(null);
            await Settle(all);
            Assert.False(share.IsDialogOpen);

            // Bob: a toast with Open, one Inbox row with a preview; nothing on disk yet
            Assert.Equal(Loc.F("{0} shared {1} “{2}”", "Alice", Loc.T("a file"), "notes.md"), bob.Vm.SessionCard?.Title);
            Assert.Equal(Loc.T("Open"), bob.Vm.SessionCard?.Primary?.Label);
            var row = Assert.Single(bob.Vm.Share.Inbox);
            Assert.Equal(1, bob.Vm.Share.InboxCount);
            Assert.Equal("for the review", row.Comment);
            Assert.Equal(Loc.F("{0} shared {1}", "Alice", Loc.T("a file")), row.Headline);
            Assert.False(File.Exists(Path.Combine(bob.Project, "notes.md")));
            Assert.Empty(carol.Vm.Share.Inbox); // addressed to Bob only
            Assert.Null(carol.Vm.SessionCard);

            // Open from the toast: the Team page, the item seen for Alice
            bob.Vm.SessionCard!.Primary!.Command.Execute(null);
            await Settle(all);
            Assert.True(bob.Vm.IsTeamPageOpen);
            await Task.Yield();
            Assert.Equal(Loc.T("Seen"), alice.Vm.Share.Sent.Single().Recipients.Single().StatusText);

            await row.AcceptCommand.ExecuteAsync(null);
            await Settle(all);
            Assert.Equal("shared text", File.ReadAllText(Path.Combine(bob.Project, "notes.md")));
            Assert.Equal(0, bob.Vm.Share.InboxCount);
            Assert.False(bob.Vm.Share.HasInbox);
            Assert.Equal(Loc.T("Accepted"), bob.Vm.Share.Inbox.Single().StatusText);

            var sent = alice.Vm.Share.Sent.Single();
            Assert.Equal("Bob", sent.Recipients.Single().Name);
            Assert.Equal(Loc.T("Accepted"), sent.Recipients.Single().StatusText);
            Assert.Equal(Loc.F("{0} of {1} answered", 1, 1), sent.Summary);
            if (language == "ru")
            {
                Assert.DoesNotContain("shared", bob.Vm.Share.Inbox.Single().Headline);
                Assert.NotEqual("Accepted", sent.Recipients.Single().StatusText);
            }
        }
        finally
        {
            Loc.Load(previous, Path.Combine(tmp, "settings.json"));
            await alice.Vm.DisposeAsync();
            await bob.Vm.DisposeAsync();
            await carol.Vm.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task A_skill_to_everyone_installs_only_for_who_accepts_and_a_link_opens_in_the_browser_page()
    {
        var tmp = TestProcesses.TempDir("team-share-skill");
        var hub = new FakeHub();
        var alice = await Open(hub, SharedMarksTests.AliceId, tmp);
        var bob = await Open(hub, SharedMarksTests.BobId, tmp);
        var carol = await Open(hub, SharedMarksTests.CarolId, tmp);
        Member[] all = [alice, bob, carol];
        try
        {
            var skill = SkillFolder(Path.Combine(tmp, "alice-skills"));
            alice.Vm.Share.Begin(TeamItemKinds.Skill, "deploy-check", skill, () => TeamShare.SkillFrom(skill));
            Assert.True(alice.Vm.Share.Recipients.Everyone);
            await alice.Vm.Share.SendCommand.ExecuteAsync(null);
            await Settle(all);

            var bobRow = Assert.Single(bob.Vm.Share.Inbox);
            var carolRow = Assert.Single(carol.Vm.Share.Inbox);
            Assert.Contains("Checks a deploy", bobRow.Preview); // what it is, before Accept
            Assert.False(Directory.Exists(bob.Skills));
            await bobRow.DeclineCommand.ExecuteAsync(null);
            await carolRow.AcceptCommand.ExecuteAsync(null);
            await Settle(all);
            Assert.False(Directory.Exists(bob.Skills)); // declined: nothing written
            Assert.True(File.Exists(Path.Combine(carol.Skills, "deploy-check", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(carol.Skills, "deploy-check", "scripts", "check.sh")));

            var sent = alice.Vm.Share.Sent.Single();
            Assert.Equal([("Bob", Loc.T("Declined")), ("Carol", Loc.T("Accepted"))], sent.Recipients.Select(r => (r.Name, r.StatusText)).OrderBy(r => r.Name));

            // A link: before Accept the browser stays closed; Accept opens it in the Browser page
            alice.Vm.ShareLink("https://example.com/spec");
            alice.Vm.Share.Recipients.Pick(B);
            await alice.Vm.Share.SendCommand.ExecuteAsync(null);
            await Settle(all);
            var link = bob.Vm.Share.Inbox.First(r => r.Item.Kind == TeamItemKinds.Link);
            Assert.False(bob.Vm.IsBrowserPageOpen);
            await link.AcceptCommand.ExecuteAsync(null);
            Assert.True(bob.Vm.IsBrowserPageOpen);
            Assert.Equal("https://example.com/spec", bob.Vm.Browser.ActiveTab.Url?.AbsoluteUri);
        }
        finally
        {
            await alice.Vm.DisposeAsync();
            await bob.Vm.DisposeAsync();
            await carol.Vm.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task A_patch_applies_only_to_a_clone_of_its_repository_never_to_the_open_project_of_another()
    {
        var tmp = TestProcesses.TempDir("team-share-patch-repo");
        var hub = new FakeHub();
        var alice = await Open(hub, SharedMarksTests.AliceId, tmp);
        var bob = await Open(hub, SharedMarksTests.BobId, tmp);
        Member[] all = [alice, bob];
        void Repo(string dir, string message)
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "a\n");
            FileTestRepo.Git(dir, "init", "-q");
            FileTestRepo.Git(dir, "add", "-A");
            FileTestRepo.Git(dir, "commit", "-q", "-m", message);
        }
        try
        {
            // Same file, same text, two unrelated repositories: the patch would apply cleanly to both
            Repo(alice.Project, "alice");
            Repo(bob.Project, "bob");
            var clone = Path.Combine(tmp, "bob-clone");
            FileTestRepo.Git(tmp, "clone", "-q", alice.Project, clone);
            File.WriteAllText(Path.Combine(alice.Project, "a.txt"), "b\n");

            await alice.Vm.ShareChangesCommand.ExecuteAsync(null);
            alice.Vm.Share.Recipients.Pick(B);
            await alice.Vm.Share.SendCommand.ExecuteAsync(null);
            await Settle(all);
            var row = Assert.Single(bob.Vm.Share.Inbox);
            Assert.Equal((await RepoIdentity.OfAsync(clone))!.Fingerprint, TeamShare.RepoFingerprint(row.Item));
            await row.AcceptCommand.ExecuteAsync(null);
            await Settle(all);
            row = Assert.Single(bob.Vm.Share.Inbox);
            Assert.True(row.CanApply);

            // The open project is another repository: the user is asked for a folder, and the other one is refused
            var picks = new Queue<string?>([bob.Project, null, clone]);
            var asked = 0;
            bob.Vm.PickFolderRequested += () => { asked++; return Task.FromResult(picks.Dequeue()); };
            await row.ApplyCommand.ExecuteAsync(null);
            Assert.Equal(1, asked);
            Assert.Equal("a\n", File.ReadAllText(Path.Combine(bob.Project, "a.txt")));
            await row.ApplyCommand.ExecuteAsync(null); // cancelled
            Assert.Equal("a\n", File.ReadAllText(Path.Combine(bob.Project, "a.txt")));
            await row.ApplyCommand.ExecuteAsync(null);
            Assert.Equal(3, asked);
            Assert.Equal("b\n", File.ReadAllText(Path.Combine(clone, "a.txt")));
            Assert.Equal("a\n", File.ReadAllText(Path.Combine(bob.Project, "a.txt")));
        }
        finally
        {
            await alice.Vm.DisposeAsync();
            await bob.Vm.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task Without_a_team_share_says_where_to_join_and_opens_nothing()
    {
        Environment.SetEnvironmentVariable("OMBRA_TEAMS_DIR", Path.Combine(TestProcesses.TempDir("team-share-none"), "teams"));
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        try
        {
            vm.Share.Attach(new FakeTeam(null, SharedMarksTests.AliceId));
            vm.ShareLink("https://example.com");
            Assert.False(vm.Share.IsDialogOpen);
            Assert.Equal(Loc.T("Join a team to share"), vm.SessionCard?.Title);
        }
        finally { await vm.DisposeAsync(); }
    }

    /// <summary>The dialog and the Team page's Inbox and Shared-by-you cards, rendered.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task The_dialog_inbox_and_sent_cards_render_in_the_language(string language)
    {
        var tmp = TestProcesses.TempDir("team-share-view-" + language);
        var previous = Loc.Current;
        Loc.Load(language, Path.Combine(tmp, "settings.json"));
        var hub = new FakeHub();
        var alice = await Open(hub, SharedMarksTests.AliceId, tmp);
        var bob = await Open(hub, SharedMarksTests.BobId, tmp);
        try
        {
            alice.Vm.ShareLink("https://example.com/spec");
            await alice.Vm.Share.SendCommand.ExecuteAsync(null);
            await Settle(alice, bob);
            bob.Vm.ShareLink("https://example.com/back");
            bob.Vm.Share.IsDialogOpen = true;

            var section = new Ombra.App.Views.Team.TeamShareSection { DataContext = bob.Vm };
            var dialog = new Ombra.App.Views.Team.ShareDialog { DataContext = bob.Vm };
            var w = new Window { Content = new StackPanel { Children = { section, dialog } }, Width = 760, Height = 1100 };
            w.Show();
            Dispatcher.UIThread.RunJobs();
            var texts = section.GetVisualDescendants().Concat(dialog.GetVisualDescendants()).OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains(Loc.T("Inbox"), texts);
            Assert.Contains("https://example.com/spec", texts);
            Assert.Contains(Loc.T("Waiting for you"), texts);
            Assert.Contains(Loc.T("Send to"), texts);
            var buttons = section.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible).Select(b => b.Content as string).ToList();
            Assert.Equal([Loc.T("Accept"), Loc.T("Decline")], buttons);
            var radios = dialog.GetVisualDescendants().OfType<RadioButton>().Select(r => r.Content as string).ToList();
            Assert.Equal([Loc.T("Everyone in the team"), Loc.T("Specific members")], radios);
            if (language == "ru")
            {
                Assert.DoesNotContain("Inbox", texts);
                Assert.DoesNotContain("Send to", texts);
                Assert.DoesNotContain("Everyone in the team", radios);
            }
            w.Close();
        }
        finally
        {
            Loc.Load(previous, Path.Combine(tmp, "settings.json"));
            await alice.Vm.DisposeAsync();
            await bob.Vm.DisposeAsync();
        }
    }
}
