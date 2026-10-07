using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Controls;

/// <summary>
/// The browser preview panel (DataContext = <see cref="PreviewViewModel"/>). The <see cref="NativeWebView"/> (WebView2 on
/// Windows, WKWebView on macOS, WebKitGTK on Linux/X11) is created only when a page is navigated while the panel is in
/// a window; before that, and wherever the engine is missing or there is no native window (headless tests), nothing
/// native is touched and the panel shows its empty state or a card that offers the system browser.
/// </summary>
public partial class PreviewPanel : UserControl
{
    /// <summary>Whether the platform's web engine can host a page in this window, and what to tell the user if not.</summary>
    internal readonly record struct EngineProbe(bool Available, string Engine, string Detail);

    private PreviewViewModel? _vm;
    private NativeWebView? _web;
    private Uri? _pending;

    public PreviewPanel() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>The web view, once created (null until the first navigation, and where the engine is unavailable).</summary>
    internal NativeWebView? WebView => _web;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is { } old)
        {
            old.NavigationRequested -= OnNavigationRequested;
            old.ReloadRequested -= OnReloadRequested;
            old.BackRequested -= OnBackRequested;
            old.ForwardRequested -= OnForwardRequested;
            old.OpenExternallyRequested -= OnOpenExternallyRequested;
            old.PropertyChanged -= OnVmPropertyChanged;
            old.Annotations.CollectionChanged -= OnAnnotationsChanged;
            old.RunScript = null;
            old.CaptureScreenshot = null;
        }
        _vm = DataContext as PreviewViewModel;
        if (_vm is { } vm)
        {
            vm.NavigationRequested += OnNavigationRequested;
            vm.ReloadRequested += OnReloadRequested;
            vm.BackRequested += OnBackRequested;
            vm.ForwardRequested += OnForwardRequested;
            vm.OpenExternallyRequested += OnOpenExternallyRequested;
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.Annotations.CollectionChanged += OnAnnotationsChanged;
            // A page chosen while the panel was not in a window yet (or by an earlier panel).
            if (vm.CurrentUrl is { } current && _web is null) OnNavigationRequested(current);
            if (_web is not null)
            {
                vm.RunScript = RunScriptAsync;
                vm.CaptureScreenshot = CaptureScreenshotAsync;
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Realize();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible) Realize();
    }

    private void OnNavigationRequested(Uri uri)
    {
        if (_vm is null || _vm.IsEngineUnavailable) return;
        if (_web is not null)
        {
            _web.Navigate(uri);
            return;
        }
        _pending = uri;
        Realize();
    }

    private void OnReloadRequested()
    {
        if (_web is not null) _web.Refresh();
        else if (_vm?.CurrentUrl is { } uri) OnNavigationRequested(uri);
    }

    private void OnBackRequested() => _web?.GoBack();

    private void OnForwardRequested() => _web?.GoForward();

    private void OnOpenExternallyRequested(Uri uri)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) _ = launcher.LaunchUriAsync(uri);
    }

    /// <summary>Creates the web view for a pending navigation once the panel is shown in a window.</summary>
    private void Realize()
    {
        if (_pending is null || _web is not null || _vm is null || _vm.IsEngineUnavailable) return;
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not { } top)
        {
            // Hidden by a container, or not in a window yet: try again after the next layout pass (a container that
            // becomes visible lays the panel out).
            LayoutUpdated -= OnLayoutUpdatedWhilePending;
            LayoutUpdated += OnLayoutUpdatedWhilePending;
            return;
        }
        LayoutUpdated -= OnLayoutUpdatedWhilePending;
        var probe = Probe(top);
        if (!probe.Available)
        {
            Unavailable(probe.Engine, probe.Detail);
            return;
        }
        var uri = _pending;
        _pending = null;
        try
        {
            var web = new NativeWebView();
            web.NavigationStarted += OnWebNavigationStarted;
            web.NavigationCompleted += OnWebNavigationCompleted;
            web.NewWindowRequested += OnWebNewWindowRequested;
            web.WebMessageReceived += OnWebMessageReceived;
            web.PropertyChanged += (_, e) =>
            {
                // The engine turned out to be missing after all (the adapter factory reports it): same card.
                if (e.Property == NativeWebView.AdapterInfoProperty && web.AdapterInfo is DetailedWebViewAdapterInfo { IsInstalled: false } info)
                    Unavailable(probe.Engine, info.UnavailableReason ?? "");
            };
            web.Navigate(uri); // remembered until the native adapter exists, then loaded
            _web = web;
            this.FindControl<Border>("WebHost")!.Child = web;
            _vm.RunScript = RunScriptAsync; // the agent's browser (AgentBrowserBridge) runs its scripts here
            _vm.CaptureScreenshot = CaptureScreenshotAsync; // and takes its screenshots
        }
        catch (Exception e)
        {
            Unavailable(probe.Engine, e.Message);
        }
    }

    private void OnLayoutUpdatedWhilePending(object? sender, EventArgs e)
    {
        if (_pending is null || _web is not null) LayoutUpdated -= OnLayoutUpdatedWhilePending;
        else if (IsEffectivelyVisible) Realize();
    }

    private void Unavailable(string engine, string detail)
    {
        _pending = null;
        if (_web is { } web)
        {
            _web = null;
            this.FindControl<Border>("WebHost")!.Child = null;
            web.NavigationStarted -= OnWebNavigationStarted;
            web.NavigationCompleted -= OnWebNavigationCompleted;
            web.NewWindowRequested -= OnWebNewWindowRequested;
            web.WebMessageReceived -= OnWebMessageReceived;
        }
        if (_vm is { } vm)
        {
            vm.RunScript = null;
            vm.CaptureScreenshot = null;
        }
        _vm?.ReportEngineUnavailable(engine, detail);
    }

    /// <summary>A script in the page (the agent's browser): the engine's result as it came back (JSON text or a bare string).</summary>
    private async Task<string?> RunScriptAsync(string script)
    {
        if (_web is not { } web) throw new InvalidOperationException("No page is open in the preview.");
        return await web.InvokeScript(script);
    }

    /// <summary>A screenshot of the page (the agent's browser): the web view's own snapshot, no screen capture.</summary>
    private Task<OmpGui.ClientCore.AgentScreenshot> CaptureScreenshotAsync(CancellationToken ct) =>
        _web is { } web
            ? Platform.WebViewSnapshot.CaptureAsync(web.TryGetPlatformHandle(), ct)
            : throw new OmpGui.ClientCore.AgentBrowserException("no_page", "No page is open in the preview.");

    private void OnWebNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        // The page's own navigations follow the same rule as the address box: http and https (and about:blank) only.
        if (e.Request is { } r && !PreviewViewModel.IsAllowed(r) && r.Scheme != "about")
        {
            e.Cancel = true;
            return;
        }
        _vm?.OnNavigationStarted(e.Request);
    }

    private async void OnWebNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (_web is not { } web || _vm is not { } vm) return;
        vm.OnNavigationCompleted(e.Request, e.IsSuccess, web.CanGoBack, web.CanGoForward);
        if (e.IsSuccess) await InjectAnnotationsAsync(web);
        try
        {
            var raw = await web.InvokeScript("document.title");
            vm.Title = DecodeScriptString(raw);
        }
        catch (Exception)
        {
            // No title (the page is gone, or the engine refused the script): the address says enough.
        }
    }

    // ── Annotations (PreviewAnnotations.cs): the page script outlines, takes the comment and pins; the list lives here ──

    private static readonly Lazy<string> AnnotateScript = new(() =>
    {
        using var stream = typeof(PreviewPanel).Assembly.GetManifestResourceStream("preview/annotate.js")
            ?? throw new InvalidOperationException("preview/annotate.js is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The script with this panel's token, for a page just loaded (it does nothing where it already runs).</summary>
    internal static string AnnotateScriptFor(string token) =>
        AnnotateScript.Value.Replace("__OMP_TOKEN__", JsonSerializer.Serialize(token, PreviewJsonContext.Default.String), StringComparison.Ordinal);

    private async Task InjectAnnotationsAsync(NativeWebView web)
    {
        if (_vm is not { } vm) return;
        try
        {
            await web.InvokeScript(AnnotateScriptFor(vm.AnnotationToken));
            await SyncAnnotationsAsync();
        }
        catch (Exception)
        {
            // A page that refuses scripts (or went away): no comments on it; the rest of the preview works.
        }
    }

    /// <summary>The page's mode and pins follow the panel's (after a navigation, a toggle, a comment added or removed).</summary>
    private async Task SyncAnnotationsAsync()
    {
        if (_web is not { } web || _vm is not { } vm) return;
        var mode = vm.IsAnnotating ? "true" : "false";
        try
        {
            await web.InvokeScript($"window.__ompAnnotate&&(__ompAnnotate.setMode({mode}),__ompAnnotate.setPins({vm.PinsJson(vm.CurrentUrl)}))");
        }
        catch (Exception)
        {
            // No page yet, or it is loading: the next navigation completes the sync.
        }
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        var body = e.Body;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // "ready": a page (re)loaded the script on its own; an annotation: renumber its pin from the list
            if (_vm?.OnPageMessage(body) is "ready" or "annotation") _ = SyncAnnotationsAsync();
        });
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PreviewViewModel.IsAnnotating)) return;
        _ = SyncAnnotationsAsync();
        // Keys go to the page while commenting (Esc there leaves the mode)
        if (_vm?.IsAnnotating == true && _web is { } web) web.Focus();
    }

    private void OnAnnotationsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => _ = SyncAnnotationsAsync();

    /// <summary>target="_blank" links: local pages stay in the preview, the rest go to the system browser.</summary>
    private void OnWebNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.Request is not { } uri || !PreviewViewModel.IsAllowed(uri)) return;
        if (PreviewViewModel.IsLocal(uri)) _vm?.NavigateCommand.Execute(uri.AbsoluteUri);
        else OnOpenExternallyRequested(uri);
    }

    /// <summary>Engines return script results as JSON ("\"Title\""); some return the bare string.</summary>
    internal static string DecodeScriptString(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.Length >= 2 && raw[0] == '"')
        {
            try { return JsonSerializer.Deserialize(raw, PreviewJsonContext.Default.String) ?? ""; }
            catch (JsonException) { }
        }
        return raw == "null" ? "" : raw;
    }

    /// <summary>
    /// Whether a page can be embedded in <paramref name="top"/>: a real native window (not the headless platform) and
    /// the platform's engine installed. Nothing native is created here.
    /// </summary>
    internal static EngineProbe Probe(TopLevel? top)
    {
        var engine = OperatingSystem.IsWindows() ? "Microsoft Edge WebView2"
            : OperatingSystem.IsMacOS() ? "WebKit (WKWebView)"
            : "WebKitGTK (libwebkit2gtk-4.1)";
        try
        {
            if (top?.TryGetPlatformHandle() is not { Handle: not 0 } handle)
                return new(false, engine, "This window has no native surface to host a web page in.");
            if (OperatingSystem.IsLinux())
            {
                if (handle.HandleDescriptor != "XID")
                    return new(false, engine, "The embedded browser works in X11 windows only.");
                var gtk = WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebKitGtk);
                return gtk.IsInstalled
                    ? new(true, engine, "")
                    : new(false, engine, "Install the libwebkit2gtk-4.1-0 package (Debian, Ubuntu) or webkit2gtk4.1 (Fedora), then press Reload.");
            }
            if (OperatingSystem.IsWindows())
            {
                if (WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2).IsInstalled) return new(true, engine, "");
                return new(false, engine, "Install the Microsoft Edge WebView2 Runtime from microsoft.com, then press Reload.");
            }
            if (OperatingSystem.IsMacOS()) return new(true, engine, "");
            return new(false, engine, "This platform has no embedded browser.");
        }
        catch (Exception e)
        {
            return new(false, engine, e.Message);
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class PreviewJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
