using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
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
    /// <summary>The active tab's web view (null while it has none).</summary>
    private NativeWebView? _web;
    /// <summary>Every tab's web view; the inactive ones stay alive (hidden), so omp's tabs keep their pages.</summary>
    private readonly Dictionary<PreviewTab, NativeWebView> _webs = [];
    /// <summary>Navigations waiting for the panel to be in a window (the web view is created then).</summary>
    private readonly Dictionary<PreviewTab, Uri> _pending = [];
    /// <summary>Pop-up tabs: the engine's own web view of each, hosted as it is.</summary>
    private readonly Dictionary<PreviewTab, PopupViewHost> _popups = [];

    public PreviewPanel()
    {
        InitializeComponent();
        SideScrollStrip.Attach(this.FindControl<ScrollViewer>("TabScroller")!);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>The active tab's web view, once created (null until its first navigation, and where the engine is unavailable).</summary>
    internal NativeWebView? WebView => _web;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is { } old)
        {
            Uncover(old);
            old.NavigationRequested -= OnNavigationRequested;
            old.TabNavigationRequested -= NavigateTab;
            old.TabClosed -= OnTabClosed;
            old.ReloadRequested -= OnReloadRequested;
            old.BackRequested -= OnBackRequested;
            old.ForwardRequested -= OnForwardRequested;
            old.OpenExternallyRequested -= OnOpenExternallyRequested;
            old.PropertyChanged -= OnVmPropertyChanged;
            old.Annotations.CollectionChanged -= OnAnnotationsChanged;
            old.AnnotationEdited -= OnAnnotationEdited;
            foreach (var t in old.Tabs)
            {
                t.RunScript = null;
                t.CaptureScreenshot = null;
            }
            old.Dialogs.PropertyChanged -= OnDialogsPropertyChanged;
            old.Dialogs.CaptureSnapshot = null;
            old.Dialogs.PickFiles = null;
            old.PopupOpened -= HostPopup;
            old.ShowDownloadRequested -= OnShowDownloadRequested;
            old.Downloads.PickSaveLocation = null;
        }
        _vm = DataContext as PreviewViewModel;
        if (_vm is { } vm)
        {
            vm.NavigationRequested += OnNavigationRequested;
            vm.TabNavigationRequested += NavigateTab;
            vm.TabClosed += OnTabClosed;
            vm.ReloadRequested += OnReloadRequested;
            vm.BackRequested += OnBackRequested;
            vm.ForwardRequested += OnForwardRequested;
            vm.OpenExternallyRequested += OnOpenExternallyRequested;
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.Annotations.CollectionChanged += OnAnnotationsChanged;
            vm.AnnotationEdited += OnAnnotationEdited;
            vm.Dialogs.PropertyChanged += OnDialogsPropertyChanged;
            vm.Dialogs.PickFiles = PickFilesAsync;
            vm.Dialogs.CaptureSnapshot = CaptureDialogSnapshotAsync; // the page behind a card (null while no web view)
            vm.PopupOpened += HostPopup;
            vm.ShowDownloadRequested += OnShowDownloadRequested;
            vm.Downloads.PickSaveLocation = PickSaveLocationAsync;
            if (vm.Dialogs.Card is not null) OnDialogCardShown();
            // Pages chosen while the panel was not in a window yet (or by an earlier panel).
            foreach (var t in vm.Tabs)
            {
                if (t.IsPopup) HostPopup(t);
                else if (_webs.TryGetValue(t, out var web)) Connect(t, web);
                else if ((t == vm.ActiveTab ? vm.CurrentUrl : t.Url) is { } current) NavigateTab(t, current);
            }
            ShowActiveTab();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _popupWatch ??= Popup.IsOpenProperty.Changed.AddClassHandler<Popup>(OnPopupIsOpenChanged);
        Realize();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _popupWatch?.Dispose();
        _popupWatch = null;
        _openPopups.Clear();
        LayoutUpdated -= OnLayoutWithPopups;
        Uncover(_vm);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible) Realize();
    }

    private void OnNavigationRequested(Uri uri)
    {
        if (_vm is { } vm) NavigateTab(vm.ActiveTab, uri);
    }

    /// <summary>Loads <paramref name="uri"/> in <paramref name="tab"/>'s web view (created once the panel is in a window).</summary>
    private void NavigateTab(PreviewTab tab, Uri uri)
    {
        if (_vm is null || _vm.IsEngineUnavailable || tab.IsClosed) return;
        if (tab.Popup is { } popup)
        {
            popup.Navigate(uri);
            return;
        }
        if (_webs.TryGetValue(tab, out var web))
        {
            web.Navigate(uri);
            return;
        }
        _pending[tab] = uri;
        Realize();
    }

    private void OnReloadRequested()
    {
        if (_vm?.ActiveTab.Popup is { } popup) popup.Reload();
        else if (_web is not null) _web.Refresh();
        else if (_vm?.CurrentUrl is { } uri) OnNavigationRequested(uri);
    }

    private void OnBackRequested()
    {
        if (_vm?.ActiveTab.Popup is { } popup) popup.GoBack();
        else _web?.GoBack();
    }

    private void OnForwardRequested()
    {
        if (_vm?.ActiveTab.Popup is { } popup) popup.GoForward();
        else _web?.GoForward();
    }

    private void OnOpenExternallyRequested(Uri uri)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) _ = launcher.LaunchUriAsync(uri);
    }

    /// <summary>Creates the web views for pending navigations once the panel is shown in a window.</summary>
    private void Realize()
    {
        if (_pending.Count == 0 || _vm is null || _vm.IsEngineUnavailable) return;
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
        foreach (var (tab, uri) in _pending.ToList())
        {
            _pending.Remove(tab);
            if (tab.IsClosed) continue;
            if (_webs.TryGetValue(tab, out var existing)) existing.Navigate(uri);
            else if (!CreateWebView(tab, uri, probe)) return;
        }
    }

    /// <summary>The web view of <paramref name="tab"/>, loading <paramref name="uri"/>; false when the engine failed (the card shows).</summary>
    private bool CreateWebView(PreviewTab tab, Uri uri, EngineProbe probe)
    {
        try
        {
            var web = new NativeWebView();
            web.EnvironmentRequested += OnWebEnvironmentRequested;
            web.NavigationStarted += OnWebNavigationStarted;
            web.NavigationCompleted += OnWebNavigationCompleted;
            web.NewWindowRequested += OnWebNewWindowRequested;
            web.WebMessageReceived += OnWebMessageReceived;
            web.AdapterCreated += OnWebAdapterCreated; // native hooks: the page's questions and events (Platform/BrowserHooks.cs)
            web.AdapterDestroyed += OnWebAdapterDestroyed;
            web.PropertyChanged += (_, e) =>
            {
                // The engine turned out to be missing after all (the adapter factory reports it): same card.
                if (e.Property == NativeWebView.AdapterInfoProperty && web.AdapterInfo is DetailedWebViewAdapterInfo { IsInstalled: false } info)
                    Unavailable(probe.Engine, info.UnavailableReason ?? "");
            };
            web.Navigate(uri); // remembered until the native adapter exists, then loaded
            web.IsVisible = tab == _vm?.ActiveTab;
            _webs[tab] = web;
            this.FindControl<Panel>("WebHost")!.Children.Add(web);
            Connect(tab, web);
            // omp's viewport (Tern): at most that size, top-left; the pane's size when none
            ApplyViewport(tab, web);
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PreviewTab.Viewport)) ApplyViewport(tab, web);
            };
            ShowActiveTab();
            return true;
        }
        catch (Exception e)
        {
            Unavailable(probe.Engine, e.Message);
            return false;
        }
    }

    /// <summary>The agent's browser (AgentBrowserBridge) runs its scripts in the tab's web view and takes its screenshots.</summary>
    private static void Connect(PreviewTab tab, NativeWebView web)
    {
        tab.RunScript = script => RunScriptAsync(web, script);
        tab.CaptureScreenshot = ct => CaptureScreenshotAsync(web, ct);
    }

    /// <summary>
    /// The page's size: the one omp asked for its tab (top-left, Tern), else the device chosen in the toolbar (centred,
    /// a little below the toolbar), else the pane's. A pane smaller than either limits it.
    /// </summary>
    private void ApplyViewport(PreviewTab tab, NativeWebView web)
    {
        var omp = tab.Viewport;
        var size = omp ?? _vm?.DeviceViewport;
        web.MaxWidth = size?.Width ?? double.PositiveInfinity;
        web.MaxHeight = size?.Height ?? double.PositiveInfinity;
        web.HorizontalAlignment = size is null ? Avalonia.Layout.HorizontalAlignment.Stretch
            : omp is null ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Left;
        web.VerticalAlignment = size is null ? Avalonia.Layout.VerticalAlignment.Stretch : Avalonia.Layout.VerticalAlignment.Top;
        web.Margin = size is not null && omp is null ? new Thickness(0, 12, 0, 0) : default;
    }

    /// <summary>The device's user agent on a web view (where the engine takes one); a page already shown loads again with it.</summary>
    private void ApplyUserAgent(PreviewTab tab, NativeWebView web)
    {
        if (_vm is not { } vm || !Platform.PageUserAgent.Set(web.TryGetPlatformHandle(), vm.DeviceUserAgent)) return;
        if (tab.Url is not null) Avalonia.Threading.Dispatcher.UIThread.Post(() => web.Refresh(), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>The active tab's web view shows (the others stay alive, hidden); the toolbar and the cards follow it.</summary>
    private void ShowActiveTab()
    {
        if (_vm is not { } vm) return;
        _web = _webs.GetValueOrDefault(vm.ActiveTab);
        foreach (var (tab, web) in _webs) web.IsVisible = tab == vm.ActiveTab;
        foreach (var (tab, host) in _popups) host.IsVisible = tab == vm.ActiveTab;
        _ = SyncAnnotationsAsync();
        RevealActiveTab();
    }

    /// <summary>The active tab's place in the strip is shown (it can be scrolled out of view when there are many).</summary>
    private void RevealActiveTab() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_vm?.ActiveTab is not { } active) return;
            var items = this.FindControl<ItemsControl>("TabItems")!;
            if (items.ContainerFromItem(active) is { } container) container.BringIntoView();
        }, Avalonia.Threading.DispatcherPriority.Loaded);

    private void OnLayoutUpdatedWhilePending(object? sender, EventArgs e)
    {
        if (_pending.Count == 0) LayoutUpdated -= OnLayoutUpdatedWhilePending;
        else if (IsEffectivelyVisible) Realize();
    }

    /// <summary>A tab left the pane: its web view goes.</summary>
    private void OnTabClosed(PreviewTab tab)
    {
        _pending.Remove(tab);
        if (_popups.Remove(tab, out var host)) this.FindControl<Panel>("WebHost")!.Children.Remove(host);
        if (!_webs.Remove(tab, out var web))
        {
            ShowActiveTab();
            return;
        }
        Drop(web);
        if (_web == web) _web = null;
        ShowActiveTab();
    }

    /// <summary>
    /// A page opened a pop-up: its web view (the engine's, built with the opener's configuration) is hosted as the
    /// tab's page; the agent's browser can drive it like any tab.
    /// </summary>
    private void HostPopup(PreviewTab tab)
    {
        if (tab.Popup is not { IsClosed: false } popup || _popups.ContainsKey(tab)) return;
        var host = new PopupViewHost(popup) { IsVisible = tab == _vm?.ActiveTab };
        _popups[tab] = host;
        this.FindControl<Panel>("WebHost")!.Children.Add(host);
        tab.RunScript = popup.RunScriptAsync;
        tab.CaptureScreenshot = ct => Platform.WebViewSnapshot.CaptureAsync(host.Handle, ct);
        var (input, isolated) = Platform.NativeInputs.For(host.Handle); // the agent's input and helper world (B4)
        tab.SetNative(host.Handle, input, isolated);
        ShowActiveTab();
    }

    private void Drop(NativeWebView web)
    {
        this.FindControl<Panel>("WebHost")!.Children.Remove(web);
        web.EnvironmentRequested -= OnWebEnvironmentRequested;
        web.NavigationStarted -= OnWebNavigationStarted;
        web.NavigationCompleted -= OnWebNavigationCompleted;
        web.NewWindowRequested -= OnWebNewWindowRequested;
        web.WebMessageReceived -= OnWebMessageReceived;
        web.AdapterCreated -= OnWebAdapterCreated;
        web.AdapterDestroyed -= OnWebAdapterDestroyed;
        DetachNativeHooks(web);
    }

    private void Unavailable(string engine, string detail)
    {
        _pending.Clear();
        _web = null;
        foreach (var (tab, web) in _webs)
        {
            Drop(web);
            tab.RunScript = null;
            tab.CaptureScreenshot = null;
            tab.SetNative(null, null, null);
        }
        _webs.Clear();
        _vm?.ReportEngineUnavailable(engine, detail);
    }

    /// <summary>The tab whose web view raised an event.</summary>
    private PreviewTab? TabOf(object? sender)
    {
        foreach (var (tab, web) in _webs)
            if (ReferenceEquals(web, sender)) return tab;
        return null;
    }

    // ── Native hooks (Platform/BrowserHooks.cs): the page's questions go to the tab's BrowserPageHooks (cards, downloads, pop-ups) ──

    private readonly Dictionary<NativeWebView, Task<IDisposable?>> _nativeHooks = [];

    /// <summary>macOS: Safari's user agent (WKWebView's own lacks the "Version/… Safari/…" part sites look for).</summary>
    private static void OnWebEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is AppleWKWebViewEnvironmentRequestedEventArgs apple && Platform.BrowserHooks.ApplicationNameForUserAgent is { } name)
            apple.ApplicationNameForUserAgent = name;
    }

    private void OnWebAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        if (sender is not NativeWebView web || TabOf(web) is not { } tab) return;
        DetachNativeHooks(web);
        var handle = e.TryGetPlatformHandle();
        _nativeHooks[web] = Platform.BrowserHooks.AttachAsync(handle, tab.Page);
        var (input, isolated) = Platform.NativeInputs.For(handle); // the agent's input and helper world (B4)
        tab.SetNative(handle, input, isolated);
        ApplyUserAgent(tab, web);
    }

    private void OnWebAdapterDestroyed(object? sender, WebViewAdapterEventArgs e)
    {
        if (sender is not NativeWebView web) return;
        DetachNativeHooks(web);
        TabOf(web)?.SetNative(null, null, null);
    }

    /// <summary>Removes the hooks; what the page still asks gets the engine's default answer.</summary>
    private void DetachNativeHooks(NativeWebView web)
    {
        if (_nativeHooks.Remove(web, out var attach)) _ = Platform.BrowserHooks.DetachAsync(attach);
    }

    /// <summary>The engine hooks of <paramref name="web"/> are in place (they open pop-ups as tabs).</summary>
    private bool HasNativeHooks(NativeWebView web) =>
        _nativeHooks.TryGetValue(web, out var attach) && attach.IsCompletedSuccessfully && attach.Result is not null;

    /// <summary>A script in a tab's page (the agent's browser): the engine's result as it came back (JSON text or a bare string).</summary>
    private static async Task<string?> RunScriptAsync(NativeWebView web, string script) => await web.InvokeScript(script);

    /// <summary>A screenshot of a tab's page (the agent's browser): the web view's own snapshot, no screen capture.</summary>
    private static Task<OmpGui.ClientCore.AgentScreenshot> CaptureScreenshotAsync(NativeWebView web, CancellationToken ct) =>
        Platform.WebViewSnapshot.CaptureAsync(web.TryGetPlatformHandle(), ct);

    private void OnWebNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        // The page's own navigations follow the same rule as the address box: http and https (and about:blank) only.
        if (e.Request is { } r && !PreviewViewModel.IsAllowed(r) && r.Scheme != "about")
        {
            e.Cancel = true;
            return;
        }
        if (TabOf(sender) is not { } tab) return;
        // omp's allowed hosts (Tern allow): a refused page is not loaded, and omp hears of it
        if (e.Request is { } next && tab.NavigationFilter?.Invoke(next) == false)
        {
            e.Cancel = true;
            return;
        }
        _vm?.OnNavigationStarted(tab, e.Request);
    }

    private async void OnWebNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (sender is not NativeWebView web || TabOf(web) is not { } tab || _vm is not { } vm) return;
        vm.OnNavigationCompleted(tab, e.Request, e.IsSuccess, web.CanGoBack, web.CanGoForward);
        if (e.IsSuccess) tab.Page.RaiseFinished(e.Request);
        if (e.IsSuccess) await InjectAnnotationsAsync(web);
        try
        {
            var title = DecodeScriptString(await web.InvokeScript("document.title"));
            if (tab == vm.ActiveTab) vm.Title = title;
            else tab.Title = title;
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
            // "ready": a page (re)loaded the script on its own; an annotation: renumber its pin from the list (a change or
            // a removal syncs through AnnotationEdited / the list)
            if (_vm?.OnPageMessage(body) is "ready" or "annotation") _ = SyncAnnotationsAsync();
        });
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PreviewViewModel.ActiveTab):
                ShowActiveTab();
                return;
            case nameof(PreviewViewModel.DeviceViewport):
                foreach (var (t, w) in _webs) ApplyViewport(t, w);
                return;
            case nameof(PreviewViewModel.DeviceUserAgent):
                foreach (var (t, w) in _webs) ApplyUserAgent(t, w);
                return;
            case not nameof(PreviewViewModel.IsAnnotating):
                return;
        }
        _ = SyncAnnotationsAsync();
        // Keys go to the page while commenting (Esc there leaves the mode)
        if (_vm?.IsAnnotating == true && _web is { } web) web.Focus();
    }

    private void OnAnnotationsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => _ = SyncAnnotationsAsync();

    private void OnAnnotationEdited() => _ = SyncAnnotationsAsync();

    /// <summary>
    /// target="_blank" links. With the engine hooks (macOS), http(s) links go on to the engine, which opens them as a
    /// pop-up tab keeping <c>window.opener</c>; otherwise local pages stay in the preview and the rest go to the system
    /// browser.
    /// </summary>
    private void OnWebNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e)
    {
        if (sender is NativeWebView web && OperatingSystem.IsMacOS() && HasNativeHooks(web) && PreviewViewModel.IsAllowed(e.Request))
        {
            e.Handled = false;
            return;
        }
        e.Handled = true;
        if (e.Request is not { } uri || !PreviewViewModel.IsAllowed(uri)) return;
        if (!PreviewViewModel.IsLocal(uri)) OnOpenExternallyRequested(uri);
        else if (_vm is { } vm && TabOf(sender) is { } tab && tab != vm.ActiveTab) _webs[tab].Navigate(uri); // a tab in the background
        else _vm?.NavigateCommand.Execute(uri.AbsoluteUri);
    }

    // ── Cards for the page's questions (BrowserDialogsViewModel over DialogBroker) ──

    /// <summary>The page as a picture for the card's background: the engine's own snapshot (no screen capture).</summary>
    private async Task<Avalonia.Media.Imaging.Bitmap?> CaptureDialogSnapshotAsync(CancellationToken ct)
    {
        var handle = _vm?.ActiveTab is { } active && _popups.TryGetValue(active, out var host) ? host.Handle : _web?.TryGetPlatformHandle();
        if (handle is null) return null;
        var shot = await Platform.WebViewSnapshot.CaptureAsync(handle, ct);
        using var png = new MemoryStream(shot.Png, writable: false);
        return new Avalonia.Media.Imaging.Bitmap(png);
    }

    // ── Pop-ups of the window over the page (tooltips, menus, flyouts) ──
    // macOS draws pop-ups inside the window (OverlayPopups, Program.cs), and the native web view draws over everything
    // Avalonia draws there: a toolbar button's tooltip would show cut off by the page. While a pop-up lies over the
    // page, the page shows as the engine's snapshot taken when it opened (as behind the browser cards), then comes back.

    /// <summary>How long the page's picture may take; slower, the pop-up stays partly under the page.</summary>
    private static readonly TimeSpan PopupSnapshotTimeout = TimeSpan.FromMilliseconds(400);
    private IDisposable? _popupWatch;
    private readonly HashSet<Popup> _openPopups = [];
    /// <summary>The picture being taken or shown for the pop-ups now over the page; null while the page is live.</summary>
    private CancellationTokenSource? _cover;

    private void OnPopupIsOpenChanged(Popup popup, AvaloniaPropertyChangedEventArgs e)
    {
        if (popup.IsOpen) _openPopups.Add(popup);
        else _openPopups.Remove(popup);
        // Where a pop-up lands is known after layout (and it can move with its target): look after each pass while any is open
        LayoutUpdated -= OnLayoutWithPopups;
        if (_openPopups.Count > 0) LayoutUpdated += OnLayoutWithPopups;
        CheckPopupsOverPage();
    }

    private void OnLayoutWithPopups(object? sender, EventArgs e) => CheckPopupsOverPage();

    private void CheckPopupsOverPage()
    {
        if (_vm is not { } vm) return;
        var top = TopLevel.GetTopLevel(this);
        var page = top is not null && this.FindControl<Panel>("PageArea") is { } area ? BoundsIn(area, top) : null;
        var over = page is { } p && _openPopups.Any(x => x.IsOpen && x.IsUsingOverlayLayer && x.Child is { IsEffectivelyVisible: true } c
            && TopLevel.GetTopLevel(c) == top && BoundsIn(c, top!) is { } r && r.Intersects(p));
        if (over) Cover(vm);
        else Uncover(vm);
    }

    private static Rect? BoundsIn(Visual v, Visual root) => v.TranslatePoint(default, root) is { } at ? new Rect(at, v.Bounds.Size) : null;

    private void Cover(PreviewViewModel vm)
    {
        if (_cover is not null || !vm.ShowWebView) return;
        var view = vm.ActiveTab is { } active && _popups.TryGetValue(active, out var host) ? (Control)host : _web;
        if (view is null) return;
        var cts = _cover = new CancellationTokenSource(PopupSnapshotTimeout);
        _ = CoverAsync(vm, view, cts);
    }

    private async Task CoverAsync(PreviewViewModel vm, Control view, CancellationTokenSource cts)
    {
        Avalonia.Media.Imaging.Bitmap? picture = null;
        try { picture = await CaptureDialogSnapshotAsync(cts.Token); }
        catch (Exception)
        {
            // No picture (engine busy, too slow): the page stays live and the pop-up partly under it.
        }
        if (_cover != cts || _vm != vm || picture is null)
        {
            picture?.Dispose();
            return;
        }
        // The picture where the view is: its size (omp's viewport can make it smaller than the pane), top-left
        var image = this.FindControl<Image>("PageUnderPopup")!;
        image.Width = view.Bounds.Width;
        image.Height = view.Bounds.Height;
        image.Margin = new Thickness(view.Bounds.X, view.Bounds.Y, 0, 0); // a device's page is centred
        vm.PageUnderPopup = picture;
    }

    private void Uncover(PreviewViewModel? vm)
    {
        _cover?.Cancel();
        _cover = null;
        if (vm?.PageUnderPopup is not { } old) return;
        vm.PageUnderPopup = null;
        old.Dispose();
    }

    /// <summary>A download card's "Save" (no folder chosen yet) or "Save as…": the system's save panel, which asks before replacing a file.</summary>
    private async Task<string?> PickSaveLocationAsync(string fileName, string? folder)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var options = new FilePickerSaveOptions { SuggestedFileName = fileName, ShowOverwritePrompt = true };
        if (folder is not null && Directory.Exists(folder)) options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(folder);
        var file = await storage.SaveFilePickerAsync(options);
        return file?.TryGetLocalPath();
    }

    /// <summary>"Show in Finder": the folder of a saved download (the user asked for it).</summary>
    private void OnShowDownloadRequested(OmpGui.ClientCore.Browser.BrowserDownload download)
    {
        if (download.Path is not { } path || Path.GetDirectoryName(path) is not { Length: > 0 } folder) return;
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) _ = launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
    }

    /// <summary>A file card's "Choose…": the system's picker (the user asked for it), local paths only.</summary>
    private async Task<IReadOnlyList<string>?> PickFilesAsync(OmpGui.ClientCore.Browser.FileChooserRequest request)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        if (request.AllowDirectories)
        {
            var folders = await storage.OpenFolderPickerAsync(new() { AllowMultiple = request.Multiple });
            return [.. folders.Select(f => f.TryGetLocalPath()).OfType<string>()];
        }
        var patterns = request.Accept.Where(a => a.StartsWith('.')).Select(a => "*" + a).ToList();
        var mimes = request.Accept.Where(a => a.Contains('/')).ToList();
        var options = new Avalonia.Platform.Storage.FilePickerOpenOptions { AllowMultiple = request.Multiple };
        if (patterns.Count > 0 || mimes.Count > 0)
            options.FileTypeFilter = [new("Accepted files") { Patterns = patterns.Count > 0 ? patterns : null, MimeTypes = mimes.Count > 0 ? mimes : null }];
        var files = await storage.OpenFilePickerAsync(options);
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    private void OnDialogsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserDialogsViewModel.Card) && _vm?.Dialogs.Card is not null) OnDialogCardShown();
    }

    /// <summary>A card opened (or the next one in line): its mark, and the keyboard in its first field (Enter answers, Esc cancels).</summary>
    private void OnDialogCardShown() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_vm?.Dialogs.Card is not { } card) return;
            // The card's own named parts (template parts inside its text boxes and buttons share names like PART_…)
            var parts = this.GetVisualDescendants().OfType<Control>()
                .Where(c => c.DataContext == card && c.Name?.StartsWith("BrowserDialog", StringComparison.Ordinal) == true)
                .DistinctBy(c => c.Name).ToDictionary(c => c.Name!, c => c);
            if (parts.GetValueOrDefault("BrowserDialogIcon") is Icon icon && this.TryFindResource(IconFor(card.Kind), out var data))
                icon.Data = data as Avalonia.Media.Geometry;
            var target = parts.GetValueOrDefault(card.FirstFocus switch
            {
                "UserName" => "BrowserDialogUser",
                "Password" => "BrowserDialogPassword",
                "Prompt" => "BrowserDialogPrompt",
                _ => card.HasTertiary ? "BrowserDialogPrimaryStacked" : "BrowserDialogPrimary",
            });
            target?.Focus(Avalonia.Input.NavigationMethod.Tab);
        }, Avalonia.Threading.DispatcherPriority.Loaded);

    private static string IconFor(OmpGui.ClientCore.Browser.BrowserDialogKind kind) => kind switch
    {
        OmpGui.ClientCore.Browser.BrowserDialogKind.Credentials => "IconKey",
        OmpGui.ClientCore.Browser.BrowserDialogKind.FileChooser => "IconFile",
        OmpGui.ClientCore.Browser.BrowserDialogKind.Download => "IconDownload",
        OmpGui.ClientCore.Browser.BrowserDialogKind.Permission => "IconShield",
        OmpGui.ClientCore.Browser.BrowserDialogKind.PopupNotice => "IconExternal",
        OmpGui.ClientCore.Browser.BrowserDialogKind.BeforeUnload => "IconAlert",
        _ => "IconGlobe",
    };

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
