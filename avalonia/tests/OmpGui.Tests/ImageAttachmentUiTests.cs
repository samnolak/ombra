using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.Controls;
using OmpGui.App.Services;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Attached images: previews in the message box, on the sent and queued message, and the image viewer.</summary>
public sealed class ImageAttachmentUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task<(MainWindow w, MainViewModel vm)> OpenAsync(string scenario = "normal")
    {
        var s = new SessionController(TestProcesses.FakeFactory(scenario, TestProcesses.TempDir("img-sessions")), new LaunchRequest(TestProcesses.TempDir("img-project")));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        return (w, vm);
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

    /// <summary>An encoded image of the given size: four coloured quarters, so a crop or a stretch shows.</summary>
    private static byte[] MakeImage(int width, int height, bool jpeg = false)
    {
        using var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var (b, g, r) = (x < width / 2, y < height / 2) switch
                {
                    (true, true) => ((byte)0x1A, (byte)0xC2, (byte)0xF2), // yellow
                    (false, true) => ((byte)0xD6, (byte)0x78, (byte)0x2A), // blue
                    (true, false) => ((byte)0x33, (byte)0x99, (byte)0x33), // green
                    _ => ((byte)0x40, (byte)0x40, (byte)0xC0), // red
                };
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
            }
        using (var fb = bmp.Lock()) System.Runtime.InteropServices.Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        using var output = new MemoryStream();
        if (jpeg) bmp.Save(output, new JpegBitmapEncoderOptions { Quality = 90 });
        else bmp.Save(output, new PngBitmapEncoderOptions());
        return output.ToArray();
    }

    /// <summary>A PNG header with nothing readable after it: passes the type check, fails to decode.</summary>
    private static readonly byte[] Corrupt = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8, 9];

    private static T Find<T>(Visual root, Func<T, bool> where) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().First(c => c.IsEffectivelyVisible && where(c));

    private static void Click(Window w, Visual target)
    {
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), w)!.Value;
        w.MouseDown(center, MouseButton.Left);
        w.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Key(Window w, Key key, PhysicalKey physical)
    {
        w.KeyPress(key, RawInputModifiers.None, physical, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Attached_images_preview_in_the_box_and_the_message_and_open_in_the_viewer(string theme)
    {
        MainViewModel.ApplyTheme(theme);
        try
        {
            var (w, vm) = await OpenAsync();
            // A wide screenshot (the chip used to show it 64×36 px, letterboxed), a tall photo, a square
            Assert.True(vm.TryAddImage(new ImageAttachment("screenshot.png", "image/png", MakeImage(1600, 900)), "test"));
            Assert.True(vm.TryAddImage(new ImageAttachment("photo.jpg", "image/jpeg", MakeImage(600, 1000, jpeg: true)), "test"));
            Assert.True(vm.TryAddImage(new ImageAttachment("square.png", "image/png", MakeImage(300, 300)), "test"));
            await Until(() => vm.Attachments.All(a => a.Preview.Thumbnail is not null), "chip previews decoded");

            // The chip shows a 52 px square preview, cropped to fill
            var chipThumb = Find<Button>(w, b => b.Classes.Contains("in-chip"));
            Assert.Equal(52, chipThumb.Bounds.Width, 0.5);
            Assert.Equal(52, chipThumb.Bounds.Height, 0.5);
            var shortSide = vm.Attachments.Select(a => a.Preview.Thumbnail!.PixelSize).Select(p => Math.Min(p.Width, p.Height));
            Assert.All(shortSide, s => Assert.True(s >= 104, $"thumbnail short side {s} px is blurry at 52 px on a 2× display"));
            Shot(w, $"images-chips-{theme}");

            // Clicking the chip's preview opens the viewer on it, ←/→ go through the box's images
            Click(w, chipThumb);
            Assert.True(vm.IsImageViewerOpen);
            await Until(() => vm.ViewerImage is not null, "viewer decoded");
            Assert.Equal("screenshot.png", vm.ViewerTitle);
            Assert.Equal("1600 × 900 · 1 of 3", vm.ViewerDetail);
            Key(w, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
            Assert.False(vm.IsImageViewerOpen);
            Assert.Equal(3, vm.Attachments.Count); // Esc closed the viewer only

            vm.ComposerText = "what do you see";
            vm.SendCommand.Execute(null);
            await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Confirmed), "sent");
            var row = vm.Rows.OfType<UserRowViewModel>().Single();
            Assert.Equal(["screenshot.png", "photo.jpg", "square.png"], row.Previews.Select(p => p.Name));
            await Until(() => row.Previews.All(p => p.Thumbnail is not null), "message thumbnails decoded");
            var thumbs = w.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("in-message") && b.IsEffectivelyVisible).ToList();
            Assert.Equal(3, thumbs.Count);
            Assert.All(thumbs, t => Assert.Equal(72, t.Bounds.Width, 0.5));
            Shot(w, $"images-message-{theme}");

            // A click on the second opens the viewer on it; → wraps to the next, ← back, Space shows 100 %
            Click(w, thumbs[1]);
            Assert.True(vm.IsImageViewerOpen);
            await Until(() => vm.ViewerImage is not null, "viewer decoded");
            Assert.Equal("photo.jpg", vm.ViewerTitle);
            Assert.True(w.FindControl<ImageViewer>("ImageViewer")!.IsKeyboardFocusWithin, "the viewer holds the keyboard");
            Shot(w, $"images-viewer-{theme}");
            Key(w, Avalonia.Input.Key.Right, PhysicalKey.ArrowRight);
            Assert.Equal("square.png", vm.ViewerTitle);
            Key(w, Avalonia.Input.Key.Right, PhysicalKey.ArrowRight);
            Assert.Equal("screenshot.png", vm.ViewerTitle);
            Key(w, Avalonia.Input.Key.Left, PhysicalKey.ArrowLeft);
            Assert.Equal("square.png", vm.ViewerTitle);
            await Until(() => vm.ViewerImage is not null, "viewer decoded");
            Key(w, Avalonia.Input.Key.Space, PhysicalKey.Space);
            Assert.True(vm.IsViewerActualSize);
            Assert.True(w.FindControl<ImageViewer>("ImageViewer")!.FindControl<ScrollViewer>("ActualSizeScroll")!.IsEffectivelyVisible);
            Key(w, Avalonia.Input.Key.Space, PhysicalKey.Space);
            Assert.False(vm.IsViewerActualSize);
            // Tab stays inside the viewer
            for (var i = 0; i < 8; i++)
            {
                Key(w, Avalonia.Input.Key.Tab, PhysicalKey.Tab);
                Assert.True(w.FindControl<ImageViewer>("ImageViewer")!.IsKeyboardFocusWithin, "Tab left the viewer");
            }
            // A click on the dimmed window around the image closes it
            var viewer = w.FindControl<ImageViewer>("ImageViewer")!;
            var corner = viewer.TranslatePoint(new Point(10, viewer.Bounds.Height - 10), w)!.Value;
            w.MouseDown(corner, MouseButton.Left);
            w.MouseUp(corner, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.IsImageViewerOpen);
            Assert.Null(vm.ViewerImage);
            await vm.DisposeAsync();
            w.Close();
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
    }

    [AvaloniaFact]
    public async Task A_corrupt_image_shows_a_placeholder_everywhere_without_failing()
    {
        var (w, vm) = await OpenAsync();
        Assert.True(vm.TryAddImage(new ImageAttachment("broken.png", "image/png", Corrupt), "test"));
        var chip = vm.Attachments.Single();
        _ = chip.Preview.Thumbnail;
        await Until(() => chip.Preview.Failed, "chip placeholder");
        Assert.Null(chip.Preview.Thumbnail);
        Assert.True(Find<Icon>(w, i => i.Classes.Contains("image-missing")).IsEffectivelyVisible);
        Assert.Contains("can't be shown", chip.Preview.Tooltip);

        vm.ComposerText = "broken";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Confirmed), "sent");
        var preview = vm.Rows.OfType<UserRowViewModel>().Single().Previews.Single();
        await Until(() => preview.Failed, "message placeholder");
        Shot(w, "images-corrupt");

        vm.OpenImageCommand.Execute(preview);
        await Until(() => vm.HasViewerProblem, "viewer explains");
        Assert.Null(vm.ViewerImage);
        Assert.True(w.FindControl<ImageViewer>("ImageViewer")!.FindControl<StackPanel>("ProblemView")!.IsEffectivelyVisible);
        vm.CloseImageViewerCommand.Execute(null);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Images_survive_their_file_being_deleted_and_show_on_a_queued_message()
    {
        var (w, vm) = await OpenAsync("queue-stream");
        var dir = TestProcesses.TempDir("img-files");
        var path = Path.Combine(dir, "shot.png");
        await File.WriteAllBytesAsync(path, MakeImage(400, 300));
        vm.ComposerText = "long task";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Running, "running");

        await vm.AddFilesAsync([path]);
        File.Delete(path);
        vm.ComposerText = "and this";
        await vm.SteerCommand.ExecuteAsync(null);
        await Until(() => vm.QueuedMessages.Count == 1 || vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == "and this"), "queued");
        if (vm.QueuedMessages.Count == 1)
        {
            var queued = vm.QueuedMessages[0].Previews.Single();
            await Until(() => queued.Thumbnail is not null, "queued preview decoded");
            Assert.True(Find<Button>(w, b => b.Classes.Contains("in-queue")).IsEffectivelyVisible);
            Shot(w, "images-queued");
        }
        await Until(() => vm.Phase == SessionPhase.Ready && vm.QueuedMessages.Count == 0, "delivered");
        var delivered = vm.Rows.OfType<UserRowViewModel>().Single(u => u.Text == "and this").Previews.Single();
        await Until(() => delivered.Thumbnail is not null, "delivered preview decoded");
        Assert.Equal("shot.png", delivered.Name);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public void A_large_image_made_smaller_keeps_its_name()
    {
        // Real-app QA polish: a dropped PNG over the size limit was re-encoded as JPEG and renamed, so the viewer said
        // "shot.jpg" for the user's "shot.png"
        using var bmp = new WriteableBitmap(new PixelSize(900, 700), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        var noise = new byte[900 * 700 * 4];
        new Random(7).NextBytes(noise); // incompressible: far over the limit as PNG
        using (var fb = bmp.Lock()) System.Runtime.InteropServices.Marshal.Copy(noise, 0, fb.Address, noise.Length);
        using var png = new MemoryStream();
        bmp.Save(png, new PngBitmapEncoderOptions());
        Assert.True(png.Length > ImageAttachments.MaxImageBytes);
        var image = ImageAttachments.FromBytes("shot.png", png.ToArray())!;
        Assert.Equal("shot.png", image.Name);
        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal("image/jpeg", ImageAttachments.MimeOf(image.Data));
        Assert.True(image.Data.Length <= ImageAttachments.MaxImageBytes);
        Assert.EndsWith(".jpg", ImageAttachments.SaveToTemp(image), StringComparison.Ordinal); // the copy for other apps by its bytes
    }

    [Fact]
    public void History_keeps_the_images_omp_returns_and_a_place_for_the_ones_it_lost()
    {
        var png = MakeImageBytes();
        var messages = JsonDocument.Parse($$"""
            [{"role":"user","content":[{"type":"text","text":"look"},
              {"type":"image","mimeType":"image/png","data":"{{Convert.ToBase64String(png)}}"},
              {"type":"image","mimeType":"image/webp","data":"blob:sha256:{{new string('a', 64)}}"}]}]
            """).RootElement;
        var s = new ConversationState();
        s.Hydrate(messages);
        var images = s.Snapshot().Items.OfType<UserItem>().Single().Images;
        Assert.Equal(2, images.Count);
        Assert.Equal(png, images[0].Data);
        Assert.Empty(images[1].Data); // omp could not resolve the stored image: shown as "not kept"
        var lost = new ImagePreview(images[1], () => images);
        Assert.True(lost.Failed);
        Assert.Equal("This image was not kept with the session", lost.Tooltip);
    }

    private static byte[] MakeImageBytes() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];
}
