using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Xunit;

namespace Ombra.Tests;

/// <summary>
/// Folding and opening rows (commands' Shell cards, step groups, "Worked for…" lines) while the long conversation is
/// scrolled about: every row on screen shows the state of its row (chevron, card, bar), an open card shows all its
/// lines at its own height, a folded row takes no room, a header held at the top of the view is where its block crosses
/// it, and a row folded by its header stays on screen.
/// </summary>
public sealed class FoldScrollTests
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
    private static object Result(string id, string tool, IEnumerable<string> lines) =>
        new { role = "toolResult", toolCallId = id, toolName = tool, content = new[] { Text(string.Join("\n", lines)) }, isError = false };

    /// <summary>25 lines like the owner's lab run: sizes, timings, a count every fifth line.</summary>
    private static IEnumerable<string> Lab(int i) =>
        Enumerable.Range(1, 25).Select(n => n % 5 == 0 ? $"{n * i}" : $"pylint-dev__pylint-{4900 + n}  1.41 GiB  {n * 21} s");

    /// <summary>Exchanges each with a command of 25 lines on its own, then one in a step group with a read and a short
    /// command, then the answer.</summary>
    private static IEnumerable<object> Conversation(int exchanges)
    {
        for (var i = 1; i <= exchanges; i++)
        {
            var lab = $"date '+%H:%M'; cd .temp/lab/screen{i}; for d in */; do du -sh \"$d\"; done";
            yield return new { role = "user", content = new[] { Text($"How is screen {i} going?") } };
            yield return new { role = "assistant", content = new object[] { Text($"Looking at screen {i}."), Call($"s{i}", "bash", new { command = lab }) }, stopReason = "toolUse" };
            yield return Result($"s{i}", "bash", Lab(i));
            yield return new { role = "assistant", content = new[] { Text($"Screen {i} is at {i * 4} of 45.") }, stopReason = "stop" };
            yield return new { role = "user", content = new[] { Text($"And the files of screen {i}?") } };
            yield return new
            {
                role = "assistant",
                content = new object[] { Call($"b{i}", "bash", new { command = lab + " # again" }), Call($"r{i}", "read", new { path = $"src/m{i}.py" }), Call($"g{i}", "bash", new { command = $"ls screen{i}" }) },
                stopReason = "toolUse",
            };
            yield return Result($"b{i}", "bash", Lab(i + 1));
            yield return Result($"r{i}", "read", Enumerable.Range(1, 20).Select(n => $"x = {n}"));
            yield return Result($"g{i}", "bash", Enumerable.Range(1, 8).Select(n => $"file{n}"));
            yield return new { role = "assistant", content = new[] { Text($"Screen {i} has its files.") }, stopReason = "stop" };
        }
    }

    private static double TopIn(Visual v, Visual to) => v.TranslatePoint(default, to)!.Value.Y;
    private static double Shift(Visual v) => (v.RenderTransform as TranslateTransform)?.Y ?? 0;

    /// <summary>What is wrong with the rows on screen: each one checked against its row.</summary>
    private static List<string> Check(ListBox list, ScrollViewer sv, string when)
    {
        var problems = new List<string>();
        var items = list.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsVisible).ToList();
        // The line headers are held under: the bottom of the row held at the top of the view (an open group's line)
        var line = items.Where(i => i.Classes.Contains("stuck")).Select(i => TopIn(i, sv) + i.Bounds.Height).DefaultIfEmpty(0).Max();
        foreach (var item in items)
        {
            var top = TopIn(item, sv);
            if (top + item.Bounds.Height < 0 || top > sv.Viewport.Height) continue;
            var name = item.DataContext?.GetType().Name;
            if (Math.Abs(item.Bounds.Height - item.DesiredSize.Height) > 0.5)
                problems.Add($"{when}: {name} laid out {item.Bounds.Height} high, measured {item.DesiredSize.Height}");
            if (item.DataContext is RowViewModel { IsFolded: true })
            {
                if (item.Bounds.Height > 0.5) problems.Add($"{when}: folded {name} takes {item.Bounds.Height}");
                continue;
            }
            if (item.DataContext is TurnFoldRowViewModel fold
                && item.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Classes.Contains("turn-fold-head")).IsChecked != fold.IsExpanded)
                problems.Add($"{when}: \"{fold.Label}\" shows {(fold.IsExpanded ? "folded" : "open")}");
            if (item.DataContext is StepGroupRowViewModel group
                && item.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Classes.Contains("tool-row")).IsChecked != group.IsExpanded)
                problems.Add($"{when}: group \"{group.Summary}\" shows {(group.IsExpanded ? "folded" : "open")}");
            if (item.DataContext is not ToolRowViewModel t) continue;
            if (item.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Classes.Contains("tool-row")).IsChecked != t.IsExpanded)
                problems.Add($"{when}: {t.Command}: its chevron shows {(t.IsExpanded ? "folded" : "open")}");
            var console = item.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("console"));
            if (console.IsVisible != t.ShowConsole) problems.Add($"{when}: {t.Command}: card shown {console.IsVisible}, row says {t.ShowConsole}");
            if (!t.ShowConsole) continue;
            var bar = console.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("card-head"));
            if (bar.IsVisible != t.IsExpanded) problems.Add($"{when}: {t.Command}: bar shown {bar.IsVisible}, row open {t.IsExpanded}");
            if (!t.IsExpanded)
            {
                if (Shift(bar) != 0 || bar.Classes.Contains("stuck")) problems.Add($"{when}: {t.Command}: the folded card's bar is still held");
                continue;
            }
            var text = console.GetVisualDescendants().OfType<SelectableTextBlock>().First(b => b.Classes.Contains("console-output"));
            var lines = t.ConsoleOutput.Split('\n').Length;
            if (text.Text != t.ConsoleOutput || lines < 8) problems.Add($"{when}: {t.Command}: the open card shows {text.Text?.Split('\n').Length} of its lines");
            if (Math.Abs(text.Bounds.Height + text.Margin.Top - text.DesiredSize.Height) > 0.5 || text.Bounds.Height < lines * 19 - 0.5)
                problems.Add($"{when}: {t.Command}: its {lines} lines laid out {text.Bounds.Height} high, measured {text.DesiredSize.Height - text.Margin.Top}");
            // The bar: where it was laid out, or held on the line while its card crosses it
            var block = (Control)bar.GetVisualParent()!;
            var blockTop = TopIn(block, sv);
            if (blockTop + block.Bounds.Height <= line + 0.5) continue;
            var expected = blockTop >= line ? blockTop : Math.Min(line, blockTop + block.Bounds.Height - bar.Bounds.Height);
            if (Math.Abs(TopIn(bar, sv) - expected) > 1) problems.Add($"{when}: {t.Command}: card at {blockTop:0} (line {line:0}), its bar at {TopIn(bar, sv):0}, not {expected:0}");
        }
        return problems;
    }

    /// <summary>
    /// A random walk of a reader through a long conversation: clicking rows' chevrons (commands, groups, "Worked for…")
    /// and the bars of open cards, scrolling to anywhere, to either end, and by the wheel. After every step the rows on
    /// screen are checked (<see cref="Check"/>); a row folded by its card's bar is still on screen.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(7)]
    [InlineData(42)]
    public async Task Rows_keep_their_fold_state_and_height_while_folded_opened_and_scrolled(int seed)
    {
        var root = TestProcesses.TempDir("fold-sessions");
        var cwd = SessionCatalog.ResolveLinks(TestProcesses.TempDir("fold-project"));
        var file = ChatScrollTests.WriteSession(root, cwd, "Folds", Conversation(8));
        var s = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(cwd, ResumeSessionFile: file));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(a => a.Text == "Screen 8 has its files."), "the session loaded");
        while (vm.History.HasOlder) await vm.LoadOlderMessagesCommand.ExecuteAsync(null);
        Frame(w);
        var list = w.FindControl<ListBox>("Transcript")!;
        var sv = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        Point Middle() => sv.TranslatePoint(new Point(sv.Bounds.Width / 2, sv.Bounds.Height / 2), w)!.Value;
        // A wheel turn up leaves the latest message (Views/ChatScrollController): the view is the reader's to move
        w.MouseWheel(Middle(), new Vector(0, 1));
        Frame(w);
        bool OnScreen(Control c) => TopIn(c, sv) is var y && y >= 0 && y + c.Bounds.Height <= sv.Viewport.Height;
        void Click(Control c)
        {
            var at = c.TranslatePoint(new Point(10, c.Bounds.Height / 2), w)!.Value;
            w.MouseDown(at, Avalonia.Input.MouseButton.Left);
            w.MouseUp(at, Avalonia.Input.MouseButton.Left);
        }
        var rnd = new Random(seed);
        var problems = new List<string>();
        for (var step = 0; step < 300 && problems.Count == 0; step++)
        {
            string what;
            ToolRowViewModel? foldedByBar = null;
            double? heldAt = null;
            switch (rnd.Next(4))
            {
                case 0 when list.GetVisualDescendants().OfType<ToggleButton>()
                    .Where(b => (b.Classes.Contains("tool-row") || b.Classes.Contains("turn-fold-head")) && b.IsEffectivelyVisible && b.IsEnabled && OnScreen(b)).ToList() is { Count: > 0 } chevrons:
                    var chevron = chevrons[rnd.Next(chevrons.Count)];
                    what = $"a click on {chevron.DataContext?.GetType().Name} {(chevron.DataContext as ToolRowViewModel)?.Command}";
                    Click(chevron);
                    break;
                case 1 when list.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("card-header") && b.IsEffectivelyVisible && OnScreen(b)).ToList() is { Count: > 0 } bars:
                    var bar = bars[rnd.Next(bars.Count)];
                    foldedByBar = bar.DataContext as ToolRowViewModel;
                    var held = bar.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card-head"));
                    if (held.Classes.Contains("stuck")) heldAt = TopIn(held, sv);
                    what = $"a click on the bar of {foldedByBar?.Command}";
                    Click(bar);
                    break;
                case 2:
                    var y = rnd.Next(3) == 0 ? (rnd.Next(2) == 0 ? 0 : sv.Extent.Height) : rnd.NextDouble() * sv.Extent.Height;
                    what = $"a scroll to {y:0}";
                    sv.Offset = new Vector(0, y);
                    break;
                default:
                    var notches = rnd.Next(-3, 4);
                    what = $"{notches} wheel notches";
                    w.MouseWheel(Middle(), new Vector(0, notches));
                    break;
            }
            Frame(w);
            var when = $"step {step}, after {what} (offset {sv.Offset.Y:0})";
            problems.AddRange(Check(list, sv, when));
            if (foldedByBar is not null)
            {
                var row = list.GetVisualDescendants().OfType<ListBoxItem>().SingleOrDefault(i => ReferenceEquals(i.DataContext, foldedByBar) && i.IsVisible);
                if (row is null || TopIn(row, sv) + row.Bounds.Height <= 0 || TopIn(row, sv) >= sv.Viewport.Height)
                    problems.Add($"{when}: the row folded by its bar is off screen{(row is null ? "" : $", at {TopIn(row, sv):0}")}");
                // Folded by its bar held at the top of the view: the row's line is where the bar was (lower only when the
                // conversation ends before a view's height under it: the view is then at its end)
                else if (heldAt is { } at && Math.Abs(TopIn(row, sv) - at) > 1 && !(TopIn(row, sv) > at && sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 0.5))
                    problems.Add($"{when}: the row folded by its bar held at {at:0} is at {TopIn(row, sv):0} (end of the view at {sv.Extent.Height - sv.Viewport.Height:0})");
            }
        }
        w.Close();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
