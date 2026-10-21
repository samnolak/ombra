using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
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
/// Several sidebar chats deleted at once (MainViewModel.SessionSelection.cs) in the real window over the fake shared
/// engine: Select in a row's menu, clicks, Shift and ⌘ clicks, Select all under a search, then one confirmation. Each
/// chat goes through the single delete's path: the chat on screen is closed first, a working one and one another
/// window holds (the runtime refuses it) are skipped and reported; Esc leaves without deleting. With OMBRA_REVIEW_DIR
/// set, the sidebar in selection mode, the confirmation and the outcome are saved.
/// </summary>
public sealed class SessionMultiSelectTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Frames(1);
            await Task.Delay(15);
        }
        Frames();
    }

    private static void Frames(int n = 3)
    {
        for (var i = 0; i < n; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>A saved session as omp writes it (title, header, one message), in the project's folder.</summary>
    private static string WriteSession(string root, string cwd, string title, DateTimeOffset at)
    {
        var id = Guid.NewGuid().ToString();
        var project = Path.Combine(root, "-" + string.Concat(cwd.Select(c => char.IsLetterOrDigit(c) ? c : '-')));
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, id + ".jsonl");
        File.WriteAllLines(file, [
            JsonSerializer.Serialize(new { type = "title", v = 1, title }),
            JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd }),
            JsonSerializer.Serialize(new { type = "message", message = new { role = "assistant", content = Array.Empty<object>(), timestamp = at.ToUnixTimeMilliseconds() } }),
        ]);
        return file;
    }

    private static async Task<(MainWindow W, MainViewModel Vm, string Root, string Cwd)> OpenAsync(string name)
    {
        var root = TestProcesses.TempDir(name + "-sessions");
        var cwd = SessionCatalog.ResolveLinks(TestProcesses.TempDir(name + "-project"));
        var s = new SessionController(TestProcesses.FakeFactory("multi", root), new LaunchRequest(cwd, ApprovalMode: "write"));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (w, vm, root, cwd);
    }

    /// <summary>Sends <paramref name="text"/> in the shown chat; with <paramref name="wait"/> until its reply is complete.</summary>
    private static async Task SendAsync(MainViewModel vm, string text, bool wait = true)
    {
        await Until(() => vm.Phase == SessionPhase.Ready, "ready to send " + text);
        vm.ComposerText = text;
        vm.SendCommand.Execute(null);
        await Until(() => vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == text), "sent " + text);
        if (wait)
            await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<AssistantRowViewModel>().LastOrDefault() is { HasText: true }, "reply to " + text);
    }

    private static async Task<SessionController> NewChatAsync(MainViewModel vm)
    {
        var before = vm.Session;
        vm.NewChatCommand.Execute(null);
        await Until(() => vm.Session != before && vm.Phase == SessionPhase.Ready && vm.Rows.Count == 0, "a new chat");
        return vm.Session;
    }

    private static SessionItemViewModel Row(MainViewModel vm, string file) => vm.Sessions.Single(s => s.Model.Path == file);

    private static Button RowButton(MainWindow w, SessionItemViewModel row) => w.GetVisualDescendants().OfType<Button>()
        .Single(b => b.Classes.Contains("session") && ReferenceEquals(b.DataContext, row) && b.IsEffectivelyVisible);

    /// <summary>A real left click on the row (with modifiers: the window's ⌘ / Shift handling).</summary>
    private static void Click(MainWindow w, SessionItemViewModel row, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var button = RowButton(w, row);
        var point = button.TranslatePoint(new Point(40, button.Bounds.Height / 2), w)!.Value;
        w.MouseDown(point, MouseButton.Left, modifiers);
        w.MouseUp(point, MouseButton.Left, modifiers);
        Frames();
    }

    /// <summary>⌘ on macOS (Control-click is its right click), Ctrl elsewhere.</summary>
    private static RawInputModifiers Command() => OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    /// <summary>The button pressed as a screen reader or the keyboard does (its click, then its command).</summary>
    private static void Press(Button button)
    {
        ((Avalonia.Automation.Provider.IInvokeProvider)Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(button)).Invoke();
        Frames();
    }

    [AvaloniaFact]
    public async Task Rows_picked_by_menu_clicks_and_Select_all_are_deleted_after_one_confirmation_skipping_working_and_held_chats()
    {
        var (w, vm, root, cwd) = await OpenAsync("multi-select");
        try
        {
            // A works in the background; B is shown, idle, with its file
            await SendAsync(vm, "first in A");
            var a = vm.Session;
            var fileA = a.SessionFile!;
            await SendAsync(vm, "FOREVER in A", wait: false);
            var b = await NewChatAsync(vm);
            await SendAsync(vm, "first in B");
            var fileB = b.SessionFile!;
            var one = WriteSession(root, cwd, "Alpha one", Now.AddMinutes(-10));
            var two = WriteSession(root, cwd, "Alpha two", Now.AddMinutes(-20));
            var beta = WriteSession(root, cwd, "Beta three", Now.AddMinutes(-30));
            var four = WriteSession(root, cwd, "Alpha four", Now.AddMinutes(-40));
            // Another window holds this one: the runtime refuses to delete it
            var held = WriteSession(root, cwd, "Held five", Now.AddMinutes(-50));
            File.WriteAllText(held + ".fakelease", """{"id":"h1","kind":"ombra","pid":4242,"since":"2026-10-17T10:00:00.000Z"}""");
            vm.RequestCatalogRefresh();
            await Until(() => new[] { fileA, fileB, one, two, beta, four, held }.All(f => vm.Sessions.Any(s => s.Model.Path == f)), "every chat listed");
            await Until(() => Row(vm, fileA).Status == SessionStatus.Running, "A marked as working");

            // Right-click → Select: selection mode with that row picked, a named check box on every row
            var row = w.GetVisualDescendants().OfType<Panel>().Single(p => p.Classes.Contains("session-row") && ReferenceEquals(p.DataContext, Row(vm, one)));
            var menu = Assert.IsType<Flyout>(row.ContextFlyout);
            menu.ShowAt(row);
            Frames();
            MicroUxTabTests.Shot(w, "sidebar-row-menu-select");
            var menuButtons = ((Control)menu.Content!).GetVisualDescendants().OfType<Button>().ToList();
            Assert.False(menuButtons.Single(b => b.Name == "DeleteSelectedItem").IsVisible);
            Press(menuButtons.Single(b => AutomationProperties.GetName(b) == Loc.T("Select")));
            Assert.True(vm.Selection.IsActive);
            Assert.True(Row(vm, one).IsSelected);
            Assert.Equal(1, vm.Selection.Count);
            Assert.Equal(Loc.Plural(1, "{0} selected", "{0} selected"), w.FindControl<TextBlock>("SelectionCountText")!.Text);
            var checks = w.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Classes.Contains("session-check") && c.IsEffectivelyVisible).ToList();
            Assert.Equal(vm.Sessions.Count, checks.Count);
            Assert.Contains(checks, c => AutomationProperties.GetName(c) == Loc.F("Select {0}", "Alpha one") && c.IsChecked == true);
            Assert.All(checks, c => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(c))));
            Assert.Contains(Loc.T("selected"), Row(vm, one).AccessibleName);

            // A click toggles a row instead of opening it; Shift-click adds the range from the row picked last
            Click(w, Row(vm, two));
            Assert.True(Row(vm, two).IsSelected);
            Assert.Same(b, vm.Session);
            Click(w, Row(vm, four), RawInputModifiers.Shift);
            Assert.True(Row(vm, beta).IsSelected && Row(vm, four).IsSelected);
            Assert.Equal(4, vm.Selection.Count);
            Click(w, Row(vm, beta));
            Click(w, Row(vm, two));
            Assert.False(Row(vm, beta).IsSelected || Row(vm, two).IsSelected);
            Assert.Equal(2, vm.Selection.Count);

            // Select all picks what the search lists, nothing it hides
            vm.SessionFilter = "Alpha";
            await Until(() => vm.Sessions.Count == 3, "only the Alpha chats listed");
            Press(w.FindControl<Button>("SelectAllButton")!);
            Assert.Equal(3, vm.Selection.Count);
            vm.SessionFilter = "";
            await Until(() => vm.Sessions.Count == 7, "every chat listed again");
            Assert.Equal(3, vm.Selection.Count);
            Assert.Equal(new[] { one, two, four }.Order(), vm.Sessions.Where(s => s.IsSelected).Select(s => s.Model.Path).Order());
            Assert.False(Row(vm, beta).IsSelected || Row(vm, held).IsSelected);

            // ⌘-click (Ctrl elsewhere) adds the working chat, the one shown and the held one
            foreach (var f in new[] { fileA, fileB, held }) Click(w, Row(vm, f), Command());
            Assert.Equal(6, vm.Selection.Count);
            Assert.Same(b, vm.Session);
            MicroUxTabTests.Shot(w, "sidebar-selection");

            // A selected row's menu offers the whole selection
            menu.ShowAt(row);
            Frames();
            var deleteSelected = ((Control)menu.Content!).GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DeleteSelectedItem");
            Assert.True(deleteSelected.IsVisible);
            Assert.Equal(Loc.Plural(6, "Delete {0} chat", "Delete {0} chats"), AutomationProperties.GetName(deleteSelected));
            menu.Hide();
            Frames();

            // Delete (the key, on a row) asks once, naming the count, the files and the working chat it skips
            RowButton(w, Row(vm, one)).Focus();
            w.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Frames();
            var card = vm.SessionCard!;
            Assert.Equal("delete", card.Kind);
            Assert.Equal(SessionCardState.Ask, card.State);
            Assert.Equal(Loc.Plural(6, "Delete {0} chat?", "Delete {0} chats?"), card.Title);
            Assert.Contains(Loc.T("Their conversation files and artifacts are deleted from disk. This cannot be undone."), card.Message);
            Assert.Contains(Loc.Plural(1, "{0} of them is working or open in another window: it is skipped.", "{0} of them are working or open in another window: they are skipped."), card.Message);
            Assert.True(File.Exists(one), "nothing is deleted before the answer");
            MicroUxTabTests.Shot(w, "sidebar-selection-confirm");

            await card.Primary.Run();
            await Until(() => !File.Exists(fileB) && !vm.OpenChats.Contains(b), "deleted");
            Assert.False(vm.Selection.IsActive);
            Assert.Equal(0, vm.Selection.Count);
            Assert.All(new[] { one, two, four, fileB }, f => Assert.False(File.Exists(f), f));
            Assert.All(new[] { beta, fileA, held }, f => Assert.True(File.Exists(f), f));
            Assert.Equal(SessionPhase.Running, a.Snapshot().Phase); // never stopped
            Assert.NotSame(b, vm.Session); // the chat on screen was closed first
            Assert.NotEqual(Loc.T("Recovered unsent input. Review it before sending; nothing was sent automatically."), vm.ComposerMessage);
            var result = vm.SessionCard!;
            Assert.Equal(Loc.F("Deleted {0}, {1} skipped: {2}", 4, 2, Loc.T("working") + ", " + Loc.T("open in another window")), result.Title);
            Assert.Contains("Held five — " + Loc.T("open in another window"), result.Detail);
            Assert.Contains("first in A — " + Loc.T("working"), result.Detail);
            await Until(() => new[] { one, two, four, fileB }.All(f => vm.Sessions.All(s => s.Model.Path != f)), "deleted rows gone from the list");
            Assert.All(vm.Sessions, s => Assert.False(s.IsSelected));
            await Task.Delay(300); // the row actions' fade after the last click
            Frames();
            MicroUxTabTests.Shot(w, "sidebar-selection-result");
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task Esc_and_Cancel_leave_selection_mode_without_deleting_and_Shift_reaches_from_the_chat_shown()
    {
        var (w, vm, root, cwd) = await OpenAsync("multi-select-esc");
        try
        {
            await SendAsync(vm, "shown chat");
            var shown = vm.Session.SessionFile!;
            var files = new[]
            {
                WriteSession(root, cwd, "First", Now.AddMinutes(-10)),
                WriteSession(root, cwd, "Second", Now.AddMinutes(-20)),
                WriteSession(root, cwd, "Third", Now.AddMinutes(-30)),
            };
            vm.RequestCatalogRefresh();
            await Until(() => files.Append(shown).All(f => vm.Sessions.Any(s => s.Model.Path == f)), "every chat listed");
            var order = vm.SessionGroups.SelectMany(g => g.Items).ToList();
            var from = order.FindIndex(s => s.Model.Path == shown);
            var to = order.FindIndex(s => s.Model.Path == files[1]);

            // Outside selection mode Shift-click reaches from the chat on screen, which stays open
            Click(w, Row(vm, files[1]), RawInputModifiers.Shift);
            Assert.True(vm.Selection.IsActive);
            Assert.Equal(Math.Abs(to - from) + 1, vm.Selection.Count);
            Assert.True(Row(vm, shown).IsSelected && Row(vm, files[1]).IsSelected);
            Assert.Equal(shown, vm.Session.SessionFile);

            // Backspace asks; Esc then leaves selection mode and takes the question away: nothing deleted
            RowButton(w, Row(vm, files[0])).Focus();
            w.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
            Frames();
            Assert.Equal(SessionCardState.Ask, vm.SessionCard!.State);
            w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Frames();
            Assert.False(vm.Selection.IsActive);
            Assert.Null(vm.SessionCard);
            Assert.All(vm.Sessions, s => Assert.False(s.IsSelected));
            Assert.False(w.FindControl<Border>("SelectionBar")!.IsVisible);
            Assert.All(files.Append(shown), f => Assert.True(File.Exists(f), f));
            Assert.Equal(SessionPhase.Ready, vm.Phase); // Esc did not reach the run

            // ⌘-click starts selection mode with that row only; Cancel leaves it; a plain click opens a chat again
            Click(w, Row(vm, files[2]), Command());
            Assert.True(vm.Selection.IsActive);
            Assert.Equal(1, vm.Selection.Count);
            Assert.True(Row(vm, files[2]).IsSelected);
            Press(w.FindControl<Button>("CancelSelectionButton")!);
            Assert.False(vm.Selection.IsActive);
            Assert.All(files.Append(shown), f => Assert.True(File.Exists(f), f));
            Click(w, Row(vm, files[2]));
            await Until(() => vm.Session.SessionFile == files[2], "a plain click opens the chat");
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }
}
