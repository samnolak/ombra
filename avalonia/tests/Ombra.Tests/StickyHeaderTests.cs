using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Xunit;

namespace Ombra.Tests;

/// <summary>
/// An open block's header stays at the top of the conversation while the block is on screen (Controls/StickyHeader,
/// TranscriptPanel.StickyBlock): a command's Shell card bar, a step group's line with its open steps' bars under it.
/// Clicking the stuck header folds the block; past the block's end it scrolls away with it.
/// </summary>
public sealed class StickyHeaderTests
{
    private static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pending work run, then laid out and drawn, twice (a scroll lays out, then the headers are placed).</summary>
    private static void Frame(Window w)
    {
        for (var i = 0; i < 2; i++)
        {
            Dispatcher.UIThread.RunJobs();
            using (w.CaptureRenderedFrame()) { }
        }
    }

    private static object Text(string t) => new { type = "text", text = t };
    private static object Call(string id, string name, object args) => new { type = "toolCall", id, name, arguments = args };
    private static object Result(string id, string tool, int lines, string line) =>
        new { role = "toolResult", toolCallId = id, toolName = tool, content = new[] { Text(string.Join("\n", Enumerable.Range(1, lines).Select(n => string.Format(line, n)))) }, isError = false };

    /// <summary>A command with 200 lines of output, two reads of 120 lines (a step group), then a few exchanges.</summary>
    private static IEnumerable<object> Conversation()
    {
        yield return new { role = "user", content = new[] { Text("Run the tests") } };
        yield return new { role = "assistant", content = new object[] { Text("Running them."), Call("b1", "bash", new { command = "pytest -q" }) }, stopReason = "toolUse" };
        yield return Result("b1", "bash", 200, "tests/test_{0}.py::test_case PASSED");
        yield return new { role = "assistant", content = new[] { Text("All 200 pass.") }, stopReason = "stop" };
        yield return new { role = "user", content = new[] { Text("Read the two modules") } };
        yield return new
        {
            role = "assistant",
            content = new object[] { Call("r1", "read", new { path = "src/a.py" }), Call("r2", "read", new { path = "src/b.py" }) },
            stopReason = "toolUse",
        };
        yield return Result("r1", "read", 120, "a = {0}");
        yield return Result("r2", "read", 120, "b = {0}");
        yield return new { role = "assistant", content = new[] { Text("Both modules are read.") }, stopReason = "stop" };
        yield return new { role = "user", content = new[] { Text("Rename the settings") } };
        yield return new { role = "assistant", content = new object[] { Call("e1", "edit", new { path = "src/settings.py", oldText = "a", newText = "b" }) }, stopReason = "toolUse" };
        yield return new
        {
            role = "toolResult", toolCallId = "e1", toolName = "edit", content = new[] { Text("Edited.") }, isError = false,
            details = new { diff = string.Join("\n", Enumerable.Range(1, 60).Select(n => n % 4 == 0 ? $"-{n,4}|old_setting_{n} = {n}" : n % 4 == 1 ? $"+{n,4}|new_setting_{n} = {n}" : $" {n,4}|setting_{n} = {n}")) },
        };
        yield return new { role = "assistant", content = new[] { Text("The settings are renamed.") }, stopReason = "stop" };
        foreach (var m in ChatScrollTests.LongConversation(4)) yield return m;
    }

    private static async Task<(MainWindow W, MainViewModel Vm, ListBox List, ScrollViewer Sv)> Open()
    {
        var root = TestProcesses.TempDir("sticky-sessions");
        var cwd = SessionCatalog.ResolveLinks(TestProcesses.TempDir("sticky-project"));
        var file = ChatScrollTests.WriteSession(root, cwd, "Sticky headers", Conversation());
        var s = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(cwd, ResumeSessionFile: file));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(a => a.Text.StartsWith("Module 4 is fine", StringComparison.Ordinal)),
            "the session loaded");
        // The finished turns fold their steps under "Worked for…": open, as a reader does to see them
        foreach (var fold in vm.Rows.OfType<TurnFoldRowViewModel>()) fold.IsExpanded = true;
        Frame(w);
        var list = w.FindControl<ListBox>("Transcript")!;
        var sv = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        // A wheel turn up leaves the latest message (Views/ChatScrollController): the view is the reader's to move
        w.MouseWheel(sv.TranslatePoint(new Point(sv.Bounds.Width / 2, sv.Bounds.Height / 2), w)!.Value, new Vector(0, 1));
        Frame(w);
        return (w, vm, list, sv);
    }

    private static double TopIn(Visual v, Visual to) => v.TranslatePoint(default, to)!.Value.Y;

    /// <summary>Scrolls so the top of the view is <paramref name="by"/> px below <paramref name="v"/>'s top.</summary>
    private static void ScrollTo(Window w, ScrollViewer sv, Visual v, double by)
    {
        sv.Offset = new Vector(0, sv.Offset.Y + TopIn(v, sv) + by);
        Frame(w);
    }

    private static void Click(Window w, Control c)
    {
        var at = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)!.Value;
        w.MouseDown(at, Avalonia.Input.MouseButton.Left);
        w.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Frame(w);
    }

    private static double Shift(Visual v) => (v.RenderTransform as TranslateTransform)?.Y ?? 0;

    [AvaloniaFact]
    public void A_changed_files_header_sticks_and_its_chevron_folds_the_change()
    {
        var folded = 0;
        var card = new DiffView
        {
            Change = new FileChange("src/new.py", FileChangeKind.Created, string.Join("\n", Enumerable.Range(1, 40).Select(n => $"+{n}|x = {n}"))),
            CollapseCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => folded++),
        };
        var head = ((Border)card.Content!).GetLogicalDescendants().OfType<Border>().Single(b => b.Classes.Contains("diff-head"));
        Assert.True(StickyHeader.GetIsSticky(head));
        Assert.Contains(head.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("New file"));
        var chevron = head.GetLogicalDescendants().OfType<Button>().Single(b => b.Classes.Contains("diff-collapse"));
        Assert.True(chevron.IsVisible);
        chevron.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, folded);
        // Without a command (outside a tool row) there is nothing to fold it into
        card.CollapseCommand = null;
        Assert.False(chevron.IsVisible);
    }

    [AvaloniaFact]
    public async Task An_open_console_keeps_its_bar_at_the_top_until_it_ends_and_the_bar_folds_it()
    {
        var (w, vm, list, sv) = await Open();
        var bash = vm.Rows.OfType<ToolRowViewModel>().Single(t => t.Command == "pytest -q");
        bash.IsExpanded = true;
        list.ScrollIntoView(bash);
        Frame(w);
        // The bar: a border (it is what moves) around the button that folds the console
        Border Bar() => w.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("card-head") && ReferenceEquals(b.DataContext, bash) && b.IsEffectivelyVisible);
        var bar = Bar();
        var button = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("card-header"));
        var block = (Control)bar.GetVisualParent()!;
        Assert.True(block.Bounds.Height > 1500, $"the open console is long: {block.Bounds.Height}");
        Assert.Contains(bar.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Shell"));
        Assert.Contains(bar.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "pytest -q");

        // Scrolled 600 px into the console: its bar is at the top of the view, over the output
        ScrollTo(w, sv, block, 600);
        Assert.Equal(0, TopIn(bar, sv), 1);
        Assert.Contains("stuck", bar.Classes);
        Assert.Equal(1, bar.ZIndex);
        // Scrolled further, it stays there, and a click there reaches it
        ScrollTo(w, sv, block, 1200);
        Assert.Equal(0, TopIn(bar, sv), 1);
        var at = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2), w)!.Value;
        Assert.Same(button, (w.InputHitTest(at) as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault());

        // Near the console's end the bar is pushed up by it, then gone with it
        ScrollTo(w, sv, block, block.Bounds.Height - 10);
        Assert.Equal(TopIn(block, sv) + block.Bounds.Height, TopIn(bar, sv) + bar.Bounds.Height, 1);
        ScrollTo(w, sv, block, block.Bounds.Height + 100);
        Assert.DoesNotContain("stuck", bar.Classes);
        Assert.Equal(0, Shift(bar));
        Assert.True(TopIn(bar, sv) < -block.Bounds.Height + 50, "the bar scrolled away with its console");

        // Back in the console, the stuck bar folds it
        ScrollTo(w, sv, block, 900);
        Assert.Equal(0, TopIn(bar, sv), 1);
        Click(w, button);
        Assert.False(bash.IsExpanded);
        Assert.False(bar.IsVisible);
        Assert.DoesNotContain("stuck", bar.Classes);
        Assert.Equal(0, Shift(bar));
        w.Close();
    }

    /// <summary>
    /// The owner's report: a card scrolled into shows its bar over its last lines; folded there by that bar, the view
    /// stayed where the card's lines had been, so the row went off above the view and the view showed whatever came
    /// after it (the next exchange). Folded by its held bar, the row's line is where the bar was, in the same layout.
    /// </summary>
    [AvaloniaFact]
    public async Task A_card_folded_by_its_held_bar_leaves_its_line_where_the_bar_was()
    {
        var (w, vm, list, sv) = await Open();
        ListBoxItem Item(object row) => list.GetVisualDescendants().OfType<ListBoxItem>().Single(i => ReferenceEquals(i.DataContext, row) && i.IsVisible);
        void FoldBy(Control bar)
        {
            var at = bar.TranslatePoint(new Point(40, bar.Bounds.Height / 2), w)!.Value;
            w.MouseDown(at, Avalonia.Input.MouseButton.Left);
            w.MouseUp(at, Avalonia.Input.MouseButton.Left);
            // One layout and one frame: the view is not drawn anywhere else first
            Dispatcher.UIThread.RunJobs();
            using (w.CaptureRenderedFrame()) { }
        }
        // With OMBRA_SHOT_DIR set: the window in the dark and the light theme at each stage
        void Shot(string name)
        {
            if (Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") is not { } dir) return;
            Directory.CreateDirectory(dir);
            var app = Application.Current!;
            var before = app.RequestedThemeVariant;
            try
            {
                foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Dark, Avalonia.Styling.ThemeVariant.Light })
                {
                    app.RequestedThemeVariant = theme;
                    Frame(w);
                    using var frame = w.CaptureRenderedFrame()!;
                    frame.Save(Path.Combine(dir, $"held-bar-{name}-{theme.Key}.png".ToLowerInvariant()), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
            }
            finally
            {
                app.RequestedThemeVariant = before;
                Frame(w);
            }
        }

        // A command's card on its own, 120 px from its end: its bar on top, the card's last lines under it
        var bash = vm.Rows.OfType<ToolRowViewModel>().Single(t => t.Command == "pytest -q");
        bash.IsExpanded = true;
        list.ScrollIntoView(bash);
        Frame(w);
        Shot("1-open");
        var bar = w.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("card-head") && ReferenceEquals(b.DataContext, bash) && b.IsEffectivelyVisible);
        var block = (Control)bar.GetVisualParent()!;
        ScrollTo(w, sv, block, block.Bounds.Height - 120);
        Assert.Equal(0, TopIn(bar, sv), 1);
        Assert.Contains("stuck", bar.Classes);
        Shot("2-held");
        var answer = vm.Rows.OfType<AssistantRowViewModel>().Single(a => a.Text == "All 200 pass.");
        FoldBy(bar);
        Assert.False(bash.IsExpanded);
        var row = Item(bash);
        Assert.Equal(0, TopIn(row, sv), 1);
        Assert.Equal(row.Bounds.Height, TopIn(Item(answer), sv), 1);
        Shot("3-folded");

        // A step's output under its open group's line: folded by its bar, the step's line is under the group's
        var group = vm.Rows.OfType<StepGroupRowViewModel>().Single(g => g.Steps.All(st => st.Name == "read"));
        group.IsExpanded = true;
        foreach (var step in group.Steps) step.IsExpanded = true;
        list.ScrollIntoView(group);
        Frame(w);
        var second = group.Steps[1];
        list.ScrollIntoView(second);
        Frame(w);
        var output = w.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("tool-output") && ReferenceEquals(b.DataContext, second) && b.IsEffectivelyVisible);
        ScrollTo(w, sv, output, 600);
        var line = Item(group);
        Assert.Contains("stuck", line.Classes);
        var outputBar = output.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("card-head"));
        Assert.Equal(line.Bounds.Height, TopIn(outputBar, sv), 1);
        Shot("4-group-held");
        FoldBy(outputBar);
        Assert.False(second.IsExpanded);
        Assert.Equal(line.Bounds.Height, TopIn(Item(second), sv), 1);
        Assert.Equal(0, TopIn(Item(group), sv), 1);

        // A changed file's card, its header held over its lines (seen with OMBRA_SHOT_DIR)
        var edit = vm.Rows.OfType<ToolRowViewModel>().Single(t => t.Name == "edit");
        edit.IsExpanded = true;
        list.ScrollIntoView(edit);
        Frame(w);
        var head = w.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("diff-head") && b.IsEffectivelyVisible);
        ScrollTo(w, sv, (Control)head.GetVisualParent()!, 300);
        Assert.Contains("stuck", head.Classes);
        Shot("5-diff-held");
        w.Close();
    }

    [AvaloniaFact]
    public async Task An_open_step_group_keeps_its_line_on_top_with_its_steps_bars_under_it()
    {
        var (w, vm, list, sv) = await Open();
        var group = vm.Rows.OfType<StepGroupRowViewModel>().Single(g => g.Steps.All(s => s.Name == "read"));
        group.IsExpanded = true;
        foreach (var step in group.Steps) step.IsExpanded = true;
        list.ScrollIntoView(group);
        Frame(w);
        ListBoxItem Item(object row) => w.GetVisualDescendants().OfType<ListBoxItem>().Single(i => ReferenceEquals(i.DataContext, row) && i.IsVisible);
        var second = group.Steps[1];
        Border Output() => w.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("tool-output") && ReferenceEquals(b.DataContext, second) && b.IsEffectivelyVisible);
        list.ScrollIntoView(second);
        Frame(w);
        var output = Output();
        var outputBar = output.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("card-head"));
        Assert.Contains(outputBar.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Output"));

        // 600 px into the second step's output: the group's line at the top of the view, the step's bar right under it
        ScrollTo(w, sv, output, 600);
        var line = Item(group);
        Assert.Equal(0, TopIn(line, sv), 1);
        Assert.Contains("stuck", line.Classes);
        Assert.Equal(line.Bounds.Height, TopIn(outputBar, sv), 1);
        Assert.Contains("stuck", outputBar.Classes);

        // Past the group's last step, nothing is stuck
        var lastRow = Item(second);
        ScrollTo(w, sv, lastRow, lastRow.Bounds.Height + 100);
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<ListBoxItem>(), i => i.Classes.Contains("stuck") || Shift(i) != 0);
        Assert.DoesNotContain("stuck", outputBar.Classes);

        // Back in it, the stuck line folds the group
        ScrollTo(w, sv, Output(), 400);
        line = Item(group);
        Assert.Equal(0, TopIn(line, sv), 1);
        Click(w, line.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Classes.Contains("tool-row")));
        Assert.False(group.IsExpanded);
        Frame(w);
        Assert.DoesNotContain(w.GetVisualDescendants().OfType<ListBoxItem>(), i => i.Classes.Contains("stuck"));
        w.Close();
    }
}
