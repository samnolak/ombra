using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.App.Controls;

public enum ConversationMenuKind { None, Image, Attachment, Notice, Error, Events, Queue, Paste, Details, Tool }

/// <summary>Menus resolve their clicked row when requested, including recycled transcript and queue controls.</summary>
public static class ConversationMenus
{
    public static readonly AttachedProperty<ConversationMenuKind> KindProperty =
        AvaloniaProperty.RegisterAttached<Control, ConversationMenuKind>("Kind", typeof(ConversationMenus));
    public static ConversationMenuKind GetKind(Control control) => control.GetValue(KindProperty);
    public static void SetKind(Control control, ConversationMenuKind kind) => control.SetValue(KindProperty, kind);

    static ConversationMenus()
    {
        KindProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            control.RemoveHandler(InputElement.ContextRequestedEvent, Requested);
            if (change.NewValue is ConversationMenuKind.None) { control.ContextFlyout = null; return; }
            control.AddHandler(InputElement.ContextRequestedEvent, Requested, RoutingStrategies.Tunnel);
            // Lets unselected text defer to this row using the window's normal context-menu routing.
            control.ContextFlyout = ContextMenus.Menu();
        });
    }

    internal static MainViewModel? Owner(Control control) => TopLevel.GetTopLevel(control)?.DataContext as MainViewModel;

    private static void Requested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control || Owner(control) is not { } vm || e.Source is not Control source) return;
        if (source is SelectableTextBlock text && text.SelectionStart != text.SelectionEnd) return;
        if (source.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => GetKind(c) != ConversationMenuKind.None) != control) return;
        if (GetKind(control) == ConversationMenuKind.Tool && source.GetSelfAndVisualAncestors().OfType<Control>()
            .TakeWhile(c => c != control).Any(c => c.ContextFlyout is not null || ContextMenus.GetPathMenu(c) is not null)) return;
        var menu = Create(control, vm);
        if (menu is null) return;
        control.ContextFlyout = menu;
        menu.ShowAt(control, showAtPointer: true);
        e.Handled = true;
    }

    internal static Flyout? Create(Control control, MainViewModel vm)
    {
        MenuAction Copy(Func<string> text, string? label = null) => new(label ?? Loc.T("Copy"), "IconCopy", c => _ = ContextMenus.CopyAsync(c, text(), Loc.T("Copied")), () => text().Length > 0);
        MenuAction Events() => new(Loc.T("Show in Events"), "IconTerminal", _ => vm.IsDebugVisible = true);
        var writable = () => !vm.IsReadOnly;
        switch (GetKind(control))
        {
            case ConversationMenuKind.Tool when control.DataContext is ToolRowViewModel tool:
                var path = tool.FilePath is { Length: > 0 } file ? vm.ResolvePath(file) : null;
                return ContextMenus.Menu(
                    tool.IsCommand ? Copy(() => tool.Command, Loc.T("Copy command")) : null,
                    Copy(() => tool.ShownOutput, Loc.T("Copy output")),
                    tool.HasDiff ? Copy(() => tool.Diff ?? "", Loc.T("Copy patch")) : null,
                    path is not null ? Copy(() => path.FullPath, Loc.T("Copy path")) : null,
                    path is not null ? new(Loc.T("Open in the Files pane"), "IconFile", _ => vm.OpenPath(path)) : null,
                    path is not null ? new(FileOpeners.RevealLabel, "IconFolder", _ => vm.Reveal(path)) : null);
            case ConversationMenuKind.Image when control.DataContext is ImagePreview preview:
                return ImageMenu(preview, vm);
            case ConversationMenuKind.Attachment when control.DataContext is AttachmentViewModel attachment:
                return ImageMenu(attachment.Preview, vm, attachment);
            case ConversationMenuKind.Queue when control.DataContext is QueuedItemViewModel queue:
                return ContextMenus.Menu(Copy(() => queue.Text),
                    new(Loc.T("Edit"), "IconCompose", _ => queue.EditCommand.Execute(null), writable),
                    new(Loc.T("Send now"), "IconArrowUp", _ => queue.SendNowCommand.Execute(null), () => !vm.IsReadOnly && queue.CanSendNow),
                    new(Loc.T("Remove"), "IconClose", _ => queue.RemoveCommand.Execute(null), writable));
            case ConversationMenuKind.Paste when control.DataContext is PastedTextViewModel paste:
                return ContextMenus.Menu(Copy(() => paste.Text),
                    new(Loc.T("Preview"), "IconFileText", c => PreviewPaste(c, paste)),
                    new(Loc.T("Expand into message"), "IconCompose", _ => paste.ExpandCommand.Execute(null), writable),
                    new(Loc.T("Remove"), "IconClose", _ => paste.RemoveCommand.Execute(null), writable));
            case ConversationMenuKind.Notice when control.DataContext is NoticeRowViewModel notice:
                return ContextMenus.Menu(Copy(() => notice.Text, Loc.T("Copy details")), Events());
            case ConversationMenuKind.Error when control.DataContext is AssistantRowViewModel reply:
                return ContextMenus.Menu(Copy(() => reply.ErrorText ?? "", Loc.T("Copy details")),
                    reply.CanRetry ? new(Loc.T("Retry"), "IconRetry", _ => vm.RetryLastTurnCommand.Execute(null), () => vm.RetryLastTurnCommand.CanExecute(null)) : null, Events());
            case ConversationMenuKind.Events:
                return ContextMenus.Menu(control.DataContext is string line ? Copy(() => line) : null,
                    Copy(() => string.Join(Environment.NewLine, vm.DebugLog), Loc.T("Copy all")),
                    new(Loc.T("Clear"), "IconClose", _ => vm.DebugLog.Clear(), () => vm.DebugLog.Count > 0));
            case ConversationMenuKind.Details:
                return ContextMenus.Menu(Copy(() => control is SelectableTextBlock selected ? selected.Text ?? "" : (control as TextBlock)?.Text ?? "", Loc.T("Copy details")), Events());
            default: return null;
        }
    }

    private static Flyout ImageMenu(ImagePreview preview, MainViewModel vm, AttachmentViewModel? attachment = null) => ContextMenus.Menu(
        new(Loc.T("Open"), "IconImage", _ => vm.OpenImageCommand.Execute(preview)),
        new(Loc.T("Copy image"), "IconCopy", c => _ = ContextMenus.CopyImageAsync(c, preview.Model), () => preview.Model.Data.Length > 0),
        new(Loc.T("Save image…"), "IconDownload", c => _ = SaveImageAsync(c, preview.Model), () => preview.Model.Data.Length > 0),
        new(Loc.T("Open in default app"), "IconExternal", c => _ = OpenImageFileAsync(c, preview.Model, reveal: false), () => preview.Model.Data.Length > 0),
        new(FileOpeners.RevealLabel, "IconFolder", c => _ = OpenImageFileAsync(c, preview.Model, reveal: true), () => preview.Model.Data.Length > 0),
        attachment is not null ? new(Loc.T("Remove"), "IconClose", _ => attachment.RemoveCommand.Execute(null), () => !vm.IsReadOnly) : null);

    private static void PreviewPaste(Control control, PastedTextViewModel paste)
    {
        var view = new CodeView { Text = paste.Text, Width = 480, Height = 280 };
        Avalonia.Automation.AutomationProperties.SetName(view, paste.Label);
        var preview = new Flyout { Content = view };
        view.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; preview.Hide(); }
        };
        preview.Opened += (_, _) => view.Surface.Focus();
        // ChoiceMenu closes the action menu after its click; show the preview afterwards.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            control.ContextFlyout = preview;
            preview.ShowAt(control);
        });
    }

    internal static Func<Control, ImageAttachment, Task<Stream?>> OpenImageDestination { get; set; } = PickImageDestinationAsync;

    private static async Task<Stream?> PickImageDestinationAsync(Control control, ImageAttachment image)
    {
        var storage = TopLevel.GetTopLevel(control)?.StorageProvider;
        if (storage is null || !storage.CanSave) throw new InvalidOperationException(Loc.T("Saving files is not available."));
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = Loc.T("Save image…"), SuggestedFileName = Path.GetFileName(image.Name) });
        return file is null ? null : await file.OpenWriteAsync();
    }

    internal static async Task SaveImageAsync(Control control, ImageAttachment image)
    {
        try
        {
            await using var stream = await OpenImageDestination(control, image);
            if (stream is null) return;
            if (stream.CanSeek)
            {
                stream.SetLength(0);
                stream.Position = 0;
            }
            await stream.WriteAsync(image.Data);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Owner(control)?.Notify("IconAlert", Loc.T("Could not save the image"), e.Message);
        }
    }

    private static async Task OpenImageFileAsync(Control control, ImageAttachment image, bool reveal)
    {
        var failure = reveal ? Loc.T("Could not show the image in its folder.") : Loc.T("Could not open the image");
        try
        {
            var path = await Task.Run(() => ImageAttachments.SaveToTemp(image));
            if (!await FileOpeners.StartAsync(reveal ? FileOpeners.Reveal(path) : FileOpeners.OpenWithSystem(path), waitForSuccess: reveal))
                throw new IOException(failure);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Owner(control)?.Notify("IconAlert", failure, e.Message);
        }
    }
}
