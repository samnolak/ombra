using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Ombra.Rpc;

/// <summary>Process-wide host configuration and the arguments for one logical session.</summary>
public sealed record OmpLaunchSpec
{
    public required string FileName { get; init; }
    /// <summary>Executable prefix only (for example Bun options and host.ts), passed verbatim to the host.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
    /// <summary>Session-only flags sent in engine_open, never added to the process command line.</summary>
    public IReadOnlyList<string> SessionArguments { get; init; } = [];
    /// <summary>Explicit user recovery only; ordinary resume always retains the saved model.</summary>
    public bool ResumeOnCurrentModel { get; init; }
    /// <summary>The logical session's cwd. The host's cwd is independent of all sessions.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>Process-scoped variables to set or remove. They must agree for every live session.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

/// <summary>
/// Words the client writes itself (not omp's), as a format and its values, so the app can show them in its own
/// language; <see cref="ToString"/> is the English the logs and <see cref="Exception.Message"/> keep.
/// </summary>
public sealed record ClientText(string Format, params object?[] Args)
{
    public override string ToString() => string.Format(System.Globalization.CultureInfo.InvariantCulture, Format, Args);
}

public sealed class OmpStartException(string message, int? exitCode, string stderrTail, bool launchFailed = false,
    string? code = null, JsonElement? data = null, bool engineStartFailed = false, ClientText? text = null) : Exception(text?.ToString() ?? message)
{
    /// <summary>The message when the client wrote it (null when it is omp's own error, passed on as it is).</summary>
    public ClientText? Text { get; } = text;
    public int? ExitCode { get; } = exitCode;
    public string StderrTail { get; } = stderrTail;
    public bool LaunchFailed { get; } = launchFailed;
    /// <summary>The shared engine process itself could not be launched or never became ready (not one logical session).</summary>
    public bool EngineStartFailed { get; } = engineStartFailed || launchFailed;
    public string? Code { get; } = code;
    public JsonElement? ErrorData { get; } = data;
    public string? UnavailableSessionModel => Code == "session_model_unavailable" &&
        ErrorData is { ValueKind: JsonValueKind.Object } data &&
        data.TryGetProperty("selector", out var selector) && selector.ValueKind == JsonValueKind.String
            ? selector.GetString() : null;
    /// <summary>For <c>session_lease_held</c>: who has the session open now; null for other failures or an unknown holder.</summary>
    public OmpSessionLeaseHolder? LeaseHolder => Code == "session_lease_held" ? OmpSessionLeaseHolder.FromErrorData(ErrorData) : null;
}

/// <summary>
/// The process that holds a session file's writer lease (session-storage.ts <c>holder.json</c>): another Ombra window
/// (<c>ombra</c>) or a terminal omp (<c>omp</c>).
/// </summary>
public sealed record OmpSessionLeaseHolder(string Kind, int? Pid, string? Label = null, DateTimeOffset? Since = null)
{
    /// <summary>Reads a holder object (<c>{kind, pid, label?, since}</c>); null when it is not one.</summary>
    public static OmpSessionLeaseHolder? Parse(JsonElement holder)
    {
        if (holder.ValueKind != JsonValueKind.Object) return null;
        var kind = holder.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        if (string.IsNullOrEmpty(kind)) return null;
        int? pid = holder.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) ? n : null;
        var label = holder.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        DateTimeOffset? since = holder.TryGetProperty("since", out var s) && s.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(s.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at
            : holder.TryGetProperty("since", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetInt64(out var epoch) ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : null;
        return new OmpSessionLeaseHolder(kind, pid, label, since);
    }

    /// <summary>The <c>holder</c> of a <c>session_lease_held</c> error's data, or of a <c>session_lease_lost</c> frame.</summary>
    public static OmpSessionLeaseHolder? FromErrorData(JsonElement? data) =>
        data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("holder", out var holder) ? Parse(holder) : null;
}

/// <summary>
/// App-owned engine process. Opens isolated RPC sessions on one bounded, v2 root transport.
/// A logical close never closes stdin or kills the process. Only this owner stops the host;
/// stopping explicitly permits a subsequent start, while disposal is final.
/// </summary>
public sealed class OmpEngine(RpcConnectionOptions? options = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Host? _host;
    private bool _disposed;

    /// <summary>The live app-owned process, including when it has no open logical sessions.</summary>
    public int? ProcessId => _host is { IsClosed: false } host ? host.ProcessId : null;

    public async Task<OmpSession> OpenSessionAsync(OmpLaunchSpec spec, TimeSpan readyTimeout, CancellationToken ct = default)
    {
        var cwd = Path.GetFullPath(spec.WorkingDirectory ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException($"Folder not found: {cwd}");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(readyTimeout);
        Host? host = null;
        OmpSession? session = null;
        try
        {
            await _lifecycle.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                host = await EnsureHostStartedLockedAsync(spec, readyTimeout, deadline.Token).ConfigureAwait(false);
                session = host.CreateSession();
            }
            finally
            {
                _lifecycle.Release();
            }

            var response = await host.Root.RequestAsync("engine_open", w =>
            {
                w.WriteString("sessionKey", session.SessionKey);
                w.WriteString("cwd", cwd);
                if (spec.ResumeOnCurrentModel) w.WriteBoolean("resumeOnCurrentModel", true);
                w.WriteStartArray("arguments");
                foreach (var argument in spec.SessionArguments) w.WriteStringValue(argument);
                w.WriteEndArray();
            }, timeout: readyTimeout, ct: deadline.Token).ConfigureAwait(false);
            if (!response.Success) throw new RpcCommandException("engine_open", response.Error ?? "unknown error", response.Code, response.Data);
            session.Ready = await session.Connection.WaitReadyAsync(deadline.Token).ConfigureAwait(false);
            return session;
        }
        catch (Exception e)
        {
            if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (e is not (OperationCanceledException or TimeoutException or RpcConnectionClosedException or RpcProtocolException or RpcCommandException))
                throw;
            int? code = session is { Exited.IsCompletedSuccessfully: true } ? session.Exited.Result
                : host is { Exited.IsCompletedSuccessfully: true } ? host.Exited.Result : null;
            var text = e is OperationCanceledException or TimeoutException
                ? new ClientText("omp did not become ready within {0:0.#}s", readyTimeout.TotalSeconds)
                : code is { } exit ? new ClientText("omp exited with code {0} before the session was ready: {1}", exit, e.Message) : null;
            throw new OmpStartException(e.Message, code, host?.StderrTail ?? "",
                code: (e as RpcCommandException)?.Code, data: (e as RpcCommandException)?.ErrorData, text: text);
        }
    }

    /// <summary>Deletes through the runtime's lifetime session lease, including CLI/TUI owners.</summary>
    public async Task DeleteSessionAsync(string path, OmpLaunchSpec spec, TimeSpan readyTimeout, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(readyTimeout);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var host = await EnsureHostStartedLockedAsync(spec, readyTimeout, deadline.Token).ConfigureAwait(false);
            var response = await host.Root.RequestAsync("engine_delete_session",
                w => w.WriteString("path", path), ct: deadline.Token).ConfigureAwait(false);
            if (!response.Success)
                throw new IOException(response.Error ?? "The runtime refused to delete the session.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The runtime did not finish session deletion before the deadline; refresh the catalog before retrying.");
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>How long <see cref="TakeOverSessionAsync"/> waits: the runtime's own 30 s wait for the holder (SESSION_TAKEOVER_WAIT_MS), plus margin.</summary>
    public static readonly TimeSpan TakeOverTimeout = TimeSpan.FromSeconds(35);

    /// <summary>
    /// Asks the holder of <paramref name="path"/>'s writer lease to let go (root <c>engine_take_session</c>) and waits until
    /// it has. Never takes the lease by force: a holder that does not answer fails with <c>session_lease_held</c>.
    /// </summary>
    /// <exception cref="RpcCommandException">The runtime refused; <c>session_lease_held</c> carries the holder in its data.</exception>
    public async Task TakeOverSessionAsync(string path, OmpLaunchSpec spec, TimeSpan readyTimeout, CancellationToken ct = default)
    {
        Host host;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(readyTimeout);
            await _lifecycle.WaitAsync(deadline.Token).ConfigureAwait(false);
            try { host = await EnsureHostStartedLockedAsync(spec, readyTimeout, deadline.Token).ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            finally { _lifecycle.Release(); }
        }
        var response = await host.Root.RequestAsync("engine_take_session", w => w.WriteString("path", path),
            timeout: TakeOverTimeout, ct: ct).ConfigureAwait(false);
        if (!response.Success)
            throw new RpcCommandException("engine_take_session", response.Error ?? "The runtime could not take the session over.", response.Code, response.Data);
    }

    /// <summary>Reads a persisted transcript through the root host without opening a logical session.</summary>
    public async Task<OmpMessagesPage> ReadSessionPageAsync(string path, OmpLaunchSpec spec, TimeSpan readyTimeout,
        string? cursor = null, int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        Host host;
        // Startup has its own bound. Once ready, a persisted history read uses the same
        // request policy as live history pages, not the shorter engine-ready deadline.
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(readyTimeout);
            await _lifecycle.WaitAsync(deadline.Token).ConfigureAwait(false);
            try { host = await EnsureHostStartedLockedAsync(spec, readyTimeout, deadline.Token).ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            finally { _lifecycle.Release(); }
        }
        var readId = Guid.NewGuid().ToString("N");
        try
        {
            var response = await host.Root.RequestAsync("engine_read_session", w =>
            {
                w.WriteString("readId", readId);
                w.WriteString("path", path);
                w.WriteNumber("limit", limit);
                if (cursor is not null) w.WriteString("cursor", cursor);
            }, ct: ct).ConfigureAwait(false);
            if (!response.Success)
                throw new RpcCommandException("engine_read_session", response.Error ?? "The runtime could not read this session.", response.Code);
            var page = OmpMessagesPage.Parse(response);
            if (page.Messages.GetArrayLength() > limit) throw new InvalidDataException("omp returned more messages than requested.");
            return page;
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            // Cleanup must not delay the original outcome behind a blocked root writer.
            // It is scoped to this read, never an abort/close of a live logical chat.
            _ = CancelReadBestEffortAsync(host.Root, readId);
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw;
        }
    }

    private static async Task CancelReadBestEffortAsync(RpcConnection root, string readId)
    {
        // Bound waiting for the writer independently of the already-cancelled caller.
        // A physical write already in progress cannot be interrupted without corrupting
        // framing; observe its eventual outcome here without making the caller await it.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await root.SendAsync("engine_cancel_read", null, w => w.WriteString("requestId", readId), deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or RpcConnectionClosedException or
            ObjectDisposedException or OperationCanceledException) { }
    }

    // Caller owns _lifecycle. Root-only operations and logical opens share exactly one startup path.
    private async Task<Host> EnsureHostStartedLockedAsync(OmpLaunchSpec spec, TimeSpan readyTimeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_host is { IsClosed: true } previous)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
            _host = null;
        }
        if (_host is { } running)
        {
            running.EnsureConfiguration(spec);
            return running;
        }
        var host = Host.Start(spec, options);
        _host = host;
        try
        {
            var ready = await host.Root.WaitReadyAsync(ct).ConfigureAwait(false);
            if (ready.EngineProtocolVersion != 1)
                throw new RpcProtocolException($"omp host requires engineProtocolVersion 1, received {ready.EngineProtocolVersion}");
            if (!ready.SupportsV2 || ready.MaxFrameBytes != RpcFrameDecoder.MaxFrameBytes ||
                ready.MaxReassembledFrameBytes != RpcFrameDecoder.MaxReassembledBytes)
                throw new RpcProtocolException("omp host does not advertise the required bounded RPC v2 limits");
            if (await host.Root.NegotiateAsync(ct).ConfigureAwait(false) != 2)
                throw new RpcProtocolException("omp host did not negotiate RPC v2");
            return host;
        }
        catch (Exception e)
        {
            _host = null;
            await host.DisposeAsync().ConfigureAwait(false);
            var code = host.Exited.IsCompletedSuccessfully ? (int?)host.Exited.Result : null;
            var text = e is OperationCanceledException or TimeoutException
                ? new ClientText("omp did not become ready within {0:0.#}s", readyTimeout.TotalSeconds)
                : code is { } exit ? new ClientText("omp exited with code {0} before the host was ready: {1}", exit, e.Message) : null;
            throw new OmpStartException(e.Message, code, host.StderrTail, engineStartFailed: true, text: text);
        }
    }

    /// <summary>Stops the app's root engine, releasing its process configuration for an explicit next start.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_host is not { } host) return;
            _host = null;
            await host.DisposeAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_host is not { } host) return;
            _host = null;
            await host.DisposeAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    internal sealed class Host : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly RpcConnectionOptions? _options;
        private readonly string _fileName;
        private readonly string[] _arguments;
        private readonly Dictionary<string, string?> _environment;
        private readonly ConcurrentDictionary<string, OmpSession> _sessions = new(StringComparer.Ordinal);
        private readonly Queue<string> _stderrTail = new();
        private readonly Task _stderrPump;
        private readonly Task _eventPump;
        private readonly Task _monitor;
        private int _stopping;
        public RpcConnection Root { get; }
        public int ProcessId { get; }
        public Task<int> Exited { get; }
        public bool IsClosed => Root.CloseReason is not null || Exited.IsCompleted;
        public string StderrTail { get { lock (_stderrTail) return string.Join('\n', _stderrTail); } }

        private Host(Process process, OmpLaunchSpec spec, RpcConnectionOptions? options)
        {
            _process = process;
            _options = options;
            _fileName = spec.FileName;
            _arguments = spec.Arguments.ToArray();
            _environment = new Dictionary<string, string?>(spec.Environment, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            ProcessId = process.Id;
            Root = new RpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, options, RouteAsync);
            Exited = WaitExitAsync(process);
            _stderrPump = PumpStderrAsync(process.StandardError);
            _eventPump = DrainRootEventsAsync();
            _monitor = MonitorAsync();
        }

        public static Host Start(OmpLaunchSpec spec, RpcConnectionOptions? options)
        {
            var psi = new ProcessStartInfo(spec.FileName)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = Directory.GetCurrentDirectory(),
            };
            foreach (var argument in spec.Arguments) psi.ArgumentList.Add(argument);
            foreach (var (key, value) in spec.Environment)
            {
                if (value is null) psi.Environment.Remove(key);
                else psi.Environment[key] = value;
            }
            try
            {
                var process = Process.Start(psi) ?? throw new OmpStartException("", null, "", launchFailed: true, text: new ClientText("could not start {0}", spec.FileName));
                return new Host(process, spec, options);
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new OmpStartException("", null, "", launchFailed: true, text: new ClientText("could not start {0}: {1}", spec.FileName, e.Message));
            }
        }

        public void EnsureConfiguration(OmpLaunchSpec spec)
        {
            if (_fileName != spec.FileName || !_arguments.SequenceEqual(spec.Arguments, StringComparer.Ordinal) ||
                _environment.Count != spec.Environment.Count ||
                spec.Environment.Any(pair => !_environment.TryGetValue(pair.Key, out var value) || value != pair.Value))
                throw new InvalidOperationException("The shared omp engine is already running with a different executable, host arguments or process environment. Stop the engine before changing its process configuration.");
        }

        public OmpSession CreateSession()
        {
            if (IsClosed) throw new RpcConnectionClosedException("The shared omp engine is closed", Root.CloseReason);
            var session = new OmpSession(this, Guid.NewGuid().ToString("N"), _options, Root.ProtocolVersion);
            _sessions[session.SessionKey] = session;
            if (IsClosed) Finish(session, Exited.IsCompletedSuccessfully ? Exited.Result : -1, Root.CloseReason?.Message);
            return session;
        }

        public Task SendAsync(string key, string type, string? id, Action<Utf8JsonWriter>? body, CancellationToken ct) =>
            Root.SendAsync("engine_session_command", null, w =>
            {
                w.WriteString("sessionKey", key);
                w.WriteStartObject("frame");
                if (id is not null) w.WriteString("id", id);
                w.WriteString("type", type);
                body?.Invoke(w);
                w.WriteEndObject();
            }, ct);

        private async ValueTask<bool> RouteAsync(JsonElement frame, long timestamp, int physicalBytes, CancellationToken ct)
        {
            if (!frame.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return false;
            var wrapped = type.ValueEquals("engine_session_frame");
            if (!wrapped && !type.ValueEquals("engine_session_closed")) return false;
            if (!frame.TryGetProperty("sessionKey", out var key) || key.ValueKind != JsonValueKind.String)
                throw new RpcProtocolException("Engine session frame has no sessionKey");
            if (wrapped)
            {
                if (!frame.TryGetProperty("frame", out var inner) || inner.ValueKind != JsonValueKind.Object)
                    throw new RpcProtocolException("Engine session envelope has no RPC object");
                if (inner.TryGetProperty("type", out var innerType) && innerType.ValueEquals("rpc_chunk"))
                    throw new RpcProtocolException("Engine session envelopes must contain decoded RPC frames");
                if (_sessions.TryGetValue(key.GetString()!, out var session))
                    await session.Connection.ReceiveAsync(inner, timestamp, physicalBytes, ct).ConfigureAwait(false);
            }
            else
            {
                if (!frame.TryGetProperty("exitCode", out var exit) || !exit.TryGetInt32(out var exitCode))
                    throw new RpcProtocolException("Engine session closure has no exitCode");
                var error = frame.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null;
                if (_sessions.TryGetValue(key.GetString()!, out var session)) Finish(session, exitCode, error);
            }
            return true;
        }

        public async Task<int> CloseSessionAsync(OmpSession session, TimeSpan grace)
        {
            session.Connection.Close(new RpcConnectionClosedException("Logical omp session closed"));
            if (session.Exited.IsCompleted) return await session.Exited.ConfigureAwait(false);
            using var deadline = new CancellationTokenSource(grace);
            try
            {
                var response = await Root.RequestAsync("engine_close", w => w.WriteString("sessionKey", session.SessionKey),
                    timeout: grace, ct: deadline.Token).ConfigureAwait(false);
                if (!response.Success) throw new RpcCommandException("engine_close", response.Error ?? "unknown error", response.Code);
                return await session.Exited.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or RpcConnectionClosedException or RpcCommandException)
            {
                // A stuck close affects this handle only. Never kill the host or replay a command to another session.
                Finish(session, -1, e.Message);
                return await session.Exited.ConfigureAwait(false);
            }
        }

        private void Finish(OmpSession session, int exitCode, string? error)
        {
            _sessions.TryRemove(session.SessionKey, out _);
            session.Finish(exitCode, error);
        }

        private async Task MonitorAsync()
        {
            await Task.WhenAny(Exited, Root.Completion).ConfigureAwait(false);
            var reason = Root.CloseReason ?? new RpcConnectionClosedException("The shared omp engine exited");
            Root.Close(reason);
            foreach (var pair in _sessions) pair.Value.Connection.Close(reason);
            if (!Exited.IsCompleted)
            {
                try { await Exited.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { Kill(); }
            }
            int code;
            try { code = await Exited.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException) { code = -1; }
            foreach (var pair in _sessions) Finish(pair.Value, code, reason.Message);
        }

        private async Task DrainRootEventsAsync()
        {
            await foreach (var frame in Root.Events.ReadAllAsync().ConfigureAwait(false))
            {
                if (frame.Type is RpcFrame.Malformed or RpcFrame.ProtocolError)
                {
                    var message = frame.Json.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String
                        ? detail.GetString()! : "Invalid shared engine root frame";
                    Root.Close(new RpcConnectionClosedException(message, new RpcProtocolException(message)));
                }
            }
        }

        private static async Task<int> WaitExitAsync(Process process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }

        private async Task PumpStderrAsync(StreamReader stderr)
        {
            try
            {
                while (await stderr.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lock (_stderrTail)
                    {
                        _stderrTail.Enqueue(line);
                        while (_stderrTail.Count > 200) _stderrTail.Dequeue();
                    }
                    var diagnostic = "[engine] " + line;
                    foreach (var pair in _sessions) pair.Value.OnStderrLine(diagnostic);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }

        private void Kill()
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
            var reason = new RpcConnectionClosedException("The shared omp engine is stopping");
            foreach (var pair in _sessions) pair.Value.Connection.Close(reason);
            try
            {
                if (!IsClosed)
                {
                    try
                    {
                        await Root.RequestAsync("engine_shutdown", timeout: TimeSpan.FromSeconds(3))
                            .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException or OperationCanceledException) { }
                }
                try { _process.StandardInput.Close(); }
                catch (Exception e) when (e is IOException or InvalidOperationException) { }
                try { await Exited.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    Kill();
                    await Exited.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
            }
            finally
            {
                await Root.DisposeAsync().ConfigureAwait(false);
                await _monitor.ConfigureAwait(false);
                await _eventPump.ConfigureAwait(false);
                try { await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
                _process.Dispose();
            }
        }
    }
}

/// <summary>A single logical session. Disposing it closes only its engine session, never the shared process.</summary>
public sealed class OmpSession : IAsyncDisposable
{
    private readonly OmpEngine.Host _host;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stopLock = new();
    private Task<int>? _stop;
    internal string SessionKey { get; }
    public RpcConnection Connection { get; }
    public RpcReady Ready { get; internal set; } = null!;
    public int ProcessId => _host.ProcessId;
    public Task<int> Exited => _exited.Task;
    public string StderrTail => _host.StderrTail;
    /// <summary>Shared process diagnostics prefixed [engine], not output attributed to this logical chat.</summary>
    public event Action<string>? StderrLine;

    internal OmpSession(OmpEngine.Host host, string key, RpcConnectionOptions? options, int protocolVersion)
    {
        _host = host;
        SessionKey = key;
        Connection = new RpcConnection(options, protocolVersion,
            (type, id, body, ct) => host.SendAsync(key, type, id, body, ct),
            async () => { await StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); });
    }

    internal void OnStderrLine(string line) => StderrLine?.Invoke(line);

    internal void Finish(int exitCode, string? error)
    {
        Connection.Close(new RpcConnectionClosedException(error ?? $"Logical omp session exited with code {exitCode}"));
        _exited.TrySetResult(exitCode);
    }

    public Task<int> StopAsync(TimeSpan grace)
    {
        lock (_stopLock) return _stop ??= _host.CloseSessionAsync(this, grace);
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
