using System.Runtime.Versioning;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// The hooks of one WKWebView beyond sign-in (BROWSER_PLAN B2, <c>browser-native.md</c> §3): our own
/// <c>WKUIDelegate</c> (Avalonia sets none) for alert/confirm/prompt/beforeunload, pop-ups, <c>window.close</c>, file
/// inputs and camera/microphone requests; selectors added to the class of the view's navigation delegate for loading
/// events, failures, web-process crashes, response policy and downloads (<see cref="InstallNavigationExtras"/>). The
/// page's questions go to <see cref="BrowserPageHooks.Broker"/>; events to <see cref="BrowserPageHooks"/>.
/// </summary>
/// <remarks>
/// IMPs run on the main thread and find their page by the <c>webView</c> argument. Every completion handler WebKit
/// gives us is copied (<see cref="MacPendingBlock"/>), answered exactly once, and answered with WebKit's
/// no-delegate default when the page goes (<see cref="Dispose"/>), the web process dies, or the broker cancels.
/// No Avalonia types: the windowless harness compiles this folder.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed partial class WebKitPage : IDisposable
{
    private const long InactiveSchedulingPolicyNone = 2; // WKInactiveSchedulingPolicyNone (macOS 14+)

    private static readonly Lock Gate = new();
    private static readonly Dictionary<nint, WebKitPage> Pages = [];

    private readonly List<MacPendingBlock> _pending = [];
    private nint _uiDelegate;
    private bool _disposed;

    private WebKitPage(nint webView, BrowserPageHooks hooks)
    {
        WebView = webView;
        Hooks = hooks;
    }

    public nint WebView { get; }
    public BrowserPageHooks Hooks { get; }

    /// <summary>What the last <see cref="Install"/> could not add (null: everything is in place).</summary>
    public string? Problem { get; private set; }

    public static WebKitPage? Find(nint webView)
    {
        lock (Gate) return Pages.GetValueOrDefault(webView);
    }

    /// <summary>
    /// Installs the UI delegate and the navigation extras on <paramref name="webView"/>; replaces (and closes) the page
    /// state an earlier attach left. Main thread.
    /// </summary>
    public static WebKitPage Install(nint webView, BrowserPageHooks hooks)
    {
        var page = new WebKitPage(webView, hooks);
        WebKitPage? previous;
        lock (Gate)
        {
            Pages.Remove(webView, out previous);
            Pages[webView] = page;
        }
        previous?.Dispose();
        var problems = new List<string>();
        if (!InstallNavigationExtras(webView, problems)) problems.Add("navigation extras not installed");
        page._uiDelegate = MacObjC.Send(MacObjC.Send(UiDelegateClass, "alloc"), "init");
        MacObjC.SendVoid(webView, "setUIDelegate:", page._uiDelegate); // weak: we keep the +1 until Dispose
        page.Problem = problems.Count == 0 ? null : string.Join("; ", problems);
        return page;
    }

    /// <summary>The page's own address (<c>WKWebView.URL</c>).</summary>
    public Uri? Url => MacObjC.ToUri(MacObjC.Send(WebView, "URL"));

    /// <summary>Hidden pages keep running timers and messages (an opener waiting for its pop-up's result, an agent's tab in the background).</summary>
    public void KeepRunningWhenHidden()
    {
        var prefs = MacObjC.Send(MacObjC.Send(WebView, "configuration"), "preferences");
        if (MacObjC.RespondsTo(prefs, "setInactiveSchedulingPolicy:")) MacObjC.SendVoidLong(prefs, "setInactiveSchedulingPolicy:", InactiveSchedulingPolicyNone);
    }

    /// <summary>Keeps <paramref name="pending"/> until it is answered; false (and the default answer) once the page is gone.</summary>
    private bool Track(MacPendingBlock pending)
    {
        lock (_pending)
        {
            if (!_disposed)
            {
                _pending.Add(pending);
                return true;
            }
        }
        pending.Default();
        return false;
    }

    private void Untrack(MacPendingBlock pending)
    {
        lock (_pending) _pending.Remove(pending);
    }

    /// <summary>Answers every handler still waiting with WebKit's default (the web process died, or the page is going).</summary>
    public int AnswerPendingWithDefaults()
    {
        MacPendingBlock[] all;
        lock (_pending)
        {
            all = [.. _pending];
            _pending.Clear();
        }
        var n = 0;
        foreach (var p in all)
            if (p.Default()) n++;
        return n;
    }

    /// <summary>
    /// Asks <paramref name="question"/> and answers <paramref name="pending"/> with its result on the main thread
    /// (<paramref name="answer"/>), or with the default when it fails or the page went meanwhile.
    /// </summary>
    private void Answer<T>(Task<T> question, MacPendingBlock pending, Action<nint, T> answer)
    {
        if (!Track(pending)) return;
        question.ContinueWith(t =>
        {
            Untrack(pending);
            if (t.IsCompletedSuccessfully) pending.Complete(b => answer(b, t.Result));
            else pending.Default();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>The broker that answers now; null means WebKit's own default answer.</summary>
    private DialogBroker? Broker()
    {
        try
        {
            return Hooks.Broker();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool AgentActive()
    {
        try
        {
            return Hooks.AgentActive();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (Pages.TryGetValue(WebView, out var current) && current == this) Pages.Remove(WebView);
        }
        lock (_pending) _disposed = true;
        AnswerPendingWithDefaults();
        if (_uiDelegate == 0) return;
        var ui = _uiDelegate;
        _uiDelegate = 0;
        MacObjC.OnMainThread(() =>
        {
            // Only ours: someone may have set another since
            if (MacObjC.Send(WebView, "UIDelegate") == ui) MacObjC.SendVoid(WebView, "setUIDelegate:", 0);
            MacObjC.Release(ui);
        });
    }
}
