using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.Services;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// Nothing the user did not ask for is thrown away: closing or quitting asks while omp works, Esc in a search box does
/// not stop the run, a message omp never got goes back into the box, unsaved Advanced settings survive leaving the page
/// (QA audit 1, 2, 9, 10, 11, 13).
/// </summary>
public sealed class WindowSafetyTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task<(MainWindow w, MainViewModel vm, SessionController s)> OpenAsync(string scenario, double width = 1100, double height = 760, ClientSettingsStore? store = null)
    {
        var s = new SessionController(TestProcesses.Fake(scenario));
        var vm = new MainViewModel(s, new AppArgs(), settings: store);
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase is SessionPhase.Ready or SessionPhase.Faulted, "ready");
        return (w, vm, s);
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

    private static bool Alive(int pid)
    {
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static async Task StartRunAsync(MainWindow w, MainViewModel vm)
    {
        w.KeyTextInput("write a long answer");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.IsRunning && vm.Rows.OfType<AssistantRowViewModel>().Any(), "streaming");
    }

    [AvaloniaFact]
    public async Task Closing_while_omp_works_asks_first_and_only_quit_stops_it()
    {
        var (w, vm, s) = await OpenAsync("slow-stream");
        await StartRunAsync(w, vm);
        var pid = s.ProcessId!.Value;

        // The close button, ⌘W, ⌘Q and the tray's Quit all arrive as a close request
        w.Close();
        Dispatcher.UIThread.RunJobs();
        var card = vm.SessionCard;
        Assert.NotNull(card);
        Assert.Equal("quit", card!.Kind);
        Assert.Equal("omp is still working", card.Title);
        Assert.True(w.IsVisible);
        Assert.False(vm.IsClosing);
        Assert.True(Alive(pid));
        Assert.True(vm.IsRunning);
        // The keyboard is on "Keep working": an Enter right after ⌘Q must not stop the run
        await Until(() => w.FocusManager?.GetFocusedElement() is Button { Name: "CardSecondary" }, "focus on Keep working", 5);
        Shot(w, "quit-confirmation");

        // Esc answers like Keep working; the run goes on
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.SessionCard);
        Assert.True(vm.IsRunning);
        Assert.True(Alive(pid));

        // A second close asks again; Quit stops omp and closes the window
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("quit", vm.SessionCard?.Kind);
        vm.SessionCard!.Primary!.Command.Execute(null);
        await Until(() => !w.IsVisible, "window closed", 15);
        await Until(() => !Alive(pid), "omp stopped", 15);
    }

    [AvaloniaFact]
    public async Task Keep_working_puts_back_the_card_the_question_covered()
    {
        var (w, vm, _) = await OpenAsync("slow-stream");
        await StartRunAsync(w, vm);
        var earlier = new SessionCardViewModel("compact", "IconCompact", "Compact the conversation", "omp summarizes…") { State = SessionCardState.Ask };
        vm.ShowCard(earlier);
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("quit", vm.SessionCard?.Kind);
        vm.SessionCard!.Secondary!.Command.Execute(null);
        Assert.Same(earlier, vm.SessionCard);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Closing_an_idle_window_asks_nothing()
    {
        var (w, vm, s) = await OpenAsync("normal");
        var pid = s.ProcessId!.Value;
        w.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.SessionCard);
        await Until(() => !w.IsVisible, "window closed", 15);
        await Until(() => !Alive(pid), "omp stopped", 15);
    }

    [AvaloniaFact]
    public async Task The_window_menu_close_goes_through_the_question()
    {
        var (w, vm, _) = await OpenAsync("slow-stream");
        var bar = w.MenuBar(vm);
        Assert.Equal(["File", "Edit", "View", "Window"], bar.Items.OfType<NativeMenuItem>().Select(i => i.Header));
        var items = bar.Items.OfType<NativeMenuItem>().SelectMany(m => m.Menu!.Items.OfType<NativeMenuItem>())
            .Where(i => i is not NativeMenuItemSeparator).ToDictionary(i => i.Header!);
        var cmd = w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;
        Assert.Equal(new KeyGesture(Key.N, cmd), items["New Session"].Gesture);
        Assert.Equal(new KeyGesture(Key.O, cmd), items["Open Folder…"].Gesture);
        Assert.Equal(new KeyGesture(Key.M, cmd), items["Minimize"].Gesture);
        Assert.Equal(new KeyGesture(Key.W, cmd), items["Close"].Gesture);
        Assert.Equal(new KeyGesture(Key.Z, cmd | KeyModifiers.Shift), items["Redo"].Gesture);

        // Edit acts on the text that has the keyboard
        var composer = w.FindControl<TextBox>("Composer")!;
        composer.Focus();
        w.KeyTextInput("hello world");
        items["Select All"].Command!.Execute(null);
        Assert.Equal("hello world", composer.SelectedText);
        items["Undo"].Command!.Execute(null);
        Assert.NotEqual("hello world", composer.Text ?? "");

        await StartRunAsync(w, vm);
        items["Close"].Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("quit", vm.SessionCard?.Kind);
        Assert.True(w.IsVisible);
        items["Minimize"].Command!.Execute(null);
        Assert.Equal(WindowState.Minimized, w.WindowState);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Esc_in_the_sidebar_search_clears_it_then_leaves_it_and_does_not_stop_the_run()
    {
        var (w, vm, _) = await OpenAsync("slow-stream", 1300);
        await StartRunAsync(w, vm);
        var search = w.FindControl<TextBox>("SessionFilterBox")!;
        Assert.True(search.IsEffectivelyVisible);
        search.Focus();
        w.KeyTextInput("abc");
        Assert.Equal("abc", vm.SessionFilter);
        // Real-app QA D6, as a Mac search field: the first Esc clears the text (the whole list again) and stays there
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("", vm.SessionFilter);
        Assert.Equal("", search.Text);
        Assert.True(search.IsFocused, "the first Esc only clears the search");
        Assert.Equal(SessionPhase.Running, vm.Phase);
        // The second leaves it, for the message box
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsRunning);
        Assert.Equal(SessionPhase.Running, vm.Phase);
        var composer = w.FindControl<TextBox>("Composer")!;
        Assert.True(composer.IsFocused, "the keyboard goes back to the message box");

        // From the message box Esc still stops the run
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Until(() => vm.Phase == SessionPhase.Ready, "aborted");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Esc_in_the_terminal_stays_with_the_shell()
    {
        var (w, vm, _) = await OpenAsync("slow-stream", 1400);
        await StartRunAsync(w, vm);
        vm.ToggleTerminalCommand.Execute(null);
        var tab = Assert.Single(vm.Terminals);
        var term = w.TerminalOf(tab)!;
        await Until(() => term.IsLive || tab.HasExited, "shell started");
        term.Focus();
        Dispatcher.UIThread.RunJobs();
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SessionPhase.Running, vm.Phase);
        var focused = w.FocusManager?.GetFocusedElement() as Visual;
        Assert.True(focused == term || focused?.GetVisualAncestors().Contains(term) == true, $"focus is on {focused}");
        await vm.DisposeAsync();
        w.Close();
        await Until(() => !w.IsVisible, "window closed", 15);
    }

    [AvaloniaFact]
    public async Task A_message_omp_never_got_goes_back_into_the_box()
    {
        // omp between the stop and the start of a restart: no connection. The session is never started here.
        var s = new SessionController(TestProcesses.Fake("normal"));
        var vm = new MainViewModel(s, new AppArgs());
        vm.Phase = SessionPhase.Ready;
        vm.ComposerText = "please keep me";
        Assert.True(vm.TryAddImage(new ImageAttachment("dot.png", "image/png", [137, 80, 78, 71]), "dot.png"));
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("please keep me", vm.ComposerText);
        Assert.Single(vm.Attachments);
        Assert.Contains("wasn't sent", vm.ComposerMessage);
        Assert.NotEqual(SessionPhase.Running, vm.Phase);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Unsaved_advanced_settings_survive_leaving_the_page_and_can_be_discarded()
    {
        var store = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("ui-advanced"), "omp-gui.local.json"));
        var (w, vm, _) = await OpenAsync("normal", store: store);
        await vm.OpenSettingsAtAsync("advanced");
        Assert.False(vm.HasUnsavedRuntime);
        vm.SettingsProfile = "work";
        Assert.True(vm.HasUnsavedRuntime);
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<TextBlock>("UnsavedRuntimeText")!.IsEffectivelyVisible);
        Assert.Contains("accent", w.FindControl<Button>("SaveRuntimeButton")!.Classes);
        Shot(w, "settings-advanced-unsaved");

        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); // Back to app
        Assert.False(vm.IsSettingsOpen);
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        Assert.Equal("work", vm.SettingsProfile);
        Assert.True(vm.HasUnsavedRuntime);

        vm.DiscardRuntimeEditsCommand.Execute(null);
        Assert.Equal("", vm.SettingsProfile);
        Assert.False(vm.HasUnsavedRuntime);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task The_events_panel_docks_when_there_is_room_and_covers_the_conversation_when_not()
    {
        var (w, vm, _) = await OpenAsync("normal", 1600);
        var panel = w.FindControl<Border>("DebugPanel")!;
        var conversation = w.FindControl<Border>("ComposerBox")!;
        vm.ToggleDebugCommand.Execute(null);
        await Until(() => panel.Bounds.Width > 0, "events laid out");
        Assert.True(Grid.GetColumn(panel) > 0, "docked beside the conversation");
        Assert.InRange(panel.Bounds.Width, 320, 421);

        // At the window's minimum width the conversation keeps its room: the panel covers it instead of squeezing it
        w.Width = 480;
        await Until(() => Grid.GetColumn(panel) == 0, "events over the conversation");
        Assert.True(panel.ZIndex > 0);
        Assert.True(conversation.Bounds.Width > 300, $"composer squeezed to {conversation.Bounds.Width}");
        Shot(w, "events-overlay-narrow");

        w.Width = 1600;
        await Until(() => Grid.GetColumn(panel) > 0, "docked again");
        // Its × closes it (over a narrow conversation the visible way back besides F12)
        var close = w.FindControl<Button>("CloseDebugButton")!;
        Assert.True(close.IsEffectivelyVisible);
        close.Command!.Execute(null);
        Assert.False(vm.IsDebugVisible);
        await vm.DisposeAsync();
        w.Close();
    }

    [Fact]
    public void Client_errors_are_kept_in_two_rotating_files()
    {
        var dir = TestProcesses.TempDir("client-log");
        var log = new ClientLog(Path.Combine(dir, "client.log"));
        Assert.Null(log.ReadTail(1000));
        var big = new string('x', 300_000);
        for (var i = 0; i < 8; i++) log.Write("ui", new InvalidOperationException($"failure {i} {big}"));
        Assert.True(new FileInfo(log.Path).Length <= ClientLog.MaxBytes);
        Assert.True(File.Exists(log.PreviousPath));
        Assert.True(new FileInfo(log.PreviousPath).Length <= ClientLog.MaxBytes);
        Assert.Equal(["client.1.log", "client.log"], Directory.GetFiles(dir).Select(Path.GetFileName).Order());
        var tail = log.ReadTail(int.MaxValue)!;
        Assert.Contains("failure 7", tail);
        Assert.Contains("[ui] System.InvalidOperationException", tail);
        Assert.DoesNotContain("failure 0", tail); // the oldest went with the rotation
    }

    [Fact]
    public void With_no_active_display_the_app_waits_for_one_with_backoff_and_logs_it_instead_of_crashing()
    {
        // Real-app QA D7: a start with the screen locked and asleep (a login item, an update's relaunch) threw Avalonia's
        // "not able to start the RenderTimer, -6661" from Main. The start waits for a display instead, and says so.
        var dir = TestProcesses.TempDir("display-wait");
        var log = new ClientLog(Path.Combine(dir, "client.log"));
        var checks = 0;
        var sleeps = new List<TimeSpan>();
        Assert.True(OmpGui.App.Platform.DisplayWait.UntilActive(() => ++checks > 8, sleeps.Add, m => log.Write("display", m)));
        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], sleeps.Select(s => s.TotalSeconds));
        var tail = log.ReadTail(int.MaxValue)!;
        Assert.Contains("[display] No active display", tail);
        Assert.Contains("[display] A display is active after 121 s", tail);

        // A display there already: no wait, nothing logged
        var quiet = new ClientLog(Path.Combine(dir, "quiet.log"));
        Assert.True(OmpGui.App.Platform.DisplayWait.UntilActive(() => true, _ => throw new InvalidOperationException("slept"), m => quiet.Write("display", m)));
        Assert.Null(quiet.ReadTail(1000));

        // A run nobody watches (package smoke, benchmark) gives up at its limit instead of hanging its script
        Assert.True(new AppArgs { SmokeExitAfterMs = 5000 }.IsUnattended);
        Assert.True(AppArgs.Parse(["--bench-latency", "50", "--exit-after-bench"]).IsUnattended);
        Assert.False(AppArgs.Parse(["--config", "x.json"]).IsUnattended);
        var bounded = new List<TimeSpan>();
        var gaveUp = new ClientLog(Path.Combine(dir, "bounded.log"));
        Assert.False(OmpGui.App.Platform.DisplayWait.UntilActive(() => false, bounded.Add, m => gaveUp.Write("display", m), TimeSpan.FromSeconds(20)));
        Assert.Equal([1, 2, 4, 8, 5], bounded.Select(s => s.TotalSeconds));
        Assert.Contains("Still no active display after 20 s", gaveUp.ReadTail(int.MaxValue));

        // Avalonia.Native's own failure is recognized (Main starts over in a new process once a display is back)
        Assert.True(OmpGui.App.Platform.DisplayWait.IsRenderTimerFailure(
            new InvalidOperationException("Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661")));
        Assert.False(OmpGui.App.Platform.DisplayWait.IsRenderTimerFailure(new InvalidOperationException("Something else")));
        if (OperatingSystem.IsMacOS()) _ = OmpGui.App.Platform.DisplayWait.MacHasActiveDisplay(); // the CoreVideo calls bind
    }

    [AvaloniaFact]
    public async Task A_ui_error_is_logged_said_once_and_in_the_diagnostics_redacted()
    {
        var dir = TestProcesses.TempDir("client-log-ui");
        var log = new ClientLog(Path.Combine(dir, "client.log"));
        var s = new SessionController(TestProcesses.Fake("normal"));
        var vm = new MainViewModel(s, new AppArgs()) { ClientLog = log };
        vm.ReportClientError(new InvalidOperationException("token sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789 in a handler"));
        Assert.StartsWith("Something went wrong in OMP GUI", vm.ComposerMessage);
        vm.ComposerMessage = "";
        vm.ReportClientError(new InvalidOperationException("second"));
        Assert.Equal("", vm.ComposerMessage); // said once

        var copy = new MemoryStream();
        await new DiagnosticsBundle { SettingsPath = Path.Combine(dir, "none.json"), ClientLog = log, Home = dir }.WriteAsync(copy);
        copy.Position = 0;
        using var zip = new System.IO.Compression.ZipArchive(copy);
        Assert.Contains("client.log", DiagnosticsBundle.Entries);
        using var reader = new StreamReader(zip.GetEntry("client.log")!.Open());
        var text = await reader.ReadToEndAsync();
        Assert.Contains("[ui] System.InvalidOperationException", text);
        Assert.Contains("second", text);
        Assert.DoesNotContain("sk-ant-api03", text);
        Assert.Contains(SecretRedactor.Mask, text);
        await vm.DisposeAsync();
    }
}
