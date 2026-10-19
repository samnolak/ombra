using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// One image in, one attachment out, whichever way it came through the real window: ⌘V of image data (during a run, and
/// again after sending) or of a file copied in Finder, a drop on the message box or elsewhere on the window, a queued
/// image taken back to edit, a chat switch with persisted drafts. An image sent without text during a run is one queue
/// row with its thumbnail while omp reports its queue (where omp names it "[Image]"), and omp delivers it once.
/// </summary>
public sealed class ImageAttachInputTests
{
    private static async Task<(MainWindow w, MainViewModel vm)> OpenAsync()
    {
        var s = new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("ia-sessions")), new LaunchRequest(TestProcesses.TempDir("ia-project")));
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

    /// <summary>Lets a second, late attach (a duplicate) land before the count is checked.</summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
    }

    private static WriteableBitmap MakeBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        new Random(7).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using (var fb = bmp.Lock()) System.Runtime.InteropServices.Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
        return bmp;
    }

    private static string MakePng(string name)
    {
        var path = Path.Combine(TestProcesses.TempDir("ia-images"), name);
        using var bmp = MakeBitmap(40, 30);
        bmp.Save(path, new PngBitmapEncoderOptions());
        return path;
    }

    private static RawInputModifiers Command(Window w) => (RawInputModifiers)w.GetPlatformSettings()!.HotkeyConfiguration.CommandModifiers;

    private static void PressPaste(MainWindow w)
    {
        w.Composer.Focus();
        w.KeyPress(Key.V, Command(w), PhysicalKey.V, "v");
        w.KeyRelease(Key.V, Command(w), PhysicalKey.V, "v");
    }

    private static int Thumbnails(MainWindow w) =>
        w.FindControl<ItemsControl>("AttachmentList")!.GetRealizedContainers().Count();

    private static async Task AssertOneAsync(MainWindow w, MainViewModel vm, string what)
    {
        await Until(() => vm.Attachments.Count > 0, $"{what} (message: '{vm.ComposerMessage}')");
        await Settle();
        Assert.Single(vm.Attachments);
        Assert.Equal(1, Thumbnails(w));
    }

    private static void Drop(MainWindow w, Control target, IDataTransfer data)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), w)!.Value;
        w.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        w.DragDrop(at, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        w.DragDrop(at, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
    }

    private static DataTransfer Files(params string[] paths)
    {
        var data = new DataTransfer();
        foreach (var p in paths) data.Add(DataTransferItem.CreateFile(LocalFile(p)));
        return data;
    }

    [AvaloniaFact]
    public async Task Pasting_a_file_copied_in_the_finder_attaches_it_once()
    {
        var (w, vm) = await OpenAsync();
        var png = MakePng("copied.png");
        // Finder puts the file, its icon as image data and its name as text on the pasteboard
        var item = DataTransferItem.CreateFile(LocalFile(png));
        using var icon = MakeBitmap(16, 16);
        item.SetBitmap(icon);
        item.SetText(Path.GetFileName(png));
        var data = new DataTransfer();
        data.Add(item);
        await w.Clipboard!.SetDataAsync(data);
        PressPaste(w);
        await AssertOneAsync(w, vm, "pasted file");
        Assert.Equal("copied.png", vm.Attachments[0].Model.Name);
        Assert.Equal("", vm.ComposerText);
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Dropping_an_image_on_the_message_box_or_the_conversation_attaches_it_once()
    {
        var (w, vm) = await OpenAsync();
        var png = MakePng("dropped.png");
        Drop(w, w.Composer, Files(png));
        await AssertOneAsync(w, vm, "dropped on the box");
        vm.Attachments[0].RemoveCommand.Execute(null);
        // Anywhere on the window: the window's own handler, the same single attachment
        Drop(w, w.FindControl<Control>("Transcript")!, Files(png));
        await AssertOneAsync(w, vm, "dropped on the conversation");
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_queued_image_taken_back_to_edit_comes_back_once()
    {
        var project = TestProcesses.TempDir("ia-edit");
        var settings = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("ia-settings"), "ombra.local.json"));
        var s = new SessionController(TestProcesses.FakeFactory("queue-stream-2", Path.Combine(project, "sessions")), new LaunchRequest(project));
        var vm = new MainViewModel(s, new AppArgs(), settings: settings);
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        vm.ComposerText = "long task";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Running, "running");
        using var bmp = MakeBitmap(40, 30);
        await w.Clipboard!.SetBitmapAsync(bmp);
        PressPaste(w);
        await AssertOneAsync(w, vm, "pasted while running");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.QueuedMessages.Count == 1, "queued");
        Assert.Single(vm.QueuedMessages[0].Previews);
        w.Composer.Focus();
        w.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        await Until(() => vm.QueuedMessages.Count == 0 && vm.Attachments.Count > 0, "back in the box");
        await Settle();
        Assert.Single(vm.Attachments);
        Assert.Equal(1, Thumbnails(w));
        await vm.DisposeAsync();
        w.Close();
    }

    [AvaloniaFact]
    public async Task An_image_pasted_during_a_run_stays_single_through_queue_delivery_and_chat_switches()
    {
        var project = TestProcesses.TempDir("ia-run");
        var settings = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("ia-settings"), "ombra.local.json"));
        var s = new SessionController(TestProcesses.FakeFactory("queue-stream", Path.Combine(project, "sessions")), new LaunchRequest(project));
        var vm = new MainViewModel(s, new AppArgs(), settings: settings);
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        vm.ComposerText = "long task";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Running, "running");
        using var bmp = MakeBitmap(40, 30);
        await w.Clipboard!.SetBitmapAsync(bmp);
        PressPaste(w);
        await AssertOneAsync(w, vm, "pasted while running");
        w.KeyTextInput("look");
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.QueuedMessages.Count == 1 || vm.Rows.OfType<UserRowViewModel>().Count() == 2, "queued");
        Assert.Empty(vm.Attachments);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.QueuedMessages.Count == 0, "delivered");
        await Settle();
        Assert.Empty(vm.Attachments);
        Assert.Single(vm.Rows.OfType<UserRowViewModel>().Single(u => u.Text == "look").Previews);
        using var bmp3 = MakeBitmap(40, 30);
        await w.Clipboard!.SetBitmapAsync(bmp3);
        PressPaste(w);
        await AssertOneAsync(w, vm, "pasted again after sending");
        vm.Attachments[0].RemoveCommand.Execute(null);
        await Settle();
        Assert.Equal(0, Thumbnails(w));

        var first = s.SessionFile;
        await vm.NewChatCommand.ExecuteAsync(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Session != s, "new chat");
        using var bmp2 = MakeBitmap(40, 30); // the paste disposed the first (the headless clipboard hands over the instance)
        await w.Clipboard!.SetBitmapAsync(bmp2);
        PressPaste(w);
        await AssertOneAsync(w, vm, "pasted in the new chat");
        var second = vm.Session;
        await Until(() => vm.Sessions.Any(x => x.Model.Path == first), "first listed");
        await vm.OpenSessionCommand.ExecuteAsync(vm.Sessions.First(x => x.Model.Path == first));
        await Until(() => vm.Session == s, "first shown");
        await Settle();
        Assert.Empty(vm.Attachments);
        vm.RequestCatalogRefresh();
        await Until(() => second.SessionFile is { } f && vm.Sessions.Any(x => x.Model.Path == f), "second listed");
        await vm.OpenSessionCommand.ExecuteAsync(vm.Sessions.First(x => x.Model.Path == second.SessionFile));
        await Until(() => vm.Session == second, "second shown");
        await Settle();
        Assert.Single(vm.Attachments);
        await vm.DisposeAsync();
        w.Close();
    }

    // ───────────── An image without text queued while omp works ─────────────

    private static async Task InLanguage(string language, Func<string, Task> body)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("ia-queue-" + language), "ombra.local.json");
        var previous = Loc.Current;
        Loc.Load(language, settingsPath);
        try
        {
            if (language == "ru") Assert.Equal("В очереди", Loc.T("Queued"));
            await body(settingsPath);
        }
        finally { Loc.Load(previous, settingsPath); }
    }

    /// <summary>A run of the fake omp that reports its queue like omp (queue_update with omp's chip texts).</summary>
    private static async Task<(MainWindow w, MainViewModel vm, SessionController s)> RunningAsync(string scenario, string settingsPath)
    {
        var project = TestProcesses.TempDir("ia-queue");
        var s = new SessionController(TestProcesses.FakeFactory(scenario, Path.Combine(project, "sessions")), new LaunchRequest(project));
        var vm = new MainViewModel(s, new AppArgs(), settings: new ClientSettingsStore(settingsPath));
        var w = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        vm.ComposerText = "long task";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Running, "running");
        return (w, vm, s);
    }

    private static long LastQueueUpdate(SessionController s) =>
        s.Snapshot().DebugTail.LastOrDefault(d => d.Type == "queue_update")?.Seq ?? 0;

    /// <summary>Enter in the message box holding only an image; returns once omp's queue_update listing it was applied.</summary>
    private static async Task QueueImageOnlyAsync(MainWindow w, MainViewModel vm, SessionController s)
    {
        Assert.Equal("", vm.ComposerText);
        Assert.Single(vm.Attachments);
        var before = LastQueueUpdate(s);
        w.Composer.Focus();
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => LastQueueUpdate(s) > before && vm.QueuedMessages.Count > 0, "omp listed the queued image");
        await Settle();
    }

    /// <summary>One row, with the thumbnail and no "[Image]" text, labelled in the app's language.</summary>
    private static void AssertOneImageRow(MainWindow w, MainViewModel vm, string kind)
    {
        var row = Assert.Single(vm.QueuedMessages);
        Assert.Equal("", row.Text);
        Assert.Single(row.Previews);
        Assert.Single(row.Model.Images);
        var container = Assert.Single(w.FindControl<ItemsControl>("QueuedList")!.GetRealizedContainers());
        var texts = container.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(kind, texts);
        Assert.DoesNotContain("[Image]", texts);
        Assert.Single(container.GetVisualDescendants().OfType<Ombra.App.Controls.ImageThumb>(), t => t.IsEffectivelyVisible);
    }

    private static Button RowButton(MainWindow w, string name) =>
        Assert.Single(w.FindControl<ItemsControl>("QueuedList")!.GetVisualDescendants().OfType<Button>(),
            b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == name);

    private static void Click(MainWindow w, Control target)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), w)!.Value;
        w.MouseMove(at);
        w.MouseDown(at, MouseButton.Left);
        w.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The user messages omp recorded in the session file, as (text, image count).</summary>
    private static List<(string Text, int Images)> RecordedUserMessages(SessionController s) =>
        [.. File.ReadAllLines(s.SessionFile!)
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("type").GetString() == "message" && e.GetProperty("message").GetProperty("role").GetString() == "user")
            .Select(e => e.GetProperty("message").GetProperty("content"))
            .Select(c => (string.Concat(c.EnumerateArray().Where(p => p.GetProperty("type").GetString() == "text").Select(p => p.GetProperty("text").GetString())),
                c.EnumerateArray().Count(p => p.GetProperty("type").GetString() == "image")))];

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public Task An_image_sent_without_text_during_a_run_is_one_queue_row_and_is_delivered_once(string language) => InLanguage(language, async settingsPath =>
    {
        // omp lists it as "[Image]": the row the message box queued is that entry, not a second one
        var (w, vm, s) = await RunningAsync("queue-update-2", settingsPath);
        using var bmp = MakeBitmap(40, 30);
        await w.Clipboard!.SetBitmapAsync(bmp);
        PressPaste(w);
        await AssertOneAsync(w, vm, "pasted while running");
        await QueueImageOnlyAsync(w, vm, s);
        Assert.Empty(vm.Attachments);
        AssertOneImageRow(w, vm, Loc.T("Queued"));

        // The row's ↑ (Send now): omp moves it to its steering queue, still one row with its thumbnail
        Click(w, RowButton(w, Loc.T("Send now")));
        await Until(() => vm.QueuedMessages.Count == 1 && vm.QueuedMessages[0].Model.Kind == QueueKind.Steer, "promoted");
        await Settle();
        AssertOneImageRow(w, vm, Loc.T("Steer"));

        // A second message ends the run: omp delivers the image once, with its one image
        vm.ComposerText = "next";
        w.Composer.Focus();
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.QueuedMessages.Count == 0 && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == "next"), "delivered");
        await Settle();
        Assert.Empty(vm.QueuedMessages);
        var sent = Assert.Single(vm.Rows.OfType<UserRowViewModel>(), u => u.HasImages);
        Assert.Equal("", sent.Text);
        Assert.Single(sent.Previews);
        Assert.Equal([("long task", 0), ("", 1), ("next", 0)], RecordedUserMessages(s));
        await vm.DisposeAsync();
        w.Close();
    });

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public Task An_image_queued_without_text_comes_back_to_edit_with_up_and_goes_with_its_x(string language) => InLanguage(language, async settingsPath =>
    {
        var (w, vm, s) = await RunningAsync("queue-update-3", settingsPath);
        using var bmp = MakeBitmap(40, 30);
        await w.Clipboard!.SetBitmapAsync(bmp);
        PressPaste(w);
        await AssertOneAsync(w, vm, "pasted while running");
        await QueueImageOnlyAsync(w, vm, s);
        AssertOneImageRow(w, vm, Loc.T("Queued"));

        // ↑ in the empty box: omp gives it back, the image returns once and no row stays behind
        var before = LastQueueUpdate(s);
        w.Composer.Focus();
        w.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        await Until(() => vm.QueuedMessages.Count == 0 && vm.Attachments.Count > 0 && LastQueueUpdate(s) > before, "back in the box");
        await Settle();
        Assert.Empty(vm.QueuedMessages);
        Assert.Single(vm.Attachments);
        Assert.Equal(1, Thumbnails(w));
        Assert.Equal("", vm.ComposerText);

        // Queued again, then its ✕: omp withdraws it and the row goes for good
        await QueueImageOnlyAsync(w, vm, s);
        AssertOneImageRow(w, vm, Loc.T("Queued"));
        before = LastQueueUpdate(s);
        Click(w, RowButton(w, Loc.T("Remove queued message")));
        await Until(() => vm.QueuedMessages.Count == 0 && LastQueueUpdate(s) > before, "removed");
        await Settle();
        Assert.Empty(vm.QueuedMessages);
        Assert.Empty(vm.Attachments);
        Assert.Equal(Loc.T("Message removed from queue"), vm.SessionCard?.Title);

        // The run ends after the next message: the withdrawn image was never delivered
        vm.ComposerText = "done";
        w.Composer.Focus();
        w.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.QueuedMessages.Count == 0 && vm.Rows.OfType<UserRowViewModel>().Any(u => u.Text == "done"), "delivered");
        await Settle();
        Assert.DoesNotContain(vm.Rows.OfType<UserRowViewModel>(), u => u.HasImages);
        Assert.Equal([("long task", 0), ("done", 0)], RecordedUserMessages(s));
        await vm.DisposeAsync();
        w.Close();
    });

    /// <summary>A local file as a drop or the clipboard hands it over (Finder's file URL): Avalonia's own file item.</summary>
    private static IStorageFile LocalFile(string path) =>
        (IStorageFile)Activator.CreateInstance(typeof(IStorageFile).Assembly.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFile", throwOnError: true)!,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, [new FileInfo(path)], null)!;
}
