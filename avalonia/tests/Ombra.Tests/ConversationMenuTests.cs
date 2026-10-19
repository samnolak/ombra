using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class ConversationMenuTests
{
    private sealed class MenuWindow : Window
    {
        static MenuWindow() => ContextMenus.Attach<MenuWindow>();
    }

    private static MainViewModel Model() => new(new SessionController(
        TestProcesses.FakeFactory("normal", TestProcesses.TempDir("copy-menu-sessions")),
        new LaunchRequest(TestProcesses.TempDir("copy-menu-project"))), new AppArgs());

    private static MenuWindow Show(MainViewModel vm, Control content)
    {
        var window = new MenuWindow { DataContext = vm, Content = content, Width = 600, Height = 400 };
        PathLinks.SetHandler(window, vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Flyout Open(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        var menu = Assert.IsType<Flyout>(control.ContextFlyout);
        Assert.True(menu.IsOpen);
        MicroUxTabTests.Shot(window, "context-" + control.GetType().Name + "-" + control.DataContext?.GetType().Name);
        return menu;
    }

    private static Button Item(Flyout menu, string name) => ((Control)menu.Content!).GetLogicalDescendants()
        .OfType<Button>().Single(button => AutomationProperties.GetName(button) == Loc.T(name));

    private static void Choose(Flyout menu, string name) => Item(menu, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public async Task Clipboard_feedback_waits_for_success_and_reports_failure_once()
    {
        await using var vm = Model();
        var target = new Border();
        var window = Show(vm, target);
        var write = ContextMenus.WriteClipboard;
        vm.CopyTextRequested += text => ContextMenus.CopyAsync(window, text);
        var cards = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SessionCard)) cards++; };
        try
        {
            var pending = new TaskCompletionSource();
            ContextMenus.WriteClipboard = (_, _) => pending.Task;
            var copy = vm.CopyTextAsync("message", Loc.T("Session ID copied"));
            Assert.False(copy.IsCompleted);
            Assert.Null(vm.SessionCard);
            pending.SetResult();
            Assert.True(await copy);
            Assert.Equal(Loc.T("Session ID copied"), vm.SessionCard!.Title);
            Assert.Equal(1, cards);

            ContextMenus.WriteClipboard = (_, _) => Task.FromException(new InvalidOperationException("clipboard locked"));
            Assert.False(await vm.CopyTextAsync("message", Loc.T("Session ID copied")));
            Assert.Equal(Loc.T("Couldn't copy"), vm.SessionCard!.Title);
            Assert.Equal("clipboard locked", vm.SessionCard.Message);
            Assert.Equal(2, cards);
            Assert.False(await vm.CopyTextAsync(""));
            Assert.Equal(2, cards);
        }
        finally { ContextMenus.WriteClipboard = write; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Code_insert_and_selected_quote_preserve_the_existing_draft()
    {
        await using var vm = Model();
        using var row = new UserRowViewModel(new UserItem(1, "first\nsecond", true));
        var text = new SelectableTextBlock { Text = row.Text, DataContext = row, Height = 80 };
        var code = new Border { Height = 80, Background = Avalonia.Media.Brushes.Gray, ContextFlyout = ContextMenus.ForCodeBlock(() => "CODE") };
        var window = Show(vm, new StackPanel { Children = { code, text } });
        try
        {
            vm.ComposerText = "before after";
            vm.ComposerCaretIndex = 7;
            Choose(Open(window, code), "Insert into message");
            Assert.Equal("before CODEafter", vm.ComposerText);
            Assert.Equal(11, vm.ComposerCaretIndex);
            code.ContextFlyout!.Hide();
            text.SelectAll();
            var point = text.TranslatePoint(new Point(20, 20), window)!.Value;
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            var native = Assert.IsType<MenuFlyout>(text.ContextFlyout);
            var quote = native.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Loc.T("Quote in message")));
            quote.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("before CODE> first\n> second\nafter", vm.ComposerText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Events_notice_and_pasted_text_menus_act_on_the_clicked_content()
    {
        await using var vm = Model();
        vm.DebugLog.Add("first event");
        vm.DebugLog.Add("second event");
        var eventRow = new SelectableTextBlock { DataContext = "first event", Text = "first event", Height = 50, Background = Avalonia.Media.Brushes.Transparent };
        var notice = new Border { DataContext = new NoticeRowViewModel(new NoticeItem(2, NoticeLevel.Warning, "notice details")), Height = 50, Background = Avalonia.Media.Brushes.Gray };
        var paste = new PastedTextViewModel("full pasted text", vm);
        vm.PastedTexts.Add(paste);
        var chip = new Border { DataContext = paste, Height = 50, Background = Avalonia.Media.Brushes.Gray };
        ConversationMenus.SetKind(eventRow, ConversationMenuKind.Events);
        ConversationMenus.SetKind(notice, ConversationMenuKind.Notice);
        ConversationMenus.SetKind(chip, ConversationMenuKind.Paste);
        var window = Show(vm, new StackPanel { Children = { eventRow, notice, chip } });
        try
        {
            Choose(Open(window, eventRow), "Copy");
            Assert.Equal("first event", await window.Clipboard!.TryGetTextAsync());
            eventRow.ContextFlyout!.Hide();
            Choose(Open(window, eventRow), "Copy all");
            Assert.Equal(string.Join(Environment.NewLine, vm.DebugLog), await window.Clipboard!.TryGetTextAsync());
            eventRow.ContextFlyout!.Hide();
            Choose(Open(window, eventRow), "Clear");
            Assert.Empty(vm.DebugLog);
            eventRow.ContextFlyout!.Hide();
            Choose(Open(window, notice), "Copy details");
            Assert.Equal("notice details", await window.Clipboard!.TryGetTextAsync());
            notice.ContextFlyout!.Hide();
            Choose(Open(window, notice), "Show in Events");
            Assert.True(vm.IsDebugVisible);
            notice.ContextFlyout!.Hide();
            Choose(Open(window, chip), "Preview");
            Dispatcher.UIThread.RunJobs();
            var preview = Assert.IsType<Flyout>(chip.ContextFlyout);
            Assert.Equal(paste.Text, Assert.IsType<CodeView>(preview.Content).Text);
            Assert.True(preview.IsOpen);
            preview.Hide();
            vm.ComposerText = "ab";
            vm.ComposerCaretIndex = 1;
            Choose(Open(window, chip), "Expand into message");
            Assert.Equal("afull pasted textb", vm.ComposerText);
            Assert.Empty(vm.PastedTexts);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Images_use_checked_clipboard_writes_and_missing_bytes_disable_file_actions()
    {
        await using var vm = Model();
        var missing = new ImageAttachment("gone.png", "image/png", []);
        using var preview = new ImagePreview(missing, () => [missing]);
        var image = new Border { DataContext = preview, Height = 80, Background = Avalonia.Media.Brushes.Gray };
        ConversationMenus.SetKind(image, ConversationMenuKind.Image);
        var window = Show(vm, image);
        var write = ContextMenus.WriteBitmap;
        try
        {
            var menu = Open(window, image);
            Assert.True(Item(menu, "Open").IsEnabled);
            Assert.False(Item(menu, "Copy image").IsEnabled);
            Assert.False(Item(menu, "Save image…").IsEnabled);
            Assert.False(Item(menu, "Open in default app").IsEnabled);
            Assert.False(Item(menu, FileOpeners.RevealLabel).IsEnabled);
            menu.Hide();
            using var bitmap = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96));
            ContextMenus.WriteBitmap = (_, _) => Task.FromException(new InvalidOperationException("image clipboard locked"));
            Assert.False(await ContextMenus.CopyImageAsync(image, bitmap, Loc.T("Copied")));
            Assert.Equal(Loc.T("Couldn't copy"), vm.SessionCard!.Title);
            Assert.Equal("image clipboard locked", vm.SessionCard.Message);
            string? viewerError = null;
            var card = vm.SessionCard;
            Assert.False(await ContextMenus.CopyImageAsync(image, bitmap, failure: error => viewerError = error));
            Assert.Equal("image clipboard locked", viewerError);
            Assert.Same(card, vm.SessionCard); // the modal's visible error, not a second toast behind it
            vm.CloseSessionCardCommand.Execute(null);
            ContextMenus.WriteBitmap = (_, _) => Task.CompletedTask;
            Assert.True(await ContextMenus.CopyImageAsync(image, bitmap, Loc.T("Copied")));
            Assert.Equal(Loc.T("Copied"), vm.SessionCard!.Title);
            Assert.False(await ContextMenus.CopyImageAsync(image, missing));
            Assert.Equal(Loc.T("Couldn't copy"), vm.SessionCard!.Title);
        }
        finally { ContextMenus.WriteBitmap = write; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Queue_menu_exposes_existing_edit_remove_and_eligible_send_commands()
    {
        await using var vm = Model();
        using var queue = new QueuedItemViewModel(new QueuedMessage(1, QueueKind.FollowUp, "queued text", []), vm);
        var target = new Border { DataContext = queue, Height = 80, Background = Avalonia.Media.Brushes.Gray };
        ConversationMenus.SetKind(target, ConversationMenuKind.Queue);
        var window = Show(vm, target);
        try
        {
            var menu = Open(window, target);
            Assert.True(Item(menu, "Edit").IsEnabled);
            Assert.True(Item(menu, "Remove").IsEnabled);
            Assert.True(Item(menu, "Send now").IsEnabled);
            Choose(menu, "Copy");
            Assert.Equal("queued text", await window.Clipboard!.TryGetTextAsync());
            menu.Hide();
            queue.Model = queue.Model with { Recovered = true };
            Assert.False(Item(Open(window, target), "Send now").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Image_save_preserves_original_bytes_and_surfaces_picker_and_write_errors()
    {
        await using var vm = Model();
        var control = new Border();
        var window = Show(vm, control);
        var picker = ConversationMenus.OpenImageDestination;
        try
        {
            var image = new ImageAttachment("original.gif", "image/gif", [1, 2, 3, 4]);
            var destination = new MemoryStream();
            destination.Write(new byte[20]);
            ConversationMenus.OpenImageDestination = (_, requested) =>
            {
                Assert.Same(image, requested);
                return Task.FromResult<Stream?>(destination);
            };
            await ConversationMenus.SaveImageAsync(control, image);
            Assert.Equal(image.Data, destination.ToArray());
            Assert.Null(vm.SessionCard);
            ConversationMenus.OpenImageDestination = (_, _) => Task.FromResult<Stream?>(null);
            await ConversationMenus.SaveImageAsync(control, image);
            Assert.Null(vm.SessionCard); // cancelling the picker is not an error
            ConversationMenus.OpenImageDestination = (_, _) => Task.FromException<Stream?>(new IOException("permission denied"));
            await ConversationMenus.SaveImageAsync(control, image);
            Assert.Equal(Loc.T("Could not save the image"), vm.SessionCard!.Title);
            Assert.Equal("permission denied", vm.SessionCard.Message);
            ConversationMenus.OpenImageDestination = (_, _) => Task.FromResult<Stream?>(new MemoryStream([], writable: false));
            await ConversationMenus.SaveImageAsync(control, image);
            Assert.Equal(Loc.T("Could not save the image"), vm.SessionCard!.Title);
            Assert.NotEmpty(vm.SessionCard.Message);
        }
        finally { ConversationMenus.OpenImageDestination = picker; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Reply_error_menu_copies_the_error_not_the_partial_reply()
    {
        await using var vm = Model();
        var reply = new AssistantRowViewModel(new AssistantItem(1, "partial answer", "", false, "error", "provider failed")) { CanRetry = true };
        var target = new Border { DataContext = reply, Height = 80, Background = Avalonia.Media.Brushes.Gray };
        ConversationMenus.SetKind(target, ConversationMenuKind.Error);
        var window = Show(vm, target);
        try
        {
            var menu = Open(window, target);
            Assert.NotNull(Item(menu, "Retry"));
            Choose(menu, "Copy details");
            Assert.Equal("provider failed", await window.Clipboard!.TryGetTextAsync());
            menu.Hide();
            reply.CanRetry = false;
            menu = Open(window, target);
            Assert.DoesNotContain(((Control)menu.Content!).GetLogicalDescendants().OfType<Button>(),
                button => AutomationProperties.GetName(button) == Loc.T("Retry"));
        }
        finally { window.Close(); }
    }
}
