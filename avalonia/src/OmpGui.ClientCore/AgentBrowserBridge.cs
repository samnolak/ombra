using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OmpGui.ClientCore.Browser;
using OmpGui.Rpc;

namespace OmpGui.ClientCore;

/// <summary>What <see cref="IAgentBrowserPage"/> reports about the page.</summary>
/// <param name="Url">The page shown or loading; null while the preview is empty.</param>
/// <param name="IsLoading">A navigation has not finished.</param>
/// <param name="LoadFailed">The last navigation failed (the server did not answer).</param>
/// <param name="Unavailable">Why no page can be shown here (the web engine is missing); null when it can.</param>
public readonly record struct AgentPageState(Uri? Url, bool IsLoading, bool LoadFailed, string? Unavailable);

/// <summary>A failure the bridge reports to omp with a code (cmux's error shape: <c>code: message</c>).</summary>
public sealed class AgentBrowserException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A PNG capture of what the preview shows (the page's visible area).</summary>
/// <param name="Png">The encoded image.</param>
/// <param name="Width">Width in pixels (device pixels: twice the page's width on a Retina screen).</param>
/// <param name="Height">Height in pixels.</param>
public sealed record AgentScreenshot(byte[] Png, int Width, int Height)
{
    private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A screenshot from PNG bytes, its size read from the header; null when the bytes are not a PNG.</summary>
    public static AgentScreenshot? FromPng(byte[] png)
    {
        // Signature, then the IHDR chunk first: length (4), "IHDR" (4), width and height (big-endian 4 each)
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(Signature) || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return null;
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        return width > 0 && height > 0 ? new AgentScreenshot(png, width, height) : null;
    }
}

/// <summary>The preview pane as the agent's browser: it opens one tab per surface omp asks for (UI thread inside).</summary>
public interface IAgentBrowserHost
{
    /// <summary>
    /// A new tab in the pane for the agent, named <paramref name="surfaceId"/>, empty until it is navigated. The pane
    /// shows it unless the user picked another tab. Throws <see cref="AgentBrowserException"/> when no tab can open.
    /// </summary>
    Task<IAgentBrowserPage> OpenTabAsync(string surfaceId, CancellationToken ct);
}

/// <summary>One tab of the preview pane as the agent's browser (implemented by the app on its UI thread).</summary>
public interface IAgentBrowserPage
{
    /// <summary>Shows the preview without loading anything (an empty tab).</summary>
    Task ShowAsync(CancellationToken ct);

    /// <summary>Starts loading <paramref name="address"/> in this tab; the address loading. Throws
    /// <see cref="AgentBrowserException"/> when the preview refuses it (not http/https) or cannot show pages.</summary>
    Task<Uri> NavigateAsync(string address, CancellationToken ct);

    Task<AgentPageState> GetStateAsync(CancellationToken ct);

    /// <summary>Runs <paramref name="script"/> in the page: the engine's result as text ("" when nothing came back).
    /// Throws <see cref="AgentBrowserException"/> ("no_page") when no page is open.</summary>
    Task<string> RunScriptAsync(string script, CancellationToken ct);

    /// <summary>Captures what the tab shows as a PNG. Throws <see cref="AgentBrowserException"/>: "no_page" when no
    /// page is open, "not_supported" where the web engine cannot capture, "screenshot_failed" when the capture failed.</summary>
    Task<AgentScreenshot> CaptureAsync(CancellationToken ct);

    /// <summary>omp is done with the tab (<c>surface.close</c>): the pane drops it, or keeps it for the user while they look at it.</summary>
    Task CloseAsync(CancellationToken ct);

    /// <summary>The user closed the tab in the pane: the agent's requests for it are refused. Any thread.</summary>
    bool IsClosed { get; }

    /// <summary>The questions the page asks (JavaScript dialogs among them); null where nothing reports them. Any thread.</summary>
    DialogBroker? Dialogs { get; }

    /// <summary>Input the engine treats like the user's (trusted events, user activation); null where the preview has
    /// none: the bridge then dispatches script events and says so in its answer. Any thread.</summary>
    INativeInput? Input => null;

    /// <summary>The app's script world in the page, apart from the page's own (the bridge's library lives there); null
    /// where the engine has none: the library then runs in the page world. Any thread.</summary>
    IIsolatedScripts? Isolated => null;

    /// <summary>omp acts on the page until the result is disposed: what the page asks meanwhile goes to omp first. Any thread.</summary>
    IDisposable? BeginAction() => null;
}

/// <summary>
/// omp's own browser tool, driving the preview panel. omp 18.8.0 drives a cmux surface (a WKWebView in the cmux
/// terminal) when <c>CMUX_SOCKET_PATH</c> names a socket: newline-delimited JSON requests
/// <c>{"id","method","params"}</c> answered by <c>{"id","ok":true,"result"}</c> or <c>{"ok":false,"error":{code,message}}</c>
/// (pi-coding-agent src/tools/browser/cmux). The app serves that protocol on a socket only this user can open (a named
/// pipe on Windows) with a password, and gives both to the omp processes it starts (<see cref="AddTo"/>). Every page
/// action is a script in the preview's web view; the preview opens when the agent opens a page.
/// </summary>
/// <remarks>
/// Each <c>browser.open_split</c> opens its own tab in the pane (its own web view), so the tabs of every omp process
/// (sessions, the TUI tab) stay valid side by side until omp closes them (<c>surface.close</c>) or the user does.
/// While a page shows a JavaScript dialog its scripts wait: requests answer <c>dialog_open</c> with the dialog's kind
/// and text instead of stalling, and <c>browser.press</c> Enter/Escape answers it. <c>browser.wait</c> answers about a
/// second before <c>timeout_ms</c>, when omp's own socket timer would give up. Screenshots capture the visible part of
/// the page only (the web view's own snapshot call: no element clip, no full page), the same as a cmux surface.
/// </remarks>
public sealed partial class AgentBrowserBridge : IDisposable
{
    public const string SocketVariable = "CMUX_SOCKET_PATH";
    public const string PasswordVariable = "CMUX_SOCKET_PASSWORD";
    /// <summary>omp's cmux switch; the variable wins over the user's <c>browser.cmux</c> in both directions.</summary>
    public const string CmuxFlagVariable = "PI_BROWSER_CMUX";

    /// <summary>What omp reads from a real cmux terminal: removed, so omp never addresses a cmux window instead.</summary>
    private static readonly string[] CmuxVariables = ["CMUX_WORKSPACE_ID", "CMUX_SURFACE_ID", "CMUX_RELAY_ID", "CMUX_RELAY_TOKEN"];

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    /// <summary>How much earlier than <c>timeout_ms</c> browser.wait answers: omp's socket timer uses the same value.</summary>
    private static readonly TimeSpan WaitMargin = TimeSpan.FromSeconds(1);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonNodeOptions NodeOptions = new();
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 512 };
    // Page text stays readable in the answers (it is JSON for omp, never HTML)
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        MaxDepth = 512,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly string? _directory;
    private Socket? _listener;
    private string? _pipeName;
    /// <summary>The open tabs by surface id, oldest first.</summary>
    private readonly OrderedDictionary<string, IAgentBrowserPage> _surfaces = new(StringComparer.Ordinal);
    private long _run;
    private readonly AgentBrowserScripts _scripts = AgentBrowserScripts.CreateRandom();

    private AgentBrowserBridge(string endpoint, string password, string? directory)
    {
        Endpoint = endpoint;
        Password = password;
        _directory = directory;
    }

    /// <summary>For tests: a bridge that only dispatches (no socket).</summary>
    internal AgentBrowserBridge(IAgentBrowserHost host) : this("", "", null) => Host = host;

    /// <summary>The socket path (or <c>\\.\pipe\…</c> on Windows) omp connects to.</summary>
    public string Endpoint { get; }

    /// <summary>What omp sends first (<c>auth &lt;password&gt;</c>).</summary>
    public string Password { get; }

    /// <summary>The pane the agent opens tabs in; until it is set, requests are answered "unavailable".</summary>
    public IAgentBrowserHost? Host { get; set; }

    /// <summary>The page scripts (their random global names), for tests.</summary>
    internal AgentBrowserScripts Scripts => _scripts;

    /// <summary>Starts listening; null (and a line on stderr) when this system offers no local socket.</summary>
    public static AgentBrowserBridge? TryStart()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var name = "ompgui-browser-" + id;
                var pipe = new AgentBrowserBridge(@"\\.\pipe\" + name, password, null) { _pipeName = name };
                _ = pipe.AcceptPipesAsync();
                return pipe;
            }
            else
            {
                // A folder only this user can enter; a socket path is at most 104 bytes on macOS
                var root = Path.GetTempPath();
                if (root.Length > 60) root = "/tmp";
                var dir = Path.Combine(root, "ompgui-" + id);
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var path = Path.Combine(dir, "browser.sock");
                var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    listener.Bind(new UnixDomainSocketEndPoint(path));
                    listener.Listen(8);
                }
                catch
                {
                    listener.Dispose();
                    throw;
                }
                var bridge = new AgentBrowserBridge(path, password, dir) { _listener = listener };
                _ = bridge.AcceptSocketsAsync();
                return bridge;
            }
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            Console.Error.WriteLine("The agent's browser is not available: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// <paramref name="spec"/> with the variables that send omp's browser tool here (socket, password and
    /// PI_BROWSER_CMUX=1, which wins over omp's <c>browser.cmux: false</c>). A variable the user set in the settings'
    /// environment stays as they set it (e.g. their own CMUX_SOCKET_PATH, or PI_BROWSER_CMUX=0).
    /// </summary>
    public OmpLaunchSpec AddTo(OmpLaunchSpec spec)
    {
        var env = new Dictionary<string, string?>(spec.Environment);
        if (env.ContainsKey(SocketVariable)) return spec;
        env[SocketVariable] = Endpoint;
        env[PasswordVariable] = Password;
        env.TryAdd(CmuxFlagVariable, "1");
        foreach (var k in CmuxVariables) env.TryAdd(k, null);
        return spec with { Environment = env };
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try { _listener?.Dispose(); }
        catch (SocketException) { }
        if (_directory is not null)
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        // _cts stays undisposed: the accept loops and connections still hold its token as they wind down
    }

    // ── Transport ──

    private async Task AcceptSocketsAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested && _listener is { } listener)
        {
            Socket client;
            try { client = await listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = ServeAsync(new NetworkStream(client, ownsSocket: true), ct);
        }
    }

    private async Task AcceptPipesAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested && _pipeName is { } name)
        {
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                continue;
            }
            _ = ServeAsync(pipe, ct);
        }
    }

    /// <summary>One omp process: the password line, then one request at a time.</summary>
    private async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);
            var writer = new StreamWriter(stream, Utf8, bufferSize: 16 * 1024, leaveOpen: true) { NewLine = "\n" };
            var first = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (first is null) return;
            if (!Authenticates(first))
            {
                await writer.WriteLineAsync(first.StartsWith("auth ", StringComparison.Ordinal) ? "ERROR: Access denied" : "ERROR: Authentication required").ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
                return;
            }
            await writer.WriteLineAsync("OK").ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                var answer = await HandleLineAsync(line, ct).ConfigureAwait(false);
                await writer.WriteLineAsync(answer).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // omp went away (or the app is closing)
        }
        finally
        {
            try { await stream.DisposeAsync().ConfigureAwait(false); }
            catch (IOException) { }
        }
    }

    private bool Authenticates(string line)
    {
        if (!line.StartsWith("auth ", StringComparison.Ordinal)) return false;
        var given = Encoding.UTF8.GetBytes(line[5..].Trim());
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(Password));
    }

    /// <summary>One request line to its answer line.</summary>
    internal async Task<string> HandleLineAsync(string line, CancellationToken ct)
    {
        JsonNode? id = null;
        try
        {
            if (JsonNode.Parse(line, NodeOptions, DocumentOptions) is not JsonObject request)
                throw new AgentBrowserException("invalid_request", "expected a JSON object");
            id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValueKind() == JsonValueKind.String ? request["method"]!.GetValue<string>() : "";
            var parameters = request["params"] as JsonObject ?? new JsonObject();
            var result = await DispatchAsync(method, parameters, ct).ConfigureAwait(false);
            return new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = result }.ToJsonString(WriteOptions);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var code = e switch
            {
                AgentBrowserException a => a.Code,
                JsonException => "invalid_request",
                InvalidOperationException or FormatException => "invalid_params",
                _ => "internal_error",
            };
            if (code == "internal_error") Console.Error.WriteLine("Agent browser request failed: " + e);
            return new JsonObject
            {
                ["id"] = id,
                ["ok"] = false,
                ["error"] = new JsonObject { ["code"] = code, ["message"] = e.Message },
            }.ToJsonString(WriteOptions);
        }
    }

    // ── Methods (the ones omp 18.8.0's cmux backend sends) ──

    internal async Task<JsonObject> DispatchAsync(string method, JsonObject p, CancellationToken ct)
    {
        var host = Host ?? throw new AgentBrowserException("unavailable", "The OMP GUI window is not ready yet.");
        switch (method)
        {
            case "system.ping":
                return new JsonObject { ["pong"] = true };
            case "browser.open_split":
                return await OpenAsync(host, p, ct).ConfigureAwait(false);
            case "surface.close":
            {
                IAgentBrowserPage? closing = null;
                if (Str(p, "surface_id") is { } id)
                    lock (_gate) _surfaces.Remove(id, out closing);
                if (closing is not null) await closing.CloseAsync(ct).ConfigureAwait(false);
                return new JsonObject();
            }
        }
        if (!method.StartsWith("browser.", StringComparison.Ordinal))
            throw new AgentBrowserException("method_not_found", $"Unknown method {method}");
        var (surface, page) = Surface(p);
        JsonObject result;
        switch (method)
        {
            case "browser.navigate":
            {
                var address = Str(p, "url") ?? throw new AgentBrowserException("invalid_params", "browser.navigate needs a url");
                var uri = await page.NavigateAsync(address, ct).ConfigureAwait(false);
                result = new JsonObject { ["url"] = uri.AbsoluteUri };
                break;
            }
            case "browser.url.get":
                result = new JsonObject { ["url"] = await UrlAsync(page, ct).ConfigureAwait(false) };
                break;
            case "browser.eval":
            {
                var script = Str(p, "script") ?? throw new AgentBrowserException("invalid_params", "browser.eval needs a script");
                result = new JsonObject { ["value"] = await EvaluateAsync(page, script, ct).ConfigureAwait(false) };
                break;
            }
            case "browser.wait":
                await WaitAsync(page, p, ct).ConfigureAwait(false);
                result = new JsonObject();
                break;
            case "browser.snapshot":
            {
                var interactive = Bool(p, "interactive") ?? false;
                var depth = Num(p, "max_depth") ?? 12;
                var value = await LibraryAsync(page, "snapshot", ct, interactive, depth, !interactive).ConfigureAwait(false);
                result = value as JsonObject ?? throw new AgentBrowserException("js_error", "The page did not return a snapshot.");
                break;
            }
            case "browser.click" or "browser.dblclick" or "browser.hover" or "browser.check" or "browser.uncheck":
                result = await ActAsync(page, method["browser.".Length..], Selector(p), new JsonObject(), ct).ConfigureAwait(false);
                break;
            case "browser.focus" or "browser.scroll_into_view":
                await LibraryAsync(page, "act", ct, method["browser.".Length..], Selector(p), null).ConfigureAwait(false);
                result = new JsonObject();
                break;
            case "browser.type" or "browser.fill":
                result = await ActAsync(page, method["browser.".Length..], Selector(p), new JsonObject { ["text"] = Str(p, "text") ?? "" }, ct)
                    .ConfigureAwait(false);
                break;
            case "browser.press":
            {
                var key = Str(p, "key") ?? throw new AgentBrowserException("invalid_params", "browser.press needs a key");
                // Enter / Escape answer a JavaScript dialog the page shows, as a person would
                result = AnswerDialog(page, key) ? new JsonObject() : await PressAsync(page, key, ct).ConfigureAwait(false);
                break;
            }
            case "browser.scroll":
                result = await ScrollAsync(page, Str(p, "selector"), Num(p, "dx") ?? 0, Num(p, "dy") ?? 0, ct).ConfigureAwait(false);
                break;
            case "browser.screenshot":
            {
                // cmux's answer: the viewport as base64 PNG (omp's cmux backend scrolls a selector into view itself and
                // says the image is the whole viewport; it resizes and saves the image)
                var shot = await page.CaptureAsync(ct).ConfigureAwait(false);
                if (shot.Png.Length == 0) throw new AgentBrowserException("screenshot_failed", "The preview returned an empty screenshot.");
                result = new JsonObject
                {
                    ["png_base64"] = Convert.ToBase64String(shot.Png),
                    ["width"] = shot.Width,
                    ["height"] = shot.Height,
                    ["url"] = await UrlAsync(page, ct).ConfigureAwait(false),
                };
                break;
            }
            default:
                throw new AgentBrowserException("method_not_found", $"Unknown method {method}");
        }
        result["surface_id"] = surface;
        return result;
    }

    /// <summary>browser.open_split: a tab of its own in the pane; the tabs opened before stay as they are.</summary>
    private async Task<JsonObject> OpenAsync(IAgentBrowserHost host, JsonObject p, CancellationToken ct)
    {
        var address = Str(p, "url") is { Length: > 0 } u ? u : "about:blank";
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        var page = await host.OpenTabAsync(id, ct).ConfigureAwait(false);
        Watch(page.Dialogs);
        string url;
        try
        {
            if (address == "about:blank")
            {
                await page.ShowAsync(ct).ConfigureAwait(false);
                url = await UrlAsync(page, ct).ConfigureAwait(false);
            }
            else url = (await page.NavigateAsync(address, ct).ConfigureAwait(false)).AbsoluteUri;
        }
        catch
        {
            await page.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        lock (_gate) _surfaces[id] = page;
        return new JsonObject { ["surface_id"] = id, ["url"] = url, ["workspace_id"] = "omp-gui", ["created_split"] = false };
    }

    /// <summary>The tab a request names (the newest open one when it names none).</summary>
    private (string Id, IAgentBrowserPage Page) Surface(JsonObject p)
    {
        var asked = Str(p, "surface_id");
        lock (_gate)
        {
            if (asked is null)
            {
                // Tabs the user closed are gone for good
                for (var i = _surfaces.Count - 1; i >= 0; i--)
                    if (_surfaces.GetAt(i).Value.IsClosed) _surfaces.RemoveAt(i);
                if (_surfaces.Count == 0)
                    throw new AgentBrowserException("not_found", "No browser tab is open in the OMP GUI preview: open one first.");
                var (id, newest) = _surfaces.GetAt(_surfaces.Count - 1);
                return (id, newest);
            }
            if (!_surfaces.TryGetValue(asked, out var page))
                throw new AgentBrowserException("not_found", "This browser tab is closed in the OMP GUI preview: open a new one.");
            if (page.IsClosed)
            {
                _surfaces.Remove(asked);
                throw new AgentBrowserException("not_found", "The user closed this tab in the OMP GUI preview: open a new one if the page is still needed.");
            }
            return (asked, page);
        }
    }

    // ── JavaScript dialogs: the page's scripts wait while one is open ──

    /// <summary>How long a question omp's own action caused (confirm, prompt, "Leave page?") waits for omp before the
    /// user gets its card instead (an unanswered one would hold the page).</summary>
    internal TimeSpan HandOffAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Brokers already watched, and the questions whose hand-off is scheduled.</summary>
    private readonly HashSet<DialogBroker> _watched = [];
    private readonly HashSet<BrowserDialog> _handOffs = [];

    /// <summary>
    /// Watches the questions of a tab's page: an alert omp's action caused is accepted (OK) at once, as the agent
    /// would; a confirm, prompt or "Leave page?" omp caused goes to the user after <see cref="HandOffAfter"/> unless omp
    /// answered it; a file chooser, download, permission or pop-up question omp caused goes to the user at once (cmux
    /// cannot answer those). The user's own dialogs are never touched.
    /// </summary>
    private void Watch(DialogBroker? broker)
    {
        if (broker is null) return;
        lock (_gate)
        {
            if (!_watched.Add(broker)) return;
        }
        broker.Changed += (_, _) => OnDialogsChanged(broker);
        OnDialogsChanged(broker);
    }

    private void OnDialogsChanged(DialogBroker broker)
    {
        foreach (var dialog in broker.AgentPending)
        {
            if (dialog.IsCompleted) continue;
            if (!dialog.IsModal)
            {
                // cmux has no file, download, permission or pop-up methods: the user answers what omp's action caused
                broker.HandToUser(dialog);
                continue;
            }
            if (dialog.Kind == BrowserDialogKind.Alert)
            {
                dialog.Complete(null);
                continue;
            }
            lock (_gate)
            {
                if (!_handOffs.Add(dialog)) continue;
            }
            _ = HandOffLaterAsync(broker, dialog);
        }
    }

    private async Task HandOffLaterAsync(DialogBroker broker, BrowserDialog dialog)
    {
        try { await Task.Delay(HandOffAfter, _cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        finally
        {
            lock (_gate) _handOffs.Remove(dialog);
        }
        if (!dialog.IsCompleted) broker.HandToUser(dialog);
    }

    /// <summary>The JavaScript dialog holding the page's scripts; an alert omp caused is accepted on the way (OK).</summary>
    private static BrowserDialog? Blocking(DialogBroker broker)
    {
        while (broker.CurrentModal is { } dialog)
        {
            if (!(dialog.RoutedToAgent && dialog.Kind == BrowserDialogKind.Alert)) return dialog;
            dialog.Complete(null);
        }
        return null;
    }

    /// <summary>What the agent is told while the page shows <paramref name="dialog"/> (code <c>dialog_open</c>).</summary>
    internal AgentBrowserException DialogOpen(BrowserDialog dialog)
    {
        var (what, how) = dialog.Kind switch
        {
            BrowserDialogKind.Alert => ("an alert", "press Enter with browser.press to close it"),
            BrowserDialogKind.Prompt => ("a prompt", "press Enter (OK, with its default text) or Escape (Cancel) with browser.press"),
            BrowserDialogKind.BeforeUnload => ("a \"Leave page?\" dialog", "press Enter to leave the page or Escape to stay, with browser.press"),
            _ => ("a confirm dialog", "press Enter (OK) or Escape (Cancel) with browser.press"),
        };
        var text = dialog.Message is { Length: > 0 } m ? $": \"{Clip(m)}\"" : "";
        var who = dialog.RoutedToAgent
            ? $"{how}; unanswered after {HandOffAfter.TotalSeconds:0.#} s, it goes to the user in the OMP GUI preview."
            : $"the user is asked in the OMP GUI preview (or {how}).";
        return new AgentBrowserException("dialog_open", $"The page is showing {what}{text}. Its scripts wait until it is answered: {who}");
    }

    /// <summary>Answers the page's JavaScript dialog for <c>browser.press</c> Enter (OK) or Escape (Cancel); false when none is open.</summary>
    private bool AnswerDialog(IAgentBrowserPage page, string key)
    {
        if (page.Dialogs is not { } broker || Blocking(broker) is not { } dialog) return false;
        bool accept = key switch
        {
            "Enter" => true,
            "Escape" or "Esc" => false,
            _ => throw DialogOpen(dialog),
        };
        object? answer = dialog.Request switch
        {
            AlertRequest => null,
            PromptRequest prompt => accept ? prompt.Default ?? "" : null,
            _ => accept,
        };
        dialog.Complete(answer); // false when the user answered it at the same moment: answered either way
        return true;
    }

    /// <summary>
    /// Runs <paramref name="script"/> unless the page shows a JavaScript dialog, and stops waiting for it when one opens
    /// meanwhile (the script itself opened it, e.g. a click on a "Delete" button that confirms): <c>dialog_open</c>.
    /// An alert omp caused is accepted and the script goes on.
    /// </summary>
    private Task<string> RunScriptAsync(IAgentBrowserPage page, string script, CancellationToken ct) =>
        UnlessDialogAsync(page, c => page.RunScriptAsync(script, c), ct);

    /// <summary><paramref name="start"/> with <see cref="RunScriptAsync(IAgentBrowserPage, string, CancellationToken)"/>'s rules for JavaScript dialogs.</summary>
    private async Task<T> UnlessDialogAsync<T>(IAgentBrowserPage page, Func<CancellationToken, Task<T>> start, CancellationToken ct)
    {
        if (page.Dialogs is not { } broker) return await start(ct).ConfigureAwait(false);
        if (Blocking(broker) is { } open) throw DialogOpen(open);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e)
        {
            if (broker.CurrentModal is not null) opened.TrySetResult();
        }
        broker.Changed += OnChanged;
        try
        {
            var run = start(ct);
            OnChanged(null, EventArgs.Empty);
            while (true)
            {
                if (await Task.WhenAny(run, opened.Task).ConfigureAwait(false) == run) return await run.ConfigureAwait(false);
                if (Blocking(broker) is { } dialog)
                {
                    // The script finishes once the dialog is answered; nobody waits for its value any more
                    _ = run.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw DialogOpen(dialog);
                }
                opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // came and went already
                OnChanged(null, EventArgs.Empty);
            }
        }
        finally
        {
            broker.Changed -= OnChanged;
        }
    }

    private async Task<string> UrlAsync(IAgentBrowserPage page, CancellationToken ct)
    {
        try
        {
            var href = await RunScriptAsync(page, "location.href", ct).ConfigureAwait(false);
            href = Unquote(href);
            if (Uri.TryCreate(href, UriKind.Absolute, out var u)) return u.AbsoluteUri;
        }
        catch (Exception e) when (e is not OperationCanceledException && e is not AgentBrowserException { Code: not ("no_page" or "dialog_open") })
        {
            // No page yet, a dialog holds the page's scripts, or the engine refused the script mid-navigation: the
            // address the preview knows
        }
        var state = await page.GetStateAsync(ct).ConfigureAwait(false);
        return state.Url?.AbsoluteUri ?? "about:blank";
    }

    /// <summary>The value of <paramref name="expression"/> in the page, as JSON.</summary>
    internal async Task<JsonNode?> EvaluateAsync(IAgentBrowserPage page, string expression, CancellationToken ct)
    {
        var run = Interlocked.Increment(ref _run);
        var text = await RunAsync(page, _scripts.Wrap(expression, run, viaEval: false), ct).ConfigureAwait(false);
        if (text is null)
        {
            // Nothing came back: the script did not parse as an expression (nothing ran: try it as statements), or the
            // page went away while it ran (a click that navigates): then there is no value.
            string ran;
            try { ran = Unquote(await RunScriptAsync(page, _scripts.RanCheck(run), ct).ConfigureAwait(false)); }
            catch (Exception e) when (e is not OperationCanceledException and not AgentBrowserException) { return null; }
            if (ran != "false") return null;
            text = await RunAsync(page, _scripts.Wrap(expression, run, viaEval: true), ct).ConfigureAwait(false)
                ?? throw new AgentBrowserException("js_error", "The page did not run the script.");
        }
        JsonObject answer;
        try
        {
            answer = JsonNode.Parse(text, NodeOptions, DocumentOptions) as JsonObject
                ?? throw new AgentBrowserException("js_error", "The page answered: " + Clip(text));
        }
        catch (JsonException)
        {
            throw new AgentBrowserException("js_error", "The page answered: " + Clip(text));
        }
        if (answer["err"] is { } err)
        {
            var message = err.GetValueKind() == JsonValueKind.String ? err.GetValue<string>() : err.ToJsonString();
            if (CodedError().Match(message) is { Success: true } m)
                throw new AgentBrowserException(m.Groups["code"].Value, m.Groups["message"].Value);
            var stack = answer["stack"]?.GetValueKind() == JsonValueKind.String ? answer["stack"]!.GetValue<string>() : "";
            var name = answer["name"]?.GetValueKind() == JsonValueKind.String ? answer["name"]!.GetValue<string>() : "";
            var head = name.Length > 0 && !message.StartsWith(name, StringComparison.Ordinal) ? name + ": " + message : message;
            // V8 and Bun stacks start with "Name: message"; WebKit's are frames only
            throw new AgentBrowserException("js_error", stack.Length == 0 ? head : stack.StartsWith(head, StringComparison.Ordinal) ? stack : head + "\n" + stack);
        }
        if (answer["promise"] is not null)
            throw new AgentBrowserException("js_error",
                "The script returned a Promise, but the OMP GUI preview evaluates synchronously: return a plain value (poll with waitForFunction for async state).");
        var value = answer["ok"];
        answer.Remove("ok");
        return value;
    }

    private async Task<string?> RunAsync(IAgentBrowserPage page, string script, CancellationToken ct)
    {
        string text;
        try { text = await RunScriptAsync(page, script, ct).ConfigureAwait(false); }
        catch (AgentBrowserException) { throw; }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
        text = Unquote(text);
        return text.Length == 0 || text == "null" || text == "undefined" ? null : text;
    }

    /// <summary>
    /// How long browser.wait waits for <paramref name="timeout"/>: omp gives up on the socket at <c>timeout_ms</c>
    /// itself, so the answer (a clean timeout error) comes about a second earlier (a quarter earlier for short waits).
    /// </summary>
    internal static TimeSpan WaitBudget(TimeSpan timeout) => timeout - (timeout >= WaitMargin * 4 ? WaitMargin : timeout / 4);

    /// <summary>browser.wait: a load state, a selector, a URL part, until the deadline.</summary>
    private async Task WaitAsync(IAgentBrowserPage page, JsonObject p, CancellationToken ct)
    {
        var budget = WaitBudget(TimeSpan.FromMilliseconds(Math.Clamp(Num(p, "timeout_ms") ?? 30_000, 0, 600_000)));
        var selector = Str(p, "selector");
        var urlPart = Str(p, "url_contains");
        var load = Str(p, "load_state") ?? (selector is null && urlPart is null ? "complete" : null);
        var what = selector is not null ? $"selector {selector}" : urlPart is not null ? $"a URL containing {urlPart}" : $"the page to be {load}";
        var timedOut = new AgentBrowserException("timeout", $"Timed out after {budget.TotalMilliseconds:0} ms waiting for {what}");
        // A script the page does not answer (a hung page) ends at the deadline too
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        var clock = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var state = await page.GetStateAsync(deadline.Token).ConfigureAwait(false);
                if (state.Unavailable is { } why) throw new AgentBrowserException("unavailable", why);
                if (await MetAsync(page, state, load, selector, urlPart, deadline.Token).ConfigureAwait(false))
                {
                    if (load is not null && state.LoadFailed)
                        throw new AgentBrowserException("navigation_failed",
                            $"{state.Url?.AbsoluteUri ?? "The page"} did not load: is the server running?");
                    return;
                }
                var left = budget - clock.Elapsed;
                if (left <= TimeSpan.Zero) throw timedOut;
                await Task.Delay(left < Poll ? left : Poll, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw timedOut;
        }
    }

    private async Task<bool> MetAsync(IAgentBrowserPage page, AgentPageState state, string? load, string? selector, string? urlPart, CancellationToken ct)
    {
        if (load is not null)
        {
            if (state.IsLoading) return false;
            string ready;
            try { ready = Unquote(await RunScriptAsync(page, "document.readyState", ct).ConfigureAwait(false)); }
            catch (AgentBrowserException e) when (e.Code == "no_page") { return true; } // an empty tab is loaded
            catch (Exception e) when (e is not OperationCanceledException and not AgentBrowserException) { return false; } // between two documents
            if (load == "interactive" ? ready == "loading" : ready is not ("complete" or "")) return false;
        }
        if (urlPart is not null && !(await UrlAsync(page, ct).ConfigureAwait(false)).Contains(urlPart, StringComparison.Ordinal)) return false;
        if (selector is not null)
        {
            try
            {
                var found = await LibraryAsync(page, "exists", ct, selector).ConfigureAwait(false);
                if (found?.GetValueKind() != JsonValueKind.True) return false;
            }
            catch (AgentBrowserException e) when (e.Code == "no_page") { return false; }
        }
        return true;
    }

    // ── Parameters ──

    private static string Selector(JsonObject p) =>
        Str(p, "selector") ?? throw new AgentBrowserException("invalid_params", "a selector is needed");

    private static string? Str(JsonObject p, string name) =>
        p[name] is { } n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;

    private static bool? Bool(JsonObject p, string name) => p[name]?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static double? Num(JsonObject p, string name) =>
        p[name] is { } n && n.GetValueKind() == JsonValueKind.Number ? n.GetValue<double>() : null;

    /// <summary>Engines return a string result as JSON ("\"complete\"") or bare: the bare text.</summary>
    internal static string Unquote(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            try { return JsonNode.Parse(raw)?.GetValue<string>() ?? ""; }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { }
        }
        return raw;
    }

    private static string Clip(string s) => s.Length > 200 ? s[..200] + "…" : s;

    [GeneratedRegex(@"^(?<code>not_found|invalid_params|invalid_target|blocked): (?<message>.*)$", RegexOptions.Singleline)]
    private static partial Regex CodedError();
}
