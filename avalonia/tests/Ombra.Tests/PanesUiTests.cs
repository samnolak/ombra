using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.App.Views.Panes;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// The header's ⋯ menu (its Panels) and the right panel in a real window: the menu's items and ticks, the keyboard
/// shortcuts, hiding, and how the panel shares the window with the conversation at 1180, 900 and 640 px.
/// </summary>
public sealed class PanesUiTests
{
    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Settle(int ms = 200)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    internal static async Task<(MainWindow W, MainViewModel Vm)> OpenAsync(string scenario, double width, double height = 760)
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("panes-ui")), new LaunchRequest(TestProcesses.TempDir("panes-ui-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        await Settle();
        return (w, vm);
    }

    internal static async Task SendAsync(MainViewModel vm, string text, Func<bool> until)
    {
        vm.ComposerText = text;
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && until(), "reply to " + text);
        await Settle(300);
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new InvalidOperationException(name + " not found");

    internal static Button ViewsButton(MainWindow w) => Named<Ombra.App.Views.Session.SessionMenuButton>(w, "SessionMenu").Button;

    /// <summary>Opens the ⋯ menu; returns its Panels section.</summary>
    internal static StackPanel OpenViewsMenu(MainWindow w)
    {
        var button = ViewsButton(w);
        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        var content = (Control)((Flyout)button.Flyout).Content!;
        return Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(content).OfType<StackPanel>().Single(p => p.Name == "PanelItems");
    }

    private static Button Item(StackPanel menu, string name) => menu.Children.OfType<Button>().Single(b => b.Name == name);

    private static bool Ticked(Button item) => item.GetVisualDescendants().OfType<Icon>().Any(i => i.Classes.Contains("menu-check") && i.IsEffectivelyVisible);

    /// <summary>A real click in the middle of <paramref name="c"/> (a menu item lives in the flyout's own top level),
    /// scrolled into view first (the ⋯ menu scrolls when it is taller than the window).</summary>
    private static void Click(Control c)
    {
        c.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var top = TopLevel.GetTopLevel(c)!;
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top)!.Value;
        top.MouseDown(p, MouseButton.Left);
        top.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task The_chat_menu_lists_the_panels_ticks_the_shown_one_and_opens_it()
    {
        var (w, vm) = await OpenAsync("plan", 1180);
        await SendAsync(vm, "plan it", () => vm.Plan.HasPlan);

        var menu = OpenViewsMenu(w);
        await Settle();
        Assert.Equal([Loc.T("Files"), Loc.T("Changes"), Loc.T("Background tasks"), Loc.T("Plan"), Loc.T("Browser"), Loc.T("Terminal")],
            menu.Children.OfType<Button>().Select(b => AutomationProperties.GetName(b)));
        Assert.All(menu.Children.OfType<Button>(), b => Assert.False(Ticked(b)));
        // The plan's progress and the shortcuts are in the menu
        var plan = Item(menu, "ViewPlanItem");
        var texts = plan.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("2/5", texts); // the dropped task counts neither way
        Assert.Contains(MainViewModel.PlanShortcut, texts);

        Click(plan);
        await Settle();
        Assert.Equal(SidePane.Plan, vm.ActivePane);
        Assert.False(ViewsButton(w).Flyout!.IsOpen); // a choice closes the menu
        Assert.True(Named<RightPanel>(w, "RightPanel").IsEffectivelyVisible);
        Assert.Equal(Loc.T("Plan"), vm.PanelTabs.Single(t => t.IsActive).Header);

        menu = OpenViewsMenu(w);
        await Settle();
        Assert.True(Ticked(Item(menu, "ViewPlanItem")));
        Assert.False(Ticked(Item(menu, "ViewTasksItem")));
        // Plan again hides the panel (a toggle); its tab stays for the next time
        Click(Item(menu, "ViewPlanItem"));
        await Settle();
        Assert.Equal(SidePane.None, vm.ActivePane);
        Assert.Contains(vm.PanelTabs, t => t.Pane == SidePane.Plan);

        // The browser is ticked while it is the tab shown
        vm.IsPreviewOpen = true;
        menu = OpenViewsMenu(w);
        await Settle();
        Assert.True(Ticked(Item(menu, "ViewBrowserItem")));
        ViewsButton(w).Flyout!.Hide();
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Shortcuts_select_the_panels_tabs_and_the_toggle_and_the_tabs_close_hide_it()
    {
        var (w, vm) = await OpenAsync("plan", 1180);
        var mod = (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers | RawInputModifiers.Shift;
        void Press(Key key, PhysicalKey physical)
        {
            w.KeyPress(key, mod, physical, null);
            Dispatcher.UIThread.RunJobs();
        }
        Press(Key.P, PhysicalKey.P);
        Assert.Equal(SidePane.Plan, vm.ActivePane);
        Press(Key.T, PhysicalKey.T);
        Assert.Equal(SidePane.Tasks, vm.ActivePane);
        Press(Key.G, PhysicalKey.G);
        Assert.Equal(SidePane.Changes, vm.ActivePane);
        Press(Key.F, PhysicalKey.F);
        Assert.Equal(SidePane.Files, vm.ActivePane);
        Assert.Equal([SidePane.Plan, SidePane.Tasks, SidePane.Changes, SidePane.Files], vm.PanelTabs.Select(t => t.Pane));
        Press(Key.F, PhysicalKey.F);
        Assert.Equal(SidePane.None, vm.ActivePane);

        // The header's toggle shows the panel again on its last tab; the strip's toggle hides it
        Click(Named<Button>(w, "ShowPanelButton"));
        await Settle();
        Assert.Equal(SidePane.Files, vm.ActivePane);
        Click(Named<Button>(w, "HidePanelButton"));
        await Settle();
        Assert.Equal(SidePane.None, vm.ActivePane);
        Assert.False(Named<RightPanel>(w, "RightPanel").IsVisible);

        // A tab's × closes it and shows the next; the last one closed hides the panel
        Press(Key.P, PhysicalKey.P);
        await Settle();
        Assert.True(Named<StackPanel>(w, "PlanEmpty").IsEffectivelyVisible);
        foreach (var tab in vm.PanelTabs.ToList()) vm.ClosePanelTabCommand.Execute(tab);
        await Settle();
        Assert.Empty(vm.PanelTabs);
        Assert.Equal(SidePane.None, vm.ActivePane);
        Assert.False(Named<RightPanel>(w, "RightPanel").IsVisible);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(1180, false)]
    [InlineData(900, false)]
    [InlineData(640, true)]
    public async Task The_panel_sits_beside_the_conversation_or_over_it_when_there_is_no_room(double width, bool over)
    {
        var (w, vm) = await OpenAsync("plan", width);
        vm.ShowPaneCommand.Execute(SidePane.Plan);
        await Settle(300);
        var pane = Named<RightPanel>(w, "RightPanel");
        var main = Named<Grid>(w, "MainColumn");
        var header = Named<Border>(w, "HeaderBar");
        var composer = Named<Border>(w, "ComposerBox");
        Rect Box(Control c) => new(c.TranslatePoint(default, w)!.Value, c.Bounds.Size);
        Assert.True(pane.IsEffectivelyVisible);
        if (over)
        {
            // Too narrow for both: the panel lies over the conversation from its right edge, above the composer
            Assert.True(pane.ZIndex > 0);
            Assert.True(Box(pane).Right <= Box(main).Right + 0.5);
            Assert.True(Box(pane).Bottom <= Box(composer).Y + 0.5, "the composer stays clear");
            Assert.False(Named<GridSplitter>(w, "RightPanelSplitter").IsVisible);
        }
        else
        {
            Assert.Equal(0, pane.ZIndex);
            Assert.InRange(pane.Bounds.Width, 320, 480);
            Assert.True(header.Bounds.Width >= 420, $"conversation {header.Bounds.Width} px");
            Assert.True(Box(header).Right <= Box(pane).X + 0.5, "header clear of the panel");
            Assert.True(Box(composer).Right <= Box(pane).X + 0.5, "composer clear of the panel");
            Assert.True(Named<GridSplitter>(w, "RightPanelSplitter").IsVisible);
            Assert.Equal(width >= 1124, vm.ShowSidebar); // the sidebar gives way first
        }

        // The browser is a tab of the same panel: the panel keeps its place, the plan stays a tab of the strip
        var box = Box(pane);
        vm.IsPreviewOpen = true;
        await Settle(300);
        Assert.True(Named<PreviewPanel>(w, "PreviewPanel").IsEffectivelyVisible);
        Assert.Equal(box, Box(pane));
        Assert.Contains(vm.PanelTabs, t => t.Pane == SidePane.Plan);

        // Hidden, it gives the conversation its room back
        vm.ClosePaneCommand.Execute(null);
        await Settle(300);
        Assert.False(pane.IsVisible);
        Assert.Equal(Box(main).Width, header.Bounds.Width, 1);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Jobs_are_read_over_rpc_never_as_a_jobs_prompt()
    {
        // The pane asks get_jobs; no /jobs text goes to omp, where an older omp would take it for a message to the model
        var dir = TestProcesses.TempDir("panes-nojobs");
        File.WriteAllText(Path.Combine(dir, "c.json"), "{}");
        var log = Path.Combine(dir, "sent.log");
        var s = new SessionController(TestProcesses.FakeWithCommands("normal", Path.Combine(dir, "c.json"), log));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        vm.ShowPaneCommand.Execute(SidePane.Tasks);
        OpenViewsMenu(w);
        ViewsButton(w).Flyout!.Hide();
        await SendAsync(vm, "hello", () => vm.Rows.OfType<AssistantRowViewModel>().Any());
        await Settle(600);
        Assert.False(File.Exists(log) && File.ReadAllText(log).Contains("/jobs", StringComparison.Ordinal));
        Assert.Contains(await JobRequests(vm), c => c.Type == "get_jobs");
        Assert.Single(vm.Rows.OfType<UserRowViewModel>());
        Assert.True(vm.Tasks.IsEmpty);
        Assert.False(vm.Tasks.HasJobsError);
        Assert.True(vm.Tasks.IsUnsupported); // the fake has no subagent commands either
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_current_step_shows_what_the_run_does_now()
    {
        await using var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        vm.Plan.Apply([new TodoPhase("Todos", [new TodoTask("Build", "in_progress"), new TodoTask("Test", "pending")])], "/s", DateTimeOffset.Now);
        vm.Phase = SessionPhase.Running;
        vm.StatusText = "Running bash 00:03";
        var current = vm.Plan.Phases[0].Tasks[0];
        Assert.Equal("Running bash 00:03", current.Activity);
        Assert.Equal("", vm.Plan.Phases[0].Tasks[1].Activity);
        vm.Phase = SessionPhase.Ready;
        Assert.False(current.HasActivity);
    }

    [AvaloniaFact]
    public async Task The_plan_pane_replaces_the_task_card_and_keeps_the_earlier_plan()
    {
        var (w, vm) = await OpenAsync("plan", 1180);
        await SendAsync(vm, "plan it", () => vm.Plan.HasPlan);
        var card = Named<Border>(w, "TodoCard");
        Assert.True(card.IsEffectivelyVisible);
        vm.ShowPaneCommand.Execute(SidePane.Plan);
        await Settle();
        Assert.False(card.IsEffectivelyVisible); // the pane shows the same list: no second copy above the composer
        Assert.Equal(Loc.F("{0} of {1} done", 2, 5), vm.Plan.ProgressText); // the dropped task counts neither way
        var rows = Named<ItemsControl>(w, "PlanPhases").GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("plan-task")).ToList();
        Assert.Equal(6, rows.Count);
        Assert.Single(rows, r => r.Classes.Contains("current"));

        await SendAsync(vm, "go on", () => vm.Plan.Changes.Count > 1);
        await SendAsync(vm, "new plan", () => vm.Plan.HasEarlierPlans);
        Assert.Equal(Loc.F("{0} of {1} done", 0, 2), vm.Plan.ProgressText);
        Assert.Equal(Loc.F("{0} of {1} done", 3, 6), Assert.Single(vm.Plan.EarlierPlans).Summary);
        Assert.Contains(vm.Plan.Changes, c => c.What == Loc.T("Completed"));

        vm.ClosePaneCommand.Execute(null);
        await Settle();
        Assert.True(card.IsEffectivelyVisible);

        // omp restarts on the session: the history is read back from the session's saved todo results
        var changes = vm.Plan.Changes.Select(c => (c.What, c.Content)).ToList();
        Assert.True(await vm.RestartOmpToApplyAsync());
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Plan.HasEarlierPlans, "history after the restart");
        Assert.Equal(changes, vm.Plan.Changes.Select(c => (c.What, c.Content)));
        Assert.Equal(Loc.F("{0} of {1} done", 3, 6), vm.Plan.EarlierPlans.Single().Summary);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Background_tasks_show_subagents_and_jobs_and_the_menu_counts_what_runs()
    {
        var (w, vm) = await OpenAsync("subagents", 1180);
        await SendAsync(vm, "look into it", () => vm.Tasks.Subagents.Count == 3);
        // After the turn the jobs are read even with the pane closed: the Background tasks toggle shows that something runs
        await Until(() => vm.Tasks.RunningCount == 2, "jobs read after the turn");
        Assert.True(Named<Border>(w, "TasksDot").IsEffectivelyVisible);
        vm.ShowPaneCommand.Execute(SidePane.Tasks);
        await Until(() => vm.Tasks.HasJobs, "jobs read");
        await Settle();
        // sa-3 runs in the background, and the bash job; the async task job is sa-3 itself
        Assert.Equal(2, vm.Tasks.RunningCount);
        Assert.Equal(["Watch the dev server", "Review the plan pane", "Find where todos are read"], vm.Tasks.Subagents.Select(r => r.Title));
        Assert.Equal([Loc.T("Running"), Loc.T("Failed"), Loc.T("Done")], vm.Tasks.Subagents.Select(r => r.StatusText));
        Assert.Equal(3, Named<ItemsControl>(w, "SubagentList").GetVisualDescendants().OfType<ToggleButton>().Count());

        // The ⋯ menu counts what runs
        var menu = OpenViewsMenu(w);
        await Settle();
        var count = Named<Border>(Item(menu, "ViewTasksItem"), "TasksCount");
        Assert.True(count.IsEffectivelyVisible);
        Assert.Equal("2", ((TextBlock)count.Child!).Text);
        ViewsButton(w).Flyout!.Hide();

        // A card opens to its conversation
        var first = vm.Tasks.Subagents.Single(r => r.Id == "sa-1");
        first.IsExpanded = true;
        await Settle();
        var show = Named<ItemsControl>(w, "SubagentList").GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.DataContext, first) && b.Command == first.LoadMessagesCommand);
        show.BringIntoView();
        await Settle();
        await first.LoadMessagesCommand.ExecuteAsync(null);
        Assert.True(first.MessagesLoaded);
        Assert.Contains(first.Messages, m => m.Role == "tool");
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>The subagents scenario after a turn, with <paramref name="more"/> extra subagents still running, pane open.</summary>
    private static async Task<(MainWindow W, MainViewModel Vm)> OpenRunningSubagents(params string[] more)
    {
        var (w, vm) = await OpenAsync("subagents", 1180);
        await SendAsync(vm, "look into it", () => vm.Tasks.Subagents.Count == 3);
        if (more.Length > 0) await PanesTests.SubagentControl(vm.Session, "{\"start\":" + JsonSerializer.Serialize(more) + "}");
        vm.ShowPaneCommand.Execute(SidePane.Tasks);
        await Until(() => vm.Tasks.Subagents.Count(r => r.IsRunning) == 1 + more.Length, "running subagents listed");
        await Settle();
        return (w, vm);
    }

    private static Control RowControl(MainWindow w, SubagentRowViewModel row, string name) =>
        Named<ItemsControl>(w, "SubagentList").GetVisualDescendants().OfType<Control>()
            .Single(c => c.Name == name && ReferenceEquals(c.DataContext, row));

    private static async Task<List<(string Type, string Id, string? Message)>> SubagentControls(MainViewModel vm) =>
        (await PanesTests.SubagentControl(vm.Session, "{}")).GetProperty("controls").EnumerateArray()
            .Select(c => (c.GetProperty("type").GetString()!, c.GetProperty("subagentId").GetString()!,
                c.TryGetProperty("message", out var m) ? m.GetString() : null)).ToList();

    [AvaloniaTheory]
    [InlineData("light", "en")]
    [InlineData("dark", "en")]
    [InlineData("light", "ru")]
    [InlineData("dark", "ru")]
    public async Task Subagent_card_hover_and_controls(string theme, string language)
    {
        var previous = Loc.Current;
        var settings = Path.Combine(TestProcesses.TempDir("card-hover"), "settings.json");
        Loc.Load(language, settings);
        MainViewModel.ApplyTheme(theme);
        var (w, vm) = await OpenRunningSubagents();
        try
        {
            var row = vm.Tasks.Subagents.Single(r => r.Id == "sa-3");
            var card = Named<ItemsControl>(w, "SubagentList").GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("task-card") && ReferenceEquals(b.DataContext, row));
            var toggle = card.GetVisualDescendants().OfType<ToggleButton>().Single();
            var box = (TextBox)RowControl(w, row, "SubagentMessageBox");
            w.MouseMove(toggle.TranslatePoint(new Point(20, 20), w)!.Value);
            await Settle();
            Assert.True(card.IsPointerOver);
            var hover = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(card.Background).Color;
            Assert.True(card.TryFindResource("GuiFill1", card.ActualThemeVariant, out var fill));
            Assert.Equal(Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(fill).Color, hover);
            var presenter = toggle.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .Single(p => p.Name == "PART_ContentPresenter");
            Assert.Equal(Avalonia.Media.Colors.Transparent,
                Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.Background).Color);
            w.MouseMove(box.TranslatePoint(new Point(4, 4), w)!.Value);
            await Settle();
            Assert.True(card.IsPointerOver);
            Assert.Equal(hover, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(card.Background).Color);
            w.MouseMove(toggle.TranslatePoint(new Point(20, 20), w)!.Value);
            await Settle();
            if (Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") is { } dir)
            {
                Directory.CreateDirectory(dir);
                using var frame = w.CaptureRenderedFrame();
                Assert.NotNull(frame);
                using var file = File.Create(Path.Combine(dir, $"card-hover-{theme}-{language}.png"));
                frame.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            Assert.Empty(LayoutAudit.Run(w, card));
            Click(toggle);
            Assert.True(row.IsExpanded);
            Click(toggle);
            Assert.False(row.IsExpanded);
            toggle.Focus();
            w.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            w.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.True(row.IsExpanded);
            Click(toggle);
            Click(box);
            w.KeyTextInput("keep going");
            Assert.False(row.IsExpanded);
            Assert.Equal("keep going", box.Text);
            await Settle();
            Click(RowControl(w, row, "SubagentSend"));
            await Until(() => row.MessageText.Length == 0, "message sent without expanding");
            Assert.False(row.IsExpanded);
            Click(RowControl(w, row, "SubagentStop"));
            await Until(() => !row.IsRunning, "stopped without expanding");
            Assert.False(row.IsExpanded);
            Assert.False(box.IsEffectivelyVisible);
            Click(toggle);
            Assert.True(row.IsExpanded);
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
            Loc.Load(previous, settings);
            MainViewModel.ApplyTheme("light");
        }
    }

    [AvaloniaFact]
    public async Task Stop_cancels_only_that_subagent_and_its_row_leaves_running()
    {
        var (w, vm) = await OpenRunningSubagents("sa-4");
        var row = vm.Tasks.Subagents.Single(r => r.Id == "sa-3");
        var other = vm.Tasks.Subagents.Single(r => r.Id == "sa-4");
        // Finished rows offer neither control
        Assert.False(RowControl(w, vm.Tasks.Subagents.Single(r => r.Id == "sa-1"), "SubagentStop").IsEffectivelyVisible);
        Click(RowControl(w, row, "SubagentStop"));
        await Until(() => !row.IsRunning, "sa-3 stopped");
        Assert.Equal(Loc.T("Stopped"), row.StatusText);
        Assert.False(row.HasActionError);
        Assert.True(other.IsRunning);
        Assert.Equal([("cancel_subagent", "sa-3", (string?)null)], await SubagentControls(vm));
        await Settle();
        Assert.False(RowControl(w, row, "SubagentStop").IsEffectivelyVisible);
        Assert.False(vm.Tasks.CanStopAll); // one runs now
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Message_steers_the_subagent_with_the_text_and_clears_the_box()
    {
        var (w, vm) = await OpenRunningSubagents();
        var row = vm.Tasks.Subagents.Single(r => r.Id == "sa-3");
        var box = (TextBox)RowControl(w, row, "SubagentMessageBox");
        Assert.False(RowControl(w, row, "SubagentSend").IsEnabled); // nothing written yet
        box.Text = "also watch the tests";
        await Settle();
        Click(RowControl(w, row, "SubagentSend"));
        await Until(() => row.MessageText.Length == 0, "message delivered");
        Assert.Equal("", box.Text);
        Assert.False(row.HasActionError);
        Assert.True(row.IsRunning);
        Assert.Equal([("steer_subagent", "sa-3", (string?)"also watch the tests")], await SubagentControls(vm));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_refused_stop_or_message_is_shown_on_the_row()
    {
        var (w, vm) = await OpenRunningSubagents();
        var row = vm.Tasks.Subagents.Single(r => r.Id == "sa-3");
        await PanesTests.SubagentControl(vm.Session, """{"cancelError":"the subagent is busy","steerError":"the subagent is finishing"}""");
        Click(RowControl(w, row, "SubagentStop"));
        await Until(() => row.HasActionError, "stop error");
        Assert.Contains("the subagent is busy", row.ActionError);
        Assert.True(row.IsRunning);
        await Settle();
        Assert.True(RowControl(w, row, "SubagentStop").IsEnabled); // it can be tried again
        Assert.Contains(Named<ItemsControl>(w, "SubagentList").GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == row.ActionError && t.IsEffectivelyVisible);

        row.MessageText = "keep going";
        Click(RowControl(w, row, "SubagentSend"));
        await Until(() => row.ActionError.Contains("the subagent is finishing", StringComparison.Ordinal), "message error");
        Assert.Equal("keep going", row.MessageText); // not lost
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Stop_all_cancels_every_running_subagent()
    {
        var (w, vm) = await OpenRunningSubagents("sa-4", "sa-5");
        var stopAll = Named<Button>(w, "StopAllSubagents");
        Assert.True(stopAll.IsEffectivelyVisible);
        Click(stopAll);
        await Until(() => vm.Tasks.Subagents.All(r => !r.IsRunning), "all stopped");
        var cancelled = (await SubagentControls(vm)).Where(c => c.Type == "cancel_subagent").Select(c => c.Id).Order().ToList();
        Assert.Equal(["sa-3", "sa-4", "sa-5"], cancelled);
        await Settle();
        Assert.False(stopAll.IsEffectivelyVisible);
        await vm.DisposeAsync();
        w.Close();
    }

    private static async Task<(MainWindow W, MainViewModel Vm, SubagentRowViewModel Row)> OpenLiveConversation(string? messages = null)
    {
        var (w, vm) = await OpenAsync("subagents", 1180);
        await SendAsync(vm, "look into it", () => vm.Tasks.Subagents.Count == 3);
        if (messages is not null) await PanesTests.SubagentControl(vm.Session, "{\"replace\":" + messages + "}");
        vm.ShowPaneCommand.Execute(SidePane.Tasks);
        var row = vm.Tasks.Subagents.Single(r => r.Id == "sa-3");
        row.IsExpanded = true;
        await Settle();
        var show = Named<ItemsControl>(w, "SubagentList").GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.DataContext, row) && b.Command == row.LoadMessagesCommand);
        Click(show);
        await Until(() => row.MessagesLoaded, "conversation loaded");
        return (w, vm, row);
    }

    [AvaloniaFact]
    public async Task An_open_conversation_appends_live_deltas_keeps_a_bounded_preview_and_retries_failed_bytes()
    {
        var messages = JsonSerializer.Serialize(Enumerable.Range(0, 45).Select(i => new { role = "user", content = $"entry-{i}" }));
        var (w, vm, row) = await OpenLiveConversation(messages);
        try
        {
            Assert.Equal(Enumerable.Range(5, 40).Select(i => $"entry-{i}"), row.Messages.Select(m => m.Text));
            var retained = row.Messages[2];
            await PanesTests.SubagentControl(vm.Session, """{"append":[{"role":"assistant","content":"é漢😀"},{"role":"assistant","content":"é漢😀"}]}""");
            await Until(() => row.Messages.Count(m => m.Text == "é漢😀") == 2, "live delta without another click");
            Assert.Equal(40, row.Messages.Count);
            Assert.Same(retained, row.Messages[0]);

            var changes = 0;
            row.Messages.CollectionChanged += (_, _) => changes++;
            var prior = await PanesTests.SubagentControl(vm.Session, "{}");
            var requests = prior.GetProperty("requests").GetArrayLength();
            await Settle(1300); // an empty live poll must not rebuild rows or disturb a text selection
            var empty = await PanesTests.SubagentControl(vm.Session, "{}");
            Assert.True(empty.GetProperty("requests").GetArrayLength() > requests);
            Assert.Equal(0, changes);
            var cursor = empty.GetProperty("requests").EnumerateArray().Last().GetProperty("fromByte").GetInt64();

            await PanesTests.SubagentControl(vm.Session, """{"append":[{"role":"assistant","content":"after error"}],"error":"read unavailable"}""");
            await Until(() => row.HasMessagesNote, "read error");
            Assert.Equal(0, changes);
            await PanesTests.SubagentControl(vm.Session, """{"error":null,"malformed":true}""");
            await Settle(1300);
            Assert.Equal(0, changes);
            var malformed = await PanesTests.SubagentControl(vm.Session, "{}");
            Assert.Equal(cursor, malformed.GetProperty("requests").EnumerateArray().Last().GetProperty("fromByte").GetInt64());
            await PanesTests.SubagentControl(vm.Session, """{"malformed":false}""");
            await Until(() => row.Messages.Any(m => m.Text == "after error"), "same bytes retried");
            Assert.Single(row.Messages, m => m.Text == "after error");
            Assert.False(row.HasMessagesNote);

            await PanesTests.SubagentControl(vm.Session, """{"replace":[{"role":"user","content":"reset"}]}""");
            await Until(() => row.Messages.Count == 1 && row.Messages[0].Text == "reset", "truncated transcript replaced");
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("collapse")]
    [InlineData("pane")]
    [InlineData("chat")]
    public async Task Leaving_a_conversation_cancels_its_read_and_drops_a_late_answer(string leave)
    {
        var (w, vm, row) = await OpenLiveConversation("""[{"role":"user","content":"before"}]""");
        var session = vm.Session;
        try
        {
            await PanesTests.SubagentControl(session, """{"append":[{"role":"assistant","content":"while hidden"}],"hold":true}""");
            await UntilSubagentReadHeld(session);
            Assert.Equal(1, PendingSubagentReadCount(session));
            if (leave == "collapse") row.IsExpanded = false;
            else if (leave == "pane") vm.ClosePaneCommand.Execute(null);
            else vm.NewChatCommand.Execute(null);
            // Bound this below the 15-second transcript deadline: a timeout is not cancellation.
            await Until(() => PendingSubagentReadCount(session) == 0, "preview request cancelled before its late reply", seconds: 5);
            if (leave == "chat")
                await Until(() => vm.Session != session && vm.Phase == SessionPhase.Ready, "another chat");
            var hidden = await PanesTests.SubagentControl(session, """{"hold":false,"release":true}""");
            await Settle(1300);
            Assert.Equal("before", Assert.Single(row.Messages).Text);
            Assert.Equal(hidden.GetProperty("requests").GetArrayLength(),
                (await PanesTests.SubagentControl(session, "{}")).GetProperty("requests").GetArrayLength());
            if (leave == "chat")
            {
                Assert.Empty(vm.Tasks.Subagents);
                await SendAsync(vm, "new tasks", () => vm.Tasks.Subagents.Count == 3);
                Assert.NotSame(row, vm.Tasks.Subagents.Single(r => r.Id == row.Id));
                Assert.All(vm.Tasks.Subagents, next => Assert.Empty(next.Messages));
            }
            else
            {
                if (leave == "pane") vm.ShowPaneCommand.Execute(SidePane.Tasks);
                else row.IsExpanded = true;
                await Until(() => row.Messages.Any(m => m.Text == "while hidden"), "resumed cursor");
                Assert.Equal(["before", "while hidden"], row.Messages.Select(m => m.Text));
            }
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_changed_transcript_path_restarts_at_zero_and_a_replaced_card_rejects_its_old_read()
    {
        var (w, vm, row) = await OpenLiveConversation("""[{"role":"user","content":"before"}]""");
        try
        {
            // The registry can know the new file before a progress frame tells the card about it.
            await PanesTests.SubagentControl(vm.Session, """{"sessionFile":"/new/transcript.jsonl","replace":[{"role":"user","content":"the new file begins with a longer first entry"},{"role":"assistant","content":"new second"}]}""");
            await Until(() => row.Messages.Count == 2 && row.Messages[0].Text.StartsWith("the new file", StringComparison.Ordinal), "new file read from zero");
            Assert.Equal("new second", row.Messages[1].Text);

            await PanesTests.SubagentControl(vm.Session, """{"hold":true,"append":[{"role":"assistant","content":"stale old-file answer"}]}""");
            await UntilSubagentReadHeld(vm.Session);
            await PanesTests.SubagentControl(vm.Session, """{"sessionFile":"/third/transcript.jsonl","replace":[{"role":"user","content":"third file"}]}""");
            row.Update(row.Info with { SessionFile = "/third/transcript.jsonl" });
            await PanesTests.SubagentControl(vm.Session, """{"hold":false,"release":true}""");
            await Until(() => row.MessagesLoaded && row.Messages.Count == 1, "replacement path");
            Assert.Equal("third file", row.Messages[0].Text);
            Assert.DoesNotContain(row.Messages, m => m.Text == "stale old-file answer");
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task An_offscreen_conversation_stops_polling_and_disposal_cancels_the_resumed_read()
    {
        var (w, vm, row) = await OpenLiveConversation("""[{"role":"user","content":"before"}]""");
        try
        {
            var laterCard = vm.Tasks.Subagents.Last();
            laterCard.Update(laterCard.Info with { Task = string.Join("\n", Enumerable.Repeat("More work below this card", 60)) });
            laterCard.IsExpanded = true;
            w.Height = 480;
            await Settle();
            var scroll = Named<ScrollViewer>(w, "TasksScroll");
            scroll.ScrollToEnd();
            await Settle();
            var card = Named<ItemsControl>(w, "SubagentList").GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("task-card") && ReferenceEquals(b.DataContext, row));
            Assert.True(card.TranslatePoint(new Point(0, card.Bounds.Height), scroll)!.Value.Y <= 0);
            var offscreen = await PanesTests.SubagentControl(vm.Session, """{"append":[{"role":"assistant","content":"later"}]}""");
            await Settle(1300);
            Assert.Equal("before", Assert.Single(row.Messages).Text);
            Assert.Equal(offscreen.GetProperty("requests").GetArrayLength(),
                (await PanesTests.SubagentControl(vm.Session, "{}")).GetProperty("requests").GetArrayLength());

            await PanesTests.SubagentControl(vm.Session, """{"hold":true}""");
            scroll.ScrollToHome();
            await Settle();
            await UntilSubagentReadHeld(vm.Session);
            var connection = vm.Session.Connection!;
            await vm.DisposeAsync();
            Assert.Equal(0, connection.PendingRequestCount);
            Assert.Equal("before", Assert.Single(row.Messages).Text);
            Assert.False(row.IsLoadingMessages);
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    /// <summary>A chat whose omp runs <paramref name="jobsJson"/> (FakeJobs.cs), the Background tasks pane open on its rows.</summary>
    private static async Task<(MainWindow W, MainViewModel Vm)> OpenJobs(string jobsJson, int rows)
    {
        var (w, vm) = await OpenAsync("normal", 1180);
        await PanesTests.JobControl(vm.Session, jobsJson);
        vm.ShowPaneCommand.Execute(SidePane.Tasks);
        await vm.Tasks.RefreshAsync();
        await Until(() => vm.Tasks.Jobs.Count == rows, "jobs listed");
        await Settle();
        return (w, vm);
    }

    private static Control JobControl(MainWindow w, JobRowViewModel row, string name) =>
        Named<ItemsControl>(w, "JobList").GetVisualDescendants().OfType<Control>()
            .Single(c => c.Name == name && ReferenceEquals(c.DataContext, row));

    private static async Task<List<(string Type, string? Id, long? FromByte)>> JobRequests(MainViewModel vm) =>
        (await PanesTests.JobControl(vm.Session, "{}")).GetProperty("controls").EnumerateArray()
            .Select(c => (c.GetProperty("type").GetString()!, c.TryGetProperty("jobId", out var id) ? id.GetString() : null,
                c.TryGetProperty("fromByte", out var from) && from.ValueKind == JsonValueKind.Number ? from.GetInt64() : (long?)null)).ToList();

    [AvaloniaFact]
    public async Task Stop_cancels_only_that_job_and_a_refusal_is_shown_on_its_row()
    {
        var (w, vm) = await OpenJobs("""{"jobs":[{"id":"bg_1","label":"npm run dev"},{"id":"bg_2","label":"cargo watch"},{"id":"bg_0","status":"completed","label":"npm test"}]}""", 3);
        var dev = vm.Tasks.Jobs.Single(r => r.Id == "bg_1");
        var watch = vm.Tasks.Jobs.Single(r => r.Id == "bg_2");
        Assert.Equal(2, vm.Tasks.RunningCount);
        Assert.False(JobControl(w, vm.Tasks.Jobs.Single(r => r.Id == "bg_0"), "JobStop").IsEffectivelyVisible); // an ended job has none

        await PanesTests.JobControl(vm.Session, """{"cancelError":"the job is busy"}""");
        Click(JobControl(w, watch, "JobStop"));
        await Until(() => watch.HasActionError, "stop refused");
        Assert.Contains("the job is busy", watch.ActionError);
        Assert.True(watch.IsRunning);
        await Settle();
        Assert.True(JobControl(w, watch, "JobStop").IsEnabled); // it can be tried again

        await PanesTests.JobControl(vm.Session, """{"cancelError":null}""");
        Click(JobControl(w, dev, "JobStop"));
        await Until(() => !dev.IsRunning, "bg_1 stopped");
        Assert.Equal(Loc.T("Cancelled"), dev.StatusText);
        Assert.False(dev.HasActionError);
        Assert.True(watch.IsRunning);
        Assert.Same(dev, vm.Tasks.Jobs.Single(r => r.Id == "bg_1")); // the same row, updated
        Assert.Equal(1, vm.Tasks.RunningCount);
        Assert.Equal([("cancel_job", "bg_2"), ("cancel_job", "bg_1")],
            (await JobRequests(vm)).Where(c => c.Type == "cancel_job").Select(c => (c.Type, c.Id)));
        await Settle();
        Assert.False(JobControl(w, dev, "JobStop").IsEffectivelyVisible);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Show_output_reads_the_tail_then_only_new_bytes_and_keeps_a_bounded_view()
    {
        var (w, vm) = await OpenJobs("""{"jobs":[{"id":"bg_1","label":"npm run dev","output":"> dev\né漢😀 ready\n"}]}""", 1);
        var row = vm.Tasks.Jobs.Single();
        var toggle = (Button)JobControl(w, row, "JobOutputToggle");
        Assert.Equal(Loc.T("Show output"), toggle.Content);
        Assert.False(JobControl(w, row, "JobOutputText").IsEffectivelyVisible);
        Click(toggle);
        await Until(() => row.HasOutput, "tail read");
        Assert.Equal("> dev\né漢😀 ready\n", row.OutputText);
        await Settle();
        Assert.Equal(Loc.T("Hide output"), toggle.Content);
        Assert.True(JobControl(w, row, "JobOutputText").IsEffectivelyVisible);
        Assert.False(row.HasOutputNote);

        // While it runs, only what it printed since is asked for, from the byte cursor
        await PanesTests.JobControl(vm.Session, """{"jobId":"bg_1","append":"compiled\n"}""");
        await Until(() => row.OutputText.EndsWith("compiled\n", StringComparison.Ordinal), "delta appended");
        Assert.Equal("> dev\né漢😀 ready\ncompiled\n", row.OutputText);
        var reads = (await JobRequests(vm)).Where(c => c.Type == "get_job_output").ToList();
        Assert.Null(reads[0].FromByte); // the tail first
        Assert.Contains(reads, c => c.FromByte == 22); // then from its UTF-8 end

        // A flood keeps only the latest part, whole lines, and says so
        var flood = string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i:D5} {new string('x', 40)}\n"));
        await PanesTests.JobControl(vm.Session, "{\"jobId\":\"bg_1\",\"append\":" + JsonSerializer.Serialize(flood) + "}");
        await Until(() => row.OutputText.EndsWith("line 02999 " + new string('x', 40) + "\n", StringComparison.Ordinal), "flood read");
        Assert.True(row.OutputText.Length <= JobRowViewModel.OutputLimit);
        Assert.StartsWith("line ", row.OutputText);
        Assert.True(row.HasOutputNote);

        // Hidden, it is no longer read
        Click(toggle);
        await Settle();
        var before = (await JobRequests(vm)).Count(c => c.Type == "get_job_output");
        await PanesTests.JobControl(vm.Session, """{"jobId":"bg_1","append":"unseen\n"}""");
        await Settle(1500);
        Assert.Equal(before, (await JobRequests(vm)).Count(c => c.Type == "get_job_output"));
        Assert.DoesNotContain("unseen", row.OutputText);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_job_quiet_on_a_prompt_shows_waiting_for_input_until_it_prints_again()
    {
        var (w, vm) = await OpenJobs("""{"jobs":[{"id":"bg_1","label":"npx create-app","output":"Project name: ","waitingForInput":true},{"id":"bg_2","label":"npm run dev"}]}""", 2);
        var asking = vm.Tasks.Jobs.Single(r => r.Id == "bg_1");
        var other = vm.Tasks.Jobs.Single(r => r.Id == "bg_2");
        Assert.Equal(Loc.T("Waiting for input"), asking.StatusText);
        Assert.True(JobControl(w, asking, "JobWaiting").IsEffectivelyVisible);
        Assert.False(JobControl(w, other, "JobWaiting").IsEffectivelyVisible);
        Assert.Equal(Loc.T("Running"), other.StatusText);
        Assert.True(JobControl(w, asking, "JobStop").IsEffectivelyVisible); // the way out

        await PanesTests.JobControl(vm.Session, """{"jobId":"bg_1","waitingForInput":false,"append":"my-app\n"}""");
        await vm.Tasks.RefreshAsync();
        await Until(() => !asking.IsWaitingForInput, "no longer waiting");
        Assert.Equal(Loc.T("Running"), asking.StatusText);
        await Settle();
        Assert.False(JobControl(w, asking, "JobWaiting").IsEffectivelyVisible);

        // An ended job waits for nothing, whatever its last line
        await PanesTests.JobControl(vm.Session, """{"jobId":"bg_1","waitingForInput":true,"status":"failed","exitCode":130}""");
        await vm.Tasks.RefreshAsync();
        await Until(() => !asking.IsRunning, "ended");
        Assert.False(asking.IsWaitingForInput);
        Assert.Equal(Loc.F("Failed (exit {0})", 130), asking.StatusText);
        await vm.DisposeAsync();
        w.Close();
    }

    private static int PendingRequests(SessionController session, string command)
    {
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var pending = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            typeof(Ombra.Rpc.RpcConnection).GetField("_pending", fields)!.GetValue(session.Connection));
        return pending.Values.Cast<object>().Count(request => (string?)request.GetType().GetProperty("Command")!.GetValue(request) == command);
    }

    [AvaloniaFact]
    public async Task Switching_chats_or_hiding_the_pane_cancels_the_job_list_read_under_way()
    {
        var (w, vm) = await OpenJobs("""{"jobs":[{"id":"bg_1","label":"npm run dev"}]}""", 1);
        await PanesTests.JobControl(vm.Session, """{"holdList":true}""");
        _ = vm.Tasks.RefreshAsync();
        await Until(() => PendingRequests(vm.Session, "get_jobs") == 1, "a held read");
        // Each switch cancels the read of the chat shown before and starts one: they never pile up
        for (var i = 0; i < 3; i++)
        {
            vm.Tasks.OnSessionShown();
            await Settle(100);
            Assert.Equal(1, PendingRequests(vm.Session, "get_jobs"));
        }
        Assert.Equal(4, (await PanesTests.JobControl(vm.Session, "{}")).GetProperty("held").GetInt32());
        vm.ClosePaneCommand.Execute(null);
        await Until(() => PendingRequests(vm.Session, "get_jobs") == 0, "hiding cancels it");
        // The cancelled reads' late answers change nothing; the next read is not blocked by them
        await PanesTests.JobControl(vm.Session, """{"holdList":false,"release":true}""");
        await Settle();
        Assert.Empty(vm.Tasks.Jobs); // the switch cleared the rows; dropped answers did not bring them back
        vm.ShowPaneCommand.Execute(SidePane.Tasks);
        await Until(() => vm.Tasks.Jobs.Count == 1, "read again");
        await vm.DisposeAsync();
        w.Close();
    }

    private static int PendingSubagentReadCount(SessionController session)
    {
        // Optional capability probes can remain pending after an older omp replies without an id.
        // Inspect the real transcript requests, not UI loading state or unrelated session RPCs.
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var pending = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            typeof(Ombra.Rpc.RpcConnection).GetField("_pending", fields)!.GetValue(session.Connection));
        return pending.Values.Cast<object>().Count(request =>
            (string?)request.GetType().GetProperty("Command")!.GetValue(request) == "get_subagent_messages");
    }

    private static async Task UntilSubagentReadHeld(SessionController session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((await PanesTests.SubagentControl(session, "{}")).GetProperty("held").GetInt32() == 0)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("No live conversation read reached omp");
            await Settle(20);
        }
    }
}
