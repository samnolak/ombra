using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>omp's builtin slash commands run for a pane or a settings section, and omp's CLI subcommands.</summary>
public class OmpCommandTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_builtin_returns_what_it_printed_and_stays_out_of_the_conversation()
    {
        var dir = TestProcesses.TempDir("cmd");
        var answers = Path.Combine(dir, "commands.json");
        var log = Path.Combine(dir, "sent.log");
        File.WriteAllText(answers, """{ "/mcp list": "github  connected\fplaywright  disabled", "/mcp": "usage: /mcp list" }""");
        await using var s = new SessionController(TestProcesses.FakeWithCommands("normal", answers, log));
        await s.StartAsync();

        var r = await s.RunSlashCommandAsync("/mcp list");
        Assert.True(r.Ok, r.Error);
        Assert.False(r.AgentInvoked);
        Assert.Equal("github  connected\nplaywright  disabled", r.Output);
        Assert.Equal("usage: /mcp list", (await s.RunSlashCommandAsync("/mcp")).Output);

        // /stats is the fake's own builtin: the same path without a canned answer
        Assert.StartsWith("Messages:", (await s.RunSlashCommandAsync("/stats")).Output);
        var snap = s.Snapshot();
        Assert.Empty(snap.Items.OfType<CommandOutputItem>());
        Assert.Empty(snap.Items.OfType<UserItem>());
        Assert.Equal(SessionPhase.Ready, snap.Phase);

        // The conversation still gets the output of a command the user typed
        await s.PromptAsync("/stats");
        await TestProcesses.Eventually(s.Snapshot, x => x.Items.OfType<CommandOutputItem>().Any(), Wait, "typed command output");
        Assert.Equal(["/mcp list", "/mcp", "/stats", "/stats"], File.ReadAllLines(log));
    }

    [Fact]
    public async Task A_terminal_only_command_is_refused_and_never_sent()
    {
        var dir = TestProcesses.TempDir("cmd");
        var log = Path.Combine(dir, "sent.log");
        File.WriteAllText(Path.Combine(dir, "c.json"), "{}");
        await using var s = new SessionController(TestProcesses.FakeWithCommands("normal", Path.Combine(dir, "c.json"), log));
        await s.StartAsync();

        var r = await s.RunSlashCommandAsync("/settings");
        Assert.False(r.Ok);
        Assert.Contains("not available", r.Error);
        Assert.False((await s.RunSlashCommandAsync("hello")).Ok);
        Assert.False(File.Exists(log));
        Assert.Empty(s.Snapshot().Items.OfType<UserItem>());
    }

    [Fact]
    public async Task Nothing_runs_when_omp_is_not_running()
    {
        await using var s = new SessionController(TestProcesses.Fake("normal"));
        var r = await s.RunSlashCommandAsync("/usage");
        Assert.False(r.Ok);
        Assert.Equal("omp is not running", r.Error);
    }

    [Theory]
    [InlineData("/mcp add x --url http://a", "mcp", true)]
    [InlineData("  /usage", "usage", true)]
    [InlineData("/worktree feature", "worktree", true)]
    [InlineData("/plan", "plan", false)]
    [InlineData("/login", "login", false)]
    [InlineData("/skill:review", "skill:review", false)]
    public void Which_builtins_run_over_rpc(string text, string name, bool rpc)
    {
        Assert.Equal(name, OmpBuiltins.NameOf(text));
        Assert.Equal(rpc, OmpBuiltins.RunsOverRpc(text));
    }

    [Fact]
    public async Task The_cli_runs_with_the_sessions_launch_and_returns_its_output()
    {
        var dir = TestProcesses.TempDir("cli");
        var answers = Path.Combine(dir, "cli.json");
        var log = Path.Combine(dir, "cli.log");
        File.WriteAllText(answers, """{ "plugin list --json": { "stdout": "[{\"name\":\"omp-fmt\",\"enabled\":true}]" }, "config set": { "stderr": "bad key", "exit": 2 } }""");
        var launch = TestProcesses.FakeCli(answers, log);

        var list = await OmpCli.RunAsync(launch(["plugin", "list", "--json"], dir));
        Assert.True(list.Ok);
        Assert.Contains("\"omp-fmt\"", list.Stdout);
        var set = await OmpCli.RunAsync(launch(["config", "set", "nope", "1"], dir));
        Assert.Equal(2, set.ExitCode);
        Assert.Contains("bad key", set.Stderr);
        Assert.Equal(1, (await OmpCli.RunAsync(launch(["unknown"], dir))).ExitCode);
        Assert.Equal(["plugin list --json", "config set nope 1", "unknown"], File.ReadAllLines(log));

        var missing = await OmpCli.RunAsync(new OmpGui.Rpc.OmpLaunchSpec { FileName = Path.Combine(dir, "no-such-omp"), Arguments = [] });
        Assert.False(missing.Ok);
        Assert.Contains("could not start", missing.Stderr);
    }

    [Fact]
    public void The_cli_launch_keeps_binary_prefix_and_profile_but_drops_rpc_flags()
    {
        var o = new OmpRuntimeOptions { Command = "/opt/bun", PrefixArgs = ["/opt/omp/cli.js"], Profile = "work", Model = "a/b", ExtraArgs = ["--x"] };
        var spec = o.ToCliLaunchSpec(["config", "get", "computer.enabled", "--json"], "/proj");
        Assert.Equal("/opt/bun", spec.FileName);
        Assert.Equal(["/opt/omp/cli.js", "config", "get", "computer.enabled", "--json"], spec.Arguments);
        Assert.Equal("/proj", spec.WorkingDirectory);
        Assert.Equal("work", spec.Environment["OMP_PROFILE"]);
    }
}

/// <summary>The same against real omp (OMPGUI_TEST_CONFIG): what its builtins and CLI print, for the panes built on them.</summary>
public class RealOmpCommandTests(ITestOutputHelper log)
{
    [Fact]
    public async Task Real_omp_runs_builtins_over_rpc_and_its_cli()
    {
        if (TestProcesses.RealOmp() is not { } options)
        {
            Assert.Skip("OMPGUI_TEST_CONFIG is not set");
            return;
        }
        await using var s = new SessionController(options.ToLaunchSpec, new LaunchRequest(options.WorkingDirectory), TimeSpan.FromSeconds(90));
        await s.StartAsync();
        foreach (var c in new[] { "/usage", "/context", "/mcp list", "/plugins list", "/computer status", "/jobs", "/tools", "/session", "/ssh list", "/todo", "/fast status", "/dirs" })
        {
            var r = await s.RunSlashCommandAsync(c);
            log.WriteLine($"=== {c}  ok={r.Ok} agent={r.AgentInvoked} error={r.Error}\n{r.Output}\n");
            Assert.True(r.Ok, $"{c}: {r.Error}");
            Assert.False(r.AgentInvoked, c);
        }
        Assert.Empty(s.Snapshot().Items.OfType<UserItem>());
        foreach (var a in new[] { new[] { "plugin", "list", "--json" }, ["config", "list", "--json"], ["config", "get", "computer.enabled", "--json"] })
        {
            var r = await OmpCli.RunAsync(options.ToCliLaunchSpec(a, options.WorkingDirectory));
            log.WriteLine($"=== omp {string.Join(' ', a)}  exit={r.ExitCode}\n{(r.Stdout.Length > 3000 ? r.Stdout[..3000] + "…" : r.Stdout)}\n{r.Stderr}\n");
            Assert.True(r.Ok, string.Join(' ', a) + ": " + r.Stderr);
        }
    }
}
