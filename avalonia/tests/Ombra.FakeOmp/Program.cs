// Scripted shared engine speaking the root engine protocol and multiplexed omp RPC, for tests only.
// Session scenarios belong in engine_open.arguments; no child process is created for a chat.
// Scenarios: normal | nonterminal-end | bad-chunk | crash-before-ready | no-models | exit-on-request | garbage | out-of-order | duplicate | silent |
//            big-messages | ignore-eof | stderr-flood | slow-stream | tool-flood | approval | approval-timeout | ask | no-prompt-ack | queue-stream[-N] | abort-hangs | approval-chain | reload-fails-once | markdown | markdown-stream | todo
//            plan (each prompt moves a two-phase plan on; the third replaces it) | subagents (subagent frames, get_subagents, /jobs)
//            recorder | recorder-fails (audio recorder stand-ins, not omp) | cli <args> (omp's CLI subcommands, see FAKE_OMP_CLI)
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var scenario = args.Length > 0 ? args[0] : "";
if (scenario is "draft-owner" or "client-owner" or "admission-owner") return Ombra.FakeOmp.JournalOwnershipProcess.Run(args);
if (scenario.StartsWith("windows-owned-", StringComparison.Ordinal)) return Ombra.FakeOmp.OwnedProcessLifetimeProcess.Run(args);
// Not omp: a stand-in for a system audio recorder (parecord / arecord) writing raw 16 kHz mono float32 to stdout.
if (scenario == "recorder")
{
    using var raw = Console.OpenStandardOutput();
    var frame = new byte[1600 * 4]; // 100 ms
    for (var n = 0L; ; n += 1600)
    {
        for (var i = 0; i < 1600; i++) BitConverter.TryWriteBytes(frame.AsSpan(i * 4), (float)(0.5 * Math.Sin(2 * Math.PI * 440 * (n + i) / 16000.0)));
        try { raw.Write(frame); raw.Flush(); } catch (IOException) { return 0; }
        Thread.Sleep(100);
    }
}
if (scenario == "recorder-slow-fail")
{
    Thread.Sleep(800); // like parecord trying to start a sound server first
    Console.Error.WriteLine("Connection failure: Connection refused");
    return 1;
}
if (scenario == "recorder-fails")
{
    Console.Error.WriteLine("Connection failure: Connection refused");
    return 1;
}
// Not RPC: omp's CLI subcommands ("cli plugin list --json"). FAKE_OMP_CLI names a JSON file mapping the joined
// arguments to {"stdout": "...", "stderr": "...", "exit": 0}; each call is appended to FAKE_OMP_CLI_LOG.
if (scenario == "cli")
{
    var line = string.Join(' ', args.Skip(1));
    if (Environment.GetEnvironmentVariable("FAKE_OMP_CLI_LOG") is { Length: > 0 } cliLog) File.AppendAllText(cliLog, line + "\n");
    var table = Environment.GetEnvironmentVariable("FAKE_OMP_CLI") is { Length: > 0 } cliFile && File.Exists(cliFile)
        ? JsonNode.Parse(File.ReadAllText(cliFile)) as JsonObject : null;
    var hit = table?.Where(kv => line == kv.Key || line.StartsWith(kv.Key + " ", StringComparison.Ordinal)).OrderByDescending(kv => kv.Key.Length).FirstOrDefault().Value;
    // omp usage --json (cli/usage-cli.ts, omp 18.8.0) when the table has no answer for it: two subscriptions with
    // 5-hour and weekly windows, resets relative to now, like a signed-in Claude Max and ChatGPT Plus account
    if (hit is null && line == "usage --json") { Console.Out.Write(FakeUsageJson()); return 0; }
    if (hit is null) { Console.Error.WriteLine($"fake omp: no answer for \"{line}\""); return 1; }
    Console.Out.Write(hit["stdout"]?.GetValue<string>() ?? "");
    Console.Error.Write(hit["stderr"]?.GetValue<string>() ?? "");
    return hit["exit"]?.GetValue<int>() ?? 0;
}
// The shape of `omp usage --json` (raw provider payloads dropped), with omp's field names and units
static string FakeUsageJson()
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    const long hour = 3_600_000;
    object Limit(string provider, string id, string label, string windowId, string windowLabel, long durationMs, long resetsIn, double used, string status = "ok") => new
    {
        id = $"{provider}:{id}", label,
        scope = new { provider, windowId, shared = true },
        window = new { id = windowId, label = windowLabel, durationMs, resetsAt = now + resetsIn },
        amount = new { used, limit = 100, remaining = 100 - used, usedFraction = used / 100, remainingFraction = 1 - used / 100, unit = "percent" },
        status,
    };
    return JsonSerializer.Serialize(new
    {
        generatedAt = now,
        reports = new object[]
        {
            new
            {
                provider = "anthropic", fetchedAt = now - 20_000,
                limits = new[]
                {
                    Limit("anthropic", "5h", "Claude 5 Hour", "5h", "5 Hour", 5 * hour, 2 * hour + 10 * 60_000, 42),
                    Limit("anthropic", "7d", "Claude 7 Day", "7d", "7 Day", 168 * hour, 76 * hour, 86, "warning"),
                },
                metadata = new { email = "dev@example.com", orgName = "Example" },
                // Two saved resets, one the provider lets the account spend now (omp's UsageResetCredits)
                resetCredits = new
                {
                    availableCount = 2, redeemableCount = 1, nextCreditId = "grant_a",
                    credits = new object[]
                    {
                        new { id = "grant_a", remainingCount = 1, usable = true, status = "available", clears = new[] { "anthropic:5h" },
                            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(now + 72 * hour + 30 * 60_000).ToString("O") },
                        new { id = "grant_b", remainingCount = 1, usable = false, status = "available",
                            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(now + 240 * hour).ToString("O") },
                    },
                },
            },
            new
            {
                provider = "openai-codex", fetchedAt = now - 20_000,
                limits = new[]
                {
                    Limit("openai-codex", "primary", "5 hours", "5h", "5 hours", 5 * hour, 4 * hour + 5 * 60_000, 9),
                    Limit("openai-codex", "secondary", "7 days", "7d", "7 days", 168 * hour, 130 * hour, 16),
                },
                metadata = new { email = "dev@example.com", planType = "plus" },
            },
        },
        accountsWithoutUsage = Array.Empty<object>(),
        disabledCredentials = Array.Empty<object>(),
    });
}
// Physical process starts are separate from logical session launches, so tests can detect a spare engine.
if (Environment.GetEnvironmentVariable("FAKE_OMP_ENGINE_LOG") is { Length: > 0 } engineLog)
    File.AppendAllText(engineLog, JsonSerializer.Serialize(new { args, pid = Environment.ProcessId }) + "\n");
if (scenario == "root-crash-before-ready")
{
    Console.Error.WriteLine("fatal: no model configured (fake shared engine)");
    return 3;
}
// Crash-looping engine for automatic recovery: while FAKE_OMP_CRASH_ON_START names an existing file, every physical
// start (already logged above) exits before the host is ready.
if (Environment.GetEnvironmentVariable("FAKE_OMP_CRASH_ON_START") is { Length: > 0 } crashOnStart && File.Exists(crashOnStart))
{
    Console.Error.WriteLine("fatal: the fake shared engine crashed while starting");
    return 3;
}
// A start that never becomes ready (while FAKE_OMP_HANG_ON_START names an existing file): it only reads stdin and
// exits once its owner closes it, so a cancelled or abandoned start leaves no process behind.
if (Environment.GetEnvironmentVariable("FAKE_OMP_HANG_ON_START") is { Length: > 0 } hangOnStart && File.Exists(hangOnStart))
{
    using var input = Console.OpenStandardInput();
    input.CopyTo(Stream.Null);
    return 4;
}
return await Ombra.FakeOmp.FakeEngineHost.RunAsync(RunSessionAsync, scenario == "unsupported-engine-protocol" ? 2 : 1);

static async Task<int> RunSessionAsync(Ombra.FakeOmp.FakeEngineSession session, string[] args, string cwd)
{
var scenario = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "normal";
if (Environment.GetEnvironmentVariable("FAKE_OMP_LAUNCH_LOG") is { Length: > 0 } launchLog)
{
    var seen = new JsonObject();
    foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        if (e.Key is string k && (k.StartsWith("PI_", StringComparison.Ordinal) || k.StartsWith("CMUX_", StringComparison.Ordinal) || k.StartsWith("NODE_", StringComparison.Ordinal)))
            seen[k] = e.Value as string;
    Ombra.FakeOmp.FakeEngineHost.AppendLog(launchLog,
        JsonSerializer.Serialize(new { args, cwd, pid = Environment.ProcessId, env = seen }) + "\n");
}
// Builtin slash commands answered like omp answers them over RPC: FAKE_OMP_COMMANDS names a JSON file mapping a
// command prefix ("/mcp list") to the text it prints; each command sent is appended to FAKE_OMP_COMMAND_LOG.
var cannedCommands = Environment.GetEnvironmentVariable("FAKE_OMP_COMMANDS") is { Length: > 0 } cmdFile && File.Exists(cmdFile)
    ? JsonNode.Parse(File.ReadAllText(cmdFile)) as JsonObject : null;
var resumeAt = Array.IndexOf(args, "--resume");
var resumePath = resumeAt >= 0 && resumeAt + 1 < args.Length ? args[resumeAt + 1] : null;
var streaming = false;
CancellationTokenSource? run = null;
Task? activeRun = null;
var dialogs = new ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>>();
var dialogSeq = 0;
var stateCalls = 0;
var wakeHold = false;
var wakeSilent = false;
(string? Id, object Data)? heldWake = null;
var inputCalls = 0;
var abortCalls = 0;

// Like omp 18.2.0's tool approval (extensibility/extensions/wrapper.ts): a select that the run's abort signal does
// not reach, answered only by extension_ui_response or by its own timeout (then the default: undefined = denied).
// A structured Deny's reason (the owned engine's `reason`) is kept in lastDenyReason.
string? lastDenyReason = null;
async Task<string?> AskAsync(object request, string id, int? timeoutMs)
{
    var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
    dialogs[id] = tcs;
    using var stopped = session.Stopping.Register(() => tcs.TrySetResult(null));
    Emit(request);
    var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs ?? Timeout.Infinite));
    dialogs.TryRemove(id, out _);
    if (done != tcs.Task) return null;
    var r = await tcs.Task;
    lastDenyReason = r?["reason"]?.GetValue<string>();
    return r?["cancelled"]?.GetValue<bool>() == true ? null : r?["value"]?.GetValue<string>();
}

void Emit(object frame) => session.Emit(frame);

void Respond(string? id, string command, object? data = null, string? error = null, string? code = null)
{
    var o = new JsonObject { ["type"] = "response", ["command"] = command, ["success"] = error is null };
    if (id is not null) o["id"] = id;
    if (data is not null) o["data"] = JsonSerializer.SerializeToNode(data);
    if (error is not null) o["error"] = error;
    if (code is not null) o["code"] = code;
    Emit(o);
}
// MCP servers (FakeMcp.cs) and background jobs (FakeJobs.cs) over RPC, in every scenario, like the owned engine
using var mcp = new Ombra.FakeOmp.FakeMcp(Emit, Respond);
var jobs = new Ombra.FakeOmp.FakeJobs(Respond);
var planMode = new Ombra.FakeOmp.FakePlanMode(Emit, Respond, supported: scenario != "no-plan-mode");
var hostTools = new Ombra.FakeOmp.FakeHostTools(Emit, Respond); // FakeHostTools.cs: set_host_tools, host_tool_* frames
var btwGoal = new Ombra.FakeOmp.FakeBtwGoal(Emit, Respond, supported: scenario != "no-goal"); // FakeBtwGoal.cs: goal, btw*, goal_updated/btw_* frames

// Sessions like omp 18.2.0: <root>/<project>/<file>.jsonl with a title line and a session header (cwd).
var sessionsRoot = Environment.GetEnvironmentVariable("FAKE_SESSION_DIR") ?? Path.Combine(Path.GetTempPath(), $"ombra-fake-{Environment.ProcessId}");
// cwd belongs to this logical session; never mutate the process working directory.
var history = new List<object>();
string? sessionName = null;
var model = (provider: "fake", id: "model");
// A screenshot's model (FAKE_MODEL=provider/id), so pictures of the client don't read "fake/model"
if (Environment.GetEnvironmentVariable("FAKE_MODEL") is { } fm && fm.Split('/', 2) is [var fp, var fi]) model = (fp, fi);
string? thinking = "medium";
// Starts on the model without reasoning (omp reports no thinking level for it).
if (scenario == "vision-start") { model = ("fake", "vision"); thinking = null; }
var signedIn = false;
var fastMode = false;
CancellationTokenSource? shellAbort = null;
var autoCompaction = true;
var autoRetry = true;
var contextPercent = 1.25;
(string? Id, object Data)? heldRefresh = null;
JsonNode? usageLimit = null;
JsonObject? usageLimitByModel = null;
string? freshSessionId = null;
var holdUsageState = false;
var heldUsageStates = new List<(string? Id, object Data)>();
var changeUsageOnAccount = false;
JsonNode? usageAfterAccountChange = null;
string? remainingAccountSource = null;
// The option setters are logged with the slash commands (FAKE_OMP_COMMAND_LOG), so tests see what reached omp
void LogOption(string command, bool value)
{
    if (Environment.GetEnvironmentVariable("FAKE_OMP_COMMAND_LOG") is { Length: > 0 } log) Ombra.FakeOmp.FakeEngineHost.AppendLog(log, $"{command} {(value ? "true" : "false")}\n");
}
var failNextMessages = false;
object[] todoPhases = [];
// set_ask_dialog: the ask tool's questions come as one `ask` request ("ask-form" scenario)
var askDialogEnabled = false;
// "plan" and "subagents" scenarios (the side panes): which prompt this is, omp's subagent subscription and registry.
var planStep = 0;
var subagentLevel = "off";
var liveSubagents = new ConcurrentDictionary<string, JsonObject>();
var commandCatalog = new object[]
{
    new { name = "compact", description = "Compact the conversation context", source = "builtin" },
    new { name = "fresh", description = "Reset provider stream state without changing the local transcript", source = "builtin" },
    new { name = "stats", description = "Show session statistics", source = "builtin" },
    new { name = "stats-reset", description = "Reset the statistics", source = "builtin" }, // "/stats" is also a prefix of another command
    new { name = "rename", description = "Rename the session", input = new { hint = "<name>" }, source = "builtin" },
    new { name = "mcp", description = "Manage MCP servers", source = "builtin", subcommands = new[] { new { name = "list", description = "List servers" }, new { name = "enable", description = "Enable a server" } } },
    new { name = "skill:systematic-debugging", description = "Debug step by step", source = "skill" },
    new { name = "model", aliases = new[] { "m" }, description = "Switch model", source = "builtin" },
};
// Like omp, which lists every builtin it runs over RPC (and runs them locally, never as a turn): the ones the app's
// settings pages and panes read, and every command a test cans.
commandCatalog = [.. commandCatalog,
    new { name = "tools", description = "Show available tools", source = "builtin" },
    new { name = "ssh", description = "Manage SSH connections", source = "builtin" },
    new { name = "computer", description = "Toggle computer use", source = "builtin" },
    new { name = "advisor", description = "Toggle the advisor", source = "builtin" },
    new { name = "extended-context", description = "Toggle extended context", source = "builtin" },
    new { name = "context", description = "Show context usage", source = "builtin" },
    new { name = "usage", description = "Show provider usage", source = "builtin" }];
foreach (var key in cannedCommands?.Select(kv => kv.Key) ?? [])
    if (key.StartsWith('/') && key[1..].Split(' ')[0] is { Length: > 0 } cannedName
        && !commandCatalog.Any(c => JsonSerializer.SerializeToNode(c)?["name"]?.GetValue<string>() == cannedName))
        commandCatalog = [.. commandCatalog, new { name = cannedName, description = "Builtin", source = "builtin" }];
// What omp 18.8 prints for the reads the settings pages make when a test cans nothing (slash-commands/helpers/*.ts)
string? LocalBuiltinOutput(string text) => text switch
{
    "/mcp list" => "No MCP servers configured.",
    "/tools" => "* read\n* bash\n* edit\n* write\n* grep\n* eval",
    "/ssh list" => "No SSH hosts configured.",
    "/computer status" => "Computer use: disabled · prelude: active · configured: display=auto, maxWidth=1280, maxHeight=800",
    _ when text.StartsWith("/mcp ", StringComparison.Ordinal) || text.StartsWith("/ssh ", StringComparison.Ordinal)
        || text.StartsWith("/computer", StringComparison.Ordinal) || text is "/context" or "/usage" => "",
    _ => null,
};
string sessionFile = "";
// Like omp (session-manager.ts, its "lazy gate"): a new session's file is written only once the session has an assistant
// message (or a branch forces it), so a new chat has no file while its first reply streams, nor while it has only a draft.
var sessionOnDisk = false;

string NewSessionFile()
{
    var project = Path.Combine(sessionsRoot, "-" + string.Concat(cwd.Select(c => char.IsLetterOrDigit(c) ? c : '-')));
    Directory.CreateDirectory(project);
    sessionOnDisk = false;
    return Path.Combine(project, $"{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss-fffZ}_{Guid.NewGuid()}.jsonl");
}

// The whole session at once: the title line, the header (its id is the file name's) and the messages so far.
void WriteSessionFile()
{
    var id = Path.GetFileNameWithoutExtension(sessionFile).Split('_', 2)[1];
    File.WriteAllLines(sessionFile, [
        JsonSerializer.Serialize(new { type = "title", v = 1, title = sessionName ?? "" }),
        JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd }),
        .. history.Select(message => JsonSerializer.Serialize(new { type = "message", message }))]);
    sessionOnDisk = true;
}

// Loads a session file; false when it was recorded in another working directory (omp 18.2.0 declines those).
bool LoadSession(string file)
{
    var lines = File.ReadAllLines(file);
    var header = lines.Select(l => JsonNode.Parse(l)).First(n => n?["type"]?.GetValue<string>() == "session");
    var savedCwd = header!["cwd"]!.GetValue<string>();
    // Logical opens retain the caller's spelling; a saved header may use the same folder's resolved path.
    if (!Ombra.ClientCore.SessionCatalog.SamePath(savedCwd, cwd)) return false;
    cwd = savedCwd;
    history.Clear();
    sessionName = JsonNode.Parse(lines[0])?["title"]?.GetValue<string>() is { Length: > 0 } t ? t : null;
    foreach (var n in lines.Select(l => JsonNode.Parse(l)).Where(n => n?["type"]?.GetValue<string>() == "message"))
        history.Add(n!["message"]!.DeepClone());
    // Like omp: the todo list comes back from the last todo result saved in the session
    if (history.OfType<JsonNode>().LastOrDefault(m => m["role"]?.GetValue<string>() == "toolResult" && m["toolName"]?.GetValue<string>() == "todo")?["details"]?["phases"] is JsonArray saved)
        todoPhases = [.. saved.Select(p => (object)p!.DeepClone())];
    sessionFile = file;
    freshSessionId = null;
    sessionOnDisk = true;
    return true;
}

void Persist(object message)
{
    history.Add(message);
    if (sessionOnDisk) File.AppendAllText(sessionFile, JsonSerializer.Serialize(new { type = "message", message }) + "\n");
    else if (JsonSerializer.SerializeToNode(message)?["role"]?.GetValue<string>() == "assistant") WriteSessionFile();
}

// Like omp (session-manager.ts #setSessionFile): a session whose header is missing or malformed (no "session" entry with
// an id first, after the title line) is refused, at start (stderr, exit 1) and by switch_session (an error), untouched.
bool HeaderIsValid(string file)
{
    try
    {
        var first = File.ReadLines(file).Where(l => l.Trim().Length > 0).Select(l => JsonNode.Parse(l))
            .FirstOrDefault(n => n?["type"]?.GetValue<string>() != "title");
        return first?["type"]?.GetValue<string>() == "session" && first["id"] is JsonValue id && id.TryGetValue<string>(out _);
    }
    catch (JsonException) { return false; }
}
string Refusal(string file) => $"Cannot resume session \"{Path.GetFullPath(file)}\": the session header is missing or malformed. The file was not modified.";

var resumeRefused = resumePath is not null && File.Exists(resumePath) && !HeaderIsValid(resumePath);
if (!resumeRefused && resumePath is not null && File.Exists(resumePath) && LoadSession(resumePath)) { }
else sessionFile = NewSessionFile();

object Assistant(string text) => new { role = "assistant", content = new[] { new { type = "text", text } }, stopReason = "stop" };

// Controllable complete-line transcript reads for live-card consumers. Held replies retain the original page.
var subagentTranscripts = new Dictionary<string, JsonArray>();
var subagentPaths = new Dictionary<string, string>();
var subagentRequests = new List<object>();
var heldSubagentReads = new List<(string? Id, object Data, string? Error)>();
var holdSubagentReads = false;
var malformedSubagentRead = false;
string? subagentReadError = null;
// cancel_subagent / steer_subagent (rpc-mode.ts): what the client asked, and canned refusals for tests.
var subagentControls = new List<object>();
string? cancelSubagentError = null;
string? steerSubagentError = null;
void SubagentLifecycle(string agentId, int index, string status, string description)
{
    if (subagentLevel == "off") return;
    Emit(new JsonObject { ["type"] = "subagent_lifecycle", ["payload"] = new JsonObject
    {
        ["id"] = agentId, ["agent"] = "task", ["agentSource"] = "bundled", ["description"] = description, ["status"] = status,
        ["sessionFile"] = Path.Combine(Path.GetDirectoryName(sessionFile)!, agentId + ".jsonl"), ["parentToolCallId"] = "tk1", ["index"] = index, ["detached"] = true,
    } });
}
var transcriptJson = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
JsonArray Transcript(string agentId)
{
    if (subagentTranscripts.TryGetValue(agentId, out var transcript)) return transcript;
    transcript = JsonSerializer.SerializeToNode(new object[]
    {
        new { role = "user", content = "Find where omp's todo phases reach the client." },
        new { role = "assistant", content = new object[] { new { type = "text", text = "Searching for the readers." }, new { type = "toolCall", id = "c1", name = "grep", arguments = new { pattern = "todoPhases" } } } },
        new { role = "toolResult", toolCallId = "c1", content = new[] { new { type = "text", text = "src/Ombra.ClientCore/ConversationState.cs:106" } }, isError = false },
        new { role = "assistant", content = new[] { new { type = "text", text = "Found 3 readers." } } },
    })!.AsArray();
    subagentTranscripts[agentId] = transcript;
    return transcript;
}

object TranscriptPage(string agentId, long fromByte)
{
    var transcript = Transcript(agentId);
    var lines = transcript.Select(message => JsonSerializer.Serialize(new { type = "message", message }, transcriptJson) + "\n").ToArray();
    var size = lines.Sum(line => (long)Encoding.UTF8.GetByteCount(line));
    var reset = fromByte > size;
    if (reset) fromByte = 0;
    var messages = new JsonArray();
    long position = 0;
    for (var index = 0; index < lines.Length; index++)
    {
        if (position >= fromByte) messages.Add(transcript[index]?.DeepClone());
        position += Encoding.UTF8.GetByteCount(lines[index]);
    }
    return new
    {
        sessionFile = subagentPaths.GetValueOrDefault(agentId) ?? Path.Combine(Path.GetDirectoryName(sessionFile)!, agentId + ".jsonl"),
        fromByte, nextByte = malformedSubagentRead ? -1 : size, reset, entries = Array.Empty<object>(), messages,
    };
}
var messagePages = new Ombra.FakeOmp.MessagePages();
var historyBusyOnce = scenario is "history-busy" or "history-start-busy";
var historyStaleOnce = scenario == "history-stale";
var historyInvalidOnce = scenario is "history-malformed" or "history-bad-cursor";
var historyPageRequests = 0;
var historyStartDuringLoad = scenario == "history-live-start";
var historyPauseCatalog = false;
string? historyHeldCatalogId = null;
if (scenario.StartsWith("history", StringComparison.Ordinal) && resumePath is null)
{
    for (var index = 0; index < 350; index++)
        history.Add(index % 2 == 0 ? new { role = "user", content = "history-" + index } : Assistant("history-" + index));
    WriteSessionFile();
}

// steer / follow_up while streaming (omp keeps them after an explicit abort; they go with the next run).
var queue = new ConcurrentQueue<(string Text, bool Steering, JsonArray? Images)>();
DateTime? lastQueuedAt = null;
var queuedCount = 0;
// "held-admission": a prompt containing HOLD is not admitted (an input hook still working) until an abort or
// abort_and_prompt invalidates it; it is then answered cancelled, like omp's RpcUserInputGate.
string? heldPromptId = null;
var heldPromptText = "";

object UserMessage(string text, JsonArray? images)
{
    var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
    // omp echoes the image parts as sent (base64) in events and in get_messages
    foreach (var img in images ?? []) content.Add(new JsonObject { ["type"] = "image", ["mimeType"] = img?["mimeType"]?.GetValue<string>(), ["data"] = img?["data"]?.GetValue<string>() ?? "" });
    return new JsonObject { ["role"] = "user", ["content"] = content };
}

async Task RunAgentAsync(string message, CancellationToken ct, int deltas, int delayMs, JsonArray? images = null)
{
    streaming = true;
    Emit(new { type = "agent_start" });
    Emit(new { type = "message_start", message = UserMessage(message, images) });
    Emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
    var sb = new StringBuilder();
    var aborted = false;
    // "queue-stream[-N]": the run lasts until the test's N queued messages are in (default 1; a fixed length ended it
    // before a loaded machine had typed them), then a second without a new one; 30 s at most.
    var untilQueued = scenario.StartsWith("queue-stream", StringComparison.Ordinal);
    var queuedWanted = scenario.StartsWith("queue-stream-", StringComparison.Ordinal) ? int.Parse(scenario["queue-stream-".Length..], System.Globalization.CultureInfo.InvariantCulture) : 1;
    // "multi" (several chats at once): what the message asks for decides the turn — "CRASH" exits mid-reply
    var crash = scenario == "multi" && message.Contains("CRASH", StringComparison.Ordinal);
    for (var i = 0; untilQueued ? i < 1500 && (queuedCount < queuedWanted || lastQueuedAt is not { } q || DateTime.UtcNow - q < TimeSpan.FromSeconds(1)) : i < deltas; i++)
    {
        if (ct.IsCancellationRequested) { aborted = true; break; }
        if (crash && i == 3)
        {
            Console.Error.WriteLine("fatal: the fake omp crashed mid-reply");
            Environment.Exit(9);
        }
        var d = $"tok{i} ";
        sb.Append(d);
        Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = d } });
        if (delayMs > 0) { try { await Task.Delay(delayMs, ct); } catch (OperationCanceledException) { aborted = true; break; } }
    }
    if (!aborted && scenario == "tool-flood")
    {
        // Real pipe/backpressure fixture: sessions deliberately reuse the same tool identity and arguments.
        const string toolCallId = "shared-tool";
        Emit(new { type = "tool_execution_start", toolCallId, toolName = "bash", args = new { command = "tool-flood" } });
        var output = new StringBuilder(message + ": ");
        for (var index = 1; index <= 2048; index++)
        {
            if (ct.IsCancellationRequested) { aborted = true; break; }
            output.Append('x');
            Emit(new { type = "tool_execution_update", toolCallId, toolName = "bash",
                args = new { command = "tool-flood" },
                partialResult = new { content = new[] { new { type = "text", text = output.ToString() } }, details = new { } } });
            if (index == 1024)
            {
                Emit(new { type = "tool_stream_update", toolCallId, toolName = "bash", update = new { delta = message + ": delta" } });
                Emit(new { type = "notice", level = "info", message = message + ": midway" });
            }
        }
        Emit(new { type = "tool_execution_end", toolCallId, toolName = "bash", isError = aborted,
            result = new { content = new[] { new { type = "text", text = message + (aborted ? ": cancelled" : ": complete") } } } });
    }
    else if (!aborted && scenario == "session")
    {
        // A paced, realistic turn for the frame-by-frame review: thinking, streamed text, a read, a long command with
        // live output, a message with only a tool call, an edit with a diff, a failing command, todos, a Markdown answer.
        var pace = int.TryParse(Environment.GetEnvironmentVariable("FAKE_OMP_PACE_MS"), out var pm) ? pm : 40;
        async Task Wait(int units) { try { await Task.Delay(pace * units, ct); } catch (OperationCanceledException) { aborted = true; } }
        async Task Stream(string kind, string text)
        {
            foreach (var w in System.Text.RegularExpressions.Regex.Split(text, "(?<= )"))
            {
                if (aborted || ct.IsCancellationRequested) { aborted = true; return; }
                if (kind == "text_delta") sb.Append(w);
                Emit(new { type = "message_update", assistantMessageEvent = new { type = kind, contentIndex = 0, delta = w } });
                await Wait(1);
            }
        }
        async Task Assistant(string thinking, string text, object? toolCall)
        {
            if (thinking.Length > 0) await Stream("thinking_delta", thinking);
            await Stream("text_delta", text);
            var content = new List<object>();
            if (thinking.Length > 0) content.Add(new { type = "thinking", thinking });
            if (text.Length > 0) content.Add(new { type = "text", text });
            if (toolCall is not null) content.Add(toolCall);
            Emit(new { type = "message_end", message = new { role = "assistant", content, stopReason = toolCall is null ? "stop" : "toolUse" } });
            sb.Clear();
        }
        async Task Tool(string id, string name, object args, string[] progress, string output, bool fail = false, object? details = null)
        {
            Emit(new { type = "tool_execution_start", toolCallId = id, toolName = name, args });
            var so = new StringBuilder();
            foreach (var line in progress)
            {
                await Wait(4);
                if (aborted) return;
                so.AppendLine(line);
                Emit(new { type = "tool_execution_update", toolCallId = id, toolName = name, partialResult = new { content = new[] { new { type = "text", text = so.ToString() } } } });
            }
            await Wait(progress.Length == 0 ? 8 : 2);
            if (aborted) return;
            Emit(new { type = "tool_execution_end", toolCallId = id, toolName = name, isError = fail, result = new { content = new[] { new { type = "text", text = output } }, details } });
        }
        void NextMessage() => Emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });

        await Assistant("The user wants the greeting changed. I should look at the project layout, find where greet lives, run the tests before and after. ",
            "I'll look at the project first.", new { type = "toolCall", id = "s1", name = "read", arguments = new { path = "src/hello.py" } });
        if (!aborted) await Tool("s1", "read", new { path = "src/hello.py" }, [], "def greet():\n    print('Hi')\n");
        if (!aborted) { NextMessage(); await Assistant("", "Running the tests to see where we start.", new { type = "toolCall", id = "s2", name = "bash", arguments = new { command = "python -m pytest -q" } }); }
        if (!aborted) await Tool("s2", "bash", new { command = "python -m pytest -q" },
            ["collecting ...", "tests/test_hello.py::test_greet PASSED", "tests/test_hello.py::test_name PASSED", "tests/test_cli.py::test_help PASSED", "4 passed in 0.31s"],
            "tests/test_hello.py::test_greet PASSED\ntests/test_hello.py::test_name PASSED\ntests/test_cli.py::test_help PASSED\n4 passed in 0.31s");
        if (!aborted)
        {
            todoPhases = [new { id = "p1", name = "Todos", tasks = new object[]
            {
                new { id = "t1", content = "Read greet", status = "completed" },
                new { id = "t2", content = "Change the greeting", status = "in_progress" },
                new { id = "t3", content = "Run the tests", status = "pending" },
            } }];
            NextMessage();
            await Assistant("", "", new { type = "toolCall", id = "s3", name = "todo", arguments = new { op = "init", items = new[] { "Read greet", "Change the greeting", "Run the tests" } } });
        }
        if (!aborted) await Tool("s3", "todo", new { op = "init", items = new[] { "Read greet", "Change the greeting", "Run the tests" } }, [], "3 tasks");
        if (!aborted) { NextMessage(); await Assistant("", "", new { type = "toolCall", id = "s4", name = "edit", arguments = new { path = "src/hello.py" } }); }
        // omp 18.2.0's own diff format (code-frame lines "+  2|code"), as its edit tool reports it
        if (!aborted) await Tool("s4", "edit", new { path = "src/hello.py" }, [], "Updated src/hello.py", details: new { diff = "   1|def greet():\n-   2|    print('Hi')  # old greeting\n+   2|    print(\"Hello\")  # new greeting\n   3|\n   4|\n  12|def main():\n+  13|    greet()\n  14|    return 0\n" });
        if (!aborted) { NextMessage(); await Assistant("", "", new { type = "toolCall", id = "s4b", name = "write", arguments = new { path = "tests/test_greet.py" } }); }
        var testFile = string.Join("\n", new[] { "import pytest", "from src.hello import greet", "", "", "def test_greet_says_hello(capsys):", "    greet()", "    out = capsys.readouterr().out", "    assert out == \"Hello\\n\"", "", "", "class TestGreeting:", "    \"\"\"The greeting in a few cases.\"\"\"", "", "    def test_once(self, capsys):", "        greet()", "        assert capsys.readouterr().out.count(\"Hello\") == 1", "", "    def test_twice(self, capsys):", "        greet()", "        greet()", "        assert capsys.readouterr().out.count(\"Hello\") == 2", "", "    @pytest.mark.parametrize(\"n\", [0, 1, 3])", "    def test_many(self, capsys, n):", "        for _ in range(n):", "            greet()", "        assert capsys.readouterr().out.count(\"Hello\") == n" }) + "\n";
        if (!aborted) await Tool("s4b", "write", new { path = "tests/test_greet.py", content = testFile }, [], "Wrote tests/test_greet.py (27 lines)");
        if (!aborted) { NextMessage(); await Assistant("", "Checking the style too.", new { type = "toolCall", id = "s5", name = "bash", arguments = new { command = "ruff check src" } }); }
        if (!aborted) await Tool("s5", "bash", new { command = "ruff check src" }, ["src/hello.py:2:5: E999 not installed"], "bash: ruff: command not found\nexit code 127", fail: true);
        if (!aborted) { NextMessage(); await Assistant("", "", new { type = "toolCall", id = "s6", name = "bash", arguments = new { command = "python -m pytest -q" } }); }
        if (!aborted) await Tool("s6", "bash", new { command = "python -m pytest -q" }, ["collecting ...", "4 passed in 0.29s"], "4 passed in 0.29s");
        if (!aborted)
        {
            NextMessage();
            await Stream("text_delta", "Done. The greeting now says **Hello**.\n\n- Changed `greet` in `src/hello.py`\n- The tests pass (4 of 4)\n- `ruff` is not installed, so style was not checked\n\n```python\ndef greet():\n    print('Hello')\n```\n\nWant me to add a test for the new text?");
        }
    }
    else if (!aborted && scenario == "showcase")
    {
        // Every kind of step a real turn has, at once (no pacing), for design screenshots: a run of reads and searches, a
        // command, an edit with changes inside lines, files created (edit op "create", and write), deleted, renamed, one
        // call changing two files (perFileResults), a failing command. Shapes as omp 18.8 reports them (edit details:
        // op / move / sourcePath / perFileResults; code-frame diffs "+12|code").
        void Message(string text, string id, string name, object args)
        {
            Emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
            if (text.Length > 0) Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = text } });
            var content = new List<object>();
            if (text.Length > 0) content.Add(new { type = "text", text });
            content.Add(new { type = "toolCall", id, name, arguments = args });
            Emit(new { type = "message_end", message = new { role = "assistant", content, stopReason = "toolUse" } });
        }
        void Step(string text, string id, string name, object args, string output, object? details = null, bool fail = false)
        {
            Message(text, id, name, args);
            Emit(new { type = "tool_execution_start", toolCallId = id, toolName = name, args });
            Emit(new { type = "tool_execution_end", toolCallId = id, toolName = name, isError = fail, result = new { content = new[] { new { type = "text", text = output } }, details } });
        }
        Step("I'll look around first.", "x1", "read", new { path = "src/hello.py" }, "def greet():\n    print('Hi')\n");
        Step("", "x2", "read", new { path = "src/cli.py" }, "import sys\nfrom src.hello import greet\n\n\ndef main():\n    greet()\n");
        Step("", "x3", "grep", new { pattern = "greet\\(", path = "src" }, "src/cli.py:6:    greet()\nsrc/hello.py:1:def greet():", new { matchCount = 2, fileCount = 2 });
        Step("", "x4", "glob", new { pattern = "tests/**/*.py" }, "tests/test_hello.py\ntests/test_cli.py", new { fileCount = 2 });
        Step("Running the tests to see where we start.", "x5", "bash", new { command = "python -m pytest -q" },
            "tests/test_hello.py::test_greet PASSED\ntests/test_hello.py::test_name PASSED\ntests/test_cli.py::test_help PASSED\n4 passed in 0.31s");
        Step("", "x6", "edit", new { path = "src/hello.py" }, "Updated src/hello.py", new
        {
            op = "update", path = "src/hello.py", firstChangedLine = 2,
            diff = " 1|def greet(name: str = \"world\"):\n-2|    print('Hi')  # old greeting\n+2|    print(f\"Hello, {name}!\")  # new greeting\n 3|\n 4|\n\n 12|def main():\n-13|    greet()\n+13|    greet(sys.argv[1] if len(sys.argv) > 1 else \"world\")\n+14|    return 0\n 15|\n",
        });
        Step("Adding tests for the new greeting.", "x7", "edit", new { path = "tests/test_greet.py" }, "Created tests/test_greet.py", new
        {
            op = "create", path = "tests/test_greet.py", firstChangedLine = 1,
            diff = "+1|import pytest\n+2|from src.hello import greet\n+3|\n+4|\n+5|def test_default(capsys):\n+6|    greet()\n+7|    assert capsys.readouterr().out == \"Hello, world!\\n\"\n+8|\n+9|\n+10|@pytest.mark.parametrize(\"name\", [\"Ann\", \"Bob\"])\n+11|def test_named(capsys, name):\n+12|    greet(name)\n+13|    assert capsys.readouterr().out == f\"Hello, {name}!\\n\"\n",
        });
        Step("", "x8", "write", new { path = "docs/GREETING.md", content = "# Greeting\n\n`greet(name)` prints `Hello, <name>!`.\nWithout a name it greets the world.\n" }, "Successfully wrote 92 bytes to docs/GREETING.md");
        Step("The old module is unused now.", "x9", "edit", new { path = "src/legacy_greet.py" }, "Deleted src/legacy_greet.py", new
        {
            op = "delete", path = "src/legacy_greet.py",
            diff = "-1|# Kept for the 0.x CLI.\n-2|def legacy_greet():\n-3|    print('Hi')\n-4|\n-5|\n-6|def shout():\n-7|    print('HI')\n",
        });
        Step("", "x10", "edit", new { path = "src/util.py" }, "Moved src/util.py to src/helpers.py", new
        {
            op = "update", path = "src/helpers.py", move = "src/helpers.py", sourcePath = "src/util.py", firstChangedLine = 1,
            diff = "-1|\"\"\"Odds and ends.\"\"\"\n+1|\"\"\"Helpers for the greeting CLI.\"\"\"\n 2|\n 3|def clamp(n, lo, hi):\n",
        });
        Step("Updating the callers in one go.", "x11", "edit", new { input = "*** Begin Patch" }, "Updated 2 files", new
        {
            diff = " 1|import sys\n-2|from src.util import clamp\n+2|from src.helpers import clamp\n 3|from src.hello import greet\n",
            firstChangedLine = 2,
            perFileResults = new object[]
            {
                new { path = "src/cli.py", op = "update", firstChangedLine = 2, diff = " 1|import sys\n-2|from src.util import clamp\n+2|from src.helpers import clamp\n 3|from src.hello import greet\n" },
                new { path = "tests/test_cli.py", op = "update", firstChangedLine = 3, diff = " 2|\n-3|from src.util import clamp\n+3|from src.helpers import clamp\n 4|\n" },
            },
        });
        Step("Checking the style too.", "x12", "bash", new { command = "ruff check src" }, "bash: ruff: command not found\nexit code 127", fail: true);
        Step("", "x13", "bash", new { command = "python -m pytest -q" }, "6 passed in 0.33s");
        Emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
        const string done = "Done. `greet` takes a name now and says **Hello, <name>!**.\n\n- Tests: 6 of 6 pass\n- `ruff` is not installed, so style was not checked";
        sb.Append(done);
        Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = done } });
    }
    else if (!aborted && scenario == "markdown")
    {
        Emit(new { type = "message_update", assistantMessageEvent = new { type = "thinking_delta", contentIndex = 0, delta = "Considering the file layout first." } });
        const string md = "# Plan\n\nI will **change** the `greet` function and *explain* it.\n\n1. Edit `hello.py`\n2. Run tests\n\n- [x] done item\n- [ ] open item\n\n```python\ndef greet():\n    print('Hello')\n```\n\n| File | Change |\n|---|---|\n| hello.py | greeting |\n\n> Note: see [docs](https://example.com/docs) or [local](file:///etc/passwd).\n\n---\nDone.";
        sb.Clear();
        sb.Append(md);
        Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = md } });
        Emit(new { type = "tool_execution_start", toolCallId = "e1", toolName = "edit", args = new { path = "hello.py", edits = new[] { new { op = "update" } } } });
        Emit(new { type = "tool_execution_end", toolCallId = "e1", toolName = "edit", result = new { content = new[] { new { type = "text", text = "Updated hello.py" } }, details = new { diff = "--- a/hello.py\n+++ b/hello.py\n@@ -1,2 +1,2 @@\n def greet():\n-    print('Hi')\n+    print('Hello')\n" } } });
    }
    else if (!aborted && scenario == "explore")
    {
        // A run of reads and searches, paced (FAKE_OMP_PACE_MS, default 40), between short texts, for the exploration
        // group's live states (ChatReviewRecorder.Shoot_an_exploration): "Exploring…" while a step runs, then "Explored".
        var pace = int.TryParse(Environment.GetEnvironmentVariable("FAKE_OMP_PACE_MS"), out var pm) ? pm : 40;
        async Task Step(string text, string id, string name, object args, string output, object? details = null)
        {
            if (aborted) return;
            Emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
            if (text.Length > 0) Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = text } });
            var content = new List<object>();
            if (text.Length > 0) content.Add(new { type = "text", text });
            content.Add(new { type = "toolCall", id, name, arguments = args });
            Emit(new { type = "message_end", message = new { role = "assistant", content, stopReason = "toolUse" } });
            Emit(new { type = "tool_execution_start", toolCallId = id, toolName = name, args });
            try { await Task.Delay(pace * 8, ct); } catch (OperationCanceledException) { aborted = true; return; }
            Emit(new { type = "tool_execution_end", toolCallId = id, toolName = name, isError = false, result = new { content = new[] { new { type = "text", text = output } }, details } });
        }
        await Step("Let me find where the greeting lives.", "p1", "grep", new { pattern = "def greet", path = "src" }, "src/hello.py:1:def greet():", new { matchCount = 1, fileCount = 1 });
        await Step("", "p2", "read", new { path = "src/hello.py" }, "def greet():\n    print('Hi')\n");
        await Step("", "p3", "glob", new { pattern = "tests/**/*.py" }, "tests/test_hello.py\ntests/test_cli.py", new { fileCount = 2 });
        await Step("", "p4", "read", new { path = "tests/test_hello.py" }, "def test_greet(capsys):\n    greet()\n    assert capsys.readouterr().out == 'Hi\\n'\n");
        await Step("", "p5", "ls", new { path = "src" }, "hello.py\ncli.py\nutil.py");
        if (!aborted)
        {
            Emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
            const string found = "`greet` is in `src/hello.py` and one test checks its output.";
            sb.Append(found);
            Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = found } });
        }
    }
    else if (!aborted && scenario == "markdown-stream")
    {
        // A long Markdown answer (about 2000 words: headings, paragraphs, lists, code fences, tables) streamed a word at a
        // time, FAKE_OMP_PACE_MS apart (default 15), for the transcript's streaming and scrolling tests.
        var pace = int.TryParse(Environment.GetEnvironmentVariable("FAKE_OMP_PACE_MS"), out var pm) ? pm : 15;
        var doc = new StringBuilder();
        for (var s = 1; s <= 12; s++)
        {
            doc.Append($"## Step {s}: the part of the change that touches module {s}\n\n");
            doc.Append($"The module {s} reads its settings once at start-up and keeps them for the whole run, so a change to the file is only seen after a restart. This step moves the read behind a small cache that is refreshed when the file changes, and keeps the old behaviour behind a flag for the tests that rely on it.\n\n");
            doc.Append($"- Read `settings_{s}.toml` through the cache\n- Refresh it when the watcher fires\n- Keep `--legacy-settings` for the old path\n\n");
            doc.Append($"```python\ndef load_settings_{s}(path):\n    with open(path) as f:\n        return parse(f.read())\n\n\ndef settings_{s}():\n    return CACHE.get(\"{s}\", load_settings_{s})\n```\n\n");
            doc.Append($"| Case | Before | After |\n|---|---|---|\n| file unchanged | read once | read once |\n| file changed | stale until restart | fresh on the next call |\n| file removed | crash | the defaults, with a warning |\n\n");
        }
        doc.Append("That is all of it: twelve modules, one cache, and the tests still pass.");
        foreach (var w in System.Text.RegularExpressions.Regex.Split(doc.ToString(), "(?<= )"))
        {
            if (ct.IsCancellationRequested) { aborted = true; break; }
            sb.Append(w);
            Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = w } });
            try { await Task.Delay(pace, ct); } catch (OperationCanceledException) { aborted = true; break; }
        }
    }
    else if (!aborted && scenario is "todo" or "todo-state-glitch")
    {
        Emit(new { type = "tool_execution_start", toolCallId = "td", toolName = "todo", args = new { action = "set" } });
        todoPhases = [new { id = "p1", name = "Todos", tasks = new object[]
        {
            new { id = "t1", content = "Map the code", status = "completed" },
            new { id = "t2", content = "Change greet", status = "in_progress" },
            new { id = "t3", content = "Run the tests", status = "pending" },
        } }];
        Emit(new { type = "tool_execution_end", toolCallId = "td", toolName = "todo", result = new { content = new[] { new { type = "text", text = "3 tasks" } } } });
    }
    else if (!aborted && scenario == "plan")
    {
        // Like omp's todo tool: the plan changes with the call, get_state reports it after the call ends.
        planStep++;
        Emit(new { type = "tool_execution_start", toolCallId = $"pl{planStep}", toolName = "todo", args = new { op = planStep == 3 ? "init" : "done" } });
        object T(string content, string status, string? blocker = null) => blocker is null ? new { content, status } : new { content, status, blocker };
        todoPhases = planStep switch
        {
            1 => [
                new { name = "Understand", tasks = new[] { T("Read the session loader", "completed"), T("Map how todos reach the client", "completed") } },
                new { name = "Build", tasks = new[]
                {
                    T("Draw the plan pane with a checkbox per task, grouped by phase, with the current task emphasized and what the run does now under it", "in_progress"),
                    T("Keep earlier plans readable", "pending"),
                    T("Publish the preview build", "blocked", "a signing key from the release team"),
                    T("Port the old terminal renderer", "abandoned"),
                } },
            ],
            2 => [
                new { name = "Understand", tasks = new[] { T("Read the session loader", "completed"), T("Map how todos reach the client", "completed") } },
                new { name = "Build", tasks = new[]
                {
                    T("Draw the plan pane with a checkbox per task, grouped by phase, with the current task emphasized and what the run does now under it", "completed"),
                    T("Keep earlier plans readable", "in_progress"),
                    T("Publish the preview build", "blocked", "a signing key from the release team"),
                    T("Port the old terminal renderer", "abandoned"),
                    T("Write the user guide section", "pending"),
                } },
            ],
            _ => [new { name = "Todos", tasks = new[] { T("Run the full test suite", "in_progress"), T("Update the screenshots", "pending") } }],
        };
        // omp's todo result carries the committed list (details.phases) and is saved with the session: omp restores the
        // list from the last one, and so can a client that reads the history
        var todoOp = planStep == 3 ? "init" : "done";
        var todoResult = new { content = new[] { new { type = "text", text = "plan updated" } }, details = new { op = todoOp, phases = todoPhases, storage = "session" } };
        Emit(new { type = "tool_execution_end", toolCallId = $"pl{planStep}", toolName = "todo", result = todoResult });
        Persist(new { role = "toolResult", toolCallId = $"pl{planStep}", toolName = "todo", todoResult.content, todoResult.details, isError = false, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        sb.Append(planStep == 3 ? "New plan: the tests, then the screenshots." : "The plan is up to date.");
    }
    else if (!aborted && scenario == "subagents")
    {
        // Like omp's task tool with three subagents: frames only at a subscription level other than "off"
        // (rpc-subagents.ts); omp forgets an agent once it ends, and one keeps running in the background.
        Emit(new { type = "tool_execution_start", toolCallId = "tk1", toolName = "task", args = new { description = "Look into the panes" } });
        var sessionDir = Path.GetDirectoryName(sessionFile)!;
        JsonObject Progress(string id, int index, string agent, string status, string task, string? tool, string? args, string[] output, int tools, long tokens, long ms, string? intent = null) => new()
        {
            ["index"] = index, ["id"] = id, ["agent"] = agent, ["agentSource"] = "bundled", ["status"] = status, ["task"] = task,
            ["description"] = null, ["lastIntent"] = intent, ["currentTool"] = tool, ["currentToolArgs"] = args,
            ["recentTools"] = new JsonArray(new JsonObject { ["tool"] = "read", ["args"] = "src/Ombra.ClientCore/ConversationState.cs", ["endMs"] = 1 }, new JsonObject { ["tool"] = "grep", ["args"] = "todoPhases", ["endMs"] = 2 }),
            ["recentOutput"] = new JsonArray(output.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()),
            ["toolCount"] = tools, ["requests"] = 3, ["tokens"] = tokens, ["cost"] = 0.01, ["durationMs"] = ms,
            ["resolvedModel"] = "fake/model",
        };
        void Lifecycle(string id, int index, string agent, string status, string description, bool detached = false)
        {
            if (subagentLevel == "off") return;
            Emit(new JsonObject { ["type"] = "subagent_lifecycle", ["payload"] = new JsonObject
            {
                ["id"] = id, ["agent"] = agent, ["agentSource"] = "bundled", ["description"] = description, ["status"] = status,
                ["sessionFile"] = Path.Combine(sessionDir, id + ".jsonl"), ["parentToolCallId"] = "tk1", ["index"] = index, ["detached"] = detached,
            } });
        }
        void Report(JsonObject progress, bool detached = false)
        {
            var id = progress["id"]!.GetValue<string>();
            if (progress["status"]!.GetValue<string>() == "running") liveSubagents[id] = new JsonObject
            {
                ["id"] = id, ["index"] = progress["index"]!.GetValue<int>(), ["agent"] = progress["agent"]!.GetValue<string>(), ["agentSource"] = "bundled",
                ["status"] = "running", ["task"] = progress["task"]!.GetValue<string>(), ["lastUpdate"] = 1, ["progress"] = progress.DeepClone(),
                ["sessionFile"] = Path.Combine(sessionDir, id + ".jsonl"), ["parentToolCallId"] = "tk1",
            };
            if (subagentLevel == "off") return;
            Emit(new JsonObject { ["type"] = "subagent_progress", ["payload"] = new JsonObject
            {
                ["index"] = progress["index"]!.GetValue<int>(), ["agent"] = progress["agent"]!.GetValue<string>(), ["agentSource"] = "bundled",
                ["task"] = progress["task"]!.GetValue<string>(), ["parentToolCallId"] = "tk1", ["detached"] = detached, ["progress"] = progress.DeepClone(),
                ["sessionFile"] = Path.Combine(sessionDir, id + ".jsonl"),
            } });
        }
        const string t1 = "Find where omp's todo phases reach the client and list every place that reads them.";
        const string t2 = "Review the plan pane for states that are not shown: blocked, dropped, replaced plans.";
        const string t3 = "Keep the dev server running and report when a build fails.";
        Lifecycle("sa-1", 0, "explore", "started", "Find where todos are read");
        Lifecycle("sa-2", 1, "reviewer", "started", "Review the plan pane");
        Lifecycle("sa-3", 2, "task", "started", "Watch the dev server", detached: true);
        Report(Progress("sa-1", 0, "explore", "running", t1, "grep", "todoPhases src/", ["src/Ombra.ClientCore/ConversationState.cs:106", "src/Ombra.App/ViewModels/MainViewModel.Commands.cs:152"], 4, 8200, 4200));
        Report(Progress("sa-2", 1, "reviewer", "running", t2, "read", "src/Ombra.App/Views/Panes/PlanPane.axaml", ["Reading the pane"], 2, 5100, 3100));
        Report(Progress("sa-3", 2, "task", "running", t3, "bash", "npm run dev", ["VITE ready in 312 ms", "Local: http://localhost:5173/"], 1, 2300, 2500), detached: true);
        Report(Progress("sa-1", 0, "explore", "completed", t1, null, null, ["Found 3 readers: ConversationState, MainViewModel.Commands, the Plan pane."], 7, 12400, 9800, "Listed the readers"));
        liveSubagents.TryRemove("sa-1", out _);
        Lifecycle("sa-1", 0, "explore", "completed", "Find where todos are read");
        Report(Progress("sa-2", 1, "reviewer", "failed", t2, null, null, ["error: the model provider refused the request (rate limited)"], 3, 6000, 7400));
        liveSubagents.TryRemove("sa-2", out _);
        Lifecycle("sa-2", 1, "reviewer", "failed", "Review the plan pane");
        Emit(new { type = "tool_execution_end", toolCallId = "tk1", toolName = "task", result = new { content = new[] { new { type = "text", text = "2 of 3 subagents finished; 1 runs in the background" } } } });
        sb.Append("Two subagents finished; one keeps watching the dev server.");
    }
    else if (!aborted && scenario == "ask")
    {
        // omp's ask tool: a select with descriptions (suggestion marked "(Recommended)"), then a free-text input.
        Emit(new { type = "tool_execution_start", toolCallId = "t1", toolName = "ask", args = new { question = "Which color?" } });
        var color = await AskAsync(new { type = "extension_ui_request", id = "q1", method = "select", title = "Which color?",
            options = new[] { "Red (Recommended)", "Blue" }, optionDetails = new object[] { new { description = "warm" }, new { } } }, "q1", null);
        var name = await AskAsync(new { type = "extension_ui_request", id = "q2", method = "input", title = "Name it", placeholder = "a name" }, "q2", null);
        var answer = $"color={color ?? "none"} name={name ?? "none"}";
        sb.Append(answer);
        Emit(new { type = "tool_execution_end", toolCallId = "t1", toolName = "ask", result = new { content = new[] { new { type = "text", text = answer } } } });
    }
    else if (!aborted && scenario == "ask-form")
    {
        // The owned engine's ask tool after set_ask_dialog: every question as one `ask` request, answered with one
        // answer per question in order (rpc-mode.ts parseAskDialogResponse). Without the opt-in: a select per question.
        Emit(new { type = "tool_execution_start", toolCallId = "af", toolName = "ask", args = new { questions = new[] { "Which color?", "Which extras?" } } });
        string answer;
        if (askDialogEnabled)
        {
            var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialogs["af1"] = tcs;
            using (session.Stopping.Register(() => tcs.TrySetResult(null)))
            {
                Emit(new
                {
                    type = "extension_ui_request", id = "af1", method = "ask",
                    questions = new object[]
                    {
                        new { id = "color", question = "Which color?", header = "Color", options = new object[] { new { label = "Red", description = "warm" }, new { label = "Blue" } }, recommended = 0 },
                        new { id = "extras", question = "Which extras?", header = "Extras", options = new object[] { new { label = "Tests" }, new { label = "Docs", preview = "README.md" } }, multi = true },
                    },
                });
                var r = await tcs.Task;
                dialogs.TryRemove("af1", out _);
                if (Environment.GetEnvironmentVariable("FAKE_OMP_COMMAND_LOG") is { Length: > 0 } askLog && r is not null)
                    Ombra.FakeOmp.FakeEngineHost.AppendLog(askLog, "ask " + r.ToJsonString() + "\n");
                answer = r?["cancelled"]?.GetValue<bool>() == true || r?["answers"] is not JsonArray answers ? "cancelled"
                    : answers.Count != 2 || answers[0]?["id"]?.GetValue<string>() != "color" || answers[1]?["id"]?.GetValue<string>() != "extras" ? "error: answers out of order"
                    : string.Join(" ", answers.Select(a => $"{a!["id"]}=[{string.Join(",", (a["selectedOptions"] as JsonArray ?? []).Select(o => o!.GetValue<string>()))}]"
                        + (a["customInput"]?.GetValue<string>() is { } own ? $"+{own}" : "")));
            }
        }
        else
            answer = "color=" + (await AskAsync(new { type = "extension_ui_request", id = "afs", method = "select", title = "Which color?", options = new[] { "Red (Recommended)", "Blue" } }, "afs", null) ?? "none");
        sb.Append(answer);
        Emit(new { type = "tool_execution_end", toolCallId = "af", toolName = "ask", result = new { content = new[] { new { type = "text", text = answer } } } });
    }
    else if (!aborted && scenario == "approval-chain")
    {
        // Like omp's agent loop: a denied approval fails tool 1 and the loop goes on to tool 2 before the abort
        // signal is seen, asking again (18.2.0 approvals ignore the signal).
        for (var t = 1; t <= 2; t++)
        {
            var tid = $"t{t}";
            Emit(new { type = "tool_execution_start", toolCallId = tid, toolName = "bash", args = new { command = $"step {t}" } });
            var ok = await AskAsync(new { type = "extension_ui_request", id = $"ap{t}", method = "select", title = $"Allow tool: bash\n$ step {t}", options = new[] { "Approve", "Deny" } }, $"ap{t}", null);
            Emit(new { type = "tool_execution_end", toolCallId = tid, toolName = "bash", isError = ok != "Approve", result = new { content = new[] { new { type = "text", text = ok == "Approve" ? "done" : "Tool call denied by user: bash" } } } });
        }
    }
    else if (!aborted && scenario == "approval-rules")
    {
        // The owned engine's approvals: omp's text prompt (tools/approval.ts formatApprovalPrompt with bash's and write's
        // formatApprovalDetails) plus the typed `approval` request. One request per line of the prompt — "write <path>"
        // for the write tool, any other line a bash command — asked one after the other. FAKE_OMP_APPROVAL_LOG receives
        // "<answer> <line>" for each, and " reason=<reason>" after a structured Deny with a reason.
        var requests = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var r = 0; r < requests.Length; r++)
        {
            var line = requests[r];
            var write = line.StartsWith("write ", StringComparison.Ordinal);
            var tool = write ? "write" : "bash";
            var tid = $"r{r + 1}";
            Emit(new { type = "tool_execution_start", toolCallId = tid, toolName = tool, args = write ? (object)new { path = line[6..], content = "hello" } : new { command = line } });
            var did = $"ap{Interlocked.Increment(ref dialogSeq)}";
            var title = write ? $"Allow tool: write\nPath: {line[6..]}\nContent:\nhello" : $"Allow tool: bash\nCommand: {line}";
            string[] scopes = ["once", "session", "always"];
            var approval = write
                ? (object)new { tool, toolCallId = tid, risk = "write", details = new[] { $"Path: {line[6..]}", "Content:", "hello" }, scopes }
                : new { tool, toolCallId = tid, risk = "exec", details = new[] { $"Command: {line}" }, command = line, scopes };
            var choice = await AskAsync(new { type = "extension_ui_request", id = did, method = "select", title, options = new[] { "Approve", "Deny" }, approval }, did, null);
            var reason = choice == "Deny" ? lastDenyReason : null;
            if (Environment.GetEnvironmentVariable("FAKE_OMP_APPROVAL_LOG") is { Length: > 0 } approvalLog)
                Ombra.FakeOmp.FakeEngineHost.AppendLog(approvalLog, $"{choice ?? "(none)"} {line}{(reason is null ? "" : " reason=" + reason)}\n");
            var ok = choice == "Approve";
            var denied = "Tool call denied by user: " + tool + (reason is null ? "" : "\nThe user said (approval prompt): " + JsonSerializer.Serialize(reason));
            Emit(new { type = "tool_execution_end", toolCallId = tid, toolName = tool, isError = !ok, result = new { content = new[] { new { type = "text", text = ok ? "ran: " + line : denied } } } });
        }
    }
    else if (!aborted && (scenario is "approval" or "approval-timeout" || scenario == "multi" && message.Contains("APPROVE", StringComparison.Ordinal)))
    {
        Emit(new { type = "tool_execution_start", toolCallId = "t1", toolName = "bash", args = new { command = "echo hi" } });
        var did = $"ap{Interlocked.Increment(ref dialogSeq)}";
        // Long enough that the card (deadline minus the client's 1 s margin) is seen on slow runners (CI run 18, Windows).
        var timeout = scenario == "approval-timeout" ? 4000 : (int?)null;
        // The owned engine sends the typed request with omp's text prompt (approval-chain keeps an older omp's text only).
        var approval = new { tool = "bash", toolCallId = "t1", risk = "exec", details = new[] { "Command: echo hi" }, command = "echo hi", scopes = new[] { "once", "session", "always" } };
        var choice = await AskAsync(new { type = "extension_ui_request", id = did, method = "select", title = "Allow tool: bash\n$ echo hi", options = new[] { "Approve", "Deny" }, timeout, approval }, did, timeout);
        if (choice == "Approve")
            Emit(new { type = "tool_execution_end", toolCallId = "t1", toolName = "bash", result = new { content = new[] { new { type = "text", text = "hi" } } } });
        else
            Emit(new { type = "tool_execution_end", toolCallId = "t1", toolName = "bash", isError = true, result = new { content = new[] { new { type = "text", text = "Tool call denied by user: bash" } } } });
    }
    else if (!aborted && message.Contains("TERMREAD", StringComparison.Ordinal) && hostTools.Has("terminal_read"))
    {
        // The model calls the client's terminal_read (host-tools.ts) and repeats what it got
        var toolArgs = new JsonObject();
        Emit(new { type = "tool_execution_start", toolCallId = "t1", toolName = "terminal_read", args = toolArgs });
        if (await hostTools.CallAsync("terminal_read", "t1", toolArgs, ct) is { } got)
        {
            Emit(new { type = "tool_execution_end", toolCallId = "t1", toolName = "terminal_read", isError = got.IsError, result = new { content = new[] { new { type = "text", text = got.Text } } } });
            sb.Append(got.IsError ? "terminal_read failed: " : "terminal says: ").Append(got.Text);
        }
        else
        {
            aborted = true;
            Emit(new { type = "tool_execution_end", toolCallId = "t1", toolName = "terminal_read", isError = true, result = new { content = new[] { new { type = "text", text = "Host tool \"terminal_read\" was aborted" } } } });
        }
    }
    else if (!aborted)
    {
        Emit(new { type = "tool_execution_start", toolCallId = "t1", toolName = "read", args = new { path = "README.md" } });
        Emit(new { type = "tool_execution_end", toolCallId = "t1", toolName = "read", result = new { content = new[] { new { type = "text", text = "file body" } } } });
    }
    object[] finalContent = scenario == "markdown"
        ? [new { type = "thinking", thinking = "Considering the file layout first." }, new { type = "text", text = sb.ToString() }]
        : [new { type = "text", text = sb.ToString() }];
    // Like omp 18.2.0: an aborted message carries errorMessage "Interrupted by user" as well as stopReason "aborted".
    Emit(new { type = "message_end", message = new { role = "assistant", content = finalContent, stopReason = aborted ? "aborted" : "stop", errorMessage = aborted ? "Interrupted by user" : null } });
    if (scenario == "nonterminal-end")
    {
        // omp 18.2.0 after a paused compaction: a non-terminal agent_end and then silence.
        Emit(new { type = "agent_end", messages = Array.Empty<object>(), isTerminal = false });
        streaming = false;
        return;
    }
    Persist(UserMessage(message, images));
    Persist(new { role = "assistant", content = new[] { new { type = "text", text = sb.ToString() } }, stopReason = aborted ? "aborted" : "stop", errorMessage = aborted ? "Interrupted by user" : null });
    // Queued messages are delivered before the agent stops (unless the run was aborted: then they stay queued).
    while (!aborted && queue.TryDequeue(out var queued))
    {
        Emit(new { type = "message_start", message = UserMessage(queued.Text, queued.Images) });
        var reply = "followed: " + queued.Text;
        Emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
        Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = reply } });
        Emit(new { type = "message_end", message = Assistant(reply) });
        Persist(UserMessage(queued.Text, queued.Images));
        Persist(Assistant(reply));
    }
    // omp is idle when it reports the end: a prompt sent right after agent_end must not be "busy".
    streaming = false;
    Emit(new { type = "agent_end", messages = Array.Empty<object>() });
}

if (resumeRefused)
{
    // Bun prints the uncaught error under a source excerpt, its message on an "error: " line
    Console.Error.WriteLine("2282 | \t\t\tthrow new Error(\n" + "error: " + Refusal(resumePath!));
    return 1;
}
if (scenario == "crash-before-ready")
{
    Console.Error.WriteLine("fatal: no model configured (fake)");
    return 3;
}
if (scenario == "no-models")
{
    // omp 18.2.0's own text (stderr, then exit) when no provider is set up.
    Console.Error.WriteLine("No models available. Use /login or set an API key environment variable. Then use /model to select a model.");
    return 1;
}
if (scenario == "stderr-flood")
    _ = Task.Run(() => { for (var i = 0; i < 20000; i++) Console.Error.WriteLine($"noise line {i} " + new string('x', 100)); });

Emit(new { type = "ready", protocolVersion = 1, supportedProtocolVersions = new[] { 1, 2 }, maxFrameBytes = 1048576, maxReassembledFrameBytes = 67108864 });
session.MarkReady();
Emit(new { type = "available_commands_update", commands = commandCatalog });
if (scenario == "garbage")
{
    session.WriteRaw("this is not json");
    session.WriteRaw("[1,2,3]");
    session.WriteRaw("");
    Emit(new { type = "some_future_event", payload = 1 });
}

var held = new List<(string? id, string type)>();
try
{
await foreach (var cmd in session.Commands.ReadAllAsync(session.Stopping))
{
    var id = cmd?["id"]?.GetValue<string>();
    var type = cmd?["type"]?.GetValue<string>() ?? "";
    if (type is "prompt" or "steer" or "follow_up") inputCalls++;
    if (type is "abort" or "abort_and_restore_queue") abortCalls++;
    if (scenario == "deferred-input" && (type is "prompt" or "steer" or "follow_up")
        && Environment.GetEnvironmentVariable("FAKE_OMP_INPUT_LOG") is { Length: > 0 } inputLog)
        Ombra.FakeOmp.FakeEngineHost.AppendLog(inputLog, type + " " + (cmd?["message"]?.GetValue<string>() ?? "") + "\n");
    // Every input that reached the engine, in any scenario and across process restarts (nothing may be resent).
    if (type is "prompt" or "steer" or "follow_up" or "abort_and_prompt"
        && Environment.GetEnvironmentVariable("FAKE_OMP_PROMPT_LOG") is { Length: > 0 } promptLog)
        Ombra.FakeOmp.FakeEngineHost.AppendLog(promptLog, type + " " + (cmd?["message"]?.GetValue<string>() ?? "") + "\n");
    if (type == "extension_ui_response")
    {
        // A control frame: overtakes queued commands (rpc-mode.ts dispatchRpcControlFrame). Late answers are dropped.
        if (id is not null && dialogs.TryRemove(id, out var pending)) pending.SetResult(cmd);
        Ombra.FakeOmp.FakeMcp.OnUiResponse(cmd);
        continue;
    }

    if (scenario == "silent" && type != "negotiate_protocol") continue;
    if (scenario == "exit-on-request" && type == "get_state") Environment.Exit(5);
    if (scenario == "invalid-admission" && type is "prompt" or "steer" or "follow_up")
    {
        Respond(id, type, new { inputDisposition = "not-a-disposition", agentInvoked = false });
        continue;
    }
    if (scenario == "out-of-order" && type is "get_state" or "get_available_models")
    {
        held.Add((id, type));
        if (held.Count < 2) continue;
        for (var i = held.Count - 1; i >= 0; i--) Respond(held[i].id, held[i].type, new { order = i });
        held.Clear();
        continue;
    }

    // "no-jobs": an omp without the job commands (answers them "Unknown command" without an id, below)
    if (mcp.Handle(id, type, cmd) || (scenario != "no-jobs" && jobs.Handle(id, type, cmd, seeded: scenario is "subagents" or "plan" && !liveSubagents.IsEmpty))
        || planMode.Handle(id, type, cmd) || hostTools.Handle(id, type, cmd) || btwGoal.Handle(id, type, cmd)) continue;
    switch (type)
    {
        case "test_crash":
            // The whole shared engine dies at once (every logical session with it), like a runtime crash.
            Console.Error.WriteLine("fatal: the fake shared engine crashed (test_crash)");
            Environment.Exit(cmd?["exitCode"]?.GetValue<int>() ?? 9);
            break;
        case "test_lease_lost":
            // Like rpc-mode.ts when another window takes the session over: session_lease_lost {holder}, then the
            // logical session ends (its turn aborted, its file flushed, its lease released).
            Respond(id, type);
            Emit(new { type = "session_lease_lost", holder = new { id = "taker", kind = cmd?["kind"]?.GetValue<string>() ?? "ombra", pid = cmd?["pid"]?.GetValue<int>() ?? 4242, since = DateTimeOffset.UtcNow.ToString("O") },
                interrupted = cmd?["interrupted"]?.GetValue<bool>() ?? false });
            return 0;
        case "test_usage_control":
            if (cmd?["clear"]?.GetValue<bool>() == true) { usageLimit = null; usageLimitByModel = null; }
            if (cmd?.ContainsKey("usageLimit") == true) usageLimit = cmd["usageLimit"]?.DeepClone();
            if (cmd?.ContainsKey("usageLimitByModel") == true) usageLimitByModel = cmd["usageLimitByModel"]?.DeepClone() as JsonObject;
            if (cmd?["holdState"] is { } holdUsage) holdUsageState = holdUsage.GetValue<bool>();
            if (cmd?.ContainsKey("accountUsage") == true)
            {
                changeUsageOnAccount = true;
                usageAfterAccountChange = cmd["accountUsage"]?.DeepClone();
            }
            if (cmd?.ContainsKey("remainingSource") == true) remainingAccountSource = cmd["remainingSource"]?.GetValue<string>();
            if (cmd?["sessionId"] is { } replacementSessionId) freshSessionId = replacementSessionId.GetValue<string>();
            if (cmd?["releaseState"]?.GetValue<bool>() == true && heldUsageStates.Count > 0)
            {
                var pending = heldUsageStates[0];
                heldUsageStates.RemoveAt(0);
                Respond(pending.Id, "get_state", pending.Data);
            }
            Respond(id, type, new { held = heldUsageStates.Count, stateCalls });
            break;
        case "test_subagent_control":
        {
            var agentId = cmd?["subagentId"]?.GetValue<string>() ?? "sa-3";
            if (cmd?["replace"] is JsonArray replacement) subagentTranscripts[agentId] = (JsonArray)replacement.DeepClone();
            if (cmd?["append"] is JsonArray additions)
                foreach (var message in additions) Transcript(agentId).Add(message?.DeepClone());
            if (cmd?["sessionFile"] is { } transcriptPath) subagentPaths[agentId] = transcriptPath.GetValue<string>();
            if (cmd?["hold"] is { } hold) holdSubagentReads = hold.GetValue<bool>();
            if (cmd?["malformed"] is { } malformed) malformedSubagentRead = malformed.GetValue<bool>();
            if (cmd?.ContainsKey("error") == true) subagentReadError = cmd["error"]?.GetValue<string>();
            if (cmd?["release"]?.GetValue<bool>() == true)
            {
                foreach (var pending in heldSubagentReads) Respond(pending.Id, "get_subagent_messages", pending.Data, pending.Error);
                heldSubagentReads.Clear();
            }
            // "crash": omp dies with its subagents (no lifecycle end for them)
            if (cmd?["crash"]?.GetValue<bool>() == true)
            {
                Console.Error.WriteLine("fatal: the fake omp crashed with subagents running");
                Environment.Exit(9);
            }
            if (cmd?.ContainsKey("cancelError") == true) cancelSubagentError = cmd["cancelError"]?.GetValue<string>();
            if (cmd?.ContainsKey("steerError") == true) steerSubagentError = cmd["steerError"]?.GetValue<string>();
            // "start": more background subagents that keep running (as omp's registry lists them)
            if (cmd?["start"] is JsonArray starts)
                foreach (var started in starts)
                {
                    var startId = started!.GetValue<string>();
                    var startIndex = 10 + liveSubagents.Count;
                    liveSubagents[startId] = new JsonObject
                    {
                        ["id"] = startId, ["index"] = startIndex, ["agent"] = "task", ["agentSource"] = "bundled", ["status"] = "running",
                        ["task"] = "Background work " + startId, ["description"] = "Background work " + startId, ["lastUpdate"] = 1,
                        ["sessionFile"] = Path.Combine(Path.GetDirectoryName(sessionFile)!, startId + ".jsonl"), ["parentToolCallId"] = "tk1",
                    };
                    SubagentLifecycle(startId, startIndex, "started", "Background work " + startId);
                }
            Respond(id, type, new { requests = subagentRequests, held = heldSubagentReads.Count, controls = subagentControls });
            break;
        }
        case "test_wake_control":
            if (cmd?["streaming"] is { } wakeStreaming) streaming = wakeStreaming.GetValue<bool>();
            if (cmd?["hold"] is { } holdWake) wakeHold = holdWake.GetValue<bool>();
            if (cmd?["silent"] is { } silentWake) wakeSilent = silentWake.GetValue<bool>();
            if (cmd?["event"] is { } wakeEvent) Emit(new { type = wakeEvent.GetValue<string>() });
            if (cmd?["release"]?.GetValue<bool>() == true && heldWake is { } wakeReply)
            {
                heldWake = null;
                Respond(wakeReply.Id, "get_state", wakeReply.Data);
            }
            Respond(id, type, new { stateCalls, inputCalls, abortCalls, held = heldWake is not null });
            break;
        case "test_engine_session_keys":
            Respond(id, type, new { sessionKeys = session.EngineSessionKeys });
            break;
        case "test_state_refresh":
            contextPercent = cmd?["percent"]?.GetValue<double>() ?? 20;
            Emit(new { type = cmd?["event"]?.GetValue<string>() ?? "auto_compaction_end" });
            Respond(id, type);
            break;
        case "test_release_refresh":
            if (heldRefresh is { } refreshReply)
            {
                Respond(refreshReply.Id, "get_state", refreshReply.Data);
                heldRefresh = null;
            }
            Respond(id, type, new { stateCalls });
            break;
        case "negotiate_protocol":
            Respond(id, type, new { protocolVersion = 2 });
            break;
        case "set_event_filter":
            Respond(id, type, new { events = (string[]?)null, messageUpdates = "delta" });
            break;
        case "set_ask_dialog":
            askDialogEnabled = cmd?["enabled"]?.GetValue<bool>() == true;
            Respond(id, type, new { enabled = askDialogEnabled });
            break;
        case "set_todos":
            // Like the owned engine: replaces the list as sent, answers with it; no event follows.
            todoPhases = cmd?["phases"] is JsonArray sentPhases ? [.. sentPhases.Select(p => (object)p!.DeepClone())] : [];
            if (Environment.GetEnvironmentVariable("FAKE_OMP_COMMAND_LOG") is { Length: > 0 } todoLog)
                Ombra.FakeOmp.FakeEngineHost.AppendLog(todoLog, "set_todos " + cmd?["phases"]?.ToJsonString() + "\n");
            Respond(id, type, new { todoPhases });
            break;
        case "editor_trust":
            Respond(id, type);
            break;
        case "editor_document":
            if (cmd?["action"]?.GetValue<string>() == "request")
                Respond(id, type, error: "The fake engine does not run language servers");
            else
                Respond(id, type, new { });
            break;
        case "get_state":
            // "todo-state-glitch": the second get_state (the first refresh after start) succeeds without data.
            stateCalls++;
            if (scenario == "todo-state-glitch" && stateCalls == 2) { Respond(id, type); break; }
            var stateData = JsonSerializer.SerializeToNode(new { model = new { provider = model.provider, id = model.id, name = model.id }, thinkingLevel = thinking, isStreaming = streaming, sessionId = Path.GetFileNameWithoutExtension(sessionFile), sessionFile, sessionName, messageCount = history.Count, queuedMessageCount = queue.Count, todoPhases, contextUsage = new { tokens = 1600, contextWindow = 128000, percent = contextPercent }, fastModeEnabled = fastMode, fastModeActive = fastMode, autoCompactionEnabled = autoCompaction, isCompacting = false })!.AsObject();
            var reportedUsageLimit = usageLimitByModel?.ContainsKey(model.provider + "/" + model.id) == true
                ? usageLimitByModel[model.provider + "/" + model.id] : usageLimitByModel?.ContainsKey(model.id) == true
                ? usageLimitByModel[model.id] : usageLimit;
            if (Environment.GetEnvironmentVariable("FAKE_OMP_USAGE_LIMIT") is { Length: > 0 } usageFile)
                reportedUsageLimit = File.Exists(usageFile) ? JsonNode.Parse(File.ReadAllText(usageFile)) : null;
            if (reportedUsageLimit is not null) stateData["usageLimit"] = reportedUsageLimit.DeepClone();
            if (freshSessionId is not null) stateData["sessionId"] = freshSessionId;
            if (planMode.State() is { } planState) stateData["planMode"] = planState;
            if (btwGoal.TryGoalState(out var goalState)) stateData["goal"] = goalState;
            // Ombra's engine reports what --untrusted-project left out of the folder (null without the flag)
            if (Array.IndexOf(args, "--untrusted-project") >= 0)
                stateData["untrustedProject"] = new JsonObject { ["items"] = Environment.GetEnvironmentVariable("FAKE_UNTRUSTED_ITEMS") is { Length: > 0 } itemsFile && File.Exists(itemsFile) ? JsonNode.Parse(File.ReadAllText(itemsFile)) : new JsonArray() };
            if (holdUsageState) { heldUsageStates.Add((id, stateData)); break; }
            if (wakeSilent) break;
            if (wakeHold) { heldWake = (id, stateData); break; }
            if (scenario == "compaction-refresh-race" && stateCalls == 2)
            {
                contextPercent = 20;
                model = ("new", "model");
                thinking = "high";
                sessionName = "compacted";
                Emit(new { type = "config_update", model = new { provider = model.provider, id = model.id }, thinkingLevel = thinking });
                Emit(new { type = "session_info_update", title = sessionName });
                for (var refreshIndex = 0; refreshIndex < 64; refreshIndex++) Emit(new { type = "auto_compaction_end", skipped = true });
            }
            if (scenario == "compaction-refresh-race" && stateCalls == 3)
            {
                heldRefresh = (id, stateData);
                Emit(new { type = "notice", level = "info", message = "refresh held" });
                break;
            }
            Respond(id, type, stateData);
            if (historyStartDuringLoad)
            {
                historyStartDuringLoad = false;
                historyPauseCatalog = true;
                streaming = true;
                model = ("new", "model");
                thinking = "high";
                sessionName = "new title";
                Emit(new { type = "agent_start" });
                var user = UserMessage("new streaming user", null);
                Persist(user);
                Emit(new { type = "message_start", message = user });
                Emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
                Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", delta = "streaming response" } });
                Emit(new { type = "config_update", model = new { provider = "new", id = "model" }, thinkingLevel = thinking });
                Emit(new { type = "session_info_update", title = sessionName });
            }
            if (scenario == "duplicate") Respond(id, type, new { duplicate = true });
            break;
        case "get_messages_page":
        {
            historyPageRequests++;
            var cursor = cmd?["cursor"]?.GetValue<string>();
            var limit = cmd?["limit"]?.GetValue<int>() ?? 100;
            if (Environment.GetEnvironmentVariable("FAKE_HISTORY_LOG") is { Length: > 0 } historyLog)
                Ombra.FakeOmp.FakeEngineHost.AppendLog(historyLog, JsonSerializer.Serialize(new { type, cursor, limit }) + "\n");
            if (scenario == "history-incompatible") { Respond(id, type, new { messages = history.Take(limit), totalMessages = history.Count }); break; }
            if (streaming || (historyBusyOnce && (cursor is not null || scenario == "history-start-busy")))
            {
                historyBusyOnce = false;
                Respond(id, type, error: "Cannot page messages while the session is changing", code: "session_busy");
                break;
            }
            if (cursor is not null && historyStaleOnce)
            {
                historyStaleOnce = false;
                Respond(id, type, error: "RPC message cursor is stale", code: "stale_cursor");
                break;
            }
            if (failNextMessages) { failNextMessages = false; Respond(id, type, error: "history temporarily unavailable"); break; }
            try
            {
                IReadOnlyList<object> source = scenario == "big-messages"
                    ? new object[] { new { role = "user", content = "hi" }, Assistant(new string('a', 3 * 1024 * 1024 + 17) + "é漢") }
                    : history;
                var page = messagePages.Page(source, freshSessionId ?? Path.GetFileNameWithoutExtension(sessionFile), cursor, limit);
                if (historyInvalidOnce && historyPageRequests > 1)
                {
                    historyInvalidOnce = false;
                    var invalidPage = JsonSerializer.SerializeToNode(page)!.AsObject();
                    if (scenario == "history-bad-cursor") invalidPage["nextCursor"] = new string('a', 2049);
                    else invalidPage["messages"]!.AsArray()[^1] = JsonSerializer.SerializeToNode(new
                    {
                        role = "assistant", content = new[] { new { type = "toolCall", id = "malformed", name = "write",
                            arguments = new { path = cwd + Path.DirectorySeparatorChar + "bad\0path", content = "invalid" } } },
                    });
                    page = invalidPage;
                }
                if (scenario == "history-replay" && cursor is null && source.Count >= 2)
                {
                    // These already-persisted messages precede the snapshot response on stdout but may still be
                    // waiting in the consumer's event queue when its request continuation runs.
                    Emit(new { type = "message_start", message = source[^2] });
                    Emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
                    Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta",
                        delta = JsonSerializer.SerializeToNode(source[^1])?["content"]?[0]?["text"]?.GetValue<string>() ?? "" } });
                    Emit(new { type = "message_end", message = source[^1] });
                }
                if (int.TryParse(Environment.GetEnvironmentVariable("FAKE_HISTORY_PAGE_DELAY_MS"), out var pageDelay) && pageDelay > 0)
                {
                    var responseId = id;
                    _ = Task.Run(async () => { await Task.Delay(pageDelay); Respond(responseId, "get_messages_page", page); });
                }
                else Respond(id, type, page);
            }
            catch (InvalidOperationException error) when (error.Message == "stale_cursor")
            {
                Respond(id, type, error: "RPC message cursor is stale", code: "stale_cursor");
            }
            break;
        }
        case "get_messages":
            if (scenario == "big-messages")
            {
                var big = new string('a', 3 * 1024 * 1024 + 17) + "é漢";
                Respond(id, type, new { messages = new object[] { new { role = "user", content = "hi" }, Assistant(big) } });
            }
            else if (failNextMessages)
            {
                failNextMessages = false;
                Respond(id, type, error: "history temporarily unavailable");
            }
            else Respond(id, type, new { messages = history });
            break;
        case "steer" or "follow_up":
            if (scenario == "deferred-input" && cmd?["message"]?.GetValue<string>() is { } hookInput)
            {
                // Input hooks are allowed to consume or empty a submission before any queue/agent events.
                if (hookInput is "input-hook:handled" or "input-hook:empty")
                {
                    Respond(id, type, new { inputDisposition = "local", agentInvoked = false });
                    break;
                }
                if (hookInput == "input-hook:cancelled")
                {
                    Respond(id, type, new { inputDisposition = "cancelled", agentInvoked = false });
                    break;
                }
                if (hookInput == "input-hook:agent")
                {
                    // The hook consumed the original input, but admitted its own turn before the ACK. Agent
                    // frames can still arrive later; the client must not let the next FIFO head overtake them.
                    streaming = true;
                    run = new CancellationTokenSource();
                    var extensionToken = run.Token;
                    Respond(id, type, new { inputDisposition = "local", agentInvoked = true });
                    activeRun = Task.Run(async () =>
                    {
                        try { await RunAgentAsync("extension-created turn", extensionToken, 100, 20); }
                        catch (Exception e) { Console.Error.WriteLine("fake extension turn failed: " + e); }
                    });
                    break;
                }
            }
            queue.Enqueue((cmd?["message"]?.GetValue<string>() ?? "", type == "steer", cmd?["images"]?.DeepClone() as JsonArray));
            lastQueuedAt = DateTime.UtcNow;
            Interlocked.Increment(ref queuedCount);
            Respond(id, type, new { inputDisposition = "admitted", agentInvoked = true });
            // The runtime schedules idle drain for both follow_up and steer, including when the UI still
            // shows a just-finished command as Running. Only recovery scenarios deliberately hold the queue.
            if (!scenario.StartsWith("queue-recovery", StringComparison.Ordinal) && !streaming && queue.TryDequeue(out var idleInput))
            {
                streaming = true;
                run = new CancellationTokenSource();
                var idleToken = run.Token;
                activeRun = Task.Run(async () =>
                {
                    try { await RunAgentAsync(idleInput.Text, idleToken, 50, 0, idleInput.Images); }
                    catch (Exception e) { Console.Error.WriteLine("fake idle drain failed: " + e); }
                });
            }
            break;
        case "remove_queued_message":
        {
            // omp 18.4.4+: withdraws the first pending message with that text (steer and follow-up share one queue here)
            var withdrawText = cmd?["message"]?.GetValue<string>() ?? "";
            var stillQueued = new List<(string Text, bool Steering, JsonArray? Images)>();
            var withdrawn = false;
            JsonArray? withdrawnImages = null;
            while (queue.TryDequeue(out var pending))
            {
                if (!withdrawn && pending.Text == withdrawText && pending.Steering == (cmd?["queue"]?.GetValue<string>() == "steering"))
                {
                    withdrawn = true;
                    withdrawnImages = pending.Images;
                }
                else stillQueued.Add(pending);
            }
            foreach (var pending in stillQueued) queue.Enqueue(pending);
            Respond(id, type, new { removed = withdrawn, images = withdrawnImages });
            break;
        }
        case "promote_queued_message":
        {
            // The first follow-up with that text becomes a steer while omp streams; when idle it is delivered alone now.
            var promoteText = cmd?["message"]?.GetValue<string>() ?? "";
            var remaining = new List<(string Text, bool Steering, JsonArray? Images)>();
            (string Text, bool Steering, JsonArray? Images)? promoted = null;
            while (queue.TryDequeue(out var pending))
            {
                if (promoted is null && !pending.Steering && pending.Text == promoteText)
                {
                    promoted = pending;
                    if (streaming) remaining.Add(pending with { Steering = true });
                }
                else remaining.Add(pending);
            }
            foreach (var pending in remaining) queue.Enqueue(pending);
            Respond(id, type, new { promoted = promoted is not null });
            if (promoted is { } now && !streaming)
            {
                Emit(new { type = "agent_start" });
                Emit(new { type = "message_start", message = UserMessage(now.Text, now.Images) });
                var reply = "followed: " + now.Text;
                Emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
                Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = reply } });
                Emit(new { type = "message_end", message = Assistant(reply) });
                Persist(UserMessage(now.Text, now.Images));
                Persist(Assistant(reply));
                streaming = false;
                Emit(new { type = "agent_end", messages = Array.Empty<object>() });
            }
            break;
        }
        case "get_available_commands":
            if (historyPauseCatalog)
            {
                historyHeldCatalogId = id;
                Emit(new { type = "history_catalog_held" });
                break;
            }
            Respond(id, type, new { commands = commandCatalog });
            break;
        case "history_release_start":
            historyPauseCatalog = false;
            Respond(historyHeldCatalogId, "get_available_commands", new { commands = commandCatalog });
            historyHeldCatalogId = null;
            Respond(id, type);
            break;
        case "history_finish_start":
            var historyFinal = Assistant("streaming response");
            Persist(historyFinal);
            Emit(new { type = "message_end", message = historyFinal });
            streaming = false;
            Emit(new { type = "agent_end", messages = Array.Empty<object>(), isTerminal = true });
            Respond(id, type);
            break;
        case "get_available_models":
            Respond(id, type, new
            {
                models = new object[]
                {
                    new { provider = "fake", id = "model", name = "Fake Model", reasoning = scenario != "thinking-levels", input = new[] { "text" }, contextWindow = 128000 },
                    new { provider = "fake", id = "vision", name = "Fake Vision", reasoning = false, input = new[] { "text", "image" }, contextWindow = 32000 },
                }.Concat(scenario != "many-providers" ? [] : new object[]
                {
                    // Two more providers, one with a family of versions (the model menu's accordion)
                    new { provider = "anthropic", id = "claude-opus-5-1", name = "Claude Opus 5.1", reasoning = true, input = new[] { "text" }, contextWindow = 200000 },
                    new { provider = "anthropic", id = "claude-opus-5-5", name = "Claude Opus 5.5", reasoning = true, input = new[] { "text" }, contextWindow = 200000 },
                    new { provider = "anthropic", id = "claude-haiku-5-5", name = "Claude Haiku 5.5", reasoning = true, input = new[] { "text" }, contextWindow = 200000 },
                    new { provider = "openai-codex", id = "gpt-5.6-luna", name = "GPT-5.6-Luna", reasoning = true, input = new[] { "text" }, contextWindow = 272000 },
                }).ToArray(),
            });
            break;
        case "set_model":
            var mid = cmd?["modelId"]?.GetValue<string>() ?? "";
            if (mid is "model" or "vision") { model = ("fake", mid); if (mid == "vision") thinking = null; Respond(id, type, new { provider = "fake", id = mid }); }
            else Respond(id, type, error: $"Model not found: {mid}");
            break;
        case "get_available_thinking_levels" when scenario == "thinking-levels":
            // omp 18.8.0: the levels of the live model, whatever its catalog row says (the model list below marks
            // fake/model as not reasoning in this scenario, like a model newer than omp's catalog)
            Respond(id, type, new { levels = model.id == "vision" ? new[] { "off" } : new[] { "off", "low", "medium", "high", "xhigh", "max" } });
            break;
        case "set_thinking_level":
            thinking = cmd?["level"]?.GetValue<string>();
            Respond(id, type);
            break;
        case "set_fast_mode":
            // Like omp: a model without a priority tier (here: "vision") refuses it
            if (cmd?["enabled"]?.GetValue<bool>() == true && model.id == "vision") { Respond(id, type, error: "Fast mode is unavailable for the current model."); break; }
            fastMode = cmd?["enabled"]?.GetValue<bool>() == true;
            LogOption(type, fastMode);
            Respond(id, type, new { enabled = fastMode, active = fastMode });
            break;
        case "set_auto_compaction":
            autoCompaction = cmd?["enabled"]?.GetValue<bool>() == true;
            LogOption(type, autoCompaction);
            Respond(id, type);
            break;
        case "set_auto_retry":
            autoRetry = cmd?["enabled"]?.GetValue<bool>() == true;
            LogOption(type, autoRetry);
            Respond(id, type);
            break;
        case "get_branch_messages":
            // Like omp: the user's messages, with the entry id a branch starts from
            Respond(id, type, new { messages = history.Select((m, i) => (n: JsonSerializer.SerializeToNode(m), i))
                .Where(x => x.n?["role"]?.GetValue<string>() == "user")
                .Select(x => new { entryId = $"e{x.i}", text = x.n?["content"]?[0]?["text"]?.GetValue<string>() ?? "" }) });
            break;
        case "branch":
            // A new session file with the conversation before that message; answers with the message's text
            var at = int.TryParse(cmd?["entryId"]?.GetValue<string>()?.TrimStart('e'), out var bi) && bi < history.Count ? bi : -1;
            if (at < 0) { Respond(id, type, error: "Invalid entry ID for branching"); break; }
            var chosen = JsonSerializer.SerializeToNode(history[at])?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
            var kept = history.Take(at).ToList();
            history.Clear();
            sessionFile = NewSessionFile();
            freshSessionId = null;
            history.AddRange(kept);
            WriteSessionFile(); // omp forces a branch's file into existence at once
            Respond(id, type, new { text = chosen, cancelled = false });
            break;
        case "get_entries":
            // Like omp: every entry in append order (here one branch: e0 → e1 → …) and the leaf
            Respond(id, type, new
            {
                entries = history.Select((m, i) => new { type = "message", id = $"e{i}", parentId = i == 0 ? null : $"e{i - 1}", timestamp = DateTimeOffset.UtcNow.ToString("O"), message = m }),
                leafId = history.Count == 0 ? null : $"e{history.Count - 1}",
            });
            break;
        case "fork":
        {
            // rpc-mode.ts: refused while the session works ("fork-busy": always, like a running background job);
            // a new session file with the conversation up to that message, inclusive (all of it without one)
            if (streaming || scenario == "fork-busy") { Respond(id, type, error: "Cannot fork the session while it is busy", code: "session_busy"); break; }
            var forkAt = history.Count - 1;
            if (cmd?["entryId"]?.GetValue<string>() is { } forkEntry
                && !(int.TryParse(forkEntry.TrimStart('e'), out forkAt) && forkAt >= 0 && forkAt < history.Count))
            {
                Respond(id, type, error: $"Invalid entry ID for forking: {forkEntry}");
                break;
            }
            if (Environment.GetEnvironmentVariable("FAKE_OMP_COMMAND_LOG") is { Length: > 0 } forkLog)
                Ombra.FakeOmp.FakeEngineHost.AppendLog(forkLog, "fork " + (cmd?["entryId"]?.GetValue<string>() ?? "(latest)") + "\n");
            var forked = history.Take(forkAt + 1).ToList();
            history.Clear();
            sessionFile = NewSessionFile();
            freshSessionId = null;
            history.AddRange(forked);
            WriteSessionFile();
            Respond(id, type, new { cancelled = false });
            break;
        }
        case "bash":
            // Like omp: answered when the command ends (off the command queue); "sleep" waits until abort_bash
            var shell = cmd?["command"]?.GetValue<string>() ?? "";
            if (Environment.GetEnvironmentVariable("FAKE_OMP_COMMAND_LOG") is { Length: > 0 } shellLog) Ombra.FakeOmp.FakeEngineHost.AppendLog(shellLog, "bash " + shell + "\n");
            var shellId = id;
            shellAbort = new CancellationTokenSource();
            var shellToken = shellAbort.Token;
            _ = Task.Run(async () =>
            {
                var stopped = false;
                if (shell.StartsWith("sleep", StringComparison.Ordinal))
                {
                    try { await Task.Delay(30000, shellToken); } catch (OperationCanceledException) { stopped = true; }
                }
                var failed = shell.StartsWith("false", StringComparison.Ordinal);
                Respond(shellId, "bash", new { output = stopped ? "" : $"ran: {shell}\n", exitCode = stopped ? (int?)null : failed ? 1 : 0, cancelled = stopped, truncated = false });
            });
            break;
        case "abort_bash":
            shellAbort?.Cancel();
            Respond(id, type);
            break;
        case "get_session_stats":
        {
            // session-stats.ts: the tallies grow with the conversation (the first prompt carries the system prompt)
            var turns = history.Count;
            long input = turns == 0 ? 0 : 12_480 + 3_100L * turns, output = 820L * turns, cacheRead = 9_600L * Math.Max(0, turns - 1);
            Respond(id, type, new
            {
                sessionId = freshSessionId ?? Path.GetFileNameWithoutExtension(sessionFile), userMessages = (turns + 1) / 2, assistantMessages = turns / 2,
                toolCalls = 0, toolResults = 0, totalMessages = turns,
                tokens = new { input, output, cacheRead, cacheWrite = 0, total = input + output + cacheRead },
                premiumRequests = 0, cost = Math.Round(input * 3e-6 + output * 15e-6 + cacheRead * 0.3e-6, 6), sessionFile,
            });
            break;
        }
        case "get_login_providers":
            Respond(id, type, new { providers = new[] { new { id = "fakeauth", name = "Fake Provider", available = true, authenticated = signedIn } } });
            break;
        case "login":
            // Like omp's OAuth flows: ask the host to open a URL, then (device/manual flows) ask for a code.
            var loginId = id;
            _ = Task.Run(async () =>
            {
                Emit(new { type = "extension_ui_request", id = "u1", method = "open_url", url = "https://example.invalid/oauth?state=abc" });
                var code = await AskAsync(new { type = "extension_ui_request", id = "c1", method = "input", title = "Paste the code", placeholder = "code" }, "c1", null);
                if (code == "1234")
                {
                    signedIn = true;
                    if (changeUsageOnAccount) { usageLimit = usageAfterAccountChange?.DeepClone(); usageLimitByModel = null; }
                    Respond(loginId, "login", new { providerId = "fakeauth" });
                }
                else Respond(loginId, "login", error: "Login cancelled");
            });
            break;
        case "get_logout_accounts":
            // Like omp's logout helper: the stored credentials for the provider, active first.
            Respond(id, type, new
            {
                accounts = signedIn
                    ? new object[] { new { credentialId = 1, provider = "fakeauth", label = "me@example.com", detail = "oauth #1", type = "oauth", active = true } }
                    : [],
            });
            break;
        case "logout":
            var credentialId = cmd?["credentialId"]?.GetValue<int>();
            if (signedIn && credentialId == 1)
            {
                signedIn = remainingAccountSource is not null;
                if (changeUsageOnAccount) { usageLimit = usageAfterAccountChange?.DeepClone(); usageLimitByModel = null; }
                Respond(id, type, new { remainingSource = remainingAccountSource });
            }
            else Respond(id, type, error: $"Credential {credentialId} is not stored for {cmd?["providerId"]?.GetValue<string>()}");
            break;
        case "new_session":
            history.Clear();
            sessionName = null;
            freshSessionId = null;
            sessionFile = NewSessionFile();
            if (scenario == "history-live-switch") historyStartDuringLoad = true;
            Respond(id, type, new { cancelled = false });
            break;
        case "switch_session":
            var target = cmd?["sessionPath"]?.GetValue<string>() ?? "";
            if (File.Exists(target) && !HeaderIsValid(target)) { Respond(id, type, error: Refusal(target)); break; }
            if (Ombra.FakeOmp.FakeEngineHost.LeaseHolder(target) is { } switchHolder)
            {
                // rpc-mode.ts: the target's writer lease belongs to another process; nothing changes here
                Emit(new { type = "response", id, command = type, success = false, error = "This session is open in another window",
                    code = "session_lease_held", data = new System.Text.Json.Nodes.JsonObject { ["sessionId"] = "held", ["holder"] = switchHolder } });
                break;
            }
            var switched = File.Exists(target) && LoadSession(target);
            if (switched && scenario == "reload-fails-once") failNextMessages = true;
            Respond(id, type, new { cancelled = !switched });
            break;
        case "set_session_name":
            sessionName = cmd?["name"]?.GetValue<string>();
            // Like omp, a name given before the file exists does not create it: it is written with the file
            if (sessionOnDisk)
            {
                var all = File.ReadAllLines(sessionFile);
                all[0] = JsonSerializer.Serialize(new { type = "title", v = 1, title = sessionName });
                File.WriteAllLines(sessionFile, all);
            }
            Respond(id, type);
            break;
        case "prompt":
            if (scenario == "bad-chunk")
            {
                // A v2 chunk sequence that never completes, interrupted by a normal frame: the stream is broken.
                session.WriteRaw("""{"type":"rpc_chunk","chunkId":"x","index":0,"count":2,"byteLength":2000000,"data":"AAAA"}""");
                Emit(new { type = "agent_start" });
                break;
            }
            if (scenario == "no-prompt-ack") break; // the request stays pending until the client gives up
            if (scenario == "held-admission" && cmd?["message"]?.GetValue<string>()?.Contains("HOLD", StringComparison.Ordinal) == true)
            {
                heldPromptId = id;
                heldPromptText = cmd?["message"]?.GetValue<string>() ?? "";
                break;
            }
            var slash = cmd?["message"]?.GetValue<string>() ?? "";
            if (slash.StartsWith('/') && Environment.GetEnvironmentVariable("FAKE_OMP_COMMAND_LOG") is { Length: > 0 } commandLog)
                Ombra.FakeOmp.FakeEngineHost.AppendLog(commandLog, slash + "\n");
            if (slash == "/fresh")
            {
                freshSessionId = Guid.NewGuid().ToString();
                Emit(new { type = "command_output", text = "Fresh provider session started (1 provider state pruned)." });
                Respond(id, type, new { inputDisposition = "local", agentInvoked = false });
                break;
            }
            if (scenario is "compaction-refresh" or "compaction-refresh-failed" && slash is "/compact" or "/compact immediate")
            {
                var compactionOutput = scenario == "compaction-refresh-failed"
                    ? "Compaction failed: Nothing to compact (session too small)"
                    : "Compaction complete. Tokens: 1600 -> 320 (saved 1280).";
                if (slash == "/compact immediate")
                {
                    contextPercent = 0.25;
                    Emit(new { type = "command_output", text = compactionOutput });
                    Respond(id, type, new { inputDisposition = "local", agentInvoked = false });
                    break;
                }
                Respond(id, type, new { inputDisposition = "local", agentInvoked = false });
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    contextPercent = 0.25;
                    Emit(new { type = "command_output", text = compactionOutput });
                });
                break;
            }
            // omp 18.2.0's answers to the status reads the model menu makes when it opens (a local builtin, never a turn)
            if (cannedCommands?.ContainsKey(slash) != true && slash is "/extended-context status" or "/advisor status")
            {
                Emit(new { type = "command_output", text = slash == "/advisor status" ? "Advisor is disabled." : "Extended context is off." });
                Respond(id, type, new { inputDisposition = "local", agentInvoked = false });
                break;
            }
            if (slash.StartsWith('/') && cannedCommands?.Where(kv => slash == kv.Key || slash.StartsWith(kv.Key + " ", StringComparison.Ordinal)).OrderByDescending(kv => kv.Key.Length).FirstOrDefault() is { Key: not null } canned)
            {
                // A string: the printed text. An object: {"text", "agentInvoked" (a turn starts, like /retry), "later" (printed
                // after the answer, like /compact and /handoff that omp runs in the background), "laterMs"}.
                var spec = canned.Value as JsonObject;
                foreach (var part in (spec is null ? canned.Value?.GetValue<string>() : spec["text"]?.GetValue<string>() ?? "")!.Split("\f"))
                    if (part.Length > 0 || spec is null) Emit(new { type = "command_output", text = part });
                var invoked = spec?["agentInvoked"]?.GetValue<bool>() == true && !streaming;
                if (invoked) streaming = true;
                Respond(id, type, new { inputDisposition = "local", agentInvoked = invoked });
                if (spec?["later"]?.GetValue<string>() is { } later)
                {
                    var laterMs = spec["laterMs"]?.GetValue<int>() ?? 300;
                    _ = Task.Run(async () => { await Task.Delay(laterMs); Emit(new { type = "command_output", text = later }); });
                }
                if (invoked)
                {
                    run = new CancellationTokenSource();
                    var token = run.Token;
                    activeRun = Task.Run(async () =>
                    {
                        try { await RunAgentAsync("(retried turn)", token, 20, 0); }
                        catch (Exception e) { Console.Error.WriteLine("fake agent failed: " + e); }
                    });
                }
                break;
            }
            if (slash.StartsWith("/stats", StringComparison.Ordinal))
            {
                // A local-only builtin: output frames, then done without an agent run.
                Emit(new { type = "command_output", text = "Messages: " + history.Count + "\nTokens: 1600" });
                Respond(id, type, new { inputDisposition = "local", agentInvoked = false });
                break;
            }
            if (slash.StartsWith("/rename ", StringComparison.Ordinal))
            {
                sessionName = slash["/rename ".Length..].Trim();
                Emit(new { type = "session_info_update", title = sessionName, sessionId = freshSessionId ?? Path.GetFileNameWithoutExtension(sessionFile) });
                Respond(id, type, new { inputDisposition = "local", agentInvoked = false });
                break;
            }
            if (LocalBuiltinOutput(slash) is { } printed)
            {
                if (printed.Length > 0) Emit(new { type = "command_output", text = printed });
                Respond(id, type, new { inputDisposition = "local", agentInvoked = false });
                break;
            }
            if (streaming) { Respond(id, type, error: "Agent is busy"); break; }
            // Like omp: the run exists as soon as the prompt is accepted (abort then waits for it).
            streaming = true;
            Respond(id, type, new { inputDisposition = "admitted", agentInvoked = true });
            run = new CancellationTokenSource();
            var msg = cmd?["message"]?.GetValue<string>() ?? "";
            // "multi": "FOREVER" streams until stopped, "LONG" for FAKE_OMP_LONG_DELTAS deltas 25 ms apart (default 120: 3 s)
            var longDeltas = int.TryParse(Environment.GetEnvironmentVariable("FAKE_OMP_LONG_DELTAS"), out var ld) ? ld : 120;
            var (n, delay) = scenario switch
            {
                "slow-stream" or "abort-hangs" => (100000, 20),
                "multi" or "deferred-input" when msg.Contains("FOREVER", StringComparison.Ordinal) => (100000, 20),
                "multi" or "deferred-input" when msg.Contains("LONG", StringComparison.Ordinal) => (longDeltas, 25),
                _ when scenario.StartsWith("queue-stream", StringComparison.Ordinal) => (60, 20),
                "markdown" or "markdown-stream" or "session" or "showcase" or "explore" or "plan" or "subagents" or "tool-flood" => (0, 0),
                _ => (50, 0),
            };
            var promptImages = cmd?["images"] as JsonArray;
            activeRun = Task.Run(async () =>
            {
                try { await RunAgentAsync(msg, run.Token, n, delay, promptImages); }
                catch (Exception e) { Console.Error.WriteLine("fake agent failed: " + e); }
            });
            break;
        case "set_subagent_subscription" when scenario == "subagents":
            subagentLevel = cmd?["level"]?.GetValue<string>() ?? "off";
            Respond(id, type, new { level = subagentLevel });
            break;
        case "get_subagents" when scenario == "subagents":
            Respond(id, type, new { subagents = new JsonArray(liveSubagents.Values.OrderBy(s => s["index"]!.GetValue<int>()).Select(s => (JsonNode)s.DeepClone()).ToArray()) });
            break;
        case "get_subagent_messages" when scenario == "subagents":
        {
            var agentId = cmd?["subagentId"]?.GetValue<string>() ?? "";
            if (agentId is not ("sa-1" or "sa-2" or "sa-3")) { Respond(id, type, error: $"Unknown subagent or session file unavailable: {agentId}"); break; }
            var fromByte = cmd?["fromByte"]?.GetValue<long>() ?? 0;
            subagentRequests.Add(new { subagentId = agentId, fromByte });
            if (Environment.GetEnvironmentVariable("FAKE_OMP_SUBAGENT_CONTROL") is { Length: > 0 } controlFile
                && File.Exists(controlFile) && JsonNode.Parse(File.ReadAllText(controlFile)) is JsonObject control)
            {
                if (control["messages"] is JsonArray messages) subagentTranscripts[agentId] = (JsonArray)messages.DeepClone();
                if (control["sessionFile"] is { } path) subagentPaths[agentId] = path.GetValue<string>();
                subagentReadError = control["error"]?.GetValue<string>();
                malformedSubagentRead = control["malformed"]?.GetValue<bool>() == true;
                holdSubagentReads = control["hold"]?.GetValue<bool>() == true;
            }
            var page = TranscriptPage(agentId, fromByte);
            if (holdSubagentReads) heldSubagentReads.Add((id, page, subagentReadError));
            else Respond(id, type, page, subagentReadError);
        }
            break;
        case "cancel_subagent" when scenario == "subagents":
        {
            // Like omp: true when it was still running (its lifecycle then reports "aborted"), false when it had ended
            var agentId = cmd?["subagentId"]?.GetValue<string>() ?? "";
            subagentControls.Add(new { type, subagentId = agentId });
            if (cancelSubagentError is { } cancelError) { Respond(id, type, error: cancelError); break; }
            var cancelled = liveSubagents.TryRemove(agentId, out var stopped);
            Respond(id, type, new { cancelled });
            if (cancelled) SubagentLifecycle(agentId, stopped!["index"]!.GetValue<int>(), "aborted", stopped["description"]?.GetValue<string>() ?? agentId);
            break;
        }
        case "steer_subagent" when scenario == "subagents":
        {
            var agentId = cmd?["subagentId"]?.GetValue<string>() ?? "";
            var message = cmd?["message"]?.GetValue<string>() ?? "";
            subagentControls.Add(new { type, subagentId = agentId, message });
            if (steerSubagentError is { } steerError) Respond(id, type, error: steerError);
            else if (!liveSubagents.ContainsKey(agentId)) Respond(id, type, error: $"Subagent {agentId} is not running");
            else Respond(id, type);
            break;
        }
        case "abort_and_restore_queue":
        {
            var restored = new List<(string Text, bool Steering, JsonArray? Images)>();
            while (queue.TryDequeue(out var pending)) restored.Add(pending);
            run?.Cancel();
            var imagesDropped = scenario == "queue-recovery-images-dropped";
            var truncated = scenario == "queue-recovery-truncated";
            if (truncated && restored.Count > 1) restored.RemoveRange(1, restored.Count - 1);
            _ = Task.Run(async () =>
            {
                while (streaming) await Task.Delay(5);
                Respond(id, type, new
                {
                    steering = restored.Where(q => q.Steering).Select(q => new { text = q.Text, images = imagesDropped ? null : q.Images }).ToArray(),
                    followUp = restored.Where(q => !q.Steering).Select(q => new { text = q.Text, images = imagesDropped ? null : q.Images }).ToArray(),
                    truncated,
                    imagesDropped,
                });
            });
            break;
        }
        case "abort" when scenario == "abort-hangs":
            // A tool that ignores the abort signal: omp never becomes idle, so abort is never answered.
            break;
        case "abort":
            // Answered once the agent is idle; the reader keeps going meanwhile (control frames still arrive).
            run?.Cancel();
            if (heldPromptId is { } heldBeforeAbort)
            {
                heldPromptId = null;
                Respond(heldBeforeAbort, "prompt", new { inputDisposition = "cancelled", agentInvoked = false });
            }
            _ = Task.Run(async () =>
            {
                while (streaming) await Task.Delay(5);
                Respond(id, type);
            });
            break;
        case "abort_and_prompt":
        {
            // Like abort, then prompt once idle: answered when the new message is admitted (or refused)
            run?.Cancel();
            var replacement = cmd?["message"]?.GetValue<string>() ?? "";
            var replacementImages = cmd?["images"]?.DeepClone() as JsonArray;
            // The invalidated prompt is answered first; its replacement starts a little later, so the client handles the
            // stale acknowledgement while the replacement's run is still pending. Replacement markers reorder this:
            // "LATE-CANCEL": the replacement runs (and is echoed) before the held prompt's cancelled answer;
            // "ADMIT-LATE": the held prompt was admitted after all: its run's start, echo and end arrive after the
            // interrupt, then a "replaced run ended" notice, and the replacement is admitted 1.5 s later (slow images).
            var superseded = heldPromptId;
            var supersededText = heldPromptText;
            heldPromptId = null;
            var lateCancel = superseded is not null && replacement.Contains("LATE-CANCEL", StringComparison.Ordinal);
            var admitLate = superseded is not null && replacement.Contains("ADMIT-LATE", StringComparison.Ordinal);
            if (admitLate)
            {
                Emit(new { type = "agent_start" });
                Emit(new { type = "message_start", message = UserMessage(supersededText, null) });
                Persist(UserMessage(supersededText, null));
                Emit(new { type = "agent_end", messages = Array.Empty<object>() });
                Emit(new { type = "notice", level = "info", message = "replaced run ended" });
                Respond(superseded, "prompt", new { inputDisposition = "admitted", agentInvoked = true });
            }
            else if (superseded is not null && !lateCancel) Respond(superseded, "prompt", new { inputDisposition = "cancelled", agentInvoked = false });
            _ = Task.Run(async () =>
            {
                if (admitLate) await Task.Delay(1500);
                else if (superseded is not null && !lateCancel) await Task.Delay(300);
                while (streaming) await Task.Delay(5);
                if (replacement.Contains("REJECT", StringComparison.Ordinal)) { Respond(id, type, error: "abort_and_prompt rejected (fake)"); return; }
                streaming = true;
                Respond(id, type, new { inputDisposition = "admitted", agentInvoked = true });
                run = new CancellationTokenSource();
                var replacementToken = run.Token;
                var replacementRun = Task.Run(async () =>
                {
                    try { await RunAgentAsync(replacement, replacementToken, 50, 0, replacementImages); }
                    catch (Exception e) { Console.Error.WriteLine("fake agent failed: " + e); }
                });
                activeRun = replacementRun;
                if (lateCancel)
                {
                    await replacementRun;
                    Respond(superseded, "prompt", new { inputDisposition = "cancelled", agentInvoked = false });
                }
            });
            break;
        }
        default:
            Respond(null, type, error: $"Unknown command: {type}");
            break;
    }
}
}
finally
{
    run?.Cancel();
    shellAbort?.Cancel();
    foreach (var dialog in dialogs.Values) dialog.TrySetResult(null);
    if (activeRun is not null) await activeRun;
}
return 0;
}
