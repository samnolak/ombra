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

namespace OmpGui.Tests;

/// <summary>The approval / question card in the real window, over the scripted fake omp.</summary>
public sealed class DialogUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task<(MainWindow w, MainViewModel vm, SessionController s)> OpenAndPromptAsync(string scenario, double width = 1000, double height = 700)
    {
        var s = new SessionController(TestProcesses.Fake(scenario));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        w.KeyTextInput("do the thing");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.HasDialog, "dialog shown");
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

    /// <summary>The control once the view has realized it (a new dialog is templated on the next layout pass).</summary>
    private static T Find<T>(Window w, Func<T, bool>? where = null) where T : Control
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var found = w.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.IsEffectivelyVisible && (where?.Invoke(c) ?? true));
            if (found is not null) return found;
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"no visible {typeof(T).Name} matched");
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            Thread.Sleep(10);
        }
    }

    private static void Click(Button b)
    {
        Assert.True(b.IsEffectivelyVisible && b.IsEffectivelyEnabled, $"button {b.Name ?? b.Content?.ToString()} not clickable");
        b.Command!.Execute(b.CommandParameter);
    }

    [AvaloniaFact]
    public async Task Approval_card_shows_the_request_and_allow_runs_the_tool()
    {
        var (w, vm, _) = await OpenAndPromptAsync("approval");
        var d = vm.CurrentDialog!;
        Assert.True(d.IsApproval);
        Assert.Equal("Permission needed", d.Caption);
        Assert.Equal("Allow tool: bash", d.Headline);
        Assert.Equal("$ echo hi", d.Details);
        Assert.Equal("Answer the request above, or press Esc to stop", vm.ComposerHint);
        // The activity line says what the run waits on, not "Working" (design review)
        Assert.StartsWith("Waiting for your approval", vm.StatusText);
        Assert.True(vm.IsWaitingOnUser);
        var allow = Find<Button>(w, b => b.Name == "AllowButton");
        var deny = Find<Button>(w, b => b.Name == "DenyButton");
        Assert.False(allow.IsFocused || deny.IsFocused, "the card must not take focus (a stray Enter would answer it)");
        Shot(w, "ui-approval");

        Click(allow);
        await Until(() => vm.Phase == SessionPhase.Ready && !vm.HasDialog, "run end");
        Assert.Equal("done", vm.Rows.OfType<ToolRowViewModel>().Single().StatusText);
        Assert.Contains(vm.Rows.OfType<NoticeRowViewModel>(), n => n.Text == "Approved by you — bash");
        Shot(w, "ui-approval-allowed");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Deny_with_the_access_key_blocks_the_tool()
    {
        var (w, vm, _) = await OpenAndPromptAsync("approval");
        w.KeyPress(Key.D, RawInputModifiers.Alt, PhysicalKey.D, "d");
        Dispatcher.UIThread.RunJobs();
        w.KeyRelease(Key.D, RawInputModifiers.Alt, PhysicalKey.D, "d");
        await Until(() => vm.Phase == SessionPhase.Ready && !vm.HasDialog, "run end");
        Assert.True(vm.Rows.OfType<ToolRowViewModel>().Single().IsFailed);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Esc_in_the_composer_stops_the_run_and_closes_the_approval()
    {
        var (w, vm, _) = await OpenAndPromptAsync("approval");
        Assert.True(w.FindControl<TextBox>("Composer")!.IsFocused);
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Until(() => vm.Phase == SessionPhase.Ready && !vm.HasDialog, "stopped");
        Assert.Contains(vm.Rows.OfType<NoticeRowViewModel>(), n => n.Text.StartsWith("Approval request closed (the run was stopped)", StringComparison.Ordinal));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Ask_tool_select_then_input_with_keyboard()
    {
        var (w, vm, _) = await OpenAndPromptAsync("ask");
        var d = vm.CurrentDialog!;
        Assert.True(d.IsSelect);
        Assert.Equal(["Red (Recommended)", "Blue"], d.Options.Select(o => o.Label));
        Assert.True(d.Options[0].Recommended);
        Assert.Equal("warm", d.Options[0].Description);
        Shot(w, "ui-ask-select");
        Click(Find<Button>(w, b => b.DataContext is DialogOptionViewModel { Label: "Blue" }));

        await Until(() => vm.CurrentDialog is { IsInput: true }, "input dialog");
        var input = Find<TextBox>(w, t => t.Name == "DialogInput");
        input.Focus();
        w.KeyTextInput("Sky");
        Assert.Equal("Sky", vm.CurrentDialog!.InputText);
        Shot(w, "ui-ask-input");
        Click(Find<Button>(w, b => b.Content as string == "Submit"));

        await Until(() => vm.Phase == SessionPhase.Ready && !vm.HasDialog, "run end");
        Assert.Contains("color=Blue name=Sky", vm.Rows.OfType<ToolRowViewModel>().Single().Output);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// Regression (package E2E, real keyboard under X11): a card's button that the user clicked disappears with the
    /// card, and focus went with it: the next keystrokes went nowhere until the user clicked the composer.
    /// </summary>
    [AvaloniaFact]
    public async Task Focus_returns_to_the_composer_when_the_focused_card_button_goes_away()
    {
        var (w, vm, _) = await OpenAndPromptAsync("approval");
        var allow = Find<Button>(w, b => b.Name == "AllowButton");
        allow.Focus(); // as a mouse click leaves it
        Assert.True(allow.IsFocused);
        Click(allow);
        await Until(() => vm.Phase == SessionPhase.Ready && !vm.HasDialog, "run end");
        await Until(() => w.Composer.IsFocused, "composer focused again", 5);
        w.KeyTextInput("typed right after");
        Assert.Equal("typed right after", vm.ComposerText);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Focus_returns_to_the_composer_after_restart_omp_brings_it_up()
    {
        // The first start finds no model (omp 18.2.0 exits before RPC); after the user set one up, Restart works.
        var starts = 0;
        var s = new SessionController(_ => starts++ == 0 ? TestProcesses.Fake("no-models") : TestProcesses.Fake("normal"), new LaunchRequest(null));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        w.Show();
        vm.OnWindowOpened();
        var restart = Find<Button>(w, b => b.Name == "RecoverButton");
        restart.Focus();
        Click(restart);
        await Until(() => vm.Phase == SessionPhase.Ready, "ready after restart");
        await Until(() => w.Composer.IsFocused, "composer focused", 5);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Focus_is_not_taken_from_a_control_the_user_is_in()
    {
        var (w, vm, _) = await OpenAndPromptAsync("approval");
        var filter = Find<TextBox>(w, t => t.Name == "SessionFilterBox");
        filter.Focus();
        vm.CurrentDialog!.AllowCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && !vm.HasDialog, "run end");
        Dispatcher.UIThread.RunJobs();
        Assert.True(filter.IsFocused);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// Review finding: while the user is in another app, the window keeps no keyboard focus; when the run ended there,
    /// the composer took focus and replaced the control the window would give focus back to (the user left it in the
    /// session filter, the terminal or a settings field). Nothing is taken while the window is in the background.
    /// </summary>
    [AvaloniaFact]
    public async Task Focus_is_not_moved_while_the_window_is_in_the_background()
    {
        var (w, vm, _) = await OpenAndPromptAsync("approval");
        var filter = Find<TextBox>(w, t => t.Name == "SessionFilterBox");
        filter.Focus();
        // The user switches to another app: the window system deactivates the window (headless has no second app,
        // so its platform callback is raised directly), and the window keeps no keyboard focus meanwhile.
        var impl = w.PlatformImpl!;
        var deactivated = impl.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .First(p => p.Name.EndsWith("Deactivated", StringComparison.Ordinal) && p.PropertyType == typeof(Action));
        ((Action)deactivated.GetValue(impl)!).Invoke();
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.IsActive);
        ((Avalonia.Input.FocusManager)w.FocusManager!).Focus(null, NavigationMethod.Unspecified, KeyModifiers.None);
        vm.CurrentDialog!.AllowCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && !vm.HasDialog, "run end");
        Dispatcher.UIThread.RunJobs();
        Assert.False(w.Composer.IsFocused);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Esc_inside_the_dialog_dismisses_only_the_dialog()
    {
        var (w, vm, _) = await OpenAndPromptAsync("ask");
        Click(Find<Button>(w, b => b.DataContext is DialogOptionViewModel { Label: "Red (Recommended)" }));
        await Until(() => vm.CurrentDialog is { IsInput: true }, "input dialog");
        Find<TextBox>(w, t => t.Name == "DialogInput").Focus();
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Until(() => vm.Phase == SessionPhase.Ready && !vm.HasDialog, "run end");
        // Only the dialog was dismissed: the run finished normally with "no answer" for the name.
        Assert.Contains("color=Red (Recommended) name=none", vm.Rows.OfType<ToolRowViewModel>().Single().Output);
        Assert.NotEqual("Interrupted", vm.Rows.OfType<AssistantRowViewModel>().Last().Footer);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaTheory]
    [InlineData(480, 360)]
    [InlineData(1000, 380)]
    public async Task Approval_card_fits_small_windows(double width, double height)
    {
        var (w, vm, _) = await OpenAndPromptAsync("approval", width, height);
        var allow = Find<Button>(w, b => b.Name == "AllowButton");
        var bottom = allow.TranslatePoint(new Avalonia.Point(0, allow.Bounds.Height), w)!.Value.Y;
        var right = allow.TranslatePoint(new Avalonia.Point(allow.Bounds.Width, 0), w)!.Value.X;
        Assert.True(bottom <= w.Bounds.Height && right <= w.Bounds.Width, $"Allow button off-window at {right},{bottom}");
        Shot(w, $"ui-approval-{width}x{height}");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public void Editor_text_and_a_sign_in_link_in_the_same_update_are_both_applied()
    {
        // Regression (review round 3, #11): one shared counter dropped the link when editor text had a higher number.
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var snap = new SessionSnapshot(1, SessionPhase.Ready, [], null, 2, null, 0, 0, null, null, null, [])
        {
            OpenUrl = new OpenUrlRequest(1, "https://example.invalid/a", null, "Enter code: 42"),
            EditorText = new EditorTextRequest(2, "prefilled"),
        };
        vm.Apply(snap);
        Assert.Equal(("https://example.invalid/a", "Enter code: 42", "prefilled"), (vm.PendingUrl, vm.PendingUrlInstructions, vm.ComposerText));
    }

    [AvaloniaFact]
    public void An_older_snapshot_never_overwrites_a_newer_one()
    {
        // Regression (one-CPU stress run): the pump applied a snapshot taken before a command's own apply, so a run
        // that had ended showed as running again (and the next Enter queued instead of sending).
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        SessionSnapshot Snap(long v, SessionPhase p) => new(v, p, [], null, 2, null, 0, 0, null, null, null, []);
        vm.Apply(Snap(10, SessionPhase.Ready));
        vm.Apply(Snap(7, SessionPhase.Running));
        Assert.Equal(SessionPhase.Ready, vm.Phase);
        vm.Apply(Snap(11, SessionPhase.Running));
        Assert.Equal(SessionPhase.Running, vm.Phase);
    }

    [AvaloniaFact]
    public async Task Countdown_shows_for_a_dialog_with_a_timeout_and_the_card_closes_at_the_deadline()
    {
        var (w, vm, _) = await OpenAndPromptAsync("approval-timeout");
        Assert.True(vm.CurrentDialog!.HasDeadline);
        Assert.EndsWith("s left", vm.CurrentDialog.Remaining);
        await Until(() => !vm.HasDialog, "expired");
        Assert.Contains(vm.Rows.OfType<NoticeRowViewModel>(), n => n.Text.StartsWith("Approval request timed out", StringComparison.Ordinal));
        await vm.DisposeAsync();
        w.Close();
    }
}
