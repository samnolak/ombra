using Avalonia.Headless.XUnit;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The Computer use, Git and worktrees and SSH pages against real omp 18.2.0. Reading needs OMPGUI_TEST_CONFIG;
/// changing omp's settings, SSH hosts and worktrees needs OMPGUI_WORKSPACE_TEST_CONFIG, a configuration whose HOME is
/// a copy made for writing (never the shared test home).
/// </summary>
public sealed class RealWorkspaceTests(ITestOutputHelper log)
{
    private static OmpRuntimeOptions? Writable()
    {
        var path = Environment.GetEnvironmentVariable("OMPGUI_WORKSPACE_TEST_CONFIG");
        return path is null || !File.Exists(path) ? null : OmpRuntimeOptions.Load(path);
    }

    private static async Task<MainViewModel> StartAsync(OmpRuntimeOptions options, string project)
    {
        var s = new SessionController(options.ToLaunchSpec, new LaunchRequest(project, ApprovalMode: "write"), TimeSpan.FromSeconds(90));
        var vm = new MainViewModel(s, new AppArgs()) { OmpCliLaunch = (args, dir) => options.ToCliLaunchSpec(args, dir) };
        vm.OnWindowOpened();
        await WorkspaceFixture.Until(() => vm.Phase == SessionPhase.Ready && vm.ProjectFolder is not null, "real omp ready", 90);
        return vm;
    }

    [AvaloniaFact]
    public async Task Real_omp_reports_computer_use_eval_and_ssh_hosts()
    {
        if (TestProcesses.RealOmp() is not { } options) { Assert.Skip("OMPGUI_TEST_CONFIG is not set"); return; }
        var vm = await StartAsync(options, TestProcesses.TempDir("real-ws"));
        var computer = vm.ComputerSettings;
        await computer.LoadAsync();
        log.WriteLine($"session: {computer.Session} eval: {computer.EvalActive} error: {computer.LoadError}");
        Assert.Equal("", computer.LoadError);
        Assert.NotNull(computer.Session);
        Assert.NotNull(computer.EvalActive);
        Assert.Equal(computer.Session!.Enabled, computer.Enabled);

        var ssh = vm.SshSettings;
        await ssh.LoadAsync();
        log.WriteLine($"ssh: {ssh.Hosts.Count} hosts, files {ssh.FilesLabel}, error {ssh.LoadError}");
        Assert.False(ssh.HasLoadError, ssh.LoadError);
        Assert.DoesNotContain("omp's agent folder", ssh.UserFile); // found through omp config path

        var git = vm.GitSettings;
        await git.LoadAsync();
        Assert.True(git.SettingsLoaded, git.SettingsError);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Real_omp_turns_computer_use_on_and_off_and_adds_and_removes_an_ssh_host()
    {
        if (Writable() is not { } options) { Assert.Skip("OMPGUI_WORKSPACE_TEST_CONFIG is not set"); return; }
        var project = TestProcesses.TempDir("real-ws");
        var vm = await StartAsync(options, project);
        var computer = vm.ComputerSettings;
        await computer.LoadAsync();
        var before = computer.Enabled;
        try
        {
            computer.Enabled = true;
            await WorkspaceFixture.Until(() => computer.HasMessage && computer.CanToggle, "saved", 60);
            log.WriteLine("on: " + computer.Message + " | " + computer.Session);
            Assert.True(computer.SessionOn, computer.Message);
            Assert.True((await WorkspaceConfig.LoadAsync(vm, default)).Config!.Bool("computer.enabled"));
            computer.Message = "";
            computer.Enabled = false;
            await WorkspaceFixture.Until(() => computer.HasMessage && computer.CanToggle, "saved off", 60);
            log.WriteLine("off: " + computer.Message + " | " + computer.Session);
            Assert.False(computer.Session!.Enabled);
        }
        finally
        {
            await WorkspaceConfig.SetAsync(vm, "computer.enabled", before ? "true" : "false", default);
        }

        var ssh = vm.SshSettings;
        await ssh.LoadAsync();
        ssh.BeginAddCommand.Execute(null);
        ssh.NewName = "ws-test-box";
        ssh.NewHost = "build.example.invalid";
        ssh.NewUser = "ci";
        ssh.NewPort = "2222";
        ssh.NewKey = "~/.ssh/some key";
        ssh.SetScopeCommand.Execute("project");
        await ssh.AddCommand.ExecuteAsync(null);
        Assert.False(ssh.IsAdding, ssh.FormError);
        var host = Assert.Single(ssh.Hosts, h => h.Name == "ws-test-box");
        Assert.Equal("ci@build.example.invalid:2222 · key ~/.ssh/some key", host.Detail); // key path read back from ssh.json
        Assert.True(File.Exists(Path.Combine(project, ".omp", "ssh.json")));
        host.AskRemoveCommand.Execute(null);
        await host.ConfirmRemoveCommand.ExecuteAsync(null);
        Assert.DoesNotContain(ssh.Hosts, h => h.Name == "ws-test-box");
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Real_omp_moves_the_session_into_a_new_worktree()
    {
        if (Writable() is not { } options) { Assert.Skip("OMPGUI_WORKSPACE_TEST_CONFIG is not set"); return; }
        if (!WorkspaceFixture.HasGit) { Assert.Skip("git is not installed"); return; }
        var f = new WorkspaceFixture();
        var (repo, _) = f.MakeRepo();
        var vm = await StartAsync(options, repo);
        var git = vm.GitSettings;
        await git.LoadAsync();
        var branch = "ws-test/" + Guid.NewGuid().ToString("N")[..6];
        git.StartNewWorktree();
        git.NewBranch = branch;
        await git.CreateWorktreeCommand.ExecuteAsync(null);
        log.WriteLine(git.Message);
        Assert.False(git.MessageIsError, git.Message);
        try
        {
            // The window follows omp into the worktree: the project folder, the branch chip, the page
            await WorkspaceFixture.Until(() => vm.ProjectFolder is { } p && !GitProbe.SamePath(p, repo), "moved", 30);
            log.WriteLine("now in " + vm.ProjectFolder);
            await vm.BranchChip.RefreshNowAsync();
            Assert.Equal(branch, vm.BranchChip.Branch);
            Assert.True(vm.BranchChip.IsWorktree);
            Assert.Equal(branch, git.BranchLabel);
            // Uncommitted changes came along (README.md modified, staged.txt, untracked.txt)
            Assert.Equal("3 files changed · 1 staged · 1 untracked", git.ChangesLabel);
        }
        finally
        {
            if (vm.ProjectFolder is { } wt && !GitProbe.SamePath(wt, repo))
            {
                await vm.DisposeAsync();
                WorkspaceFixture.Git(repo, "worktree", "remove", "--force", wt);
                WorkspaceFixture.Git(repo, "branch", "-D", branch);
            }
            else await vm.DisposeAsync();
        }
    }
}
