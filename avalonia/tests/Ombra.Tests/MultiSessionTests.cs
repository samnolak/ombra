using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

/// <summary>
/// Several chats at once in the real window over one fake shared engine ("multi": the message decides
/// the turn — FOREVER streams until stopped, LONG streams for 3 s, APPROVE asks an approval, CRASH exits mid-reply).
/// Switching or stopping a logical chat preserves its peers; a host crash faults every chat and requires explicit recovery.
/// </summary>
public sealed class MultiSessionTests
{
    private static async Task Until(Func<bool> condition, string what, int seconds = 30)
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

    /// <summary>One frame: pending work run, then laid out and drawn (what the user sees).</summary>
    private static void Frame(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        using (w.CaptureRenderedFrame()) { }
    }

    private sealed record Opened(MainWindow W, MainViewModel Vm, AttentionTests.RecordingNotifier N, string Root, string Cwd);

    private static async Task<Opened> OpenAsync(string? resume = null, string? root = null, string? cwd = null)
    {
        root ??= TestProcesses.TempDir("multi-sessions");
        // As omp records it: its working directory with symbolic links resolved (macOS: /var is /private/var)
        cwd ??= SessionCatalog.ResolveLinks(TestProcesses.TempDir("multi-project"));
        var s = new SessionController(TestProcesses.FakeFactory("multi", root), new LaunchRequest(cwd, resume, ApprovalMode: "write"));
        var n = new AttentionTests.RecordingNotifier();
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760, Notifier = n };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return new(w, vm, n, root, cwd);
    }

    /// <summary>Sends <paramref name="text"/> in the shown chat and waits until it is in the conversation.</summary>
    private static async Task SendAsync(MainViewModel vm, string text)
    {
        await Until(() => vm.Phase == SessionPhase.Ready, "ready to send " + text);
        vm.ComposerText = text;
        vm.SendCommand.Execute(null);
        await Until(() => vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == text), "sent " + text);
    }

    /// <summary>Sends <paramref name="text"/> and waits for the turn to end.</summary>
    private static async Task RunAsync(MainViewModel vm, string text)
    {
        await SendAsync(vm, text);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Last().Text == text
            && vm.Rows.OfType<AssistantRowViewModel>().LastOrDefault() is { HasText: true }, "reply to " + text);
    }

    /// <summary>A new chat (⌘N) while the shown one may be working; waits until its logical session is ready.</summary>
    private static async Task<SessionController> NewChatAsync(MainViewModel vm)
    {
        var before = vm.Session;
        vm.NewChatCommand.Execute(null);
        await Until(() => vm.Session != before && vm.Phase == SessionPhase.Ready && vm.Rows.Count == 0, "a new chat");
        return vm.Session;
    }

    private static string ReplyText(SessionSnapshot s) => s.Items.OfType<AssistantItem>().LastOrDefault()?.Text ?? "";

    private static bool Alive(int? pid)
    {
        if (pid is not { } id) return false;
        try
        {
            using var p = Process.GetProcessById(id);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static SessionItemViewModel? Row(MainViewModel vm, string file) => vm.Sessions.FirstOrDefault(i => i.Model.Path == file);

    private static ScrollViewer Scroll(Window w) => w.FindControl<ListBox>("Transcript")!.GetVisualDescendants().OfType<ScrollViewer>().First();

    private static double? Top(Control? c, Visual to) => c?.IsEffectivelyVisible == true ? c.TranslatePoint(default, to)?.Y : null;

    [AvaloniaFact]
    public async Task Extension_status_and_widgets_stay_safe_when_switching_chats()
    {
        const string controls = "日本語 e\u0301 👩‍💻 \u001b]0;title\u0007 \u009b31m\u009dosc\u009c\r\n\t";
        const string visible = "日本語 e\u0301 👩‍💻 ␛]0;title␇ ⟦U+009B⟧31m⟦U+009D⟧osc⟦U+009C⟧\r\n\t";
        var (w, vm, _, _, _) = await OpenAsync();
        var observed = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.WidgetsText)) observed.Add(vm.WidgetsText);
            if (e.PropertyName == nameof(vm.StatusDetail)) observed.Add(vm.StatusDetail);
        };
        static void Publish(SessionController session, string text)
        {
            var status = JsonSerializer.SerializeToElement(new
            {
                type = "extension_ui_request", id = "status", method = "setStatus", statusKey = "test", statusText = text,
            });
            var widget = JsonSerializer.SerializeToElement(new
            {
                type = "extension_ui_request", id = "widget", method = "setWidget", widgetKey = "test", widgetLines = new[] { text },
            });
            session.Mutate(state =>
            {
                state.Apply(new RpcFrame("extension_ui_request", null, status, Stopwatch.GetTimestamp(), 0), DateTimeOffset.UtcNow);
                state.Apply(new RpcFrame("extension_ui_request", null, widget, Stopwatch.GetTimestamp(), 0), DateTimeOffset.UtcNow);
            });
        }
        static SessionItemViewModel SessionRow(SessionController session) => new(new SessionSummary(
            session.SessionFile!, "test", session.CurrentLaunch.WorkingDirectory!, "Control safety", DateTimeOffset.UtcNow));
        void AssertShown(string prefix)
        {
            Frame(w);
            Assert.Equal(prefix + visible, vm.WidgetsText);
            Assert.Contains(prefix + visible, vm.StatusDetail);
            Assert.Contains(w.GetVisualDescendants().OfType<SelectableTextBlock>(),
                t => t.IsEffectivelyVisible && t.Text == prefix + visible);
            Assert.Equal(vm.StatusDetail, ToolTip.GetTip(w.FindControl<Grid>("ActivityLine")!));
        }
        try
        {
            await RunAsync(vm, "keep chat A");
            var a = vm.Session;
            Publish(a, "A " + controls);
            vm.Apply(a.Snapshot());
            AssertShown("A ");
            var original = a.Snapshot();
            var b = await NewChatAsync(vm);
            Assert.Empty(vm.WidgetsText);
            await RunAsync(vm, "keep chat B");
            Publish(b, "B " + controls);
            vm.Apply(b.Snapshot());
            AssertShown("B ");

            // EnterSession restores its cached widget text before Apply sees unchanged widget records.
            foreach (var (session, prefix) in new[] { (a, "A "), (b, "B "), (a, "A ") })
            {
                await vm.OpenSessionCommand.ExecuteAsync(SessionRow(session));
                Assert.Same(session, vm.Session);
                AssertShown(prefix);
                Assert.Equal(prefix + controls, session.Snapshot().ExtensionStatus["test"]);
                Assert.Equal(prefix + controls, Assert.Single(session.Snapshot().Widgets).Lines[0]);
            }
            Assert.Equal("A " + controls, original.ExtensionStatus["test"]);
            Assert.Equal("A " + controls, Assert.Single(original.Widgets).Lines[0]);
            Assert.NotEmpty(observed);
            Assert.All(observed, text => Assert.DoesNotContain(text,
                c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')));
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_chat_streams_on_while_another_is_used_and_comes_back_whole_where_it_was_left()
    {
        var root = TestProcesses.TempDir("multi-sessions");
        var cwd = SessionCatalog.ResolveLinks(TestProcesses.TempDir("multi-project"));
        var file = ChatScrollTests.WriteSession(root, cwd, "Long chat", ChatScrollTests.LongConversation(12));
        var (w, vm, _, _, _) = await OpenAsync(file, root, cwd);
        await Until(() => vm.Rows.Count >= 60, "the long chat loaded");
        var a = vm.Session;
        var aPid = a.ProcessId;

        // A long reply streams; the reader scrolls up to read something earlier meanwhile
        await SendAsync(vm, "FOREVER in A");
        await Until(() => vm.Rows.LastOrDefault() is AssistantRowViewModel { HasText: true }, "A streaming");
        var list = w.FindControl<ListBox>("Transcript")!;
        var sv = Scroll(w);
        Frame(w);
        var over = list.TranslatePoint(new Point(list.Bounds.Width / 2, 100), w)!.Value;
        for (var i = 0; i < 6; i++)
        {
            w.MouseWheel(over, new Vector(0, 1));
            Frame(w);
        }
        Assert.True(w.FindControl<Button>("JumpToLatest")!.IsVisible, "the reader left the latest message");
        var held = list.GetRealizedContainers().First(c => Top(c, sv) is > 0 and < 200);
        var heldRow = (RowViewModel)held.DataContext!;
        var heldTop = Top(held, sv)!.Value;
        var rowsOfA = vm.Rows.ToList();

        // Another logical chat shares the engine while A's run continues independently.
        var b = await NewChatAsync(vm);
        Assert.Equal(aPid, b.ProcessId);
        Assert.Equal(aPid, a.ProcessId);
        Assert.Equal(SessionPhase.Running, a.Snapshot().Phase);
        var streamedWhenLeft = ReplyText(a.Snapshot()).Length;
        await RunAsync(vm, "hello from B");
        await Until(() => ReplyText(a.Snapshot()).Length > streamedWhenLeft + 100, "A streamed on in the background");
        Assert.Equal(SessionPhase.Running, a.Snapshot().Phase);
        await Until(() => Row(vm, file)?.Status == SessionStatus.Running, "A marked as working in the sidebar");
        Assert.True(Row(vm, file)!.OpenCommand!.CanExecute(Row(vm, file)), "a chat's row stays enabled while chats work");

        // Back to A: the same rows (none built again, none twice), the whole reply, the reader where they were
        vm.OpenSessionCommand.Execute(Row(vm, file));
        await Until(() => vm.Session == a, "A shown again");
        var streamed = ReplyText(a.Snapshot()).Length;
        await Until(() => vm.Rows.OfType<AssistantRowViewModel>().Last().Text.Length >= streamed, "A's whole reply");
        Assert.Equal(rowsOfA, vm.Rows.ToList());
        Assert.Single(vm.Rows.OfType<UserRowViewModel>(), u => u.Text == "FOREVER in A");
        Frame(w);
        Frame(w);
        Assert.True(w.FindControl<Button>("JumpToLatest")!.IsVisible, "still reading above the latest message");
        var again = list.ContainerFromItem(heldRow) as Control;
        Assert.NotNull(again);
        Assert.InRange(Top(again, sv)!.Value, heldTop - 1, heldTop + 1);

        // B kept its own conversation meanwhile
        vm.AbortCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "A stopped");
        Assert.Equal(["hello from B"], b.Snapshot().Items.OfType<UserItem>().Select(u => u.Text));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task An_approval_in_a_background_chat_marks_it_and_its_notification_opens_it_on_the_card()
    {
        var (w, vm, n, _, _) = await OpenAsync();
        await RunAsync(vm, "first in A");
        var a = vm.Session;
        var file = a.SessionFile!;
        await SendAsync(vm, "LONG, then APPROVE a command");
        await NewChatAsync(vm);
        vm.ComposerText = "typing in B";

        await Until(() => a.Snapshot().Dialogs.Count == 1, "A asks for an approval");
        await Until(() => Row(vm, file)?.Status == SessionStatus.Waiting, "A marked as needing an answer");
        Assert.False(vm.HasDialog); // A's question is not shown over B
        await Until(() => n.Calls.Any(c => c.Title.StartsWith(Loc.T("omp needs your approval"), StringComparison.Ordinal)), "the notification");
        var call = n.Calls.Single(c => c.Title.StartsWith(Loc.T("omp needs your approval"), StringComparison.Ordinal));
        Assert.Contains("first in A", call.Title); // whose question it is

        call.Clicked();
        await Until(() => vm.Session == a && vm.HasDialog, "the notification opened A on its card");
        Frame(w);
        Assert.True(vm.CurrentDialog!.IsApproval);
        Assert.Contains(w.GetVisualDescendants().OfType<Border>(), b => b.Name == "DialogCard" && b.IsEffectivelyVisible);
        Assert.Single(n.Calls); // shown, it does not notify again

        vm.CurrentDialog.AllowCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && Row(vm, file)?.Status == SessionStatus.None, "A finished");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Each_chat_keeps_its_own_draft_and_attachments()
    {
        var (w, vm, _, _, _) = await OpenAsync();
        await RunAsync(vm, "first in A");
        var a = vm.Session;
        var file = a.SessionFile!;
        var composer = w.FindControl<TextBox>("Composer")!;
        vm.ComposerText = "draft for A";
        Assert.True(vm.TryAddImage(new ImageAttachment("dot.png", "image/png", Convert.FromBase64String(OnePixelPng)), "dot.png"));
        var chip = Assert.Single(vm.Attachments);

        var b = await NewChatAsync(vm);
        Assert.Equal("", vm.ComposerText);
        Assert.Equal("", composer.Text ?? "");
        Assert.Empty(vm.Attachments);
        await RunAsync(vm, "first in B");
        var fileB = b.SessionFile!;
        vm.ComposerText = "draft for B";

        await Until(() => Row(vm, file) is not null && Row(vm, fileB) is not null, "both listed");
        await vm.OpenSessionCommand.ExecuteAsync(Row(vm, file));
        Assert.Equal(a, vm.Session);
        Assert.Equal("draft for A", vm.ComposerText);
        Frame(w);
        Assert.Equal("draft for A", composer.Text);
        Assert.Same(chip, Assert.Single(vm.Attachments));

        await vm.OpenSessionCommand.ExecuteAsync(Row(vm, fileB));
        Assert.Equal(b, vm.Session);
        Assert.Equal("draft for B", vm.ComposerText);
        Assert.Empty(vm.Attachments);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Chats_in_different_projects_run_at_once_and_a_new_one_never_stops_a_run()
    {
        var (w, vm, _, _, cwd) = await OpenAsync();
        var a = vm.Session;
        await SendAsync(vm, "FOREVER in the first project");
        var other = SessionCatalog.ResolveLinks(TestProcesses.TempDir("multi-other-project"));
        // Open folder… while omp works: a new omp in that folder, the run goes on
        vm.PickFolderRequested += () => Task.FromResult<string?>(other);
        Assert.True(vm.OpenFolderCommand.CanExecute(null));
        await vm.OpenFolderCommand.ExecuteAsync(null);
        await Until(() => vm.Session != a && vm.Phase == SessionPhase.Ready, "a chat in the other project");
        var b = vm.Session;
        Assert.True(SessionCatalog.SamePath(other, b.Snapshot().Cwd));
        Assert.True(SessionCatalog.SamePath(cwd, a.Snapshot().Cwd));
        Assert.Equal(SessionPhase.Running, a.Snapshot().Phase);
        await RunAsync(vm, "hello in the other project");
        Assert.True(Alive(a.ProcessId) && Alive(b.ProcessId));
        Assert.Equal(SessionPhase.Running, a.Snapshot().Phase);
        Assert.Equal(SessionItemViewModel.ProjectName(other), vm.ProjectName);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Deleting_a_chat_that_works_in_the_background_asks_then_stops_its_omp()
    {
        var (w, vm, _, _, _) = await OpenAsync();
        await RunAsync(vm, "first in A");
        var a = vm.Session;
        var file = a.SessionFile!;
        var pid = a.ProcessId;
        await SendAsync(vm, "FOREVER in A");
        await NewChatAsync(vm);
        await Until(() => Row(vm, file)?.Status == SessionStatus.Running, "A working in the sidebar");

        vm.DeleteSessionItemCommand.Execute(Row(vm, file));
        var card = vm.SessionCard!;
        Assert.Equal("delete", card.Kind);
        Assert.Contains(Loc.T("omp is working in it: deleting stops its run, then the conversation's file and its artifacts are deleted. This cannot be undone."), card.Message);
        Assert.Equal(Loc.T("Stop and delete"), card.Primary!.Label);
        Assert.True(Alive(pid), "nothing happens before the answer");
        Assert.Equal(SessionPhase.Running, a.Snapshot().Phase);

        card.Primary.Command.Execute(null);
        await Until(() => !File.Exists(file) && !vm.OpenChats.Contains(a), "deleted");
        Assert.True(Alive(pid), "closing one logical session keeps the engine alive");
        await Until(() => Row(vm, file) is null, "gone from the sidebar");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_new_permission_mode_restarts_idle_chats_now_and_a_working_one_after_its_run()
    {
        var (w, vm, _, _, _) = await OpenAsync();
        await RunAsync(vm, "first in A");
        var a = vm.Session;
        var fileA = a.SessionFile!;
        await SendAsync(vm, "FOREVER in A");
        var b = await NewChatAsync(vm);
        await RunAsync(vm, "first in B");
        var c = await NewChatAsync(vm);
        await RunAsync(vm, "first in C");
        var (pidA, pidB, pidC) = (a.ProcessId, b.ProcessId, c.ProcessId);

        vm.SetApprovalModeCommand.Execute("always-ask");
        // The idle chats (B in the background, C shown) restart with it at once. The shown phase alone said nothing: it
        // still read Ready from before the restart until the next update was applied, so the header's mode was the old one.
        await Until(() => b.CurrentLaunch.ApprovalMode == "always-ask" && c.CurrentLaunch.ApprovalMode == "always-ask"
            && b.ProcessId == pidB && c.ProcessId == pidC && c.Snapshot().Phase == SessionPhase.Ready
            && vm.Phase == SessionPhase.Ready && vm.ApprovalMode == "always-ask", "idle chats restarted, the header showing the new mode");
        // The working one keeps its run, and the user is told
        Assert.Equal(pidA, a.ProcessId);
        Assert.Equal("write", a.CurrentLaunch.ApprovalMode);
        Assert.Equal(SessionPhase.Running, a.Snapshot().Phase);
        await Until(() => vm.SessionCard?.Message.Contains(Loc.T("A chat that is working (marked in the sidebar) switches when its run ends: omp restarts then."), StringComparison.Ordinal) == true, "told about the working chat");
        Assert.Equal(["first in B"], b.Snapshot().Items.OfType<UserItem>().Select(u => u.Text)); // restarted on its session

        // A's run ends: it switches then
        await vm.OpenSessionCommand.ExecuteAsync(Row(vm, fileA));
        Assert.Equal("always-ask", vm.ShownApprovalMode); // its menu shows the mode waiting for the run to end
        vm.AbortCommand.Execute(null);
        await Until(() => a.CurrentLaunch.ApprovalMode == "always-ask" && a.ProcessId == pidA && a.Snapshot().Phase == SessionPhase.Ready
            && vm.Phase == SessionPhase.Ready && vm.ApprovalMode == "always-ask", "A restarted after its run, the header showing the new mode");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Opening_chats_keeps_idle_and_working_sessions_in_one_engine()
    {
        var (w, vm, _, _, _) = await OpenAsync();
        await RunAsync(vm, "first in A");
        var a = vm.Session;
        await SendAsync(vm, "FOREVER in A");
        var b = await NewChatAsync(vm);
        await RunAsync(vm, "first in B");
        var c = await NewChatAsync(vm);
        Assert.Equal([a, b, c], vm.OpenChats);
        Assert.Equal(SessionPhase.Running, a.Snapshot().Phase);
        Assert.Equal(SessionPhase.Ready, b.Snapshot().Phase);
        Assert.Equal(a.ProcessId, b.ProcessId);
        Assert.Equal(a.ProcessId, c.ProcessId);
        vm.ToggleDebugCommand.Execute(null);
        Assert.StartsWith(Loc.T("1 omp process"), vm.OmpProcessesText);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Process_settings_wait_for_all_chats_then_restart_the_shared_engine()
    {
        var (w, vm, _, _, _) = await OpenAsync();
        await RunAsync(vm, "first in A");
        var a = vm.Session;
        await SendAsync(vm, "FOREVER in A");
        var b = await NewChatAsync(vm);
        await RunAsync(vm, "first in B");
        var pid = a.ProcessId;
        Assert.False(await vm.RestartEnginesAsync());
        Assert.Equal(2, vm.PendingEngineRestarts);
        Assert.Equal(pid, b.ProcessId);
        Assert.Equal(SessionPhase.Running, a.Snapshot().Phase);
        await a.AbortAsync();
        await Until(() => a.Snapshot().Phase == SessionPhase.Ready && b.Snapshot().Phase == SessionPhase.Ready
            && a.ProcessId != pid && a.ProcessId == b.ProcessId, "all chats resumed in the replacement engine");
        Assert.Equal(0, vm.PendingEngineRestarts);
        Assert.Equal("write", a.CurrentLaunch.ApprovalMode);
        Assert.Equal("write", b.CurrentLaunch.ApprovalMode);
        Assert.Contains(a.Snapshot().Items, i => i is UserItem { Text: "first in A" });
        Assert.Contains(b.Snapshot().Items, i => i is UserItem { Text: "first in B" });
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Quitting_asks_about_every_working_chat_and_stops_every_omp()
    {
        var (w, vm, _, _, _) = await OpenAsync();
        await RunAsync(vm, "first in A");
        var a = vm.Session;
        await SendAsync(vm, "FOREVER in A");
        var b = await NewChatAsync(vm);
        await RunAsync(vm, "first in B");
        await NewChatAsync(vm);
        await RunAsync(vm, "first in C");
        var pids = vm.OpenChats.Select(c => c.ProcessId).ToList();
        Assert.Equal(3, pids.Count);
        Assert.All(pids, p => Assert.True(Alive(p)));

        // The chat that works is in the background: the question names it
        w.Close();
        await Until(() => vm.SessionCard is { Kind: "quit" }, "asked before quitting");
        Assert.Equal(Loc.F("omp is still working in “{0}”", "first in A"), vm.SessionCard!.Title);
        vm.SessionCard.Primary!.Command.Execute(null);
        await Until(() => pids.All(p => !Alive(p)), "every omp stopped", 20);
        Assert.Equal(SessionPhase.Stopped, a.Snapshot().Phase);
        Assert.Equal(SessionPhase.Stopped, b.Snapshot().Phase);
    }

    [AvaloniaFact]
    public async Task A_shared_engine_crash_in_a_background_chat_requires_explicit_recovery_without_resending()
    {
        var (w, vm, n, _, _) = await OpenAsync();
        try
        {
            await RunAsync(vm, "first in A");
            var a = vm.Session;
            var fileA = a.SessionFile!;
            var b = await NewChatAsync(vm);
            await RunAsync(vm, "first in B");
            var fileB = b.SessionFile!;
            var pid = a.ProcessId;
            Assert.NotNull(pid);
            Assert.Equal(pid, b.ProcessId);
            vm.ComposerText = "B's unsent draft";

            await vm.OpenSessionCommand.ExecuteAsync(Row(vm, fileA));
            Assert.Same(a, vm.Session);
            var notifications = n.Calls.Count;
            await SendAsync(vm, "FOREVER in A");
            Assert.True(await a.QueueAsync(QueueKind.FollowUp, "A's unsent queued input", []));
            await vm.OpenSessionCommand.ExecuteAsync(Row(vm, fileB));
            Assert.Same(b, vm.Session);
            Assert.Contains(a.Snapshot().Queued, input => input.Text == "A's unsent queued input" && !input.Recovered);
            // Crash only after A is queued and in the background; a timed crash can beat queue admission.
            using (var process = Process.GetProcessById(pid.Value)) process.Kill();

            await Until(() => a.Snapshot().Phase == SessionPhase.Faulted && vm.Phase == SessionPhase.Faulted,
                "the shared host crash faults both chats");
            await Until(() => Row(vm, fileA)?.Status == SessionStatus.Error && Row(vm, fileB)?.Status == SessionStatus.Error,
                "both chats marked as stopped by an error");
            await Until(() => n.Calls.Count > notifications, "the background crash is announced");
            Assert.False(Alive(pid));
            Assert.True(vm.CanRecover);
            Assert.True(vm.ShowRecoverCard);
            Assert.Equal("B's unsent draft", vm.ComposerText);

            // Recovering the shown chat starts one replacement host, not its failed peer or any saved input.
            await vm.RecoverCommand.ExecuteAsync(null);
            await Until(() => vm.Phase == SessionPhase.Ready, "B explicitly recovered");
            Assert.NotEqual(pid, b.ProcessId);
            Assert.Equal(SessionPhase.Faulted, a.Snapshot().Phase);
            Assert.Equal("B's unsent draft", vm.ComposerText);
            Assert.Equal(["first in B"], b.Snapshot().Items.OfType<UserItem>().Select(item => item.Text));
            await RunAsync(vm, "hello from B");

            await vm.OpenSessionCommand.ExecuteAsync(Row(vm, fileA));
            Assert.Same(a, vm.Session);
            Assert.True(vm.CanRecover);
            Assert.True(vm.ShowRecoverCard);
            Assert.Equal("A's unsent queued input", vm.ComposerText);
            await vm.RecoverCommand.ExecuteAsync(null);
            await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == "first in A"),
                "A explicitly recovered on its saved session");
            Assert.Equal(b.ProcessId, a.ProcessId);
            Assert.Equal("A's unsent queued input", vm.ComposerText);
            Assert.DoesNotContain(a.Snapshot().Items.OfType<UserItem>(), item => item.Text == "A's unsent queued input");
            Assert.Contains(b.Snapshot().Items, item => item is UserItem { Text: "hello from B" });
            await Until(() => Row(vm, fileA)?.Status == SessionStatus.None, "A no longer marked");
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_new_chat_with_only_a_draft_stays_in_the_sidebar_and_can_be_shown_again()
    {
        // Review R4 #1: an empty session was listed only while shown, so a new chat left with a draft vanished from
        // the sidebar and kept its omp with no way back to it.
        var (w, vm, _, _, _) = await OpenAsync();
        await RunAsync(vm, "first in A");
        var fileA = vm.Session.SessionFile!;
        var n = await NewChatAsync(vm);
        var fileN = n.SessionFile!;
        vm.ComposerText = "an unsent draft";
        await Until(() => Row(vm, fileA) is not null, "A listed");
        await vm.OpenSessionCommand.ExecuteAsync(Row(vm, fileA));
        Assert.Contains(n, vm.OpenChats); // a chat with a draft is kept
        vm.RequestCatalogRefresh();
        await Until(() => Row(vm, fileN) is not null, "the draft chat still listed");
        await vm.OpenSessionCommand.ExecuteAsync(Row(vm, fileN));
        Assert.Same(n, vm.Session);
        Assert.Equal("an unsent draft", vm.ComposerText);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_new_chat_whose_first_reply_still_streams_is_listed_and_can_be_shown_again()
    {
        // Real-app QA D3: omp writes a new session's file only once its first reply is complete, so a new chat left while
        // that reply streamed was missing from the sidebar (no way back to it) until the reply ended.
        var (w, vm, _, _, _) = await OpenAsync();
        var a = vm.Session;
        await SendAsync(vm, "FOREVER first in A");
        await Until(() => vm.Rows.LastOrDefault() is AssistantRowViewModel { HasText: true }, "A streaming");
        var fileA = a.SessionFile!;
        Assert.False(File.Exists(fileA), "like omp, the fake writes a new session's file only after its first reply");
        await NewChatAsync(vm);
        await RunAsync(vm, "first in B");
        await Until(() => Row(vm, fileA) is { Status: SessionStatus.Running }, "A listed as working while its reply streams");
        Assert.Equal("FOREVER first in A", Row(vm, fileA)!.Title);
        Assert.Equal(SessionPhase.Running, a.Snapshot().Phase);
        await vm.OpenSessionCommand.ExecuteAsync(Row(vm, fileA));
        Assert.Same(a, vm.Session);
        Assert.True(Row(vm, fileA)!.IsCurrent);
        vm.AbortCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "A stopped");
        // Its file is there now; the row is the same one, now read from it
        var row = Row(vm, fileA);
        await Until(() => File.Exists(fileA), "A saved");
        vm.RequestCatalogRefresh();
        await Until(() => vm.Sessions.Count(i => i.Model.Path == fileA) == 1 && Row(vm, fileA) == row, "A's row kept");
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>A session omp refuses to open: its header has no id (omp: "the session header is missing or malformed").</summary>
    private static string WriteDamagedSession(string root, string cwd)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "-damaged")).FullName;
        var file = Path.Combine(dir, "2026-01-01T00-00-00-000Z_damaged.jsonl");
        File.WriteAllText(file, "{\"type\":\"title\",\"v\":1,\"title\":\"Damaged chat\"}\n"
            + System.Text.Json.JsonSerializer.Serialize(new { type = "session", version = 3, cwd }) + "\n");
        return file;
    }

    [AvaloniaFact]
    public async Task A_session_omp_refuses_to_switch_to_leaves_the_new_chat_current_with_the_notice_above_the_greeting()
    {
        // Real-app QA D5: the failed session stayed highlighted in the sidebar, and the notice ran under the greeting's logo.
        var (w, vm, _, root, cwd) = await OpenAsync();
        var damaged = WriteDamagedSession(root, cwd);
        var shown = vm.Session.SessionFile!;
        vm.RequestCatalogRefresh();
        await Until(() => Row(vm, damaged) is not null && Row(vm, shown) is not null, "both listed");
        await vm.OpenSessionCommand.ExecuteAsync(Row(vm, damaged));
        await Until(() => vm.Rows.OfType<NoticeRowViewModel>().Any(n => n.IsError && n.Text.Contains("Changing sessions failed")), "the failure said");
        Assert.Equal(shown, vm.Session.SessionFile);
        Assert.False(Row(vm, damaged)!.IsCurrent);
        Assert.True(Row(vm, shown)!.IsCurrent);
        Assert.True(vm.ShowGreeting);
        Frame(w);
        // The notice is in the greeting's own flow, above the logo, and the conversation list (which drew it under the
        // logo) is not drawn with it
        var list = w.FindControl<ListBox>("Transcript")!;
        Assert.Equal(0, list.Opacity);
        Assert.False(list.IsHitTestVisible);
        var notice = w.FindControl<ItemsControl>("GreetingNotices")!;
        var logo = w.FindControl<Image>("GreetingLogo")!;
        var greeting = w.FindControl<WrapPanel>("Greeting")!;
        Assert.True(notice.IsEffectivelyVisible);
        var noticeBottom = notice.TranslatePoint(new Point(0, notice.Bounds.Height), w)!.Value.Y;
        var below = logo.IsVisible ? logo : (Control)greeting;
        Assert.True(noticeBottom <= below.TranslatePoint(default, w)!.Value.Y, "the notice ends above the logo and the question");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_greeting_lists_no_rows_of_a_conversation_shown_or_switched_to()
    {
        // Final review R5 #2: the greeting's notice list was bound to every row, so it built a control for each row of a
        // long conversation while hidden, again on every chat switch.
        var root = TestProcesses.TempDir("multi-sessions");
        var cwd = SessionCatalog.ResolveLinks(TestProcesses.TempDir("multi-project"));
        var file = ChatScrollTests.WriteSession(root, cwd, "Long chat", ChatScrollTests.LongConversation(12));
        var (w, vm, _, _, _) = await OpenAsync(file, root, cwd);
        await Until(() => vm.Rows.Count >= 60, "the long chat loaded");
        var notices = w.FindControl<ItemsControl>("GreetingNotices")!;
        int Built() => notices.GetRealizedContainers().Count();
        Frame(w);
        Assert.Empty(vm.GreetingNotices);
        Assert.Equal(0, Built());
        // To a new chat and back: nothing built for the long chat's rows either way
        var a = vm.Session;
        await NewChatAsync(vm);
        Frame(w);
        Assert.Equal(0, Built());
        vm.ComposerText = "keep this chat";
        vm.OpenSessionCommand.Execute(Row(vm, file));
        await Until(() => vm.Session == a && vm.Rows.Count >= 60, "the long chat shown again");
        Frame(w);
        Assert.Empty(vm.GreetingNotices);
        Assert.Equal(0, Built());
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_session_omp_cannot_start_on_goes_back_to_the_chat_left_and_says_why()
    {
        // Real-app QA D5: opened beside a used chat, a session omp refused left a dead chat "omp didn't start" (blaming the
        // omp command) current in the sidebar.
        var (w, vm, _, root, cwd) = await OpenAsync();
        await RunAsync(vm, "first in A");
        Assert.Equal(1, w.FindControl<ListBox>("Transcript")!.Opacity); // drawn again with the conversation
        var a = vm.Session;
        var fileA = a.SessionFile!;
        var damaged = WriteDamagedSession(root, cwd);
        vm.RequestCatalogRefresh();
        await Until(() => Row(vm, damaged) is not null && Row(vm, fileA) is not null, "both listed");
        await vm.OpenSessionCommand.ExecuteAsync(Row(vm, damaged));
        Assert.Same(a, vm.Session);
        await Until(() => vm.OpenChats.Count == 1, "the failed chat closed");
        Assert.Equal([a], vm.OpenChats);
        Assert.Equal(SessionCardState.Failed, vm.SessionCard?.State);
        Assert.True(Row(vm, fileA)!.IsCurrent);
        Assert.False(Row(vm, damaged)!.IsCurrent);
        Assert.Equal(SessionStatus.None, Row(vm, damaged)!.Status);
        Assert.Equal(SessionPhase.Ready, vm.Phase);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Ctrl_Tab_walks_the_list_while_the_chat_it_opened_is_still_starting()
    {
        // Review R4 #2: a chat still starting had no current row, so the next Ctrl+Tab went back to the first row.
        var (w, vm, _, _, _) = await OpenAsync();
        await RunAsync(vm, "first in A");
        await NewChatAsync(vm);
        await RunAsync(vm, "first in B");
        await NewChatAsync(vm);
        await RunAsync(vm, "first in C");
        await Until(() => vm.Sessions.Count == 3, "three listed");
        var order = vm.SessionGroups.SelectMany(g => g.Items).Select(i => i.Model.Path).ToList();
        var at = order.IndexOf(vm.Session.SessionFile!);
        // Two steps at once: the second is taken while the first one's omp is still starting
        Assert.True(vm.CycleSession(1));
        Assert.True(vm.CycleSession(1));
        var expected = order[(at + 2) % order.Count];
        await Until(() => (vm.Session.SessionFile ?? vm.Session.CurrentLaunch.ResumeSessionFile) == expected, "two rows on");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Status_chips_back_forward_and_next_waiting_move_between_chats()
    {
        var (w, vm, _, _, _) = await OpenAsync();
        await RunAsync(vm, "first in A");
        var a = vm.Session;
        await NewChatAsync(vm);
        await RunAsync(vm, "first in B");
        var b = vm.Session;
        await NewChatAsync(vm);
        await RunAsync(vm, "first in C");
        var c = vm.Session;
        await Until(() => vm.Sessions.Count == 3, "three listed");
        string FileOf(SessionController s) => s.SessionFile!;

        // ⌘[ goes back through the chats shown, ⌘] forward again
        Assert.True(vm.NavigateHistory(back: true));
        await Until(() => vm.Session == b, "back to B");
        Assert.True(vm.NavigateHistory(back: true));
        await Until(() => vm.Session == a, "back to A");
        Assert.True(vm.NavigateHistory(back: false));
        await Until(() => vm.Session == b, "forward to B");
        Assert.True(vm.NavigateHistory(back: false));
        await Until(() => vm.Session == c, "forward to C");
        Assert.False(vm.NavigateHistory(back: false));

        // ⌘1–9: a row of the sidebar by its place (⌘2 when the first row is the chat shown)
        var listed = vm.SessionGroups.SelectMany(g => g.Items).Select(i => i.Model.Path).ToList();
        var target = listed[0] == FileOf(c) ? 2 : 1;
        Assert.True(vm.OpenNthSession(target));
        await Until(() => vm.Session.SessionFile == listed[target - 1] && vm.Phase == SessionPhase.Ready, "row " + target);
        Assert.False(vm.OpenNthSession(9)); // only three

        // A asks for an approval in the background: the Needs input chip keeps only it; ⌥⌘A opens it
        var asker = a;
        if (vm.Session != a) await vm.OpenSessionCommand.ExecuteAsync(Row(vm, FileOf(a)));
        await Until(() => vm.Session == a && vm.Phase == SessionPhase.Ready, "A shown");
        await SendAsync(vm, "APPROVE in A");
        await Until(() => vm.Session.Snapshot().Dialogs.Count > 0, "approval asked");
        await vm.OpenSessionCommand.ExecuteAsync(Row(vm, FileOf(c)));
        await Until(() => vm.Session == c && Row(vm, FileOf(a))!.Status == SessionStatus.Waiting, "A waits");

        vm.IsSessionSearchOpen = true;
        Frame(w);
        Assert.True(w.FindControl<WrapPanel>("StatusFilterChips")!.IsEffectivelyVisible);
        vm.SetStatusFilterCommand.Execute(SessionStatusFilter.NeedsInput);
        Assert.Equal([FileOf(asker)], vm.Sessions.Select(s => s.Model.Path));
        vm.SetStatusFilterCommand.Execute(SessionStatusFilter.Errors);
        Assert.Empty(vm.Sessions);
        Assert.True(vm.ShowNoSessionMatch);
        vm.SetStatusFilterCommand.Execute(SessionStatusFilter.Errors); // again: all
        Assert.Equal(SessionStatusFilter.All, vm.StatusFilter);
        Assert.Equal(3, vm.Sessions.Count);

        Assert.True(vm.OpenNextWaiting());
        await Until(() => vm.Session == asker, "the waiting chat");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_chat_that_becomes_idle_keeps_its_logical_session()
    {
        var (w, vm, _, _, _) = await OpenAsync();
        await RunAsync(vm, "first in A");
        var a = vm.Session;
        await SendAsync(vm, "LONG in A");
        var b = await NewChatAsync(vm);
        await RunAsync(vm, "first in B");
        await Until(() => a.Snapshot().Phase == SessionPhase.Ready, "A finished", 30);
        Assert.Equal([a, b], vm.OpenChats);
        Assert.Equal(a.ProcessId, b.ProcessId);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Three_new_chats_switched_while_their_first_replies_stream_keep_their_rows()
    {
        // Real-app QA NEW-2: a new chat left while its first reply streamed read "New session" in the sidebar until the
        // reply ended (its row's title was read before its first message, and nothing read it again); a new chat left
        // untouched stayed listed after it closed (a click on it opened an empty chat on a file that never existed); and
        // the shown chat got the "new reply" dot for another chat's reply that came while the window was in the background.
        var (w, vm, _, _, _) = await OpenAsync();
        var chats = new List<(SessionController C, string File, string Text)>();
        foreach (var name in new[] { "A", "B", "C" })
        {
            if (chats.Count > 0) await NewChatAsync(vm);
            var text = "FOREVER first in " + name;
            await SendAsync(vm, text);
            chats.Add((vm.Session, vm.Session.SessionFile!, text));
        }
        Assert.All(chats, c => Assert.False(File.Exists(c.File), "like omp, no file while the first reply streams"));

        // Every switch: each chat's row under its first message, the shown one current, the others working
        for (var round = 0; round < 2; round++)
            foreach (var (c, file, _) in chats)
            {
                await vm.OpenSessionCommand.ExecuteAsync(Row(vm, file));
                Assert.Same(c, vm.Session);
                Assert.Equal(3, vm.Sessions.Count);
                foreach (var other in chats)
                {
                    var row = Row(vm, other.File);
                    Assert.NotNull(row);
                    Assert.Equal(other.Text, row.Title);
                    Assert.Equal(other.C == c, row.IsCurrent);
                    Assert.Equal(SessionStatus.Running, row.Status);
                }
            }
        Assert.All(chats, c => Assert.Equal(SessionPhase.Running, c.C.Snapshot().Phase));

        // A new chat from a working one, left untouched: it closes, and its row goes with it
        var untouched = await NewChatAsync(vm);
        var untouchedFile = untouched.SessionFile!;
        await Until(() => Row(vm, untouchedFile) is { IsCurrent: true } row && row.Title == Loc.T("New chat"), "the new chat listed");
        await vm.OpenSessionCommand.ExecuteAsync(Row(vm, chats[0].File));
        Assert.DoesNotContain(untouched, vm.OpenChats);
        Assert.Null(Row(vm, untouchedFile));
        Assert.Equal(3, vm.Sessions.Count);

        // A stops (window in front: nothing to tell). Then, with the window in the background, B's run ends: B gets the
        // dot, the shown A does not (the window's title does)
        vm.AbortCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && Row(vm, chats[0].File)?.Status == SessionStatus.None, "A stopped");
        vm.SetWindowActive(false);
        await chats[1].C.AbortAsync();
        await Until(() => Row(vm, chats[1].File)?.Status == SessionStatus.Unread, "B's ended run marked");
        Assert.True(vm.NeedsAttention);
        Assert.Equal(SessionStatus.None, Row(vm, chats[0].File)!.Status);
        Assert.Equal("FOREVER first in B", Row(vm, chats[1].File)!.Title);
        // A reply in the shown chat while the window is in the background marks that chat until the window is back
        await RunAsync(vm, "LONG second in A");
        await Until(() => Row(vm, chats[0].File)?.Status == SessionStatus.Unread, "A's reply marked while the window was away");
        vm.SetWindowActive(true);
        Assert.Equal(SessionStatus.None, Row(vm, chats[0].File)!.Status);
        Assert.Equal(SessionStatus.Unread, Row(vm, chats[1].File)!.Status); // B's reply is still unseen

        // Quitting counts the one chat still working
        Assert.Equal(Loc.F("omp is still working in “{0}”", "FOREVER first in C"), vm.QuitWarning(0)!.Title);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Switching_chats_while_one_streams_opens_each_at_its_latest_message()
    {
        // A chat opened while another streams lands on its last row; the streaming chat, left while it
        // followed its reply, is back on the end of the (longer) reply, not where it was when it was left.
        var root = TestProcesses.TempDir("multi-sessions");
        var cwd = SessionCatalog.ResolveLinks(TestProcesses.TempDir("multi-project"));
        var fileA = ChatScrollTests.WriteSession(root, cwd, "Long A", ChatScrollTests.LongConversation(12));
        var fileB = ChatScrollTests.WriteSession(root, cwd, "Long B", ChatScrollTests.LongConversation(12));
        var (w, vm, _, _, _) = await OpenAsync(fileA, root, cwd);
        await Until(() => vm.Rows.Count >= 60, "A loaded");
        var a = vm.Session;
        var sv = Scroll(w);
        bool AtEnd() { Frame(w); Frame(w); return sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 2; }

        await SendAsync(vm, "FOREVER in A");
        await Until(() => vm.Rows.LastOrDefault() is AssistantRowViewModel { HasText: true }, "A streaming");
        Assert.True(AtEnd(), "A follows its reply");

        await Until(() => Row(vm, fileB) is not null, "B listed");
        await vm.OpenSessionCommand.ExecuteAsync(Row(vm, fileB));
        await Until(() => vm.Session != a && vm.Rows.Count >= 60, "B shown");
        Assert.True(AtEnd(), "B opens on its last row");
        Assert.False(w.FindControl<Button>("JumpToLatest")!.IsVisible);

        var left = ReplyText(a.Snapshot()).Length;
        await Until(() => ReplyText(a.Snapshot()).Length > left + 200, "A streamed on in the background");
        Assert.True(AtEnd(), "B did not move while A streamed");

        await vm.OpenSessionCommand.ExecuteAsync(Row(vm, fileA));
        await Until(() => vm.Session == a, "A shown again");
        var streamed = ReplyText(a.Snapshot()).Length;
        await Until(() => vm.Rows.OfType<AssistantRowViewModel>().Last().Text.Length >= streamed, "A's whole reply");
        Assert.True(AtEnd(), "A is back on the end of its reply");
        Assert.False(w.FindControl<Button>("JumpToLatest")!.IsVisible);

        vm.AbortCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "A stopped");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task New_chat_uses_shared_engine_and_current_permission_mode()
    {
        var (w, vm, _, _, _) = await OpenAsync();
        await RunAsync(vm, "first in the project");
        var project = vm.Session;
        var pid = project.ProcessId;
        await vm.SetApprovalModeCommand.ExecuteAsync("always-ask");
        await Until(() => vm.Phase == SessionPhase.Ready && project.CurrentLaunch.ApprovalMode == "always-ask", "permissions switched");
        var chat = await NewChatAsync(vm);
        Assert.Equal(pid, chat.ProcessId);
        Assert.Equal("always-ask", chat.CurrentLaunch.ApprovalMode);
        Assert.True(vm.IsProjectless);
        Assert.Equal([project, chat], vm.OpenChats);
        await RunAsync(vm, "hello in a chat");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_chat_left_untouched_takes_its_empty_folder_with_it()
    {
        var (w, vm, _, _, _) = await OpenAsync();
        await RunAsync(vm, "first in the project");
        var chat = await NewChatAsync(vm);
        var folder = chat.CurrentLaunch.WorkingDirectory!;
        Assert.True(Directory.Exists(folder));
        await vm.OpenSessionCommand.ExecuteAsync(vm.Sessions.Single(i => i.Title == "first in the project"));
        await Until(() => !vm.OpenChats.Contains(chat), "the untouched chat closed");
        await Until(() => !Directory.Exists(folder), "its folder removed");

        // A chat something was said in keeps its folder
        var kept = await NewChatAsync(vm);
        await RunAsync(vm, "hello in a chat");
        await vm.OpenSessionCommand.ExecuteAsync(vm.Sessions.Single(i => i.Title == "first in the project"));
        await Task.Delay(500);
        Assert.True(Directory.Exists(kept.CurrentLaunch.WorkingDirectory));
        await vm.DisposeAsync();
        w.Close();
    }

    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
}
