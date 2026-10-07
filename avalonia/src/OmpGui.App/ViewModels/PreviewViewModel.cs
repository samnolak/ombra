using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasPage), nameof(ShowEmptyState), nameof(ShowSuggestionRow), nameof(ShowFallback), nameof(ShowWebView))]
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
    [NotifyPropertyChangedFor(nameof(ShowFallback), nameof(ShowWebView), nameof(FallbackText))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAnnotateCommand))]
    private bool _isEngineUnavailable;

    /// <summary>The engine the platform needs ("WebKitGTK", "Microsoft Edge WebView2", "WebKit").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FallbackText))]
    private string _engineName = DefaultEngineName();

    /// <summary>More on why the engine is unavailable (the engine's own reason, an install hint).</summary>
    [ObservableProperty] private string _engineDetail = "";

    /// <summary>omp's browser tool is driving the page (AgentBrowserBridge): the panel says so until it has been idle a moment.</summary>
    [ObservableProperty] private bool _isAgentBrowsing;

    private DispatcherTimer? _agentIdle;

    /// <summary>Runs a script in the page and returns the engine's result; set by the view (it throws while no page is open).</summary>
    public Func<string, Task<string?>>? RunScript { get; set; }

    /// <summary>Captures the page shown as a PNG (Platform/WebViewSnapshot); set by the view together with <see cref="RunScript"/>.
    /// Failures are <see cref="OmpGui.ClientCore.AgentBrowserException"/>s with a message for the agent.</summary>
    public Func<CancellationToken, Task<OmpGui.ClientCore.AgentScreenshot>>? CaptureScreenshot { get; set; }

    /// <summary>The last navigation failed (the server did not answer); reset when the next one starts.</summary>
    public bool LoadFailed { get; private set; }

    public PreviewViewModel()
    {
        Suggestions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSuggestions));
            OnPropertyChanged(nameof(ShowSuggestionRow));
            OnPropertyChanged(nameof(EmptyText));
        };
        Annotations.CollectionChanged += (_, _) => OnAnnotationsChanged();
    }

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
    public bool ShowWebView => HasPage && !IsEngineUnavailable;
    public string FallbackText => $"The embedded browser needs {EngineName} on this system";

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

    [RelayCommand(CanExecute = nameof(HasPage))]
    private void Reload()
    {
        Message = "";
        if (IsEngineUnavailable && CurrentUrl is { } uri)
        {
            // The engine may have been installed since: the view checks again and embeds the page, or shows the card.
            IsEngineUnavailable = false;
            IsLoading = true;
            NavigationRequested?.Invoke(uri);
            return;
        }
        IsLoading = true;
        ReloadRequested?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => BackRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Forward() => ForwardRequested?.Invoke();

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

    /// <summary>The web view started loading <paramref name="uri"/> (also for links clicked in the page).</summary>
    public void OnNavigationStarted(Uri? uri)
    {
        IsLoading = true;
        LoadFailed = false;
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
        Message = success ? "" : $"{(uri is null ? "The page" : Display(uri))} did not load. Is the server running?";
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
