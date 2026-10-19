using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// The header's bell against real git: a bare remote and two clones (mine and Bob's). Bob pushes; the bell's check
/// (git fetch) sees how far my branch is behind, by whom and which files; the update is a fast-forward when I have
/// nothing local, stash → fast-forward → pop with changes in other files, and refused (overlapping files, diverged
/// history, a running agent turn) otherwise. No fetch while the watch is not allowed.
/// </summary>
public sealed class GitBellTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") is { } d
        ? d : Path.Combine(Path.GetTempPath(), "ombra-shots");

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "user.name=" + (dir.EndsWith("bob", StringComparison.Ordinal) ? "Bob" : "Me"), "-c", "user.email=t@example.com",
                     "-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main", .. args])
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
        return output.Trim();
    }

    private sealed record Repos(string Remote, string Me, string Bob);

    /// <summary>A bare remote with a.txt and b.txt on main, cloned as mine and Bob's.</summary>
    private static Repos Setup(string name)
    {
        var root = TestProcesses.TempDir("git-bell-" + name);
        var remote = Path.Combine(root, "remote.git");
        Directory.CreateDirectory(remote);
        Git(remote, "init", "--bare", "--quiet", "--initial-branch=main");
        var seed = Path.Combine(root, "seed");
        Directory.CreateDirectory(seed);
        Git(seed, "init", "--quiet", "--initial-branch=main");
        File.WriteAllText(Path.Combine(seed, "a.txt"), "a\n");
        File.WriteAllText(Path.Combine(seed, "b.txt"), "b\n");
        Git(seed, "add", ".");
        Git(seed, "commit", "--quiet", "-m", "seed");
        Git(seed, "push", "--quiet", remote, "main");
        Git(root, "clone", "--quiet", remote, "me");
        Git(root, "clone", "--quiet", remote, "bob");
        return new Repos(remote, Path.Combine(root, "me"), Path.Combine(root, "bob"));
    }

    private static void BobPushes(Repos r, string file, string text, int commits = 1)
    {
        for (var i = 0; i < commits; i++)
        {
            File.AppendAllText(Path.Combine(r.Bob, file), text + i + "\n");
            Git(r.Bob, "commit", "--quiet", "-am", "bob " + i);
        }
        Git(r.Bob, "push", "--quiet", "origin", "HEAD");
    }

    private sealed class Host
    {
        public List<WatchedFolder> Folders = [];
        public bool Allowed = true;
        public bool Busy;
        public int Holds;
        public string? Asked;
        public (string Top, string Target)? Shown;
        public bool InboxOpened;

        public GitBellHost Make() => new(() => Folders, () => Allowed, _ => Busy ? null : new Hold(this), (_, text) => Asked = text,
            (top, target) => { Shown = (top, target); return Task.CompletedTask; }, () => InboxOpened = true);

        private sealed class Hold : IDisposable
        {
            private readonly Host _host;
            public Hold(Host host) { _host = host; host.Holds++; }
            public void Dispose() => _host.Holds--;
        }
    }

    private static (GitBellViewModel Bell, Host Host) Bell(params string[] folders)
    {
        var host = new Host { Folders = [.. folders.Select(f => new WatchedFolder(f, true))] };
        return (new GitBellViewModel(host.Make(), new ObservableCollection<TeamInboxRow>()), host);
    }

    [Fact]
    public async Task A_teammates_push_shows_count_authors_and_files()
    {
        var r = Setup("ahead");
        BobPushes(r, "b.txt", "bob", commits: 3);
        var (bell, _) = Bell(r.Me);
        await bell.CheckAsync();
        var row = Assert.Single(bell.Repos);
        Assert.Equal(3, row.Behind);
        Assert.Equal(["Bob"], row.State!.Incoming!.Authors);
        Assert.Equal(["b.txt"], row.State.Incoming.Files);
        Assert.Equal(UpdateRisk.Clean, row.Risk);
        Assert.Contains("me · main: " + Loc.Plural(3, "{0} new commit from {1}", "{0} new commits from {1}", "Bob"), row.Title);
        Assert.Contains("b.txt", row.FilesText);
        Assert.Equal(1, bell.Count);
        Assert.True(row.CanUpdate);
        Assert.False(row.CanAskAgent);
    }

    [Fact]
    public async Task Update_fast_forwards_a_clean_branch()
    {
        var r = Setup("ff");
        BobPushes(r, "b.txt", "bob", commits: 2);
        var (bell, _) = Bell(r.Me);
        await bell.CheckAsync();
        await bell.Repos[0].UpdateCommand.ExecuteAsync(null);
        Assert.Equal(Git(r.Bob, "rev-parse", "HEAD"), Git(r.Me, "rev-parse", "HEAD"));
        Assert.Empty(bell.Repos);
        Assert.False(bell.HasBadge);
    }

    [Fact]
    public async Task Update_keeps_uncommitted_changes_in_other_files()
    {
        var r = Setup("keep");
        BobPushes(r, "b.txt", "bob");
        File.AppendAllText(Path.Combine(r.Me, "a.txt"), "mine\n");
        Git(r.Me, "add", "a.txt");
        File.AppendAllText(Path.Combine(r.Me, "a.txt"), "and more\n");
        File.WriteAllText(Path.Combine(r.Me, "new.txt"), "untracked\n");
        var (bell, _) = Bell(r.Me);
        await bell.CheckAsync();
        var row = bell.Repos.Single();
        Assert.Equal(UpdateRisk.KeepChanges, row.Risk);
        Assert.True(row.CanUpdateKeepingChanges);
        await row.UpdateCommand.ExecuteAsync(null);
        Assert.Equal(Git(r.Bob, "rev-parse", "HEAD"), Git(r.Me, "rev-parse", "HEAD"));
        Assert.Equal("a\nmine\nand more\n", File.ReadAllText(Path.Combine(r.Me, "a.txt")));
        Assert.Contains("+mine", Git(r.Me, "diff", "--cached")); // the staged part stays staged
        Assert.Equal("untracked\n", File.ReadAllText(Path.Combine(r.Me, "new.txt")));
        Assert.Contains("bob0", File.ReadAllText(Path.Combine(r.Me, "b.txt")));
        Assert.Equal("", Git(r.Me, "stash", "list"));
    }

    [Fact]
    public async Task A_change_made_after_the_check_to_an_incoming_file_is_never_overwritten()
    {
        var r = Setup("late-edit");
        BobPushes(r, "b.txt", "bob");
        var head = Git(r.Me, "rev-parse", "HEAD");
        var (bell, _) = Bell(r.Me);
        await bell.CheckAsync();
        var row = bell.Repos.Single();
        Assert.Equal(UpdateRisk.Clean, row.Risk);
        // Between the check and the click the file the incoming commit changes is edited (an editor, a terminal)
        File.AppendAllText(Path.Combine(r.Me, "b.txt"), "late\n");
        File.WriteAllText(Path.Combine(r.Me, "z.txt"), "late untracked\n");
        await row.UpdateCommand.ExecuteAsync(null);
        Assert.Equal(head, Git(r.Me, "rev-parse", "HEAD"));
        Assert.Equal("b\nlate\n", File.ReadAllText(Path.Combine(r.Me, "b.txt")));
        Assert.Equal("late untracked\n", File.ReadAllText(Path.Combine(r.Me, "z.txt")));
        Assert.Equal(UpdateRisk.Overlap, row.Risk);
        Assert.Equal("", Git(r.Me, "stash", "list"));
    }

    [Fact]
    public async Task Overlapping_files_are_not_updated_and_the_agent_request_names_them()
    {
        var r = Setup("overlap");
        BobPushes(r, "b.txt", "bob");
        File.AppendAllText(Path.Combine(r.Me, "b.txt"), "mine\n");
        var head = Git(r.Me, "rev-parse", "HEAD");
        var (bell, host) = Bell(r.Me);
        await bell.CheckAsync();
        var row = bell.Repos.Single();
        Assert.Equal(UpdateRisk.Overlap, row.Risk);
        Assert.Equal(["b.txt"], row.State!.Overlap);
        Assert.Contains("b.txt", row.OverlapText);
        Assert.False(row.CanUpdate || row.CanUpdateKeepingChanges);
        Assert.NotNull(await GitUpstream.UpdateAsync(row.Top, CancellationToken.None));
        Assert.Equal(head, Git(r.Me, "rev-parse", "HEAD"));
        Assert.Equal("b\nmine\n", File.ReadAllText(Path.Combine(r.Me, "b.txt")));
        row.AskAgentCommand.Execute(null);
        Assert.Contains(Loc.F("Update this repository's branch {0} from {1} carefully: run git fetch, then rebase or merge onto {1}, keeping my uncommitted and unpushed changes. Resolve any conflicts so that both my changes and the incoming ones are preserved; do not discard either side.{2} Then run the tests and tell me what changed.", "main", "origin/main", " " + Loc.F("Files changed on both sides: {0}.", "b.txt")), host.Asked);
        Assert.Contains("b.txt", host.Asked);
        await row.ShowIncomingCommand.ExecuteAsync(null);
        Assert.Equal("origin/main", host.Shown?.Target);
        Assert.Contains("bob0", await GitUpstream.IncomingPatchAsync(row.Top, "origin/main", CancellationToken.None));
    }

    [Fact]
    public async Task Unpushed_commits_with_new_upstream_commits_are_diverged_and_not_updated()
    {
        var r = Setup("diverged");
        BobPushes(r, "b.txt", "bob");
        File.WriteAllText(Path.Combine(r.Me, "c.txt"), "mine\n");
        Git(r.Me, "add", "c.txt");
        Git(r.Me, "commit", "--quiet", "-m", "mine");
        var head = Git(r.Me, "rev-parse", "HEAD");
        var (bell, _) = Bell(r.Me);
        await bell.CheckAsync();
        var row = bell.Repos.Single();
        Assert.Equal(UpdateRisk.Diverged, row.Risk);
        Assert.True(row.CanAskAgent);
        Assert.NotNull(await GitUpstream.UpdateAsync(row.Top, CancellationToken.None));
        Assert.Equal(head, Git(r.Me, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task No_update_while_the_agent_works_in_the_repository_then_it_runs_when_the_turn_ends()
    {
        var r = Setup("running");
        BobPushes(r, "b.txt", "bob");
        var head = Git(r.Me, "rev-parse", "HEAD");
        var (bell, host) = Bell(r.Me);
        await bell.CheckAsync();
        host.Busy = true;
        var row = bell.Repos.Single();
        await row.UpdateCommand.ExecuteAsync(null);
        Assert.True(row.IsQueued);
        Assert.Equal(head, Git(r.Me, "rev-parse", "HEAD"));
        host.Busy = false;
        bell.OnTurnEnded(r.Me);
        await bell.Checked;
        Assert.Equal(Git(r.Bob, "rev-parse", "HEAD"), Git(r.Me, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Nothing_is_fetched_while_the_watch_is_not_allowed()
    {
        var r = Setup("privacy");
        var before = Git(r.Me, "rev-parse", "origin/main");
        BobPushes(r, "b.txt", "bob");
        var (bell, host) = Bell(r.Me);
        host.Allowed = false;
        await bell.CheckAsync();
        Assert.Empty(bell.Repos);
        Assert.Equal(before, Git(r.Me, "rev-parse", "origin/main"));
        host.Allowed = true;
        await bell.CheckAsync();
        Assert.NotEqual(before, Git(r.Me, "rev-parse", "origin/main"));
    }

    [Fact]
    public void Strict_network_privacy_or_the_setting_turns_the_watch_off()
    {
        var store = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("git-bell-settings"), "ombra.local.json"));
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(), null, store);
        Assert.True(vm.GitWatchAllowed());
        store.Update(o => o with { StrictNetworkPrivacy = true });
        Assert.False(vm.GitWatchAllowed());
        store.Update(o => o with { StrictNetworkPrivacy = null });
        vm.WatchGitRemotes = false;
        Assert.False(vm.GitWatchAllowed());
    }

    [Fact]
    public async Task New_commits_on_the_default_branch_show_while_on_a_feature_branch()
    {
        var r = Setup("feature");
        Git(r.Me, "switch", "--quiet", "-c", "feature");
        Git(r.Me, "push", "--quiet", "-u", "origin", "feature");
        BobPushes(r, "b.txt", "bob", commits: 2);
        var (bell, _) = Bell(r.Me);
        await bell.CheckAsync();
        var row = bell.Repos.Single();
        Assert.Equal(0, row.Behind);
        Assert.Equal("origin/main", row.State!.DefaultBranch!.Ref);
        Assert.Equal(2, row.State.DefaultBranch.Count);
        Assert.Contains(Loc.Plural(2, "{1}: {0} new commit from {2}, not in your branch", "{1}: {0} new commits from {2}, not in your branch", "origin/main", "Bob"), row.DefaultText);
        Assert.Equal(1, bell.Count);
    }

    // ───────────── An untrusted folder ─────────────

    [Fact]
    public async Task An_untrusted_folder_is_not_fetched_in_the_background_only_when_asked()
    {
        var r = Setup("untrusted");
        var before = Git(r.Me, "rev-parse", "origin/main");
        BobPushes(r, "b.txt", "bob");
        var host = new Host { Folders = [new WatchedFolder(r.Me, AutoFetch: false)] };
        var bell = new GitBellViewModel(host.Make(), []);
        await bell.CheckAsync();
        Assert.Equal(before, Git(r.Me, "rev-parse", "origin/main"));
        Assert.Empty(bell.Repos);
        await bell.CheckNowCommand.ExecuteAsync(null);
        Assert.NotEqual(before, Git(r.Me, "rev-parse", "origin/main"));
        Assert.Equal(1, bell.Repos.Single().Behind);
    }

    /// <summary>Runs git as a user's terminal would, without the bell's protection (the control of the tests below).</summary>
    private static int PlainGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    [Fact]
    public async Task A_repository_config_that_runs_a_command_on_fetch_runs_nothing()
    {
        var r = Setup("evil-uploadpack");
        BobPushes(r, "b.txt", "bob");
        // Through an included file, as a hostile folder would hide it
        var evil = Path.Combine(r.Me, "..", "evil.cfg");
        var marker = Path.Combine(r.Me, "..", "pwned");
        Git(r.Me, "config", "-f", evil, "remote.origin.uploadpack", $"touch '{marker}'; git-upload-pack");
        Git(r.Me, "config", "include.path", evil);
        // Control: plain git runs it
        var control = Path.Combine(r.Me, "..", "control");
        Git(Path.Combine(r.Me, ".."), "clone", "--quiet", "--no-local", r.Remote, "control");
        Git(control, "config", "include.path", evil);
        PlainGit(control, "fetch", "--quiet", "origin");
        Assert.True(File.Exists(marker));
        File.Delete(marker);

        Assert.Null(await GitUpstream.FetchAsync(r.Me, CancellationToken.None));
        Assert.False(File.Exists(marker));
        Assert.Equal(Git(r.Bob, "rev-parse", "HEAD"), Git(r.Me, "rev-parse", "origin/main"));
    }

    [Fact]
    public async Task A_repository_ssh_command_and_credential_helper_are_replaced_by_the_users_own()
    {
        var r = Setup("evil-ssh");
        var marker = Path.Combine(r.Me, "..", "pwned-ssh");
        Git(r.Me, "config", "core.sshCommand", $"touch '{marker}'; false");
        Git(r.Me, "config", "credential.helper", $"!touch '{marker}'");
        Git(r.Me, "config", "credential.https://example.invalid.helper", $"!touch '{marker}'");
        Git(r.Me, "remote", "set-url", "origin", "ssh://git@127.0.0.1:1/repo.git");
        PlainGit(r.Me, "fetch", "--quiet", "origin");
        Assert.True(File.Exists(marker));
        File.Delete(marker);

        Assert.NotNull(await GitUpstream.FetchAsync(r.Me, CancellationToken.None)); // the real ssh: nothing listens there
        Assert.False(File.Exists(marker));
        var guard = string.Join(" ", await GitUpstream.GuardAsync(r.Me, CancellationToken.None));
        Assert.Contains("core.sshcommand=ssh", guard);
        Assert.Contains("credential.helper=", guard);
        Assert.Contains("credential.https://example.invalid.helper=", guard);
        Assert.DoesNotContain(marker, guard);
    }

    [Fact]
    public async Task A_repository_filter_driver_does_not_run_while_updating()
    {
        var r = Setup("evil-filter");
        File.WriteAllText(Path.Combine(r.Bob, ".gitattributes"), "*.txt filter=evil\n");
        Git(r.Bob, "add", ".gitattributes");
        BobPushes(r, "b.txt", "bob");
        var marker = Path.Combine(r.Me, "..", "pwned-filter");
        Git(r.Me, "config", "filter.evil.smudge", $"sh -c \"touch '{marker}'; cat\"");
        Git(r.Me, "config", "filter.evil.clean", $"sh -c \"touch '{marker}'; cat\"");
        Git(r.Me, "config", "filter.evil.required", "true");
        var (bell, _) = Bell(r.Me);
        await bell.CheckAsync();
        await bell.Repos.Single().UpdateCommand.ExecuteAsync(null);
        Assert.Equal(Git(r.Bob, "rev-parse", "HEAD"), Git(r.Me, "rev-parse", "HEAD"));
        Assert.Contains("bob0", File.ReadAllText(Path.Combine(r.Me, "b.txt")));
        Assert.False(File.Exists(marker));
    }

    // ───────────── No turn while git writes ─────────────

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task While_an_update_holds_the_repository_no_turn_starts_there_and_none_running_lets_it_start(string language)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("git-bell-hold-" + language), "settings.json");
        var previous = Loc.Current;
        Loc.Load(language, settingsPath);
        try
        {
            var r = Setup("hold-" + language);
            var root = TestProcesses.TempDir("git-bell-hold-chat");
            // "approval": a turn waits for an approval, so it is still running when the update asks
            var session = new SessionController(TestProcesses.FakeFactory("approval", Path.Combine(root, "sessions")), new LaunchRequest(r.Me));
            var vm = new MainViewModel(session, new AppArgs(), settings: new ClientSettingsStore(settingsPath)) { Chats = new ChatFolders(Path.Combine(root, "chats")) };
            await session.StartAsync();
            vm.Apply(session.Snapshot());

            var hold = vm.BeginGitUpdate(r.Me);
            Assert.NotNull(hold);
            Assert.Null(vm.BeginGitUpdate(r.Me)); // one update at a time
            vm.ComposerText = "fix it";
            Assert.False(vm.SendCommand.CanExecute(null));
            Assert.Equal(Loc.T("Updating this repository from its remote: Send is available again when it finishes."), vm.UpdateFirstHint);
            // Paths that do not go through Send are refused too, with the reason
            var command = await vm.RunOmpCommandAsync("/usage");
            Assert.False(command.Ok);
            Assert.Equal(Loc.T("Updating from the remote"), vm.SessionCard?.Title);
            Assert.Equal("fix it", vm.ComposerText);
            if (language == "ru") Assert.NotEqual("Updating from the remote", Loc.T("Updating from the remote"));
            hold!.Dispose();
            Assert.True(vm.SendCommand.CanExecute(null));
            Assert.Null(vm.UpdateFirstHint);

            // A turn running there: no update begins
            await vm.SendCommand.ExecuteAsync(null);
            vm.Apply(await TestProcesses.Eventually(session.Snapshot, s => s.Phase == SessionPhase.Running, TimeSpan.FromSeconds(20), "running"));
            Assert.Null(vm.BeginGitUpdate(r.Me));
            await session.DisposeAsync();
        }
        finally { Loc.Load(previous, settingsPath); }
    }

    // ───────────── The list, rendered ─────────────

    /// <summary>Four repositories, one per risk: clean, local changes elsewhere, overlap, diverged.</summary>
    private static async Task<GitBellViewModel> FourStatesAsync()
    {
        var clean = Setup("ui-clean");
        BobPushes(clean, "b.txt", "bob", 2);
        var keep = Setup("ui-keep");
        BobPushes(keep, "b.txt", "bob");
        File.AppendAllText(Path.Combine(keep.Me, "a.txt"), "mine\n");
        var overlap = Setup("ui-overlap");
        BobPushes(overlap, "b.txt", "bob");
        File.AppendAllText(Path.Combine(overlap.Me, "b.txt"), "mine\n");
        var diverged = Setup("ui-diverged");
        BobPushes(diverged, "b.txt", "bob");
        File.WriteAllText(Path.Combine(diverged.Me, "c.txt"), "c\n");
        Git(diverged.Me, "add", ".");
        Git(diverged.Me, "commit", "--quiet", "-m", "mine");
        var (bell, _) = Bell(clean.Me, keep.Me, overlap.Me, diverged.Me);
        await bell.CheckAsync();
        return bell;
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task The_bell_list_shows_each_risk_with_its_action(string language)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("git-bell-view-" + language), "settings.json");
        var previous = Loc.Current;
        Loc.Load(language, settingsPath);
        try
        {
            var bell = await FourStatesAsync();
            Assert.Equal([UpdateRisk.Clean, UpdateRisk.KeepChanges, UpdateRisk.Overlap, UpdateRisk.Diverged], bell.Repos.Select(r => r.Risk));
            var list = new GitBellList { DataContext = bell };
            var w = new Window { Content = list, Width = 420, Height = 900 };
            w.Show();
            Settle(w);
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains(Loc.T("Notifications"), texts);
            Assert.Contains(Loc.T("Not updated automatically: the incoming commits change files you changed too."), texts);
            Assert.Contains(Loc.T("Not updated automatically: you have unpushed commits, the histories diverged."), texts);
            var buttons = w.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
            Assert.Contains(Loc.T("Update"), buttons);
            Assert.Contains(Loc.T("Update (keep my changes)"), buttons);
            Assert.Equal(2, buttons.Count(b => b == Loc.T("Ask the agent to update")));
            Assert.Equal(4, buttons.Count(b => b == Loc.T("Show incoming changes")));
            if (language == "ru")
            {
                Assert.NotEqual("Update (keep my changes)", Loc.T("Update (keep my changes)"));
                Assert.NotEqual("Ask the agent to update", Loc.T("Ask the agent to update"));
                Assert.DoesNotContain("new commits", bell.Repos[0].Title);
            }
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                w.RequestedThemeVariant = theme;
                Shot(w, $"git-bell-{language}-{theme.Key.ToString()!.ToLowerInvariant()}");
            }
        }
        finally { Loc.Load(previous, settingsPath); }
    }

    [AvaloniaFact]
    public async Task The_header_bell_badge_counts_and_the_composer_hint_shows_while_typing()
    {
        var r = Setup("header");
        BobPushes(r, "b.txt", "bob", 2);
        var vm = new MainViewModel(new SessionController(_ => TestProcesses.Fake("normal"), new LaunchRequest(r.Me)), new AppArgs(), null,
            new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("git-bell-header"), "ombra.local.json")));
        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
        w.Show();
        Settle(w);
        var folder = vm.Session.CurrentLaunch.WorkingDirectory;
        Assert.Equal(r.Me, folder);
        vm.Bell.Check();
        await vm.Bell.Checked;
        Settle(w);
        var bell = w.GetVisualDescendants().OfType<GitBellButton>().Single();
        Assert.True(bell.IsEffectivelyVisible);
        Assert.True(vm.Bell.HasBadge);
        Assert.Equal("1", vm.Bell.BadgeText);
        Assert.Null(vm.UpdateFirstHint);
        vm.ComposerText = "fix the build";
        Assert.Contains("2", vm.UpdateFirstHint);
        Assert.Contains("origin/main", vm.UpdateFirstHint);
        vm.DismissUpdateFirstHintCommand.Execute(null);
        Assert.Null(vm.UpdateFirstHint);
    }

    private static void Settle(Window w)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }
    }

    private static void Shot(Window w, string name)
    {
        Settle(w);
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
