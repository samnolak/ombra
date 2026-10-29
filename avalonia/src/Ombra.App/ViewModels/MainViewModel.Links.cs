using CommunityToolkit.Mvvm.ComponentModel;

namespace Ombra.App.ViewModels;

/// <summary>Where a link the user opens from Ombra goes (<see cref="MainViewModel.OpenLink(Uri, LinkTarget)"/>).</summary>
public enum LinkTarget
{
    /// <summary>A click: the right panel's Browser tool, next to the chat (a tab of its own).</summary>
    Panel,
    /// <summary>⌘-click or the link's menu: a new tab of the rail's Browser page.</summary>
    BrowserPage,
    /// <summary>The link's menu, and a sign-in's "Open in default browser" (passkeys, a company's SSO): the system's default browser.</summary>
    SystemBrowser,
    /// <summary>
    /// A sign-in page (omp's provider login, a connector's OAuth): in Ombra, where its redirect to the local callback
    /// (http on this computer) completes too. A tab of the right panel's Browser tool next to the chat; over Settings,
    /// Ombra's sign-in window (<see cref="MainViewModel.SignInBrowser"/>).
    /// </summary>
    SignIn,
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

    /// <summary>
    /// Ombra's sign-in window's browser while the window is open (MainWindow.SignIn.cs shows it, and closes it when
    /// this goes back to null): sign-ins started with no chat on screen (Settings). Built-in preview or Nightly, as the
    /// Browser page.
    /// </summary>
    [ObservableProperty] private PreviewViewModel? _signInBrowser;

    /// <summary>The sign-in window should show (made for <see cref="SignInBrowser"/>, or brought to the front).</summary>
    public event Action? SignInWindowRequested;

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
            case LinkTarget.SignIn:
                OpenSignInPage(uri);
                return;
            case LinkTarget.Panel when !Preview.IsEngineUnavailable:
                if (IsSettingsOpen || IsBrowserPageOpen || IsTeamPageOpen || IsEditorPageOpen) ShowChatsCommand.Execute(null);
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

    /// <summary>
    /// A sign-in page (<see cref="LinkTarget.SignIn"/>): over Settings in the sign-in window, else in a tab of the right
    /// panel's Browser tool (the Browser page and the Team page give way to the chat). Where it opened, to close its tab
    /// once the sign-in is done; null when it went to the system's browser (no engine to show it) or was refused.
    /// </summary>
    private (PreviewViewModel Browser, PreviewTab Tab)? OpenSignInPage(Uri uri)
    {
        var browser = IsSettingsOpen ? SignInBrowser ?? NewUserBrowser() : Preview;
        if (browser.IsEngineUnavailable)
        {
            OpenUrlRequested?.Invoke(uri.AbsoluteUri);
            return null;
        }
        var window = IsSettingsOpen;
        if (window) SignInBrowser = browser;
        else
        {
            if (IsBrowserPageOpen || IsTeamPageOpen || IsEditorPageOpen) ShowChatsCommand.Execute(null);
            IsPreviewOpen = true;
        }
        var refused = browser.OpenUserTab(uri);
        if (window) SignInWindowRequested?.Invoke();
        if (refused is not null)
        {
            Notify("IconAlert", Localization.Loc.T("Couldn't open the link"), refused);
            return null;
        }
        return (browser, browser.ActiveTab);
    }

    /// <summary>
    /// A sign-in is done: its page leaves (the sign-in window closes with its last tab). The pane's first tab stays,
    /// showing the callback's page.
    /// </summary>
    private void CloseSignInPage((PreviewViewModel Browser, PreviewTab Tab) page)
    {
        if (page.Tab.IsClosed) return;
        if (ReferenceEquals(page.Browser, SignInBrowser) && page.Browser.Tabs.Count == 1) SignInBrowser = null;
        else page.Browser.CloseTabCommand.Execute(page.Tab);
    }
}
