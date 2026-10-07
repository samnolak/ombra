using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

/// <summary>The preview panel as the agent's browser (implemented by the app on its UI thread).</summary>
public interface IAgentBrowserPage
{
    /// <summary>Shows the preview without loading anything (an empty tab).</summary>
    Task ShowAsync(CancellationToken ct);

    /// <summary>Shows the preview and starts loading <paramref name="address"/>; the address loading. Throws
    /// <see cref="AgentBrowserException"/> when the preview refuses it (not http/https) or cannot show pages.</summary>
    Task<Uri> NavigateAsync(string address, CancellationToken ct);

    Task<AgentPageState> GetStateAsync(CancellationToken ct);

    /// <summary>Runs <paramref name="script"/> in the page: the engine's result as text ("" when nothing came back).
    /// Throws <see cref="AgentBrowserException"/> ("no_page") when no page is open.</summary>
    Task<string> RunScriptAsync(string script, CancellationToken ct);

    /// <summary>Captures what the preview shows as a PNG. Throws <see cref="AgentBrowserException"/>: "no_page" when no
    /// page is open, "not_supported" where the web engine cannot capture, "screenshot_failed" when the capture failed.</summary>
    Task<AgentScreenshot> CaptureAsync(CancellationToken ct);
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
/// The preview shows one page: a new tab (<c>browser.open_split</c>) takes it over and the earlier tab's requests are
/// refused with a clear message. Screenshots capture the visible part of the page only (the web view's own snapshot
/// call: no element clip, no full page), the same as a cmux surface.
/// </remarks>
public sealed partial class AgentBrowserBridge : IDisposable
{
    public const string SocketVariable = "CMUX_SOCKET_PATH";
    public const string PasswordVariable = "CMUX_SOCKET_PASSWORD";

    /// <summary>What omp reads from a real cmux terminal: removed, so omp never addresses a cmux window instead.</summary>
    private static readonly string[] CmuxVariables = ["CMUX_WORKSPACE_ID", "CMUX_SURFACE_ID", "CMUX_RELAY_ID", "CMUX_RELAY_TOKEN"];

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
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
    private string? _surface;
    private long _run;

    private AgentBrowserBridge(string endpoint, string password, string? directory)
    {
        Endpoint = endpoint;
        Password = password;
        _directory = directory;
    }

    /// <summary>For tests: a bridge that only dispatches (no socket).</summary>
    internal AgentBrowserBridge(IAgentBrowserPage page) : this("", "", null) => Page = page;

    /// <summary>The socket path (or <c>\\.\pipe\…</c> on Windows) omp connects to.</summary>
    public string Endpoint { get; }

    /// <summary>What omp sends first (<c>auth &lt;password&gt;</c>).</summary>
    public string Password { get; }

    /// <summary>The page the agent drives; until it is set, requests are answered "unavailable".</summary>
    public IAgentBrowserPage? Page { get; set; }

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
    /// <paramref name="spec"/> with the variables that send omp's browser tool here. A variable the user set in the
    /// settings' environment stays as they set it (e.g. their own CMUX_SOCKET_PATH); PI_BROWSER_CMUX=0 or omp's
    /// <c>browser.cmux: false</c> turn this off on omp's side.
    /// </summary>
    public OmpLaunchSpec AddTo(OmpLaunchSpec spec)
    {
        var env = new Dictionary<string, string?>(spec.Environment);
        if (env.ContainsKey(SocketVariable)) return spec;
        env[SocketVariable] = Endpoint;
        env[PasswordVariable] = Password;
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
        var page = Page ?? throw new AgentBrowserException("unavailable", "The OMP GUI window is not ready yet.");
        switch (method)
        {
            case "system.ping":
                return new JsonObject { ["pong"] = true };
            case "browser.open_split":
                return await OpenAsync(page, p, ct).ConfigureAwait(false);
            case "surface.close":
                lock (_gate)
                {
                    // The page stays in the preview for the user to see; this tab is over.
                    if (Str(p, "surface_id") is { } closing && closing == _surface) _surface = null;
                }
                return new JsonObject();
        }
        if (!method.StartsWith("browser.", StringComparison.Ordinal))
            throw new AgentBrowserException("method_not_found", $"Unknown method {method}");
        var surface = Surface(p);
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
                var value = await EvaluateAsync(page, AgentBrowserScripts.Call("snapshot", interactive, depth, !interactive), ct).ConfigureAwait(false);
                result = value as JsonObject ?? throw new AgentBrowserException("js_error", "The page did not return a snapshot.");
                break;
            }
            case "browser.click" or "browser.dblclick" or "browser.hover" or "browser.focus" or "browser.check" or "browser.uncheck"
                or "browser.scroll_into_view":
                await EvaluateAsync(page, AgentBrowserScripts.Call("act", method["browser.".Length..], Selector(p), null), ct).ConfigureAwait(false);
                result = new JsonObject();
                break;
            case "browser.type" or "browser.fill":
                await EvaluateAsync(page, AgentBrowserScripts.Call("act", method["browser.".Length..], Selector(p),
                    new JsonObject { ["text"] = Str(p, "text") ?? "" }), ct).ConfigureAwait(false);
                result = new JsonObject();
                break;
            case "browser.press":
            {
                var key = Str(p, "key") ?? throw new AgentBrowserException("invalid_params", "browser.press needs a key");
                await EvaluateAsync(page, AgentBrowserScripts.Call("press", key), ct).ConfigureAwait(false);
                result = new JsonObject();
                break;
            }
            case "browser.scroll":
            {
                var dx = Num(p, "dx") ?? 0;
                var dy = Num(p, "dy") ?? 0;
                var script = Str(p, "selector") is { } selector
                    ? AgentBrowserScripts.Call("act", "scroll", selector, new JsonObject { ["dx"] = dx, ["dy"] = dy })
                    : AgentBrowserScripts.Call("scroll", dx, dy);
                await EvaluateAsync(page, script, ct).ConfigureAwait(false);
                result = new JsonObject();
                break;
            }
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

    private async Task<JsonObject> OpenAsync(IAgentBrowserPage page, JsonObject p, CancellationToken ct)
    {
        var address = Str(p, "url") is { Length: > 0 } u ? u : "about:blank";
        string url;
        if (address == "about:blank")
        {
            await page.ShowAsync(ct).ConfigureAwait(false);
            url = await UrlAsync(page, ct).ConfigureAwait(false);
        }
        else url = (await page.NavigateAsync(address, ct).ConfigureAwait(false)).AbsoluteUri;
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        lock (_gate) _surface = id;
        return new JsonObject { ["surface_id"] = id, ["url"] = url, ["workspace_id"] = "omp-gui", ["created_split"] = false };
    }

    /// <summary>The tab a request names: the current one (or none named).</summary>
    private string Surface(JsonObject p)
    {
        var asked = Str(p, "surface_id");
        lock (_gate)
        {
            if (_surface is null)
                throw new AgentBrowserException("not_found", "No browser tab is open in the OMP GUI preview: open one first.");
            if (asked is not null && asked != _surface)
                throw new AgentBrowserException("not_found",
                    "This tab was replaced by a newer one: the OMP GUI preview shows one page at a time. Use the newest tab, or open this one again.");
            return _surface;
        }
    }

    private static async Task<string> UrlAsync(IAgentBrowserPage page, CancellationToken ct)
    {
        try
        {
            var href = await page.RunScriptAsync("location.href", ct).ConfigureAwait(false);
            href = Unquote(href);
            if (Uri.TryCreate(href, UriKind.Absolute, out var u)) return u.AbsoluteUri;
        }
        catch (Exception e) when (e is not OperationCanceledException && e is not AgentBrowserException { Code: not "no_page" })
        {
            // No page yet, or the engine refused the script mid-navigation: the address the preview knows
        }
        var state = await page.GetStateAsync(ct).ConfigureAwait(false);
        return state.Url?.AbsoluteUri ?? "about:blank";
    }

    /// <summary>The value of <paramref name="expression"/> in the page, as JSON.</summary>
    internal async Task<JsonNode?> EvaluateAsync(IAgentBrowserPage page, string expression, CancellationToken ct)
    {
        var run = Interlocked.Increment(ref _run);
        var text = await RunAsync(page, AgentBrowserScripts.Wrap(expression, run, viaEval: false), ct).ConfigureAwait(false);
        if (text is null)
        {
            // Nothing came back: the script did not parse as an expression (nothing ran: try it as statements), or the
            // page went away while it ran (a click that navigates): then there is no value.
            string ran;
            try { ran = Unquote(await page.RunScriptAsync(AgentBrowserScripts.RanCheck(run), ct).ConfigureAwait(false)); }
            catch (Exception e) when (e is not OperationCanceledException and not AgentBrowserException) { return null; }
            if (ran != "false") return null;
            text = await RunAsync(page, AgentBrowserScripts.Wrap(expression, run, viaEval: true), ct).ConfigureAwait(false)
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

    private static async Task<string?> RunAsync(IAgentBrowserPage page, string script, CancellationToken ct)
    {
        string text;
        try { text = await page.RunScriptAsync(script, ct).ConfigureAwait(false); }
        catch (AgentBrowserException) { throw; }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
        text = Unquote(text);
        return text.Length == 0 || text == "null" || text == "undefined" ? null : text;
    }

    /// <summary>browser.wait: a load state, a selector, a URL part, until the deadline.</summary>
    private async Task WaitAsync(IAgentBrowserPage page, JsonObject p, CancellationToken ct)
    {
        var timeout = Math.Clamp(Num(p, "timeout_ms") ?? 30_000, 0, 600_000);
        var selector = Str(p, "selector");
        var urlPart = Str(p, "url_contains");
        var load = Str(p, "load_state") ?? (selector is null && urlPart is null ? "complete" : null);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var state = await page.GetStateAsync(ct).ConfigureAwait(false);
            if (state.Unavailable is { } why) throw new AgentBrowserException("unavailable", why);
            if (await MetAsync(page, state, load, selector, urlPart, ct).ConfigureAwait(false))
            {
                if (load is not null && state.LoadFailed)
                    throw new AgentBrowserException("navigation_failed",
                        $"{state.Url?.AbsoluteUri ?? "The page"} did not load: is the server running?");
                return;
            }
            if (clock.Elapsed.TotalMilliseconds >= timeout)
            {
                var what = selector is not null ? $"selector {selector}" : urlPart is not null ? $"a URL containing {urlPart}" : $"the page to be {load}";
                throw new AgentBrowserException("timeout", $"Timed out after {timeout:0} ms waiting for {what}");
            }
            await Task.Delay(Poll, ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> MetAsync(IAgentBrowserPage page, AgentPageState state, string? load, string? selector, string? urlPart, CancellationToken ct)
    {
        if (load is not null)
        {
            if (state.IsLoading) return false;
            string ready;
            try { ready = Unquote(await page.RunScriptAsync("document.readyState", ct).ConfigureAwait(false)); }
            catch (AgentBrowserException e) when (e.Code == "no_page") { return true; } // an empty tab is loaded
            catch (Exception e) when (e is not OperationCanceledException and not AgentBrowserException) { return false; } // between two documents
            if (load == "interactive" ? ready == "loading" : ready is not ("complete" or "")) return false;
        }
        if (urlPart is not null && !(await UrlAsync(page, ct).ConfigureAwait(false)).Contains(urlPart, StringComparison.Ordinal)) return false;
        if (selector is not null)
        {
            try
            {
                var found = await EvaluateAsync(page, "!!(" + AgentBrowserScripts.Library + ").resolve(" + AgentBrowserScripts.Literal(selector) + ")", ct).ConfigureAwait(false);
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

    [GeneratedRegex(@"^(?<code>not_found|invalid_params|invalid_target): (?<message>.*)$", RegexOptions.Singleline)]
    private static partial Regex CodedError();
}
