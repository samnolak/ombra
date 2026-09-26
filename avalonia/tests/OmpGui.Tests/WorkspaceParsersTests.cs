using OmpGui.App.ViewModels;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>What omp's /computer, /tools, /ssh and /wt print (omp 18.2.0, real outputs) and git's and gh's formats.</summary>
public class WorkspaceParsersTests
{
    [Fact]
    public void Computer_status_as_omp_prints_it()
    {
        var off = WorkspaceParsers.ParseComputerStatus("Computer use: disabled · prelude: inactive · configured: display=all, maxWidth=3840, maxHeight=2400");
        Assert.Equal(new ComputerUseStatus(false, false, "all", "3840", "2400"), off);
        // /computer on prints its confirmation and the status in one line
        var on = WorkspaceParsers.ParseComputerStatus("Computer use enabled for this session. Computer use: enabled · prelude: active · configured: display=2, maxWidth=1920, maxHeight=1080");
        Assert.Equal(new ComputerUseStatus(true, true, "2", "1920", "1080"), on);
        Assert.Null(WorkspaceParsers.ParseComputerStatus("Computer use disabled for this session."));
        Assert.Null(WorkspaceParsers.ParseComputerStatus(null));
    }

    [Fact]
    public void Tools_list_marks_active_and_available_tools()
    {
        var tools = WorkspaceParsers.ParseTools("* read\n* bash\n- ast_edit\n* eval\n- debug\n~ xd://lsp\n");
        Assert.True(tools["eval"]);
        Assert.False(tools["debug"]);
        Assert.False(tools.ContainsKey("xd://lsp"));
        Assert.False(WorkspaceParsers.ParseTools("* read\n- eval")["eval"]);
        Assert.Empty(WorkspaceParsers.ParseTools("No tools are available."));
    }

    [Fact]
    public void Git_status_porcelain_v2_is_summed_up()
    {
        const string text = """
            # branch.oid 1f2e3d4c5b6a79881f2e3d4c5b6a79881f2e3d4c
            # branch.head feature/login
            # branch.upstream origin/feature/login
            # branch.ab +2 -1
            1 M. N... 100644 100644 100644 aaaa bbbb src/a.cs
            1 MM N... 100644 100644 100644 aaaa bbbb src/b.cs
            1 .M N... 100644 100644 100644 aaaa bbbb src/c.cs
            2 R. N... 100644 100644 100644 aaaa bbbb R100 new.cs	old.cs
            u UU N... 100644 100644 100644 100644 aaaa bbbb cccc conflict.cs
            ? notes.txt
            ? tmp/
            """;
        var s = WorkspaceParsers.ParseGitStatus(text);
        Assert.Equal("feature/login", s.Branch);
        Assert.Equal("origin/feature/login", s.Upstream);
        Assert.Equal((2, 1), (s.Ahead, s.Behind));
        Assert.Equal(3, s.Staged);   // a, b, the rename
        Assert.Equal(2, s.Unstaged); // b, c
        Assert.Equal(2, s.Untracked);
        Assert.Equal(1, s.Conflicts);
        Assert.Equal(7, s.ChangedFiles);
        Assert.False(s.Clean);
        Assert.Equal("7 files changed · 3 staged · 2 untracked · 1 with conflicts", GitSettingsViewModel.ChangesText(s));

        var detached = WorkspaceParsers.ParseGitStatus("# branch.oid abcdef1234\n# branch.head (detached)\n");
        Assert.True(detached.Detached);
        Assert.Null(detached.Branch);
        Assert.True(detached.Clean);
        Assert.Equal("No uncommitted changes", GitSettingsViewModel.ChangesText(detached));

        var fresh = WorkspaceParsers.ParseGitStatus("# branch.oid (initial)\n# branch.head main\n? README.md\n");
        Assert.True(fresh.Unborn);
        Assert.Null(fresh.Upstream);
        Assert.Equal("1 file changed · 1 untracked", GitSettingsViewModel.ChangesText(fresh));
    }

    [Fact]
    public void Worktree_list_porcelain_gives_each_worktree()
    {
        const string text = """
            worktree /home/me/app
            HEAD 1111111111111111111111111111111111111111
            branch refs/heads/main

            worktree /home/me/.omp/wt/feature-x-298d3fa
            HEAD 2222222222222222222222222222222222222222
            branch refs/heads/feature/x

            worktree /tmp/review
            HEAD 3333333333333333333333333333333333333333
            detached
            locked reviewing a PR

            worktree /tmp/gone
            HEAD 4444444444444444444444444444444444444444
            branch refs/heads/old
            prunable gitdir file points to non-existent location

            """;
        var list = WorkspaceParsers.ParseWorktrees(text);
        Assert.Equal(4, list.Count);
        Assert.True(list[0].IsMain);
        Assert.Equal("main", list[0].Branch);
        Assert.False(list[1].IsMain);
        Assert.Equal("feature/x", list[1].Branch);
        Assert.Equal("/home/me/.omp/wt/feature-x-298d3fa", list[1].Path);
        Assert.True(list[2].Detached);
        Assert.Null(list[2].Branch);
        Assert.True(list[2].Locked);
        Assert.Equal("reviewing a PR", list[2].LockReason);
        Assert.True(list[3].Prunable);

        var row = new WorktreeRowViewModel(null!, list[2], isCurrent: false);
        Assert.Equal("No branch (at 3333333)", row.Title);
        Assert.Equal("Locked", row.Tag);
    }

    [Fact]
    public void Gh_auth_status_new_old_failed_and_signed_out()
    {
        const string current = """
            github.com
              ✓ Logged in to github.com account octocat (keyring)
              - Active account: true
              - Git operations protocol: https
              - Token: gho_************************************
              - Token scopes: 'gist', 'read:org', 'repo', 'workflow'

              ✓ Logged in to github.com account hubot (keyring)
              - Active account: false
            """;
        var a = WorkspaceParsers.ParseGhAuth(current);
        Assert.True(a.SignedIn);
        Assert.Equal(2, a.Accounts.Count);
        Assert.Equal("octocat", a.Primary!.Login);

        var old = WorkspaceParsers.ParseGhAuth("github.com\n  ✓ Logged in to github.com as monalisa (/home/me/.config/gh/hosts.yml)\n  ✓ Git operations for github.com configured to use https protocol.\n  ✓ Token: *****");
        Assert.Equal("monalisa", old.Primary!.Login);

        const string failed = """
            github.com
              X Failed to log in to github.com account octocat (default)
              - Active account: true
              - The token in default is invalid.
              - To re-authenticate, run: gh auth login -h github.com
            """;
        var f = WorkspaceParsers.ParseGhAuth(failed);
        Assert.False(f.SignedIn);
        Assert.Null(f.Primary);
        Assert.Equal("The token in default is invalid.", f.Accounts[0].Problem);

        var none = WorkspaceParsers.ParseGhAuth("You are not logged into any GitHub hosts. To log in, run: gh auth login");
        Assert.False(none.SignedIn);
        Assert.Empty(none.Accounts);
    }

    [Fact]
    public void Ssh_list_as_omp_prints_it()
    {
        var hosts = WorkspaceParsers.ParseSshList("pbox | 10.0.0.9 | - | 22 [project]\nbox | example.invalid | me | 2222 [user]");
        Assert.NotNull(hosts);
        Assert.Equal(new SshHostEntry("pbox", "10.0.0.9", null, 22, "project"), hosts[0]);
        Assert.Equal(new SshHostEntry("box", "example.invalid", "me", 2222, "user"), hosts[1]);
        Assert.Empty(WorkspaceParsers.ParseSshList("No SSH hosts configured.")!);
        Assert.Null(WorkspaceParsers.ParseSshList("Failed to list SSH hosts: Failed to parse SSH config file x"));
    }

    [Fact]
    public void Wt_output_branch_names_urls_and_quoting()
    {
        var moved = WorkspaceParsers.ParseMovedToWorktree("Moved to worktree /home/me/.omp/wt/feature-try-1-298d3fa on branch feature/try-1 (checked out, uncommitted changes carried over).");
        Assert.Equal(("/home/me/.omp/wt/feature-try-1-298d3fa", "feature/try-1"), moved);
        Assert.Null(WorkspaceParsers.ParseMovedToWorktree("Worktree creation failed: Branch 'x' already exists; pick another name."));

        Assert.Null(WorkspaceParsers.BranchNameProblem(""));
        Assert.Null(WorkspaceParsers.BranchNameProblem("feature/login-2"));
        Assert.NotNull(WorkspaceParsers.BranchNameProblem("has space"));
        Assert.NotNull(WorkspaceParsers.BranchNameProblem("-x"));
        Assert.NotNull(WorkspaceParsers.BranchNameProblem("a..b"));
        Assert.NotNull(WorkspaceParsers.BranchNameProblem("a/"));

        Assert.Equal("https://github.com/o/r.git", WorkspaceParsers.RedactRemoteUrl("https://me:ghp_secret@github.com/o/r.git"));
        Assert.Equal("git@github.com:o/r.git", WorkspaceParsers.RedactRemoteUrl("git@github.com:o/r.git"));

        Assert.Equal("plain", WorkspaceParsers.QuoteArg("plain"));
        Assert.Equal("\"/my keys/id\"", WorkspaceParsers.QuoteArg("/my keys/id"));
        Assert.Equal("'say \"hi\"'", WorkspaceParsers.QuoteArg("say \"hi\""));
        Assert.Null(WorkspaceParsers.QuoteArg("it's \"both\""));
    }

    [Fact]
    public void Xdpyinfo_extensions_and_linux_checks()
    {
        const string xdpy = "name of display:    :0\nnumber of extensions:    3\n    BIG-REQUESTS\n    RANDR\n    XTEST\ndefault screen number:    0\n";
        Assert.Equal(["BIG-REQUESTS", "RANDR", "XTEST"], ComputerChecks.ParseXdpyinfoExtensions(xdpy).Order());
    }

    [Theory]
    [InlineData(":0", null, true, CheckState.Ok, CheckState.Ok)]
    [InlineData(":0", null, false, CheckState.Ok, CheckState.Failed)]
    [InlineData(null, "wayland-0", true, CheckState.Warning, null)]
    [InlineData(null, null, true, CheckState.Failed, null)]
    public async Task Linux_requirements_follow_the_display_server(string? display, string? wayland, bool xtest, CheckState first, CheckState? second)
    {
        var env = new Dictionary<string, string?> { ["DISPLAY"] = display, ["WAYLAND_DISPLAY"] = wayland, ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/bus" };
        var ran = new List<string>();
        Task<ToolResult> Run(ToolCommand c, CancellationToken _)
        {
            ran.Add(c.ToString());
            return Task.FromResult(new ToolResult(0, $"number of extensions:    2\n    RANDR\n    {(xtest ? "XTEST" : "XINERAMA")}\nscreen #0:\n", ""));
        }
        var checks = await ComputerChecks.RunAsync(Run, k => env.GetValueOrDefault(k), CancellationToken.None, "linux");
        Assert.Equal(first, checks[0].State);
        if (second is { } s)
        {
            Assert.Equal(s, checks[1].State);
            Assert.Equal([$"xdpyinfo -display {display}"], ran);
        }
        else Assert.Empty(ran); // no X server to ask
        if (display is null && wayland is null) Assert.Single(checks);
        else Assert.Contains(checks, c => c.Title.StartsWith("Accessibility", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mac_and_windows_requirements()
    {
        Task<ToolResult> None(ToolCommand c, CancellationToken _) => throw new InvalidOperationException("no tool runs");
        var mac = await ComputerChecks.RunAsync(None, _ => null, CancellationToken.None, "macos");
        Assert.Equal(["Screen Recording", "Accessibility"], mac.Select(c => c.Title));
        if (!OperatingSystem.IsMacOS())
        {
            Assert.All(mac, c => Assert.Equal(CheckState.Unknown, c.State));
            Assert.Equal(ComputerChecks.ScreenRecordingPane, mac[0].Action);
            Assert.Equal(ComputerChecks.AccessibilityPane, mac[1].Action);
        }
        var win = await ComputerChecks.RunAsync(None, _ => null, CancellationToken.None, "windows");
        Assert.Equal(CheckState.Ok, Assert.Single(win).State);
    }

    [Fact]
    public void Tool_results_say_the_tools_own_words()
    {
        Assert.Equal("fatal: not a git repository (or any of the parent directories): .git",
            new ToolResult(128, "", "warning: x\nfatal: not a git repository (or any of the parent directories): .git\n").Message);
        Assert.False(new ToolResult(0, "", "", NotFound: true).Ok);
        Assert.Equal("git status", new ToolCommand("git", ["status"]).ToString());
        Assert.Null(WorkspaceTools.Which("surely-not-a-tool-" + Guid.NewGuid().ToString("N")));
    }
}
