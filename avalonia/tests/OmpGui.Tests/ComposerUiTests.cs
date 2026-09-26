using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
}
