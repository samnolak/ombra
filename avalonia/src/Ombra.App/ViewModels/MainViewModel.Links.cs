namespace Ombra.App.ViewModels;

/// <summary>Where a link the user opens from Ombra goes (<see cref="MainViewModel.OpenLink(Uri, LinkTarget)"/>).</summary>
public enum LinkTarget
{
    /// <summary>A click: the right panel's Browser tool, next to the chat (a tab of its own).</summary>
    Panel,
    /// <summary>⌘-click or the link's menu: a new tab of the rail's Browser page.</summary>
    BrowserPage,
    /// <summary>The link's menu, and sign-ins: the system's default browser.</summary>
    SystemBrowser,
}

/// <summary>
/// The one way Ombra opens a link the user asked for: the conversation's links, tool output and terminal links, cards,
/// menus, settings. http/https stay in Ombra unless the user picked the system's browser; everything else (file:,
/// folders, mailto:) goes to the system as before. Opening through the system was how a click in beta.1 reached a
/// Nightly window outside Ombra: on a Mac whose default browser is Nightly, LaunchServices hands it every web link.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The Browser page's Nightly window, while Nightly shows the page (MainWindow.BrowserWindow.cs): opens a
    /// link in a new tab of it. Null with the built-in preview.</summary>
    public Action<Uri>? OpenInBrowserWindow { get; set; }

    /// <summary><paramref name="url"/> as <see cref="OpenLink(Uri, LinkTarget)"/>; nothing when it is not an absolute address.</summary>
    internal void OpenLink(string url, LinkTarget target = LinkTarget.Panel)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) OpenLink(uri, target);
    }

    /// <summary>
    /// Opens <paramref name="uri"/> where <paramref name="target"/> says. The right panel's Browser tool shows it in a
    /// tab of its own (the chat stays: settings, the Browser page and the Team page close over it), whichever engine
    /// the pane uses (Nightly, or the built-in preview with its consent card or its install banner); a pane whose
    /// engine cannot run on this system hands it to the system's browser, like the Browser page without a way to show it.
    /// </summary>
    internal void OpenLink(Uri uri, LinkTarget target = LinkTarget.Panel)
    {
        if (!PreviewViewModel.IsAllowed(uri)) target = LinkTarget.SystemBrowser;
        switch (target)
        {
            case LinkTarget.Panel when !Preview.IsEngineUnavailable:
                if (IsSettingsOpen || IsBrowserPageOpen || IsTeamPageOpen) ShowChatsCommand.Execute(null);
                IsPreviewOpen = true;
                if (Preview.OpenUserTab(uri) is { } refused) Notify("IconAlert", Localization.Loc.T("Couldn't open the link"), refused);
                return;
            case LinkTarget.BrowserPage when OpenInBrowserWindow is { } open:
                OpenBrowserPageCommand.Execute(null);
                open(uri);
                return;
            case LinkTarget.BrowserPage when !Browser.IsEngineUnavailable:
                OpenBrowserPageCommand.Execute(null);
                if (Browser.OpenUserTab(uri) is { } error) Notify("IconAlert", Localization.Loc.T("Couldn't open the link"), error);
                return;
            default:
                OpenUrlRequested?.Invoke(uri.AbsoluteUri);
                return;
        }
    }
}
