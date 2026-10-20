using Ombra.App.Localization;
using System.Runtime.CompilerServices;
using Ombra.App.Services;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.ViewModels;
using Avalonia.Media.Imaging;
using Ombra.ClientCore;

namespace Ombra.App.Controls;

/// <summary>One row of a menu made in code: what it says, its icon (a geometry key in Icons*.axaml), what it does with
/// the control the menu was opened on, and when it can (asked each time the menu opens; always when null).</summary>
public sealed record MenuAction(string Label, string Icon, Action<Control> Run, Func<bool>? CanRun = null);

/// <summary>
/// Right-click menus made in code (code blocks, links, the code viewer), in the same shape as the ones in XAML: a
/// <see cref="Flyout"/> of <c>Button.menu-item</c> rows, an icon and a label each, opened at the pointer and closed by a
/// choice (<see cref="ChoiceMenu"/>), Esc or a click elsewhere. Every action works on the control the menu was opened
/// on, not on whatever is selected.
/// Two rules for every window that opts in (<see cref="Attach{T}"/>): on macOS Control+click is a right click, and text
/// with nothing selected in it defers to the menu of what it sits in (a message, a code block), so the text's own
/// "Copy" menu shows only when there is a selection to copy.
/// </summary>
public static class ContextMenus
{
    /// <summary>The complete code, including any folded part, can be copied or inserted into the message.</summary>
    public static Flyout ForCodeBlock(Func<string> code)
    {
        Flyout? menu = null;
        menu = Menu(new MenuAction(Loc.T("Copy code"), "IconCopy", target => _ = CopyAsync(target, code(), Loc.T("Code copied"))),
            new MenuAction(Loc.T("Insert into message"), "IconCompose", target => ConversationMenus.Owner(target)?.InsertMessageText(code()),
                () => menu?.Target is { } target && ConversationMenus.Owner(target) is { IsReadOnly: false }));
        return menu;
    }

    /// <summary>
    /// A link's menu (its right click, or ⌘-click): open it in the right panel's Browser tool (what a click does), in the
    /// Browser page or in the system's default browser (<see cref="ViewModels.MainViewModel.OpenLink(Uri, ViewModels.LinkTarget)"/>),
    /// copy its full address, or share it with the team (P5).
    /// </summary>
    public static Flyout ForLink(string url)
    {
        Flyout? menu = null;
        var web = Uri.TryCreate(url, UriKind.Absolute, out var uri) && ViewModels.PreviewViewModel.IsAllowed(uri);
        bool InOmbra() => web && menu?.Target is { } target && ConversationMenus.Owner(target) is not null;
        menu = Menu(
            new MenuAction(Loc.T("Open in preview"), "IconGlobe", target => OpenLink(target, url), InOmbra),
            new MenuAction(Loc.T("Open in Browser page"), "IconGlobe", target => OpenLink(target, url, LinkTarget.BrowserPage), InOmbra),
            new MenuAction(web ? Loc.T("Open in default browser") : Loc.T("Open link"), "IconExternal",
                target => OpenLink(target, url, LinkTarget.SystemBrowser)),
            new MenuAction(Loc.T("Copy link"), "IconLink", target => _ = CopyAsync(target, url, Loc.T("Link copied"))),
            null,
            new MenuAction(Loc.T("Share with team…"), "IconShare", target => ConversationMenus.Owner(target)?.ShareLink(url),
                () => menu?.Target is { } target && ConversationMenus.Owner(target) is not null));
        return menu;
    }

    private static readonly ConditionalWeakTable<FlyoutBase, object> PathMenus = new();

    /// <summary>
    /// The one menu for a path in the conversation (the reply's text, a tool's file, a turn's changed file, a change
    /// card's header): open it (the Files pane or the image viewer, the first editor found), add it to the message,
    /// reveal it in the file manager, a shell in its folder, copy its path (absolute or relative to the project).
    /// </summary>
    public static Flyout ForPath(PathTarget target, IPathLinkHandler handler)
    {
        var folder = target.Kind == PathKind.Folder ? target.FullPath : Path.GetDirectoryName(target.FullPath);
        var relative = handler.ProjectRoot is { } root && ProjectFiles.Relative(root, target.FullPath) is { Length: > 0 } rel
            ? rel.Replace('/', Path.DirectorySeparatorChar) : null;
        var menu = Menu(
            new MenuAction(target.Kind == PathKind.Image ? Loc.T("Open") : Loc.T("Open in the Files pane"), "IconFile", _ => handler.OpenPath(target)),
            target.Kind != PathKind.Folder && handler.EditorName is { } editor
                ? new MenuAction(Loc.F("Open in {0}", editor), "IconExternal", _ => handler.OpenInEditor(target)) : null,
            new MenuAction(Loc.T("Add to message"), "IconAt", _ => handler.AddToMessage(target)),
            null,
            new MenuAction(FileOpeners.RevealLabel, "IconFolder", _ => handler.Reveal(target)),
            folder is { Length: > 0 } ? new MenuAction(Loc.T("Open in terminal"), "IconTerminal", _ => handler.OpenTerminal(folder)) : null,
            null,
            new MenuAction(Loc.T("Copy path"), "IconCopy", c => _ = CopyAsync(c, target.FullPath, Loc.T("Path copied"))),
            relative is not null ? new MenuAction(Loc.T("Copy relative path"), "IconCopy", c => _ = CopyAsync(c, relative, Loc.T("Path copied"))) : null);
        PathMenus.AddOrUpdate(menu, PathMenus);
        return menu;
    }

    /// <summary>A menu made by <see cref="ForPath"/>: text showing one is not deferred to what it sits in.</summary>
    public static bool IsPathMenu(FlyoutBase? menu) => menu is not null && PathMenus.TryGetValue(menu, out _);

    /// <summary>
    /// A path the control names (a tool's file, a turn's changed file): right-clicking it opens <see cref="ForPath"/>
    /// for it when it exists, looked up through the window's <see cref="PathLinks.HandlerProperty"/> at that moment.
    /// </summary>
    public static readonly AttachedProperty<string?> PathMenuProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("PathMenu", typeof(ContextMenus));

    public static string? GetPathMenu(Control c) => c.GetValue(PathMenuProperty);
    public static void SetPathMenu(Control c, string? value) => c.SetValue(PathMenuProperty, value);

    /// <summary>Text a button copies when clicked (a card's Copy command / Copy output), with <see cref="CopyAsync"/>.</summary>
    public static readonly AttachedProperty<string?> CopyOnClickProperty =
        AvaloniaProperty.RegisterAttached<Button, string?>("CopyOnClick", typeof(ContextMenus));

    public static string? GetCopyOnClick(Button b) => b.GetValue(CopyOnClickProperty);
    public static void SetCopyOnClick(Button b, string? value) => b.SetValue(CopyOnClickProperty, value);

    static ContextMenus()
    {
        PathMenuProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.RemoveHandler(InputElement.ContextRequestedEvent, OnPathMenuRequested);
            if (e.NewValue is string { Length: > 0 }) c.AddHandler(InputElement.ContextRequestedEvent, OnPathMenuRequested, RoutingStrategies.Tunnel);
            else c.ContextFlyout = null;
        });
        CopyOnClickProperty.Changed.AddClassHandler<Button>((b, e) =>
        {
            b.Click -= OnCopyClick;
            if (e.NewValue is not null) b.Click += OnCopyClick;
        });
    }

    /// <summary>
    /// Before anything opens a menu (tunnel, so first): the path's menu, opened here. Setting the control's
    /// ContextFlyout during the request is not enough: the flyout's own ContextRequested handler is subscribed when the
    /// property is set, after this event's route was built, so it would only open on the next right-click. The flyout is
    /// still set as the ContextFlyout so Esc (CloseOpen) and the window's tracking find it. When the path names nothing on
    /// disk the request goes on to whatever the control sits in (the row's menu).
    /// </summary>
    private static void OnPathMenuRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control c) return;
        if (GetPathMenu(c) is not { Length: > 0 } path || PathLinks.GetHandler(c) is not { } handler || handler.ResolvePath(path) is not { } target)
        {
            c.ContextFlyout = null;
            return;
        }
        var menu = ForPath(target, handler);
        c.ContextFlyout = menu;
        menu.ShowAt(c, showAtPointer: true);
        e.Handled = true;
    }

    private static void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && GetCopyOnClick(b) is { Length: > 0 } text) _ = CopyAsync(b, text);
    }

    /// <summary>Writes the text to the clipboard; tests replace it to make the clipboard fail.</summary>
    internal static Func<IClipboard, string, Task> WriteClipboard { get; set; } = (clipboard, text) => clipboard.SetTextAsync(text);
    internal static Func<IClipboard, Bitmap, Task> WriteBitmap { get; set; } = (clipboard, bitmap) => clipboard.SetBitmapAsync(bitmap);

    /// <summary>
    /// The one copy: puts <paramref name="text"/> on the clipboard (whether or not the window is active) and, once it is
    /// there, says so: a copy button (an <see cref="Icon"/> its content) shows a check for 1.5 s, anything else a brief
    /// with <paramref name="confirmation"/> (none when null). A clipboard that fails says why in a brief instead.
    /// Never throws; false when nothing was copied.
    /// </summary>
    public static Task<bool> CopyAsync(Control from, string text, string? confirmation = null) =>
        text.Length == 0 ? Task.FromResult(false) : CopyCoreAsync(from, clipboard => WriteClipboard(clipboard, text), confirmation);

    public static Task<bool> CopyImageAsync(Control from, Bitmap bitmap, string? confirmation = null, Action<string>? failure = null) =>
        CopyCoreAsync(from, clipboard => WriteBitmap(clipboard, bitmap), confirmation, failure);

    /// <summary>Decodes <paramref name="image"/> off the UI thread, then copies it as <see cref="CopyImageAsync(Control, Bitmap, string?, Action{string}?)"/>.</summary>
    internal static async Task<bool> CopyImageAsync(Control control, ImageAttachment image)
    {
        var decoded = await Task.Run(() => ImageAttachments.DecodeFull(image.Data));
        if (decoded is not { } result)
        {
            ConversationMenus.Owner(control)?.Notify("IconAlert", Loc.T("Couldn't copy"), Loc.T("This image can’t be shown: it is not an image the app can read."));
            return false;
        }
        using var bitmap = result.Bitmap;
        return await CopyImageAsync(control, bitmap, Loc.T("Copied"));
    }

    private static async Task<bool> CopyCoreAsync(Control from, Func<IClipboard, Task> write, string? confirmation, Action<string>? failure = null)
    {
        var handler = PathLinks.GetHandler(from);
        try
        {
            var clipboard = TopLevel.GetTopLevel(from)?.Clipboard ?? throw new InvalidOperationException(Loc.T("No clipboard is available."));
            await write(clipboard);
        }
        catch (Exception e) when (e is not OutOfMemoryException) // the platform's clipboard fails in its own ways (COM, X11, Wayland)
        {
            if (failure is not null) failure(e.Message);
            else handler?.Notify("IconAlert", Loc.T("Couldn't copy"), e.Message);
            return false;
        }
        if (from is Button { Content: Icon glyph })
        {
            glyph.Data = Resource("IconCheck");
            DispatcherTimer.RunOnce(() => glyph.Data = Resource("IconCopy"), TimeSpan.FromSeconds(1.5));
        }
        else if (confirmation is not null) handler?.Notify("IconCopy", confirmation, "");
        return true;
    }

    private static Geometry? Resource(string key) => Application.Current?.TryFindResource(key, out var g) == true ? g as Geometry : null;

    /// <summary>The platform's Select all keys, shown in the text fields' menu (TextBox has Cut/Copy/Paste ones only).</summary>
    public static KeyGesture? SelectAllGesture => Application.Current?.PlatformSettings?.HotkeyConfiguration.SelectAll.FirstOrDefault();

    /// <summary>
    /// A menu of <paramref name="actions"/>; a null between them draws a separator. The rows are made the first time it
    /// opens: a code block made for every streamed render then costs one object until someone right-clicks it.
    /// </summary>
    public static Flyout Menu(params MenuAction?[] actions)
    {
        var flyout = new Flyout { Placement = PlacementMode.Pointer };
        ChoiceMenu.SetCloseOnChoice(flyout, true);
        var rows = new List<(Button Row, MenuAction Action)>();
        flyout.Opening += (_, _) =>
        {
            if (flyout.Content is null)
            {
                var panel = new StackPanel { MinWidth = 200, Spacing = 2 };
                var separator = false;
                foreach (var action in actions)
                {
                    if (action is null)
                    {
                        separator = panel.Children.Count > 0;
                        continue;
                    }
                    if (separator) panel.Children.Add(new Border { Classes = { "menu-separator" } });
                    separator = false;
                    var row = Row(action);
                    row.Click += (_, _) => { if (flyout.Target is { } target) action.Run(target); };
                    rows.Add((row, action));
                    panel.Children.Add(row);
                }
                flyout.Content = panel;
            }
            foreach (var (row, action) in rows) row.IsEnabled = action.CanRun?.Invoke() ?? true;
        };
        return flyout;
    }

    private static Button Row(MenuAction action)
    {
        var icon = new Icon { Size = 16, Margin = new Thickness(0, 0, 10, 0) };
        if (Application.Current?.TryGetResource(action.Icon, null, out var data) == true) icon.Data = data as Geometry;
        var label = new TextBlock { Text = action.Label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        var row = new Button
        {
            Classes = { "menu-item" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { icon, label } },
        };
        AutomationProperties.SetName(row, action.Label);
        return row;
    }

    /// <summary>A link the user opens from a control: <see cref="MainViewModel.OpenLink(Uri, LinkTarget)"/> of the window's
    /// model (a click: the right panel's Browser tool); a window without one hands it to the system.</summary>
    public static void OpenLink(Control target, string url, LinkTarget to = LinkTarget.Panel)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || TopLevel.GetTopLevel(target) is not { } top) return;
        if (top.DataContext is MainViewModel vm) vm.OpenLink(uri, to);
        else _ = top.Launcher.LaunchUriAsync(uri);
    }

    // ───────────── Window rules ─────────────

    /// <summary>The window-wide rules (see the class summary) for every window of type <typeparamref name="T"/>; once per
    /// type, from its static constructor.</summary>
    public static void Attach<T>() where T : TopLevel
    {
        InputElement.PointerPressedEvent.AddClassHandler<T>(OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.ContextRequestedEvent.AddClassHandler<T>((_, e) => OnContextRequested(e), RoutingStrategies.Tunnel);
        InputElement.ContextRequestedEvent.AddClassHandler<T>((_, e) => OnContextOpened(e), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static WeakReference<FlyoutBase>? _open;

    /// <summary>The menu a right click (or Control+click) just opened, kept so Esc can close it wherever the keyboard is.</summary>
    private static void OnContextOpened(ContextRequestedEventArgs e)
    {
        if (e.Source is Control source
            && source.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.ContextFlyout).FirstOrDefault(f => f is { IsOpen: true }) is { } open)
            _open = new WeakReference<FlyoutBase>(open);
    }

    /// <summary>
    /// Esc closes an open right-click menu first. The keyboard stays where it was when a menu opens (a Control+click
    /// moves no focus), so the menu never sees that Esc itself, and the window would take it as "stop omp".
    /// </summary>
    public static bool CloseOpen()
    {
        if (_open is null || !_open.TryGetTarget(out var menu) || !menu.IsOpen) return false;
        _open = null;
        menu.Hide();
        return true;
    }

    /// <summary>
    /// Control+click on macOS: Avalonia passes it on as a plain left click (it would press the button, select the row,
    /// start a text selection). It opens the menu on press, like every Mac app, and nothing under it sees the click: the
    /// press is handled, and the pointer belongs to the window until the release, so the press and release make no tap
    /// on the control either (Avalonia's tap follows the pointer's capture, handled or not).
    /// </summary>
    private static void OnPressed(TopLevel window, PointerPressedEventArgs e)
    {
        if (!OperatingSystem.IsMacOS() || e.KeyModifiers != KeyModifiers.Control
            || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed || e.Source is not Control source) return;
        e.Handled = true;
        e.Pointer.Capture(window);
        source.RaiseEvent(new ContextRequestedEventArgs(e));
    }

    /// <summary>
    /// Text with nothing selected hands the request to its parent when something around it has a menu: right-clicking a
    /// message's words opens the message's menu, and the text's own "Copy" menu is for a selection.
    /// </summary>
    private static void OnContextRequested(ContextRequestedEventArgs e)
    {
        if (e.Source is SelectableTextBlock selection && selection.SelectionStart != selection.SelectionEnd
            && ConversationMenus.Owner(selection) is { } vm
            && selection.GetSelfAndVisualAncestors().OfType<Control>().Any(c => c is MarkdownView || c.DataContext is UserRowViewModel))
        {
            // Keep Avalonia's native selection/copy/select-all menu; only add the message action.
            if (selection.ContextFlyout is MenuFlyout menu && !menu.Items.OfType<MenuItem>().Any(i => i.Tag is "quote-selection"))
            {
                var quote = new MenuItem { Header = Loc.T("Quote in message"), Tag = "quote-selection", IsEnabled = !vm.IsReadOnly };
                quote.Click += (_, _) => vm.InsertMessageText("> " + (selection.SelectedText ?? "").ReplaceLineEndings("\n").Replace("\n", "\n> ") + "\n");
                menu.Items.Add(quote);
            }
            return;
        }
        if (e.Source is not SelectableTextBlock text || text.SelectionStart != text.SelectionEnd || IsPathMenu(text.ContextFlyout)
            || ConversationMenus.GetKind(text) != ConversationMenuKind.None
            || text.GetVisualParent() is not Control parent || !parent.GetSelfAndVisualAncestors().OfType<Control>().Any(c => c.ContextFlyout is not null))
            return;
        e.Handled = true;
        parent.RaiseEvent(new ContextRequestedEventArgs(e));
    }
}
