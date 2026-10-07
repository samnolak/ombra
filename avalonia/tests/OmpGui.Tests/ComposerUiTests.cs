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
using OmpGui.App;
using OmpGui.App.Services;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Composer: images, file references, queue and steer while the agent works.</summary>
public sealed class ComposerUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

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
        Assert.Equal("1 image", vm.Rows.OfType<UserRowViewModel>().Single().ImagesText);
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
        Assert.StartsWith("At most 3 images", vm.ComposerMessage);
        vm.Attachments[0].RemoveCommand.Execute(null);
        await vm.AddFilesAsync([broken]);
        Assert.Equal(2, vm.Attachments.Count);
        Assert.StartsWith("broken.png: not an image", vm.ComposerMessage);
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
        Assert.True(w.FindControl<Button>("SteerButton")!.IsEffectivelyVisible);
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
        Assert.Equal("Pasted text +40 lines", vm.PastedTexts[0].Label);
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
    public async Task Shift_tab_steps_through_the_permission_modes_and_bypass_asks_first()
    {
        var (w, vm, s) = await OpenAsync();
        var composer = w.FindControl<TextBox>("Composer")!;
        composer.Focus();
        var box = w.FindControl<Border>("ComposerBox")!;
        Assert.Null(vm.ShownApprovalMode); // omp's default: no mode chosen in this test's settings

        // Two quick steps: each shown at once, one omp restart once the keys pause
        w.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.Equal("always-ask", vm.ShownApprovalMode);
        Assert.Equal("Ask permissions", vm.ApprovalLabel);
        w.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.Equal("write", vm.ShownApprovalMode);
        Assert.True(vm.IsAcceptEdits);
        Assert.Contains("mode-write", box.Classes);
        Assert.Equal("Accept edits", vm.ApprovalLabel);
        Assert.True(composer.IsFocused); // the keys stay in the message box
        await Until(() => s.Snapshot().ApprovalMode == "write" && vm.Phase == SessionPhase.Ready && vm.PendingApprovalMode is null, "omp restarted in Accept edits");

        // Accept edits → Bypass: nothing changes until confirmed in the permission menu, which opens for it
        w.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.True(vm.ConfirmYolo);
        Assert.Equal("write", vm.ShownApprovalMode);
        Assert.True(w.FindControl<Button>("ApprovalButton")!.Flyout!.IsOpen);
        Shot(w, "ui-composer-bypass-confirm");
        await vm.SetApprovalModeCommand.ExecuteAsync("yolo");
        Assert.Contains("mode-yolo", box.Classes);
        await Until(() => s.Snapshot().ApprovalMode == "yolo" && vm.Phase == SessionPhase.Ready, "bypass");
        Assert.Equal("Bypass permissions", vm.ApprovalLabel);
        await vm.DisposeAsync();
        w.Close();
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
        Assert.True(w.FindControl<OmpGui.App.Controls.ShortcutsSheet>("ShortcutsSheet")!.IsEffectivelyVisible);
        Assert.Contains(vm.Shortcuts.SelectMany(g => g.Items), s => s.Action == "Switch permission mode");
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
