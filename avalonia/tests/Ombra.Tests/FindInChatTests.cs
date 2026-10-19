using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Xunit;

namespace Ombra.Tests;

/// <summary>
/// Find in chat (⌘F / Ctrl+F): the bar opens with the keyboard in it, counts the matches of every loaded row — your
/// messages, the replies, a tool's arguments and output, rows never built on screen — goes through them with Enter and
/// Shift+Enter, scrolling a far one into view and opening what folds it, follows a streaming reply without moving the
/// view, and closes with Esc, the keyboard back in the message box.
/// </summary>
public sealed class FindInChatTests
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

    private static void Frame(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        using (w.CaptureRenderedFrame()) { }
    }

    private static object Text(string t) => new { type = "text", text = t };

    /// <summary>"zebra" 8 times (any case) in a user message, a reply along the way, a read's path and its output (twice),
    /// the turn's answer, and the last exchange; between them a long conversation without it; a decomposed "zébra".</summary>
    private static IEnumerable<object> ZebraConversation(string firstQuestion = "Where is the zebra config?")
    {
        yield return new { role = "user", content = new[] { Text(firstQuestion) } };
        yield return new
        {
            role = "assistant",
            content = new object[]
            {
                Text("Looking for the Zebra settings."),
                new { type = "toolCall", id = "z1", name = "read", arguments = new { path = "src/zebra.toml" } },
            },
            stopReason = "toolUse",
        };
        yield return new { role = "toolResult", toolCallId = "z1", toolName = "read", content = new[] { Text("zebra = true\nZEBRA_MODE=fast\nother = 1") }, isError = false };
        yield return new { role = "assistant", content = new[] { Text("The zebra is configured in src/settings.toml.") }, stopReason = "stop" };
        foreach (var m in ChatScrollTests.LongConversation(12)) yield return m;
        yield return new { role = "user", content = new[] { Text("Last question about the zebra, and the ze\u0301bra.") } };
        yield return new { role = "assistant", content = new[] { Text("Yes, the zebra is fine.") }, stopReason = "stop" };
    }

    private static async Task<(MainWindow w, MainViewModel vm)> Open(string scenario, IEnumerable<object> messages)
    {
        var root = TestProcesses.TempDir("find-sessions");
        var cwd = SessionCatalog.ResolveLinks(TestProcesses.TempDir("find-project"));
        var file = ChatScrollTests.WriteSession(root, cwd, "Find session", messages);
        var s = new SessionController(TestProcesses.FakeFactory(scenario, root), new LaunchRequest(cwd, ResumeSessionFile: file));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().Any(a => a.Text.Contains("zebra is fine", StringComparison.Ordinal)),
            "the conversation loaded");
        Frame(w);
        return (w, vm);
    }

    private static RawInputModifiers Command(Window w) => (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;

    private static ScrollViewer Scroll(Window w) => w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<ScrollViewer>().First();

    /// <summary>⌘F, then the query typed into the bar.</summary>
    private static async Task<TextBox> FindAsync(MainWindow w, MainViewModel vm, string query)
    {
        w.KeyPress(Key.F, Command(w), PhysicalKey.F, null);
        var box = w.FindControl<TextBox>("FindBox")!;
        await Until(() => vm.IsFindOpen && box.IsFocused, "the find bar open with the keyboard in it");
        w.KeyTextInput(query);
        await Until(() => vm.FindQuery == query, "the query typed");
        Frame(w);
        return box;
    }

    private static void Press(Window w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, key == Key.Enter ? PhysicalKey.Enter : PhysicalKey.Escape, null);
        Frame(w);
    }

    /// <summary>The text holding the match shown: the one transcript text with a selection.</summary>
    private static SelectableTextBlock Selected(Window w) =>
        w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => !string.IsNullOrEmpty(b.SelectedText));

    private static bool OnScreen(Visual v, ScrollViewer sv) =>
        v.IsEffectivelyVisible && v.TranslatePoint(default, sv) is { } at && at.Y < sv.Viewport.Height && at.Y + v.Bounds.Height > 0;

    [AvaloniaFact]
    public async Task Command_F_opens_the_bar_and_counts_matches_in_messages_replies_and_tool_calls()
    {
        var (w, vm) = await Open("normal", ZebraConversation());
        var box = await FindAsync(w, vm, "zebra");
        // The user's messages, the replies (one folded under its turn's line), the read's path and both lines of its output
        Assert.Equal(8, vm.FindTotal);
        // Opened on the latest message: the newest match is the one shown
        Assert.Equal("8/8", w.FindControl<TextBlock>("FindCounter")!.Text);
        Assert.Equal("zebra", Selected(w).SelectedText, ignoreCase: true);

        // Composed and upper-case finds the decomposed lower-case one
        box.Text = "ZÉBRA";
        await Until(() => vm.FindTotal == 1, "the decomposed é found by the composed É");
        Assert.Equal("1/1", vm.FindCounter);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_match_shown_stays_selected_when_the_theme_changes()
    {
        var app = Avalonia.Application.Current!;
        var before = app.RequestedThemeVariant;
        app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        var (w, vm) = await Open("normal", ZebraConversation());
        try
        {
            await FindAsync(w, vm, "zebra");
            Assert.Equal("zebra", Selected(w).SelectedText, ignoreCase: true);
            // The replies render again in the new theme's colors: the match shown is selected in the new text
            app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            Frame(w);
            await Until(() => w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Any(b => string.Equals(b.SelectedText, "zebra", StringComparison.OrdinalIgnoreCase)), "the match selected again", seconds: 5);
            Assert.Equal("8/8", vm.FindCounter);
        }
        finally
        {
            app.RequestedThemeVariant = before;
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task Enter_goes_through_the_matches_scrolling_a_far_one_into_view_and_opening_what_folds_it()
    {
        var (w, vm) = await Open("normal", ZebraConversation());
        var sv = Scroll(w);
        var list = w.FindControl<ListBox>("Transcript")!;
        await FindAsync(w, vm, "zebra");
        Assert.Equal("8/8", vm.FindCounter);
        var first = vm.Rows.OfType<UserRowViewModel>().First();
        Assert.False(list.ContainerFromItem(first) is Control c && OnScreen(c, sv), "the first message is already on screen: the test needs a far match");

        // After the last, the first: thousands of pixels up, in a row never built
        Press(w, Key.Enter);
        Assert.Equal("1/8", vm.FindCounter);
        var shown = Selected(w);
        Assert.True(OnScreen(shown, sv), "the first match is not in view");
        Assert.Same(first, shown.FindAncestorOfType<ListBoxItem>()!.DataContext);
        Assert.Equal("zebra", shown.SelectedText, ignoreCase: true);

        // Each next one is in view and selected, folded turns and tool output opened for it
        var read = vm.Rows.OfType<ToolRowViewModel>().First(t => t.Name == "read" && t.Output.Contains("zebra", StringComparison.Ordinal));
        for (var n = 2; n <= 8; n++)
        {
            Press(w, Key.Enter);
            Assert.Equal($"{n}/8", vm.FindCounter);
            var text = Selected(w);
            Assert.True(OnScreen(text, sv), $"match {n} is not in view");
            Assert.Equal("zebra", text.SelectedText, ignoreCase: true);
            var row = (RowViewModel)text.FindAncestorOfType<ListBoxItem>()!.DataContext!;
            Assert.False(row.IsFolded, $"match {n} is in a folded row");
            // The read's path and output (matches 3 to 5) open its output; the next match folds it back
            if (n is >= 3 and <= 5) Assert.True(read.IsExpanded, $"match {n}: the read's output is not open");
            if (n == 6) Assert.False(read.IsExpanded, "the read's output stayed open after its matches");
        }

        // Shift+Enter goes back; after the first, the last
        Press(w, Key.Enter, RawInputModifiers.Shift);
        Assert.Equal("7/8", vm.FindCounter);
        for (var i = 0; i < 6; i++) Press(w, Key.Enter, RawInputModifiers.Shift);
        Assert.Equal("1/8", vm.FindCounter);
        Press(w, Key.Enter, RawInputModifiers.Shift);
        Assert.Equal("8/8", vm.FindCounter);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Esc_closes_the_bar_and_gives_the_keyboard_back_to_the_message_box()
    {
        var (w, vm) = await Open("normal", ZebraConversation());
        await FindAsync(w, vm, "zebra");
        Press(w, Key.Enter);
        Assert.Single(w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<SelectableTextBlock>(), b => !string.IsNullOrEmpty(b.SelectedText));

        Press(w, Key.Escape);
        var composer = w.FindControl<TextBox>("Composer")!;
        await Until(() => !vm.IsFindOpen && composer.IsFocused, "the bar closed, the keyboard in the message box");
        Assert.False(w.FindControl<Border>("FindBar")!.IsVisible);
        // Esc in the bar closes it only: the selection of the match goes, omp is not stopped
        Assert.DoesNotContain(w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<SelectableTextBlock>(), b => !string.IsNullOrEmpty(b.SelectedText));
        Assert.Equal(SessionPhase.Ready, vm.Phase);

        // ⌘F again: the last query, selected to type over
        w.KeyPress(Key.F, Command(w), PhysicalKey.F, null);
        var box = w.FindControl<TextBox>("FindBox")!;
        await Until(() => vm.IsFindOpen && box.IsFocused, "open again");
        Assert.Equal("zebra", box.SelectedText);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_query_without_matches_shows_0_of_0_and_the_keys_do_nothing()
    {
        var (w, vm) = await Open("normal", ZebraConversation());
        await FindAsync(w, vm, "okapi");
        Assert.Equal(0, vm.FindTotal);
        Assert.Equal("0/0", w.FindControl<TextBlock>("FindCounter")!.Text);
        // Let the opened conversation finish laying out first, so a later move can only come from the keys
        double offset;
        do { offset = Scroll(w).Offset.Y; Frame(w); await Task.Delay(50); Frame(w); } while (Scroll(w).Offset.Y != offset);
        var paneOpen = vm.IsSidePaneOpen;
        Press(w, Key.Enter);
        Press(w, Key.Enter, RawInputModifiers.Shift);
        w.KeyPress(Key.G, Command(w), PhysicalKey.G, null);
        w.KeyPress(Key.G, Command(w) | RawInputModifiers.Shift, PhysicalKey.G, null);
        Frame(w);
        Assert.Equal("0/0", vm.FindCounter);
        Assert.Equal(offset, Scroll(w).Offset.Y);
        Assert.False(w.FindControl<Button>("FindNextButton")!.IsEnabled);
        // ⇧⌘G stayed the find bar's: the Changes tab was not toggled
        Assert.Equal(paneOpen, vm.IsSidePaneOpen);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_count_follows_a_streaming_reply_without_moving_the_view_or_the_match_shown()
    {
        var (w, vm) = await Open("slow-stream", ZebraConversation("Where is the tok marker?"));
        var sv = Scroll(w);
        var list = w.FindControl<ListBox>("Transcript")!;
        vm.ComposerText = "go";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Rows.LastOrDefault() is AssistantRowViewModel { HasText: true, Streaming: true }, "the reply streaming");
        Frame(w);
        // The reader goes to the top (Home in the conversation): the search starts there
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home, Source = list });
        Frame(w);
        await FindAsync(w, vm, "tok");
        Assert.Equal(1, vm.FindCurrent);
        var offset = sv.Offset.Y;
        var total = vm.FindTotal;
        for (var frame = 0; frame < 40; frame++)
        {
            Frame(w);
            await Task.Delay(20);
        }
        await Until(() => vm.FindTotal > total, "the count following the reply");
        Frame(w);
        Assert.Equal(1, vm.FindCurrent);
        Assert.Equal($"1/{vm.FindTotal}", vm.FindCounter);
        Assert.Equal(offset, sv.Offset.Y, 0.5);
        if (vm.AbortCommand.CanExecute(null)) vm.AbortCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "the run stopped");
        await vm.DisposeAsync();
        w.Close();
    }
}
