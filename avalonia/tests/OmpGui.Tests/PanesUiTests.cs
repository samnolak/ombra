using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.App.Views.Panes;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The Views menu (⋮) and the side pane in a real window: the menu's items and ticks, the keyboard shortcuts, closing,
/// and how the pane shares the window with the conversation and the preview at 1180, 900 and 640 px.
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

    internal static Button ViewsButton(MainWindow w) => Named<ViewsMenu>(w, "ViewsMenu").Button;

    internal static StackPanel OpenViewsMenu(MainWindow w)
    {
        var button = ViewsButton(w);
        button.Flyout!.ShowAt(button);
        Dispatcher.UIThread.RunJobs();
        return (StackPanel)((Flyout)button.Flyout).Content!;
    }

    private static Button Item(StackPanel menu, string name) => menu.Children.OfType<Button>().Single(b => b.Name == name);

    private static bool Ticked(Button item) => item.GetVisualDescendants().OfType<Icon>().Any(i => i.Classes.Contains("views-check") && i.IsEffectivelyVisible);

    /// <summary>A real click in the middle of <paramref name="c"/> (a menu item lives in the flyout's own top level).</summary>
    private static void Click(Control c)
    {
        var top = TopLevel.GetTopLevel(c)!;
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top)!.Value;
        top.MouseDown(p, MouseButton.Left);
        top.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task The_views_menu_lists_the_panes_ticks_the_shown_one_and_opens_it()
    {
        var (w, vm) = await OpenAsync("plan", 1180);
        await SendAsync(vm, "plan it", () => vm.Plan.HasPlan);

        var menu = OpenViewsMenu(w);
        await Settle();
        Assert.Equal(["Files", "Background tasks", "Plan", "Browser", "Terminal"],
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
        Assert.True(Named<SidePaneHost>(w, "SidePanePanel").IsEffectivelyVisible);
        Assert.Equal("Plan", Named<TextBlock>(w, "PaneTitle").Text);

        menu = OpenViewsMenu(w);
        await Settle();
        Assert.True(Ticked(Item(menu, "ViewPlanItem")));
        Assert.False(Ticked(Item(menu, "ViewTasksItem")));
        // Plan again closes it (a toggle, as in Claude Code)
        Click(Item(menu, "ViewPlanItem"));
        await Settle();
        Assert.Equal(SidePane.None, vm.ActivePane);

        // Browser and terminal are ticked while open
        vm.IsPreviewOpen = true;
        menu = OpenViewsMenu(w);
        await Settle();
        Assert.True(Ticked(Item(menu, "ViewBrowserItem")));
        ViewsButton(w).Flyout!.Hide();
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Shortcuts_toggle_the_panes_and_the_close_button_closes()
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
        Press(Key.F, PhysicalKey.F);
        Assert.Equal(SidePane.Files, vm.ActivePane);
        Press(Key.F, PhysicalKey.F);
        Assert.Equal(SidePane.None, vm.ActivePane);

        Press(Key.P, PhysicalKey.P);
        await Settle();
        Assert.True(Named<StackPanel>(w, "PlanEmpty").IsEffectivelyVisible);
        Click(Named<Button>(w, "ClosePaneButton"));
        await Settle();
        Assert.Equal(SidePane.None, vm.ActivePane);
        Assert.False(Named<SidePaneHost>(w, "SidePanePanel").IsVisible);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(1180, false)]
    [InlineData(900, false)]
    [InlineData(640, true)]
    public async Task The_pane_sits_beside_the_conversation_or_over_it_when_there_is_no_room(double width, bool over)
    {
        var (w, vm) = await OpenAsync("plan", width);
        vm.ShowPaneCommand.Execute(SidePane.Plan);
        await Settle(300);
        var pane = Named<SidePaneHost>(w, "SidePanePanel");
        var main = Named<Grid>(w, "MainColumn");
        var header = Named<Border>(w, "HeaderBar");
        var composer = Named<Border>(w, "ComposerBox");
        Rect Box(Control c) => new(c.TranslatePoint(default, w)!.Value, c.Bounds.Size);
        Assert.True(pane.IsEffectivelyVisible);
        if (over)
        {
            // Too narrow for both: the pane covers the whole conversation column until it is closed
            Assert.True(pane.ZIndex > 0);
            Assert.Equal(Box(main).Width, Box(pane).Width, 1);
            Assert.False(Named<GridSplitter>(w, "SidePaneSplitter").IsVisible);
        }
        else
        {
            Assert.Equal(0, pane.ZIndex);
            Assert.Equal(340, pane.Bounds.Width, 1);
            Assert.True(header.Bounds.Width >= 420, $"conversation {header.Bounds.Width} px");
            Assert.True(Box(header).Right <= Box(pane).X + 0.5, "header clear of the pane");
            Assert.True(Box(composer).Right <= Box(pane).X + 0.5, "composer clear of the pane");
            Assert.True(Named<GridSplitter>(w, "SidePaneSplitter").IsVisible);
            Assert.Equal(width >= 1040, vm.ShowSidebar); // the sidebar gives way first
        }

        // With the preview as well: both beside the conversation when they fit, else the preview covers the conversation
        vm.IsPreviewOpen = true;
        await Settle(300);
        var preview = Named<PreviewPanel>(w, "PreviewPanel");
        if (width >= 1180)
        {
            Assert.Equal(0, preview.ZIndex);
            Assert.True(header.Bounds.Width >= 420 - 0.5, $"conversation {header.Bounds.Width} px");
            Assert.True(preview.Bounds.Width >= 320 - 0.5 && pane.Bounds.Width >= 280 - 0.5);
            Assert.True(Box(pane).Right <= Box(preview).X + 0.5, "pane and preview side by side");
        }
        else Assert.True(preview.ZIndex > 0);
        if (!over) Assert.Equal(0, pane.ZIndex);
        if (width >= 1180)
        {
            // Both docked: the preview's splitter sits between the two panels
            Assert.Equal(4, Grid.GetColumn(preview));
            Assert.Equal(3, Grid.GetColumn(Named<GridSplitter>(w, "PreviewSplitter")));
        }

        // The pane closes: the preview alone takes the columns next to the conversation (its splitter resizes the two)
        vm.ClosePaneCommand.Execute(null);
        await Settle(300);
        if (width >= 900)
        {
            Assert.Equal(2, Grid.GetColumn(preview));
            Assert.Equal(1, Grid.GetColumn(Named<GridSplitter>(w, "PreviewSplitter")));
            Assert.Equal(0, preview.ZIndex);
            Assert.True(header.Bounds.Width >= 420 - 0.5 && Box(header).Right <= Box(preview).X + 0.5);
        }

        // Closing gives the conversation its room back
        vm.IsPreviewOpen = false;
        await Settle(300);
        Assert.Equal(Box(main).Width, header.Bounds.Width, 1);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Jobs_are_never_asked_of_an_omp_that_does_not_list_them()
    {
        // An omp that does not run /jobs over RPC would take it for a message to the model
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
        Assert.Single(vm.Rows.OfType<UserRowViewModel>());
        Assert.True(vm.Tasks.IsEmpty);
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
        Assert.Equal("2 of 5 done", vm.Plan.ProgressText); // the dropped task counts neither way
        var rows = Named<ItemsControl>(w, "PlanPhases").GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("plan-task")).ToList();
        Assert.Equal(6, rows.Count);
        Assert.Single(rows, r => r.Classes.Contains("current"));

        await SendAsync(vm, "go on", () => vm.Plan.Changes.Count > 1);
        await SendAsync(vm, "new plan", () => vm.Plan.HasEarlierPlans);
        Assert.Equal("0 of 2 done", vm.Plan.ProgressText);
        Assert.Equal("3 of 6 done", Assert.Single(vm.Plan.EarlierPlans).Summary);
        Assert.Contains(vm.Plan.Changes, c => c.What == "Completed");

        vm.ClosePaneCommand.Execute(null);
        await Settle();
        Assert.True(card.IsEffectivelyVisible);

        // omp restarts on the session: the history is read back from the session's saved todo results
        var changes = vm.Plan.Changes.Select(c => (c.What, c.Content)).ToList();
        Assert.True(await vm.RestartOmpToApplyAsync());
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Plan.HasEarlierPlans, "history after the restart");
        Assert.Equal(changes, vm.Plan.Changes.Select(c => (c.What, c.Content)));
        Assert.Equal("3 of 6 done", vm.Plan.EarlierPlans.Single().Summary);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Background_tasks_show_subagents_and_jobs_and_the_menu_counts_what_runs()
    {
        var (w, vm) = await OpenAsync("subagents", 1180);
        await SendAsync(vm, "look into it", () => vm.Tasks.Subagents.Count == 3);
        // After the turn the jobs are read even with the pane closed: the ⋮ shows that something runs
        await Until(() => vm.Tasks.RunningCount == 2, "jobs read after the turn");
        Assert.True(Named<Border>(w, "ViewsDot").IsEffectivelyVisible);
        vm.ShowPaneCommand.Execute(SidePane.Tasks);
        await Until(() => vm.Tasks.HasJobs, "jobs read");
        await Settle();
        // sa-3 runs in the background, and the bash job; the async task job is sa-3 itself
        Assert.Equal(2, vm.Tasks.RunningCount);
        Assert.Equal(["Watch the dev server", "Review the plan pane", "Find where todos are read"], vm.Tasks.Subagents.Select(r => r.Title));
        Assert.Equal(["Running", "Failed", "Done"], vm.Tasks.Subagents.Select(r => r.StatusText));
        Assert.Equal(3, Named<ItemsControl>(w, "SubagentList").GetVisualDescendants().OfType<ToggleButton>().Count());

        var menu = OpenViewsMenu(w);
        await Settle();
        var count = Named<Border>(Item(menu, "ViewTasksItem"), "TasksCount");
        Assert.True(count.IsEffectivelyVisible);
        Assert.Equal("2", ((TextBlock)count.Child!).Text);
        ViewsButton(w).Flyout!.Hide();

        // A card opens to its conversation
        var first = vm.Tasks.Subagents.Single(r => r.Id == "sa-1");
        first.IsExpanded = true;
        await first.LoadMessagesCommand.ExecuteAsync(null);
        Assert.True(first.MessagesLoaded);
        Assert.Contains(first.Messages, m => m.Role == "tool");
        await vm.DisposeAsync();
        w.Close();
    }
}
