using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ombra.ClientCore.Browser;
using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>What <see cref="IAgentBrowserPage"/> reports about the page.</summary>
/// <param name="Url">The page shown or loading; null while the preview is empty.</param>
/// <param name="IsLoading">A navigation has not finished.</param>
/// <param name="LoadFailed">The last navigation failed (the server did not answer).</param>
/// <param name="Unavailable">Why no page can be shown here (the web engine is missing); null when it can.</param>
/// <param name="Title">The page's title as the engine last reported it to the preview; null when it has none yet.</param>
public readonly record struct AgentPageState(Uri? Url, bool IsLoading, bool LoadFailed, string? Unavailable, string? Title = null);

/// <summary>A failure the bridge reports to omp with a code (cmux's error shape: <c>code: message</c>).</summary>
public sealed class AgentBrowserException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// A device preset the preview offers (the device bar's): CSS size, upright, the device pixel ratio and the platform
/// (<c>desktop</c>, <c>iphone</c>, <c>ipad</c>, <c>android</c>).
/// </summary>
public sealed record AgentDevicePreset(string Name, int Width, int Height, double PixelRatio, string Platform);

/// <summary>
/// What <c>browser.set_device</c> asks of the preview's device mode: a <paramref name="Preset"/> by name, or a size;
/// then any of the pixel ratio (0 the display's), touch, platform and orientation over it. Unset fields stay as they are
/// (a preset sets them all first).
/// </summary>
public sealed record AgentDeviceRequest(
    string? Preset = null, int? Width = null, int? Height = null, double? PixelRatio = null, bool? Touch = null,
    string? Platform = null, bool? Landscape = null);

/// <summary>The preview's device mode after <c>browser.set_device</c>. <paramref name="Width"/>/<paramref name="Height"/>:
/// the page's size (turned when landscape), null when it fills the pane. <paramref name="Emulated"/>: the engine also
/// emulates the pixel ratio and touch (the Nightly runtime); elsewhere only the size and the user agent change.</summary>
public sealed record AgentDeviceState(
    string? Preset, int? Width, int? Height, double PixelRatio, bool Touch, string Platform, bool Landscape, bool Emulated);

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

    /// <summary>Intent hints for the tab the agent opens next, before it exists (<c>agent_open</c>); null where the
    /// engine new tabs open in takes none. Any thread.</summary>
    IAgentSpeculation? Speculation => null;
}

/// <summary>
/// An engine that can warm the connection to a page the agent is about to load (Augur v1: DNS, TCP and TLS ahead of
/// the navigation, never a request): the Nightly runtime. Hints are fire-and-forget; a hint whose action fails is
/// cancelled by its id. Any thread.
/// </summary>
public interface IAgentSpeculation
{
    void Speculate(SpeculationHint hint);

    void CancelSpeculation(string id);
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
    /// <paramref name="ct"/> stops waiting for it (the bridge's deadline). Throws <see cref="IsolatedScriptException"/>
    /// when the engine says the script threw or did not run (a syntax error, a document between two loads), and
    /// <see cref="AgentBrowserException"/>: "no_page" when no page is open, "window_closed" / "runtime_disconnected"
    /// when the engine lost the page meanwhile.</summary>
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

    /// <summary>Intent hints for this tab's engine (<c>agent_goto</c>, <c>agent_click</c>); null where it takes none. Any thread.</summary>
    IAgentSpeculation? Speculation => null;

    /// <summary>omp acts on the page until the result is disposed: what the page asks meanwhile goes to omp first. Any thread.</summary>
    IDisposable? BeginAction() => null;

    /// <summary>The preview's presets (<c>browser.devices</c>).</summary>
    IReadOnlyList<AgentDevicePreset> DevicePresets => [];

    /// <summary>Sets the preview's device mode for every tab (<c>browser.set_device</c>; null: back to the pane's size and
    /// the engine's own user agent, pixel ratio and input). Throws <see cref="AgentBrowserException"/> "invalid_params" for
    /// an unknown preset or platform or a size out of range, "not_supported" where the preview has no device mode.</summary>
    Task<AgentDeviceState> SetDeviceAsync(AgentDeviceRequest? device, CancellationToken ct) =>
        throw new AgentBrowserException("not_supported", "This preview has no device mode.");
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
/// second before <c>timeout_ms</c>, when omp's own socket timer would give up. Every page script (and every input
/// batch) has a deadline well inside omp's 30-second socket timer (<see cref="ScriptDeadline"/>), and every request
/// as a whole answers a second before it (<see cref="RequestBudget"/>, the steps of an action sharing it): a hung or
/// slow page answers <c>page_unresponsive</c> (<c>script_timeout</c> past a <c>timeout_ms</c> browser.eval asked for)
/// instead of making omp drop the connection, whose next queued request would then fail with "cmux socket is not
/// connected". The address (<c>browser.url.get</c>, a screenshot's <c>url</c>) is the one the preview knows, never a
/// page script, and omp's own reads of the title, of the CSS width before a screenshot and of the geometry beside an
/// observe wait for the page 300 ms at most (the title is then the preview's), so a frozen page's screenshot is immediate.
/// Screenshots capture the visible part of the page only (the web view's own snapshot call: no element clip, no full
/// page), the same as a cmux surface. Where the tab's engine takes intent hints (<see cref="IAgentSpeculation"/>, the
/// Nightly runtime), opening a page, going to one and clicking a link tell it first, so it can warm the connection.
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
    /// <summary>omp's own socket timer for a request that names none (cmux <c>socket-client.ts</c>).</summary>
    private static readonly TimeSpan OmpRequestTimeout = TimeSpan.FromSeconds(30);
    /// <summary>The longest <c>timeout_ms</c> a browser.eval may ask for its script.</summary>
    internal static readonly TimeSpan ScriptTimeoutLimit = TimeSpan.FromMinutes(2);
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
                var name = "ombra-browser-" + id;
                var pipe = new AgentBrowserBridge(@"\\.\pipe\" + name, password, null) { _pipeName = name };
                _ = pipe.AcceptPipesAsync();
                return pipe;
            }
            else
            {
                // A folder only this user can enter; a socket path is at most 104 bytes on macOS
                var root = Path.GetTempPath();
                if (root.Length > 60) root = "/tmp";
                var dir = Path.Combine(root, "ombra-" + id);
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

    /// <summary>
    /// How long a whole request may take (every script, input batch and pause of an action together): it answers
    /// before omp's own socket timer gives up on it (<see cref="WaitBudget"/> of omp's 30 s), so omp never drops the
    /// connection. browser.wait keeps its own budget; a browser.eval with <c>timeout_ms</c> has that one.
    /// </summary>
    internal TimeSpan RequestBudget { get; set; } = WaitBudget(OmpRequestTimeout);

    /// <summary>
    /// One request within its deadline (<see cref="RequestBudget"/>): each step of it gets the time left (its token is
    /// the request's), and a request still running at the deadline answers <c>page_unresponsive</c> (<c>script_timeout</c>
    /// for a browser.eval's <c>timeout_ms</c>), even when a step does not stop.
    /// </summary>
    internal Task<JsonObject> DispatchAsync(string method, JsonObject p, CancellationToken ct)
    {
        // browser.wait answers its own timeout a second before omp's timer for it (WaitBudget of its timeout_ms)
        if (method == "browser.wait") return DispatchCoreAsync(method, p, ct);
        var deadline = method == "browser.eval" && ScriptTimeout(p) is { } asked ? Deadline.Start(asked, asked: true) : Deadline.Start(RequestBudget);
        return BoundedAsync(c => DispatchCoreAsync(method, p, c), deadline, ct);
    }

    private async Task<JsonObject> DispatchCoreAsync(string method, JsonObject p, CancellationToken ct)
    {
        var host = Host ?? throw new AgentBrowserException("unavailable", "The Ombra window is not ready yet.");
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
                var intent = Intend(page.Speculation, SpeculationHint.AgentGoto, address);
                Uri uri;
                try
                {
                    uri = await page.NavigateAsync(address, ct).ConfigureAwait(false);
                }
                catch
                {
                    intent?.Cancel();
                    throw;
                }
                result = new JsonObject { ["url"] = uri.AbsoluteUri };
                break;
            }
            case "browser.url.get":
                result = new JsonObject { ["url"] = await UrlAsync(page, ct).ConfigureAwait(false) };
                break;
            case "browser.eval":
            {
                var script = Str(p, "script") ?? throw new AgentBrowserException("invalid_params", "browser.eval needs a script");
                result = new JsonObject { ["value"] = await EvalAsync(page, script, p, ct).ConfigureAwait(false) };
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
                // says the image is the whole viewport; it resizes and saves the image). Nothing here runs in the page:
                // a hung page is still captured (its last frame) at once.
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
            case "browser.devices":
                result = new JsonObject
                {
                    ["devices"] = new JsonArray([.. page.DevicePresets.Select(d => (JsonNode)new JsonObject
                    {
                        ["name"] = d.Name,
                        ["width"] = d.Width,
                        ["height"] = d.Height,
                        ["dpr"] = d.PixelRatio,
                        ["platform"] = d.Platform,
                    })]),
                };
                break;
            case "browser.set_device":
            {
                var state = await page.SetDeviceAsync(DeviceRequest(p), ct).ConfigureAwait(false);
                result = new JsonObject
                {
                    ["preset"] = state.Preset,
                    ["width"] = state.Width,
                    ["height"] = state.Height,
                    ["dpr"] = state.PixelRatio,
                    ["touch"] = state.Touch,
                    ["platform"] = state.Platform,
                    ["landscape"] = state.Landscape,
                    ["emulated"] = state.Emulated,
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
        // Before the tab exists: its engine starts on the connection while the pane and its window are made
        var intent = Intend(host.Speculation, SpeculationHint.AgentOpen, address);
        IAgentBrowserPage page;
        try
        {
            page = await host.OpenTabAsync(id, ct).ConfigureAwait(false);
        }
        catch
        {
            intent?.Cancel();
            throw;
        }
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
            intent?.Cancel();
            await page.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        lock (_gate) _surfaces[id] = page;
        return new JsonObject { ["surface_id"] = id, ["url"] = url, ["workspace_id"] = "ombra", ["created_split"] = false };
    }

    // ── intent hints (Augur v1) ──

    /// <summary>A hint that went out, cancelled when its action fails or goes elsewhere.</summary>
    private readonly record struct Intent(IAgentSpeculation Engine, string Id)
    {
        public void Cancel() => Engine.CancelSpeculation(Id);
    }

    /// <summary>
    /// Tells <paramref name="engine"/> the agent is about to load <paramref name="address"/> (<paramref name="source"/>,
    /// the load due in <paramref name="leadMs"/>; null: the source's usual lead). Nothing for an engine without hints or
    /// an address that is not an absolute http(s) URL without credentials.
    /// </summary>
    private static Intent? Intend(IAgentSpeculation? engine, string source, string? address, int? leadMs = null)
    {
        if (engine is null || address is null || !Uri.TryCreate(address, UriKind.Absolute, out var url)
            || SpeculationHint.For(source, url, leadMs) is not { } hint)
            return null;
        engine.Speculate(hint);
        return new Intent(engine, hint.Id);
    }

    /// <summary>The explicit tab identity supplied by the originating browser handle; never the active or newest tab.</summary>
    private (string Id, IAgentBrowserPage Page) Surface(JsonObject p)
    {
        var asked = Str(p, "surface_id") ??
            throw new AgentBrowserException("invalid_params", "A browser request needs the surface_id returned by browser.open_split.");
        lock (_gate)
        {
            if (!_surfaces.TryGetValue(asked, out var page))
                throw new AgentBrowserException("not_found", "This browser tab is closed in the Ombra preview: open a new one.");
            if (page.IsClosed)
            {
                _surfaces.Remove(asked);
                throw new AgentBrowserException("not_found", "The user closed this tab in the Ombra preview: open a new one if the page is still needed.");
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
            ? $"{how}; unanswered after {HandOffAfter.TotalSeconds:0.#} s, it goes to the user in the Ombra preview."
            : $"the user is asked in the Ombra preview (or {how}).";
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
    private Task<string> RunScriptAsync(IAgentBrowserPage page, string script, CancellationToken ct, Deadline? deadline = null)
    {
        var limit = deadline ?? Deadline.Start(ScriptDeadline);
        return UnlessDialogAsync(page, c => BoundedAsync(d => page.RunScriptAsync(script, d), limit, c), ct);
    }

    /// <summary><paramref name="start"/> with <see cref="RunScriptAsync(IAgentBrowserPage, string, CancellationToken, Deadline?)"/>'s rules for JavaScript dialogs.</summary>
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
                    Observe(run);
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

    // ── deadlines: a hung page never holds omp's socket ──

    /// <summary>
    /// How long a page script (or the isolated library's call) may take before the request answers
    /// <c>page_unresponsive</c>: well inside omp's own 30-second socket timer, which would otherwise drop the
    /// connection (and fail omp's next queued request with "cmux socket is not connected").
    /// </summary>
    internal TimeSpan ScriptDeadline { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one batch of native input may take: a batch first waits up to 10 s for the user to pause
    /// (their input to the pane wins), then ends with a round trip through the page.</summary>
    internal TimeSpan InputDeadline { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long omp's own probes (<see cref="TitleProbe"/>, <see cref="WidthProbe"/>, <see cref="GeometryProbe"/>) wait for the page.</summary>
    internal TimeSpan ProbeDeadline { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The time a request's scripts share, from <see cref="Start"/> on: every script of one evaluation (the expression,
    /// the check whether it ran, the statements form) gets what is left, so together they never take longer than
    /// <paramref name="Limit"/>. <paramref name="Asked"/> when omp set the limit (<c>timeout_ms</c>), else the bridge's own.
    /// </summary>
    internal readonly record struct Deadline(TimeSpan Limit, bool Asked, long Started)
    {
        public static Deadline Start(TimeSpan limit, bool asked = false) => new(limit, asked, Stopwatch.GetTimestamp());

        /// <summary>What is left of <see cref="Limit"/> (zero or less: spent).</summary>
        public TimeSpan Left => Limit - Stopwatch.GetElapsedTime(Started);

        public AgentBrowserException Expired() => Asked
            ? new AgentBrowserException("script_timeout", $"The script did not finish within timeout_ms ({Limit.TotalMilliseconds:0} ms).")
            : new AgentBrowserException("page_unresponsive",
                $"The page did not answer within {Limit.TotalSeconds:0.#} s: it is busy or hung. A screenshot and the address still work; load the page again (browser.navigate) or try later.");
    }

    /// <summary>
    /// What omp's cmux backend (the pinned 18.12.2, <c>cmux-tab.ts</c> / <c>rpc.ts</c>) sends as browser.eval scripts
    /// to read what the preview mostly knows: <c>tab.title()</c>; the CSS width before every screenshot (it goes on
    /// without it when the read fails); <c>GEOMETRY_SCRIPT</c> beside every observe's snapshot. Matched exactly.
    /// </summary>
    private const string TitleProbe = "document.title", WidthProbe = "window.innerWidth",
        GeometryProbe = "(() => ({ innerWidth: window.innerWidth, innerHeight: window.innerHeight, dpr: window.devicePixelRatio||1, scrollX: window.scrollX, scrollY: window.scrollY, scrollWidth: document.documentElement.scrollWidth, scrollHeight: document.documentElement.scrollHeight }))()";

    /// <summary>
    /// <paramref name="start"/> (a script, an input batch) given what is left of <paramref name="deadline"/>. Past it the
    /// bridge stops waiting and cancels it, so the engine forgets the request (the Nightly runtime drops a late answer),
    /// and the request answers <c>page_unresponsive</c>, or <c>script_timeout</c> when omp asked for the limit
    /// (browser.eval's <c>timeout_ms</c>); a spent deadline answers so without starting. The caller's own cancellation
    /// stays a cancellation.
    /// </summary>
    private static async Task<T> BoundedAsync<T>(Func<CancellationToken, Task<T>> start, Deadline deadline, CancellationToken ct)
    {
        var left = deadline.Left;
        if (left <= TimeSpan.Zero) throw deadline.Expired();
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(left);
        Task<T>? run = null;
        try
        {
            run = start(timer.Token);
            // An engine that cannot stop its script still answers here on time
            return await run.WaitAsync(timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            if (run is not null) Observe(run);
            throw deadline.Expired();
        }
    }

    /// <summary>A task nobody waits for any more: its failure, if it comes, is observed.</summary>
    private static void Observe(Task run) =>
        _ = run.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>The script deadline browser.eval asked for (<c>timeout_ms</c>, at most <see cref="ScriptTimeoutLimit"/>),
    /// answered a little before omp's own timer for it (<see cref="WaitBudget"/>); null: <see cref="ScriptDeadline"/>.</summary>
    internal static TimeSpan? ScriptTimeout(JsonObject p) =>
        Num(p, "timeout_ms") is { } ms ? WaitBudget(TimeSpan.FromMilliseconds(Math.Clamp(ms, 100, ScriptTimeoutLimit.TotalMilliseconds))) : null;

    /// <summary>
    /// browser.eval: its scripts bounded together by the <c>timeout_ms</c> omp asked for, else <see cref="ScriptDeadline"/>.
    /// omp's probes of what the preview mostly knows get <see cref="ProbeDeadline"/>: when the page does not answer that
    /// fast the title is the one the preview knows, and the width and geometry fail at once (the preview knows neither
    /// the page's scroll nor its pixel ratio), so neither tab.title(), a screenshot nor an observe waits on a hung page.
    /// </summary>
    private async Task<JsonNode?> EvalAsync(IAgentBrowserPage page, string script, JsonObject p, CancellationToken ct)
    {
        if (ScriptTimeout(p) is { } asked) return await EvaluateAsync(page, script, ct, Deadline.Start(asked, asked: true)).ConfigureAwait(false);
        if (script is not (TitleProbe or WidthProbe or GeometryProbe)) return await EvaluateAsync(page, script, ct).ConfigureAwait(false);
        AgentBrowserException hung;
        try
        {
            return await EvaluateAsync(page, script, ct, Deadline.Start(ProbeDeadline)).ConfigureAwait(false);
        }
        catch (AgentBrowserException e) when (e.Code == "page_unresponsive" && script == TitleProbe)
        {
            hung = e;
        }
        return (await page.GetStateAsync(ct).ConfigureAwait(false)).Title is { } title ? JsonValue.Create(title) : throw hung;
    }

    /// <summary>The address the preview knows for the tab (what it loaded, where the page moved since): never a page
    /// script, so a hung page still has one.</summary>
    private static async Task<string> UrlAsync(IAgentBrowserPage page, CancellationToken ct) =>
        (await page.GetStateAsync(ct).ConfigureAwait(false)).Url?.AbsoluteUri ?? "about:blank";

    /// <summary>The value of <paramref name="expression"/> in the page, as JSON. Its scripts share
    /// <paramref name="deadline"/> (null: <see cref="ScriptDeadline"/> from now).</summary>
    internal async Task<JsonNode?> EvaluateAsync(IAgentBrowserPage page, string expression, CancellationToken ct, Deadline? deadline = null)
    {
        var limit = deadline ?? Deadline.Start(ScriptDeadline);
        var run = Interlocked.Increment(ref _run);
        var text = await RunAsync(page, _scripts.Wrap(expression, run, viaEval: false), ct, limit).ConfigureAwait(false);
        if (text is null)
        {
            // Nothing came back: the script did not parse as an expression (nothing ran: try it as statements), or the
            // page went away while it ran (a click that navigates): then there is no value.
            string ran;
            try { ran = Unquote(await RunScriptAsync(page, _scripts.RanCheck(run), ct, limit).ConfigureAwait(false)); }
            catch (IsolatedScriptException) { return null; }
            if (ran != "false") return null;
            text = await RunAsync(page, _scripts.Wrap(expression, run, viaEval: true), ct, limit).ConfigureAwait(false)
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
                "The script returned a Promise, but the Ombra preview evaluates synchronously: return a plain value (poll with waitForFunction for async state).");
        var value = answer["ok"];
        answer.Remove("ok");
        return value;
    }

    /// <summary>The script's answer; null when nothing came back or the engine says the script threw or did not run
    /// (<see cref="IsolatedScriptException"/>). Every other failure (a deadline, a closed window, no page) goes on as it is.</summary>
    private async Task<string?> RunAsync(IAgentBrowserPage page, string script, CancellationToken ct, Deadline? deadline)
    {
        string text;
        try { text = await RunScriptAsync(page, script, ct, deadline).ConfigureAwait(false); }
        catch (IsolatedScriptException) { return null; }
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
            catch (IsolatedScriptException) { return false; } // between two documents
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
        p[name] is { } n && n.GetValueKind() == JsonValueKind.Number ? n.Deserialize<double>() : null;

    /// <summary>browser.set_device's parameters: <c>clear: true</c> ends the device mode (null), else a preset and/or
    /// <c>width</c>+<c>height</c> (whole CSS pixels), <c>dpr</c>, <c>touch</c>, <c>platform</c>, <c>landscape</c>.</summary>
    internal static AgentDeviceRequest? DeviceRequest(JsonObject p)
    {
        if (Bool(p, "clear") == true) return null;
        int? Side(string name) => Num(p, name) switch
        {
            null => null,
            { } v when v == Math.Floor(v) && v >= 1 && v <= 100_000 => (int)v,
            _ => throw new AgentBrowserException("invalid_params", $"browser.set_device: {name} is a whole number of CSS pixels"),
        };
        var request = new AgentDeviceRequest(Str(p, "preset"), Side("width"), Side("height"), Num(p, "dpr"), Bool(p, "touch"),
            Str(p, "platform"), Bool(p, "landscape"));
        if (request.Width.HasValue != request.Height.HasValue)
            throw new AgentBrowserException("invalid_params", "browser.set_device needs both width and height");
        if (request.PixelRatio is < 0 or > 10)
            throw new AgentBrowserException("invalid_params", "browser.set_device: dpr is 0 (the display's) to 10");
        if (request == new AgentDeviceRequest())
            throw new AgentBrowserException("invalid_params", "browser.set_device needs a preset, a width and height, dpr, touch, platform, landscape or clear: true");
        return request;
    }

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

    private static string Clip(string s) => s.Length > 200 ? s[..GraphemeText.PrefixLength(s, 200)] + "…" : s;

    [GeneratedRegex(@"^(?<code>not_found|invalid_params|invalid_target|blocked): (?<message>.*)$", RegexOptions.Singleline)]
    private static partial Regex CodedError();
}
