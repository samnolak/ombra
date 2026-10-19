using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

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

    /// <summary>A low new-session window drops the logo, then the starters; nothing of the greeting is cut off above.</summary>
    [AvaloniaFact]
    public async Task A_low_window_keeps_the_whole_greeting_visible()
    {
        var (w, vm) = await Open("normal");
        var area = w.FindControl<Grid>("ConversationArea")!;
        var empty = w.FindControl<StackPanel>("EmptyState")!;
        foreach (var width in new double[] { 480, 800, 1180 })
            for (double height = 360; height <= 760; height += 40)
            {
                w.Width = width;
                w.Height = height;
                await Settle(60);
                var box = new Rect(area.TranslatePoint(default, w)!.Value, area.Bounds.Size);
                foreach (var c in empty.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is TextBlock or Button or Image && c.Bounds.Height > 0))
                {
                    var r = new Rect(c.TranslatePoint(default, w)!.Value, c.Bounds.Size);
                    Assert.True(r.Top >= box.Top - 0.5 && r.Bottom <= box.Bottom + 0.5, $"{width}x{height}: {c.GetType().Name} {r} is cut off ({box})");
                }
            }
        Assert.True(w.FindControl<WrapPanel>("Greeting")!.IsEffectivelyVisible);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>A new session's greeting, message box and the row under it sit together in the middle of the frame,
    /// with the terminal open too (it takes the bottom of the frame: the group centres in what is left).</summary>
    [AvaloniaFact]
    public async Task The_new_session_group_is_centred_in_the_frame_with_and_without_the_terminal()
    {
        var (w, vm) = await Open("normal");
        var area = w.FindControl<Grid>("ConversationArea")!;
        var column = w.FindControl<Grid>("MainColumn")!;
        var empty = w.FindControl<StackPanel>("EmptyState")!;
        var footer = w.FindControl<Grid>("ComposerFooter")!;
        foreach (var terminal in new[] { false, true })
        {
            if (terminal) vm.ToggleTerminalCommand.Execute(null);
            foreach (var (width, height) in new[] { (1180.0, 760.0), (900.0, 900.0), (1180.0, 1000.0) })
            {
                w.Width = width;
                w.Height = height;
                await Settle(300);
                var groupTop = empty.GetVisualChildren().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Height > 0)
                    .Min(c => c.TranslatePoint(default, w)!.Value.Y);
                var groupBottom = footer.TranslatePoint(new Point(0, footer.Bounds.Height), w)!.Value.Y;
                var frameTop = area.TranslatePoint(default, w)!.Value.Y;
                var frameBottom = column.TranslatePoint(new Point(0, column.Bounds.Height), w)!.Value.Y;
                var above = groupTop - frameTop;
                var below = frameBottom - groupBottom;
                Assert.True(Math.Abs(above - below) <= 2, $"terminal {terminal}, {width}x{height}: {above:0} above the greeting, {below:0} under the row below the box");
            }
        }
        await vm.DisposeAsync();
        w.Close();
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
            Assert.DoesNotContain(Loc.T("Ready"), vm.StatusText);
            var running = Loc.F("Running {0}", "\0").Split('\0')[0];
            await Until(() => vm.StatusText.StartsWith(running, StringComparison.Ordinal) || vm.StatusText.StartsWith(Loc.T("Thinking"), StringComparison.Ordinal), "an activity verb", 10);
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
        // The reply that ends the turn has the actions line (Copy, the time), always shown; no other reply has one
        var last = replies.Last(r => r.HasText);
        Assert.Equal([last], replies.Where(r => r.ShowActions));
        Assert.True(last.IsTurnEnd);
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
    public async Task Sending_goes_to_the_latest_message_and_the_jump_button_brings_a_reader_back()
    {
        var (w, vm) = await Open("session", 1180, 600);
        await RunSession(vm);
        Assert.True(AtBottom(w), "the reply was not followed to its end");
        var list = w.FindControl<ListBox>("Transcript")!;
        var jump = w.FindControl<Button>("JumpToLatest")!;
        var p = list.TranslatePoint(new Point(list.Bounds.Width / 2, 100), w)!.Value;
        async Task ScrollUp()
        {
            for (var i = 0; i < 6; i++) w.MouseWheel(p, new Vector(0, 3));
            await Settle();
            Assert.False(AtBottom(w), "the wheel did not scroll up");
            Assert.True(jump.IsVisible, "no way back to the latest message");
        }

        // Scrolled up: the ↓ button goes back down and follows again
        await ScrollUp();
        jump.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Settle();
        Assert.True(AtBottom(w), "the jump button did not reach the end");
        Assert.False(jump.IsVisible);

        // Scrolled up, then a message sent: the conversation goes to its end, the streamed reply included
        await ScrollUp();
        Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", "1");
        try
        {
            vm.ComposerText = "and again";
            vm.SendCommand.Execute(null);
            await Until(() => vm.Phase == SessionPhase.Running, "running");
            await Until(() => vm.Phase == SessionPhase.Ready, "run end");
            await Settle();
        }
        finally { Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", null); }
        Assert.True(AtBottom(w), "sending did not go to the latest message");
        Assert.False(jump.IsVisible);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_right_panel_never_squeezes_the_conversation_below_its_minimum()
    {
        var (w, vm) = await Open("normal", 1180, 760);
        vm.IsPreviewOpen = true;
        foreach (var width in new[] { 1440, 1180, 1000, 900, 800, 700, 560 })
        {
            w.Width = width;
            await Settle();
            using var frame = w.CaptureRenderedFrame();
            var header = w.FindControl<Border>("HeaderBar")!;
            var composer = w.FindControl<Border>("ComposerBox")!;
            var panel = w.FindControl<Control>("RightPanel")!;
            var covers = Grid.GetColumn(panel) == 0;
            if (!covers) Assert.True(header.Bounds.Width >= 419, $"conversation {header.Bounds.Width:0} px wide at {width}");
            else
            {
                // Over the conversation: above the message box, never on it
                var bottom = panel.TranslatePoint(new Point(0, panel.Bounds.Height), w)!.Value.Y;
                Assert.True(bottom <= composer.TranslatePoint(default, w)!.Value.Y + 0.5, $"the panel covers the composer at {width}");
            }
            Assert.True(panel.Bounds.Width >= 319, $"panel {panel.Bounds.Width:0} px at {width}");
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
        var composer = w.FindControl<Border>("ComposerBox")!;
        var scroll = w.FindControl<ScrollViewer>("CardsScroll")!;
        foreach (var size in new[] { new Size(480, 360), new Size(640, 600), new Size(480, 360) })
        {
            w.Width = size.Width;
            w.Height = size.Height;
            await Settle();
            using var frame = w.CaptureRenderedFrame();
            Assert.True(Top(w, composer) + composer.Bounds.Height <= w.Bounds.Height + 0.5, "the composer is cut off at the bottom");
            var allow = w.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AllowButton");
            var y = allow.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
            Assert.True(y >= 0 && y + allow.Bounds.Height <= scroll.Bounds.Height + 0.5, "Allow is scrolled out of view");
        }
        // Reading the command at the top must not fight a continuous answer-follow loop.
        scroll.Offset = default;
        await Settle();
        Assert.Equal(0, scroll.Offset.Y);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Ordinary_cards_keep_the_readers_scroll_position_after_layout_changes()
    {
        var (w, vm) = await Open("normal", 480, 360);
        vm.WidgetsText = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"Widget line {i}"));
        await Settle();
        var scroll = w.FindControl<ScrollViewer>("CardsScroll")!;
        Assert.True(scroll.Extent.Height - scroll.Viewport.Height > 20);
        scroll.Offset = new Vector(0, 20);
        w.Width = 500;
        await Settle();
        Assert.Equal(20, scroll.Offset.Y);
        await vm.DisposeAsync();
        w.Close();
    }

    [Fact]
    public void A_running_command_shows_its_newest_output_lines_and_folds_to_its_line_when_done()
    {
        var row = new ToolRowViewModel(new ToolItem(1, "t", "bash", "{}", ToolStatus.Running, null, "$ make"));
        Assert.Equal((true, false), (row.ShowConsole, row.ShowConsoleOutput)); // the Shell card, with the command alone
        row.Update(new ToolItem(1, "t", "bash", "{}", ToolStatus.Running, "a\n\nb\nc\nd\ne\n", "$ make"));
        Assert.Equal("c\nd\ne", row.ConsoleOutput);
        row.IsExpanded = true;
        Assert.Equal("a\n\nb\nc\nd\ne", row.ConsoleOutput); // open: all of it
        row.IsExpanded = false;
        row.Update(new ToolItem(1, "t", "bash", "{}", ToolStatus.Succeeded, "a\nb\nc\nd\n", "$ make"));
        Assert.Equal((false, true), (row.ShowConsole, row.CanExpand)); // done: its line, which opens the card
    }

    /// <summary>A tool line's tooltip is words, never the call's raw JSON: the whole command (none while its Shell card
    /// shows it), the file, or another tool's arguments one per line.</summary>
    [Fact]
    public void A_tool_lines_tooltip_says_what_it_did_in_words()
    {
        var command = new ToolRowViewModel(new ToolItem(1, "t", "bash", """{"command":"ls -la && make"}""", ToolStatus.Succeeded, "", "$ ls -la && make"));
        Assert.Equal("ls -la && make", command.Tooltip);
        command.Update(new ToolItem(1, "t", "bash", """{"command":"ls -la && make"}""", ToolStatus.Running, "a\nb", "$ ls -la && make"));
        Assert.Null(command.Tooltip); // the Shell card under the line shows the command while it runs
        var read = new ToolRowViewModel(new ToolItem(2, "r", "read", """{"path":"src/a.py"}""", ToolStatus.Succeeded, "x", "src/a.py"));
        Assert.Equal("src/a.py", read.Tooltip);
        var mcp = new ToolRowViewModel(new ToolItem(3, "m", "mcp__github__create_issue", """{"owner":"acme","repo":"weather","draft":true,"labels":["bug"]}""",
            ToolStatus.Succeeded, "", "acme · weather"));
        Assert.Equal("owner: acme\nrepo: weather\ndraft: true", mcp.Tooltip);
        foreach (var row in new[] { command, read, mcp }) Assert.DoesNotContain("{", row.Tooltip ?? "");
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("👩‍💻")]
    public void Tool_argument_tooltips_keep_whole_graphemes_within_their_budget(string glyph)
    {
        for (var cut = 1; cut < glyph.Length; cut++)
        {
            var head = new string('a', 159 - cut);
            var args = System.Text.Json.JsonSerializer.Serialize(new { description = head + glyph + " rest" });
            var row = new ToolRowViewModel(new ToolItem(1, "t", "mcp__example__call", args, ToolStatus.Succeeded, ""));
            Assert.Equal("description: " + head + "…", row.Tooltip);
        }
    }

    /// <summary>A hovered tool line's fill has rounded corners: the line reaches 8 px past the reading column, and the
    /// list item it sits in used to clip it into a square-cornered strip.</summary>
    [AvaloniaFact]
    public async Task A_hovered_tool_line_is_rounded_not_cut_square()
    {
        var (w, vm) = await Open("markdown");
        vm.ComposerText = "explain the change";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Any(), "a tool line");
        // The finished turn folds its steps under "Worked for…": open, the step line shows
        foreach (var fold in vm.Rows.OfType<TurnFoldRowViewModel>()) fold.IsExpanded = true;
        await Settle();
        var row = w.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => b.Classes.Contains("tool-row") && b.IsEffectivelyVisible);
        var at = row.TranslatePoint(default, w)!.Value;
        w.MouseMove(at + new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), RawInputModifiers.None);
        await Settle(150);
        using var frame = w.CaptureRenderedFrame()!;
        var size = frame.PixelSize;
        var buf = new byte[size.Width * size.Height * 4];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(new PixelRect(size), pin.AddrOfPinnedObject(), buf.Length, size.Width * 4); }
        finally { pin.Free(); }
        uint Px(double x, double y) => BitConverter.ToUInt32(buf, ((int)y * size.Width + (int)x) * 4) | 0xFF000000;
        var page = Px(at.X - 4, at.Y + row.Bounds.Height / 2);
        var fill = Px(at.X + 3, at.Y + row.Bounds.Height / 2);
        Assert.NotEqual(page, fill); // hovered: filled from its own left edge, not from the column's
        Assert.Equal(page, Px(at.X, at.Y)); // its corners are rounded off: the page shows there
        Assert.Equal(page, Px(at.X + row.Bounds.Width - 1, at.Y + row.Bounds.Height - 1));
        await vm.DisposeAsync();
        w.Close();
    }

    [Fact]
    public void Thinking_reads_live_then_says_how_long_it_took()
    {
        var row = new AssistantRowViewModel(new AssistantItem(1, "", "", true, null, null));
        Assert.False(row.HasContent);
        row.Update(new AssistantItem(1, "", "hmm", true, null, null));
        Assert.True(row.IsThinkingLive);
        Assert.Equal(Loc.T("Thinking…"), row.ThinkingLabel);
        row.Update(new AssistantItem(1, "Answer", "hmm", true, null, null));
        Assert.False(row.IsThinkingLive);
        Assert.Matches("^" + Loc.F("Thought for {0}s", @"\d+") + "$", row.ThinkingLabel);
        Assert.False(row.ShowActions); // streaming, and not known to end the turn
        row.Update(new AssistantItem(1, "Answer", "hmm", false, "stop", null));
        row.IsTurnEnd = true;
        Assert.True(row.ShowActions);
        // Loaded from history: no timing
        Assert.Equal(Loc.T("Thinking"), new AssistantRowViewModel(new AssistantItem(2, "x", "old", false, "stop", null)).ThinkingLabel);
    }
}
