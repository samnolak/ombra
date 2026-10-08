namespace OmpGui.ClientCore.Browser;

/// <summary>
/// One web view of the browser pane as the engine hooks see it (macOS: <c>Platform/Mac/WebKitHooks</c>): what they ask
/// the app (inputs, read at the moment the page asks) and what they report (events). The app makes one per pane tab
/// (<c>PreviewTab.Page</c>) and one per pop-up; the hooks of that tab's web view are attached with it. Engine-neutral
/// and free of UI types, so the windowless harness and headless tests drive it the same way.
/// </summary>
/// <remarks>
/// Events are raised on the thread the engine calls back on (the main thread, which is the UI thread on macOS). A
/// handler that throws never reaches the engine: the exception is dropped after the other handlers ran.
/// </remarks>
public sealed class BrowserPageHooks : IBrowserAuthHandler
{
    /// <summary>Who answers the page's questions (sign-in, alert/confirm/prompt, beforeunload, files, download, permission). Null: the engine's own default answer.</summary>
    public Func<DialogBroker?> Broker { get; set; } = static () => null;

    /// <summary>Whether omp's browser tool is acting on this page right now (its input, script or navigation): the page's questions are then <see cref="BrowserDialogRequest.CausedByAgent"/>.</summary>
    public Func<bool> AgentActive { get; set; } = static () => false;

    /// <summary>Sign-in requests go here instead of <see cref="Broker"/> when set (tools and tests that answer them directly).</summary>
    public IBrowserAuthHandler? Auth { get; set; }

    /// <summary>
    /// Where a download is saved: a full file path, or null to not download. Asked once per download, after the engine
    /// knows the file's name. Default: nothing is downloaded (no folder is written without someone choosing it).
    /// </summary>
    public Func<DownloadRequest, CancellationToken, Task<string?>> ChooseDownloadPath { get; set; } =
        static (_, _) => Task.FromResult<string?>(null);

    /// <summary>
    /// A page opened a new window (<c>window.open</c>, <c>target=_blank</c>): the app shows <paramref name="popup"/>
    /// (a tab in the pane) and returns the pop-up's own hooks; null refuses it (the page's <c>window.open</c> returns null).
    /// Called synchronously while the engine waits.
    /// </summary>
    public Func<BrowserPopup, BrowserPageHooks?>? OpenPopup { get; set; }

    /// <summary>Remembered per-site permission answers ("Always allow on this site"); null: always ask.</summary>
    public BrowserSiteSettings? SiteSettings { get; set; }

    /// <summary>A navigation started (provisional: nothing of the new page is shown yet).</summary>
    public event Action<Uri?>? Started;

    /// <summary>The new page's content started arriving (the address bar shows its address now).</summary>
    public event Action<Uri?>? Committed;

    /// <summary>The page finished loading (main views: from the app's own navigation-completed event; pop-ups: from their delegate).</summary>
    public event Action<Uri?>? Finished;

    /// <summary>A navigation failed (cancelled navigations and the one a download interrupts are not reported).</summary>
    public event Action<BrowserLoadFailure>? Failed;

    /// <summary>A frame's response arrived (before its policy is decided).</summary>
    public event Action<BrowserResponse>? Response;

    /// <summary>The page's web content process ended (crash or killed): the page is blank until reloaded.</summary>
    public event Action? Terminated;

    /// <summary>The page closed its own window (<c>window.close()</c> in a pop-up).</summary>
    public event Action? Closed;

    /// <summary>A download started or changed state (asking, saving, finished, failed, canceled).</summary>
    public event Action<BrowserDownload>? DownloadChanged;

    Task<CredentialAnswer?> IBrowserAuthHandler.RequestCredentialsAsync(CredentialRequest request, CancellationToken ct) =>
        Auth?.RequestCredentialsAsync(request, ct)
        ?? Broker()?.RequestCredentialsAsync(request, ct)
        ?? Task.FromResult<CredentialAnswer?>(null);

    public void RaiseStarted(Uri? url) => Raise(Started, url);
    public void RaiseCommitted(Uri? url) => Raise(Committed, url);
    public void RaiseFinished(Uri? url) => Raise(Finished, url);
    public void RaiseFailed(BrowserLoadFailure failure) => Raise(Failed, failure);
    public void RaiseResponse(BrowserResponse response) => Raise(Response, response);
    public void RaiseDownloadChanged(BrowserDownload download) => Raise(DownloadChanged, download);

    public void RaiseTerminated()
    {
        // Questions of a page that is gone: answered as nobody-answered (the engine dropped them anyway)
        try
        {
            Broker()?.CancelAll();
        }
        catch (Exception)
        {
            // the broker is gone with the pane
        }
        Raise(Terminated);
    }

    public void RaiseClosed() => Raise(Closed);

    private static void Raise(Action? handlers)
    {
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try
            {
                ((Action)h)();
            }
            catch (Exception)
            {
                // nothing may unwind into the engine
            }
        }
    }

    private static void Raise<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)h)(value);
            }
            catch (Exception)
            {
                // nothing may unwind into the engine
            }
        }
    }
}

/// <summary>A navigation that failed: the engine's error domain, code and message.</summary>
/// <param name="Provisional">It failed before anything of the new page was shown (server unreachable, TLS, refused).</param>
public sealed record BrowserLoadFailure(Uri? Url, string Domain, long Code, string Message, bool Provisional)
{
    /// <summary>The server's certificate was not accepted (NSURLErrorDomain -1200…-1206, -2000).</summary>
    public bool IsCertificateError => Domain == "NSURLErrorDomain" && (Code is >= -1206 and <= -1200 || Code == -2000);

    /// <summary>
    /// Not a failure the user needs to see: a navigation the page or the user cancelled (NSURLErrorCancelled -999),
    /// or the one a download replaced (WebKitErrorDomain 102, frame load interrupted by a policy change).
    /// </summary>
    public static bool IsIgnored(string? domain, long code) =>
        (domain == "NSURLErrorDomain" && code == -999) || (domain == "WebKitErrorDomain" && code == 102);
}

/// <summary>A frame's response as the engine reports it before deciding whether to show or download it.</summary>
public sealed record BrowserResponse(Uri? Url, int Status, string? MimeType, IReadOnlyDictionary<string, string> Headers,
    bool IsMainFrame, bool Download);

/// <summary>
/// A new window a page opened, built by the engine with the opener's configuration so <c>window.opener</c> works
/// (OAuth sign-in pop-ups post their result to it, then close themselves). The app hosts its native view
/// (<see cref="NativeHandle"/>, an <c>NSView*</c> on macOS) as a tab, and drives it through the members below.
/// Main thread only.
/// </summary>
public abstract class BrowserPopup
{
    /// <summary>The native view to host (<see cref="HandleDescriptor"/> says what it is).</summary>
    public abstract nint NativeHandle { get; }

    /// <summary>"NSView" on macOS.</summary>
    public abstract string HandleDescriptor { get; }

    /// <summary>The address the opener asked for (the engine loads it itself), if any.</summary>
    public Uri? RequestedUrl { get; init; }

    public abstract Uri? Url { get; }
    public abstract string Title { get; }
    public abstract bool CanGoBack { get; }
    public abstract bool CanGoForward { get; }
    public abstract bool IsClosed { get; }

    public abstract void Navigate(Uri url);
    public abstract void Reload();
    public abstract void GoBack();
    public abstract void GoForward();

    /// <summary>Runs a script in the pop-up's page: a string result as it is, anything else as JSON, null for undefined/null.</summary>
    public abstract Task<string?> RunScriptAsync(string script);

    /// <summary>Tears the pop-up down (the user closed its tab, or its opener went): pending questions get their default answer.</summary>
    public abstract void Close();
}
