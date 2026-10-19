using System.Text.Json.Nodes;

namespace Ombra.FakeOmp;

/// <summary>
/// Goal mode and side questions like the owned engine (rpc-goal.ts, rpc-btw.ts): <c>goal</c> get/create/pause/resume/drop,
/// the <c>goal</c> field of <c>get_state</c> and <c>goal_updated</c> frames; <c>btw</c> (a running record, then its
/// answer as <c>btw_delta</c> frames and the finished <c>btw_record</c>), <c>btw_cancel</c> and <c>get_btw_history</c>.
/// A question with "SLOW" in it answers for a minute (to cancel). Each command is appended to FAKE_OMP_COMMAND_LOG.
/// Absent (Unknown command, no <c>goal</c> in get_state) when not supported, as in an omp without them.
/// </summary>
internal sealed class FakeBtwGoal(Action<object> emit, Action<string?, string, object?, string?, string?> respond, bool supported)
{
    private readonly object _gate = new();
    private JsonObject? _goal;
    private int _goals;
    /// <summary>Newest first.</summary>
    private readonly List<JsonObject> _records = [];
    private JsonObject? _running;
    private CancellationTokenSource? _answer;
    private int _ids;

    private static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("FAKE_OMP_COMMAND_LOG") is { Length: > 0 } log) FakeEngineHost.AppendLog(log, line + "\n");
    }

    /// <summary><c>get_state.goal</c> (a GoalModeState or null); absent when goal mode is not supported.</summary>
    public bool TryGoalState(out JsonNode? state)
    {
        lock (_gate) state = supported ? ModeState() : null;
        return supported;
    }

    private JsonObject? ModeState() => _goal is null ? null : new JsonObject
    {
        ["enabled"] = _goal["status"]!.GetValue<string>() == "active",
        ["mode"] = "active",
        ["goal"] = _goal.DeepClone(),
    };

    private void GoalUpdated(JsonNode? goal, JsonNode? state) =>
        emit(new JsonObject { ["type"] = "goal_updated", ["goal"] = goal?.DeepClone(), ["state"] = state?.DeepClone() });

    public bool Handle(string? id, string type, JsonNode? cmd)
    {
        if (!supported || type is not ("goal" or "btw" or "btw_cancel" or "get_btw_history")) return false;
        lock (_gate)
        {
            switch (type)
            {
                case "goal":
                    Goal(id, cmd);
                    break;
                case "btw":
                    Ask(id, cmd);
                    break;
                case "btw_cancel":
                    Log("btw_cancel");
                    respond(id, type, new { cancelled = Finish("cancelled") }, null, null);
                    break;
                default:
                    Log("get_btw_history");
                    respond(id, type, new JsonObject { ["records"] = new JsonArray([.. _records.Select(r => (JsonNode)r.DeepClone())]) }, null, null);
                    break;
            }
        }
        return true;
    }

    private void Goal(string? id, JsonNode? cmd)
    {
        var op = cmd?["op"]?.GetValue<string>() ?? "";
        Log("goal " + op + (cmd?["objective"] is { } o ? " " + o.GetValue<string>() : ""));
        var status = _goal?["status"]?.GetValue<string>();
        string? error = null;
        switch (op)
        {
            case "get":
                break;
            case "create":
                var objective = cmd?["objective"]?.GetValue<string>()?.Trim() ?? "";
                if (objective.Length == 0) error = "objective is required when op=create";
                else if (status == "active") error = "A goal is already active. Drop it before creating another.";
                else if (status == "paused") error = "Resume or drop the paused goal before creating another.";
                else
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    _goal = new JsonObject
                    {
                        ["id"] = $"g{++_goals}", ["objective"] = objective, ["status"] = "active", ["tokensUsed"] = 0L, ["timeUsedSeconds"] = 0L,
                        ["createdAt"] = now, ["updatedAt"] = now,
                    };
                    if (cmd?["token_budget"] is { } budget) _goal["tokenBudget"] = budget.GetValue<long>();
                    GoalUpdated(_goal, ModeState());
                }
                break;
            case "pause":
                if (status == "active")
                {
                    _goal!["status"] = "paused";
                    _goal["timeUsedSeconds"] = _goal["timeUsedSeconds"]!.GetValue<long>() + 5;
                    GoalUpdated(_goal, ModeState());
                }
                break;
            case "resume":
                if (status != "paused") { error = "No paused goal to resume."; break; }
                _goal!["status"] = "active";
                GoalUpdated(_goal, ModeState());
                break;
            case "drop":
                if (_goal is null) break;
                var dropped = _goal.DeepClone().AsObject();
                dropped["status"] = "dropped";
                _goal = null;
                GoalUpdated(dropped, new JsonObject { ["enabled"] = false, ["mode"] = "active", ["goal"] = dropped.DeepClone() });
                break;
            default:
                error = $"Unknown goal op: {op}";
                break;
        }
        if (error is not null) respond(id, "goal", null, error, null);
        else respond(id, "goal", new JsonObject { ["goal"] = _goal?.DeepClone(), ["state"] = ModeState() }, null, null);
    }

    private void Ask(string? id, JsonNode? cmd)
    {
        var question = cmd?["question"]?.GetValue<string>()?.Trim() ?? "";
        Log("btw " + question);
        if (question.Length == 0) { respond(id, "btw", null, "btw requires a non-empty question", null); return; }
        if (_running is not null) { respond(id, "btw", null, "A /btw question is still running; cancel it first", null); return; }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var turn = new JsonObject { ["question"] = question, ["answer"] = "", ["status"] = "running", ["createdAt"] = now, ["updatedAt"] = now };
        JsonObject record;
        if (cmd?["recordId"]?.GetValue<string>() is { } topic)
        {
            if (_records.Find(r => r["id"]!.GetValue<string>() == topic) is not { } previous)
            {
                respond(id, "btw", null, $"Unknown /btw topic: {topic}", null);
                return;
            }
            record = previous;
            (record["followUps"] ??= new JsonArray()).AsArray().Add(turn);
        }
        else
        {
            record = turn.DeepClone().AsObject();
            record["id"] = $"btw{++_ids}";
            record["leafId"] = null;
            _records.Insert(0, record);
        }
        _running = record;
        emit(new JsonObject { ["type"] = "btw_record", ["record"] = record.DeepClone() });
        respond(id, "btw", new JsonObject { ["record"] = record.DeepClone() }, null, null);
        var answer = $"Side answer about {question}: the conversation says so.";
        var slow = question.Contains("SLOW", StringComparison.Ordinal);
        var cts = _answer = new CancellationTokenSource();
        var recordId = record["id"]!.GetValue<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                var words = slow ? Enumerable.Range(0, 600).Select(i => $"w{i} ").ToArray() : answer.Split(' ').Select(w => w + " ").ToArray();
                foreach (var word in words)
                {
                    await Task.Delay(slow ? 100 : 20, cts.Token);
                    lock (_gate)
                    {
                        if (cts.IsCancellationRequested || !ReferenceEquals(_running, record)) return;
                        var latest = Latest(record);
                        latest["answer"] = latest["answer"]!.GetValue<string>() + word;
                        emit(new { type = "btw_delta", recordId, delta = word });
                    }
                }
                lock (_gate) if (!cts.IsCancellationRequested && ReferenceEquals(_running, record)) Finish("complete");
            }
            catch (OperationCanceledException) { }
        });
    }

    private static JsonObject Latest(JsonObject record) =>
        record["followUps"] is JsonArray { Count: > 0 } follow ? follow[^1]!.AsObject() : record;

    /// <summary>Ends the running question as <paramref name="status"/>; false when none ran.</summary>
    private bool Finish(string status)
    {
        if (_running is not { } record) return false;
        _running = null;
        _answer?.Cancel();
        var latest = Latest(record);
        latest["status"] = status;
        latest["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        emit(new JsonObject { ["type"] = "btw_record", ["record"] = record.DeepClone() });
        return true;
    }
}
