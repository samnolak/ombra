using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.App.Views.Session;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// What omp 18.2.0 prints for the Session area's builtins over RPC (captured from the real omp: SP/real-cmds.txt and
/// the Session area's own runs), used by the parsers' tests and as the fake omp's answers.
/// </summary>
internal static class SessionFixtures
{
    public const string Context =
        "Context window: 131072 tokens (13% used)\n" +
        "  System prompt    [█░░░░░░░░░░░░░░░░░░░░░░░] 3%  4518 tokens\n" +
        "  System tools     [██░░░░░░░░░░░░░░░░░░░░░░] 8%  11000 tokens\n" +
        "  System context   [░░░░░░░░░░░░░░░░░░░░░░░░] 1%  1478 tokens\n" +
        "  Skills           [░░░░░░░░░░░░░░░░░░░░░░░░] 0%  476 tokens\n" +
        "  Auto-compact buf [████░░░░░░░░░░░░░░░░░░░░] 15%  19660 tokens\n" +
        "  Free             [█████████████████░░░░░░░] 72%  93940 tokens";

    public static string Commands(string project) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["/context"] = Context,
        ["/compact"] = new { text = "", later = "Compaction complete. Tokens: 48210 -> 9120 (saved 39090).", laterMs = 400 },
        ["/handoff"] = new { text = "", later = "Context handed off and compacted in place.", laterMs = 300 },
        ["/fresh"] = "Fresh provider session started (1 provider state pruned).",
        ["/retry"] = new { text = "Retrying the last failed turn.", agentInvoked = true },
        ["/export"] = "Session exported to: omp-session-test.html",
        ["/share"] = "Share URL: https://my.omp.sh/s/Abc123#k3y",
        ["/dirs"] = $"Workspace directories:\n  {project} (working directory)",
        ["/add-dir"] = $"Added /tmp/extra-lib.\nWorkspace directories:\n  {project} (working directory)\n  /tmp/extra-lib",
        ["/remove-dir"] = $"Removed /tmp/extra-lib.\nWorkspace directories:\n  {project} (working directory)",
        ["/pin"] = "Session pinned to the top of the resume list.",
        ["/stats"] = "Syncing session files...\fSynced 3 new entries from 2 files (40 total)\nDashboard available at: http://127.0.0.1:3847",
        ["/memory stats"] = "Memory backend is off — there is nothing to show.",
        ["/extended-context status"] = "Extended context is off.",
        ["/extended-context on"] = "Extended context enabled.",
        ["/extended-context off"] = "Extended context disabled.",
        ["/advisor status"] = "Advisor is disabled.",
        ["/advisor on"] = "Advisor setting enabled, but no model is assigned to the 'advisor' role.",
        ["/advisor off"] = "Advisor disabled.",
        ["/session delete"] = "Session deleted: /tmp/x.jsonl. Use ACP `session/load` to switch to another session.",
    });
}

internal static class SessionCardActionExtensions
{
    /// <summary>Runs a card button's command to its end, async or not.</summary>
    public static Task Run(this SessionCardAction? a) =>
        a?.Command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand async ? async.ExecuteAsync(null)
        : Task.Run(() => Dispatcher.UIThread.Invoke(() => a!.Command.Execute(null)));
}

/// <summary>The parsers of what omp prints (pure).</summary>
public sealed class SessionOutputTests
{
    [Fact]
    public void Context_breakdown_is_read_from_omps_bars()
    {
        var r = SessionOutputs.ParseContext(SessionFixtures.Context)!;
        Assert.Equal(131072, r.Window);
        Assert.Equal(13, r.UsedPercent);
        Assert.Equal(["System prompt", "System tools", "System context", "Skills", "Auto-compact buffer", "Free"], r.Categories.Select(c => c.Label));
        Assert.Equal(4518, r.Categories[0].Tokens);
        Assert.Equal(93940, r.Categories[^1].Tokens);
        Assert.Equal(4518 + 11000 + 1478 + 476, r.UsedTokens);
        Assert.Equal("Context usage is unavailable: no model is selected for this session.",
            SessionOutputs.ParseContext("Context usage is unavailable: no model is selected for this session.")!.Unavailable);
        var fallback = SessionOutputs.ParseContext("Context\nWindow: 200000\nUsed: 50000")!;
        Assert.Equal(25, fallback.UsedPercent);
        Assert.Null(SessionOutputs.ParseContext("something else"));
    }

    [Fact]
    public void Background_results_and_one_line_answers_are_recognised()
    {
        Assert.Equal(new MaintenanceResult(true, "Compaction complete. Tokens: 48210 -> 9120 (saved 39090).", 48210, 9120),
            SessionOutputs.ParseCompactEnd("Compaction complete. Tokens: 48210 -> 9120 (saved 39090)."));
        Assert.Equal(new MaintenanceResult(false, "Nothing to compact (session too small)"), SessionOutputs.ParseCompactEnd("Compaction failed: Nothing to compact (session too small)"));
        Assert.Null(SessionOutputs.ParseCompactEnd("Retrying the last failed turn."));
        Assert.True(SessionOutputs.ParseHandoffEnd("Context handed off and compacted in place.")!.Ok);
        Assert.Equal("Nothing to hand off (already compacted)", SessionOutputs.ParseHandoffEnd("Handoff failed: Nothing to hand off (already compacted)")!.Message);

        var (cwd, added, note) = SessionOutputs.ParseDirectories("Added /b.\nWorkspace directories:\n  /a (working directory)\n  /b");
        Assert.Equal(("/a", "Added /b."), (cwd, note));
        Assert.Equal(["/b"], added);
        Assert.Equal("omp-session-x.html", SessionOutputs.ParseExportPath("Session exported to: omp-session-x.html"));
        Assert.Equal(("https://my.omp.sh/s/a#k", null, "large content was trimmed to fit the share size limit."),
            SessionOutputs.ParseShare("Share URL: https://my.omp.sh/s/a#k\nNote: large content was trimmed to fit the share size limit."));
        Assert.Equal("http://127.0.0.1:3847", SessionOutputs.ParseDashboardUrl("Syncing session files...\nSynced 1 new entries\nDashboard already running at: http://127.0.0.1:3847 (requested 0.0.0.0:1 ignored)"));
        Assert.True(SessionOutputs.ParseOnOff("Fast mode is on.", "Fast mode"));
        Assert.False(SessionOutputs.ParseOnOff("Extended context disabled.", "Extended context"));
        Assert.Null(SessionOutputs.ParseOnOff("Fast mode is unavailable for the current model.", "Fast mode"));
        Assert.Equal(AdvisorState.NeedsModel, SessionOutputs.ParseAdvisor("Advisor setting is enabled, but no model is assigned to the 'advisor' role."));
        Assert.Equal(AdvisorState.On, SessionOutputs.ParseAdvisor("Advisor is enabled (anthropic/claude-x). Context: 1 / 2 tokens (50%). Spend: 1 input, 2 output, $0.0000."));
        Assert.Equal("anthropic/claude-x", SessionOutputs.AdvisorModel("Advisor is enabled (anthropic/claude-x). Context: 1 tokens."));
        Assert.Equal(AdvisorState.Off, SessionOutputs.ParseAdvisor("Advisor is disabled."));
    }

    [Theory]
    [InlineData("/install foo@market")]
    [InlineData("/append a task")]
    [InlineData("/smithery-search github")]
    public void Subcommands_are_not_builtins_and_are_never_run_as_one(string text)
    {
        // omp 18.2.0 has no top-level /install, /append or /smithery-search: sent, they would reach the model
        Assert.False(OmpBuiltins.RunsOverRpc(text));
        Assert.True(OmpBuiltins.RunsOverRpc("/marketplace install foo@market"));
        Assert.True(OmpBuiltins.RunsOverRpc("/todo append a task"));
        Assert.True(OmpBuiltins.RunsOverRpc("/mcp smithery-search github"));
    }

    [Fact]
    public void The_terminal_only_table_covers_exactly_omps_terminal_only_builtins()
    {
        Assert.Equal(OmpBuiltins.TuiOnly.OrderBy(x => x), TerminalOnlyCommands.All.Select(c => c.Name).OrderBy(x => x));
    }
}

/// <summary>The session menu, the context ring, the model options and terminal-only commands, in the real window.</summary>
public sealed class SessionAreaTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");

    internal sealed record Env(MainWindow W, MainViewModel Vm, string Log, string Project, string Cli);

    internal static async Task<Env> OpenAsync(string scenario = "normal", double width = 1180, double height = 760, Dictionary<string, object>? cli = null,
        Func<string, Dictionary<string, object>>? extraCommands = null, string? model = null)
    {
        var dir = TestProcesses.TempDir("session-area");
        var project = TestProcesses.TempDir("session-project");
        var sessions = Path.Combine(dir, "sessions");
        var commands = Path.Combine(dir, "commands.json");
        var log = Path.Combine(dir, "sent.log");
        var cliAnswers = Path.Combine(dir, "cli.json");
        var cliLog = Path.Combine(dir, "cli.log");
        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SessionFixtures.Commands(project))!
            .ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        foreach (var (k, v) in extraCommands?.Invoke(project) ?? []) map[k] = v;
        File.WriteAllText(commands, JsonSerializer.Serialize(map));
        File.WriteAllText(cliAnswers, JsonSerializer.Serialize(cli ?? new Dictionary<string, object>
        {
            ["config get retry.enabled --json"] = new { stdout = """{"key":"retry.enabled","value":true,"type":"boolean"}""" },
            ["config get share.serverUrl --json"] = new { stdout = """{"key":"share.serverUrl","value":"https://my.omp.sh/s"}""" },
            ["config get share.store --json"] = new { stdout = """{"key":"share.store","value":"blob"}""" },
            ["config get share.redactSecrets --json"] = new { stdout = """{"key":"share.redactSecrets","value":true}""" },
        }));
        Func<LaunchRequest, OmpLaunchSpec> launch = req =>
        {
            var spec = TestProcesses.FakeFactory(scenario, sessions)(req);
            return spec with
            {
                Environment = new Dictionary<string, string?>(spec.Environment) { ["FAKE_OMP_COMMANDS"] = commands, ["FAKE_OMP_COMMAND_LOG"] = log, ["FAKE_MODEL"] = model },
            };
        };
        var s = new SessionController(launch, new LaunchRequest(project));
        var vm = new MainViewModel(s, new AppArgs()) { OmpCliLaunch = TestProcesses.FakeCli(cliAnswers, cliLog) };
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return new Env(w, vm, log, project, cliLog);
    }

    internal static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    internal static async Task Settle(int ms = 250)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    internal static string[] Sent(Env e) => File.Exists(e.Log) ? File.ReadAllLines(e.Log) : [];

    internal static async Task Send(Env e, string text)
    {
        e.Vm.ComposerText = text;
        e.Vm.SendCommand.Execute(null);
        await Settle(100);
    }

    internal static async Task Close(Env e)
    {
        await e.Vm.DisposeAsync();
        e.W.Close();
    }

    [AvaloniaFact]
    public async Task The_title_opens_the_session_menu_and_each_item_sends_its_builtin()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        await Send(e, "hello there");
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(), "first reply");

        var title = e.W.GetVisualDescendants().OfType<SessionTitle>().Single();
        title.Button.Flyout!.ShowAt(title.Button);
        await Settle();
        var items = title.Button.Flyout is Flyout { Content: Control menu } ? menu.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("menu-item")).ToList() : [];
        Assert.Equal(["Rename", "Copy session ID", "Copy session file path", "Compact conversation", "Hand off", "Fresh provider session", "Retry the last turn",
            "Rewind to an earlier message", "Export as HTML", "Share a link", "Workspace folders", "Move to another folder", "Pin session", "Memory", "Delete session"],
            items.Select(b => Avalonia.Automation.AutomationProperties.GetName(b)));
        Assert.All(items, b => Assert.True(b.IsEffectivelyEnabled, Avalonia.Automation.AutomationProperties.GetName(b)));
        items.Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Rename").Command!.Execute(null);
        Assert.True(vm.IsRenaming);
        vm.IsRenaming = false;
        title.Button.Flyout.Hide();

        // Compact: asks for an optional focus, runs in the background, reports omp's result line
        vm.CompactConversationCommand.Execute(null);
        Assert.Equal(SessionCardState.Ask, vm.SessionCard!.State);
        vm.SessionCard.Input = "keep the parser notes";
        await vm.SessionCard.Primary.Run();
        Assert.Equal("Conversation compacted", vm.SessionCard.Title);
        Assert.Equal("The context went from 48,210 to 9,120 tokens (39,090 freed).", vm.SessionCard.Message);
        Assert.Contains("/compact keep the parser notes", Sent(e));

        vm.HandOffCommand.Execute(null);
        await vm.SessionCard!.Primary.Run();
        Assert.Equal("Handed off", vm.SessionCard.Title);
        Assert.Contains("/handoff", Sent(e));

        await vm.FreshProviderSessionCommand.ExecuteAsync(null);
        Assert.Equal("Fresh provider session", vm.SessionCard!.Title);
        Assert.Contains("1 cached provider state(s) dropped", vm.SessionCard.Message);

        var copied = new List<string>();
        vm.CopyTextRequested += copied.Add;
        var opened = new List<string>();
        vm.OpenUrlRequested += opened.Add;
        await vm.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal("Exported as HTML", vm.SessionCard!.Title);
        Assert.Equal(Path.Combine(e.Project, "omp-session-test.html"), vm.SessionCard.Detail);
        vm.SessionCard.Secondary!.Command.Execute(null); // Show in folder
        Assert.Equal(new Uri(e.Project).AbsoluteUri, opened.Last());

        // Share: says what it uploads and where before anything leaves the machine
        vm.ShareSessionCommand.Execute(null);
        Assert.Equal(SessionCardState.Ask, vm.SessionCard!.State);
        Assert.DoesNotContain("/share", Sent(e));
        await Until(() => vm.SessionCard.Message.Contains("my.omp.sh"), "share target from omp's settings");
        Assert.Contains("anyone who has the link can read it", vm.SessionCard.Message);
        await vm.SessionCard.Primary.Run();
        Assert.Equal("Link copied", vm.SessionCard.Title);
        Assert.Equal("https://my.omp.sh/s/Abc123#k3y", copied.Last());

        await vm.CopySessionIdCommand.ExecuteAsync(null);
        Assert.Equal("Session ID copied", vm.SessionCard!.Title);
        Assert.Matches("^[0-9a-f-]{36}$", copied.Last());
        vm.CopySessionFileCommand.Execute(null);
        Assert.EndsWith(".jsonl", copied.Last());

        // Workspace folders: list, add (the folder picker), remove
        await vm.WorkspaceFoldersCommand.ExecuteAsync(null);
        Assert.Equal([e.Project], vm.SessionCard!.Folders.Select(f => f.Path));
        vm.PickFolderRequested += () => Task.FromResult<string?>("/tmp/extra-lib");
        await vm.SessionCard.Primary.Run();
        Assert.Equal([e.Project, "/tmp/extra-lib"], vm.SessionCard.Folders.Select(f => f.Path));
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.SessionCard.Folders[1].Remove!).ExecuteAsync(null);
        Assert.Equal([e.Project], vm.SessionCard.Folders.Select(f => f.Path));
        Assert.Contains("/add-dir /tmp/extra-lib", Sent(e));
        Assert.Contains("/remove-dir /tmp/extra-lib", Sent(e));

        await vm.TogglePinCommand.ExecuteAsync(null);
        Assert.True(vm.IsSessionPinned);
        Assert.Equal("Unpin session", vm.PinLabel);

        await vm.OpenUsageDashboardCommand.ExecuteAsync(null);
        Assert.Equal("http://127.0.0.1:3847", vm.SessionCard!.Detail);
        await vm.ShowMemoryCommand.ExecuteAsync(null);
        Assert.Equal("Memory is off", vm.SessionCard!.Title);

        // Retry starts a run that shows like any other
        await vm.RetryLastTurnCommand.ExecuteAsync(null);
        await Until(() => vm.Rows.OfType<AssistantRowViewModel>().Count() >= 2 && vm.Phase == SessionPhase.Ready, "the retried turn");
        Assert.False(vm.HasSessionCard);
        Assert.Contains("/retry", Sent(e));
        await Close(e);
    }

    [AvaloniaFact]
    public async Task Rewind_starts_a_new_session_before_the_chosen_message_and_gives_it_back()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        await Send(e, "first question");
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(), "first reply");
        await Send(e, "second question");
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Count() == 2, "second reply");
        var before = vm.Session.Snapshot().SessionFile;

        await Send(e, "/rewind"); // omp's terminal-only /branch, /rewind: the GUI's Rewind
        await Until(() => vm.SessionCard is { Kind: "rewind", HasChoices: true }, "rewind choices");
        Assert.Equal(["second question", "first question"], vm.SessionCard!.Choices.Select(c => c.Label));
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.SessionCard.Choices[0].Command).ExecuteAsync(null);
        Assert.Equal("Rewound", vm.SessionCard.Title);
        await Until(() => vm.Rows.OfType<UserRowViewModel>().Select(u => u.Text).SequenceEqual(["first question"]), "history before the message");
        Assert.Equal("second question", vm.ComposerText);
        Assert.NotEqual(before, vm.Session.Snapshot().SessionFile);
        await Until(() => vm.Sessions.Count == 2, "the earlier session is kept");
        await Close(e);
    }

    [AvaloniaFact]
    public async Task Delete_asks_first_then_deletes_and_starts_a_new_session_and_drop_opens_it()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        await Send(e, "throwaway");
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(), "reply");
        var file = vm.Session.Snapshot().SessionFile;

        await Send(e, "/drop"); // omp's terminal-only /drop: the GUI's delete, which asks first
        Assert.Equal("delete", vm.SessionCard!.Kind);
        Assert.Equal(SessionCardState.Ask, vm.SessionCard.State);
        Assert.DoesNotContain("/session delete", Sent(e));
        await vm.SessionCard.Primary.Run();
        await Until(() => vm.Session.Snapshot().SessionFile != file && vm.Rows.Count == 0, "a new session");
        Assert.Equal("Session deleted", vm.SessionCard!.Title);
        Assert.Equal(["/session delete"], Sent(e));
        await Close(e);
    }

    [AvaloniaFact]
    public async Task Moving_the_session_follows_omp_to_the_new_folder()
    {
        var target = TestProcesses.TempDir("moved-here");
        var e = await OpenAsync(extraCommands: _ => new() { ["/move"] = $"Moved to {target}." });
        e.Vm.PickFolderRequested += () => Task.FromResult<string?>(target);
        await e.Vm.MoveSessionCommand.ExecuteAsync(null);
        Assert.Equal(SessionCardState.Ask, e.Vm.SessionCard!.State);
        await e.Vm.SessionCard.Primary.Run();
        Assert.Equal("Session moved", e.Vm.SessionCard.Title);
        await Until(() => e.Vm.ProjectPath == target, "project follows the move");
        Assert.Contains($"/move {target}", Sent(e));
        await Close(e);
    }

    [AvaloniaFact]
    public async Task The_context_ring_shows_usage_and_its_popover_draws_context_and_usage()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        await Until(() => vm.Usage.HasPercent, "context percent");
        Assert.Equal(1.25, vm.Usage.Percent);
        Assert.Equal("normal", vm.Usage.RingLevel);
        var ring = e.W.GetVisualDescendants().OfType<UsageIndicator>().Single();
        Assert.True(ring.Button.IsEffectivelyVisible);

        ring.Button.Command!.Execute(null);
        await Until(() => !vm.Usage.IsLoading && vm.Usage.HasContext, "context loaded");
        Assert.Equal("13% of 131k tokens used", vm.Usage.ContextHeadline);
        Assert.Equal(["System prompt", "System tools", "System context", "Skills", "Auto-compact buffer", "Free"], vm.Usage.ContextRows.Select(r => r.Label));
        Assert.Equal("4,518", vm.Usage.ContextRows[0].Tokens);
        Assert.True(vm.Usage.ContextRows[^1].IsFree);
        // get_session_stats: nothing spent before the first prompt (no cost row while it is zero)
        Assert.Equal(["Input", "Output"], vm.Usage.Totals.Select(t => t.Label));
        Assert.Equal("0", vm.Usage.Totals[0].Value);

        // Refreshes after each turn while open
        var before = Sent(e).Count(l => l == "/context");
        await Send(e, "one more");
        await Until(() => Sent(e).Count(l => l == "/context") > before && !vm.Usage.IsLoading, "refresh after the turn");
        await Until(() => vm.Usage.Totals.Any(t => t.Label == "Cost"), "the turn's tokens and cost");
        Assert.Equal(["Input", "Output", "Cache read", "Cost"], vm.Usage.Totals.Select(t => t.Label));
        Assert.Equal(("18,680", "$0.08"), (vm.Usage.Totals[0].Value, vm.Usage.Totals[^1].Value));
        Assert.DoesNotContain("/usage", Sent(e)); // never through the conversation's command channel

        // Compact now: straight to the running card, then omp's result
        await vm.Usage.CompactNowCommand.ExecuteAsync(null);
        Assert.False(vm.Usage.IsOpen);
        Assert.Equal("Conversation compacted", vm.SessionCard!.Title);
        Assert.Contains("/compact", Sent(e));
        await Close(e);
    }

    [Fact]
    public void The_ring_warns_near_the_limit()
    {
        var u = new UsageViewModel(null!);
        u.Percent = 69.9;
        Assert.Equal("normal", u.RingLevel);
        u.Percent = 70;
        Assert.Equal("warn", u.RingLevel);
        u.Percent = 85;
        Assert.Equal("full", u.RingLevel);
        Assert.Contains("compacts it soon", u.Tooltip);
    }

    [AvaloniaFact]
    public async Task Model_options_read_their_state_when_the_menu_opens_and_set_it()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        var o = vm.ModelOptions;
        await vm.OpenModelMenuCommand.ExecuteAsync(null);
        await Until(() => o.IsLoaded && !o.IsLoading, "options loaded");
        Assert.False(o.FastMode);
        Assert.False(o.ExtendedContext);
        Assert.False(o.Advisor);
        Assert.True(o.AutoCompaction);
        Assert.True(o.AutoRetry);
        Assert.True(o.AutoRetryKnown);
        Assert.Contains("/extended-context status", Sent(e));
        Assert.Contains("/advisor status", Sent(e));
        Assert.DoesNotContain(Sent(e), l => l.StartsWith("set_", StringComparison.Ordinal)); // reading changes nothing

        o.FastMode = true;
        await Until(() => Sent(e).Contains("set_fast_mode true"), "fast mode on");
        o.ExtendedContext = true;
        await Until(() => Sent(e).Contains("/extended-context on"), "extended context on");
        o.AutoCompaction = false;
        await Until(() => Sent(e).Contains("set_auto_compaction false"), "auto-compaction off");
        o.AutoRetry = false;
        await Until(() => Sent(e).Contains("set_auto_retry false"), "auto-retry off");
        o.Advisor = true;
        await Until(() => o.AdvisorNeedsModel, "advisor needs a model");
        Assert.True(o.Advisor);
        Assert.Equal("Needs a model for omp's advisor role.", o.AdvisorNote);

        // Read again: omp's state, not what the switches said
        vm.IsModelMenuOpen = false;
        await vm.OpenModelMenuCommand.ExecuteAsync(null);
        await Until(() => !o.IsLoading, "reloaded");
        Assert.True(o.FastMode);
        Assert.False(o.AutoCompaction);

        // A model without a priority tier: omp refuses fast mode, the switch goes back and says why
        vm.IsModelMenuOpen = false;
        await vm.ChooseModelCommand.ExecuteAsync(vm.Models.Single(m => m.Key == "fake/vision"));
        o.FastMode = false;
        await Until(() => Sent(e).Count(l => l == "set_fast_mode false") == 1, "fast off");
        o.FastMode = true;
        await Until(() => o.HasFastNote, "refused");
        Assert.False(o.FastMode);
        Assert.Equal("Not available for this model: it has no priority tier.", o.FastNote);
        await Close(e);
    }

    [AvaloniaFact]
    public async Task Terminal_only_commands_never_reach_omp_and_open_the_gui_equivalent()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        await Send(e, "/settings");
        Assert.True(vm.IsSettingsOpen);
        Assert.Equal("general", vm.SettingsCategory);
        vm.IsSettingsOpen = false;
        await Send(e, "/login");
        Assert.True(vm.IsSettingsOpen);
        Assert.Equal("providers", vm.SettingsCategory);
        vm.IsSettingsOpen = false;
        await Send(e, "/model");
        Assert.True(vm.IsModelMenuOpen);
        vm.IsModelMenuOpen = false;
        vm.IsSidebarVisible = false;
        await Send(e, "/resume parser");
        Assert.True(vm.IsSidebarVisible);
        Assert.Equal("parser", vm.SessionFilter);
        vm.SessionFilter = "";

        await Send(e, "/plan add a cache");
        Assert.Equal("terminal", vm.SessionCard!.Kind);
        Assert.Equal("/plan runs in omp's terminal", vm.SessionCard.Title);
        Assert.Equal("", vm.ComposerText);
        vm.SessionCard.Primary!.Command.Execute(null); // Open omp terminal (no TUI launch in tests: nothing opens)
        Assert.False(vm.HasSessionCard);
        await Send(e, "/quit");
        Assert.Equal("explain", vm.SessionCard!.Kind);
        Assert.Contains("Close the window", vm.SessionCard.Message);

        await Send(e, "/new");
        await Until(() => vm.Phase == SessionPhase.Ready, "new session");

        // Nothing of that reached omp (only the model menu read its options), and nothing went to the model as a message
        Assert.Equal(["/extended-context status", "/advisor status"], Sent(e));
        Assert.Empty(vm.Rows.OfType<UserRowViewModel>());

        // A skill (omp lists it itself) and an RPC builtin still go to omp
        await Send(e, "/skill:systematic-debugging look at it");
        await Until(() => vm.Rows.OfType<UserRowViewModel>().Any(), "skill sent");
        await Until(() => vm.Phase == SessionPhase.Ready, "skill run over");
        await Send(e, "/model fake/vision");
        await Until(() => Sent(e).Contains("/model fake/vision"), "model with a name goes to omp");
        await Close(e);
    }

    [AvaloniaFact]
    public async Task A_bang_command_runs_in_the_session_shell_and_can_be_stopped()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        await Send(e, "!git status");
        await Until(() => vm.Rows.OfType<ToolRowViewModel>().Any(t => t.IsDone), "shell row done");
        var row = vm.Rows.OfType<ToolRowViewModel>().Single();
        Assert.Equal(("shell", "git status"), (row.Name, row.Summary));
        Assert.Contains("ran: git status", row.Output);
        Assert.Empty(vm.Rows.OfType<UserRowViewModel>());
        Assert.False(vm.HasSessionCard);
        Assert.Equal(["bash git status"], Sent(e));

        await Send(e, "!sleep 30");
        await Until(() => vm.SessionCard is { Kind: "shell", IsRunning: true }, "running card");
        await vm.SessionCard!.Primary.Run();
        await Until(() => vm.Rows.OfType<ToolRowViewModel>().Count() == 2 && !vm.Rows.OfType<ToolRowViewModel>().Last().IsRunning, "stopped");
        Assert.False(vm.Rows.OfType<ToolRowViewModel>().Last().IsDone);

        await Send(e, "!!ls");
        Assert.Equal("!! runs in omp's terminal", vm.SessionCard!.Title);
        Assert.Equal(["bash git status", "bash sleep 30"], Sent(e));
        await Close(e);
    }

    [AvaloniaFact]
    public async Task The_slash_menu_lists_terminal_only_commands_last_under_their_heading()
    {
        var e = await OpenAsync();
        e.Vm.ComposerText = "/p";
        Dispatcher.UIThread.RunJobs();
        var list = e.Vm.CommandSuggestions.ToList();
        var firstTui = list.FindIndex(c => c.IsTerminalOnly);
        Assert.True(firstTui >= 0);
        Assert.All(list.Take(firstTui), c => Assert.False(c.IsTerminalOnly)); // what runs here comes first
        Assert.All(list.Skip(firstTui), c => Assert.True(c.IsTerminalOnly));
        Assert.True(list[firstTui].StartsTerminalGroup);
        Assert.Single(list, c => c.StartsTerminalGroup);
        Assert.Contains(list.Skip(firstTui), c => c.Label == "/plan");
        // The window's own answers are ordinary entries; omp's builtins carry no routing label
        e.Vm.ComposerText = "/sett";
        var settings = e.Vm.CommandSuggestions.Single(c => c.Label == "/settings");
        Assert.False(settings.IsTerminalOnly);
        Assert.False(settings.HasTag);
        e.Vm.ComposerText = "/comp";
        Assert.False(e.Vm.CommandSuggestions.Single(c => c.Label == "/compact").HasTag);
        // Commands the window does another way (quit, copy…) are not offered
        e.Vm.ComposerText = "/qui";
        Assert.DoesNotContain(e.Vm.CommandSuggestions, c => c.Label == "/quit");
        e.Vm.ComposerText = "";
        await Close(e);
    }

    // ───────────── Layout audit: the title menu, the session card, the usage popover, the model menu ─────────────

    private sealed class Audit
    {
        public readonly StringBuilder Report = new();
        public int Count;

        public void Take(Window w, string screen, Control? scope = null)
        {
            using (var frame = w.CaptureRenderedFrame())
            {
                if (Dir is not null)
                {
                    Directory.CreateDirectory(Path.Combine(Dir, "session"));
                    using var f = File.Create(Path.Combine(Dir, "session", screen + ".png"));
                    frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
            }
            var found = LayoutAudit.Run(w);
            if (scope is not null && TopLevel.GetTopLevel(scope) is not Window) found.AddRange(PopupAudit(scope, screen));
            Count += found.Count;
            Report.Append(LayoutAudit.Report(screen, found));
        }

        /// <summary>A popup has its own root: audit it as a window of its own, and save its picture next to the screen.</summary>
        private static List<LayoutAudit.Finding> PopupAudit(Control scope, string screen)
        {
            if (TopLevel.GetTopLevel(scope) is not { } root) return [];
            if (Dir is not null && root is Avalonia.Controls.Primitives.PopupRoot pr)
            {
                using var frame = pr.CaptureRenderedFrame();
                using var f = File.Create(Path.Combine(Dir, "session", screen + "-popup.png"));
                frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            return [];
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(DesignMatrix.Standard), MemberType = typeof(DesignMatrix))]
    public async Task Session_screens_are_aligned(string theme, double width, double height)
    {
        MainViewModel.ApplyTheme(theme);
        var a = new Audit();
        var tag = $"{theme}-{width}";
        try
        {
            var e = await OpenAsync("normal", width, height);
            var vm = e.Vm;
            await Send(e, "Explain the parser change");
            await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(), "reply");
            await Settle();

            var title = e.W.GetVisualDescendants().OfType<SessionTitle>().Single();
            title.Button.Flyout!.ShowAt(title.Button);
            await Settle(400);
            a.Take(e.W, $"{tag}-title-menu", (title.Button.Flyout as Flyout)!.Content as Control);
            title.Button.Flyout.Hide();
            await Settle(100);

            vm.CompactConversationCommand.Execute(null);
            await Settle();
            a.Take(e.W, $"{tag}-card-compact-ask");
            await vm.SessionCard!.Primary.Run();
            await Settle();
            a.Take(e.W, $"{tag}-card-compact-done");

            vm.ShareSessionCommand.Execute(null);
            await Until(() => vm.SessionCard!.Message.Contains("my.omp.sh"), "share text");
            await Settle();
            a.Take(e.W, $"{tag}-card-share-ask");
            await vm.SessionCard!.Primary.Run();
            await Settle();
            a.Take(e.W, $"{tag}-card-share-done");

            vm.PickFolderRequested += () => Task.FromResult<string?>("/tmp/extra-lib");
            await vm.WorkspaceFoldersCommand.ExecuteAsync(null);
            await vm.SessionCard!.Primary.Run();
            await Settle();
            a.Take(e.W, $"{tag}-card-folders");

            await vm.RewindCommand.ExecuteAsync(null);
            await Settle();
            a.Take(e.W, $"{tag}-card-rewind");

            await Send(e, "/goal ship it");
            await Settle();
            a.Take(e.W, $"{tag}-card-terminal-only");
            vm.CloseSessionCard();

            await Send(e, "!sleep 30");
            await Until(() => vm.SessionCard is { Kind: "shell", IsRunning: true }, "shell running");
            await Settle();
            a.Take(e.W, $"{tag}-card-shell-running");
            await vm.SessionCard!.Primary.Run();
            await Until(() => !vm.HasSessionCard, "shell stopped");
            await Send(e, "!git status");
            await Until(() => vm.Rows.OfType<ToolRowViewModel>().Any(t => t.Name == "shell" && t.IsDone), "shell done");
            foreach (var t in vm.Rows.OfType<ToolRowViewModel>()) t.IsExpanded = true;
            await Settle();
            a.Take(e.W, $"{tag}-shell-rows");

            var ring = e.W.GetVisualDescendants().OfType<UsageIndicator>().Single();
            vm.Usage.IsOpen = true;
            await Until(() => !vm.Usage.IsLoading && vm.Usage.HasContext, "usage loaded");
            await Settle(400);
            a.Take(e.W, $"{tag}-usage-popover", ring.Popup.Child as Control);
            vm.Usage.IsOpen = false;
            await Settle(100);

            await vm.OpenModelMenuCommand.ExecuteAsync(null);
            await Until(() => vm.ModelOptions.IsLoaded && !vm.ModelOptions.IsLoading, "options");
            vm.ModelOptions.Advisor = true;
            await Until(() => vm.ModelOptions.AdvisorNeedsModel, "advisor note");
            await Settle(400);
            a.Take(e.W, $"{tag}-model-menu-options", e.W.FindControl<Border>("ModelMenu"));
            vm.IsModelMenuOpen = false;
            await Close(e);
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
        if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "session-audit.tsv"), a.Report.ToString());
        Assert.True(a.Count == 0, $"{a.Count} layout findings:\n{a.Report}");
    }
}
