using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace OmpGui.App.Views.Settings;

/// <summary>Row menus of the Connectors and Plugins and skills pages: a choice closes the menu (as the composer's menus do).</summary>
internal static class ConnectorsSettingsMenus
{
    /// <summary>
    /// Called for every click on the page: the first click on a button with a menu hooks the menu so that choosing one
    /// of its items closes it.
    /// </summary>
    public static void CloseOnChoice(object? source, HashSet<Flyout> hooked)
    {
        if (source is not Button { Flyout: Flyout { Content: Control content } flyout } || !hooked.Add(flyout)) return;
        content.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Classes: var c } && c.Contains("menu-item"))
                Dispatcher.UIThread.Post(flyout.Hide, DispatcherPriority.Background);
        });
    }

    public static void Copy(Control from, string text)
    {
        if (TopLevel.GetTopLevel(from)?.Clipboard is { } clipboard) _ = ClipboardExtensions.SetTextAsync(clipboard, text);
    }
}
