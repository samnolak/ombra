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
        /// <summary>Holds taken under a running turn (Update now), ever.</summary>
        public int UnderTurn;
        /// <summary>What the running turn changed or opened (repository-relative).</summary>
        public List<string> AgentFiles = [];
        public string? Asked;
        public (string Top, string Target)? Shown;
        public bool InboxOpened;

        public GitBellHost Make() => new(() => Folders, () => Allowed, (_, underTurn) => Busy && !underTurn ? null : new Hold(this, underTurn && Busy),
            _ => AgentFiles, (_, text) => Asked = text, (top, target) => { Shown = (top, target); return Task.CompletedTask; }, () => InboxOpened = true);

        private sealed class Hold : IDisposable
        {
            private readonly Host _host;
            public Hold(Host host, bool underTurn) { _host = host; host.Holds++; if (underTurn) host.UnderTurn++; }
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
    public async Task An_update_pressed_while_the_agent_works_is_queued_can_be_cancelled_and_runs_when_the_turn_ends()
    {
        var r = Setup("running");
        BobPushes(r, "b.txt", "bob");
        var head = Git(r.Me, "rev-parse", "HEAD");
        var (bell, host) = Bell(r.Me);
        await bell.CheckAsync();
        host.Busy = true;
        var row = bell.Repos.Single();
        await row.UpdateCommand.ExecuteAsync(null);
        // Queued, and it says so: the button waits, a spinner, Cancel
        Assert.True(row.IsQueued);
        Assert.True(row.IsWaiting);
        Assert.Equal(Loc.T("Will update when the agent's turn ends."), row.Message);
        Assert.False(row.IsMessageError);
        Assert.False(row.UpdateCommand.CanExecute(null));
        Assert.True(row.CanUpdate);
        Assert.Equal(head, Git(r.Me, "rev-parse", "HEAD"));
        row.CancelQueuedCommand.Execute(null);
        Assert.False(row.IsQueued || row.IsWaiting || row.HasMessage);
        Assert.True(row.UpdateCommand.CanExecute(null));
        await row.UpdateCommand.ExecuteAsync(null);
        Assert.True(row.IsQueued);
        // Another chat's turn there ends while this one still works: still queued
        bell.OnTurnEnded(r.Me);
        await bell.Checked;
        Assert.True(row.IsQueued);
        Assert.Equal(head, Git(r.Me, "rev-parse", "HEAD"));
        host.Busy = false;
        bell.OnTurnEnded(r.Me);
        await bell.Checked;
        Assert.Equal(Git(r.Bob, "rev-parse", "HEAD"), Git(r.Me, "rev-parse", "HEAD"));
        Assert.False(row.IsQueued);
        Assert.Empty(bell.Repos);
        Assert.Equal(0, host.UnderTurn);
    }

    [Fact]
    public async Task Update_now_goes_under_the_running_turn_only_while_the_incoming_files_are_neither_the_turns_nor_uncommitted()
    {
        var r = Setup("now");
        BobPushes(r, "b.txt", "bob");
        File.AppendAllText(Path.Combine(r.Me, "a.txt"), "mine\n");
        var head = Git(r.Me, "rev-parse", "HEAD");
        var (bell, host) = Bell(r.Me);
        await bell.CheckAsync();
        var row = bell.Repos.Single();
        Assert.Equal(UpdateRisk.KeepChanges, row.Risk);
        host.Busy = true;

        // The turn opened the incoming file: no Update now, and the row says which file
        host.AgentFiles = ["b.txt"];
        await row.UpdateCommand.ExecuteAsync(null);
        Assert.True(row.IsQueued);
        Assert.False(row.CanUpdateNow);
        Assert.Equal(["b.txt"], row.AgentOverlap);
        Assert.Equal(Loc.F("Can't update now: the agent's turn changed or opened files the incoming commits change ({0}).", "b.txt"), row.NowText);
        await row.UpdateNowCommand.ExecuteAsync(null); // weighed again when it runs: still not
        Assert.True(row.IsQueued);
        Assert.Equal(Loc.T("Will update when the agent's turn ends."), row.Message);
        Assert.Equal(head, Git(r.Me, "rev-parse", "HEAD"));

        // The turn moves on to other files: weighed again when the list opens
        host.AgentFiles = ["a.txt", "docs/notes.md"];
        bell.IsOpen = true;
        Assert.True(row.CanUpdateNow);
        Assert.False(row.HasNowText);

        // Meanwhile the incoming file got an uncommitted change: read again, it is no fast-forward any more
        File.AppendAllText(Path.Combine(r.Me, "b.txt"), "agent\n");
        await row.UpdateNowCommand.ExecuteAsync(null);
        Assert.False(row.IsQueued);
        Assert.Equal(UpdateRisk.Overlap, row.Risk);
        Assert.Equal(head, Git(r.Me, "rev-parse", "HEAD"));
        Assert.Equal("b\nagent\n", File.ReadAllText(Path.Combine(r.Me, "b.txt")));

        // Without it: Update now updates under the running turn, the turn's own changes stay
        Git(r.Me, "checkout", "--", "b.txt");
        await bell.CheckAsync();
        await row.UpdateCommand.ExecuteAsync(null);
        Assert.True(row.CanUpdateNow);
        await row.UpdateNowCommand.ExecuteAsync(null);
        Assert.Equal(Git(r.Bob, "rev-parse", "HEAD"), Git(r.Me, "rev-parse", "HEAD"));
        Assert.Equal("a\nmine\n", File.ReadAllText(Path.Combine(r.Me, "a.txt")));
        Assert.True(host.UnderTurn > 0);
        Assert.Equal(0, host.Holds);
        Assert.Empty(bell.Repos);
    }

    /// <summary>
    /// The real chat: a turn waits for an approval in the repository. It read a file the incoming commits do not
    /// change: Update now; then one it does: the update waits, and runs when that turn ends.
    /// </summary>
    [AvaloniaFact]
    public async Task A_queued_update_runs_when_the_chats_turn_ends_and_update_now_weighs_what_the_turn_read()
    {
        var r = Setup("turn-end");
        BobPushes(r, "b.txt", "bob");
        File.AppendAllText(Path.Combine(r.Me, "a.txt"), "mine\n");
        var root = TestProcesses.TempDir("git-bell-turn-end-chat");
        var session = new SessionController(TestProcesses.FakeFactory("approval", Path.Combine(root, "sessions")), new LaunchRequest(r.Me));
        var vm = new MainViewModel(session, new AppArgs(), settings: new ClientSettingsStore(Path.Combine(root, "settings.json")))
        {
            Chats = new ChatFolders(Path.Combine(root, "chats")),
        };
        await session.StartAsync();
        vm.Apply(session.Snapshot());
        vm.StartGitBell();
        await vm.Bell.CheckNowCommand.ExecuteAsync(null);
        var row = vm.Bell.Repos.Single();

        async Task<SessionSnapshot> TurnAsync(string message)
        {
            vm.ComposerText = message;
            await vm.SendCommand.ExecuteAsync(null);
            var asking = await TestProcesses.Eventually(session.Snapshot, s => s.Dialogs.Count > 0, TimeSpan.FromSeconds(20), "asking");
            vm.Apply(asking);
            return asking;
        }

        async Task EndAsync(SessionSnapshot asking)
        {
            Assert.True(await session.AnswerDialogAsync(asking.Dialogs[0].Id, new DialogAnswer.Value("Approve")));
            vm.Apply(await TestProcesses.Eventually(session.Snapshot, s => s.Phase == SessionPhase.Ready && s.RunsEnded > asking.RunsEnded,
                TimeSpan.FromSeconds(20), "turn end"));
        }

        try
        {
            var asking = await TurnAsync("READ a.txt");
            Assert.Contains("a.txt", vm.AgentFilesIn(row.Top));
            await row.UpdateCommand.ExecuteAsync(null);
            Assert.True(row.IsQueued);
            Assert.True(row.CanUpdateNow);
            await row.UpdateNowCommand.ExecuteAsync(null);
            Assert.Equal(Git(r.Bob, "rev-parse", "HEAD"), Git(r.Me, "rev-parse", "HEAD"));
            Assert.Equal(SessionPhase.Running, session.Snapshot().Phase);
            await EndAsync(asking);

            BobPushes(r, "b.txt", "bob again");
            await vm.Bell.CheckNowCommand.ExecuteAsync(null);
            row = vm.Bell.Repos.Single();
            var head = Git(r.Me, "rev-parse", "HEAD");
            asking = await TurnAsync("READ b.txt:1-5");
            Assert.Contains("b.txt", vm.AgentFilesIn(row.Top));
            await row.UpdateCommand.ExecuteAsync(null);
            Assert.True(row.IsQueued);
            Assert.False(row.CanUpdateNow);
            Assert.Contains("b.txt", row.NowText);
            Assert.Equal(head, Git(r.Me, "rev-parse", "HEAD"));
            await EndAsync(asking);
            var bob = Git(r.Bob, "rev-parse", "HEAD");
            await TestProcesses.Eventually(() => Git(r.Me, "rev-parse", "HEAD"), h => h == bob, TimeSpan.FromSeconds(30), "updated when the turn ended");
            Assert.Equal("a\nmine\n", File.ReadAllText(Path.Combine(r.Me, "a.txt")));
            Assert.False(row.IsQueued);
        }
        finally { await session.DisposeAsync(); }
    }

    [Fact]
    public void A_tool_call_names_the_files_it_changed_or_was_pointed_at()
    {
        static ToolItem Tool(string name, string args, IReadOnlyList<FileChange>? changes = null) =>
            new(0, "t", name, args, ToolStatus.Running, null) { Changes = changes };
        Assert.Equal(["src/a.cs:50-80"], ConversationState.NamedFiles(Tool("read", """{"path":"src/a.cs:50-80"}""")));
        Assert.Equal(["a.txt", "b.txt"], ConversationState.NamedFiles(Tool("grep", """{"pattern":"x","path":"a.txt;b.txt"}""")));
        Assert.Equal(["/abs/c.txt"], ConversationState.NamedFiles(Tool("write", """{"file_path":"/abs/c.txt","content":"x"}""")));
        Assert.Equal(["src/x.cs", "src/y.cs"], ConversationState.NamedFiles(Tool("edit",
            """{"input":"[src/x.cs#1A2B]\nPUT 1.=1:\n+a\n[src/y.cs#3C4D]\nCUT 2.=3"}""")));
        Assert.Equal(["docs/z.md"], ConversationState.NamedFiles(Tool("apply_patch", """{"input":"*** Begin Patch\n*** Update File: docs/z.md\n@@\n-a\n+b"}""")));
        // A long call's arguments are cut: its changes name the files once it ends; no URL is a file
        Assert.Equal(["new.cs", "old.cs"], ConversationState.NamedFiles(Tool("edit", """{"input":"[new.cs#1A2B]\nPUT… [900 more chars]""",
            [new FileChange("new.cs", FileChangeKind.Moved, null, "old.cs")])));
        Assert.Empty(ConversationState.NamedFiles(Tool("read", """{"path":"skill://figma/SKILL.md"}""")));
    }

    // ───────────── Why the remote refused ─────────────

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void A_remote_refusing_access_says_what_to_do(string language)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("git-bell-access-" + language), "settings.json");
        var previous = Loc.Current;
        Loc.Load(language, settingsPath);
        try
        {
            // The owner's case: a private repository (or a renamed one) and an account without access, over https and ssh
            const string https = "remote: Repository not found.\nfatal: repository 'https://github.com/samnolak/example-business.git/' not found\n";
            var github = Loc.F("GitHub says {0} was not found. A private repository looks like that to an account without access to it: sign in with one that has it (gh auth login in Terminal), or check the address (git remote -v) — the repository may have been renamed or deleted.", "samnolak/example-business");
            Assert.Equal(github, GitUpstream.AccessProblem(https, "https://github.com/samnolak/example-business.git"));
            Assert.Equal(github, GitUpstream.AccessProblem("ERROR: Repository not found.\nfatal: Could not read from remote repository.\n", "git@github.com:samnolak/example-business.git"));
            Assert.Contains("gh auth login", github);
            Assert.Contains("git remote -v", github);
            var token = GitUpstream.AccessProblem(https, "https://me:s3cret@github.com/samnolak/example-business.git");
            Assert.Equal(github, token);
            Assert.Equal(Loc.F("{0} says {1} was not found: it may be private and the account git signs in with has no access to it, or it was renamed or deleted. Check the address (git remote -v) and the account.", "gitlab.example.com", "team/app"),
                GitUpstream.AccessProblem("remote: The project you were looking for could not be found or you don't have permission to view it.\nfatal: repository 'https://gitlab.example.com/team/app.git/' not found\n", "https://gitlab.example.com/team/app.git"));
            Assert.Equal(Loc.T("GitHub asked for a sign-in and git has no saved credentials for it. Sign in in Terminal: gh auth login, then gh auth setup-git."),
                GitUpstream.AccessProblem("fatal: could not read Username for 'https://github.com': terminal prompts disabled\n", "https://github.com/o/r.git"));
            Assert.Equal(Loc.F("{0} asked for a sign-in and git has no saved credentials for it. Sign in once in Terminal: a git fetch there saves them with your credential helper.", "git.example.org"),
                GitUpstream.AccessProblem("fatal: Authentication failed for 'https://git.example.org/o/r.git/'\n", "https://git.example.org/o/r.git"));
            Assert.Equal(Loc.F("{0} refused your SSH key: check that it is loaded (ssh-add -l in Terminal) and added to your account there.", "example.org"),
                GitUpstream.AccessProblem("git@example.org: Permission denied (publickey).\nfatal: Could not read from remote repository.\n", "ssh://git@example.org/team/app.git"));
            Assert.Equal(Loc.F("{0}'s SSH host key is not known yet: connect once in Terminal ({1}) to accept it.", "example.org", "ssh -T git@example.org"),
                GitUpstream.AccessProblem("Host key verification failed.\nfatal: Could not read from remote repository.\n", "git@example.org:team/app.git"));
            // Anything else (and a local remote) keeps git's own words
            Assert.Null(GitUpstream.AccessProblem("fatal: unable to access 'https://example.org/o/r.git/': Could not resolve host: example.org\n", "https://example.org/o/r.git"));
            Assert.Null(GitUpstream.AccessProblem("fatal: repository '/tmp/x' not found\n", "/tmp/x"));
            if (language == "ru")
            {
                Assert.DoesNotContain("was not found", github);
                Assert.NotEqual("Will update when the agent's turn ends.", Loc.T("Will update when the agent's turn ends."));
            }
        }
        finally { Loc.Load(previous, settingsPath); }
    }

    /// <summary>A remote over http that answers 404 (as GitHub does for a private repository without access): the card says what to do.</summary>
    [Fact]
    public async Task A_remote_that_says_not_found_shows_what_to_do_in_the_card()
    {
        var r = Setup("not-found");
        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        var port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await server.AcceptTcpClientAsync(stop.Token);
                var stream = client.GetStream();
                // The request's headers (git sends no body with its GET), then the answer
                var request = new List<byte>();
                var buffer = new byte[4096];
                while (!System.Text.Encoding.ASCII.GetString([.. request]).Contains("\r\n\r\n", StringComparison.Ordinal)
                       && await stream.ReadAsync(buffer, stop.Token) is var n && n > 0)
                    request.AddRange(buffer[..n]);
                await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), stop.Token);
            }
        });
        Git(r.Me, "remote", "set-url", "origin", $"http://127.0.0.1:{port}/team/app.git");
        var (bell, _) = Bell(r.Me);
        await bell.CheckAsync();
        var row = bell.Repos.Single();
        Assert.Equal(Loc.T("Couldn't check the remote: ") + Loc.F("{0} says {1} was not found: it may be private and the account git signs in with has no access to it, or it was renamed or deleted. Check the address (git remote -v) and the account.", "127.0.0.1", "team/app"),
            row.Error);
        stop.Cancel();
        server.Stop();
        await Assert.ThrowsAnyAsync<Exception>(() => serving);
    }

    [Fact]
    public void Git_runs_with_the_apps_environment_plus_homebrew_on_macos()
    {
        var env = GitUpstream.Environment();
        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
        // ssh reaches the user's agent: nothing removes SSH_AUTH_SOCK
        Assert.False(env.ContainsKey("SSH_AUTH_SOCK"));
        if (!OperatingSystem.IsMacOS())
        {
            Assert.False(env.ContainsKey("PATH"));
            return;
        }
        var dirs = env["PATH"]!.Split(':');
        Assert.Contains("/opt/homebrew/bin", dirs);
        Assert.Contains("/usr/local/bin", dirs);
        var inherited = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(inherited, dirs.Take(inherited.Length));
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

    /// <summary>
    /// Update pressed while the agent works: the card says it waits for the turn, its button is disabled by a spinner,
    /// Cancel; Update now where the turn touched none of the incoming files, the reason where it did.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task A_queued_update_shows_that_it_waits_for_the_turn_with_cancel_and_update_now(string language)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("git-bell-queued-" + language), "settings.json");
        var previous = Loc.Current;
        Loc.Load(language, settingsPath);
        try
        {
            var keep = Setup("ui-queued-keep-" + language);
            BobPushes(keep, "b.txt", "bob");
            File.AppendAllText(Path.Combine(keep.Me, "a.txt"), "mine\n");
            var clean = Setup("ui-queued-clean-" + language);
            BobPushes(clean, "b.txt", "bob", 2);
            var (bell, host) = Bell(keep.Me, clean.Me);
            await bell.CheckAsync();
            host.Busy = true;
            await bell.Repos[0].UpdateCommand.ExecuteAsync(null);
            host.AgentFiles = ["b.txt"];
            await bell.Repos[1].UpdateCommand.ExecuteAsync(null);
            Assert.True(bell.Repos.All(r => r.IsQueued));

            var list = new GitBellList { DataContext = bell };
            var w = new Window { Content = list, Width = 420, Height = 900 };
            w.Show();
            Settle(w);
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Equal(2, texts.Count(t => t == Loc.T("Will update when the agent's turn ends.")));
            Assert.Contains(Loc.F("Can't update now: the agent's turn changed or opened files the incoming commits change ({0}).", "b.txt"), texts);
            var buttons = w.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            Assert.Single(buttons, b => b.Content as string == Loc.T("Update now"));
            Assert.Equal(2, buttons.Count(b => b.Content as string == Loc.T("Cancel")));
            Assert.All(buttons.Where(b => b.Name is "BellUpdate" or "BellUpdateKeep"), b => Assert.False(b.IsEffectivelyEnabled));
            Assert.Equal(2, w.GetVisualDescendants().OfType<Avalonia.Controls.Control>().Count(c => c.Name == "BellWaiting" && c.IsEffectivelyVisible));
            if (language == "ru")
            {
                Assert.NotEqual("Will update when the agent's turn ends.", Loc.T("Will update when the agent's turn ends."));
                Assert.DoesNotContain("Can't update now", bell.Repos[1].NowText);
            }
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                w.RequestedThemeVariant = theme;
                Shot(w, $"git-bell-queued-{language}-{theme.Key.ToString()!.ToLowerInvariant()}");
            }

            // Cancel: the button is back, nothing waits
            buttons.First(b => b.Content as string == Loc.T("Cancel")).Command!.Execute(null);
            Settle(w);
            Assert.False(bell.Repos[0].IsQueued);
            Assert.True(w.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BellUpdateKeep" && b.IsEffectivelyVisible).IsEffectivelyEnabled);
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
