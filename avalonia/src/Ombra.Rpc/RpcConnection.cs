using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;

namespace Ombra.Rpc;

public sealed class RpcConnectionOptions
{
    private TimeSpan? _defaultRequestTimeout;
    /// <summary>
    /// Ordinary requests allow 40 seconds; transcript, tree and entry reads allow 120 seconds.
    /// Setting this property explicitly overrides both categories. A per-request timeout takes precedence.
    /// Prompts are acknowledged immediately, so their deadline bounds acknowledgements, not runs.
    /// </summary>
    public TimeSpan DefaultRequestTimeout
    {
        get => _defaultRequestTimeout ?? TimeSpan.FromSeconds(40);
        init => _defaultRequestTimeout = value;
    }
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    internal TimeSpan RequestTimeout(string command) => _defaultRequestTimeout ?? (command switch
    {
        "engine_read_session" or "get_messages" or "get_messages_page" or
        "get_tree" or "get_entries" or "get_branch_messages" or "get_subagent_messages" => TimeSpan.FromSeconds(120),
        _ => DefaultRequestTimeout,
    });

    /// <summary>Bounded wire-event queue. When full, stdout reading pauses; only redundant unread text-tool snapshots may be coalesced by the reader.</summary>
    public int EventQueueCapacity { get; init; } = 4096;
}

/// <summary>
/// Request/response correlation and a bounded event queue for one RPC session. A stream-backed
/// connection owns framing and v2 reassembly; engine sessions receive already-decoded frames
/// from their shared root transport and never own its streams.
/// </summary>
public sealed class RpcConnection : IAsyncDisposable
{
    // omp caps physical frames at 1 MiB; allow slack so a slightly larger foreign line is reported, not fatal.
    private const int MaxLineBytes = RpcFrameDecoder.MaxFrameBytes + 64 * 1024;
    // omp parses with JSON.parse (no depth limit); tool args and results can nest deeply.
    internal static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 1024 };
    // Non-ASCII stays UTF-8 instead of \uXXXX, so a prompt costs its real size against the 1 MiB line limit.
    private static readonly JsonWriterOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Stream? _output;
    private readonly RpcConnectionOptions _options;
    private readonly Channel<RpcFrame> _events;
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ChannelReader<RpcFrame> _eventReader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TaskCompletionSource<RpcReady> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly RpcFrameDecoder? _decoder;
    private readonly Task _readLoop;
    private readonly TaskCompletionSource _logicalCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string, string?, Action<Utf8JsonWriter>?, CancellationToken, Task>? _sendFrame;
    private readonly Func<ValueTask>? _closeSession;
    private readonly Func<JsonElement, long, int, CancellationToken, ValueTask<bool>>? _routeFrame;
    private int _framePhysicalBytes;
    private long _nextId;
    private long _eventSequence;
    private long _coalescingBoundary;
    private int _protocolVersion = 1;
    private Exception? _closeReason;

    private sealed record Pending(string Command, TaskCompletionSource<JsonElement> Tcs, RpcTimings Timings);

    private RpcConnection(RpcConnectionOptions? options)
    {
        _options = options ?? new RpcConnectionOptions();
        _events = Channel.CreateBounded<RpcFrame>(new BoundedChannelOptions(_options.EventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        _eventReader = new ToolUpdateReader(_events.Reader);
        _readLoop = _logicalCompletion.Task;
    }

    public RpcConnection(Stream input, Stream output, RpcConnectionOptions? options = null)
        : this(input, output, options, null)
    {
    }

    internal RpcConnection(Stream input, Stream output, RpcConnectionOptions? options,
        Func<JsonElement, long, int, CancellationToken, ValueTask<bool>>? routeFrame)
        : this(options)
    {
        _output = output;
        _decoder = new RpcFrameDecoder();
        _routeFrame = routeFrame;
        _readLoop = Task.Run(() => ReadLoopAsync(input));
    }

    internal RpcConnection(RpcConnectionOptions? options, int protocolVersion,
        Func<string, string?, Action<Utf8JsonWriter>?, CancellationToken, Task> sendFrame,
        Func<ValueTask> closeSession)
        : this(options)
    {
        _protocolVersion = protocolVersion;
        _sendFrame = sendFrame;
        _closeSession = closeSession;
    }

    /// <summary>
    /// Non-response frames and unmatched responses. Consecutive unread text-only tool snapshots with
    /// unchanged arguments and no nonempty metadata collapse to the latest; all other events retain order.
    /// Coalescing never crosses a response or intercepted event. Completes when stdout ends.
    /// </summary>
    public ChannelReader<RpcFrame> Events => _eventReader;

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
    /// <summary>Physical lines for a root, logical frames for a session, and the corresponding transport byte count.</summary>
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
        if (_sendFrame is not null || _protocolVersion == 2) return _protocolVersion;
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
        var effectiveTimeout = timeout ?? _options.RequestTimeout(type);
        using var deadline = new CancellationTokenSource(effectiveTimeout, _options.Clock);
        using var cancellation = ct.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), deadline);
        var timings = new RpcTimings { Sent = Stopwatch.GetTimestamp() };
        var id = "g" + Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pending = new Pending(type, new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously), timings);
        if (_closeReason is not null) throw new RpcConnectionClosedException("RPC connection is closed", _closeReason);
        _pending[id] = pending;
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
        catch (OperationCanceledException) when (_closeReason is not null && !ct.IsCancellationRequested)
        {
            throw new RpcConnectionClosedException("RPC connection is closed", _closeReason);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{type} got no response within {effectiveTimeout.TotalSeconds:0.#}s");
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
        if (_closeReason is not null) throw new RpcConnectionClosedException("RPC connection is closed", _closeReason);
        using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        if (_sendFrame is not null)
        {
            await _sendFrame(type, id, body, writeCancellation.Token).ConfigureAwait(false);
            return;
        }
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
        await _writeLock.WaitAsync(writeCancellation.Token).ConfigureAwait(false);
        try
        {
            if (_closeReason is not null) throw new RpcConnectionClosedException("RPC connection is closed", _closeReason);
            // Not cancellable once started: a half-written line would corrupt the next command.
            await _output!.WriteAsync(buffer.WrittenMemory, CancellationToken.None).ConfigureAwait(false);
            await _output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            var reason = new RpcConnectionClosedException("omp stdin is closed", e);
            Close(reason);
            throw reason;
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
        return new RpcResponse(command, success, success ? null : error ?? "unknown error", data, timings)
        {
            Code = frame.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null,
        };
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
                    if (_routeFrame is not null)
                        throw new RpcConnectionClosedException($"Engine stdout line exceeded {MaxLineBytes} bytes");
                    // A line this long cannot come from omp; drop what we have and resync at the next newline.
                    await EmitAsync(Synthetic(RpcFrame.ProtocolError, $"stdout line exceeded {MaxLineBytes} bytes; discarded"), ct).ConfigureAwait(false);
                    buffer = buffer.Slice(buffer.End);
                    _decoder!.Reset();
                    _framePhysicalBytes = 0;
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
        if (_routeFrame is not null && line.Length > RpcFrameDecoder.MaxFrameBytes)
            throw new RpcConnectionClosedException($"Engine stdout line exceeded {RpcFrameDecoder.MaxFrameBytes} bytes");
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
            if (_routeFrame is not null) throw new RpcConnectionClosedException("Malformed engine stdout frame", e);
            await EmitAsync(Synthetic(RpcFrame.Malformed, e.Message, Preview(line)), ct).ConfigureAwait(false);
            return;
        }
        if (parsed.ValueKind != JsonValueKind.Object)
        {
            if (_routeFrame is not null) throw new RpcConnectionClosedException("Engine stdout frame is not a JSON object");
            await EmitAsync(Synthetic(RpcFrame.Malformed, "frame is not a JSON object", Preview(line)), ct).ConfigureAwait(false);
            return;
        }

        JsonElement? logical;
        try
        {
            if (_protocolVersion == 1 && parsed.TryGetProperty("type", out var pt) && pt.ValueEquals("rpc_chunk"))
                throw new RpcProtocolException("rpc_chunk received before protocol v2 was negotiated");
            logical = _decoder!.Push(parsed);
        }
        catch (RpcProtocolException e)
        {
            // Like the official client: a broken v2 sequence means a lost logical frame (maybe a response or
            // agent_end), so the stream cannot be trusted any more. Report it and end the connection.
            await EmitAsync(Synthetic(RpcFrame.ProtocolError, e.Message), ct).ConfigureAwait(false);
            throw new RpcConnectionClosedException("omp RPC framing error: " + e.Message, e);
        }
        _framePhysicalBytes += bytes + 1;
        if (logical is not { } frame) return;
        var physicalBytes = _framePhysicalBytes;
        _framePhysicalBytes = 0;
        if (_routeFrame is not null && await _routeFrame(frame, ts, physicalBytes, ct).ConfigureAwait(false)) return;
        await HandleFrameAsync(frame, ts, physicalBytes, ct).ConfigureAwait(false);
    }

    /// <summary>Routes a parsed inner frame without serializing, parsing or decoding it again.</summary>
    internal async ValueTask ReceiveAsync(JsonElement frame, long ts, int physicalBytes, CancellationToken ct)
    {
        if (_closeReason is not null) return;
        Interlocked.Increment(ref _lines);
        Interlocked.Add(ref _bytesInLines, physicalBytes);
        try
        {
            await HandleFrameAsync(frame, ts, physicalBytes, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException) when (_closeReason is not null)
        {
            // Closing one logical session must unblock the root reader, not close its peers.
        }
    }

    private async ValueTask HandleFrameAsync(JsonElement frame, long ts, int bytes, CancellationToken ct)
    {

        var type = frame.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        var id = frame.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;

        if (type == "ready" && !_ready.Task.IsCompleted)
        {
            _coalescingBoundary++;
            _ready.TrySetResult(ParseReady(frame));
            return;
        }
        if (type == "response" && id is not null && _pending.TryGetValue(id, out var pending))
        {
            _coalescingBoundary++;
            pending.Timings.ResponseRead = ts;
            pending.Timings.EventsBefore = _eventSequence;
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
        if (Intercept is { } hook && hook(type, frame))
        {
            _coalescingBoundary++;
            return;
        }
        await EmitAsync(new RpcFrame(type, id, frame, ts, bytes), ct).ConfigureAwait(false);
    }

    private static bool IsV2Ack(JsonElement f) =>
        f.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True &&
        f.TryGetProperty("command", out var c) && c.ValueEquals("negotiate_protocol") &&
        f.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
        d.TryGetProperty("protocolVersion", out var v) && v.TryGetInt32(out var n) && n == 2;

    private ValueTask EmitAsync(RpcFrame frame, CancellationToken ct)
    {
        frame.Sequence = ++_eventSequence;
        frame.CoalescingBoundary = _coalescingBoundary;
        return _events.Writer.WriteAsync(frame, ct);
    }

    // Coalesce only backlog already available to the sole consumer: no lookahead wait, timer, mutable
    // published frame or per-tool history. The bounded wire queue still provides normal backpressure.
    // Runtime partialResult.content is a replacement snapshot (including rolling/truncated output);
    // tool_stream_update is the separate delta contract. Unknown fields, media and nonempty details
    // stay lossless rather than assuming their metadata can be replaced like text.
    private sealed class ToolUpdateReader(ChannelReader<RpcFrame> reader) : ChannelReader<RpcFrame>
    {
        public override Task Completion => reader.Completion;

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            reader.WaitToReadAsync(cancellationToken);

        public override bool TryRead(out RpcFrame item)
        {
            if (!reader.TryRead(out item!)) return false;
            if (!IsTextSnapshot(item)) return true;
            // Bound one synchronous read even if the producer keeps refilling a long-running tool.
            var available = reader.Count;
            while (available-- > 0 && reader.TryPeek(out var next) && next.CoalescingBoundary == item.CoalescingBoundary &&
                IsTextSnapshot(next) && SameProperty(item.Json, next.Json, "toolCallId") &&
                SameProperty(item.Json, next.Json, "toolName") && SameProperty(item.Json, next.Json, "args"))
            {
                // SingleReader guarantees the peeked frame cannot be taken by another consumer.
                if (!reader.TryRead(out var latest)) break;
                item = latest;
            }
            return true;
        }

        private static bool SameProperty(JsonElement a, JsonElement b, string name)
        {
            var hasA = a.TryGetProperty(name, out var left);
            var hasB = b.TryGetProperty(name, out var right);
            return hasA == hasB && (!hasA || JsonElement.DeepEquals(left, right));
        }

        private static bool IsTextSnapshot(RpcFrame frame)
        {
            if (frame.Type != "tool_execution_update") return false;
            var json = frame.Json;
            if (!json.TryGetProperty("toolCallId", out var id) || id.ValueKind != JsonValueKind.String ||
                !json.TryGetProperty("toolName", out var name) || name.ValueKind != JsonValueKind.String ||
                !json.TryGetProperty("partialResult", out var result) || result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var property in json.EnumerateObject())
                if (!property.NameEquals("type") && !property.NameEquals("toolCallId") &&
                    !property.NameEquals("toolName") && !property.NameEquals("args") && !property.NameEquals("partialResult"))
                    return false;
            foreach (var property in result.EnumerateObject())
            {
                if (property.NameEquals("content")) continue;
                if (!property.NameEquals("details") || (property.Value.ValueKind != JsonValueKind.Null &&
                    (property.Value.ValueKind != JsonValueKind.Object || property.Value.EnumerateObject().MoveNext())))
                    return false;
            }
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object ||
                    !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !type.ValueEquals("text") ||
                    !block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    return false;
                foreach (var property in block.EnumerateObject())
                    if (!property.NameEquals("type") && !property.NameEquals("text")) return false;
            }
            return true;
        }
    }

    private static RpcReady ParseReady(JsonElement f)
    {
        int Int(string name, int fallback) => f.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : fallback;
        var versions = new List<int>();
        if (f.TryGetProperty("supportedProtocolVersions", out var s) && s.ValueKind == JsonValueKind.Array)
            foreach (var v in s.EnumerateArray())
                if (v.TryGetInt32(out var n)) versions.Add(n);
        if (versions.Count == 0) versions.Add(1);
        return new RpcReady(Int("protocolVersion", 1), versions, Int("maxFrameBytes", RpcFrameDecoder.MaxFrameBytes),
            Int("maxReassembledFrameBytes", RpcFrameDecoder.MaxReassembledBytes))
        {
            EngineProtocolVersion = Int("engineProtocolVersion", 0),
        };
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

    internal void Close(Exception reason)
    {
        if (Interlocked.CompareExchange(ref _closeReason, reason, null) is not null) return;
        _ready.TrySetException(reason);
        foreach (var p in _pending.Values) p.Tcs.TrySetException(reason);
        _events.Writer.TryComplete();
        _shutdown.Cancel();
        if (_sendFrame is not null) _logicalCompletion.TrySetResult();
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _readLoop.ConfigureAwait(false);
            return;
        }
        Close(new RpcConnectionClosedException("RPC connection disposed"));
        try
        {
            if (_closeSession is not null) await _closeSession().ConfigureAwait(false);
            await _readLoop.ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }
}
