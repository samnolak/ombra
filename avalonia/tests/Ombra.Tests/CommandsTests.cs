using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>Slash commands from omp's catalog, local command output, todos and context usage.</summary>
public sealed class CommandsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static async Task<(MainWindow w, MainViewModel vm, SessionController s)> OpenAsync(string scenario = "normal")
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("cmd-sessions")), new LaunchRequest(TestProcesses.TempDir("cmd-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (w, vm, s);
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
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

    [Fact]
    public async Task The_catalog_arrives_at_start_and_local_commands_print_output_without_a_run()
    {
        await using var s = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("c")), new LaunchRequest(TestProcesses.TempDir("p")));
        await s.StartAsync();
        var cmds = s.Snapshot().Commands;
        Assert.Contains(cmds, c => c.Name == "compact");
        Assert.Equal(["list", "enable"], cmds.Single(c => c.Name == "mcp").Subcommands.Select(x => x.Name));
        Assert.Equal(["m"], cmds.Single(c => c.Name == "model").Aliases);

        await s.PromptAsync("/stats");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.Items.OfType<CommandOutputItem>().Any(), Wait, "output");
        Assert.StartsWith("Messages:", snap.Items.OfType<CommandOutputItem>().Single().Text);

        await s.PromptAsync("/rename Parser work");
        await TestProcesses.Eventually(s.Snapshot, x => x.SessionName == "Parser work", Wait, "renamed by command");
    }

    [Fact]
    public async Task Todos_and_context_usage_come_from_get_state_after_the_todo_tool()
    {
        await using var s = new SessionController(TestProcesses.FakeFactory("todo", TestProcesses.TempDir("c")), new LaunchRequest(TestProcesses.TempDir("p")));
        await s.StartAsync();
        Assert.Empty(s.Snapshot().Todos);
        Assert.Equal(1.25, s.Snapshot().ContextPercent);
        await s.PromptAsync("plan it");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Todos.Count == 1, Wait, "todos");
        Assert.Equal(["completed", "in_progress", "pending"], snap.Todos[0].Tasks.Select(t => t.Status));
    }

    [Theory]
    [InlineData("auto_compaction_end")]
    [InlineData("session_settled")]
    public async Task Compaction_and_settlement_refresh_indicators_without_settling_pending_input(string eventType)
    {
        await using var s = new SessionController(TestProcesses.Fake("compaction-refresh"));
        await s.StartAsync();
        s.Mutate(state =>
        {
            state.Enqueue(QueueKind.FollowUp, "next input", []);
            state.BeginRun(DateTimeOffset.UtcNow);
        });
        await s.Connection!.RequestAsync("test_state_refresh", w => w.WriteString("event", eventType));
        var refreshed = await TestProcesses.Eventually(s.Snapshot, x => x.ContextPercent == 20, Wait, "authoritative context");
        Assert.Equal(SessionPhase.Running, refreshed.Phase);
        Assert.Equal("next input", Assert.Single(refreshed.Queued).Text);
        Assert.Equal(0, refreshed.RunsEnded);
    }

    [Theory]
    [InlineData(false, false, "compaction-refresh")]
    [InlineData(true, false, "compaction-refresh")]
    [InlineData(true, true, "compaction-refresh")]
    [InlineData(false, false, "compaction-refresh-failed")]
    [InlineData(true, true, "compaction-refresh-failed")]
    public async Task Manual_compaction_refreshes_without_auto_compaction_or_agent_end(bool intercepted, bool immediate, string scenario)
    {
        await using var s = new SessionController(TestProcesses.Fake(scenario));
        await s.StartAsync();
        var command = immediate ? "/compact immediate" : "/compact";
        var printed = "";
        if (intercepted)
        {
            var result = await s.RunSlashCommandAsync(command);
            Assert.True(result.Ok);
            printed = result.Output;
        }
        else await s.PromptAsync(command);
        var refreshed = await TestProcesses.Eventually(s.Snapshot, x => x.ContextPercent == 0.25, Wait, "manual compaction context");
        Assert.Equal(SessionPhase.Ready, refreshed.Phase);
        Assert.DoesNotContain(refreshed.DebugTail, d => d.Type is "auto_compaction_end" or "agent_end");
        if (SessionOutputs.ParseCompactEnd(printed) is null)
            await TestProcesses.Eventually(s.Snapshot,
                x => x.Items.OfType<CommandOutputItem>().Any(item => SessionOutputs.ParseCompactEnd(item.Text) is not null),
                Wait, "visible manual compaction completion");
    }

    [Theory]
    [InlineData("compaction-refresh")]
    [InlineData("compaction-refresh-failed")]
    public async Task Retired_command_interceptor_forwards_background_output_and_refreshes_state(string scenario)
    {
        var clock = new CommandCaptureClock();
        await using var s = new SessionController(TestProcesses.Fake(scenario), clock: clock);
        await s.StartAsync();
        var conn = s.Connection!;
        Func<string, System.Text.Json.JsonElement, bool>? retired = null;
        // BeginRun reads the clock after installing the hook, before sending the command.
        clock.Capture = () => { if (conn.Intercept is { } capture) retired = capture; };
        var immediate = await s.RunSlashCommandAsync("/compact immediate");
        clock.Capture = null;
        Assert.True(immediate.Ok);
        Assert.NotNull(SessionOutputs.ParseCompactEnd(immediate.Output));
        Assert.NotNull(retired);
        await TestProcesses.Eventually(s.Snapshot, x => x.ContextPercent == 0.25, Wait, "first compact refresh");
        await conn.RequestAsync("test_state_refresh");
        await TestProcesses.Eventually(s.Snapshot, x => x.ContextPercent == 20, Wait, "reset authoritative context");

        // Model the read loop retaining the delegate just before the command seals its output.
        // A later actual protocol frame must not be consumed into that already-returned buffer.
        conn.Intercept = retired;
        try
        {
            await conn.PromptAsync("/compact");
            var visible = await TestProcesses.Eventually(s.Snapshot,
                x => x.ContextPercent == 0.25 && x.Items.OfType<CommandOutputItem>().Any(),
                Wait, "late output and automatic refresh");
            var output = Assert.Single(visible.Items.OfType<CommandOutputItem>());
            Assert.Equal(immediate.Output, output.Text);
            Assert.Equal(SessionPhase.Ready, visible.Phase);
        }
        finally { conn.Intercept = null; }
    }

    private sealed class CommandCaptureClock : TimeProvider
    {
        public Action? Capture;
        public override DateTimeOffset GetUtcNow()
        {
            Volatile.Read(ref Capture)?.Invoke();
            return TimeProvider.System.GetUtcNow();
        }
    }

    [Fact]
    public async Task Refresh_bursts_discard_the_obsolete_answer_and_coalesce_into_one_followup()
    {
        await using var s = new SessionController(TestProcesses.Fake("compaction-refresh-race"));
        await s.StartAsync();
        await s.Connection!.RequestAsync("test_state_refresh", w => w.WriteNumber("percent", 90));
        var held = await TestProcesses.Eventually(s.Snapshot,
            x => x.Items.OfType<NoticeItem>().Any(n => n.Text == "refresh held"), Wait, "followup refresh held");
        Assert.Equal(1.25, held.ContextPercent); // the obsolete 90% reply must never be published
        Assert.Equal("new/model", held.Model);
        Assert.Equal("high", held.ThinkingLevel);
        Assert.Equal("compacted", held.SessionName);
        var release = await s.Connection.RequestAsync("test_release_refresh");
        Assert.Equal(3, release.Data!.Value.GetProperty("stateCalls").GetInt32()); // startup + old + one followup
        await TestProcesses.Eventually(s.Snapshot, x => x.ContextPercent == 20, Wait, "latest context");
    }

    [Fact]
    public async Task Background_compaction_refresh_is_private_to_its_logical_session()
    {
        await using var foreground = new SessionController(TestProcesses.Fake("compaction-refresh"));
        await foreground.StartAsync();
        await using var background = foreground.Sibling(new LaunchRequest());
        await background.StartAsync();
        await background.PromptAsync("/compact");
        await TestProcesses.Eventually(background.Snapshot, x => x.ContextPercent == 0.25, Wait, "background context");
        Assert.Equal(1.25, foreground.Snapshot().ContextPercent);
        Assert.Equal(SessionPhase.Ready, foreground.Snapshot().Phase);
    }

    [AvaloniaFact]
    public async Task Typed_compaction_updates_the_visible_context_indicator_without_a_new_turn()
    {
        var (w, vm, _) = await OpenAsync("compaction-refresh");
        try
        {
            vm.ComposerText = "/compact";
            vm.SendCommand.Execute(null);
            await Until(() => vm.Session.Snapshot().ContextPercent == 0.25 && vm.Usage.Percent == 0.25, "compacted indicator");
            Assert.Equal(SessionPhase.Ready, vm.Phase);
        }
        finally { await vm.DisposeAsync(); w.Close(); }
    }

    [AvaloniaFact]
    public async Task Slash_menu_filters_navigates_accepts_and_offers_subcommands()
    {
        var (w, vm, _) = await OpenAsync();
        var composer = w.FindControl<TextBox>("Composer")!;
        composer.Focus();
        w.KeyTextInput("/m");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsCommandMenuOpen);
        Assert.Equal(["/mcp", "/model"], vm.CommandSuggestions.Select(c => c.Label));
        Assert.True(w.FindControl<Border>("CommandMenu")!.IsEffectivelyVisible);

        w.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Assert.True(vm.CommandSuggestions[1].IsSelected);
        w.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        w.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("/mcp ", vm.ComposerText);
        Assert.Equal(["/mcp list", "/mcp enable"], vm.CommandSuggestions.Select(c => c.Label)); // subcommands next
        Assert.Equal(vm.ComposerText.Length, composer.CaretIndex);

        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(vm.IsCommandMenuOpen);
        Assert.Equal(SessionPhase.Ready, vm.Phase); // Esc closed the menu, nothing else

        vm.ComposerText = "";
        composer.Focus();
        w.KeyTextInput("/sta");
        Dispatcher.UIThread.RunJobs();
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); // accepts "/stats "
        Assert.Equal("/stats ", vm.ComposerText);
        Assert.False(vm.IsCommandMenuOpen);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); // sends
        await Until(() => vm.Rows.OfType<CommandOutputRowViewModel>().Any() && vm.Phase == SessionPhase.Ready, "command output");

        // Regression (acceptance run): a command typed out in full ran only on a second Enter — the first one
        // "completed" it to itself. One Enter sends it now.
        w.KeyTextInput("/stats");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsCommandMenuOpen);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", vm.ComposerText);
        // Regression: every command output got key 0, so the view showed only the first one.
        await Until(() => vm.Rows.OfType<CommandOutputRowViewModel>().Count() == 2 && vm.Phase == SessionPhase.Ready, "second command output");
        Assert.Equal("", vm.ComposerText);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Todo_card_and_context_usage_are_shown()
    {
        var (w, vm, _) = await OpenAsync("todo");
        Assert.Contains(Loc.F("context {0:0}%", 1), vm.StatusDetail);
        vm.ComposerText = "plan it";
        vm.SendCommand.Execute(null);
        await Until(() => vm.HasTodos && vm.Phase == SessionPhase.Ready, "todos shown");
        Assert.Equal(Loc.F("Tasks {0}/{1}", 1, 3), vm.TodoSummary);
        Assert.Equal(["✓", "◐", "○"], vm.TodoPhases.Single().Tasks.Select(t => t.Mark));
        Assert.True(w.FindControl<Border>("TodoCard")!.IsEffectivelyVisible);
        await vm.DisposeAsync();
        w.Close();
    }

    [Fact]
    public async Task Every_transcript_item_gets_its_own_key()
    {
        await using var s = new SessionController(TestProcesses.Fake("normal"));
        await s.StartAsync();
        await s.PromptAsync("/stats");
        await TestProcesses.Eventually(s.Snapshot, x => x.Items.OfType<CommandOutputItem>().Count() == 1, TimeSpan.FromSeconds(20), "first output");
        await s.PromptAsync("/stats");
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Items.OfType<CommandOutputItem>().Count() == 2, TimeSpan.FromSeconds(20), "second output");
        Assert.Equal(snap.Items.Count, snap.Items.Select(i => i.Key).Distinct().Count());
        Assert.DoesNotContain(snap.Items, i => i.Key == 0);
    }

    [Fact]
    public async Task A_state_answer_it_cannot_read_does_not_stop_later_refreshes()
    {
        // Regression (review round 4): a get_state without data threw outside the caught types; the refresh loop ended
        // with its counter set and todos / context usage never refreshed again.
        await using var s = new SessionController(TestProcesses.Fake("todo-state-glitch"));
        await s.StartAsync();
        await s.PromptAsync("plan it");   // the todo tool ends: refresh #1 gets no data
        await TestProcesses.Eventually(s.Snapshot, x => x.Phase == SessionPhase.Ready && x.DebugTail.Any(d => d.Type == "state_refresh_error"), TimeSpan.FromSeconds(20), "unreadable answer seen");
        await s.PromptAsync("plan again"); // refresh #2 must still happen
        var snap = await TestProcesses.Eventually(s.Snapshot, x => x.Todos.Count > 0, TimeSpan.FromSeconds(20), "todos refreshed");
        Assert.Equal("Change greet", snap.Todos.SelectMany(p => p.Tasks).Single(t => t.Status == "in_progress").Content);
    }

    [Fact]
    public void Todo_phases_that_are_not_objects_are_skipped()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""[1, "x", {"name":"P","tasks":[2, {"content":"t","status":"pending"}]}]""");
        var phase = Assert.Single(ConversationState.ParseTodos(doc.RootElement));
        Assert.Equal("t", Assert.Single(phase.Tasks).Content);
    }
}
