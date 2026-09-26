using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;

namespace OmpGui.Rpc;

public sealed class RpcConnectionOptions
{
    /// <summary>Default deadline for a request's response. Prompts are acknowledged immediately, so this bounds acks, not runs.</summary>
    public TimeSpan DefaultRequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Bounded event queue. When full, stdout reading pauses (backpressure reaches omp through the pipe); nothing is dropped.</summary>
    public int EventQueueCapacity { get; init; } = 4096;
}

/// <summary>
/// JSONL RPC over a pair of streams (the child's stdout and stdin): framing, v2 reassembly,
/// request/response correlation and a bounded event queue. Knows nothing about processes or UI.
/// Reading runs on its own task and never depends on the consumer's thread.
/// </summary>
public sealed class RpcConnection : IAsyncDisposable
{
    // omp caps physical frames at 1 MiB; allow slack so a slightly larger foreign line is reported, not fatal.
    private const int MaxLineBytes = RpcFrameDecoder.MaxFrameBytes + 64 * 1024;
    // omp parses with JSON.parse (no depth limit); tool args and results can nest deeply.
    internal static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 1024 };
    // Non-ASCII stays UTF-8 instead of \uXXXX, so a prompt costs its real size against the 1 MiB line limit.
    private static readonly JsonWriterOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Stream _output;
    private readonly RpcConnectionOptions _options;
    private readonly Channel<RpcFrame> _events;
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource<RpcReady> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly RpcFrameDecoder _decoder = new();
    private readonly Task _readLoop;
    private long _nextId;
    private int _protocolVersion = 1;
    private Exception? _closeReason;

    private sealed record Pending(string Command, TaskCompletionSource<JsonElement> Tcs, RpcTimings Timings);

    public RpcConnection(Stream input, Stream output, RpcConnectionOptions? options = null)
    {
        _output = output;
        _options = options ?? new RpcConnectionOptions();
        _events = Channel.CreateBounded<RpcFrame>(new BoundedChannelOptions(_options.EventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        _readLoop = Task.Run(() => ReadLoopAsync(input));
    }

    /// <summary>Every non-response frame, plus responses nobody is waiting for. Completes when stdout ends.</summary>
    public ChannelReader<RpcFrame> Events => _events.Reader;

    /// <summary>Completes (never faults) when the read side has ended; <see cref="CloseReason"/> says why.</summary>
    public Task Completion => _readLoop;
    public Exception? CloseReason => _closeReason;
    public int ProtocolVersion => _protocolVersion;
    public int PendingRequestCount => _pending.Count;

    // Read-side counters for diagnostics (written by the read loop only).
    private long _bytesInLines;
    private long _lines;
    private long _pendingBytes;
    private string _pendingHead = "";
    /// <summary>Lines and bytes read so far, plus the unterminated tail currently buffered.</summary>
    public string ReadDiagnostics => $"lines={Interlocked.Read(ref _lines)} lineBytes={Interlocked.Read(ref _bytesInLines)} pendingBytes={Interlocked.Read(ref _pendingBytes)} pendingHead={_pendingHead}";

    /// <summary>
    /// Sees each event frame (type, frame) in the order it was read, before it is queued; returning true consumes it.
    /// Called on the read loop, so a frame before a response is seen before that response's request completes.
    /// Keep it short and never block in it.
    /// </summary>
    public Func<string, JsonElement, bool>? Intercept { get; set; }

    public Task<RpcReady> WaitReadyAsync(CancellationToken ct = default) => _ready.Task.WaitAsync(ct);

    /// <summary>Selects protocol v2 when the ready frame advertises it. Returns the version in use.</summary>
    public async Task<int> NegotiateAsync(CancellationToken ct = default)
    {
        var ready = await WaitReadyAsync(ct).ConfigureAwait(false);
        // Like the official RpcClient: only opt in when the advertised limits are the ones our decoder enforces.
        if (!ready.SupportsV2 || ready.MaxFrameBytes != RpcFrameDecoder.MaxFrameBytes ||
            ready.MaxReassembledFrameBytes != RpcFrameDecoder.MaxReassembledBytes)
            return _protocolVersion;
        var response = await RequestAsync("negotiate_protocol", w => w.WriteNumber("protocolVersion", 2), ct: ct).ConfigureAwait(false);
        if (!response.Success) throw new RpcCommandException("negotiate_protocol", response.Error ?? "unknown error");
        // The read loop switched to v2 when it saw this response, before any chunk can follow it.
        return _protocolVersion;
    }

    /// <summary>
    /// Sends a command with a fresh id and awaits its response (success or failure). The deadline covers waiting
    /// for the writer and for the response; a write already in progress is not interrupted (a half-written line
    /// would corrupt the next command), so a peer that stops reading stdin can hold a request past its deadline.
    /// </summary>
    public async Task<RpcResponse> RequestAsync(string type, Action<Utf8JsonWriter>? body = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var timings = new RpcTimings { Sent = Stopwatch.GetTimestamp() };
        var id = "g" + Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pending = new Pending(type, new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously), timings);
        if (_closeReason is not null) throw new RpcConnectionClosedException("RPC connection is closed", _closeReason);
        _pending[id] = pending;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? _options.DefaultRequestTimeout);
        try
        {
            await WriteFrameAsync(type, id, body, deadline.Token).ConfigureAwait(false);
            timings.Flushed = Stopwatch.GetTimestamp();
            // Recheck after registering: the reader may have closed between the first check and the write.
            if (_closeReason is not null) pending.Tcs.TrySetException(new RpcConnectionClosedException("RPC connection is closed", _closeReason));
            var frame = await pending.Tcs.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            timings.Delivered = Stopwatch.GetTimestamp();
            return ToResponse(type, frame, timings);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{type} got no response within {(timeout ?? _options.DefaultRequestTimeout).TotalSeconds:0.#}s");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Writes one frame that has no response (e.g. <c>extension_ui_response</c>).</summary>
    public Task SendAsync(string type, string? id, Action<Utf8JsonWriter>? body, CancellationToken ct = default) =>
        WriteFrameAsync(type, id, body, ct);

    private async Task WriteFrameAsync(string type, string? id, Action<Utf8JsonWriter>? body, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, WriteOptions))
        {
            w.WriteStartObject();
            if (id is not null) w.WriteString("id", id);
            w.WriteString("type", type);
            body?.Invoke(w);
            w.WriteEndObject();
        }
        buffer.Write("\n"u8);
        if (buffer.WrittenCount > RpcFrameDecoder.MaxFrameBytes)
            throw new ArgumentException($"{type} is {buffer.WrittenCount} bytes; omp accepts at most {RpcFrameDecoder.MaxFrameBytes} per line");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Not cancellable once started: a half-written line would corrupt the next command.
            await _output.WriteAsync(buffer.WrittenMemory, CancellationToken.None).ConfigureAwait(false);
            await _output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new RpcConnectionClosedException("omp stdin is closed", e);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static RpcResponse ToResponse(string command, JsonElement frame, RpcTimings timings)
    {
        var success = frame.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        string? error = frame.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        JsonElement? data = frame.TryGetProperty("data", out var d) ? d : null;
        return new RpcResponse(command, success, success ? null : error ?? "unknown error", data, timings);
    }

    private async Task ReadLoopAsync(Stream input)
    {
        var reader = PipeReader.Create(input, new StreamPipeReaderOptions(bufferSize: 64 * 1024, minimumReadSize: 16 * 1024));
        var ct = _shutdown.Token;
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;
                while (TryReadLine(ref buffer, out var line))
                {
                    Interlocked.Increment(ref _lines);
                    Interlocked.Add(ref _bytesInLines, line.Length + 1);
                    var ts = Stopwatch.GetTimestamp();
                    await HandleLineAsync(line, ts, ct).ConfigureAwait(false);
                }
                if (buffer.Length > MaxLineBytes)
                {
                    // A line this long cannot come from omp; drop what we have and resync at the next newline.
                    await EmitAsync(Synthetic(RpcFrame.ProtocolError, $"stdout line exceeded {MaxLineBytes} bytes; discarded"), ct).ConfigureAwait(false);
                    buffer = buffer.Slice(buffer.End);
                    _decoder.Reset();
                }
                Interlocked.Exchange(ref _pendingBytes, buffer.Length);
                _pendingHead = buffer.IsEmpty ? "" : Preview(buffer.Length > 120 ? buffer.Slice(0, 120) : buffer);
                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    if (!buffer.IsEmpty) await HandleLineAsync(buffer, Stopwatch.GetTimestamp(), ct).ConfigureAwait(false);
                    Close(new RpcConnectionClosedException("omp stdout reached end of stream"));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Close(new RpcConnectionClosedException("RPC connection disposed"));
        }
        catch (RpcConnectionClosedException e)
        {
            Close(e); // already says why (e.g. a framing error), keep it as the close reason
        }
        catch (Exception e)
        {
            Close(new RpcConnectionClosedException("reading omp stdout failed: " + e.Message, e));
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var pos = buffer.PositionOf((byte)'\n');
        if (pos is null)
        {
            line = default;
            return false;
        }
        line = buffer.Slice(0, pos.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, pos.Value));
        return true;
    }

    private async ValueTask HandleLineAsync(ReadOnlySequence<byte> line, long ts, CancellationToken ct)
    {
        var bytes = (int)line.Length;
        if (IsWhitespace(line)) return;
        JsonElement parsed;
        try
        {
            // Clone is required: the document can point straight into the pipe's buffer, which is reused
            // as soon as the reader advances (removing it corrupted tool events in tests).
            using var doc = JsonDocument.Parse(line, ParseOptions);
            parsed = doc.RootElement.Clone();
        }
        catch (JsonException e)
        {
            await EmitAsync(Synthetic(RpcFrame.Malformed, e.Message, Preview(line)), ct).ConfigureAwait(false);
            return;
        }
        if (parsed.ValueKind != JsonValueKind.Object)
        {
            await EmitAsync(Synthetic(RpcFrame.Malformed, "frame is not a JSON object", Preview(line)), ct).ConfigureAwait(false);
            return;
        }

        JsonElement? logical;
        try
        {
            if (_protocolVersion == 1 && parsed.TryGetProperty("type", out var pt) && pt.ValueEquals("rpc_chunk"))
                throw new RpcProtocolException("rpc_chunk received before protocol v2 was negotiated");
            logical = _decoder.Push(parsed);
        }
        catch (RpcProtocolException e)
        {
            // Like the official client: a broken v2 sequence means a lost logical frame (maybe a response or
            // agent_end), so the stream cannot be trusted any more. Report it and end the connection.
            await EmitAsync(Synthetic(RpcFrame.ProtocolError, e.Message), ct).ConfigureAwait(false);
            throw new RpcConnectionClosedException("omp RPC framing error: " + e.Message, e);
        }
        if (logical is not { } frame) return;

        var type = frame.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        var id = frame.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;

        if (type == "ready" && !_ready.Task.IsCompleted)
        {
            _ready.TrySetResult(ParseReady(frame));
            return;
        }
        if (type == "response" && id is not null && _pending.TryGetValue(id, out var pending))
        {
            pending.Timings.ResponseRead = ts;
            // Switch before reading on: omp may start chunking right after this response.
            if (pending.Command == "negotiate_protocol" && IsV2Ack(frame)) _protocolVersion = 2;
            if (pending.Tcs.TrySetResult(frame)) return;
        }

        if (type == "response")
        {
            // Nobody is waiting: a late response after a timeout/cancel, a duplicate, a same-id async prompt
            // failure, or an id-less failure (unknown command, parse). Surface it; never drop it silently.
            await EmitAsync(new RpcFrame(RpcFrame.UnmatchedResponse, id, frame, ts, bytes), ct).ConfigureAwait(false);
            return;
        }
        if (Intercept is { } hook && hook(type, frame)) return;
        await EmitAsync(new RpcFrame(type, id, frame, ts, bytes), ct).ConfigureAwait(false);
    }

    private static bool IsV2Ack(JsonElement f) =>
        f.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True &&
        f.TryGetProperty("command", out var c) && c.ValueEquals("negotiate_protocol") &&
        f.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
        d.TryGetProperty("protocolVersion", out var v) && v.TryGetInt32(out var n) && n == 2;

    private ValueTask EmitAsync(RpcFrame frame, CancellationToken ct) => _events.Writer.WriteAsync(frame, ct);

    private static RpcReady ParseReady(JsonElement f)
    {
        int Int(string name, int fallback) => f.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : fallback;
        var versions = new List<int>();
        if (f.TryGetProperty("supportedProtocolVersions", out var s) && s.ValueKind == JsonValueKind.Array)
            foreach (var v in s.EnumerateArray())
                if (v.TryGetInt32(out var n)) versions.Add(n);
        if (versions.Count == 0) versions.Add(1);
        return new RpcReady(Int("protocolVersion", 1), versions, Int("maxFrameBytes", RpcFrameDecoder.MaxFrameBytes),
            Int("maxReassembledFrameBytes", RpcFrameDecoder.MaxReassembledBytes));
    }

    private static RpcFrame Synthetic(string type, string message, string? preview = null)
    {
        var json = JsonSerializer.SerializeToElement(new { type, message, preview });
        return new RpcFrame(type, null, json, Stopwatch.GetTimestamp(), 0);
    }

    private static bool IsWhitespace(ReadOnlySequence<byte> line)
    {
        foreach (var seg in line)
            foreach (var b in seg.Span)
                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r')) return false;
        return true;
    }

    private static string Preview(ReadOnlySequence<byte> line)
    {
        var slice = line.Length > 200 ? line.Slice(0, 200) : line;
        return System.Text.Encoding.UTF8.GetString(slice.ToArray());
    }

    private void Close(Exception reason)
    {
        Interlocked.CompareExchange(ref _closeReason, reason, null);
        _ready.TrySetException(_closeReason!);
        foreach (var p in _pending.Values) p.Tcs.TrySetException(_closeReason!);
        _events.Writer.TryComplete();
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _readLoop.ConfigureAwait(false);
            return;
        }
        _shutdown.Cancel();
        await _readLoop.ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
