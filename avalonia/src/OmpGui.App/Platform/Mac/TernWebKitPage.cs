using System.Runtime.Versioning;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// A Tern tab (<see cref="ITernPage"/>) over one <c>WKWebView</c>: scripts, captures, identity and cookies through
/// <see cref="TernWebKit"/>, trusted input through <see cref="INativeInput"/>, navigation events from the engine hooks
/// (<see cref="BrowserPageHooks"/>, via <see cref="TernPageEvents"/>), the page's questions in <see cref="Dialogs"/>.
/// The windowless harness uses it as it is; the app's pane tab overrides navigation, viewport, capture and close so the
/// pane follows (the pane shows the tab, sizes its web view, keeps it for the user).
/// </summary>
/// <remarks>No Avalonia types: the windowless harness compiles this folder.</remarks>
[SupportedOSPlatform("macos")]
internal class TernWebKitPage : ITernPage
{
    /// <summary>How long after omp's last action what the page asks is still taken as caused by omp.</summary>
    private static readonly TimeSpan ActionGrace = TimeSpan.FromSeconds(1);

    private readonly TernWebKit _engine;
    private readonly TernPageEvents _events;
    private readonly INativeInput? _input;
    private int _acting;
    private long _lastActionEnd;
    private int _closed;

    /// <param name="wkWebView">The <c>WKWebView*</c> (main thread to create).</param>
    /// <param name="hooks">The engine hooks attached to that web view.</param>
    /// <param name="dialogs">Where its questions go (the tab's broker).</param>
    /// <param name="input">Trusted input for that web view; null where there is none.</param>
    public TernWebKitPage(nint wkWebView, TernBlock block, BrowserPageHooks hooks, DialogBroker? dialogs, INativeInput? input)
    {
        WebView = wkWebView;
        Dialogs = dialogs;
        _input = input;
        _engine = TernWebKit.Attach(wkWebView, block);
        _events = new TernPageEvents(hooks, block, _engine.ForgetFrames);
    }

    public nint WebView { get; }

    public DialogBroker? Dialogs { get; }

    public virtual bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>omp's action runs now or ended less than a second ago.</summary>
    public bool IsAgentActing =>
        Volatile.Read(ref _acting) > 0 || Environment.TickCount64 - Interlocked.Read(ref _lastActionEnd) < ActionGrace.TotalMilliseconds;

    public virtual IDisposable BeginAgentAction()
    {
        Interlocked.Increment(ref _acting);
        return new ActionScope(this);
    }

    private sealed class ActionScope(TernWebKitPage page) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            Interlocked.Exchange(ref page._lastActionEnd, Environment.TickCount64);
            Interlocked.Decrement(ref page._acting);
        }
    }

    protected TernWebKit Engine => _engine;

    public virtual Task NavigateAsync(Uri url, CancellationToken ct) => _engine.NavigateAsync(url);

    public virtual Task HistoryAsync(TernHistory go, CancellationToken ct) => _engine.HistoryAsync(go);

    public virtual Task<TernPageState> GetStateAsync(CancellationToken ct) => _engine.GetStateAsync();

    public Task<string?> EvaluateAsync(string function, string argsJson, TernWorld world, string? frame, CancellationToken ct) =>
        _engine.EvaluateAsync(function, argsJson, world, frame, ct);

    public async Task InputAsync(IReadOnlyList<NativeInputStep> steps, CancellationToken ct)
    {
        if (_input is null) throw new TernException(TernException.Unsupported, "Trusted input is not available for this web view.");
        try
        {
            await _input.SendAsync(steps, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException e)
        {
            throw new TernException(TernException.Unsupported, e.Message);
        }
    }

    public virtual Task<byte[]> CaptureAsync(TernCaptureRequest request, CancellationToken ct) => _engine.CaptureAsync(request, ct);

    public Task<byte[]> PdfAsync(CancellationToken ct) => _engine.PdfAsync(ct);

    public virtual Task SetViewportAsync(int width, int height, CancellationToken ct) => _engine.SetFrameSizeAsync(width, height);

    public Task SetScriptsAsync(IReadOnlyList<TernScript> scripts, CancellationToken ct) => _engine.SetScriptsAsync(scripts);

    public Task SetUserAgentAsync(string? userAgent, CancellationToken ct) => _engine.SetUserAgentAsync(userAgent);

    public Task SetAppearanceAsync(string? scheme, CancellationToken ct) => _engine.SetAppearanceAsync(scheme);

    public Task<IReadOnlyList<TernCookie>> GetCookiesAsync(CancellationToken ct) => _engine.GetCookiesAsync(ct);

    public Task SetCookieAsync(TernCookie cookie, CancellationToken ct) => _engine.SetCookieAsync(cookie, ct);

    public Task DeleteCookieAsync(string name, string domain, string path, CancellationToken ct) => _engine.DeleteCookieAsync(name, domain, path, ct);

    public Task CopyAsync(CancellationToken ct) => _engine.CopyAsync();

    /// <summary>omp is done with the tab: its scripts, handlers and observers leave the web view.</summary>
    public virtual Task CloseAsync(CancellationToken ct)
    {
        Detach();
        return Task.CompletedTask;
    }

    /// <summary>Removes what omp added to the web view (idempotent).</summary>
    protected void Detach()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _events.Dispose();
        _engine.Dispose();
    }
}
