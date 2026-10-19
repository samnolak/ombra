using Avalonia.Headless.XUnit;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// Computer use, Git and worktrees, SSH hosts and the branch chip: what each control sends to omp (builtins over RPC,
/// its CLI) and to git / gh / ssh, and what the page shows back.
/// </summary>
public sealed class WorkspaceSettingsTests
{
    private static Task Until(Func<bool> c, string what, int seconds = 20) => WorkspaceFixture.Until(c, what, seconds);

    // ── Computer use ──

    [AvaloniaFact]
    public async Task Computer_use_switch_saves_the_setting_and_turns_it_on_in_the_session()
    {
        var f = new WorkspaceFixture();
        f.Commands["/computer on"] = "Computer use enabled for this session. Computer use: enabled · prelude: active · configured: display=all, maxWidth=3840, maxHeight=2400";
        f.Cli["config set computer.enabled true --json"] = new { stdout = """{"key":"computer.enabled","value":true}""" };
        var vm = await f.StartAsync(TestProcesses.TempDir("ws-project"));
        var page = vm.ComputerSettings;
        await page.LoadAsync();
        Assert.False(page.Enabled);
        Assert.True(page.CanToggle);
        Assert.Equal(Loc.T("Off in this session"), page.SessionLabel);
        Assert.Equal(CheckState.Ok, page.Requirements[0].State); // eval is active ("* eval")

        page.Enabled = true;
        await Until(() => page.SessionOn, "on in the session"); // from what /computer on printed
        Assert.Equal(Loc.T("On in this session"), page.SessionLabel);
        Assert.Contains("config set computer.enabled true --json", f.CliCalls());
        Assert.Equal(["/computer status", "/tools", "/computer on"], f.SentCommands());
        Assert.False(page.MessageIsError);
        Assert.Contains(Loc.T("Computer use is on, saved for the next sessions too."), page.Message);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Computer_use_without_eval_says_why_and_offers_the_fix()
    {
        var f = new WorkspaceFixture();
        f.Commands["/tools"] = "* read\n- eval";
        f.Commands["/computer on"] = "Computer use is unavailable in this session.";
        f.SetConfig(new() { ["eval.js"] = false, ["eval.py"] = true });
        f.Cli["config set computer.enabled true --json"] = new { stdout = """{"key":"computer.enabled","value":true}""" };
        f.Cli["config set eval.js true --json"] = new { stdout = """{"key":"eval.js","value":true}""" };
        var vm = await f.StartAsync(TestProcesses.TempDir("ws-project"));
        var page = vm.ComputerSettings;
        await page.LoadAsync();
        var eval = page.Requirements[0];
        Assert.Equal(CheckState.Failed, eval.State);
        Assert.Equal(Loc.T("Turn on JavaScript eval"), eval.ActionLabel);

        page.Enabled = true;
        await Until(() => page.HasMessage, "a message");
        Assert.True(page.MessageIsError);
        Assert.Contains(Loc.T("Saved, but omp can't use the computer in this session: it runs through omp's eval tool, which is off here. "), page.Message);

        await page.RunCheckActionCommand.ExecuteAsync(eval);
        Assert.Contains("config set eval.js true --json", f.CliCalls());
        Assert.True(page.NeedsRestart);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Computer_actions_permission_keeps_other_tools_policies()
    {
        var f = new WorkspaceFixture();
        f.SetConfig(new() { ["tools.approval"] = new Dictionary<string, object> { ["bash"] = "allow", ["computer"] = "deny" } });
        f.Cli["config set tools.approval"] = new { stdout = """{"key":"tools.approval","value":{}}""" };
        var vm = await f.StartAsync(TestProcesses.TempDir("ws-project"));
        var page = vm.ComputerSettings;
        await page.LoadAsync();
        Assert.Equal("deny", page.Policy);
        Assert.Contains(Loc.T("omp can't use the computer at all, even with computer use on."), page.PolicyDescription);

        await page.SetPolicyCommand.ExecuteAsync("prompt");
        Assert.Equal("prompt", page.Policy);
        Assert.Contains("""config set tools.approval {"bash":"allow","computer":"prompt"} --json""", f.CliCalls());
        Assert.True(page.NeedsRestart);

        await page.SetPolicyCommand.ExecuteAsync("default");
        Assert.Contains("""config set tools.approval {"bash":"allow"} --json""", f.CliCalls());
        Assert.Contains(page.PolicyDescription, new[]
        {
            Loc.T("Follows the permission mode. Now “Bypass permissions”: omp clicks and types without asking."),
            Loc.T("Follows the permission mode. Now “Ask permissions”: screenshots and reading run, clicks and typing ask first."),
            Loc.T("Follows the permission mode. Now “Accept edits”: screenshots and reading run, clicks and typing ask first."),
            Loc.T("Follows the permission mode by the message box: screenshots and reading run; clicks and typing ask first, unless it is “Bypass permissions”."),
        });
        // Nothing of this goes over RPC: omp reads tools.approval when it starts
        Assert.DoesNotContain(f.SentCommands(), c => c.Contains("approval", StringComparison.Ordinal));
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Screenshot_size_is_checked_then_saved_key_by_key()
    {
        var f = new WorkspaceFixture();
        f.Cli["config set computer.maxWidth 1920 --json"] = new { stdout = """{"key":"computer.maxWidth","value":1920}""" };
        f.Cli["config set computer.maxHeight 1080 --json"] = new { stdout = """{"key":"computer.maxHeight","value":1080}""" };
        var vm = await f.StartAsync(TestProcesses.TempDir("ws-project"));
        var page = vm.ComputerSettings;
        await page.LoadAsync();
        Assert.False(page.ScreenChanged);
        page.MaxWidth = "wide";
        Assert.True(page.ScreenChanged);
        await page.SaveScreenCommand.ExecuteAsync(null);
        Assert.True(page.MessageIsError);
        Assert.DoesNotContain(f.CliCalls(), c => c.StartsWith("config set", StringComparison.Ordinal));

        page.MaxWidth = "1920";
        page.MaxHeight = "1080";
        await page.SaveScreenCommand.ExecuteAsync(null);
        Assert.False(page.MessageIsError, page.Message);
        Assert.Equal(["config set computer.maxWidth 1920 --json", "config set computer.maxHeight 1080 --json"],
            f.CliCalls().Where(c => c.StartsWith("config set", StringComparison.Ordinal)));
        Assert.False(page.ScreenChanged);
        Assert.True(page.NeedsRestart);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Computer_page_without_omp_running_saves_for_the_next_start()
    {
        var f = new WorkspaceFixture();
        f.Cli["config set computer.enabled true --json"] = new { stdout = """{"key":"computer.enabled","value":false}""" }; // the project overrides it
        var vm = f.Vm(f.Session(TestProcesses.TempDir("ws-project")));
        var page = vm.ComputerSettings;
        await page.LoadAsync();
        Assert.False(page.IsOmpRunning);
        Assert.False(page.HasSessionStatus);
        Assert.Equal(CheckState.Ok, page.Requirements[0].State); // eval on in the settings
        page.Enabled = true;
        await Until(() => page.HasMessage, "a message");
        Assert.Contains(Loc.F("Saved, but this project's own omp settings (.omp/config.yml in the project) set {0} differently, and they win.", Loc.T("computer use")), page.Message);
        Assert.Empty(f.SentCommands());
        await vm.DisposeAsync();
    }

    // ── Git and worktrees ──

    [AvaloniaFact]
    public async Task Git_page_reads_branch_changes_upstream_remote_and_worktrees()
    {
        if (!WorkspaceFixture.HasGit) { Assert.Skip("git is not installed"); return; }
        var f = new WorkspaceFixture();
        var (repo, worktree) = f.MakeRepo();
        f.Tools["gh auth status"] = new ToolResult(0, "github.com\n  ✓ Logged in to github.com account octocat (keyring)\n  - Active account: true\n", "");
        var vm = await f.StartAsync(repo);
        var page = vm.GitSettings;
        await page.LoadAsync();

        Assert.True(page.IsRepo, page.RepoError);
        Assert.Equal("main", page.BranchLabel);
        Assert.False(page.IsLinkedWorktree);
        Assert.Equal(Loc.Plural(3, "{0} file changed", "{0} files changed") + " · " + Loc.F("{0} staged", 1) + " · " + Loc.F("{0} untracked", 1), page.ChangesLabel);
        Assert.Equal(Loc.Plural(1, "{0} commit ahead of {1}", "{0} commits ahead of {1}", "origin/main"), page.UpstreamLabel);
        Assert.StartsWith("origin  ", page.RemoteLabel);
        Assert.Equal(2, page.Worktrees.Count);
        Assert.True(page.Worktrees[0].IsMain && page.Worktrees[0].IsCurrent);
        Assert.Equal("feature/x", page.Worktrees[1].Title);
        Assert.True(SessionCatalog.SamePath(worktree, page.Worktrees[1].Path));
        Assert.True(page.GhSignedIn);
        Assert.Equal(Loc.F("Signed in as {0}", "octocat"), page.GhLabel);
        Assert.True(page.SettingsLoaded);
        Assert.Contains(Loc.T("omp has no separate environments: a session works in a folder on this computer (the project, or one of its worktrees), and its SSH hosts are the other machines it can reach."), GitSettingsViewModel.EnvironmentsNote);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task A_worktree_with_changes_is_not_removed_a_clean_one_is_after_confirming()
    {
        if (!WorkspaceFixture.HasGit) { Assert.Skip("git is not installed"); return; }
        var f = new WorkspaceFixture();
        var (repo, worktree) = f.MakeRepo();
        File.WriteAllText(Path.Combine(worktree, "wip.txt"), "work in progress\n");
        var vm = await f.StartAsync(repo);
        var page = vm.GitSettings;
        await page.LoadAsync();
        var row = page.Worktrees[1];
        Assert.False(page.Worktrees[0].CanRemove); // the main folder, where this session works

        await row.AskRemoveCommand.ExecuteAsync(null);
        Assert.False(row.IsConfirming);
        Assert.Equal(Loc.Plural(1, "It has {0} uncommitted change. Commit or discard it first.", "It has {0} uncommitted changes. Commit or discard them first."), row.Problem);
        Assert.True(Directory.Exists(worktree));

        File.Delete(Path.Combine(worktree, "wip.txt"));
        await row.AskRemoveCommand.ExecuteAsync(null);
        Assert.True(row.IsConfirming);
        Assert.Contains(Loc.F(" Branch {0} stays.", "feature/x"), row.ConfirmText);
        await row.ConfirmRemoveCommand.ExecuteAsync(null);
        Assert.False(Directory.Exists(worktree));
        Assert.Single(page.Worktrees);
        Assert.Contains(Loc.F(" Branch {0} is still there.", "feature/x"), page.Message);
        // git names the worktree with links resolved (macOS: /var is /private/var): the same folder either way
        Assert.Contains(f.ToolLog, c => c.StartsWith("git worktree remove ", StringComparison.Ordinal) && SessionCatalog.SamePath(c["git worktree remove ".Length..], worktree));
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task New_worktree_runs_wt_and_follows_the_session_there()
    {
        if (!WorkspaceFixture.HasGit) { Assert.Skip("git is not installed"); return; }
        var f = new WorkspaceFixture();
        var (repo, worktree) = f.MakeRepo();
        f.Commands["/wt feature/y"] = $"Moved to worktree {worktree} on branch feature/y (checked out, uncommitted changes carried over).";
        f.Commands["/wt feature/x"] = "Worktree creation failed: Branch 'feature/x' already exists; pick another name.";
        var vm = await f.StartAsync(repo);
        var page = vm.GitSettings;
        await page.LoadAsync();
        Assert.True(page.CanCreateWorktree);

        page.StartNewWorktree();
        page.NewBranch = "bad name";
        Assert.True(page.HasNewBranchProblem);
        Assert.Equal(Loc.T("Use letters, digits, / - _ . and no spaces (not ~ ^ : ? * [ \\ or ..)."), page.NewBranchProblem);
        await page.CreateWorktreeCommand.ExecuteAsync(null);
        Assert.DoesNotContain(f.SentCommands(), c => c.StartsWith("/wt", StringComparison.Ordinal));

        page.NewBranch = "feature/y";
        Assert.Contains(Loc.F("omp makes branch {0} from the current commit in a new folder under {1}, copies your uncommitted changes there, and moves this conversation into it.", "feature/y", "\0").Split('\0')[0], page.CreateHint);
        await page.CreateWorktreeCommand.ExecuteAsync(null);
        Assert.Contains("/wt feature/y", f.SentCommands());
        Assert.False(page.IsCreating);
        Assert.False(page.MessageIsError, page.Message);
        Assert.Contains(Loc.F("This conversation now works in {0}, on branch {1}.", "\0", "feature/y").Split('\0')[1], page.Message);
        // A later restart (recovery, approval mode) starts omp in the worktree, where omp moved the session
        Assert.Equal(worktree, f.LastSession!.CurrentLaunch.WorkingDirectory);

        // omp's refusal comes back as it said it
        page.StartNewWorktree();
        page.NewBranch = "feature/x";
        await page.CreateWorktreeCommand.ExecuteAsync(null);
        Assert.True(page.MessageIsError);
        Assert.Equal("Worktree creation failed: Branch 'feature/x' already exists; pick another name.", page.Message);
        Assert.True(page.IsCreating);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Git_page_outside_git_and_github_states_and_settings()
    {
        var f = new WorkspaceFixture();
        f.Tools["gh auth status"] = new ToolResult(1, "", "You are not logged into any GitHub hosts. To log in, run: gh auth login\n");
        f.Cli["config set github.enabled true --json"] = new { stdout = """{"key":"github.enabled","value":true}""" };
        f.Cli["config set task.isolation.merge branch --json"] = new { stdout = """{"key":"task.isolation.merge","value":"branch"}""" };
        f.Cli["config set worktree.base ~/trees --json"] = new { stdout = """{"key":"worktree.base","value":"~/trees"}""" };
        var vm = await f.StartAsync(TestProcesses.TempDir("ws-plain"));
        var page = vm.GitSettings;
        await page.LoadAsync();
        if (WorkspaceFixture.HasGit)
        {
            Assert.True(page.IsNotRepo);
            Assert.False(page.IsRepo);
            Assert.Empty(page.Worktrees);
        }
        Assert.True(page.GhSignedOut);
        Assert.Equal(Loc.T("Not signed in"), page.GhLabel);

        page.GithubEnabled = true;
        await Until(() => page.NeedsRestart, "saved for the next start");
        Assert.Contains("config set github.enabled true --json", f.CliCalls());

        await page.SetIsolationMergeCommand.ExecuteAsync("branch");
        Assert.Contains("config set task.isolation.merge branch --json", f.CliCalls());
        Assert.Equal("branch", page.IsolationMerge);

        page.WorktreeBase = "relative/path";
        await page.SaveBaseCommand.ExecuteAsync(null);
        Assert.True(page.MessageIsError);
        page.WorktreeBase = "~/trees";
        Assert.True(page.BaseChanged);
        await page.SaveBaseCommand.ExecuteAsync(null);
        Assert.Contains("config set worktree.base ~/trees --json", f.CliCalls());
        Assert.False(page.BaseChanged);
        Assert.Contains("~/trees", page.CreateHint);

        // gh missing altogether
        f.Tools.Clear();
        await page.CheckGitHubAgainCommand.ExecuteAsync(null);
        Assert.True(page.GhNotInstalled);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Signing_in_to_github_opens_gh_auth_login_in_a_terminal()
    {
        var f = new WorkspaceFixture();
        f.Tools["gh auth status"] = new ToolResult(1, "", "You are not logged into any GitHub hosts. To log in, run: gh auth login\n");
        var vm = await f.StartAsync(TestProcesses.TempDir("ws-project"));
        await vm.OpenSettingsAtAsync("git");
        var page = vm.GitSettings;
        await Until(() => page.GhSignedOut, "signed out");
        page.SignInToGitHubCommand.Execute(null);
        var t = Assert.Single(vm.Terminals);
        Assert.Equal(["auth", "login"], t.Args);
        Assert.EndsWith("gh", Path.GetFileNameWithoutExtension(t.File));
        Assert.True(vm.IsTerminalOpen);
        Assert.False(vm.IsSettingsOpen);

        // When the login ends, the page asks gh again
        f.Tools["gh auth status"] = new ToolResult(0, "github.com\n  ✓ Logged in to github.com account octocat (keyring)\n  - Active account: true\n", "");
        t.OnExited(0);
        await Until(() => page.GhSignedIn, "signed in after the terminal ended");
        await vm.DisposeAsync();
    }

    // ── Branch chip ──

    [AvaloniaFact]
    public async Task Branch_chip_shows_the_branch_and_the_worktree_and_its_menu_works()
    {
        if (!WorkspaceFixture.HasGit) { Assert.Skip("git is not installed"); return; }
        var f = new WorkspaceFixture();
        var (repo, worktree) = f.MakeRepo();
        var vm = await f.StartAsync(repo);
        await Until(() => vm.BranchChip.Branch == "main", "branch read");
        Assert.False(vm.BranchChip.IsWorktree);
        Assert.True(vm.BranchChip.IsVisible);

        string? copied = null;
        vm.CopyTextRequested += s => { copied = s; return Task.FromResult(true); };
        vm.BranchChip.CopyBranchCommand.Execute(null);
        Assert.Equal("main", copied);

        await vm.BranchChip.NewWorktreeCommand.ExecuteAsync(null);
        Assert.True(vm.IsSettingsOpen);
        Assert.Equal("git", vm.SettingsCategory);
        Assert.True(vm.GitSettings.IsCreating);
        await vm.DisposeAsync();

        var inWorktree = await f.StartAsync(worktree);
        await Until(() => inWorktree.BranchChip.Branch == "feature/x", "worktree branch read");
        Assert.True(inWorktree.BranchChip.IsWorktree);
        Assert.Contains(Loc.F("In a worktree of {0}", "\0").Split('\0')[0], inWorktree.BranchChip.Tooltip);
        Assert.Contains(Loc.T(", worktree"), inWorktree.BranchChip.AccessibleName);

        // A detached HEAD shows the commit
        WorkspaceFixture.Git(worktree, "checkout", "-q", "--detach");
        await inWorktree.BranchChip.RefreshNowAsync();
        Assert.True(inWorktree.BranchChip.IsDetached);
        Assert.Equal(Loc.T("Copy commit"), inWorktree.BranchChip.CopyLabel);
        await inWorktree.DisposeAsync();

        var outside = await f.StartAsync(TestProcesses.TempDir("ws-plain"));
        await outside.BranchChip.RefreshNowAsync();
        Assert.False(outside.BranchChip.IsVisible);
        await outside.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task A_branch_read_finishing_after_disposal_does_not_publish_a_late_result()
    {
        var fixture = new WorkspaceFixture();
        var folder = TestProcesses.TempDir("ws-closing");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var vm = new MainViewModel(fixture.Session(folder), new Ombra.App.AppArgs())
        {
            RunTool = (command, _) =>
            {
                if (!command.ToString().StartsWith("git rev-parse --show-toplevel", StringComparison.Ordinal))
                    return Task.FromResult(new ToolResult(0, "late-branch\n", ""));
                if (Interlocked.Increment(ref reads) == 2) entered.TrySetResult();
                return finish.Task; // a tool result already in flight can finish after cancellation
            },
        };
        var chip = vm.BranchChip; // its automatic read and the explicitly awaited read share the gate
        var read = chip.RefreshNowAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await vm.DisposeAsync();
            finish.SetResult(new ToolResult(0, $"{folder}\n{folder}/.git\n{folder}/.git\n", ""));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.Null(chip.Branch);
            Assert.False(chip.IsVisible);
        }
        finally
        {
            finish.TrySetCanceled();
        }
    }

    // ── SSH hosts ──

    [AvaloniaFact]
    public async Task Ssh_hosts_list_add_remove_with_omps_grammar()
    {
        var f = new WorkspaceFixture();
        f.Commands["/ssh list"] = "pbox | 10.0.0.9 | - | 22 [project]\nbox | example.com | me | 2222 [user]";
        f.Commands["/ssh add"] = "Added SSH host \"new-box\" (user).";
        f.Commands["/ssh remove"] = "Removed SSH host \"pbox\" from project config.";
        var agent = Path.Combine(f.Dir, "agent");
        Directory.CreateDirectory(agent);
        f.Cli["config path"] = new { stdout = agent + "\n" };
        File.WriteAllText(Path.Combine(agent, "ssh.json"), """{"hosts":{"box":{"host":"example.com","username":"me","port":2222,"keyPath":"~/.ssh/box"}}}""");
        var vm = await f.StartAsync(TestProcesses.TempDir("ws-project"));
        var page = vm.SshSettings;
        await page.LoadAsync();
        Assert.False(page.ReadOnly);
        Assert.Equal(["pbox", "box"], page.Hosts.Select(h => h.Name));
        Assert.Equal(Loc.T("This project"), page.Hosts[0].ScopeLabel);
        Assert.Equal(Loc.F("{0} · key {1}", "me@example.com:2222", "~/.ssh/box"), page.Hosts[1].Detail);

        page.BeginAddCommand.Execute(null);
        page.NewName = "new box";
        await page.AddCommand.ExecuteAsync(null);
        Assert.Contains(Loc.T("The name can only use letters, digits, - _ and . (no spaces)."), page.FormError);
        page.NewName = "new-box";
        page.NewHost = "build.example.com";
        page.NewUser = "ci";
        page.NewPort = "70000";
        await page.AddCommand.ExecuteAsync(null);
        Assert.Contains(Loc.T("The port is a number from 1 to 65535."), page.FormError);
        page.NewPort = "2200";
        page.NewKey = "/keys/my key";
        await page.AddCommand.ExecuteAsync(null);
        Assert.False(page.IsAdding, page.FormError);
        Assert.Contains("/ssh add new-box --host build.example.com --user ci --port 2200 --key \"/keys/my key\" --scope user", f.SentCommands());

        var pbox = page.Hosts[0];
        pbox.AskRemoveCommand.Execute(null);
        Assert.True(pbox.IsConfirming);
        await pbox.ConfirmRemoveCommand.ExecuteAsync(null);
        Assert.Contains("/ssh remove pbox --scope project", f.SentCommands());
        Assert.Equal(Loc.F("Removed {0}.", "pbox"), page.Message);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Ssh_test_connects_like_omp_and_says_what_failed()
    {
        var f = new WorkspaceFixture();
        f.Commands["/ssh list"] = "box | example.com | me | 2222 [user]\nlab | 10.1.1.1 | - | 22 [user]";
        const string common = "ssh -n -o LogLevel=DEBUG1 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=8 -o ControlPath=none";
        f.Tools[common + " -p 2222 me@example.com exit"] = new ToolResult(0, "", "");
        f.Tools[common + " 10.1.1.1 exit"] = new ToolResult(255, "", """
            debug1: Connecting to 10.1.1.1 [10.1.1.1] port 22.
            debug1: Connection established.
            debug1: Local version string SSH-2.0-OpenSSH_10.3
            debug1: Remote protocol version 2.0, remote software version OpenSSH_9.6
            debug1: Server host key: ssh-ed25519 SHA256:AAAAexampleexampleexampleexampleexampleexample
            debug1: Host '10.1.1.1' is known and matches the ED25519 host key.
            debug1: Authentications that can continue: publickey,password
            me@10.1.1.1: Permission denied (publickey,password).
            """);
        var vm = await f.StartAsync(TestProcesses.TempDir("ws-project"));
        var page = vm.SshSettings;
        await page.LoadAsync();
        await page.Hosts[0].TestCommand.ExecuteAsync(null);
        Assert.True(page.Hosts[0].TestOk);
        Assert.StartsWith(Loc.F("Connected{0} in {1} ms.", "", "\0").Split('\0')[0], page.Hosts[0].TestResult);
        await page.Hosts[1].TestCommand.ExecuteAsync(null);
        Assert.False(page.Hosts[1].TestOk);
        Assert.Contains("Permission denied (publickey,password).", page.Hosts[1].TestResult);
        Assert.Contains(Loc.T("omp signs in with keys only (no passwords): put your public key on the server, or choose the key file."), page.Hosts[1].TestResult);

        // No ssh client at all
        f.Tools.Clear();
        await page.Hosts[0].TestCommand.ExecuteAsync(null);
        Assert.Contains(Loc.T("ssh was not found on this computer. omp needs the OpenSSH client too."), page.Hosts[0].TestResult);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Ssh_page_without_omp_shows_the_saved_files_read_only()
    {
        var f = new WorkspaceFixture();
        var project = TestProcesses.TempDir("ws-project");
        var agent = Path.Combine(f.Dir, "agent");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(project, ".omp"));
        f.Cli["config path"] = new { stdout = agent + "\n" };
        File.WriteAllText(Path.Combine(agent, "ssh.json"), """{"hosts":{"box":{"host":"user-box"},"shared":{"host":"from-user"}}}""");
        File.WriteAllText(Path.Combine(project, ".omp", "ssh.json"), """{"hosts":{"shared":{"host":"from-project","port":"2022"}}}""");
        var s = f.Session(project);
        var vm = f.Vm(s);
        // A project folder without omp running: the launch's folder
        var page = vm.SshSettings;
        await page.LoadAsync();
        Assert.True(page.ReadOnly);
        Assert.False(page.CanChange);
        Assert.Equal(["shared", "box"], page.Hosts.Select(h => h.Name));
        Assert.Equal("from-project:2022", page.Hosts[0].Address); // the project entry wins, as in /ssh list
        Assert.Empty(f.SentCommands());
        await vm.DisposeAsync();
    }
}
