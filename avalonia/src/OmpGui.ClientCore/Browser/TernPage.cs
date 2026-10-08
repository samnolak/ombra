namespace OmpGui.ClientCore.Browser;

/// <summary>
/// A Tern protocol error answered to omp: <c>{"error":{"kind","message"}}</c>. Kinds omp knows (wire.ts):
/// <c>invalid</c>, <c>not_found</c>, <c>not_agent</c>, <c>no_window</c>, <c>window_closed</c>, <c>unsupported</c>,
/// <c>js</c> (a page exception; the message is its text, first line <c>Error: …</c>), <c>failed</c>.
/// <c>no_window</c> and <c>unsupported</c> during an open make omp fall back to its own Chromium.
/// </summary>
public sealed class TernException(string kind, string message) : Exception(message)
{
    public const string Invalid = "invalid";
    public const string NotFound = "not_found";
    public const string NoWindow = "no_window";
    public const string WindowClosed = "window_closed";
    public const string Unsupported = "unsupported";
    public const string Js = "js";
    public const string Failed = "failed";

    public string Kind { get; } = kind;
}

/// <summary>Which JavaScript world a Tern <c>eval</c> or script runs in: the page's own, or omp's isolated one.</summary>
public enum TernWorld
{
    Page,
    Isolated,
}

/// <summary>A Tern <c>nav</c> step.</summary>
public enum TernHistory
{
    Back,
    Forward,
    Reload,
}

/// <summary>A document-start script for future documents (Tern <c>scripts</c>).</summary>
/// <param name="AllFrames">Every frame; false = the main frame only.</param>
/// <param name="AtStart">At document start; false = at document end.</param>
public sealed record TernScript(string Source, TernWorld World, bool AllFrames, bool AtStart);

/// <summary>A rectangle in the main frame's viewport, CSS pixels.</summary>
public readonly record struct TernRect(double X, double Y, double Width, double Height);

/// <summary>A Tern <c>capture</c>: the viewport, <see cref="Rect"/>, or the whole page, scaled by <see cref="Scale"/>
/// (pixels per CSS pixel), PNG or JPEG.</summary>
public sealed record TernCaptureRequest(double Scale, bool Jpeg, int Quality, TernRect? Rect, bool FullPage);

/// <summary>What Tern <c>state</c> answers.</summary>
/// <param name="Width">Viewport width, CSS pixels.</param>
public readonly record struct TernPageState(string Url, string Title, int Width, int Height, bool Loading, bool CanGoBack, bool CanGoForward);

/// <summary>A cookie as Tern carries it: <see cref="Expires"/> in seconds since 1970, -1 for a session cookie;
/// <see cref="SameSite"/> <c>Strict</c>, <c>Lax</c>, <c>None</c> or null.</summary>
public sealed record TernCookie(string Name, string Value, string Domain, string Path, double Expires, bool HttpOnly, bool Secure, string? SameSite);

/// <summary>omp's <c>open</c>: a tab for pane <paramref name="Owner"/> (the <c>TERN_PANE</c> the app gave that omp),
/// with the viewport omp asked for (a hint: the tab takes the pane's size until omp sets a viewport).</summary>
public sealed record TernOpenRequest(int Owner, int Width, int Height);

/// <summary>Where a tab's own events go (navigation, title, frames, page messages): its <see cref="TernBlock"/>. Any thread.</summary>
public interface ITernEventSink
{
    /// <summary>Appends an event of <paramref name="type"/> (<c>url</c>, <c>committed</c>, <c>loaded</c>, <c>failed</c>,
    /// <c>title</c>, <c>frame</c>, <c>response</c>, <c>message</c>, <c>download</c>, …) with its fields.</summary>
    void Post(string type, System.Text.Json.Nodes.JsonObject? fields = null);
}

/// <summary>The app as a Tern daemon: it opens one pane tab per omp <c>open</c>.</summary>
public interface ITernBrowser
{
    /// <summary>
    /// A new tab in the pane for omp, empty (about:blank) and shown unless the user picked another tab. Its events go to
    /// <paramref name="block"/>. Throws <see cref="TernException"/> (<c>no_window</c> where no page can be shown here:
    /// omp then uses its own browser).
    /// </summary>
    Task<ITernPage> OpenAsync(TernOpenRequest request, TernBlock block, CancellationToken ct);

    /// <summary>Puts <paramref name="text"/> on the system clipboard (Tern <c>clipboard</c> write).</summary>
    Task WriteClipboardAsync(string text, CancellationToken ct);
}

/// <summary>
/// One tab omp drives over Tern (a Tern "block"). Implemented by the app over a pane tab's native web view, by the
/// windowless harness over its own. Methods may be called concurrently from any thread; implementations move to their
/// UI thread. Failures are <see cref="TernException"/>s.
/// </summary>
public interface ITernPage
{
    /// <summary>The user closed the tab: omp's requests are refused (<c>window_closed</c>). Any thread.</summary>
    bool IsClosed { get; }

    /// <summary>The questions the page asks (JavaScript dialogs, files, downloads, sign-in); null where nothing reports them.</summary>
    DialogBroker? Dialogs { get; }

    /// <summary>omp's action is running in the page (input, script, navigation): what the page asks meanwhile is omp's.
    /// Dispose when it ends.</summary>
    IDisposable BeginAgentAction();

    /// <summary>Starts loading <paramref name="url"/>; returns once it started (completion arrives as events).</summary>
    Task NavigateAsync(Uri url, CancellationToken ct);

    Task HistoryAsync(TernHistory go, CancellationToken ct);

    Task<TernPageState> GetStateAsync(CancellationToken ct);

    /// <summary>
    /// Calls <paramref name="function"/> (a JavaScript function's source) with the JSON array
    /// <paramref name="argsJson"/> spread as its arguments, in <paramref name="world"/> of the frame at index path
    /// <paramref name="frame"/> (null = main frame), awaiting a returned Promise. The settled value as JSON text, null
    /// for undefined. A page exception is <c>js</c> with its text.
    /// </summary>
    Task<string?> EvaluateAsync(string function, string argsJson, TernWorld world, string? frame, CancellationToken ct);

    /// <summary>Trusted input at main-frame viewport CSS pixels, in order (<see cref="INativeInput"/>).</summary>
    Task InputAsync(IReadOnlyList<NativeInputStep> steps, CancellationToken ct);

    Task<byte[]> CaptureAsync(TernCaptureRequest request, CancellationToken ct);

    Task<byte[]> PdfAsync(CancellationToken ct);

    /// <summary>The page lays out at <paramref name="width"/> × <paramref name="height"/> CSS pixels.</summary>
    Task SetViewportAsync(int width, int height, CancellationToken ct);

    /// <summary>Replaces the document-start scripts of future documents (omp's own list, in order).</summary>
    Task SetScriptsAsync(IReadOnlyList<TernScript> scripts, CancellationToken ct);

    /// <summary>A custom user agent; null restores the engine's own.</summary>
    Task SetUserAgentAsync(string? userAgent, CancellationToken ct);

    /// <summary><c>prefers-color-scheme</c>: "light", "dark", or null to follow the app.</summary>
    Task SetAppearanceAsync(string? scheme, CancellationToken ct);

    /// <summary>Every cookie of the tab's store.</summary>
    Task<IReadOnlyList<TernCookie>> GetCookiesAsync(CancellationToken ct);

    Task SetCookieAsync(TernCookie cookie, CancellationToken ct);

    Task DeleteCookieAsync(string name, string domain, string path, CancellationToken ct);

    /// <summary>Copies the page's selection to the system clipboard (the page's own Copy command).</summary>
    Task CopyAsync(CancellationToken ct);

    /// <summary>omp is done with the tab: it leaves the pane (or stays for the user while they look at it).</summary>
    Task CloseAsync(CancellationToken ct);
}
