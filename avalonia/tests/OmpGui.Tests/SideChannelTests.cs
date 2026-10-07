using System.Diagnostics;
using System.Text.Json;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// What the app asks omp on the side never shows in the conversation: the settings pages' reads run as omp's local
/// builtins (no turn, no user message, no new title), a builtin this omp does not list is never sent (it would reach
/// the model as a message), an optional query an older omp lacks fails quietly, and one folder reached by two paths
/// (macOS /var → /private/var, a trailing slash, another case) is one project.
/// </summary>
public sealed class SideChannelTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-10-07T12:00:00Z");

    private sealed record Setup(MainViewModel Vm, SessionController Session, string Log)
    {
        public IReadOnlyList<string> Sent => File.Exists(Log) ? File.ReadAllLines(Log) : [];
    }

    private static async Task<Setup> StartAsync()
    {
        var dir = TestProcesses.TempDir("side");
        var project = Path.Combine(dir, "project");
        Directory.CreateDirectory(project);
        var commands = Path.Combine(dir, "commands.json");
        File.WriteAllText(commands, "{}"); // nothing canned: omp's own answers
        var log = Path.Combine(dir, "sent.log");
        var sessions = Path.Combine(dir, "sessions");
        var s = new SessionController(req =>
        {
            var spec = TestProcesses.FakeWithCommands("normal", commands, log);
            return spec with
            {
                WorkingDirectory = req.WorkingDirectory,
                Environment = new Dictionary<string, string?>(spec.Environment) { ["FAKE_SESSION_DIR"] = sessions },
            };
        }, new LaunchRequest(project));
        var vm = new MainViewModel(s, new AppArgs()) { OmpCliLaunch = TestProcesses.FakeCli(Path.Combine(dir, "cli.json"), Path.Combine(dir, "cli.log")) };
        File.WriteAllText(Path.Combine(dir, "cli.json"), "{}");
        await s.StartAsync();
        return new(vm, s, log);
    }

    [Fact]
    public async Task Settings_pages_read_omp_without_a_turn_a_message_or_a_new_title()
    {
        var t = await StartAsync();
        await using var _ = t.Session;
        await t.Vm.Connectors.RefreshAsync();
        await t.Vm.SshSettings.LoadAsync();
        await t.Vm.ComputerSettings.LoadAsync();

        Assert.Equal(["/mcp list", "/tools", "/ssh list", "/computer status", "/tools"], t.Sent);
        var snap = t.Session.Snapshot();
        Assert.Empty(snap.Items.OfType<UserItem>());
        Assert.Empty(snap.Items.OfType<AssistantItem>());
        Assert.Null(snap.SessionName);
        Assert.Equal(SessionPhase.Ready, snap.Phase);
        Assert.Equal("", t.Vm.Connectors.LoadError);
        Assert.True(t.Vm.Connectors.ShowEmpty);
        Assert.Equal("", t.Vm.SshSettings.LoadError);
        Assert.True(t.Vm.SshSettings.IsEmpty);
        Assert.False(t.Vm.SshSettings.ReadOnly);
    }

    [Fact]
    public async Task A_builtin_this_omp_does_not_list_is_never_sent()
    {
        var t = await StartAsync();
        await using var _ = t.Session;
        // The fake omp of the "normal" scenario does not list /jobs (an omp without it would pass it to the model)
        var r = await t.Session.RunSlashCommandAsync("/jobs");
        Assert.False(r.Ok);
        Assert.True(r.Unsupported);
        Assert.DoesNotContain("/jobs", t.Sent);
        Assert.Empty(t.Session.Snapshot().Items.OfType<UserItem>());
        Assert.Equal(SessionPhase.Ready, t.Session.Snapshot().Phase);
    }

    [Fact]
    public void A_catalog_decides_which_builtins_run_and_an_empty_one_falls_back_to_the_known_list()
    {
        var catalog = new List<SlashCommand>
        {
            new("mcp", null, null, "builtin", [], []),
            new("wt", null, null, "builtin", ["worktree"], []),
            new("skill:x", null, null, "skill", [], []),
        };
        Assert.True(OmpBuiltins.RunsOverRpc("/mcp list", catalog));
        Assert.True(OmpBuiltins.RunsOverRpc("/worktree feature", catalog)); // an alias
        Assert.False(OmpBuiltins.RunsOverRpc("/ssh list", catalog));
        Assert.False(OmpBuiltins.RunsOverRpc("/plan", catalog)); // terminal-only, whatever the catalog
        Assert.True(OmpBuiltins.RunsOverRpc("/ssh list", []));
    }

    [Fact]
    public void An_older_omp_without_an_optional_query_shows_no_error()
    {
        var s = new ConversationState();
        s.Apply(F("""{"type":"_unmatched_response","command":"get_available_thinking_levels","success":false,"error":"Unknown command: get_available_thinking_levels"}"""), T);
        Assert.Empty(s.Snapshot().Items);
        // A real failure of the same query still says so
        s.Apply(F("""{"type":"_unmatched_response","command":"get_available_thinking_levels","success":false,"error":"Model registry is not ready"}"""), T);
        Assert.Single(s.Snapshot().Items.OfType<NoticeItem>());
    }

    [Fact]
    public async Task Thinking_levels_from_an_omp_without_them_fall_back_quietly()
    {
        var t = await StartAsync();
        await using var _ = t.Session;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await t.Session.GetThinkingLevelsAsync(cts.Token); }
        catch (OperationCanceledException) { }
        Assert.Empty(t.Session.Snapshot().Items.OfType<NoticeItem>());
    }

    [Fact]
    public void One_folder_by_two_paths_is_one_project()
    {
        var dir = TestProcesses.TempDir("paths");
        var real = Path.Combine(dir, "Real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(dir, "link");
        Directory.CreateSymbolicLink(link, real);

        Assert.True(SessionCatalog.PathComparer.Equals(real, link));
        Assert.True(SessionCatalog.PathComparer.Equals(real, real + Path.DirectorySeparatorChar));
        Assert.Equal(SessionCatalog.PathComparer.GetHashCode(real), SessionCatalog.PathComparer.GetHashCode(link + Path.DirectorySeparatorChar));
        Assert.False(SessionCatalog.PathComparer.Equals(real, dir));
        var hidden = new HashSet<string>([link], SessionCatalog.PathComparer);
        Assert.Contains(real, hidden);
        // Another case names the same folder only where the volume ignores case
        var other = Path.Combine(dir, "real");
        Assert.Equal(Directory.Exists(other), SessionCatalog.PathComparer.Equals(real, other));
        if (OperatingSystem.IsMacOS())
            Assert.True(SessionCatalog.PathComparer.Equals("/var/tmp", "/private/var/tmp"));
    }

    private static RpcFrame F(string json)
    {
        var e = JsonDocument.Parse(json).RootElement.Clone();
        return new RpcFrame(e.GetProperty("type").GetString()!, null, e, Stopwatch.GetTimestamp(), json.Length + 1);
    }
}
