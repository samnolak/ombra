using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Services;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>Composer: images, file references, queue and steer while the agent works.</summary>
public sealed class ComposerUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task<(MainWindow w, MainViewModel vm, SessionController s)> OpenAsync(string scenario = "normal")
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("cu-sessions")), new LaunchRequest(TestProcesses.TempDir("cu-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
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
        frame?.Save(file, new PngBitmapEncoderOptions());
    }

    /// <summary>A real PNG of the given size (noise, so it does not compress to nothing).</summary>
    private static string MakePng(string dir, string name, int width, int height)
    {
        var rng = new Random(42);
        var pixels = new byte[width * height * 4];
        rng.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        using var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using (var fb = bmp.Lock()) System.Runtime.InteropServices.Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        var path = Path.Combine(dir, name);
        bmp.Save(path, new PngBitmapEncoderOptions());
        return path;
    }

    [AvaloniaFact]
    public async Task Images_attach_scale_send_and_show_on_the_message()
    {
        var (w, vm, _) = await OpenAsync();
        var dir = TestProcesses.TempDir("images");
        var small = MakePng(dir, "small.png", 40, 30);
        var big = MakePng(dir, "big.png", 3000, 2000); // ~24 MB of noise as PNG: must be scaled down to fit
        var notes = Path.Combine(dir, "notes with space.txt");
        File.WriteAllText(notes, "x");

        await vm.AddFilesAsync([small, big, notes]);
        Assert.Equal(2, vm.Attachments.Count);
        Assert.Equal("image/png", vm.Attachments[0].Model.MimeType);
        Assert.Equal("image/jpeg", vm.Attachments[1].Model.MimeType);
        Assert.True(vm.Attachments[1].Model.Data.Length <= ImageAttachments.MaxImageBytes);
        Assert.Contains($"\"{notes}\"", vm.ComposerText);
        Shot(w, "ui-composer-attachments");

        vm.Attachments[0].RemoveCommand.Execute(null);
        Assert.Single(vm.Attachments);
        vm.ComposerText = "look " + vm.ComposerText;
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Confirmed && u.HasImages), "sent with image");
        Assert.Single(vm.Rows.OfType<UserRowViewModel>().Single().Previews);
        Assert.Empty(vm.Attachments);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Image_limit_and_unreadable_images_are_explained()
    {
        var (w, vm, _) = await OpenAsync();
        var dir = TestProcesses.TempDir("images");
        var paths = Enumerable.Range(0, 4).Select(i => MakePng(dir, $"i{i}.png", 20, 20)).ToList();
        var broken = Path.Combine(dir, "broken.png");
        File.WriteAllBytes(broken, [1, 2, 3, 4]);
        await vm.AddFilesAsync(paths);
        Assert.Equal(ImageAttachments.MaxImages, vm.Attachments.Count);
        vm.Attachments[0].RemoveCommand.Execute(null);
        await vm.AddFilesAsync([broken]);
        Assert.Equal(2, vm.Attachments.Count);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_message_typed_before_the_prompt_is_acknowledged_still_queues()
    {
        // Regression (CI run 15, macOS): Send was still "executing" (waiting for the prompt ack), so Enter did nothing.
        var (w, vm, _) = await OpenAsync("no-prompt-ack");
        vm.ComposerText = "first";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Running, "running");
        var composer = w.FindControl<TextBox>("Composer")!;
        composer.Focus();
        w.KeyTextInput("second");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.QueuedMessages.Count == 1, "queued");
        Assert.Equal("", vm.ComposerText);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task While_working_enter_queues_and_alt_enter_steers()
    {
        // The fake's run lasts until both messages are in: the steer below must reach a running agent however slow the
        // machine types
        var (w, vm, _) = await OpenAsync("queue-stream-2");
        vm.ComposerText = "long task";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Running, "running");
        var composer = w.FindControl<TextBox>("Composer")!;
        composer.Focus();
        w.KeyTextInput("queue me");
        Dispatcher.UIThread.RunJobs();
        Assert.True(w.FindControl<Button>("QueueButton")!.IsEffectivelyVisible);
        Assert.True(w.FindControl<Button>("StopButton")!.IsEffectivelyVisible);
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        w.KeyTextInput("steer me");
        w.KeyPress(Key.Enter, RawInputModifiers.Alt, PhysicalKey.Enter, null);
        await Until(() => vm.QueuedMessages.Count == 2 || vm.Phase == SessionPhase.Ready, "queued shown");
        Shot(w, "ui-composer-queued");
        await Until(() => vm.Phase == SessionPhase.Ready && vm.QueuedMessages.Count == 0, "delivered");
        Assert.Equal(["long task", "queue me", "steer me"], vm.Rows.OfType<UserRowViewModel>().Select(u => u.Text));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task While_working_with_text_the_mode_thinking_and_model_keep_their_labels()
    {
        // Regression: the three mid-run buttons (Steer, Stop and send, Queue) pushed the toolbar into density 2–4, which
        // hid the permission mode's and thinking's words and then the model's, while "By the way" kept its label
        var (w, vm, _) = await OpenAsync("queue-stream-2");
        vm.ComposerText = "long task";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Running, "running");
        var composer = w.FindControl<TextBox>("Composer")!;
        composer.Focus();
        w.KeyTextInput("queue me");
        await Until(() => vm.CanQueue, "queue offered");
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        var toolbar = w.FindControl<Grid>("ComposerToolbar")!;
        TextBlock Label(string cls) => toolbar.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains(cls));
        Assert.True(Label("approval-label").IsEffectivelyVisible, "permission mode label");
        Assert.True(Label("thinking-label").IsEffectivelyVisible, "thinking label");
        Assert.True(Label("model-label").IsEffectivelyVisible || Label("model-short-label").IsEffectivelyVisible, "model label");
        var stop = w.FindControl<Button>("StopButton")!;
        var box = w.FindControl<Border>("ComposerBox")!;
        var right = stop.TranslatePoint(new Point(stop.Bounds.Width, 0), box)!.Value.X;
        Assert.True(right <= box.Bounds.Width, $"Stop inside the box: {right} <= {box.Bounds.Width}");
        Shot(w, "ui-composer-running-with-text");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_queued_message_can_be_taken_back_or_edited_before_omp_takes_it()
    {
        // The fake's run lasts until three messages were queued (removals do not count)
        var (w, vm, _) = await OpenAsync("queue-stream-3");
        vm.ComposerText = "long task";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Running, "running");
        var composer = w.FindControl<TextBox>("Composer")!;
        composer.Focus();
        foreach (var text in new[] { "drop me", "edit me" })
        {
            w.KeyTextInput(text);
            w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        }
        await Until(() => vm.QueuedMessages.Count == 2, "two queued");
        Shot(w, "ui-composer-queued-remove");

        // ×: withdrawn from omp's queue
        await vm.QueuedMessages[0].RemoveCommand.ExecuteAsync(null);
        Assert.Equal(["edit me"], vm.QueuedMessages.Select(q => q.Text));

        // ↑ in the empty box: the last queued message comes back to be edited
        w.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        await Until(() => vm.ComposerText == "edit me" && vm.QueuedMessages.Count == 0, "back in the box");
        vm.ComposerText = "edited";
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        w.KeyTextInput("last");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        await Until(() => vm.Phase == SessionPhase.Ready && vm.QueuedMessages.Count == 0, "delivered");
        Assert.Equal(["long task", "edited", "last"], vm.Rows.OfType<UserRowViewModel>().Select(u => u.Text));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_long_paste_becomes_a_chip_and_is_sent_in_full()
    {
        var (w, vm, _) = await OpenAsync();
        var composer = w.FindControl<TextBox>("Composer")!;
        composer.Focus();
        var log = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}"));
        var clipboard = w.Clipboard!;
        await clipboard.SetTextAsync(log);
        var paste = (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;
        w.KeyPress(Key.V, paste, PhysicalKey.V, null);
        await Until(() => vm.PastedTexts.Count == 1, "chip");
        Assert.Equal("", vm.ComposerText);
        Assert.True(w.FindControl<ItemsControl>("PastedTextList")!.IsEffectivelyVisible);

        // A short paste goes into the box as usual
        await clipboard.SetTextAsync("why does it fail?");
        w.KeyPress(Key.V, paste, PhysicalKey.V, null);
        await Until(() => vm.ComposerText == "why does it fail?", "short paste inline");
        Shot(w, "ui-composer-paste-chip");

        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Confirmed), "sent");
        Assert.Equal("why does it fail?\n\n" + log, vm.Rows.OfType<UserRowViewModel>().Single().Text);
        Assert.Empty(vm.PastedTexts);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task An_all_hidden_paste_preserves_the_selected_draft()
    {
        var (w, vm, _) = await OpenAsync();
        try
        {
            var composer = w.FindControl<TextBox>("Composer")!;
            vm.ComposerText = "keep this draft";
            composer.Focus();
            composer.SelectAll();
            await w.Clipboard!.SetTextAsync("\u200b");
            var paste = (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;
            w.KeyPress(Key.V, paste, PhysicalKey.V, null);
            await Until(() => !string.IsNullOrEmpty(vm.ComposerMessage), "cleanup notice");
            Assert.Equal("keep this draft", vm.ComposerText);
            Assert.Equal("keep this draft", composer.SelectedText);
            Assert.Empty(vm.PastedTexts);
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task Shift_tab_steps_through_the_permission_modes_and_plan_mode_and_bypass_asks_first()
    {
        var (w, vm, s) = await OpenAsync();
        var composer = w.FindControl<TextBox>("Composer")!;
        composer.Focus();
        var pill = w.FindControl<Button>("ApprovalButton")!;
        Assert.Null(vm.ShownApprovalMode); // omp's default: no mode chosen in this test's settings

        // Two quick steps: each shown at once, one omp restart once the keys pause
        w.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.Equal("always-ask", vm.ShownApprovalMode);
        w.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.Equal("write", vm.ShownApprovalMode);
        Assert.True(vm.IsAcceptEdits);
        Assert.Contains("mode-write", pill.Classes);
        Assert.True(composer.IsFocused); // the keys stay in the message box
        await Until(() => s.Snapshot().ApprovalMode == "write" && vm.Phase == SessionPhase.Ready && vm.PendingApprovalMode is null, "omp restarted in Accept edits");

        // Accept edits → Plan mode (Claude Code's order): the session plans read-only, the permission mode stays under it
        w.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.True(vm.IsPlanMode);
        Assert.Equal("write", vm.ShownApprovalMode);
        await Until(() => s.Snapshot().PlanMode.Enabled && !vm.IsPlanActionRunning, "omp in plan mode");
        Assert.True(w.FindControl<Button>("PlanModeChip")!.IsVisible);
        Assert.True(composer.IsFocused);

        // Plan mode → Bypass: plan mode ends, and nothing else changes until confirmed in the permission menu, which opens for it
        w.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.False(vm.IsPlanMode);
        await Until(() => !s.Snapshot().PlanMode.Enabled, "plan mode left");
        Assert.True(vm.ConfirmYolo);
        Assert.Equal("write", vm.ShownApprovalMode);
        Assert.True(w.FindControl<Button>("ApprovalButton")!.Flyout!.IsOpen);
        Shot(w, "ui-composer-bypass-confirm");
        // Another Shift+Tab while the confirmation waits moves on past Bypass (to Ask): a key is never the confirmation
        vm.CycleApprovalMode();
        Assert.False(vm.ConfirmYolo);
        Assert.Equal("always-ask", vm.ShownApprovalMode);
        await Until(() => s.Snapshot().ApprovalMode == "always-ask" && vm.Phase == SessionPhase.Ready && vm.PendingApprovalMode is null, "omp restarted in Ask");
        Assert.Equal("always-ask", vm.ShownApprovalMode);
        vm.CycleApprovalMode();
        Assert.Equal("write", vm.ShownApprovalMode);
        await Until(() => s.Snapshot().ApprovalMode == "write" && vm.Phase == SessionPhase.Ready && vm.PendingApprovalMode is null, "Accept edits again");
        vm.CycleApprovalMode();
        Assert.True(vm.IsPlanMode);
        await Until(() => s.Snapshot().PlanMode.Enabled && !vm.IsPlanActionRunning, "plan mode again");
        vm.CycleApprovalMode();
        Assert.True(vm.ConfirmYolo);
        Assert.Equal("write", vm.ShownApprovalMode);
        await vm.SetApprovalModeCommand.ExecuteAsync("yolo");
        Assert.Contains("mode-yolo", pill.Classes);
        await Until(() => s.Snapshot().ApprovalMode == "yolo" && vm.Phase == SessionPhase.Ready && vm.PendingApprovalMode is null, "bypass");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Cycling_during_a_permission_restart_uses_the_pending_choice_and_drains_the_next_one()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = TestProcesses.FakeFactory("no-plan-mode", TestProcesses.TempDir("cu-mode-sessions")); // the permission modes alone
        var s = new SessionController(async (request, ct) =>
        {
            if (request.ApprovalMode == "always-ask")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return factory(request);
        }, new LaunchRequest(TestProcesses.TempDir("cu-mode-project"), ApprovalMode: "write"));
        var vm = new MainViewModel(s, new AppArgs());
        try
        {
            // No window update pump: reproduce the stale Ready/write projection deterministically while Core
            // already has the new launch request. Restart completion must publish the mode before clearing it.
            await s.StartAsync().WaitAsync(TimeSpan.FromSeconds(20));
            vm.Apply(s.Snapshot());
            var enginePid = s.ProcessId;
            Assert.NotNull(enginePid);
            vm.CycleApprovalMode();
            Assert.True(vm.ConfirmYolo);
            vm.CycleApprovalMode();
            var applying = vm.SetApprovalModeCommand.ExecutionTask!;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal("always-ask", s.Snapshot().ApprovalMode);
            Assert.Equal(SessionPhase.Ready, vm.Phase);
            Assert.Equal("write", vm.ApprovalMode);
            Assert.Equal("always-ask", vm.PendingApprovalMode);
            Assert.Equal("always-ask", vm.ShownApprovalMode);
            Assert.False(applying.IsCompleted);
            Assert.False(vm.ConfirmYolo);

            // This must queue write, not reopen Bypass from the stale write projection. Completing the older
            // Ask restart must neither clear the newer explicit choice nor leave it waiting for another wake-up.
            vm.CycleApprovalMode();
            Assert.Equal("write", vm.PendingApprovalMode);
            Assert.Equal("write", vm.ShownApprovalMode);
            Assert.False(vm.ConfirmYolo);
            release.TrySetResult();
            await applying.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Equal("write", s.Snapshot().ApprovalMode);
            Assert.Equal(SessionPhase.Ready, s.Snapshot().Phase);
            Assert.Equal(enginePid, s.ProcessId); // only the logical session reopens, not the shared engine
            Assert.Equal(SessionPhase.Ready, vm.Phase);
            Assert.Equal("write", vm.ApprovalMode);
            Assert.Null(vm.PendingApprovalMode);
            vm.CycleApprovalMode();
            Assert.True(vm.ConfirmYolo);
            Assert.Equal("write", vm.ShownApprovalMode);
        }
        finally
        {
            release.TrySetResult();
            await vm.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task The_shortcut_sheet_opens_with_command_slash_and_esc_closes_it_before_stopping_omp()
    {
        var (w, vm, _) = await OpenAsync("queue-stream");
        var mod = (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;
        w.FindControl<TextBox>("Composer")!.Focus();
        w.KeyPress(Key.OemQuestion, mod, PhysicalKey.Slash, null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsShortcutsOpen);
        Assert.True(w.FindControl<Ombra.App.Controls.ShortcutsSheet>("ShortcutsSheet")!.IsEffectivelyVisible);
        Shot(w, "ui-shortcut-sheet");

        vm.ComposerText = "long task";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Running, "running");
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsShortcutsOpen);
        Assert.Equal(SessionPhase.Running, vm.Phase); // the first Esc only closed the sheet
        w.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await Until(() => vm.Phase == SessionPhase.Ready, "stopped by the second Esc");

        // /hotkeys opens it too
        vm.ComposerText = "/hotkeys";
        vm.SendCommand.Execute(null);
        Assert.True(vm.IsShortcutsOpen);
        await vm.DisposeAsync();
        w.Close();
    }
}
