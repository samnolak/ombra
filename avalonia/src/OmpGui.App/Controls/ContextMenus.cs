using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace OmpGui.App.Controls;

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
    /// <summary>Copy code, for a code block whose text <paramref name="code"/> returns.</summary>
    public static Flyout ForCodeBlock(Func<string> code) =>
        Menu(new MenuAction("Copy code", "IconCopy", target => CopyText(target, code())));

    /// <summary>Open a link in the browser, or copy it.</summary>
    public static Flyout ForLink(string url) => Menu(
        new MenuAction("Open link", "IconExternal", target => OpenLink(target, url)),
        new MenuAction("Copy link", "IconLink", target => CopyText(target, url)));

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
                foreach (var action in actions)
                {
                    if (action is null)
                    {
                        panel.Children.Add(new Border { Classes = { "menu-separator" } });
                        continue;
                    }
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

    private static void CopyText(Control target, string text)
    {
        if (text.Length > 0 && TopLevel.GetTopLevel(target)?.Clipboard is { } clipboard) _ = clipboard.SetTextAsync(text);
    }

    private static void OpenLink(Control target, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && TopLevel.GetTopLevel(target)?.Launcher is { } launcher)
            _ = launcher.LaunchUriAsync(uri);
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
        if (e.Source is not SelectableTextBlock text || text.SelectionStart != text.SelectionEnd
            || text.GetVisualParent() is not Control parent || !parent.GetSelfAndVisualAncestors().OfType<Control>().Any(c => c.ContextFlyout is not null))
            return;
        e.Handled = true;
        parent.RaiseEvent(new ContextRequestedEventArgs(e));
    }
}
