using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

[assembly: AvaloniaTestApplication(typeof(OmpGui.Tests.HeadlessApp))]

namespace OmpGui.Tests;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<OmpGui.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>The real window and ViewModel over the scripted fake omp, driven with keyboard input.</summary>
public sealed class UiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task<(MainWindow w, MainViewModel vm, SessionController s)> OpenAsync(string scenario, double width = 1000, double height = 700)
    {
        var s = new SessionController(TestProcesses.Fake(scenario));
        var vm = new MainViewModel(s, new AppArgs());
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

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static async Task CloseAsync(MainWindow w, MainViewModel vm)
    {
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Enter_sends_shift_enter_adds_a_line_and_send_is_disabled_while_empty_or_running()
    {
        var (w, vm, _) = await OpenAsync("slow-stream");
        var composer = w.FindControl<TextBox>("Composer")!;
        Assert.True(composer.IsFocused);
        Assert.False(vm.SendCommand.CanExecute(null));

        w.KeyTextInput("first line");
        w.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
        w.KeyTextInput("second line");
        Assert.Contains('\n', vm.ComposerText);
        Assert.True(vm.SendCommand.CanExecute(null));
        Shot(w, "ui-composer-multiline");

        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.IsRunning && vm.Rows.OfType<AssistantRowViewModel>().Any(a => a.Text.Length > 40), "streaming");
        Assert.Equal("", vm.ComposerText);
        Assert.False(vm.SendCommand.CanExecute(null));
        Assert.True(vm.AbortCommand.CanExecute(null));
        Assert.True(w.FindControl<Button>("StopButton")!.IsVisible);
        Assert.False(w.FindControl<Button>("SendButton")!.IsVisible);
        Shot(w, "ui-streaming");

        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Until(() => vm.Phase == SessionPhase.Ready, "aborted");
        Assert.Equal("Interrupted", vm.Rows.OfType<AssistantRowViewModel>().Last().Footer);
        Shot(w, "ui-after-abort");
        await CloseAsync(w, vm);
    }

    [AvaloniaFact]
    public async Task Start_failure_is_shown_and_sending_stays_disabled()
    {
        var (w, vm, _) = await OpenAsync("crash-before-ready");
        Assert.Equal(SessionPhase.Faulted, vm.Phase);
        Assert.True(vm.HasError);
        Assert.StartsWith("omp stopped:", vm.StatusText);
        Assert.True(vm.CanRecover);
        Assert.Equal("omp didn't start", vm.RecoverTitle);
        // Said once (design review G1): the setup screen, with the error under Show details; not in the conversation,
        // not in the activity line; no message box on the setup screen
        Assert.True(vm.ShowSetupScreen);
        Assert.DoesNotContain(vm.Rows, r => r is NoticeRowViewModel { IsError: true });
        Assert.False(w.FindControl<Grid>("ActivityLine")!.IsEffectivelyVisible);
        Assert.False(w.FindControl<Border>("ComposerBox")!.IsEffectivelyVisible);
        Assert.True(vm.HasErrorDetail);
        Assert.False(vm.SendCommand.CanExecute(null));
        Shot(w, "ui-start-failure");
        await CloseAsync(w, vm);
    }

    [AvaloniaTheory]
    [InlineData(640, 480)]
    [InlineData(480, 360)]
    [InlineData(1600, 1000)]
    [InlineData(1000, 380)]
    public async Task Layout_holds_at_small_and_large_sizes(double width, double height)
    {
        var (w, vm, _) = await OpenAsync("normal", width, height);
        w.KeyTextInput("hello there");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Any(), "run end");
        var composer = w.FindControl<TextBox>("Composer")!;
        var send = w.FindControl<Button>("SendButton")!;
        Assert.True(composer.Bounds.Width > 100, $"composer squeezed to {composer.Bounds.Width}");
        Assert.True(send.Bounds.Width > 0 && send.IsVisible);
        var sendRight = send.TranslatePoint(new Point(send.Bounds.Width, 0), w)!.Value.X;
        Assert.True(sendRight <= w.Bounds.Width + 0.5, $"send button clipped at {sendRight} > {w.Bounds.Width}");
        Shot(w, $"ui-size-{width}x{height}");
        await CloseAsync(w, vm);
    }

    [AvaloniaFact]
    public async Task Long_transcript_and_debug_panel()
    {
        var (w, vm, s) = await OpenAsync("normal", 1200, 800);
        for (var i = 0; i < 12; i++)
        {
            vm.ComposerText = $"message {i}";
            vm.SendCommand.Execute(null);
            try
            {
                await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Count() == i + 1 && vm.Rows.OfType<ToolRowViewModel>().Count() == i + 1, $"run {i}");
            }
            catch (TimeoutException e)
            {
                var snap = s.Snapshot();
                throw new TimeoutException(e.Message + $"\nvm phase {vm.Phase}, core phase {snap.Phase}, rows: "
                    + string.Join(" | ", vm.Rows.Select(r => r.GetType().Name.Replace("RowViewModel", "") + ":" + (r as NoticeRowViewModel)?.Text))
                    + "\ncore events:\n" + string.Join("\n", snap.DebugTail.TakeLast(12).Select(d => $"{d.Type} {d.Summary}")), e);
            }
        }
        w.KeyPress(Key.F12, RawInputModifiers.None, PhysicalKey.F12, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsDebugVisible);
        Assert.True(vm.DebugLog.Count > 50);
        Shot(w, "ui-long-transcript-debug");
        await CloseAsync(w, vm);
    }

    [AvaloniaFact]
    public async Task Minimized_window_pauses_ui_updates_and_restore_shows_current_state()
    {
        var (w, vm, s) = await OpenAsync("slow-stream");
        vm.ComposerText = "go";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Rows.OfType<AssistantRowViewModel>().Any(a => a.Text.Length > 20), "streaming");
        w.WindowState = WindowState.Minimized;
        vm.SetWindowState("Minimized");
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        var appliesAtMinimize = vm.UiApplies;
        var framesAtMinimize = s.Snapshot().FramesReceived;
        // Core keeps folding frames while minimized (deadline-based wait: CI runners vary in speed).
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (s.Snapshot().FramesReceived < framesAtMinimize + 20 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }
        var frames = s.Snapshot().FramesReceived;
        Assert.True(frames >= framesAtMinimize + 20, $"Core must keep receiving while minimized: {framesAtMinimize} -> {frames}, phase {s.Snapshot().Phase}");
        Assert.True(vm.UiApplies <= appliesAtMinimize + 1, $"UI applied {vm.UiApplies - appliesAtMinimize} times while minimized");

        w.WindowState = WindowState.Normal;
        vm.SetWindowState("Normal");
        var coreText = s.Snapshot().Items.OfType<AssistantItem>().Last().Text.Length;
        await Until(() => vm.Rows.OfType<AssistantRowViewModel>().Last().Text.Length >= coreText, "restore catches up", 5);
        vm.AbortCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "aborted");
        await CloseAsync(w, vm);
    }

    [AvaloniaFact]
    public async Task Stale_running_phase_is_corrected_after_stop()
    {
        var (w, vm, _) = await OpenAsync("normal");
        vm.Phase = SessionPhase.Running; // the UI has not caught up with Core (Ready) yet
        vm.AbortCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready, "settled", 10);
        await CloseAsync(w, vm);
    }

    [AvaloniaFact]
    public async Task Closing_twice_still_stops_omp_gracefully()
    {
        var (w, vm, s) = await OpenAsync("normal");
        var pid = s.ProcessId!.Value;
        w.Close();
        w.Close();
        await Until(() => !w.IsVisible, "window closed", 15);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
        Assert.Equal(SessionPhase.Stopped, s.Snapshot().Phase);
    }

    [AvaloniaFact]
    public async Task Send_hotkey_uses_the_platform_command_modifier()
    {
        var (w, vm, _) = await OpenAsync("normal");
        var expected = w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;
        var hotkey = w.FindControl<Button>("SendButton")!.HotKey;
        Assert.NotNull(hotkey);
        Assert.Equal(Key.Enter, hotkey!.Key);
        Assert.Equal(expected, hotkey.KeyModifiers);
        await CloseAsync(w, vm);
    }

    [AvaloniaFact]
    public async Task Closing_while_a_prompt_request_is_pending_raises_nothing()
    {
        // Regression (CI run 10, macOS): the cancelled prompt request surfaced as an unhandled UI exception.
        var (w, vm, s) = await OpenAsync("no-prompt-ack");
        vm.ComposerText = "never acknowledged";
        vm.SendCommand.Execute(null);
        await Until(() => vm.IsRunning, "running");
        var pid = s.ProcessId!.Value;
        w.Close();
        await Until(() => !w.IsVisible, "window closed", 15);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
    }

    [AvaloniaFact]
    public async Task Closing_while_minimized_stops_omp()
    {
        var (w, vm, s) = await OpenAsync("slow-stream");
        vm.ComposerText = "go";
        vm.SendCommand.Execute(null);
        await Until(() => vm.IsRunning, "running");
        var pid = s.ProcessId!.Value;
        w.WindowState = WindowState.Minimized;
        vm.SetWindowState("Minimized");
        w.Close();
        await Until(() => !w.IsVisible, "window closed", 15);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
    }

    [AvaloniaFact]
    public async Task The_caret_stops_blinking_after_a_while_without_input_and_resumes_on_input()
    {
        // Regression (final benchmark): an idle window kept repainting the caret twice a second (2.5% of a core under
        // software rendering) because deactivation was never reported by the window system.
        var (w, vm, _) = await OpenAsync("normal");
        w.CaretIdleTimeout = TimeSpan.FromMilliseconds(200);
        var composer = w.FindControl<TextBox>("Composer")!;
        w.KeyTextInput("a");
        w.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
        Assert.NotEqual(TimeSpan.Zero, composer.CaretBlinkInterval); // typing: the caret blinks
        await Until(() => composer.CaretBlinkInterval == TimeSpan.Zero, "caret idle", 5);
        w.KeyPress(Key.B, RawInputModifiers.None, PhysicalKey.B, "b");
        Assert.NotEqual(TimeSpan.Zero, composer.CaretBlinkInterval); // back on the next key
        await CloseAsync(w, vm);
    }
}
