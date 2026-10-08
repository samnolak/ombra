using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using OmpGui.App.Platform.Mac;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.ViewModels;

/// <summary>
/// The preview pane as omp's Tern daemon (<see cref="TernHost"/>): every tab omp opens is a pane tab of its own
/// (<see cref="PreviewViewModel.OpenAgentTab"/>), driven through its WKWebView (<see cref="TernPreviewPage"/>). Where
/// the pane cannot show pages (no engine, no window, not macOS) an open answers <c>no_window</c> and omp uses its own
/// browser.
/// </summary>
internal sealed class TernPreviewHost(MainViewModel main) : ITernBrowser
{
    /// <summary>How long an open waits for the pane to make the tab's web view.</summary>
    private static readonly TimeSpan NativeWait = TimeSpan.FromSeconds(10);

    public async Task<ITernPage> OpenAsync(TernOpenRequest request, TernBlock block, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS())
            throw new TernException(TernException.NoWindow, "The OMP GUI browser serves omp's tabs on macOS only for now.");
        var tab = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var p = main.Preview;
            if (p.IsEngineUnavailable) throw new TernException(TernException.NoWindow, Unavailable(p));
            p.NoteAgentActivity();
            main.IsPreviewOpen = true;
            var tab = p.OpenAgentTab("tern-" + block.Id);
            tab.NavigationFilter = block.AllowsNavigation;
            p.OpenBlankForAgent(tab);
            return tab;
        });
        IPlatformHandle handle;
        try
        {
            handle = await NativeHandleAsync(tab, ct);
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => main.Preview.CloseTabForAgent(tab));
            throw;
        }
        return await Dispatcher.UIThread.InvokeAsync(() => OperatingSystem.IsMacOS() && handle is IAppleWKWebViewPlatformHandle apple
            ? NewPage(tab, block, apple.WKWebView)
            : throw new TernException(TernException.NoWindow, "The preview's web engine is not WebKit."));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private ITernPage NewPage(PreviewTab tab, TernBlock block, nint wkWebView) => new TernPreviewPage(main, tab, block, wkWebView);

    /// <summary>The tab's web view once the pane made it; <c>no_window</c> when it cannot (no engine, no window).</summary>
    private async Task<IPlatformHandle> NativeHandleAsync(PreviewTab tab, CancellationToken ct)
    {
        var made = new TaskCompletionSource<IPlatformHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (tab.NativeHandle is { } h) made.TrySetResult(h);
            else if (main.Preview.IsEngineUnavailable) made.TrySetException(new TernException(TernException.NoWindow, Unavailable(main.Preview)));
            else if (tab.IsClosed) made.TrySetException(new TernException(TernException.WindowClosed, "The user closed the tab."));
        }
        void OnNative(PreviewTab _) => Check();
        void OnPreview(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PreviewViewModel.IsEngineUnavailable)) Check();
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            tab.NativeChanged += OnNative;
            main.Preview.PropertyChanged += OnPreview;
            Check();
        });
        try
        {
            return await made.Task.WaitAsync(NativeWait, ct);
        }
        catch (TimeoutException)
        {
            throw new TernException(TernException.NoWindow, "The OMP GUI browser pane could not show the tab (is the window open?).");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                tab.NativeChanged -= OnNative;
                main.Preview.PropertyChanged -= OnPreview;
            });
        }
    }

    /// <summary>The system clipboard (writing needs no permission; omp never reads it here).</summary>
    public async Task WriteClipboardAsync(string text, CancellationToken ct)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (window?.Clipboard is not { } clipboard) throw new TernException(TernException.NoWindow, "The OMP GUI window is not open.");
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
        });
    }

    internal static string Unavailable(PreviewViewModel p) =>
        p.EngineDetail.Length > 0 ? $"{p.FallbackText}. {p.EngineDetail}" : p.FallbackText + ".";
}

/// <summary>
/// One of omp's Tern tabs in the preview pane: <see cref="TernWebKitPage"/> over the tab's WKWebView, with the pane
/// following — navigation through the pane (its toolbar, its rules), omp's viewport as the web view's size, captures of
/// a background tab shown for the moment, and on close the tab leaves the pane (or stays for the user looking at it,
/// without omp's scripts).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class TernPreviewPage : TernWebKitPage
{
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(300);
    private readonly MainViewModel _main;
    private readonly PreviewTab _tab;

    public TernPreviewPage(MainViewModel main, PreviewTab tab, TernBlock block, nint wkWebView)
        : base(wkWebView, block, tab.Page, tab.Dialogs, tab.Input)
    {
        _main = main;
        _tab = tab;
        // The web view went (the pane dropped it): omp's requests for the tab are refused from now on
        tab.NativeChanged += OnNativeChanged;
    }

    public override bool IsClosed => base.IsClosed || _tab.IsClosed;

    private void OnNativeChanged(PreviewTab tab)
    {
        if (tab.NativeHandle is not IAppleWKWebViewPlatformHandle apple || apple.WKWebView != WebView) Release();
    }

    /// <summary>omp acts on the page: what it asks meanwhile is omp's (the tab's hooks read it), and the pane says so.</summary>
    public override IDisposable BeginAgentAction()
    {
        Dispatcher.UIThread.Post(_main.Preview.NoteAgentActivity);
        return _tab.AgentAction();
    }

    public override async Task NavigateAsync(Uri url, CancellationToken ct)
    {
        if (url.Scheme == "about")
        {
            await base.NavigateAsync(url, ct);
            return;
        }
        var refused = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _main.IsPreviewOpen = true;
            return _tab.IsClosed ? "The user closed this tab." : _main.Preview.NavigateTabForAgent(_tab, url.AbsoluteUri);
        });
        if (refused is not null) throw new TernException(TernException.Invalid, refused);
    }

    public override async Task SetViewportAsync(int width, int height, CancellationToken ct)
    {
        await Dispatcher.UIThread.InvokeAsync(() => _tab.Viewport = new PixelSize(width, height));
        // Laid out before the answer, so omp's next state reads the new size
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
    }

    /// <summary>The web view draws only while the pane shows it: a background tab is shown for the capture, then the user's pick comes back.</summary>
    public override async Task<byte[]> CaptureAsync(TernCaptureRequest request, CancellationToken ct)
    {
        var (wait, restore) = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var p = _main.Preview;
            var closed = !_main.IsPreviewOpen;
            _main.IsPreviewOpen = true;
            var previous = p.ActiveTab;
            if (previous == _tab || _tab.IsClosed) return (closed, (PreviewTab?)null);
            var follows = p.FollowsAgent;
            p.ShowForAgent(_tab);
            if (!follows) p.ActiveTab = _tab;
            return (true, follows ? null : previous);
        });
        try
        {
            if (wait) await Task.Delay(ShowDelay, ct);
            return await base.CaptureAsync(request, ct);
        }
        finally
        {
            if (restore is not null)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_main.Preview.ActiveTab == _tab && !restore.IsClosed) _main.Preview.ActiveTab = restore;
                });
        }
    }

    public override async Task CloseAsync(CancellationToken ct) =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Release();
            _main.Preview.CloseTabForAgent(_tab);
        });

    /// <summary>omp's part leaves the tab (its scripts, its size, its host rules). UI thread.</summary>
    private void Release()
    {
        _tab.NativeChanged -= OnNativeChanged;
        _tab.NavigationFilter = null;
        _tab.Viewport = null;
        Detach();
    }
}
