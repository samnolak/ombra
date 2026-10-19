using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using Ombra.App.Localization;

namespace Ombra.App.Controls;

internal static class TerminalMenus
{
    internal static void Attach(TerminalControl control)
    {
        control.ContextFlyout = ContextMenus.Menu(
            new MenuAction(Loc.T("Copy selection"), "IconCopy", c => _ = ContextMenus.CopyAsync(c, Selection(control)),
                () => control.Terminal?.Selection.HasSelection == true),
            new MenuAction(Loc.T("Copy all"), "IconCopy", c => _ = ContextMenus.CopyAsync(c, AllText(control)),
                () => control.Terminal is not null),
            new MenuAction(Loc.T("Paste"), "IconPaste", c => { _ = PasteAsync(control); }, () => control.IsLive),
            new MenuAction(Loc.T("Clear"), "IconClose", _ => control.ClearScreen()),
            null,
            new MenuAction(Loc.T("Open selected link"), "IconExternal", c =>
            {
                if (SelectedUrl(control) is { } uri) ContextMenus.OpenLink(c, uri.AbsoluteUri);
            }, () => SelectedUrl(control) is not null));
        // The vendor's right-click pastes immediately. Intercept it before the terminal's input handler;
        // a context-menu gesture must never send text to a shell.
        control.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(control).Properties.IsRightButtonPressed) return;
            if (control.ContextFlyout is Flyout menu) menu.ShowAt(control, showAtPointer: true);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        control.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Right) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        control.Loaded += (_, _) =>
        {
            foreach (var view in control.GetVisualDescendants().OfType<TerminalView>())
            {
                view.UrlClicked -= OnUrlClicked;
                view.UrlClicked += OnUrlClicked;
            }
        };
    }

    internal static string AllText(TerminalControl control) => control.Terminal is { } terminal
        ? string.Join('\n', TerminalText.Tail(terminal, control.BufferSize)) : "";
    private static string Selection(TerminalControl control) => control.Terminal?.Selection.GetSelectionText() ?? "";
    private static Uri? SelectedUrl(TerminalControl control) =>
        Uri.TryCreate(Selection(control).Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;

    private static void OnUrlClicked(object? sender, UrlClickedEventArgs e)
    {
        if (sender is Control control && Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            ContextMenus.OpenLink(control, uri.AbsoluteUri);
    }

    private static async Task PasteAsync(TerminalControl control)
    {
        try { await control.PasteAsync(); }
        catch (Exception ex) { PathLinks.GetHandler(control)?.Notify("IconAlert", Loc.T("Couldn't paste"), ex.Message); }
    }
}
