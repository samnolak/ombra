using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// The Computer use, Git and worktrees and SSH pages against the fake omp: canned builtin answers (FAKE_OMP_COMMANDS,
/// each command logged), a scripted omp CLI (FakeCli, each call logged), real git in temp repositories, and scripted
/// gh / ssh answers.
/// </summary>
internal sealed class WorkspaceFixture
{
    public string Dir { get; } = TestProcesses.TempDir("workspace");
    public string CommandsFile => Path.Combine(Dir, "commands.json");
    public string CommandLog => Path.Combine(Dir, "commands.log");
    public string CliFile => Path.Combine(Dir, "cli.json");
    public string CliLog => Path.Combine(Dir, "cli.log");
    public List<string> ToolLog { get; } = [];

    /// <summary>Scripted answers for gh, ssh and xdpyinfo (joined command → result); git runs for real.</summary>
    public Dictionary<string, ToolResult> Tools { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Commands { get; } = new(StringComparer.Ordinal)
    {
        ["/computer status"] = "Computer use: disabled · prelude: inactive · configured: display=all, maxWidth=3840, maxHeight=2400",
        ["/tools"] = "* read\n* bash\n* edit\n* eval\n* glob\n- debug",
        ["/ssh list"] = "No SSH hosts configured.",
    };

    public Dictionary<string, object> Cli { get; } = new(StringComparer.Ordinal);

    public WorkspaceFixture() => SetConfig(new Dictionary<string, object?>());

    /// <summary>What <c>omp config list --json</c> prints: every key given as {value, type, description}.</summary>
    public void SetConfig(Dictionary<string, object?> values)
    {
        var all = new Dictionary<string, object?>
        {
            ["computer.enabled"] = false, ["computer.display"] = "all", ["computer.maxWidth"] = 3840, ["computer.maxHeight"] = 2400,
            ["eval.js"] = true, ["eval.py"] = true, ["tools.approval"] = new Dictionary<string, object>(), ["tools.approvalMode"] = "yolo",
            ["github.enabled"] = false, ["worktree.clone"] = true, ["worktree.cleanSource"] = false,
            ["task.isolation.enabled"] = false, ["task.isolation.merge"] = "patch", ["task.isolation.apply"] = true,
        };
        foreach (var (k, v) in values) all[k] = v;
        var json = JsonSerializer.Serialize(all.ToDictionary(kv => kv.Key, kv => (object)new { value = kv.Value, type = "x", description = "" }));
        Cli["config list --json"] = new { stdout = json };
    }

    public void Write()
    {
        File.WriteAllText(CommandsFile, JsonSerializer.Serialize(Commands));
        File.WriteAllText(CliFile, JsonSerializer.Serialize(Cli));
    }

    public IReadOnlyList<string> SentCommands() => File.Exists(CommandLog) ? File.ReadAllLines(CommandLog) : [];
    public IReadOnlyList<string> CliCalls() => File.Exists(CliLog) ? File.ReadAllLines(CliLog) : [];

    public Task<ToolResult> RunTool(ToolCommand c, CancellationToken ct)
    {
        lock (ToolLog) ToolLog.Add(c.ToString());
        if (c.File == "git") return WorkspaceTools.RunAsync(c, ct);
        var key = c.ToString();
        var hit = Tools.Where(kv => key == kv.Key || key.StartsWith(kv.Key + " ", StringComparison.Ordinal)).OrderByDescending(kv => kv.Key.Length).FirstOrDefault();
        return Task.FromResult(hit.Value ?? new ToolResult(-1, "", c.File + " was not found", NotFound: true));
    }

    /// <summary>The fake omp in <paramref name="project"/> with this fixture's answers.</summary>
    public SessionController Session(string project, string scenario = "normal")
    {
        Write();
        var sessions = Path.Combine(Dir, "sessions");
        return new SessionController(req =>
        {
            var spec = TestProcesses.Fake(scenario);
            return spec with
            {
                Arguments = req.ResumeSessionFile is { } r ? [.. spec.Arguments, "--resume", r] : spec.Arguments,
                WorkingDirectory = req.WorkingDirectory,
                Environment = new Dictionary<string, string?>
                {
                    ["FAKE_SESSION_DIR"] = sessions,
                    ["FAKE_OMP_COMMANDS"] = CommandsFile,
                    ["FAKE_OMP_COMMAND_LOG"] = CommandLog,
                },
            };
        }, new LaunchRequest(project, ApprovalMode: "write"));
    }

    public MainViewModel Vm(SessionController s) => new(s, new AppArgs())
    {
        OmpCliLaunch = TestProcesses.FakeCli(CliFile, CliLog),
        RunTool = RunTool,
    };

    /// <summary>A window on the fake omp, started (omp ready) unless <paramref name="start"/> is false.</summary>
    public async Task<(MainWindow W, MainViewModel Vm)> OpenAsync(string project, double width = 1180, double height = 760, bool start = true)
    {
        var vm = Vm(Session(project));
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        if (start)
        {
            vm.OnWindowOpened();
            await Until(() => vm.Phase == SessionPhase.Ready, "omp ready");
        }
        await Settle();
        return (w, vm);
    }

    /// <summary>The session of the last <see cref="StartAsync"/>.</summary>
    public SessionController? LastSession { get; private set; }

    /// <summary>A started view model without a window (the pages' logic).</summary>
    public async Task<MainViewModel> StartAsync(string project)
    {
        LastSession = Session(project);
        var vm = Vm(LastSession);
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready && vm.ProjectFolder is not null, "omp ready");
        return vm;
    }

    public static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    public static async Task Settle(int ms = 250)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    // ── git repositories for the tests ──

    public static bool HasGit => WorkspaceTools.Which("git") is not null;

    public static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-c", "user.email=t@example.com", "-c", "user.name=Test", "-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main" }.Concat(args))
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }

    /// <summary>
    /// A repository "app" on main with an upstream (origin, a bare repository beside it) one commit behind, a modified,
    /// a staged and an untracked file, and a linked worktree "app-feature" on feature/x.
    /// </summary>
    public (string Repo, string Worktree) MakeRepo()
    {
        var root = Path.Combine(Dir, "git");
        Directory.CreateDirectory(root);
        var origin = Path.Combine(root, "origin.git");
        var repo = Path.Combine(root, "app");
        Directory.CreateDirectory(origin);
        Directory.CreateDirectory(repo);
        Git(origin, "init", "-q", "--bare");
        Git(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "first");
        Git(repo, "remote", "add", "origin", origin);
        Git(repo, "push", "-q", "-u", "origin", "main");
        File.WriteAllText(Path.Combine(repo, "b.txt"), "b\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "second");
        var worktree = Path.Combine(root, "app-feature");
        Git(repo, "worktree", "add", "-q", "-b", "feature/x", worktree);
        File.AppendAllText(Path.Combine(repo, "README.md"), "more\n");
        File.WriteAllText(Path.Combine(repo, "staged.txt"), "s\n");
        Git(repo, "add", "staged.txt");
        File.WriteAllText(Path.Combine(repo, "untracked.txt"), "u\n");
        return (repo, worktree);
    }
}
