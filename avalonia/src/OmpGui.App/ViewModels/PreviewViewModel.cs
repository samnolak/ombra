using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.ViewModels;

/// <summary>
/// The browser preview panel (Controls/PreviewPanel): an address, the page shown and the local addresses the agent's
/// tools printed (dev servers). The view owns the native web view; this model asks it to navigate through
/// <see cref="NavigationRequested"/> and friends, and the view reports back through <see cref="OnNavigationStarted"/> /
/// <see cref="OnNavigationCompleted"/>. Only http and https pages open.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    public const int MaxSuggestions = 5;

    /// <summary>
    /// http(s) URLs on the local machine: localhost (and *.localhost is not included on purpose), 127.0.0.1, 0.0.0.0,
    /// [::1] and [::], with an optional port and path. The host must end there (no "localhost.example.com").
    /// </summary>
    [GeneratedRegex(@"\bhttps?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1?\])(?!\.?[\w-])(?::(?<port>\d{1,5}))?(?:[/?#][^\s""'<>`\x00-\x1f\x7f]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalUrlRegex();

    /// <summary>ANSI escape sequences (colours in terminal lines, e.g. Vite's "Local: http://localhost:5173/").</summary>
    [GeneratedRegex(@"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)")]
    private static partial Regex AnsiRegex();

    /// <summary>What was typed without a scheme: a host (a name, an IPv4 address or a bracketed IPv6 one), an optional port and a path.</summary>
    [GeneratedRegex(@"^(?<host>\[[0-9a-fA-F:.]+\]|[^\s/:?#\[\]]+)(?::\d{1,5})?(?:[/?#].*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemelessRegex();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:")]
    private static partial Regex SchemeRegex();

    /// <summary>Text in the address box.</summary>
    [ObservableProperty] private string _address = "";

    /// <summary>The page shown (or being loaded); null while the panel is empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasPage), nameof(ShowEmptyState), nameof(ShowSuggestionRow), nameof(ShowFallback), nameof(ShowWebView),
        nameof(ShowCrashPage), nameof(ShowErrorPage))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand), nameof(OpenExternallyCommand), nameof(ToggleAnnotateCommand))]
    private Uri? _currentUrl;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private bool _canGoBack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ForwardCommand))]
    private bool _canGoForward;

    [ObservableProperty] private bool _isLoading;

    /// <summary>The page title, when the engine reported one.</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>Why the last address was refused (or the page failed); empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "";

    /// <summary>Set by the view when the platform's web engine cannot be used (not installed, no native window).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFallback), nameof(ShowWebView), nameof(FallbackText), nameof(ShowCrashPage), nameof(ShowErrorPage))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAnnotateCommand))]
    private bool _isEngineUnavailable;

    /// <summary>The page's web process ended (crash, killed): the crash page shows instead of the blank web view.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWebView), nameof(ShowCrashPage), nameof(ShowErrorPage))]
    private bool _isCrashed;

    /// <summary>Why the last navigation failed, as the engine said (the error page shows it); null when it did not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWebView), nameof(ShowErrorPage), nameof(ErrorTitle), nameof(ErrorDetail))]
    private BrowserLoadFailure? _loadError;

    /// <summary>The engine the platform needs ("WebKitGTK", "Microsoft Edge WebView2", "WebKit").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FallbackText))]
    private string _engineName = DefaultEngineName();

    /// <summary>More on why the engine is unavailable (the engine's own reason, an install hint).</summary>
    [ObservableProperty] private string _engineDetail = "";

    /// <summary>omp's browser tool is driving the page (AgentBrowserBridge): the panel says so until it has been idle a moment.</summary>
    [ObservableProperty] private bool _isAgentBrowsing;

    private DispatcherTimer? _agentIdle;

    /// <summary>The tabs: the user's own first (always there), then one per page omp's browser tool opened.</summary>
    public ObservableCollection<PreviewTab> Tabs { get; }

    /// <summary>The tab the pane shows; the page properties of this model (address, title, loading…) are its.</summary>
    [ObservableProperty] private PreviewTab _activeTab;

    /// <summary>The tab strip shows once omp opened a tab of its own.</summary>
    public bool ShowTabStrip => Tabs.Count > 1;

    /// <summary>The tab omp used last.</summary>
    private PreviewTab? _agentTab;

    /// <summary>
    /// The pane follows omp to the tab it opens or navigates, unless the user picked another tab; picking omp's tab
    /// again (or closing the one picked) follows omp again.
    /// </summary>
    public bool FollowsAgent { get; private set; } = true;

    /// <summary>The view navigates this tab's web view (a tab other than the active one: omp's, in the background).</summary>
    public event Action<PreviewTab, Uri>? TabNavigationRequested;

    /// <summary>A tab left the pane: the view drops its web view.</summary>
    public event Action<PreviewTab>? TabClosed;

    /// <summary>The last navigation failed (the server did not answer); reset when the next one starts.</summary>
    public bool LoadFailed
    {
        get;
        private set
        {
            field = value;
            ActiveTab.LoadFailed = value;
        }
    }

    public PreviewViewModel(BrowserSiteSettings? siteSettings = null)
    {
        SiteSettings = siteSettings ?? new BrowserSiteSettings();
        Downloads = new BrowserDownloads(SiteSettings);
        Downloads.Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDownloads));
        Tabs = [Configure(new PreviewTab("", isAgentTab: false) { IsActive = true })];
        _activeTab = Tabs[0];
        Dialogs.Follow(_activeTab.Dialogs);
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowTabStrip));
        Suggestions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSuggestions));
            OnPropertyChanged(nameof(ShowSuggestionRow));
            OnPropertyChanged(nameof(EmptyText));
        };
        Annotations.CollectionChanged += (_, _) => OnAnnotationsChanged();
        Dialogs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BrowserDialogsViewModel.CoversPage)) OnPropertyChanged(nameof(ShowWebView));
        };
    }

    /// <summary>The page's questions (sign-in, alerts, files, downloads, permissions, popups) as cards over the page.</summary>
    public BrowserDialogsViewModel Dialogs { get; } = new();

    /// <summary>"Always allow on this site" answers and the folder downloads are saved to.</summary>
    public BrowserSiteSettings SiteSettings { get; }

    /// <summary>The pane's downloads (the strip at the bottom) and where they are saved.</summary>
    public BrowserDownloads Downloads { get; }

    public bool HasDownloads => Downloads.Items.Count > 0;

    /// <summary>Local addresses found in tool output and terminal lines, most recent first (at most <see cref="MaxSuggestions"/>).</summary>
    public ObservableCollection<string> Suggestions { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 0;
    public bool IsEmpty => CurrentUrl is null;
    public bool HasPage => CurrentUrl is not null;
    public bool HasMessage => Message.Length > 0;
    public bool ShowEmptyState => IsEmpty;
    /// <summary>With a server found already, the empty page points at it instead of asking for one.</summary>
    public string EmptyText => HasSuggestions
        ? "Open the address found above, or type one"
        : "Start a dev server and its address shows up here";
    public bool ShowSuggestionRow => IsEmpty && HasSuggestions;
    public bool ShowFallback => HasPage && IsEngineUnavailable;
    /// <summary>The native web view shows; hidden while a card, the crash page or the error page covers the page (it would draw over them).</summary>
    public bool ShowWebView => HasPage && !IsEngineUnavailable && !Dialogs.CoversPage && !IsCrashed && LoadError is null;
    public bool ShowCrashPage => HasPage && !IsEngineUnavailable && IsCrashed;
    public bool ShowErrorPage => HasPage && !IsEngineUnavailable && !IsCrashed && LoadError is not null;
    public string FallbackText => $"The embedded browser needs {EngineName} on this system";

    /// <summary>The error page's title: a certificate problem, or a page that could not be opened.</summary>
    public string ErrorTitle => LoadError?.IsCertificateError == true ? "This connection isn't private" : "The page couldn't be opened";

    /// <summary>The engine's reason and the address that failed.</summary>
    public string ErrorDetail => LoadError is not { } f ? ""
        : f.Url is { } u && IsAllowed(u) ? $"{Display(u)}: {f.Message}" : f.Message;

    /// <summary>The view navigates <see cref="PreviewTab.Popup"/>-less tabs itself; pop-up tabs host the engine's own view (created by the page).</summary>
    public event Action<PreviewTab>? PopupOpened;

    /// <summary>"Show in Finder" for a finished download (the view opens its folder).</summary>
    public event Action<BrowserDownload>? ShowDownloadRequested;

    /// <summary>The view navigates its web view to this address (already checked: http or https).</summary>
    public event Action<Uri>? NavigationRequested;
    public event Action? ReloadRequested;
    public event Action? BackRequested;
    public event Action? ForwardRequested;
    /// <summary>Open this address in the system browser (the view launches it).</summary>
    public event Action<Uri>? OpenExternallyRequested;
    /// <summary>The user closed the panel (the host hides it).</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Navigates to the address box (or to <paramref name="address"/>, e.g. a suggestion chip, which also fills the
    /// box). "localhost:3000" becomes http://localhost:3000; anything other than http and https is refused.
    /// </summary>
    [RelayCommand]
    private void Navigate(string? address = null)
    {
        if (address is not null) Address = address;
        if (string.IsNullOrWhiteSpace(Address)) return;
        if (!TryNormalize(Address, out var uri, out var error))
        {
            Message = error;
            return;
        }
        Message = "";
        Address = Display(uri);
        CurrentUrl = uri;
        Title = "";
        IsLoading = true;
        LoadFailed = false;
        LoadError = null;
        IsCrashed = false;
        Dialogs.Broker.CancelAll(); // the questions of the page left behind
        NavigationRequested?.Invoke(uri);
    }

    /// <summary>The agent opens a page (omp's browser tool): like the address box; the reason when it is refused.</summary>
    public string? NavigateForAgent(string address)
    {
        Navigate(address);
        return HasMessage ? Message : null;
    }

    /// <summary>The agent used the page just now: the panel shows it for a few seconds after the last use.</summary>
    public void NoteAgentActivity()
    {
        IsAgentBrowsing = true;
        _agentIdle ??= new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) =>
        {
            _agentIdle!.Stop();
            IsAgentBrowsing = false;
        });
        _agentIdle.Stop();
        _agentIdle.Start();
    }

    // ── Tabs ──

    /// <summary>omp's Tern browser opened <paramref name="tab"/> empty (about:blank): its web view is made now, so omp
    /// can script and capture it before its first navigation.</summary>
    public void OpenBlankForAgent(PreviewTab tab)
    {
        if (tab.IsClosed || tab.Url is not null) return;
        TabNavigationRequested?.Invoke(tab, new Uri("about:blank"));
    }

    /// <summary>omp's browser tool opened a page (a cmux surface): a tab of its own, shown unless the user picked another.</summary>
    public PreviewTab OpenAgentTab(string id)
    {
        var tab = Configure(new PreviewTab(id, isAgentTab: true));
        Tabs.Add(tab);
        _agentTab = tab;
        if (FollowsAgent) ActiveTab = tab;
        return tab;
    }

    /// <summary>
    /// The engine hooks of <paramref name="tab"/>'s web view answer here: the page's questions go to the pane's cards,
    /// downloads to <see cref="Downloads"/>, pop-ups become tabs; failures and crashes show their pages.
    /// </summary>
    private PreviewTab Configure(PreviewTab tab)
    {
        var page = tab.Page;
        page.Broker = () => tab.Dialogs;
        page.AgentActive = () => tab.IsAgentActing;
        page.SiteSettings = SiteSettings;
        page.ChooseDownloadPath = (request, ct) => Downloads.ChooseDestinationAsync(request, page.Broker(), ct);
        page.OpenPopup = popup => tab.IsClosed ? null : OpenPopupTab(tab, popup).Page;
        page.Failed += failure => OnUi(() => OnNavigationFailed(tab, failure));
        page.Terminated += () => OnUi(() => OnPageCrashed(tab));
        page.DownloadChanged += download => OnUi(() => OnDownloadChanged(tab, download));
        if (tab.Popup is { } popup)
        {
            // Pop-ups have no NativeWebView: their own delegate reports the loading
            page.Started += url => OnUi(() => OnNavigationStarted(tab, url));
            page.Finished += url => OnUi(() =>
            {
                if (tab.IsClosed) return;
                OnNavigationCompleted(tab, url, true, popup.CanGoBack, popup.CanGoForward);
                if (tab == ActiveTab) Title = popup.Title;
                else tab.Title = popup.Title;
            });
            page.Closed += () => OnUi(() => RemoveTab(tab));
        }
        return tab;
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// A page in <paramref name="opener"/> opened a new window: a tab right after the opener (and its other pop-ups),
    /// shown at once when the opener is the tab on screen. It closes itself (<c>window.close()</c>), with its opener, or
    /// by the user's ×; the opener is shown again then.
    /// </summary>
    public PreviewTab OpenPopupTab(PreviewTab opener, BrowserPopup popup)
    {
        var tab = Configure(new PreviewTab("", isAgentTab: false) { Popup = popup, Opener = opener });
        if (IsAllowed(popup.RequestedUrl))
        {
            tab.Url = popup.RequestedUrl;
            tab.Address = Display(popup.RequestedUrl!);
        }
        tab.IsLoading = true;
        var at = Tabs.IndexOf(opener);
        while (at + 1 < Tabs.Count && IsPopupOf(Tabs[at + 1], opener)) at++;
        Tabs.Insert(at < 0 ? Tabs.Count : at + 1, tab);
        PopupOpened?.Invoke(tab);
        if (opener == ActiveTab) ActiveTab = tab;
        return tab;
    }

    private static bool IsPopupOf(PreviewTab tab, PreviewTab opener)
    {
        for (var o = tab.Opener; o is not null; o = o.Opener)
            if (o == opener) return true;
        return false;
    }

    /// <summary>omp shows <paramref name="tab"/> (opened or navigated it): the pane follows unless the user picked another tab.</summary>
    public void ShowForAgent(PreviewTab tab)
    {
        if (tab.IsClosed) return;
        _agentTab = tab;
        if (FollowsAgent) ActiveTab = tab;
    }

    /// <summary>omp navigates <paramref name="tab"/>: like the address box when it is the active tab; the reason when refused.</summary>
    public string? NavigateTabForAgent(PreviewTab tab, string address)
    {
        ShowForAgent(tab);
        if (tab == ActiveTab) return NavigateForAgent(address);
        if (!TryNormalize(address, out var uri, out var error)) return error;
        tab.Url = uri;
        tab.Address = Display(uri);
        tab.Title = "";
        tab.Message = "";
        tab.IsLoading = true;
        tab.LoadFailed = false;
        TabNavigationRequested?.Invoke(tab, uri);
        return null;
    }

    /// <summary>The user picks a tab; picking omp's latest tab lets the pane follow omp again.</summary>
    [RelayCommand]
    private void SelectTab(PreviewTab? tab)
    {
        if (tab is null || tab.IsClosed) return;
        ActiveTab = tab;
        FollowsAgent = tab == _agentTab || _agentTab is null;
    }

    /// <summary>The user closes one of omp's tabs or a pop-up (their own tab stays); omp's requests for it are refused from now on.</summary>
    [RelayCommand]
    private void CloseTab(PreviewTab? tab)
    {
        if (tab is null || !tab.CanClose) return;
        if (tab == ActiveTab && tab.IsAgentTab) FollowsAgent = true;
        RemoveTab(tab);
    }

    /// <summary>omp is done with <paramref name="tab"/>: it leaves the pane, unless the user is looking at it (then it stays until they close it).</summary>
    public void CloseTabForAgent(PreviewTab tab)
    {
        if (_agentTab == tab) _agentTab = null;
        if (tab == ActiveTab && Tabs.Contains(tab)) return;
        RemoveTab(tab);
    }

    private void RemoveTab(PreviewTab tab)
    {
        var i = Tabs.IndexOf(tab);
        if (i <= 0) return; // the user's own tab stays
        // Its pop-ups go with it
        foreach (var popup in Tabs.Where(t => t.Opener == tab).ToList()) RemoveTab(popup);
        i = Tabs.IndexOf(tab);
        if (tab == ActiveTab)
            ActiveTab = tab.Opener is { IsClosed: false } opener && Tabs.Contains(opener) ? opener : Tabs[i + 1 < Tabs.Count ? i + 1 : i - 1];
        Tabs.RemoveAt(i);
        tab.MarkClosed();
        if (_agentTab == tab) _agentTab = null;
        tab.Popup?.Close();
        TabClosed?.Invoke(tab);
    }

    partial void OnActiveTabChanging(PreviewTab value)
    {
        // What the toolbar showed stays with the tab left behind
        var old = ActiveTab;
        old.Address = Address;
        old.CanGoBack = CanGoBack;
        old.CanGoForward = CanGoForward;
        old.Message = Message;
        old.LoadError = LoadError;
        old.Crashed = IsCrashed;
        old.IsActive = false;
    }

    partial void OnActiveTabChanged(PreviewTab value)
    {
        value.IsActive = true;
        IsAnnotating = false;
        Dialogs.Follow(value.Dialogs); // the cards show this tab's questions; the others' wait for their tab
        CurrentUrl = value.Url;
        Address = value.Address;
        Title = value.Title;
        IsLoading = value.IsLoading;
        LoadFailed = value.LoadFailed;
        CanGoBack = value.CanGoBack;
        CanGoForward = value.CanGoForward;
        Message = value.Message;
        LoadError = value.LoadError;
        IsCrashed = value.Crashed;
    }

    // The active tab's page state is the model's: kept in step for the tab strip
    partial void OnCurrentUrlChanged(Uri? value) => ActiveTab.Url = value;
    partial void OnTitleChanged(string value) => ActiveTab.Title = value;
    partial void OnIsLoadingChanged(bool value) => ActiveTab.IsLoading = value;

    [RelayCommand(CanExecute = nameof(HasPage))]
    private void Reload()
    {
        Message = "";
        Dialogs.Broker.CancelAll();
        if (IsEngineUnavailable && CurrentUrl is { } uri)
        {
            // The engine may have been installed since: the view checks again and embeds the page, or shows the card.
            IsEngineUnavailable = false;
            IsLoading = true;
            NavigationRequested?.Invoke(uri);
            return;
        }
        // The error page's "Try again": the address that failed (the engine still holds the page before it)
        if (LoadError?.Url is { } failed && IsAllowed(failed) && !IsCrashed)
        {
            Navigate(Display(failed));
            return;
        }
        LoadError = null;
        IsCrashed = false;
        IsLoading = true;
        ReloadRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        Dialogs.Broker.CancelAll();
        BackRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Forward()
    {
        Dialogs.Broker.CancelAll();
        ForwardRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(HasPage))]
    private void OpenExternally()
    {
        if (CurrentUrl is { } uri && IsAllowed(uri)) OpenExternallyRequested?.Invoke(uri);
    }

    [RelayCommand]
    private void Close()
    {
        IsAnnotating = false;
        CloseRequested?.Invoke();
    }

    // ── Downloads (the strip at the bottom of the pane) ──

    [RelayCommand]
    private void ShowDownload(BrowserDownload? download)
    {
        if (download is { IsFinished: true, Path: not null }) ShowDownloadRequested?.Invoke(download);
    }

    [RelayCommand]
    private void CancelDownload(BrowserDownload? download) => download?.Cancel();

    [RelayCommand]
    private void ClearDownloads() => Downloads.ClearDone();

    private void OnDownloadChanged(PreviewTab tab, BrowserDownload download)
    {
        if (download.State == BrowserDownloadState.Asking && !tab.IsClosed)
        {
            // A navigation that turned into a download: the page stays where it was
            if (tab == ActiveTab)
            {
                IsLoading = false;
                if (IsAllowed(download.PageUrl))
                {
                    CurrentUrl = download.PageUrl;
                    Address = Display(download.PageUrl!);
                }
            }
            else tab.IsLoading = false;
        }
        Downloads.Track(download);
    }

    /// <summary>
    /// Finds local http(s) addresses (localhost, 127.0.0.1, 0.0.0.0, [::1]) in <paramref name="text"/> — tool output,
    /// terminal lines — and puts them first in <see cref="Suggestions"/> (an address seen before moves to the front).
    /// The first one found fills the address box while nothing is open; nothing is navigated. Any thread.
    /// </summary>
    public void OfferUrlsFrom(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf("://", StringComparison.Ordinal) < 0) return;
        var found = FindLocalUrls(text);
        if (found.Count == 0) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Offer(found));
            return;
        }
        Offer(found);
    }

    private void Offer(IReadOnlyList<string> found)
    {
        foreach (var url in found)
        {
            var i = Suggestions.IndexOf(url);
            if (i == 0) continue;
            if (i > 0) Suggestions.Move(i, 0);
            else Suggestions.Insert(0, url);
        }
        while (Suggestions.Count > MaxSuggestions) Suggestions.RemoveAt(Suggestions.Count - 1);
        if (CurrentUrl is null && string.IsNullOrWhiteSpace(Address)) Address = Suggestions[0];
    }

    /// <summary>The local addresses in <paramref name="text"/>, in the order they appear, normalised and without repeats.</summary>
    public static IReadOnlyList<string> FindLocalUrls(string text)
    {
        var result = new List<string>();
        if (text.Contains('\x1b')) text = AnsiRegex().Replace(text, "");
        foreach (Match m in LocalUrlRegex().Matches(text))
        {
            if (m.Groups["port"].Success && (!int.TryParse(m.Groups["port"].Value, out var port) || port is < 1 or > 65535)) continue;
            var raw = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\'', '"', '*', '>');
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) continue;
            var url = Display(ForBrowser(uri));
            if (!result.Contains(url)) result.Add(url);
        }
        return result;
    }

    /// <summary>
    /// Checks and completes what was typed: "localhost:3000" → http://localhost:3000, "example.com" → https://example.com.
    /// Only http and https come through; file:, javascript:, data: and the rest are refused with a reason.
    /// </summary>
    public static bool TryNormalize(string text, out Uri uri, out string error)
    {
        uri = null!;
        error = "";
        var s = text.Trim();
        if (s.Length == 0)
        {
            error = "Type an address, such as localhost:3000.";
            return false;
        }
        if (!s.Contains("://", StringComparison.Ordinal))
        {
            var m = SchemelessRegex().Match(s);
            if (m.Success)
                s = (IsLocalHost(m.Groups["host"].Value.Trim('[', ']')) ? "http://" : "https://") + s;
            else if (SchemeRegex().IsMatch(s))
            {
                error = $"Only http and https pages open in the preview (not \"{s.Split(':')[0]}:\").";
                return false;
            }
        }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var parsed) || (parsed.Scheme is "http" or "https" && parsed.Host.Length == 0))
        {
            error = "That is not a web address.";
            return false;
        }
        if (!IsAllowed(parsed))
        {
            error = $"Only http and https pages open in the preview (not \"{parsed.Scheme}:\").";
            return false;
        }
        uri = ForBrowser(parsed);
        return true;
    }

    /// <summary>Whether the preview shows <paramref name="uri"/> (http and https only; also checked for the page's own navigations).</summary>
    public static bool IsAllowed(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "http" or "https" } && uri.Host.Length > 0;

    /// <summary>Whether <paramref name="uri"/> is on this machine (a dev server rather than the web).</summary>
    public static bool IsLocal(Uri? uri) => uri is { IsAbsoluteUri: true } && IsLocalHost(uri.Host.Trim('[', ']'));

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || host is "0.0.0.0" or "::1" or "::"
        || host.StartsWith("127.", StringComparison.Ordinal);

    /// <summary>
    /// 0.0.0.0 and [::] are what a dev server listens on ("all interfaces"), not an address a browser can open (Chromium
    /// and WebView2 refuse them): they become localhost.
    /// </summary>
    private static Uri ForBrowser(Uri uri)
    {
        var host = uri.Host.Trim('[', ']');
        if (host is not ("0.0.0.0" or "::")) return uri;
        return new UriBuilder(uri) { Host = "localhost" }.Uri;
    }

    /// <summary>The address as the box shows it: no trailing slash for a bare origin.</summary>
    public static string Display(Uri uri)
    {
        var s = uri.AbsoluteUri;
        return uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && s.EndsWith('/') ? s[..^1] : s;
    }

    // ── Reported by the view ──

    /// <summary>A tab's web view started loading: the toolbar follows for the active tab, the tab keeps it otherwise.</summary>
    public void OnNavigationStarted(PreviewTab tab, Uri? uri)
    {
        if (tab == ActiveTab)
        {
            OnNavigationStarted(uri);
            return;
        }
        tab.IsLoading = true;
        tab.LoadFailed = false;
        tab.LoadError = null;
        tab.Crashed = false;
        if (IsAllowed(uri))
        {
            tab.Url = uri;
            tab.Address = Display(uri!);
        }
    }

    /// <summary>A tab's web view finished (or failed) loading.</summary>
    public void OnNavigationCompleted(PreviewTab tab, Uri? uri, bool success, bool canGoBack, bool canGoForward)
    {
        if (tab == ActiveTab)
        {
            OnNavigationCompleted(uri, success, canGoBack, canGoForward);
            return;
        }
        tab.IsLoading = false;
        tab.LoadFailed = !success;
        tab.CanGoBack = canGoBack;
        tab.CanGoForward = canGoForward;
        if (success)
        {
            tab.LoadError = null;
            tab.Crashed = false;
        }
        if (IsAllowed(uri))
        {
            tab.Url = uri;
            tab.Address = Display(uri!);
        }
        tab.Message = success ? "" : $"{(uri is null ? "The page" : Display(uri))} did not load. Is the server running?";
    }

    /// <summary>The web view started loading <paramref name="uri"/> (also for links clicked in the page).</summary>
    public void OnNavigationStarted(Uri? uri)
    {
        IsLoading = true;
        LoadFailed = false;
        LoadError = null;
        IsCrashed = false;
        if (IsAllowed(uri))
        {
            CurrentUrl = uri;
            Address = Display(uri!);
        }
    }

    /// <summary>The web view finished (or failed) loading.</summary>
    public void OnNavigationCompleted(Uri? uri, bool success, bool canGoBack, bool canGoForward)
    {
        IsLoading = false;
        LoadFailed = !success;
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
        if (IsAllowed(uri))
        {
            CurrentUrl = uri;
            Address = Display(uri!);
        }
        if (success)
        {
            LoadError = null;
            IsCrashed = false;
        }
        Message = success ? "" : $"{(uri is null ? "The page" : Display(uri))} did not load. Is the server running?";
    }

    /// <summary>
    /// A tab's navigation failed as the engine reported it (macOS): the error page says why, with Try again; the page
    /// before stays in the engine. A failed main view shows no spinner afterwards.
    /// </summary>
    public void OnNavigationFailed(PreviewTab tab, BrowserLoadFailure failure)
    {
        if (tab.IsClosed) return;
        if (tab == ActiveTab)
        {
            IsLoading = false;
            LoadFailed = true;
            Message = "";
            LoadError = failure;
            return;
        }
        tab.IsLoading = false;
        tab.LoadFailed = true;
        tab.Message = "";
        tab.LoadError = failure;
    }

    /// <summary>A tab's web process ended: the crash page (Reload, Open in browser) replaces the blank page; its questions were cancelled.</summary>
    public void OnPageCrashed(PreviewTab tab)
    {
        if (tab.IsClosed) return;
        if (tab == ActiveTab)
        {
            IsLoading = false;
            IsCrashed = true;
            return;
        }
        tab.IsLoading = false;
        tab.Crashed = true;
    }

    /// <summary>The platform's web engine cannot be used: the panel shows a card with "Open in browser" instead.</summary>
    public void ReportEngineUnavailable(string engineName, string detail)
    {
        EngineName = engineName;
        EngineDetail = detail;
        IsEngineUnavailable = true;
        IsLoading = false;
    }

    private static string DefaultEngineName() =>
        OperatingSystem.IsWindows() ? "Microsoft Edge WebView2"
        : OperatingSystem.IsMacOS() ? "WebKit (WKWebView)"
        : "WebKitGTK (libwebkit2gtk-4.1)";
}
