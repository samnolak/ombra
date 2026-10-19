using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using static Ombra.Tests.SessionAreaTests;

namespace Ombra.Tests;

/// <summary>Type-to-focus, focus after a chat switch, the splitters' grab strip and reset, and self-closing notes.</summary>
public sealed class MicroUxUiTests
{
    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name) ?? throw new InvalidOperationException(name + " not found");

    private static Point Center(Control c, Visual top) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top)!.Value;

    /// <summary>
    /// A click in the conversation, beside the reply's text, as a user does before reading with the keys: the conversation
    /// takes the keyboard (ChatScrollController gives it to the transcript; a text block under the pointer may take it itself).
    /// </summary>
    private static async Task<ListBox> ClickInConversationAsync(MainWindow w, MainViewModel vm)
    {
        await PanesUiTests.SendAsync(vm, "hello", () => vm.Rows.OfType<AssistantRowViewModel>().Any());
        var transcript = Named<ListBox>(w, "Transcript");
        var index = vm.Rows.ToList().IndexOf(vm.Rows.OfType<AssistantRowViewModel>().Last());
        var row = transcript.ContainerFromIndex(index)!;
        var at = row.TranslatePoint(new Point(Math.Max(1, row.Bounds.Width - 24), row.Bounds.Height / 2), w)!.Value;
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        await Settle(100);
        Assert.True(transcript.IsKeyboardFocusWithin, "a click in the conversation gives it the keyboard");
        return transcript;
    }

    [AvaloniaFact]
    public async Task A_character_typed_on_a_transcript_row_goes_to_the_composer_and_focuses_it()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("normal", 1180);
        var composer = Named<TextBox>(w, "Composer");
        await ClickInConversationAsync(w, vm);
        Assert.False(composer.IsFocused);

        w.KeyTextInput("a");
        await Settle(100);
        Assert.True(composer.IsFocused);
        Assert.Equal("a", vm.ComposerText);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_character_typed_with_ctrl_or_cmd_held_on_a_transcript_row_does_nothing()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("normal", 1180);
        var composer = Named<TextBox>(w, "Composer");
        await ClickInConversationAsync(w, vm);

        foreach (var modifier in new[] { RawInputModifiers.Control, RawInputModifiers.Meta })
        {
            w.KeyPress(Key.A, modifier, PhysicalKey.A, "a");
            w.KeyTextInput("a");
            await Settle(100);
            Assert.False(composer.IsFocused);
            Assert.Equal("", vm.ComposerText ?? "");
            w.KeyRelease(Key.A, modifier, PhysicalKey.A, "a");
        }
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task After_switching_chats_the_composer_has_the_keyboard()
    {
        // As MultiSessionTests: the "multi" fake writes session files, so chats are listed in the sidebar
        var cwd = SessionCatalog.ResolveLinks(TestProcesses.TempDir("microux-project"));
        var s = new SessionController(TestProcesses.FakeFactory("multi", TestProcesses.TempDir("microux-sessions")), new LaunchRequest(cwd, ApprovalMode: "write"));
        var vm = new MainViewModel(s, new Ombra.App.AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        var composer = Named<TextBox>(w, "Composer");
        // Two listed chats (a chat is listed once omp wrote its file: after its first message)
        await PanesUiTests.SendAsync(vm, "first in A", () => vm.Rows.OfType<AssistantRowViewModel>().Any());
        var first = vm.Session.SessionFile;
        var a = vm.Session;
        vm.NewChatCommand.Execute(null);
        await Until(() => vm.Session != a && vm.Phase == SessionPhase.Ready && vm.Rows.Count == 0, "a new chat");
        await PanesUiTests.SendAsync(vm, "first in B", () => vm.Rows.OfType<AssistantRowViewModel>().Any());
        await Until(() => vm.Sessions.Count == 2, "two listed");
        Named<ListBox>(w, "Transcript").Focus();
        await Settle(100);
        Assert.False(composer.IsFocused);

        Assert.True(vm.CycleSession(1));
        await Until(() => (vm.Session.SessionFile ?? vm.Session.CurrentLaunch.ResumeSessionFile) == first, "the first chat again");
        await Until(() => composer.IsFocused, "composer focused after the switch", 5);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_panel_splitter_grabs_in_a_7px_strip_and_a_double_click_restores_the_default_width()
    {
        var (w, vm) = await PanesUiTests.OpenAsync("plan", 1600);
        vm.ShowPaneCommand.Execute(SidePane.Plan);
        await Settle(300);
        var pane = Named<Control>(w, "RightPanel");
        var splitter = Named<GridSplitter>(w, "RightPanelSplitter");
        Assert.True(splitter.IsVisible);
        Assert.Equal(7, splitter.Bounds.Width);
        Assert.Equal(480, pane.Bounds.Width, 1);

        // Grabbed 3 px off the hairline (inside the strip), dragged 100 px to the left: the panel widens
        var line = Center(splitter, w);
        var grab = new Point(line.X + 3, line.Y);
        Assert.Same(splitter, w.InputHitTest(grab) is Visual hit ? hit.GetSelfAndVisualAncestors().OfType<GridSplitter>().FirstOrDefault() : null);
        w.MouseDown(grab, MouseButton.Left);
        w.MouseMove(new Point(grab.X - 50, grab.Y), RawInputModifiers.LeftMouseButton);
        w.MouseMove(new Point(grab.X - 100, grab.Y), RawInputModifiers.LeftMouseButton);
        w.MouseUp(new Point(grab.X - 100, grab.Y), MouseButton.Left);
        await Settle(300);
        Assert.InRange(pane.Bounds.Width, 560, 600);

        // Double-click: back to the default width, and it stays there
        var at = Center(splitter, w);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        await Settle(300);
        Assert.Equal(480, pane.Bounds.Width, 1);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_hovered_brief_card_stays_past_its_timer_and_closes_after_the_pointer_leaves()
    {
        var e = await OpenAsync();
        var (w, vm) = (e.W, e.Vm);
        vm.BriefCardDuration = TimeSpan.FromMilliseconds(300);
        await vm.CopySessionIdCommand.ExecuteAsync(null);
        await Settle(100);
        Assert.Equal(Loc.T("Session ID copied"), vm.SessionCard?.Title);
        var host = Named<Control>(w, "SessionCardHost");
        w.MouseMove(Center(host, w));
        await Settle(900); // three times its timer
        Assert.Equal(Loc.T("Session ID copied"), vm.SessionCard?.Title);

        w.MouseMove(new Point(w.Bounds.Width / 2, 120));
        await Until(() => vm.SessionCard is null, "the card closes once left", 5);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_same_note_again_extends_the_one_shown_and_others_queue_up_to_two()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        vm.BriefCardDuration = TimeSpan.FromMilliseconds(600);
        await vm.CopySessionIdCommand.ExecuteAsync(null);
        var shown = vm.SessionCard;
        await Settle(400);
        await vm.CopySessionIdCommand.ExecuteAsync(null);
        await vm.CopySessionIdCommand.ExecuteAsync(null);
        Assert.Same(shown, vm.SessionCard);
        Assert.Equal(0, vm.QueuedTimedCards);
        await Settle(400); // past the first timer: the repeat extended it
        Assert.Same(shown, vm.SessionCard);

        // A different note waits its turn, then shows
        vm.CopySessionFileCommand.Execute(null);
        vm.CopySessionFileCommand.Execute(null);
        Assert.Same(shown, vm.SessionCard);
        Assert.Equal(1, vm.QueuedTimedCards);
        await Until(() => vm.SessionCard?.Title == Loc.T("Session file path copied"), "the queued note", 5);
        Assert.Equal(0, vm.QueuedTimedCards);
        await Until(() => vm.SessionCard is null, "all notes closed", 5);
        await vm.DisposeAsync();
        e.W.Close();
    }

    [AvaloniaFact]
    public async Task A_permission_notice_shows_at_once_over_a_brief_which_resumes_after_it()
    {
        var e = await OpenAsync();
        var vm = e.Vm;
        vm.BriefCardDuration = TimeSpan.FromMilliseconds(500);
        await vm.CopySessionIdCommand.ExecuteAsync(null);
        vm.CopySessionFileCommand.Execute(null);
        Assert.Equal(Loc.T("Session ID copied"), vm.SessionCard?.Title);

        vm.ExplainApprovalKeyboardGuard();
        Assert.Equal(Loc.T("Permission needed"), vm.SessionCard?.Title); // at once, not behind the notes
        Assert.Equal(2, vm.QueuedTimedCards); // both notes kept: it does not count against their queue

        // Closed, the interrupted note comes back first, then the one that was waiting
        vm.CloseSessionCard();
        Assert.Equal(Loc.T("Session ID copied"), vm.SessionCard?.Title);
        await Until(() => vm.SessionCard?.Title == Loc.T("Session file path copied"), "the waiting note", 5);
        await Until(() => vm.SessionCard is null, "all notes closed", 5);
        await vm.DisposeAsync();
        e.W.Close();
    }
}
