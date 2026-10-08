using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;
using Xunit;

namespace OmpGui.Tests;

/// <summary>
/// The conversation's scrolling with a long chat and a streaming reply (P0-1/P0-2 of docs/FIX_PLAN.md): the view follows
/// the reply without a pixel of jitter, holds still for a reader who scrolled up, opens on the last row, and rows
/// changing above the view move nothing on screen.
/// </summary>
public sealed class ChatScrollTests
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

    private static object Text(string t) => new { type = "text", text = t };
    private static object Call(string id, string name, object args) => new { type = "toolCall", id, name, arguments = args };

    /// <summary>A long conversation: <paramref name="exchanges"/> times a question, a Markdown reply with two tool calls and their results, and a closing answer.</summary>
    internal static IEnumerable<object> LongConversation(int exchanges)
    {
        for (var i = 1; i <= exchanges; i++)
        {
            yield return new { role = "user", content = new[] { Text($"Question {i}: what does module {i} do with its settings?") } };
            yield return new
            {
                role = "assistant",
                content = new object[]
                {
                    Text($"## Module {i}\n\nIt reads `settings_{i}.toml` once. A few notes:\n\n- the reader caches it\n- the watcher is off\n\n```python\ndef load_{i}(path):\n    return parse(open(path).read())\n```"),
                    Call($"r{i}", "read", new { path = $"src/module_{i}.py" }),
                    Call($"b{i}", "bash", new { command = $"pytest tests/test_module_{i}.py -q" }),
                },
                stopReason = "toolUse",
            };
            yield return new { role = "toolResult", toolCallId = $"r{i}", toolName = "read", content = new[] { Text(string.Join("\n", Enumerable.Range(1, 30).Select(n => $"line {n}"))) }, isError = false };
            yield return new { role = "toolResult", toolCallId = $"b{i}", toolName = "bash", content = new[] { Text(string.Join("\n", Enumerable.Range(1, 12).Select(n => $"tests/test_module_{i}.py::test_{n} PASSED"))) }, isError = false };
            yield return new { role = "assistant", content = new[] { Text($"Module {i} is fine: **{i * 3} tests** pass, and the settings are read once.") }, stopReason = "stop" };
        }
    }

    internal static string WriteSession(string root, string cwd, string title, IEnumerable<object> messages)
    {
        var project = Path.Combine(root, "-" + string.Concat(cwd.Select(c => char.IsLetterOrDigit(c) ? c : '-')));
        Directory.CreateDirectory(project);
        var id = Guid.NewGuid().ToString();
        var file = Path.Combine(project, $"{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss-fffZ}_{id}.jsonl");
        var sb = new StringBuilder();
        sb.AppendLine(JsonSerializer.Serialize(new { type = "title", v = 1, title }));
        sb.AppendLine(JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd }));
        foreach (var m in messages) sb.AppendLine(JsonSerializer.Serialize(new { type = "message", message = m }));
        File.WriteAllText(file, sb.ToString());
        return file;
    }

    private static async Task<(MainWindow w, MainViewModel vm)> OpenLong(string scenario, int exchanges)
    {
        var root = TestProcesses.TempDir("scroll-sessions");
        // As omp records it: its working directory has symbolic links resolved (macOS: /var is /private/var)
        var cwd = SessionCatalog.ResolveLinks(TestProcesses.TempDir("scroll-project"));
        var file = WriteSession(root, cwd, "Long session", LongConversation(exchanges));
        var s = new SessionController(TestProcesses.FakeFactory(scenario, root), new LaunchRequest(cwd, ResumeSessionFile: file));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.Count >= exchanges * 5, "the long session loaded");
        return (w, vm);
    }

    private static ScrollViewer Scroll(Window w) => w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<ScrollViewer>().First();

    /// <summary>One frame: pending work run, then laid out and drawn (what the user sees).</summary>
    private static void Frame(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        using (w.CaptureRenderedFrame()) { }
    }

    private static double Gap(ScrollViewer sv) => sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y;

    private static double? Top(Control? c, Visual to) => c?.IsEffectivelyVisible == true ? c.TranslatePoint(default, to)?.Y : null;

    /// <summary>Starts the long streamed Markdown reply and waits for its row to have text on screen.</summary>
    private static async Task<AssistantRowViewModel> StartReply(MainWindow w, MainViewModel vm)
    {
        vm.ComposerText = "go";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Rows.LastOrDefault() is AssistantRowViewModel { HasText: true, Streaming: true }, "the reply streaming");
        Frame(w);
        return (AssistantRowViewModel)vm.Rows.Last();
    }

    private static async Task StopReply(MainViewModel vm)
    {
        if (vm.AbortCommand.CanExecute(null)) vm.AbortCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "the run stopped");
    }

    /// <summary>
    /// Following a reply that grows at the bottom of a long chat: on every frame the view is on the very bottom, and the
    /// rows above move up by exactly what the reply grew (no frame where they jump, blink or come back down). In the
    /// reply only the block being written changes: the ones before it (code blocks and tables among them) keep their
    /// controls and their height.
    /// </summary>
    [AvaloniaFact]
    public async Task A_streaming_reply_moves_the_rows_above_it_only_by_what_it_grew()
    {
        Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", "6");
        try
        {
            var (w, vm) = await OpenLong("markdown-stream", 60);
            var list = w.FindControl<ListBox>("Transcript")!;
            var sv = Scroll(w);
            var reply = await StartReply(w, vm);
            var row = (Control)list.ContainerFromItem(reply)!;
            var blocks = ((Panel)row.GetVisualDescendants().OfType<MarkdownView>().Single().Content!).Children;
            var bad = new List<string>();
            double? lastTop = null, lastHeight = null;
            List<(Control Block, double Height)> last = [];
            for (var frame = 0; frame < 200 && vm.Phase == SessionPhase.Running; frame++)
            {
                Frame(w);
                var top = Top(row, sv)!.Value;
                var height = row.Bounds.Height;
                if (Math.Abs(Gap(sv)) > 0.5) bad.Add($"frame {frame}: {Gap(sv):0.#} px off the bottom");
                if (lastTop is { } t && lastHeight is { } h && Math.Abs(top - t + height - h) > 0.5)
                    bad.Add($"frame {frame}: rows above moved {top - t:0.#} px for a reply grown {height - h:0.#} px");
                for (var b = 0; b < last.Count - 1; b++)
                    if (!ReferenceEquals(blocks[b], last[b].Block) || blocks[b].Bounds.Height != last[b].Height)
                        bad.Add($"frame {frame}: finished block {b} ({last[b].Block.GetType().Name}) was replaced or changed height");
                (lastTop, lastHeight) = (top, height);
                last = [.. blocks.Select(c => (c, c.Bounds.Height))];
                await Task.Delay(16);
            }
            Assert.True(lastHeight > sv.Viewport.Height, "the reply never grew past the view: nothing was tested");
            Assert.Contains(blocks, c => c.Classes.Contains("codeblock"));
            Assert.Contains(blocks, c => c.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("md-table-frame")));
            Assert.Empty(bad);
            Assert.Same(row, list.ContainerFromItem(reply));

            // A resize and the preview opening and closing while the reply goes on: on the bottom on every frame
            foreach (var change in new Action[] { () => w.Width = 900, () => vm.IsPreviewOpen = true, () => vm.IsPreviewOpen = false, () => w.Width = 1180 })
            {
                change();
                for (var frame = 0; frame < 8; frame++)
                {
                    Frame(w);
                    Assert.True(Math.Abs(Gap(sv)) <= 0.5, $"{Gap(sv):0.#} px off the bottom after a resize or a pane");
                    await Task.Delay(16);
                }
            }
            Assert.Equal(SessionPhase.Running, vm.Phase);
            await StopReply(vm);
            await vm.DisposeAsync();
            w.Close();
        }
        finally { Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", null); }
    }

    /// <summary>
    /// A reader who scrolls up during a reply stays exactly where they are for the rest of it, with "Jump to latest"
    /// shown; scrolling back down to the bottom follows the reply again.
    /// </summary>
    [AvaloniaFact]
    public async Task Scrolling_up_during_a_reply_holds_the_view_and_scrolling_back_down_follows_again()
    {
        Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", "6");
        try
        {
            var (w, vm) = await OpenLong("markdown-stream", 60);
            var list = w.FindControl<ListBox>("Transcript")!;
            var jump = w.FindControl<Button>("JumpToLatest")!;
            var sv = Scroll(w);
            await StartReply(w, vm);
            for (var frame = 0; frame < 30; frame++) { Frame(w); await Task.Delay(16); }
            var p = list.TranslatePoint(new Point(list.Bounds.Width / 2, 100), w)!.Value;
            for (var i = 0; i < 3; i++) w.MouseWheel(p, new Vector(0, 1));
            Frame(w);
            Assert.True(jump.IsVisible, "no way back to the latest message");
            var held = list.GetRealizedContainers().First(c => Top(c, sv) is > 0 and < 200);
            var heldTop = Top(held, sv);
            var offset = sv.Offset.Y;
            for (var frame = 0; frame < 120; frame++)
            {
                Frame(w);
                Assert.Equal(heldTop, Top(held, sv));
                await Task.Delay(16);
            }
            Assert.Equal(offset, sv.Offset.Y);
            Assert.True(Gap(sv) > 200, "the reply did not grow below the view");
            Assert.Equal(SessionPhase.Running, vm.Phase);

            // Back down with the wheel: following again
            for (var i = 0; i < 400 && Gap(sv) > 0.5; i++) { w.MouseWheel(p, new Vector(0, -3)); Frame(w); }
            Assert.False(jump.IsVisible);
            for (var frame = 0; frame < 30; frame++)
            {
                Frame(w);
                Assert.True(Math.Abs(Gap(sv)) <= 0.5, $"frame {frame}: {Gap(sv):0.#} px off the bottom");
                await Task.Delay(16);
            }
            await StopReply(vm);
            await vm.DisposeAsync();
            w.Close();
        }
        finally { Environment.SetEnvironmentVariable("FAKE_OMP_PACE_MS", null); }
    }

    /// <summary>A long conversation opens on its last row, exactly, from the first frame on.</summary>
    [AvaloniaFact]
    public async Task A_long_conversation_opens_exactly_on_its_last_row()
    {
        var (w, vm) = await OpenLong("normal", 100);
        var list = w.FindControl<ListBox>("Transcript")!;
        var sv = Scroll(w);
        Frame(w);
        Assert.Equal(0, Gap(sv), 0.5);
        var last = (Control)list.ContainerFromItem(vm.Rows.Last())!;
        // The last row ends on the view's bottom, above the list's bottom padding
        Assert.Equal(sv.Viewport.Height - list.Padding.Bottom, Top(last, sv)!.Value + last.Bounds.Height, 0.5);
        await Settle(600);
        Frame(w);
        Assert.Equal(0, Gap(sv), 0.5);
        // The extent is the rows' real height, not a guess from the few on screen: the scroll bar matches the conversation
        Assert.True(sv.Extent.Height > vm.Rows.Count * 60, $"extent {sv.Extent.Height} for {vm.Rows.Count} rows");
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// Text clicked in the last reply keeps that row realized (and its selection) wherever the reader scrolls; scrolling
    /// up and back down short of it still finds rows on screen, not blank space between the two.
    /// </summary>
    [AvaloniaFact]
    public async Task Rows_between_a_focused_row_and_the_view_are_built_when_scrolled_to()
    {
        var (w, vm) = await OpenLong("normal", 60);
        var list = w.FindControl<ListBox>("Transcript")!;
        var sv = Scroll(w);
        Frame(w);
        var text = list.GetVisualDescendants().OfType<SelectableTextBlock>().Last(t => t.IsEffectivelyVisible && Top(t, sv) is > 0 && Top(t, sv) < sv.Viewport.Height);
        var click = text.TranslatePoint(new Point(4, text.Bounds.Height / 2), w)!.Value;
        w.MouseDown(click, Avalonia.Input.MouseButton.Left);
        w.MouseUp(click, Avalonia.Input.MouseButton.Left);
        Frame(w);
        Assert.True(text.IsKeyboardFocusWithin);

        // Wheel turns: up eight views, then back down four (still well above the focused row)
        var page = sv.Viewport.Height;
        var over = sv.TranslatePoint(new Point(sv.Bounds.Width / 2, sv.Bounds.Height / 2), w)!.Value;
        var startOffset = sv.Offset.Y;
        while (sv.Offset.Y > startOffset - 8 * page)
        {
            w.MouseWheel(over, new Vector(0, 1));
            Frame(w);
        }
        var upAt = sv.Offset.Y;
        while (sv.Offset.Y < upAt + 4 * page)
        {
            w.MouseWheel(over, new Vector(0, -1));
            Frame(w);
            var middle = page / 2;
            Assert.True(list.GetRealizedContainers().Any(c => Top(c, sv) is { } top && top <= middle && top + c.Bounds.Height >= middle),
                $"nothing on screen at offset {sv.Offset.Y}");
        }
        Assert.True(text.IsKeyboardFocusWithin);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// A streaming reply takes new text without building its finished blocks again: a selection in them (and in the
    /// paragraph being written) stays, and a code block being written keeps its control as its lines come in.
    /// </summary>
    [AvaloniaFact]
    public void A_streaming_reply_keeps_its_blocks_and_the_selection_made_in_them()
    {
        var view = new MarkdownView { IsStreaming = true, Markdown = "First paragraph, finished.\n\nSecond one, still" };
        var w = new Window { Content = view, Width = 600, Height = 400 };
        w.Show();
        Frame(w);
        var blocks = ((Panel)view.Content!).Children;
        var first = (SelectableTextBlock)blocks[0];
        var growing = (SelectableTextBlock)blocks[1];
        first.Focus();
        (first.SelectionStart, first.SelectionEnd) = (0, 5);
        (growing.SelectionStart, growing.SelectionEnd) = (0, 6);

        view.Markdown += " being written";
        Frame(w);
        Assert.Same(first, blocks[0]);
        Assert.Equal("First", first.SelectedText);
        Assert.Same(growing, blocks[1]);
        Assert.Equal("Second", growing.SelectedText);

        view.Markdown += "\n\n```python\ndef f():\n";
        Frame(w);
        var code = blocks[2];
        Assert.Contains("codeblock", code.Classes);
        view.Markdown += "    return 1\n";
        Frame(w);
        Assert.Same(code, blocks[2]);
        var codeText = code.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Classes.Contains("md-code"));
        codeText.SelectAll();
        Assert.Equal("def f():\n    return 1", codeText.SelectedText.Replace("\r", ""));

        view.Markdown += "```\n\nDone.";
        view.IsStreaming = false;
        Frame(w);
        Assert.Same(first, blocks[0]);
        Assert.Same(growing, blocks[1]);
        Assert.Same(code, blocks[2]);
        Assert.Equal("First", first.SelectedText);
        Assert.Equal(4, blocks.Count);
        w.Close();
    }

    /// <summary>
    /// Link reference definitions ("[d]: url"): one after two blocks renders (Markdig gives their group the document's
    /// start), and a link written below a definition while the reply streams resolves, during the stream and after it.
    /// </summary>
    [AvaloniaFact]
    public void Link_reference_definitions_render_and_resolve_while_a_reply_streams()
    {
        static List<string> Links(Visual v) =>
            v.GetVisualDescendants().OfType<HyperlinkButton>().Select(b => b.NavigateUri!.AbsoluteUri).ToList();

        var view = new MarkdownView { Markdown = "Here is the plan.\n\nSee [the docs][d] for details.\n\n[d]: https://example.com/docs\n" };
        var w = new Window { Content = view, Width = 600, Height = 400 };
        w.Show();
        Frame(w);
        var blocks = ((Panel)view.Content!).Children;
        Assert.Equal(2, blocks.Count);
        Assert.Equal(["https://example.com/docs"], Links(view));

        view = new MarkdownView { IsStreaming = true, Markdown = "Intro.\n\n[d]: https://example.com/docs\n\nFirst" };
        w.Content = view;
        Frame(w);
        var intro = ((Panel)view.Content!).Children[0];
        view.Markdown += " use of [docs][d].";
        Frame(w);
        // The definition did not change: the blocks above the one being written keep their controls
        Assert.Same(intro, ((Panel)view.Content!).Children[0]);
        view.Markdown += "\n\nThen [again][d], a paragraph later.";
        Frame(w);
        Assert.Equal(["https://example.com/docs", "https://example.com/docs"], Links(view));
        view.IsStreaming = false;
        Frame(w);
        Assert.Equal(2, Links(view).Count);
        w.Close();
    }

    /// <summary>A reply too long for Markdown streams into one plain text block: the same block takes each new text.</summary>
    [AvaloniaFact]
    public async Task A_huge_streaming_reply_keeps_one_plain_text_block()
    {
        var view = new MarkdownView { IsStreaming = true, Markdown = new string('x', 210_000) };
        var w = new Window { Content = view, Width = 600, Height = 400 };
        w.Show();
        Frame(w);
        var plain = Assert.IsType<SelectableTextBlock>(view.Content);
        view.Markdown += " more";
        await Task.Delay(300);
        Frame(w);
        Assert.Same(plain, view.Content);
        Assert.EndsWith(" more", plain.Text);
        w.Close();
    }

    /// <summary>
    /// The keys scroll the conversation exactly when the focus is in it: Page Up by a view, Home to the top, Page Down
    /// back by a view, End to the latest message (following again).
    /// </summary>
    [AvaloniaFact]
    public async Task Page_Home_and_End_scroll_the_conversation_when_it_has_the_focus()
    {
        var (w, vm) = await OpenLong("normal", 40);
        var list = w.FindControl<ListBox>("Transcript")!;
        var jump = w.FindControl<Button>("JumpToLatest")!;
        var sv = Scroll(w);
        Frame(w);
        // A click on the conversation's text gives it the keys (the message box had them)
        var text = list.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.IsEffectivelyVisible && Top(t, sv) is > 20 and < 300);
        var click = text.TranslatePoint(new Point(4, text.Bounds.Height / 2), w)!.Value;
        w.MouseDown(click, Avalonia.Input.MouseButton.Left);
        w.MouseUp(click, Avalonia.Input.MouseButton.Left);
        Frame(w);
        Assert.True(list.IsKeyboardFocusWithin, $"focus {w.FocusManager?.GetFocusedElement()}");
        // Page Up: what was on screen moves down by exactly one view (rows measured above it on the way move nothing)
        var held = list.GetRealizedContainers().First(c => Top(c, sv) is >= 0 and < 200);
        var heldTop = Top(held, sv)!.Value;
        var before = (sv.Offset.Y, sv.Extent.Height, sv.Viewport.Height);
        w.KeyPress(Avalonia.Input.Key.PageUp, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.PageUp, null);
        Frame(w);
        Assert.True(Math.Abs(heldTop + sv.Viewport.Height - Top(held, sv)!.Value) < 0.5,
            $"held {heldTop} -> {Top(held, sv)}; before {before}, after {(sv.Offset.Y, sv.Extent.Height, sv.Viewport.Height)}; focus {w.FocusManager?.GetFocusedElement()}");
        Assert.True(jump.IsVisible);
        w.KeyPress(Avalonia.Input.Key.PageDown, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.PageDown, null);
        Frame(w);
        Assert.Equal(0, Gap(sv), 0.5);
        Assert.False(jump.IsVisible, "back on the bottom: following again");
        w.KeyPress(Avalonia.Input.Key.Home, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Home, null);
        Frame(w);
        Assert.Equal(0, sv.Offset.Y);
        Assert.Same(vm.Rows[0], list.GetRealizedContainers().OrderBy(c => Top(c, sv)).First().DataContext);
        w.KeyPress(Avalonia.Input.Key.End, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.End, null);
        Frame(w);
        Assert.Equal(0, Gap(sv), 0.5);
        Assert.False(jump.IsVisible);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// Dragging the scroll bar's thumb through a long conversation (most rows never measured yet): the view only ever
    /// moves the way the thumb goes, never back, and the thumb ends on the bottom, following the latest message again.
    /// </summary>
    [AvaloniaFact]
    public async Task Dragging_the_thumb_through_a_long_conversation_never_jumps_back()
    {
        var (w, vm) = await OpenLong("normal", 100);
        var sv = Scroll(w);
        var jump = w.FindControl<Button>("JumpToLatest")!;
        Frame(w);
        var bar = sv.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().First(b => b.TemplatedParent == sv && b.Orientation == Avalonia.Layout.Orientation.Vertical);
        var thumb = bar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().First();
        Point ThumbCenter() => thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), w)!.Value;
        var track = bar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Track>().First();
        var trackTop = track.TranslatePoint(default, w)!.Value.Y;
        var trackBottom = trackTop + track.Bounds.Height;

        // Up to the top
        var at = ThumbCenter();
        w.MouseDown(at, Avalonia.Input.MouseButton.Left);
        w.MouseMove(new Point(at.X, trackTop - 50));
        w.MouseUp(new Point(at.X, trackTop - 50), Avalonia.Input.MouseButton.Left);
        Frame(w);
        Assert.Equal(0, sv.Offset.Y);
        Assert.True(jump.IsVisible);

        // Down to the bottom in small steps: the view never goes back
        at = ThumbCenter();
        w.MouseDown(at, Avalonia.Input.MouseButton.Left);
        var previous = sv.Offset.Y;
        var steps = new List<string>();
        for (var y = at.Y + 10; y < trackBottom + 50; y += 10)
        {
            w.MouseMove(new Point(at.X, y));
            Frame(w);
            steps.Add($"{y - at.Y:0}:{sv.Offset.Y:0}/{sv.Extent.Height - sv.Viewport.Height:0}");
            Assert.True(sv.Offset.Y >= previous - 0.5, "the view went back: " + string.Join(" ", steps));
            previous = sv.Offset.Y;
        }
        w.MouseUp(new Point(at.X, trackBottom + 50), Avalonia.Input.MouseButton.Left);
        Frame(w);
        Assert.Equal(0, Gap(sv), 0.5);
        Assert.False(jump.IsVisible, "the thumb ended on the bottom: following again");
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// QA D2: a long conversation ending with a 14,000 px reply. Rows not laid out yet were placed by the average of the
    /// measured ones (the giant reply among them): the scroll bar was three times too long, dragging its thumb showed
    /// frames with nothing on screen and ended mid-track. Now each unmeasured row has its own estimate: the length found
    /// on opening holds within a few percent all the way up, every frame of the drag has rows on screen, and the thumb
    /// let go above the track is at the very top.
    /// </summary>
    [AvaloniaFact]
    public async Task Dragging_the_thumb_up_past_a_giant_reply_keeps_rows_on_screen_and_ends_on_top()
    {
        var root = TestProcesses.TempDir("scroll-sessions");
        var cwd = SessionCatalog.ResolveLinks(TestProcesses.TempDir("scroll-project"));
        var giant = string.Concat(Enumerable.Range(1, 20).Select(s =>
            $"## Section {s}\n\nParagraph {s} with **bold** and `code`, {string.Join(" ", Enumerable.Range(0, 30).Select(i => $"w{s}x{i}"))}.\n\n" +
            $"- item one of {s}\n- item two\n  - nested {s}\n\n```csharp\n{string.Join("\n", Enumerable.Range(0, 6).Select(i => $"var line{i} = Compute({s}, {i});"))}\n```\n\n" +
            $"| A | B |\n|---|---|\n| {s} | b{s} |\n\n"));
        var messages = LongConversation(70).Append(new { role = "user", content = new[] { Text("one long answer please") } })
            .Append((object)new { role = "assistant", content = new[] { Text(giant) }, stopReason = "stop" });
        var file = WriteSession(root, cwd, "Long session", messages);
        var s = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(cwd, ResumeSessionFile: file));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.Count >= 70 * 5 + 2, "the long session loaded");
        var list = w.FindControl<ListBox>("Transcript")!;
        var sv = Scroll(w);
        Frame(w);
        Frame(w);
        Assert.Equal(0, Gap(sv), 0.5);
        var opened = sv.Extent.Height;
        var bar = sv.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>().First(b => b.TemplatedParent == sv && b.Orientation == Avalonia.Layout.Orientation.Vertical);
        var thumb = bar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().First();
        var trackTop = bar.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Track>().First().TranslatePoint(default, w)!.Value.Y;
        var at = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), w)!.Value;
        w.MouseDown(at, Avalonia.Input.MouseButton.Left);
        var bad = new List<string>();
        for (var y = at.Y - 6; y > trackTop - 40; y -= 6)
        {
            w.MouseMove(new Point(at.X, y));
            Frame(w);
            var middle = sv.Viewport.Height / 2;
            if (!list.GetRealizedContainers().Any(c => Top(c, sv) is { } top && top <= middle && top + c.Bounds.Height >= middle))
                bad.Add($"nothing on screen at offset {sv.Offset.Y:0}");
            if (Math.Abs(sv.Extent.Height - opened) > opened * 0.1) bad.Add($"length {sv.Extent.Height:0}, opened at {opened:0}");
        }
        w.MouseUp(new Point(at.X, trackTop - 40), Avalonia.Input.MouseButton.Left);
        Frame(w);
        Frame(w);
        Assert.Empty(bad);
        Assert.Equal(0, sv.Offset.Y);
        Assert.Same(vm.Rows[0], list.GetRealizedContainers().OrderBy(c => Top(c, sv)).First().DataContext);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// Review finding: a reader scrolled up into a long conversation (most rows above never measured) narrows the window
    /// or opens a pane. The new width estimates the unmeasured rows again, which moved every row above the view; the
    /// panel looked for the row to hold where the rows had been, did not find it, built the rows at the old pixels and
    /// hid the ones on screen. Then: the rows above it measured again at the new width moved it by ~48 px (Avalonia's
    /// scroll anchoring does not follow them through a resize). The row at the top of the view stays exactly where it was.
    /// </summary>
    [AvaloniaFact]
    public async Task Narrowing_the_window_while_scrolled_up_keeps_the_row_at_the_top_in_place()
    {
        var (w, vm) = await OpenLong("normal", 100);
        var list = w.FindControl<ListBox>("Transcript")!;
        var sv = Scroll(w);
        Frame(w);
        var p = list.TranslatePoint(new Point(list.Bounds.Width / 2, 100), w)!.Value;
        for (var i = 0; i < 12; i++) w.MouseWheel(p, new Vector(0, 3));
        Frame(w);
        Frame(w);
        Control Held() => list.GetRealizedContainers().Where(c => c.IsVisible && Top(c, sv) is { } t && t + c.Bounds.Height > 0 && t < sv.Viewport.Height)
            .OrderBy(c => Math.Abs(Top(c, sv)!.Value)).First();
        foreach (var width in new[] { 700.0, 560, 1180 })
        {
            var held = Held();
            var row = held.DataContext;
            var heldTop = Top(held, sv)!.Value;
            w.Width = width;
            Frame(w);
            Frame(w);
            var now = list.GetRealizedContainers().FirstOrDefault(c => c.IsVisible && ReferenceEquals(c.DataContext, row));
            Assert.True(now is not null, $"the row at the top of the view is gone after the width became {width}");
            // Exactly where the reader left it (TranscriptPanel holds it through the width change)
            var top = Top(now, sv)!.Value;
            Assert.True(Math.Abs(top - heldTop) <= 1, $"at width {width} the row at the top moved {heldTop:0.#} -> {top:0.#}");
        }
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>A tool row above the view opening (or closing) moves nothing the reader sees.</summary>
    [AvaloniaFact]
    public async Task A_tool_row_opening_above_the_view_moves_nothing_on_screen()
    {
        var (w, vm) = await OpenLong("normal", 40);
        var list = w.FindControl<ListBox>("Transcript")!;
        var sv = Scroll(w);
        Frame(w);
        var p = list.TranslatePoint(new Point(list.Bounds.Width / 2, 100), w)!.Value;
        for (var i = 0; i < 20; i++) w.MouseWheel(p, new Vector(0, 3));
        Frame(w);
        Frame(w);
        var tool = list.GetRealizedContainers().Where(c => c.DataContext is ToolRowViewModel { CanExpand: true } && Top(c, sv) < -50).Last();
        var held = list.GetRealizedContainers().First(c => Top(c, sv) is > 0 and < 300);
        var heldTop = Top(held, sv);
        var offset = sv.Offset.Y;
        ((ToolRowViewModel)tool.DataContext!).IsExpanded = true;
        Frame(w);
        Assert.True(sv.Offset.Y > offset + 100, "the opened row did not grow");
        Assert.Equal(heldTop, Top(held, sv));
        ((ToolRowViewModel)tool.DataContext!).IsExpanded = false;
        Frame(w);
        Assert.Equal(heldTop, Top(held, sv));
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>The selectable texts of the rows on screen, top to bottom.</summary>
    private static List<SelectableTextBlock> TextsOnScreen(ListBox list, ScrollViewer sv) =>
        [.. list.GetRealizedContainers().Where(c => c.IsVisible).SelectMany(c => c.GetVisualDescendants().OfType<SelectableTextBlock>())
            .Where(t => t.IsEffectivelyVisible && t.Bounds.Height > 0 && Top(t, sv) is { } y && y > 10 && y + t.Bounds.Height < sv.Viewport.Height - 10)
            .OrderBy(t => Top(t, sv))];

    /// <summary>
    /// Real-app finding: text clicked near the bottom of a long chat keeps the focus while the wheel takes the reader to
    /// the top (the wheel moves no focus); closing any menu then gave the focus back to that text, and the conversation
    /// jumped thousands of pixels to bring it into view. Closing a menu moves nothing.
    /// </summary>
    [AvaloniaFact]
    public async Task Closing_a_menu_does_not_scroll_the_conversation_to_text_focused_earlier()
    {
        var (w, vm) = await OpenLong("normal", 30);
        var list = w.FindControl<ListBox>("Transcript")!;
        var sv = Scroll(w);
        Frame(w);
        Frame(w);
        var text = TextsOnScreen(list, sv).Last();
        var at = text.TranslatePoint(new Point(4, text.Bounds.Height / 2), w)!.Value;
        w.MouseDown(at, Avalonia.Input.MouseButton.Left);
        w.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Frame(w);
        Assert.True(text.IsKeyboardFocusWithin, "the clicked text did not take the focus");
        var p = list.TranslatePoint(new Point(list.Bounds.Width / 2, 100), w)!.Value;
        for (var i = 0; i < 40 && sv.Offset.Y > 0; i++)
        {
            w.MouseWheel(p, new Vector(0, 20));
            Frame(w);
        }
        await Settle(100);
        var offset = sv.Offset.Y;
        Assert.True(sv.Extent.Height - offset > 3 * sv.Viewport.Height, "the wheel did not leave the bottom");
        var first = TextsOnScreen(list, sv).First();
        var c = first.TranslatePoint(new Point(4, first.Bounds.Height / 2), w)!.Value;
        w.MouseMove(c);
        w.MouseDown(c, Avalonia.Input.MouseButton.Right);
        w.MouseUp(c, Avalonia.Input.MouseButton.Right);
        await Settle(150);
        Assert.Contains(first.GetSelfAndVisualAncestors().OfType<Control>(), x => x.ContextFlyout is { IsOpen: true });
        w.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
        await Settle(150);
        Frame(w);
        Assert.DoesNotContain(first.GetSelfAndVisualAncestors().OfType<Control>(), x => x.ContextFlyout is { IsOpen: true });
        Assert.True(Math.Abs(sv.Offset.Y - offset) <= 1, $"closing the menu scrolled the conversation from {offset:0} to {sv.Offset.Y:0}");
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// Real-app polish: Page Up and the other keys scrolled the conversation only after a click on its prose; a click
    /// between blocks, on a code block or in its margin left the focus in the message box, where the keys move the caret.
    /// A click anywhere in the conversation gives it the keys, as in Claude.
    /// </summary>
    [AvaloniaFact]
    public async Task After_a_click_anywhere_in_the_conversation_the_keys_scroll_it()
    {
        var (w, vm) = await OpenLong("normal", 20);
        var list = w.FindControl<ListBox>("Transcript")!;
        var sv = Scroll(w);
        Frame(w);
        Frame(w);
        var code = list.GetRealizedContainers().Where(c => c.IsVisible).SelectMany(c => c.GetVisualDescendants().OfType<Control>())
            .Last(c => c.Classes.Contains("codeblock") && Top(c, sv) is > 10 and < 400);
        var spots = new (string What, Point At)[]
        {
            ("the list's margin", list.TranslatePoint(new Point(8, sv.Viewport.Height / 2), w)!.Value),
            ("a code block's corner", code.TranslatePoint(new Point(code.Bounds.Width - 4, 4), w)!.Value),
        };
        foreach (var (what, at) in spots)
        {
            w.FindControl<TextBox>("Composer")!.Focus();
            Frame(w);
            w.MouseDown(at, Avalonia.Input.MouseButton.Left);
            w.MouseUp(at, Avalonia.Input.MouseButton.Left);
            Frame(w);
            var offset = sv.Offset.Y;
            w.KeyPress(Avalonia.Input.Key.PageUp, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.PageUp, null);
            Frame(w);
            Assert.True(offset - sv.Offset.Y > sv.Viewport.Height / 2, $"after a click on {what} Page Up moved the view {offset - sv.Offset.Y:0} px (focus: {w.FocusManager?.GetFocusedElement()?.GetType().Name})");
            w.KeyPress(Avalonia.Input.Key.End, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.End, null);
            Frame(w);
            Assert.True(Math.Abs(Gap(sv)) <= 0.5, $"after a click on {what} End did not go to the latest message");
        }
        await vm.DisposeAsync();
        w.Close();
    }
}
