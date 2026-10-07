// Scripted omp stand-in speaking the RPC wire format of omp 18.2.0 (docs/rpc.md), for tests only.
//   OmpGui.FakeOmp <scenario>
// Scenarios: normal | nonterminal-end | bad-chunk | crash-before-ready | no-models | exit-on-request | garbage | out-of-order | duplicate | silent |
//            big-messages | ignore-eof | stderr-flood | slow-stream | approval | approval-timeout | ask | no-prompt-ack | queue-stream[-N] | abort-hangs | approval-chain | reload-fails-once | markdown | todo
//            plan (each prompt moves a two-phase plan on; the third replaces it) | subagents (subagent frames, get_subagents, /jobs)
//            recorder | recorder-fails (audio recorder stand-ins, not omp) | cli <args> (omp's CLI subcommands, see FAKE_OMP_CLI)
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var scenario = args.Length > 0 ? args[0] : "normal";
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
    if (hit is null) { Console.Error.WriteLine($"fake omp: no answer for \"{line}\""); return 1; }
    Console.Out.Write(hit["stdout"]?.GetValue<string>() ?? "");
    Console.Error.Write(hit["stderr"]?.GetValue<string>() ?? "");
    return hit["exit"]?.GetValue<int>() ?? 0;
}
// Builtin slash commands answered like omp answers them over RPC: FAKE_OMP_COMMANDS names a JSON file mapping a
// command prefix ("/mcp list") to the text it prints; each command sent is appended to FAKE_OMP_COMMAND_LOG.
var cannedCommands = Environment.GetEnvironmentVariable("FAKE_OMP_COMMANDS") is { Length: > 0 } cmdFile && File.Exists(cmdFile)
    ? JsonNode.Parse(File.ReadAllText(cmdFile)) as JsonObject : null;
var resumeAt = Array.IndexOf(args, "--resume");
var resumePath = resumeAt >= 0 && resumeAt + 1 < args.Length ? args[resumeAt + 1] : null;
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var gate = new object();
var protocol = 1;
var streaming = false;
CancellationTokenSource? run = null;
var dialogs = new ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>>();
var dialogSeq = 0;
var stateCalls = 0;

// Like omp 18.2.0's tool approval (extensibility/extensions/wrapper.ts): a select that the run's abort signal does
// not reach, answered only by extension_ui_response or by its own timeout (then the default: undefined = denied).
async Task<string?> AskAsync(object request, string id, int? timeoutMs)
{
    var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
    dialogs[id] = tcs;
    Emit(request);
    var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs ?? Timeout.Infinite));
    dialogs.TryRemove(id, out _);
    if (done != tcs.Task) return null;
    var r = await tcs.Task;
    return r?["cancelled"]?.GetValue<bool>() == true ? null : r?["value"]?.GetValue<string>();
}

void Emit(object frame)
{
    var json = JsonSerializer.Serialize(frame);
    lock (gate)
    {
        if (protocol == 2 && Encoding.UTF8.GetByteCount(json) + 1 > 1024 * 1024)
        {
            // Official v2 chunking: 256 KiB raw slices, base64, uninterrupted sequence.
            var bytes = Encoding.UTF8.GetBytes(json);
            var count = (bytes.Length + 262143) / 262144;
            for (var i = 0; i < count; i++)
            {
                var slice = bytes.AsSpan(i * 262144, Math.Min(262144, bytes.Length - i * 262144));
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "rpc_chunk", chunkId = "rpc-1", index = i, count, byteLength = bytes.Length, data = Convert.ToBase64String(slice),
                }));
            }
            return;
        }
        stdout.WriteLine(json);
    }
}

void Respond(string? id, string command, object? data = null, string? error = null)
{
    var o = new JsonObject { ["type"] = "response", ["command"] = command, ["success"] = error is null };
    if (id is not null) o["id"] = id;
    if (data is not null) o["data"] = JsonSerializer.SerializeToNode(data);
    if (error is not null) o["error"] = error;
    Emit(o);
}

// Sessions like omp 18.2.0: <root>/<project>/<file>.jsonl with a title line and a session header (cwd).
var sessionsRoot = Environment.GetEnvironmentVariable("FAKE_SESSION_DIR") ?? Path.Combine(Path.GetTempPath(), $"ompgui-fake-{Environment.ProcessId}");
var cwd = Environment.CurrentDirectory;
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
// The option setters are logged with the slash commands (FAKE_OMP_COMMAND_LOG), so tests see what reached omp
void LogOption(string command, bool value)
{
    if (Environment.GetEnvironmentVariable("FAKE_OMP_COMMAND_LOG") is { Length: > 0 } log) File.AppendAllText(log, $"{command} {(value ? "true" : "false")}\n");
}
var failNextMessages = false;
object[] todoPhases = [];
// "plan" and "subagents" scenarios (the side panes): which prompt this is, omp's subagent subscription and registry.
var planStep = 0;
var subagentLevel = "off";
var liveSubagents = new ConcurrentDictionary<string, JsonObject>();
var commandCatalog = new object[]
{
    new { name = "compact", description = "Compact the conversation context", source = "builtin" },
    new { name = "stats", description = "Show session statistics", source = "builtin" },
    new { name = "stats-reset", description = "Reset the statistics", source = "builtin" }, // "/stats" is also a prefix of another command
    new { name = "rename", description = "Rename the session", input = new { hint = "<name>" }, source = "builtin" },
    new { name = "mcp", description = "Manage MCP servers", source = "builtin", subcommands = new[] { new { name = "list", description = "List servers" }, new { name = "enable", description = "Enable a server" } } },
    new { name = "skill:systematic-debugging", description = "Debug step by step", source = "skill" },
    new { name = "model", aliases = new[] { "m" }, description = "Switch model", source = "builtin" },
};
// The side panes' scenarios answer /jobs, and list it as omp lists the builtins it runs over RPC
if (scenario is "plan" or "subagents") commandCatalog = [.. commandCatalog, new { name = "jobs", description = "Show background jobs", source = "builtin" }];
string sessionFile = "";

string NewSessionFile()
{
    var project = Path.Combine(sessionsRoot, "-" + string.Concat(cwd.Select(c => char.IsLetterOrDigit(c) ? c : '-')));
    Directory.CreateDirectory(project);
    var id = Guid.NewGuid().ToString();
    var file = Path.Combine(project, $"{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss-fffZ}_{id}.jsonl");
    File.WriteAllText(file, JsonSerializer.Serialize(new { type = "title", v = 1, title = "" }) + "\n"
        + JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd }) + "\n");
    return file;
}

// Loads a session file; false when it was recorded in another working directory (omp 18.2.0 declines those).
bool LoadSession(string file)
{
    var lines = File.ReadAllLines(file);
    var header = lines.Select(l => JsonNode.Parse(l)).First(n => n?["type"]?.GetValue<string>() == "session");
    if (!string.Equals(header!["cwd"]!.GetValue<string>(), cwd, StringComparison.Ordinal)) return false;
    history.Clear();
    sessionName = JsonNode.Parse(lines[0])?["title"]?.GetValue<string>() is { Length: > 0 } t ? t : null;
    foreach (var n in lines.Select(l => JsonNode.Parse(l)).Where(n => n?["type"]?.GetValue<string>() == "message"))
        history.Add(n!["message"]!.DeepClone());
    // Like omp: the todo list comes back from the last todo result saved in the session
    if (history.OfType<JsonNode>().LastOrDefault(m => m["role"]?.GetValue<string>() == "toolResult" && m["toolName"]?.GetValue<string>() == "todo")?["details"]?["phases"] is JsonArray saved)
        todoPhases = [.. saved.Select(p => (object)p!.DeepClone())];
    sessionFile = file;
    return true;
}

void Persist(object message)
{
    history.Add(message);
    File.AppendAllText(sessionFile, JsonSerializer.Serialize(new { type = "message", message }) + "\n");
}

if (resumePath is not null && File.Exists(resumePath) && LoadSession(resumePath)) { }
else sessionFile = NewSessionFile();

object Assistant(string text) => new { role = "assistant", content = new[] { new { type = "text", text } }, stopReason = "stop" };

// steer / follow_up while streaming (omp keeps them after an explicit abort; they go with the next run).
var queue = new ConcurrentQueue<string>();
DateTime? lastQueuedAt = null;
var queuedCount = 0;

object UserMessage(string text, JsonArray? images)
{
    var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
    foreach (var img in images ?? []) content.Add(new JsonObject { ["type"] = "image", ["mimeType"] = img?["mimeType"]?.GetValue<string>(), ["data"] = "" });
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
    for (var i = 0; untilQueued ? i < 1500 && (queuedCount < queuedWanted || lastQueuedAt is not { } q || DateTime.UtcNow - q < TimeSpan.FromSeconds(1)) : i < deltas; i++)
    {
        if (ct.IsCancellationRequested) { aborted = true; break; }
        var d = $"tok{i} ";
        sb.Append(d);
        Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = d } });
        if (delayMs > 0) { try { await Task.Delay(delayMs, ct); } catch (OperationCanceledException) { aborted = true; break; } }
    }
    if (!aborted && scenario == "session")
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
            ["recentTools"] = new JsonArray(new JsonObject { ["tool"] = "read", ["args"] = "src/OmpGui.ClientCore/ConversationState.cs", ["endMs"] = 1 }, new JsonObject { ["tool"] = "grep", ["args"] = "todoPhases", ["endMs"] = 2 }),
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
        Report(Progress("sa-1", 0, "explore", "running", t1, "grep", "todoPhases src/", ["src/OmpGui.ClientCore/ConversationState.cs:106", "src/OmpGui.App/ViewModels/MainViewModel.Commands.cs:152"], 4, 8200, 4200));
        Report(Progress("sa-2", 1, "reviewer", "running", t2, "read", "src/OmpGui.App/Views/Panes/PlanPane.axaml", ["Reading the pane"], 2, 5100, 3100));
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
    else if (!aborted && scenario is "approval" or "approval-timeout")
    {
        Emit(new { type = "tool_execution_start", toolCallId = "t1", toolName = "bash", args = new { command = "echo hi" } });
        var did = $"ap{Interlocked.Increment(ref dialogSeq)}";
        // Long enough that the card (deadline minus the client's 1 s margin) is seen on slow runners (CI run 18, Windows).
        var timeout = scenario == "approval-timeout" ? 4000 : (int?)null;
        var choice = await AskAsync(new { type = "extension_ui_request", id = did, method = "select", title = "Allow tool: bash\n$ echo hi", options = new[] { "Approve", "Deny" }, timeout }, did, timeout);
        if (choice == "Approve")
            Emit(new { type = "tool_execution_end", toolCallId = "t1", toolName = "bash", result = new { content = new[] { new { type = "text", text = "hi" } } } });
        else
            Emit(new { type = "tool_execution_end", toolCallId = "t1", toolName = "bash", isError = true, result = new { content = new[] { new { type = "text", text = "Tool call denied by user: bash" } } } });
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
        Emit(new { type = "message_start", message = UserMessage(queued, null) });
        var reply = "followed: " + queued;
        Emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
        Emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = reply } });
        Emit(new { type = "message_end", message = Assistant(reply) });
        Persist(UserMessage(queued, null));
        Persist(Assistant(reply));
    }
    // omp is idle when it reports the end: a prompt sent right after agent_end must not be "busy".
    streaming = false;
    Emit(new { type = "agent_end", messages = Array.Empty<object>() });
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
Emit(new { type = "available_commands_update", commands = commandCatalog });
if (scenario == "garbage")
{
    stdout.WriteLine("this is not json");
    stdout.WriteLine("[1,2,3]");
    stdout.WriteLine("");
    Emit(new { type = "some_future_event", payload = 1 });
}

var held = new List<(string? id, string type)>();
while (true)
{
    var line = await stdin.ReadLineAsync();
    if (line is null)
    {
        if (scenario == "ignore-eof") await Task.Delay(Timeout.Infinite);
        run?.Cancel();
        return 0;
    }
    if (line.Length == 0) continue;
    JsonNode? cmd;
    try { cmd = JsonNode.Parse(line); }
    catch (JsonException e) { Respond(null, "parse", error: e.Message); continue; }
    var id = cmd?["id"]?.GetValue<string>();
    var type = cmd?["type"]?.GetValue<string>() ?? "";
    if (type == "extension_ui_response")
    {
        // A control frame: overtakes queued commands (rpc-mode.ts dispatchRpcControlFrame). Late answers are dropped.
        if (id is not null && dialogs.TryRemove(id, out var pending)) pending.SetResult(cmd);
        continue;
    }

    if (scenario == "silent" && type != "negotiate_protocol") continue;
    if (scenario == "exit-on-request" && type == "get_state") return 5;
    if (scenario == "out-of-order" && type is "get_state" or "get_available_models")
    {
        held.Add((id, type));
        if (held.Count < 2) continue;
        for (var i = held.Count - 1; i >= 0; i--) Respond(held[i].id, held[i].type, new { order = i });
        held.Clear();
        continue;
    }

    switch (type)
    {
        case "negotiate_protocol":
            Respond(id, type, new { protocolVersion = 2 });
            protocol = 2;
            break;
        case "get_state":
            // "todo-state-glitch": the second get_state (the first refresh after start) succeeds without data.
            if (scenario == "todo-state-glitch" && ++stateCalls == 2) { Respond(id, type); break; }
            Respond(id, type, new { model = new { provider = model.provider, id = model.id, name = model.id }, thinkingLevel = thinking, isStreaming = streaming, sessionId = Path.GetFileNameWithoutExtension(sessionFile), sessionFile, sessionName, messageCount = history.Count, queuedMessageCount = queue.Count, todoPhases, contextUsage = new { tokens = 1600, contextWindow = 128000, percent = 1.25 }, fastModeEnabled = fastMode, fastModeActive = fastMode, autoCompactionEnabled = autoCompaction, isCompacting = false });
            if (scenario == "duplicate") Respond(id, type, new { duplicate = true });
            break;
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
            queue.Enqueue(cmd?["message"]?.GetValue<string>() ?? "");
            lastQueuedAt = DateTime.UtcNow;
            Interlocked.Increment(ref queuedCount);
            Respond(id, type);
            break;
        case "get_available_commands":
            Respond(id, type, new { commands = commandCatalog });
            break;
        case "get_available_models":
            Respond(id, type, new
            {
                models = new object[]
                {
                    new { provider = "fake", id = "model", name = "Fake Model", reasoning = scenario != "thinking-levels", input = new[] { "text" }, contextWindow = 128000 },
                    new { provider = "fake", id = "vision", name = "Fake Vision", reasoning = false, input = new[] { "text", "image" }, contextWindow = 32000 },
                },
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
            foreach (var m in kept) Persist(m);
            Respond(id, type, new { text = chosen, cancelled = false });
            break;
        case "bash":
            // Like omp: answered when the command ends (off the command queue); "sleep" waits until abort_bash
            var shell = cmd?["command"]?.GetValue<string>() ?? "";
            if (Environment.GetEnvironmentVariable("FAKE_OMP_COMMAND_LOG") is { Length: > 0 } shellLog) File.AppendAllText(shellLog, "bash " + shell + "\n");
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
                if (code == "1234") { signedIn = true; Respond(loginId, "login", new { providerId = "fakeauth" }); }
                else Respond(loginId, "login", error: "Login cancelled");
            });
            break;
        case "new_session":
            history.Clear();
            sessionName = null;
            sessionFile = NewSessionFile();
            Respond(id, type, new { cancelled = false });
            break;
        case "switch_session":
            var target = cmd?["sessionPath"]?.GetValue<string>() ?? "";
            var switched = File.Exists(target) && LoadSession(target);
            if (switched && scenario == "reload-fails-once") failNextMessages = true;
            Respond(id, type, new { cancelled = !switched });
            break;
        case "set_session_name":
            sessionName = cmd?["name"]?.GetValue<string>();
            var all = File.ReadAllLines(sessionFile);
            all[0] = JsonSerializer.Serialize(new { type = "title", v = 1, title = sessionName });
            File.WriteAllLines(sessionFile, all);
            Respond(id, type);
            break;
        case "prompt":
            if (scenario == "bad-chunk")
            {
                // A v2 chunk sequence that never completes, interrupted by a normal frame: the stream is broken.
                stdout.WriteLine("""{"type":"rpc_chunk","chunkId":"x","index":0,"count":2,"byteLength":2000000,"data":"AAAA"}""");
                Emit(new { type = "agent_start" });
                break;
            }
            if (scenario == "no-prompt-ack") break; // the request stays pending until the client gives up
            var slash = cmd?["message"]?.GetValue<string>() ?? "";
            if (slash.StartsWith('/') && Environment.GetEnvironmentVariable("FAKE_OMP_COMMAND_LOG") is { Length: > 0 } commandLog)
                File.AppendAllText(commandLog, slash + "\n");
            // omp 18.2.0's answers to the status reads the model menu makes when it opens (a local builtin, never a turn)
            if (cannedCommands?.ContainsKey(slash) != true && slash is "/extended-context status" or "/advisor status")
            {
                Emit(new { type = "command_output", text = slash == "/advisor status" ? "Advisor is disabled." : "Extended context is off." });
                Respond(id, type, new { agentInvoked = false });
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
                Respond(id, type, new { agentInvoked = invoked });
                if (spec?["later"]?.GetValue<string>() is { } later)
                {
                    var laterMs = spec["laterMs"]?.GetValue<int>() ?? 300;
                    _ = Task.Run(async () => { await Task.Delay(laterMs); Emit(new { type = "command_output", text = later }); });
                }
                if (invoked)
                {
                    run = new CancellationTokenSource();
                    var token = run.Token;
                    _ = Task.Run(async () =>
                    {
                        try { await RunAgentAsync("(retried turn)", token, 20, 0); }
                        catch (Exception e) { Console.Error.WriteLine("fake agent failed: " + e); }
                    });
                }
                break;
            }
            if (scenario is "subagents" or "plan" && slash.StartsWith("/jobs", StringComparison.Ordinal))
            {
                // omp's /jobs listing (slash-commands/builtin-session.ts)
                Emit(new { type = "command_output", text = liveSubagents.IsEmpty
                    ? "No background jobs running. (Background jobs run async tools — e.g. long-running bash, debug, or task subagents that would otherwise tie up a turn. They appear here while alive and for ~5 minutes after.)"
                    : "Background Jobs\nRunning: 2\n\nRunning Jobs\n  [bg-1] bash (running) — 2m\n    npm run dev\n  [bg-2] task (running) — 1m\n    Watch the dev server\n\nRecent Jobs\n  [bg-0] bash (completed) — 4m\n    npm test -- --watch=false" });
                Respond(id, type, new { agentInvoked = false });
                break;
            }
            if (slash.StartsWith("/stats", StringComparison.Ordinal))
            {
                // A local-only builtin: output frames, then done without an agent run.
                Emit(new { type = "command_output", text = "Messages: " + history.Count + "\nTokens: 1600" });
                Respond(id, type, new { agentInvoked = false });
                break;
            }
            if (slash.StartsWith("/rename ", StringComparison.Ordinal))
            {
                sessionName = slash["/rename ".Length..].Trim();
                Emit(new { type = "session_info_update", title = sessionName, sessionId = Path.GetFileNameWithoutExtension(sessionFile) });
                Respond(id, type, new { agentInvoked = false });
                break;
            }
            if (streaming) { Respond(id, type, error: "Agent is busy"); break; }
            // Like omp: the run exists as soon as the prompt is accepted (abort then waits for it).
            streaming = true;
            Respond(id, type);
            run = new CancellationTokenSource();
            var msg = cmd?["message"]?.GetValue<string>() ?? "";
            var (n, delay) = scenario switch { "slow-stream" or "abort-hangs" => (100000, 20), "queue-stream" or "queue-stream-2" => (60, 20), "markdown" or "session" or "plan" or "subagents" => (0, 0), _ => (50, 0) };
            var promptImages = cmd?["images"] as JsonArray;
            _ = Task.Run(async () =>
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
            if (cmd?["subagentId"]?.GetValue<string>() is not ("sa-1" or "sa-2" or "sa-3")) { Respond(id, type, error: $"Unknown subagent or session file unavailable: {cmd?["subagentId"]}"); break; }
            Respond(id, type, new
            {
                sessionFile = "sa.jsonl", fromByte = 0, nextByte = 10, reset = false, entries = Array.Empty<object>(),
                messages = new object[]
                {
                    new { role = "user", content = "Find where omp's todo phases reach the client." },
                    new { role = "assistant", content = new object[] { new { type = "text", text = "Searching for the readers." }, new { type = "toolCall", id = "c1", name = "grep", arguments = new { pattern = "todoPhases" } } } },
                    new { role = "toolResult", toolCallId = "c1", content = new[] { new { type = "text", text = "src/OmpGui.ClientCore/ConversationState.cs:106" } }, isError = false },
                    new { role = "assistant", content = new[] { new { type = "text", text = "Found 3 readers." } } },
                },
            });
            break;
        case "abort" when scenario == "abort-hangs":
            // A tool that ignores the abort signal: omp never becomes idle, so abort is never answered.
            break;
        case "abort":
            // Answered once the agent is idle; the reader keeps going meanwhile (control frames still arrive).
            run?.Cancel();
            _ = Task.Run(async () =>
            {
                while (streaming) await Task.Delay(5);
                Respond(id, type);
            });
            break;
        default:
            Respond(null, type, error: $"Unknown command: {type}");
            break;
    }
}
