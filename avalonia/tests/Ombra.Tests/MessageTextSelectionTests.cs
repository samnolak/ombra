using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
/// Selecting a message's text with the mouse: a reply is a stack of text blocks (paragraphs, list items, code), and a drag
/// from one goes on through the others (Controls/MessageSelection) instead of stopping at the end of the block pressed;
/// ⌘C copies all of it with its line breaks, ⌘A selects the whole reply, the text's menu copies and quotes all of it, a
/// click elsewhere clears it. Your own message is one block: a drag selects through its lines. With OMBRA_SHOT_DIR set,
/// a selection through a reply is saved in English and Russian.
/// </summary>
public sealed class MessageTextSelectionTests
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

    private static async Task Settle(int ms = 150)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private static void Frame(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        using (w.CaptureRenderedFrame()) { }
    }

    private static object Text(string t) => new { type = "text", text = t };

    /// <summary>The message and the reply, in English or Russian; the reply's words as they read, block by block.</summary>
    private sealed record Chat(string Question, string Reply, string Alpha, string Beta, string ItemOne, string ItemTwo, string Code, string Last);

    private static Chat English => new(
        "First line of my question\nSecond line of it\nThird line, the last",
        "Alpha paragraph starts here and is long enough to wrap onto a second line in the reply column, so a drag goes down through it.\n\n"
        + "Beta paragraph follows with `inline code` and a [link](https://example.com/docs).\n\n- item one\n- item two\n\n```python\ndef f():\n    return 1\n```\n\nLast paragraph.",
        "Alpha paragraph starts here and is long enough to wrap onto a second line in the reply column, so a drag goes down through it.",
        "Beta paragraph follows with inline code and a link.", "item one", "item two", "def f():\n    return 1", "Last paragraph.");

    private static Chat Russian => new(
        "Первая строка моего вопроса\nВторая строка\nТретья, последняя",
        "Первый абзац начинается здесь и достаточно длинный, чтобы перенестись на вторую строку в колонке ответа, и выделение идёт вниз через него.\n\n"
        + "Второй абзац с `кодом в строке` и [ссылкой](https://example.com/docs).\n\n- пункт один\n- пункт два\n\n```python\ndef f():\n    return 1\n```\n\nПоследний абзац.",
        "Первый абзац начинается здесь и достаточно длинный, чтобы перенестись на вторую строку в колонке ответа, и выделение идёт вниз через него.",
        "Второй абзац с кодом в строке и ссылкой.", "пункт один", "пункт два", "def f():\n    return 1", "Последний абзац.");

    private static async Task<(MainWindow w, MainViewModel vm)> Open(Chat chat)
    {
        var root = TestProcesses.TempDir("select-sessions");
        var cwd = SessionCatalog.ResolveLinks(TestProcesses.TempDir("select-project"));
        var file = ChatScrollTests.WriteSession(root, cwd, "Selection", [
            new { role = "user", content = new[] { Text(chat.Question) } },
            new { role = "assistant", content = new[] { Text(chat.Reply) }, stopReason = "stop" },
        ]);
        var s = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(cwd, ResumeSessionFile: file));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1000, Height = 900 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(r => r.HasText), "the chat loaded");
        Frame(w);
        Frame(w);
        return (w, vm);
    }

    private static MarkdownView Reply(Window w) =>
        w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<MarkdownView>().First(v => v.IsEffectivelyVisible);

    /// <summary>The reply's text block whose words start with <paramref name="start"/>.</summary>
    private static SelectableTextBlock Block(Window w, string start) =>
        Reply(w).GetVisualDescendants().OfType<SelectableTextBlock>().First(b => Words(b).StartsWith(start, StringComparison.Ordinal));

    /// <summary>A block's words as they read (a link its label, no chip padding).</summary>
    private static string Words(SelectableTextBlock b) => MessageSelection.Readable(b, 0, int.MaxValue);

    /// <summary>A point in <paramref name="block"/>'s line <paramref name="line"/> (its middle), <paramref name="x"/> from its left; a negative x from its right.</summary>
    private static Point At(Window w, SelectableTextBlock block, int line, double x)
    {
        var lines = block.TextLayout.TextLines;
        var top = lines.Take(line).Sum(l => l.Height);
        return block.TranslatePoint(new Point(x < 0 ? block.Bounds.Width + x : x, block.Padding.Top + top + lines[line].Height / 2), w)!.Value;
    }

    /// <summary>On the last character of a block's line, past its middle (a press there is at the line's end; one beyond
    /// the line's last character misses the text).</summary>
    private static Point LineEnd(Window w, SelectableTextBlock block, int line) =>
        At(w, block, line, block.TextLayout.TextLines[line].Start + block.TextLayout.TextLines[line].Width - 1);

    private static void Drag(Window w, Point from, Point to)
    {
        w.MouseMove(from);
        w.MouseDown(from, MouseButton.Left);
        for (var i = 1; i <= 10; i++)
        {
            w.MouseMove(from + (to - from) * (i / 10.0), RawInputModifiers.LeftMouseButton);
            Frame(w);
        }
        w.MouseUp(to, MouseButton.Left);
        Frame(w);
    }

    private static void Command(Window w, Key key, PhysicalKey physical, string symbol) =>
        w.KeyPress(key, (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers, physical, symbol);

    private static async Task<string> Copied(Window w)
    {
        await w.Clipboard!.SetTextAsync("(nothing copied)");
        Command(w, Key.C, PhysicalKey.C, "c");
        await Settle();
        return ((await w.Clipboard!.TryGetTextAsync()) ?? "").ReplaceLineEndings("\n");
    }

    private static string[] Selected(Window w) =>
        Reply(w).GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.SelectionStart != b.SelectionEnd).Select(Words).ToArray();

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task A_drag_selects_down_through_a_replys_paragraphs_and_list_and_copies_them_with_their_line_breaks(string language)
    {
        var settings = Path.Combine(TestProcesses.TempDir("select-lang"), "settings.json");
        var previous = Loc.Current;
        Loc.Load(language, settings);
        var chat = language == "ru" ? Russian : English;
        var (w, vm) = await Open(chat);
        try
        {
            var alpha = Block(w, chat.Alpha[..12]);
            Assert.True(alpha.TextLayout.TextLines.Count >= 2, "the first paragraph wraps");
            // From the first paragraph's first word to past the end of the list's second item: everything between
            Drag(w, At(w, alpha, 0, 1), At(w, Block(w, chat.ItemTwo), 0, -2));
            Assert.Equal([chat.Alpha, chat.Beta, chat.ItemOne, chat.ItemTwo], Selected(w));
            Assert.True(alpha.IsFocused, "the block pressed keeps the keyboard");
            Assert.Equal($"{chat.Alpha}\n\n{chat.Beta}\n\n{chat.ItemOne}\n{chat.ItemTwo}", await Copied(w));
            if (Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") is { } dir)
            {
                Directory.CreateDirectory(dir);
                using var frame = w.CaptureRenderedFrame()!;
                frame.Save(Path.Combine(dir, $"message-selection-{language}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }

            // A drag within the first paragraph (pressed apart from the last press: not a double click) selects through
            // its wrapped lines, nothing else
            Drag(w, At(w, alpha, 0, 40), At(w, alpha, 1, 30));
            Assert.Single(Selected(w));
            Assert.True(alpha.SelectionEnd > alpha.TextLayout.TextLines[0].Length, "the selection reaches the paragraph's second line");
            Assert.Equal(Words(alpha)[alpha.SelectionStart..alpha.SelectionEnd], await Copied(w));
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
            Loc.Load(previous, settings);
        }
    }

    [AvaloniaFact]
    public async Task A_drag_up_from_the_code_selects_back_through_the_list_and_a_paragraph_and_a_click_clears_it()
    {
        var chat = English;
        var (w, vm) = await Open(chat);
        try
        {
            var code = Reply(w).GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => b.Classes.Contains("md-code"));
            Drag(w, LineEnd(w, code, 1), At(w, Block(w, "Beta"), 0, 0));
            Assert.Equal([chat.Beta, chat.ItemOne, chat.ItemTwo, chat.Code], Selected(w).Select(s => s.ReplaceLineEndings("\n")));
            Assert.Equal($"{chat.Beta}\n\n{chat.ItemOne}\n{chat.ItemTwo}\n\n{chat.Code}", await Copied(w));

            // A drag that turns back into the block pressed leaves the others: from the code's start down into the last
            // paragraph, then back up to the code's end
            var start = At(w, code, 0, 1);
            var back = LineEnd(w, code, 1);
            w.MouseMove(start);
            w.MouseDown(start, MouseButton.Left);
            w.MouseMove(At(w, Block(w, "Last"), 0, -2), RawInputModifiers.LeftMouseButton);
            Frame(w);
            Assert.Equal([chat.Code, chat.Last], Selected(w).Select(s => s.ReplaceLineEndings("\n")));
            w.MouseMove(back, RawInputModifiers.LeftMouseButton);
            Frame(w);
            w.MouseUp(back, MouseButton.Left);
            Frame(w);
            Assert.Equal([chat.Code], Selected(w).Select(s => s.ReplaceLineEndings("\n")));
            Assert.Equal(chat.Code, await Copied(w));

            // A click in the other message clears the reply's selection
            Drag(w, At(w, Block(w, "Beta"), 0, 2), At(w, Block(w, "Last"), 0, -2));
            Assert.Equal(5, Selected(w).Length);
            var question = w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Text == chat.Question);
            var p = At(w, question, 0, 4);
            w.MouseDown(p, MouseButton.Left);
            w.MouseUp(p, MouseButton.Left);
            Frame(w);
            Assert.Empty(Selected(w));
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task Select_all_in_a_reply_selects_the_whole_reply_and_its_menu_copies_and_quotes_all_of_it()
    {
        var chat = English;
        var (w, vm) = await Open(chat);
        try
        {
            var beta = Block(w, "Beta");
            var p = At(w, beta, 0, 4);
            w.MouseDown(p, MouseButton.Left);
            w.MouseUp(p, MouseButton.Left);
            Frame(w);
            Command(w, Key.A, PhysicalKey.A, "a");
            Frame(w);
            Assert.Equal(6, Selected(w).Length);
            var whole = $"{chat.Alpha}\n\n{chat.Beta}\n\n{chat.ItemOne}\n{chat.ItemTwo}\n\n{chat.Code}\n\n{chat.Last}";
            Assert.Equal(whole, await Copied(w));

            // The Edit menu (the mouse, VoiceOver) does the same with the keyboard in the reply
            await w.Clipboard!.SetTextAsync("");
            w.RunEdit(EditAction.Copy);
            await Settle();
            Assert.Equal(whole, (await w.Clipboard!.TryGetTextAsync())?.ReplaceLineEndings("\n"));

            // A right click on a block in the selection: the text's menu, the selection kept; Copy and Quote take all of it
            var item = Block(w, "item two");
            var at = At(w, item, 0, 10);
            w.MouseMove(at);
            w.MouseDown(at, MouseButton.Right);
            w.MouseUp(at, MouseButton.Right);
            await Settle();
            var menu = Assert.IsType<MenuFlyout>(item.ContextFlyout);
            Assert.True(menu.IsOpen);
            Assert.Equal(6, Selected(w).Length);
            await w.Clipboard!.SetTextAsync("");
            var copy = menu.Items.OfType<MenuItem>().First(i => i.Command is not null);
            copy.Command!.Execute(copy.CommandParameter);
            await Settle();
            Assert.Equal(whole, (await w.Clipboard!.TryGetTextAsync())?.ReplaceLineEndings("\n"));
            menu.Items.OfType<MenuItem>().Single(i => i.Tag is "quote-selection").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("> " + whole.Replace("\n", "\n> ") + "\n", vm.ComposerText);
            menu.Hide();
            vm.ComposerText = ""; // the quote's lines grew the message box, which took room from the conversation
            await Settle(300);

            // The text's menu is one for all text (Fluent's): its Quote quotes the text it opens on, here your message
            var question = w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Text == chat.Question);
            Assert.Same(menu, question.ContextFlyout);
            Drag(w, At(w, question, 1, 1), At(w, question, 2, -1));
            var inQuestion = At(w, question, 2, 10);
            w.MouseMove(inQuestion);
            w.MouseDown(inQuestion, MouseButton.Right);
            w.MouseUp(inQuestion, MouseButton.Right);
            await Settle();
            Assert.True(menu.IsOpen);
            menu.Items.OfType<MenuItem>().Single(i => i.Tag is "quote-selection").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("> Second line of it\n> Third line, the last\n", vm.ComposerText);
            menu.Hide();
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    /// <summary>A table's cells copy a tab apart and its rows a line apart; a quote's paragraphs a line apart.</summary>
    [AvaloniaFact]
    public async Task A_drag_through_a_table_and_a_quote_copies_cells_a_tab_apart_and_rows_a_line_apart()
    {
        var view = new MarkdownView { Markdown = "| Name | Size |\n|---|---|\n| alpha | 12 |\n| beta | 7 |\n\n> First quoted.\n>\n> Second quoted." };
        var w = new Window { Content = new ScrollViewer { Content = view }, Width = 600, Height = 500 };
        w.Show();
        try
        {
            Frame(w);
            Frame(w);
            SelectableTextBlock Cell(string words) =>
                view.GetVisualDescendants().OfType<SelectableTextBlock>().First(b => Words(b) == words);
            Drag(w, At(w, Cell("Name"), 0, 1), LineEnd(w, Cell("7"), 0));
            Assert.Equal("Name\tSize\nalpha\t12\nbeta\t7", await Copied(w));
            Drag(w, At(w, Cell("beta"), 0, 1), LineEnd(w, Cell("Second quoted."), 0));
            Assert.Equal("beta\t7\n\nFirst quoted.\nSecond quoted.", await Copied(w));
        }
        finally { w.Close(); }
    }

    /// <summary>Your message is one block: a drag selects through its lines, ⌘C copies them with their line breaks.</summary>
    [AvaloniaFact]
    public async Task A_drag_selects_through_the_lines_of_your_message()
    {
        var chat = English;
        var (w, vm) = await Open(chat);
        try
        {
            var question = w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Text == chat.Question);
            Drag(w, At(w, question, 0, 1), At(w, question, 2, -1));
            Assert.Equal(chat.Question, question.SelectedText.ReplaceLineEndings("\n"));
            Assert.Equal(chat.Question, await Copied(w));
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }
}
