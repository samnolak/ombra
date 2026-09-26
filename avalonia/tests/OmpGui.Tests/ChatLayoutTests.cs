using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The chat window's layout under change (design review 3): statuses that do not move things, a conversation that stays
/// on its latest message through resizes and panels, side panels that never crush it, a composer that stays on screen.
/// </summary>
public sealed class ChatLayoutTests
{
    private static async Task Settle(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
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

    private static async Task<(MainWindow w, MainViewModel vm)> Open(string scenario, double width = 1180, double height = 760, string? pace = null)
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("layout")), new LaunchRequest(TestProcesses.TempDir("layout-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (w, vm);
    }

    private static ScrollViewer Scroll(Window w) => w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<ScrollViewer>().First();

    private static bool AtBottom(Window w)
    {
        using var frame = w.CaptureRenderedFrame(); // what is drawn: laid out and scrolled
        var sv = Scroll(w);
        return sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 2;
    }

    private static double Top(Window w, Control c) => c.TranslatePoint(new Point(0, 0), w)!.Value.Y;

    private static async Task RunSession(MainViewModel vm)
    {
        Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", "1");
        try
        {
            vm.ComposerText = "change the greeting to Hello";
            vm.SendCommand.Execute(null);
            await Until(() => vm.Phase == SessionPhase.Running, "running");
            await Until(() => vm.Phase == SessionPhase.Ready, "run end");
            await Settle();
        }
        finally { Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", null); }
    }

    [AvaloniaFact]
    public async Task Starting_and_ending_a_run_moves_neither_the_composer_nor_the_conversation_bottom()
    {
        var (w, vm) = await Open("session");
        vm.ComposerText = "first";
        vm.SendCommand.Execute(null); // the first message moves the composer down from the middle (a new session)
        await Until(() => vm.Phase == SessionPhase.Ready, "first run end");
        await Settle();
        var composer = w.FindControl<Border>("ComposerBox")!;
        var slot = w.FindControl<Panel>("ActivitySlot")!;
        var idleTop = Top(w, composer);
        var idleViewport = Scroll(w).Viewport.Height;
        Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", "60");
        try
        {
            vm.ComposerText = "second";
            vm.SendCommand.Execute(null);
            await Until(() => vm.Phase == SessionPhase.Running, "running");
            await Settle(200);
            Assert.True(w.FindControl<Grid>("ActivityLine")!.IsEffectivelyVisible);
            Assert.Equal(idleTop, Top(w, composer));
            Assert.Equal(20, slot.Bounds.Height);
            Assert.Equal(idleViewport, Scroll(w).Viewport.Height);
            Assert.DoesNotContain("Ready", vm.StatusText);
            await Until(() => vm.StatusText.StartsWith("Running ", StringComparison.Ordinal) || vm.StatusText.StartsWith("Thinking", StringComparison.Ordinal), "an activity verb", 10);
        }
        finally { Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", null); }
        await Until(() => vm.Phase == SessionPhase.Ready, "run end");
        await Settle();
        Assert.False(w.FindControl<Grid>("ActivityLine")!.IsEffectivelyVisible);
        Assert.Equal(idleTop, Top(w, composer));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Replies_with_only_tool_calls_take_no_room_and_only_the_turn_end_has_actions()
    {
        var (w, vm) = await Open("session");
        await RunSession(vm);
        var replies = vm.Rows.OfType<AssistantRowViewModel>().ToList();
        Assert.Contains(replies, r => !r.HasContent);
        foreach (var empty in replies.Where(r => !r.HasContent))
        {
            var container = w.FindControl<ListBox>("Transcript")!.ContainerFromItem(empty);
            if (container is not null) Assert.True(container.Bounds.Height < 1, "an empty reply took " + container.Bounds.Height);
        }
        Assert.Equal([replies.Last(r => r.HasText)], replies.Where(r => r.ShowActions));
        Assert.Contains(vm.Rows.OfType<ToolRowViewModel>(), t => t.Name == "todo" && t.Summary == "init · 3 items");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Resizes_and_panels_keep_the_latest_message_in_view_until_the_reader_scrolls_away()
    {
        var (w, vm) = await Open("session", 1440, 900);
        await RunSession(vm);
        Assert.True(AtBottom(w));
        foreach (var (wd, ht) in new[] { (1180, 760), (1000, 700), (860, 640), (760, 600), (640, 560), (480, 360), (1180, 760) })
        {
            w.Width = wd; w.Height = ht;
            await Settle();
            Assert.True(AtBottom(w), $"not on the latest message at {wd}x{ht}");
        }
        vm.ToggleTerminalCommand.Execute(null);
        await Settle(600);
        Assert.True(AtBottom(w), "terminal opened");
        vm.ToggleTerminalCommand.Execute(null);
        vm.IsPreviewOpen = true;
        await Settle();
        Assert.True(AtBottom(w), "preview opened");
        vm.IsPreviewOpen = false;
        vm.IsTodoExpanded = true;
        await Settle();
        Assert.True(AtBottom(w), "task list opened");

        // The reader scrolls up: the conversation stays there, and a resize does not pull it down
        var list = w.FindControl<ListBox>("Transcript")!;
        var p = list.TranslatePoint(new Point(list.Bounds.Width / 2, 100), w)!.Value;
        var before = Scroll(w).Offset.Y;
        var hit = w.InputHitTest(p) as Avalonia.Visual;
        for (var i = 0; i < 6; i++) w.MouseWheel(p, new Vector(0, 3));
        await Settle();
        Assert.False(AtBottom(w), $"offset {before} -> {Scroll(w).Offset.Y}, extent {Scroll(w).Extent.Height}, viewport {Scroll(w).Viewport.Height}, under the pointer: {hit?.GetType().Name} in {string.Join(" < ", (hit?.GetVisualAncestors() ?? []).Take(6).Select(v => v.GetType().Name))}");
        w.Height = 700;
        await Settle();
        Assert.False(AtBottom(w), "a resize pulled the reader back down");
        Assert.True(w.FindControl<Button>("JumpToLatest")!.IsVisible || !AtBottom(w));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_preview_never_squeezes_the_conversation_below_its_minimum()
    {
        var (w, vm) = await Open("normal", 1180, 760);
        vm.IsPreviewOpen = true;
        foreach (var width in new[] { 1440, 1180, 1000, 900, 800, 700, 560 })
        {
            w.Width = width;
            await Settle();
            using var frame = w.CaptureRenderedFrame();
            var header = w.FindControl<Border>("HeaderBar")!;
            var preview = w.FindControl<Control>("PreviewPanel")!;
            var covers = Grid.GetColumnSpan(preview) > 1;
            if (!covers) Assert.True(header.Bounds.Width >= 419, $"conversation {header.Bounds.Width:0} px wide at {width}");
            else Assert.True(preview.Bounds.Width >= header.Bounds.Width - 1, "the covering preview spans the column");
            Assert.True(preview.Bounds.Width >= 319, $"preview {preview.Bounds.Width:0} px at {width}");
        }
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_question_on_a_low_window_keeps_the_composer_on_screen()
    {
        var (w, vm) = await Open("approval", 480, 360);
        vm.ComposerText = "do the thing";
        vm.SendCommand.Execute(null);
        await Until(() => vm.HasDialog, "approval");
        await Settle();
        using var frame = w.CaptureRenderedFrame();
        var composer = w.FindControl<Border>("ComposerBox")!;
        Assert.True(Top(w, composer) + composer.Bounds.Height <= w.Bounds.Height + 0.5, "the composer is cut off at the bottom");
        var allow = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AllowButton");
        var scroll = w.FindControl<ScrollViewer>("CardsScroll")!;
        var y = allow.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
        Assert.True(y >= 0 && y + allow.Bounds.Height <= scroll.Bounds.Height + 0.5, "Allow is scrolled out of view");
        await vm.DisposeAsync();
        w.Close();
    }

    [Fact]
    public void A_running_tool_shows_its_newest_output_lines_and_folds_them_when_done()
    {
        var row = new ToolRowViewModel(new ToolItem(1, "t", "bash", "{}", ToolStatus.Running, null, "$ make"));
        Assert.False(row.ShowTail);
        row.Update(new ToolItem(1, "t", "bash", "{}", ToolStatus.Running, "a\n\nb\nc\nd\ne\n", "$ make"));
        Assert.True(row.ShowTail);
        Assert.Equal("b\nc\nd\ne", row.Tail);
        row.IsExpanded = true;
        Assert.False(row.ShowTail); // the full output is open instead
        row.IsExpanded = false;
        row.Update(new ToolItem(1, "t", "bash", "{}", ToolStatus.Succeeded, "a\nb\n", "$ make"));
        Assert.False(row.ShowTail);
    }

    [Fact]
    public void Thinking_reads_live_then_says_how_long_it_took()
    {
        var row = new AssistantRowViewModel(new AssistantItem(1, "", "", true, null, null));
        Assert.False(row.HasContent);
        row.Update(new AssistantItem(1, "", "hmm", true, null, null));
        Assert.True(row.IsThinkingLive);
        Assert.Equal("Thinking…", row.ThinkingLabel);
        row.Update(new AssistantItem(1, "Answer", "hmm", true, null, null));
        Assert.False(row.IsThinkingLive);
        Assert.Matches(@"^Thought for \d+s$", row.ThinkingLabel);
        Assert.False(row.ShowActions); // streaming, and not known to end the turn
        row.Update(new AssistantItem(1, "Answer", "hmm", false, "stop", null));
        row.IsTurnEnd = true;
        Assert.True(row.ShowActions);
        // Loaded from history: no timing
        Assert.Equal("Thinking", new AssistantRowViewModel(new AssistantItem(2, "x", "old", false, "stop", null)).ThinkingLabel);
    }
}
