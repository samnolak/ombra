using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ombra.Rpc;

namespace Ombra.FakeOmp;

/// <summary>One process transport; scenario state and command readers are private to each logical session.</summary>
internal sealed class FakeEngineHost
{
    private readonly StreamWriter _stdout = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    private readonly object _outputGate = new();
    private readonly ConcurrentDictionary<string, FakeEngineSession> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedKeys = new(StringComparer.Ordinal);
    private readonly List<Task> _operations = [];
    private static readonly object LogGate = new();
    private readonly Func<FakeEngineSession, string[], string, Task<int>> _runSession;
    private int _protocol = 1;
    private long _chunkSequence;
    internal string[] SessionKeys => _sessions.Keys.ToArray();

    private FakeEngineHost(Func<FakeEngineSession, string[], string, Task<int>> runSession) => _runSession = runSession;

    public static async Task<int> RunAsync(Func<FakeEngineSession, string[], string, Task<int>> runSession, int engineProtocolVersion)
    {
        var host = new FakeEngineHost(runSession);
        try { return await host.ReadAsync(engineProtocolVersion); }
        finally
        {
            foreach (var session in host._sessions.Values) session.Stop();
            await Task.WhenAll(host._operations);
            host._stdout.Dispose();
        }
    }

    private async Task<int> ReadAsync(int engineProtocolVersion)
    {
        Emit(new { type = "ready", engineProtocolVersion, protocolVersion = 1,
            supportedProtocolVersions = new[] { 1, 2 }, maxFrameBytes = RpcFrameDecoder.MaxFrameBytes,
            maxReassembledFrameBytes = RpcFrameDecoder.MaxReassembledBytes });
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var decoder = new RpcFrameDecoder();
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? command;
            try
            {
                using var document = JsonDocument.Parse(line);
                var decoded = decoder.Push(document.RootElement);
                if (decoded is null) continue;
                // JsonObject wraps the owned parsed element; routing never stringifies or reparses an inner frame.
                command = JsonObject.Create(decoded.Value.Clone());
            }
            catch (JsonException error) { Respond(null, "parse", error: error.Message); continue; }
            var type = command?["type"]?.GetValue<string>() ?? "";
            var id = command?["id"]?.GetValue<string>();
            var key = command?["sessionKey"]?.GetValue<string>();
            switch (type)
            {
                case "negotiate_protocol":
                    Respond(id, type, new { protocolVersion = 2 });
                    _protocol = 2;
                    break;
                case "engine_open":
                    if (string.IsNullOrWhiteSpace(key) || !_usedKeys.Add(key))
                    {
                        Respond(id, type, error: "sessionKey must be unique for this engine lifetime");
                        break;
                    }
                    if (command?["arguments"] is not JsonArray arguments || command["cwd"]?.GetValue<string>() is not { } cwd)
                    {
                        Respond(id, type, error: "engine_open requires cwd and arguments");
                        break;
                    }
                    var sessionArgs = arguments.Select(a => a!.GetValue<string>()).ToArray();
                    if (Environment.GetEnvironmentVariable("FAKE_OMP_SESSION_LOG") is { Length: > 0 } log)
                        File.AppendAllText(log, command.ToJsonString() + "\n");
                    if (sessionArgs.FirstOrDefault() == "missing-saved-model")
                    {
                        if (command["resumeOnCurrentModel"]?.GetValue<bool>() != true)
                        {
                            // Deliberately unrelated prose: consumers must use the typed error, not substring matching.
                            Emit(new { type = "engine_session_closed", sessionKey = key, exitCode = 1 });
                            Emit(new { type = "response", id, command = type, success = false,
                                error = "Saved selection is not in the registry", code = "session_model_unavailable",
                                data = new { provider = "removed-provider", modelId = "missing-model", selector = "removed-provider/missing-model" } });
                            break;
                        }
                        sessionArgs[0] = "deferred-input";
                    }
                    // Session ownership (session-storage.ts): "<file>.fakelease" holds the holder record another window
                    // or a terminal omp keeps for the file; opening it is refused with the typed error.
                    if (Array.IndexOf(sessionArgs, "--resume") is var resumeAt and >= 0 && resumeAt + 1 < sessionArgs.Length
                        && LeaseHolder(sessionArgs[resumeAt + 1]) is { } holder)
                    {
                        Emit(new { type = "engine_session_closed", sessionKey = key, exitCode = 1 });
                        Emit(new { type = "response", id, command = type, success = false,
                            error = "This session is open in another window", code = "session_lease_held",
                            data = new JsonObject { ["sessionId"] = "held", ["holder"] = holder } });
                        break;
                    }
                    var session = new FakeEngineSession(this, key);
                    _sessions[key] = session;
                    session.Start(_runSession, sessionArgs, cwd);
                    _operations.Add(session.Exited);
                    _operations.Add(CompleteOpenAsync(session, id));
                    break;
                case "engine_session_command":
                    if (key is not null && _sessions.TryGetValue(key, out var target) && command?["frame"] is JsonObject frame)
                        target.Post(frame);
                    // No root acknowledgement, and never route an unknown key to another session.
                    break;
                case "engine_close":
                    if (key is null || !_sessions.TryGetValue(key, out var closing))
                    {
                        Respond(id, type, error: "Unknown engine session");
                        break;
                    }
                    if (closing.IgnoreClose) break;
                    closing.Stop();
                    _operations.Add(CompleteCloseAsync(closing, id));
                    break;
                case "engine_read_session":
                    if (Environment.GetEnvironmentVariable("FAKE_OMP_READ_LOG") is { Length: > 0 } readLog)
                        File.AppendAllText(readLog, command!.ToJsonString() + "\n");
                    if (Environment.GetEnvironmentVariable("FAKE_OMP_HOLD_READS") == "1") break;
                    try
                    {
                        // Transport-only fixture. Canonical persisted-branch reconstruction is tested against the real host.
                        var path = command?["path"]?.GetValue<string>() ?? throw new IOException("Missing path");
                        var bytes = await File.ReadAllBytesAsync(path);
                        var snapshot = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                        var messages = System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Select(line => JsonNode.Parse(line)).Where(n => n?["type"]?.GetValue<string>() == "message")
                            .Select(n => n!["message"]!.DeepClone()).ToArray();
                        var cursor = command?["cursor"]?.GetValue<string>()?.Split('_');
                        if (cursor is not null && cursor[0] != snapshot)
                        {
                            Emit(new { type = "response", id, command = type, success = false, code = "stale_cursor", error = "History changed" });
                            break;
                        }
                        var end = cursor is null ? messages.Length : int.Parse(cursor[1]);
                        var start = Math.Max(0, end - (command?["limit"]?.GetValue<int>() ?? 100));
                        Respond(id, type, new { messages = messages[start..end], totalMessages = messages.Length,
                            direction = "backward", startOffset = start, endOffset = end, sessionId = "read-only",
                            snapshotId = snapshot, nextCursor = start > 0 ? $"{snapshot}_{start}" : null });
                    }
                    catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
                    {
                        Respond(id, type, error: error.Message);
                    }
                    break;
                case "engine_cancel_read":
                    if (Environment.GetEnvironmentVariable("FAKE_OMP_READ_LOG") is { Length: > 0 } cancelLog)
                        File.AppendAllText(cancelLog, command!.ToJsonString() + "\n");
                    Respond(id, type);
                    break;
                case "engine_take_session":
                {
                    // Asks the holder to let go and waits for it; never takes the lease by force. "<file>.fakelease-stuck"
                    // is a holder that does not answer: refused with the holder, as after the runtime's 15 s.
                    var path = command?["path"]?.GetValue<string>() ?? "";
                    if (Environment.GetEnvironmentVariable("FAKE_OMP_SESSION_LOG") is { Length: > 0 } takeLog)
                        AppendLog(takeLog, command!.ToJsonString() + "\n");
                    if (File.Exists(path + ".fakelease-stuck") && LeaseHolder(path) is { } stuck)
                    {
                        Emit(new { type = "response", id, command = type, success = false,
                            error = "The other window did not let go of the session", code = "session_lease_held",
                            data = new JsonObject { ["sessionId"] = "held", ["holder"] = stuck } });
                        break;
                    }
                    File.Delete(path + ".fakelease");
                    Respond(id, type);
                    break;
                }
                case "engine_delete_session":
                    try
                    {
                        // Transport fixture only; real-process tests exercise native ownership. A "<file>.fakelease"
                        // holder is refused as the runtime refuses another live writer's session.
                        var path = command?["path"]?.GetValue<string>()
                            ?? throw new IOException("Missing session path");
                        if (LeaseHolder(path) is not null)
                            throw new IOException($"Session is busy in another live writer; nothing was deleted: {path}");
                        File.Delete(path);
                        var artifacts = path[..^".jsonl".Length];
                        if (Directory.Exists(artifacts)) Directory.Delete(artifacts, recursive: true);
                        Respond(id, type);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        Respond(id, type, error: error.Message);
                    }
                    break;
                case "engine_shutdown":
                    foreach (var active in _sessions.Values) active.Stop();
                    await Task.WhenAll(_operations);
                    Respond(id, type);
                    return 0;
                case "engine_crash":
                    Console.Error.WriteLine("fatal: the fake shared engine crashed");
                    Environment.Exit(9);
                    return 9;
                default:
                    Respond(id, type, error: $"Unknown engine command: {type}");
                    break;
            }
        }
        return 0;
    }

    internal static JsonNode? LeaseHolder(string sessionFile) =>
        File.Exists(sessionFile + ".fakelease") ? JsonNode.Parse(File.ReadAllText(sessionFile + ".fakelease")) : null;

    private async Task CompleteOpenAsync(FakeEngineSession session, string? id)
    {
        if (await session.Ready) Respond(id, "engine_open", new { sessionKey = session.Key });
        else Respond(id, "engine_open", error: "Logical session exited before ready");
    }

    private async Task CompleteCloseAsync(FakeEngineSession session, string? id)
    {
        await session.Exited;
        Respond(id, "engine_close");
    }

    internal void Closed(FakeEngineSession session, int exitCode, string? error)
    {
        _sessions.TryRemove(session.Key, out _);
        Emit(new { type = "engine_session_closed", sessionKey = session.Key, exitCode, error });
    }

    private void Respond(string? id, string command, object? data = null, string? error = null) =>
        Emit(new { type = "response", id, command, success = error is null, data, error });

    internal static void AppendLog(string path, string text)
    {
        lock (LogGate) File.AppendAllText(path, text);
    }

    internal void Emit(object frame)
    {
        var json = JsonSerializer.Serialize(frame);
        lock (_outputGate)
        {
            if (_protocol == 2 && Encoding.UTF8.GetByteCount(json) + 1 > RpcFrameDecoder.MaxFrameBytes)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                var count = (bytes.Length + 262143) / 262144;
                var chunkId = "engine-" + ++_chunkSequence;
                for (var index = 0; index < count; index++)
                {
                    var slice = bytes.AsSpan(index * 262144, Math.Min(262144, bytes.Length - index * 262144));
                    _stdout.WriteLine(JsonSerializer.Serialize(new { type = "rpc_chunk", chunkId, index, count,
                        byteLength = bytes.Length, data = Convert.ToBase64String(slice) }));
                }
            }
            else _stdout.WriteLine(json);
        }
    }

    internal void WriteRaw(string line)
    {
        lock (_outputGate) _stdout.WriteLine(line);
    }
}

internal sealed class FakeEngineSession(FakeEngineHost host, string key)
{
    private readonly Channel<JsonObject> _commands = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions
        { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private bool _closed;

    public string Key => key;
    public string[] EngineSessionKeys => host.SessionKeys;
    public ChannelReader<JsonObject> Commands => _commands.Reader;
    public CancellationToken Stopping => _stopping.Token;
    public Task<bool> Ready => _ready.Task;
    public Task<int> Exited => _exited.Task;
    public bool IgnoreClose { get; private set; }
    public void MarkReady() => _ready.TrySetResult(true);
    public void Post(JsonObject frame) => _commands.Writer.TryWrite(frame);
    public void Stop() => _stopping.Cancel();

    public void Emit(object frame)
    {
        lock (_gate)
            if (!_closed) host.Emit(new { type = "engine_session_frame", sessionKey = key, frame });
    }

    public void WriteRaw(string line)
    {
        lock (_gate)
            if (!_closed) host.WriteRaw(line);
    }

    public void Start(Func<FakeEngineSession, string[], string, Task<int>> run, string[] arguments, string cwd)
    {
        IgnoreClose = arguments.FirstOrDefault() == "ignore-eof";
        _ = Task.Run(async () =>
        {
            var code = 0;
            string? error = null;
            try { code = await run(this, arguments, cwd); }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (Exception exception) { code = 1; error = exception.Message; }
            finally
            {
                lock (_gate)
                {
                    _closed = true;
                    _commands.Writer.TryComplete();
                    host.Closed(this, code, error);
                }
                _ready.TrySetResult(false);
                _exited.TrySetResult(code);
            }
        });
    }
}
