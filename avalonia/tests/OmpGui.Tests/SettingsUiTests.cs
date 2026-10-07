using Avalonia.Interactivity;
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

/// <summary>Header model picker, approval chip, recovery banner and settings page in the real window.</summary>
public sealed class SettingsUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task<(MainWindow w, MainViewModel vm, SessionController s, ClientSettingsStore store)> OpenAsync(double width = 1200, double height = 760)
    {
        var store = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("ui-settings"), "omp-gui.local.json"));
        var s = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("ui-sessions")),
            new LaunchRequest(TestProcesses.TempDir("ui-project"), ApprovalMode: "write"));
        var vm = new MainViewModel(s, new AppArgs(), settings: store);
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (w, vm, s, store);
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

    [AvaloniaFact]
    public async Task Model_picker_lists_omps_models_and_switches()
    {
        var (w, vm, _, _) = await OpenAsync();
        Assert.Equal("fake/model", vm.CurrentModel);
        Assert.Equal("medium", vm.SelectedThinking);
        w.FindControl<Button>("ModelButton")!.Command!.Execute(null);
        await Until(() => vm.IsModelMenuOpen && vm.Models.Count == 2, "models listed");
        Assert.True(vm.Models.Single(m => m.Key == "fake/model").IsCurrent);
        Shot(w, "ui-model-picker");

        vm.ChooseModelCommand.Execute(vm.Models.Single(m => m.Key == "fake/vision"));
        await Until(() => vm.CurrentModel == "fake/vision" && !vm.IsModelMenuOpen, "switched");
        Assert.False(vm.ShowThinking); // the vision model has no reasoning: no thinking selector
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// Regression (package E2E, real keyboard under X11): the picker opened without focus, so typing a filter and
    /// Enter did nothing; a model could only be chosen with the mouse.
    /// </summary>
    [AvaloniaFact]
    public async Task Model_picker_works_from_the_keyboard()
    {
        var (w, vm, _, _) = await OpenAsync();
        var filter = w.FindControl<TextBox>("ModelFilterBox")!;
        var chip = w.FindControl<Button>("ModelButton")!;
        chip.Focus(); // a click on the chip leaves it focused, and the popup gives focus back to it when it closes
        chip.Command!.Execute(null);
        await Until(() => vm.IsModelMenuOpen && vm.Models.Count == 2 && filter.IsFocused, "picker open, filter focused");
        // Type to filter, Enter takes the first match; focus goes back to the composer.
        filter.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "vision" });
        await Until(() => vm.Models.Count == 1, "filtered");
        filter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Until(() => vm.CurrentModel == "fake/vision" && !vm.IsModelMenuOpen, "switched by Enter");
        // Regression (package E2E): focus went back to the chip, so the next Space re-opened the picker and the rest
        // of the message went into its filter. After a choice the composer has focus.
        await Until(() => w.FindControl<TextBox>("Composer")!.IsFocused, "composer focused");
        Dispatcher.UIThread.RunJobs();
        Assert.False(chip.IsFocused);

        // Down moves into the list, Enter chooses the selected entry.
        w.FindControl<Button>("ModelButton")!.Command!.Execute(null);
        await Until(() => vm.IsModelMenuOpen && vm.Models.Count == 2 && filter.IsFocused, "picker open again");
        filter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        var list = w.FindControl<ListBox>("ModelList")!;
        Assert.Equal(0, list.SelectedIndex);
        list.SelectedIndex = 1; // what the ListBox's own Down does
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await Until(() => vm.CurrentModel == ((ModelItemViewModel)list.Items[1]!).Key || !vm.IsModelMenuOpen, "chosen from the list");
        await Until(() => !vm.IsModelMenuOpen, "closed");

        // Esc closes without a change and gives focus back to the model chip.
        var before = vm.CurrentModel;
        w.FindControl<Button>("ModelButton")!.Command!.Execute(null);
        await Until(() => vm.IsModelMenuOpen && filter.IsFocused, "picker open a third time");
        filter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await Until(() => !vm.IsModelMenuOpen && w.FindControl<Button>("ModelButton")!.IsFocused, "closed by Esc");
        Assert.Equal(before, vm.CurrentModel);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// Regression (package E2E): the thinking selector was decided from the model list, which was loaded only when the
    /// picker opened; after a start or a reopened session it showed for a model without reasoning. The list is loaded
    /// once omp is ready.
    /// </summary>
    [AvaloniaFact]
    public async Task The_thinking_selector_hides_for_a_model_without_reasoning_without_opening_the_picker()
    {
        var s = new SessionController(TestProcesses.Fake("vision-start"));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready && vm.CurrentModel == "fake/vision", "ready on the vision model");
        await Until(() => !vm.ShowThinking, "thinking selector hidden", 10);
        Assert.False(vm.IsModelMenuOpen);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// omp 18.8.0 names the thinking levels of the live model (get_available_thinking_levels). Its model list called
    /// claude-opus-5-5 not reasoning, so the selector was hidden; the levels omp names now decide, "max" included, and
    /// a model with only "off" has no selector.
    /// </summary>
    [AvaloniaFact]
    public async Task The_thinking_menu_lists_the_levels_omp_names_for_the_model()
    {
        var s = new SessionController(TestProcesses.Fake("thinking-levels"));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready && vm.ThinkingOptions.Contains("max"), "levels fetched");
        Assert.Equal(["off", "low", "medium", "high", "xhigh", "max"], vm.ThinkingOptions);
        Assert.True(vm.ShowThinking); // although the model list says the model does not reason
        vm.SetThinkingCommand.Execute("max");
        await Until(() => s.Snapshot().ThinkingLevel == "max", "max reached omp");

        vm.OpenModelMenuCommand.Execute(null);
        await Until(() => vm.Models.Any(m => m.Key == "fake/vision"), "models listed");
        vm.ChooseModelCommand.Execute(vm.Models.First(m => m.Key == "fake/vision"));
        await Until(() => vm.CurrentModel == "fake/vision" && !vm.ShowThinking, "no selector for a model with only off");
        Assert.Equal(["off"], vm.ThinkingOptions);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// Regression (package E2E, real 1100 px window): a long session title was drawn over the model chip; it trims now.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1000)]
    [InlineData(760)]
    public async Task A_long_session_title_trims_instead_of_covering_the_header_controls(double width)
    {
        var (w, vm, _, _) = await OpenAsync(width);
        vm.StartRenameCommand.Execute(null);
        vm.RenameText = "Reply with one short sentence about the sea, then explain the tides in great detail please";
        await vm.CommitRenameCommand.ExecuteAsync(null);
        await Until(() => vm.SessionTitle.StartsWith("Reply with one short", StringComparison.Ordinal), "renamed");
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var title = w.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("title"));
        // The header keeps the title and its buttons (the model chip moved to the composer): the title never reaches them.
        var first = w.FindControl<Button>("TerminalButton")!;
        var titleRight = title.TranslatePoint(new Point(title.Bounds.Width, 0), w)!.Value.X;
        var buttonsLeft = first.TranslatePoint(new Point(0, 0), w)!.Value.X;
        Assert.True(titleRight <= buttonsLeft, $"title ends at {titleRight}, the header buttons start at {buttonsLeft}");
        // The composer toolbar fits too: the model chip ends before the send button.
        var chip = w.FindControl<Button>("ModelButton")!;
        var send = w.FindControl<Button>("SendButton")!;
        var chipRight = chip.TranslatePoint(new Point(chip.Bounds.Width, 0), w)!.Value.X;
        var sendLeft = send.TranslatePoint(new Point(0, 0), w)!.Value.X;
        Assert.True(chipRight <= sendLeft, $"model chip ends at {chipRight}, send starts at {sendLeft}");
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>Regression (package E2E): after Done on the Settings page the next keystrokes went nowhere.</summary>
    [AvaloniaFact]
    public async Task Closing_settings_gives_focus_back_to_the_composer()
    {
        var (w, vm, _, _) = await OpenAsync();
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        var done = w.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Done" && b.IsEffectivelyVisible);
        done.Focus();
        done.Command!.Execute(null);
        await Until(() => !vm.IsSettingsOpen && w.FindControl<TextBox>("Composer")!.IsFocused, "composer focused after Done", 5);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Thinking_level_is_sent_to_omp()
    {
        var (w, vm, s, _) = await OpenAsync();
        vm.SelectedThinking = "high";
        await Until(() => s.Snapshot().ThinkingLevel == "high", "thinking applied");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Never_ask_needs_a_second_confirmation_and_is_saved()
    {
        var (w, vm, s, store) = await OpenAsync();
        Assert.Equal("Accept edits", vm.ApprovalLabel);
        await vm.SetApprovalModeCommand.ExecuteAsync("yolo");
        Assert.True(vm.ConfirmYolo);
        Assert.Equal("write", s.Snapshot().ApprovalMode); // nothing changed yet
        vm.CancelYoloCommand.Execute(null);
        Assert.False(vm.ConfirmYolo);

        await vm.SetApprovalModeCommand.ExecuteAsync("yolo");
        await vm.SetApprovalModeCommand.ExecuteAsync("yolo");
        await Until(() => vm.ApprovalMode == "yolo" && vm.Phase == SessionPhase.Ready, "yolo after confirm");
        Assert.True(vm.IsYolo);
        Assert.Equal("yolo", store.Load().ApprovalMode);

        await vm.SetApprovalModeCommand.ExecuteAsync("write");
        await Until(() => vm.ApprovalMode == "write", "back to write");
        Assert.Equal("write", store.Load().ApprovalMode);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Recovery_banner_restarts_omp_on_the_same_session()
    {
        var (w, vm, s, _) = await OpenAsync();
        vm.ComposerText = "keep this";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Any(), "run");
        System.Diagnostics.Process.GetProcessById(s.ProcessId!.Value).Kill();
        await Until(() => vm.CanRecover, "fault shown");
        Assert.True(w.FindControl<Button>("RecoverButton")!.IsEffectivelyVisible);
        Shot(w, "ui-recovery-banner");
        w.FindControl<Button>("RecoverButton")!.Command!.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == "keep this"), "recovered");
        Assert.False(vm.CanRecover);
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>
    /// Regression (package E2E): the theme under Appearance was applied but saved only by "Save and restart omp" at the
    /// top of the page, so choosing it and pressing Done lost it at the next start. It is saved when chosen (like
    /// notifications), without restarting omp.
    /// </summary>
    [AvaloniaFact]
    public async Task The_theme_is_saved_when_chosen_without_restarting_omp()
    {
        var (w, vm, s, store) = await OpenAsync();
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        await Until(() => vm.IsSettingsOpen, "settings open");
        Assert.Null(store.Load().Theme); // opening the page writes nothing
        var pid = s.ProcessId;
        vm.SettingsTheme = "dark";
        vm.CloseSettingsCommand.Execute(null);
        Assert.Equal("dark", store.Load().Theme);
        Assert.Equal(pid, s.ProcessId);
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        Assert.Equal("dark", vm.SettingsTheme);
        vm.SettingsTheme = "system";
        vm.CloseSettingsCommand.Execute(null);
        Assert.Equal("system", store.Load().Theme);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Settings_page_saves_runtime_fields_and_restarts_omp()
    {
        var (w, vm, s, store) = await OpenAsync();
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        await Until(() => vm.IsSettingsOpen && vm.LoginProviders.Count == 1, "settings open");
        Shot(w, "ui-settings");
        vm.SettingsProfile = "harness";
        vm.SettingsTheme = "dark";
        var pid = s.ProcessId;
        await vm.SaveRuntimeCommand.ExecuteAsync(null);
        Assert.Equal(("harness", "dark"), (store.Load().Profile, store.Load().Theme));
        Assert.NotEqual(pid, s.ProcessId);
        Assert.Equal(SessionPhase.Ready, vm.Phase);
        Assert.Contains("restarted", vm.SettingsMessage);
        Shot(w, "ui-settings-dark");
        vm.SettingsTheme = "system";
        vm.CloseSettingsCommand.Execute(null);
        Assert.False(w.FindControl<Border>("SettingsPage")!.IsVisible);
        await vm.DisposeAsync();
        w.Close();
    }
}
