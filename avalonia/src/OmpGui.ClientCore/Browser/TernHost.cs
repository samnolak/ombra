using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmpGui.Rpc;

namespace OmpGui.ClientCore.Browser;

/// <summary>
/// The app as omp's Tern daemon (omp 18.8.0 pi-coding-agent src/tools/browser/tern/wire.ts): every page omp's browser
/// tool opens is a tab of the browser pane, driven through Tern's browser ops. omp uses Tern when
/// <c>TERN_PANE_SOCKET</c> names a socket and <c>TERN_PANE</c> is a decimal pane id (<see cref="AddTo"/>), and ranks it
/// above cmux.
/// </summary>
/// <remarks>
/// <para>Transport: a Unix stream socket in a folder only this user can enter (a current-user named pipe on Windows).
/// Each frame, both ways, is a u32 little-endian byte length then one UTF-8 JSON object (at most 256 MiB). omp greets
/// with <c>{"hello":{}}</c> and needs <c>{"welcome":{"ops":[…]}}</c> within 10 s; then requests
/// <c>{"id":N,"browser":{op,…}}</c> are answered <c>{"id":N,"browser":{"ok":…}}</c> or <c>{"error":{kind,message}}</c>,
/// in any order (they run concurrently; ops of one tab too).</para>
/// <para>Timeouts are omp's: the host never gives up on a request itself. A request omp abandoned is still answered
/// (omp drops the late answer, and closes a tab whose <c>open</c> answered late). When omp's connection ends, the tabs it
/// opened are closed (a tab the user is looking at stays for them).</para>
/// <para><c>fork</c> (open <c>omp --fork</c> in a pane beside the asking one) is not offered: the welcome lists no ops
/// and a fork request answers <c>unsupported</c>, after which omp's <c>/fork</c> forks in place.</para>
/// </remarks>
public sealed class TernHost : IDisposable
{
    public const string SocketVariable = "TERN_PANE_SOCKET";
    public const string PaneVariable = "TERN_PANE";
    /// <summary>omp's Tern switch; the variable wins over the user's <c>browser.tern</c> in both directions.</summary>
    public const string TernFlagVariable = "PI_BROWSER_TERN";

    /// <summary>omp's frame limit (wire.ts MAX_FRAME_BYTES).</summary>
    internal const int MaxFrameBytes = 256 << 20;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        MaxDepth = 512,
        // Page text stays readable in the answers (JSON for omp, never HTML)
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 512 };

    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<int, TernBlock> _blocks = [];
    private readonly string? _directory;
    private Socket? _listener;
    private string? _pipeName;
    private int _nextPane;
    private int _nextBlock;

    private TernHost(string endpoint, string? directory)
    {
        Endpoint = endpoint;
        _directory = directory;
    }

    /// <summary>The socket path (or <c>\\.\pipe\…</c> on Windows) omp connects to.</summary>
    public string Endpoint { get; }

    /// <summary>The pane the agent opens tabs in; until it is set, <c>open</c> answers <c>no_window</c> (omp then uses its
    /// own browser).</summary>
    public ITernBrowser? Browser { get; set; }

    /// <summary>Starts listening; null (and a line on stderr) when this system offers no local socket.</summary>
    public static TernHost? TryStart()
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var name = "ompgui-tern-" + id;
                var pipe = new TernHost(@"\\.\pipe\" + name, null) { _pipeName = name };
                _ = pipe.AcceptPipesAsync();
                return pipe;
            }
            // A folder only this user can enter; a socket path is at most 104 bytes on macOS
            var root = Path.GetTempPath();
            if (root.Length > 60) root = "/tmp";
            var dir = Path.Combine(root, "ompgui-tern-" + id);
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var path = Path.Combine(dir, "tern.sock");
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(16);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
            var host = new TernHost(path, dir) { _listener = listener };
            _ = host.AcceptSocketsAsync();
            return host;
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            Console.Error.WriteLine("The agent's Tern browser is not available: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// <paramref name="spec"/> with the variables that send omp's browser tool here: the socket, a pane id of its own
    /// (echoed in omp's <c>open</c>), and <c>PI_BROWSER_TERN=1</c> (wins over the user's <c>browser.tern: false</c>).
    /// Unchanged when the user's settings environment names their own Tern socket; a <c>PI_BROWSER_TERN</c> they set
    /// stays as they set it.
    /// </summary>
    public OmpLaunchSpec AddTo(OmpLaunchSpec spec)
    {
        if (spec.Environment.ContainsKey(SocketVariable)) return spec;
        var env = new Dictionary<string, string?>(spec.Environment)
        {
            [SocketVariable] = Endpoint,
            [PaneVariable] = Interlocked.Increment(ref _nextPane).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        env.TryAdd(TernFlagVariable, "1");
        return spec with { Environment = env };
    }

    /// <summary>The open tab omp addresses as <paramref name="id"/>, for the app (null when there is none).</summary>
    public TernBlock? Block(int id)
    {
        lock (_gate) return _blocks.GetValueOrDefault(id);
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
            _ = new Connection(this, new NetworkStream(client, ownsSocket: true)).RunAsync(ct);
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
            _ = new Connection(this, pipe).RunAsync(ct);
        }
    }

    /// <summary>One frame: null at the end of the stream. Throws <see cref="InvalidDataException"/> for a frame omp's
    /// own reader refuses (too large, not UTF-8 JSON, not an object).</summary>
    internal static async Task<JsonObject?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await FillAsync(stream, header, ct).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxFrameBytes) throw new InvalidDataException($"A Tern frame of {length} bytes is over the limit.");
        var body = new byte[length];
        if (!await FillAsync(stream, body, ct).ConfigureAwait(false)) throw new EndOfStreamException("The Tern frame was cut short.");
        try
        {
            return JsonNode.Parse(body, documentOptions: ReadOptions) as JsonObject
                ?? throw new InvalidDataException("A Tern frame is not a JSON object.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("A Tern frame is not UTF-8 JSON: " + e.Message, e);
        }
    }

    /// <summary>The frame bytes of <paramref name="message"/>: u32 LE length, then its UTF-8 JSON.</summary>
    internal static byte[] EncodeFrame(JsonObject message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, WriteOptions);
        if (json.Length > MaxFrameBytes) throw new InvalidDataException($"A Tern answer of {json.Length} bytes is over the limit.");
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length);
        json.CopyTo(frame, 4);
        return frame;
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return read == 0 && buffer.Length > 0 ? false : throw new EndOfStreamException("The Tern frame was cut short.");
            read += n;
        }
        return true;
    }

    // ── Ops without a block ──

    private async Task<JsonObject> OpenAsync(Connection connection, JsonObject p, CancellationToken ct)
    {
        var browser = Browser ?? throw new TernException(TernException.NoWindow, "The OMP GUI window is not ready yet.");
        var owner = (int)(TernBlock.Num(p, "owner") ?? 0);
        var width = (int)Math.Round(TernBlock.Num(p, "width") ?? 1365);
        var height = (int)Math.Round(TernBlock.Num(p, "height") ?? 768);
        var block = new TernBlock(Interlocked.Increment(ref _nextBlock), owner);
        var page = await browser.OpenAsync(new TernOpenRequest(owner, width, height), block, ct).ConfigureAwait(false);
        block.Attach(page);
        lock (_gate) _blocks[block.Id] = block;
        connection.Own(block);
        if (TernBlock.Str(p, "url") is { Length: > 0 } url && url != "about:blank")
        {
            try
            {
                await block.DispatchAsync("goto", new JsonObject { ["url"] = url }, ct).ConfigureAwait(false);
            }
            catch
            {
                await CloseAsync(connection, block, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        return new JsonObject { ["block"] = block.Id };
    }

    private async Task CloseAsync(Connection connection, TernBlock block, CancellationToken ct)
    {
        lock (_gate) _blocks.Remove(block.Id);
        connection.Disown(block);
        block.Detach();
        await block.Page.CloseAsync(ct).ConfigureAwait(false);
    }

    private async Task<JsonObject> BrowserAsync(Connection connection, JsonObject p, CancellationToken ct)
    {
        var op = TernBlock.Str(p, "op") ?? throw new TernException(TernException.Invalid, "A browser request needs an op.");
        switch (op)
        {
            case "open":
                return await OpenAsync(connection, p, ct).ConfigureAwait(false);
            case "clipboard":
            {
                if (TernBlock.Str(p, "text") is not { } text)
                    throw new TernException(TernException.Unsupported, "omp can't read the user's clipboard in the OMP GUI browser.");
                var browser = Browser ?? throw new TernException(TernException.NoWindow, "The OMP GUI window is not ready yet.");
                await browser.WriteClipboardAsync(text, ct).ConfigureAwait(false);
                return [];
            }
        }
        if (TernBlock.Num(p, "block") is not { } id) throw new TernException(TernException.Invalid, $"The {op} op needs a block.");
        TernBlock? block;
        lock (_gate) block = _blocks.GetValueOrDefault((int)id);
        if (block is null) throw new TernException(TernException.NotFound, $"No Tern block {id} is open.");
        if (op == "close")
        {
            await CloseAsync(connection, block, ct).ConfigureAwait(false);
            return [];
        }
        return await block.DispatchAsync(op, p, ct).ConfigureAwait(false);
    }

    /// <summary>One omp process: its greeting, then pipelined requests, each answered as it finishes.</summary>
    private sealed class Connection(TernHost host, Stream stream)
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly HashSet<TernBlock> _owned = [];

        public void Own(TernBlock block)
        {
            lock (_owned) _owned.Add(block);
        }

        public void Disown(TernBlock block)
        {
            lock (_owned) _owned.Remove(block);
        }

        public async Task RunAsync(CancellationToken appClosing)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(appClosing);
            var ct = cts.Token;
            try
            {
                // The greeting first; anything else is not omp
                var hello = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
                if (hello is null || !hello.ContainsKey("hello")) return;
                await WriteAsync(new JsonObject { ["welcome"] = new JsonObject { ["ops"] = new JsonArray() } }, ct).ConfigureAwait(false);
                while (await ReadFrameAsync(stream, ct).ConfigureAwait(false) is { } frame)
                {
                    if (frame["id"] is not { } idNode || idNode.GetValueKind() != JsonValueKind.Number) continue;
                    var channel = frame.Select(kv => kv.Key).FirstOrDefault(k => k != "id");
                    if (channel is null) continue;
                    _ = AnswerAsync(idNode.DeepClone(), channel, frame[channel], ct);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException or SocketException)
            {
                // omp went away, sent a frame its own reader would refuse, or the app is closing
            }
            finally
            {
                cts.Cancel(); // requests still running for this omp stop
                TernBlock[] owned;
                lock (_owned) owned = [.. _owned];
                foreach (var block in owned)
                {
                    try { await host.CloseAsync(this, block, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception e) when (e is TernException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
                }
                try { await stream.DisposeAsync().ConfigureAwait(false); }
                catch (IOException) { }
            }
        }

        private async Task AnswerAsync(JsonNode id, string channel, JsonNode? body, CancellationToken ct)
        {
            JsonObject answer;
            try
            {
                var result = channel switch
                {
                    "browser" => await host.BrowserAsync(this, body as JsonObject ?? throw new TernException(TernException.Invalid, "A browser request must be an object."), ct).ConfigureAwait(false),
                    "fork" => throw new TernException(TernException.Unsupported, "The OMP GUI can't open a forked omp in a pane of its own: /fork forks in place."),
                    _ => throw new TernException(TernException.Unsupported, $"The OMP GUI does not answer {channel} requests."),
                };
                answer = new JsonObject { ["ok"] = result };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // the connection is gone
            }
            catch (Exception e)
            {
                var (kind, message) = e switch
                {
                    TernException t => (t.Kind, t.Message),
                    OperationCanceledException => (TernException.Failed, "The request was cancelled."),
                    JsonException or FormatException or InvalidOperationException => (TernException.Invalid, e.Message),
                    _ => (TernException.Failed, e.Message),
                };
                if (e is not (TernException or OperationCanceledException or JsonException or FormatException))
                    Console.Error.WriteLine("Tern request failed: " + e);
                answer = Error(kind, message);
            }
            byte[] frame;
            try
            {
                frame = EncodeFrame(new JsonObject { ["id"] = id, [channel] = answer });
            }
            catch (InvalidDataException e)
            {
                // A capture or PDF too large for one frame
                frame = EncodeFrame(new JsonObject { ["id"] = id.DeepClone(), [channel] = Error(TernException.Failed, e.Message) });
            }
            try
            {
                await WriteAsync(frame, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // omp went away before the answer
            }
        }

        private static JsonObject Error(string kind, string message) =>
            new() { ["error"] = new JsonObject { ["kind"] = kind, ["message"] = message } };

        private Task WriteAsync(JsonObject message, CancellationToken ct) => WriteAsync(EncodeFrame(message), ct);

        private async Task WriteAsync(byte[] frame, CancellationToken ct)
        {
            await _write.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(frame, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }
    }
}
