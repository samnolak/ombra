using System.Text.Json.Nodes;

namespace Ombra.FakeOmp;

/// <summary>
/// Plan mode over RPC like the owned engine (rpc-plan.ts): <c>plan_mode</c> get/enter/exit/approve/reject, the
/// <c>planMode</c> field of <c>get_state</c> and <c>plan_mode_updated</c> frames. While plan mode is on a prompt runs a
/// planning turn that ends with a proposed plan (<c>review</c>, the Markdown document); approve leaves plan mode and
/// runs the execution turn; reject with feedback runs the next planning turn, which proposes a revised plan. Each
/// command is appended to FAKE_OMP_COMMAND_LOG. Absent (Unknown command, no <c>planMode</c>) when not supported, as
/// in an omp without it.
/// </summary>
internal sealed class FakePlanMode(Action<object> emit, Action<string?, string, object?, string?, string?> respond, bool supported)
{
    public const string PlanFile = "local://demo-plan.md";
    public const string Plan = "# Demo plan\n\n1. Add the greeting.\n2. Test it.\n";

    private readonly object _gate = new();
    private string? _planFilePath;
    private string? _review;
    private int _revisions;
    /// <summary>A "SLOW" planning message runs this long (the review stays; approve/reject are refused meanwhile).</summary>
    private bool _running;

    private static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("FAKE_OMP_COMMAND_LOG") is { Length: > 0 } log) FakeEngineHost.AppendLog(log, line + "\n");
    }

    /// <summary><c>get_state.planMode</c>; null when plan mode is not supported.</summary>
    public JsonNode? State()
    {
        if (!supported) return null;
        lock (_gate) return Snapshot();
    }

    private JsonObject Snapshot() => new()
    {
        ["state"] = _planFilePath is { } path ? new JsonObject { ["enabled"] = true, ["planFilePath"] = path, ["workflow"] = "parallel" } : null,
        ["review"] = _review is { } content ? new JsonObject { ["revision"] = $"r{_revisions}", ["planFilePath"] = PlanFile, ["title"] = "demo", ["content"] = content } : null,
    };

    private void Updated()
    {
        var frame = Snapshot();
        frame["type"] = "plan_mode_updated";
        emit(frame);
    }

    /// <summary>Answers <c>plan_mode</c>, and a prompt while planning; false for anything else.</summary>
    public bool Handle(string? id, string type, JsonNode? cmd)
    {
        if (!supported) return false;
        lock (_gate)
        {
            if (type == "prompt" && _planFilePath is not null)
            {
                var message = cmd?["message"]?.GetValue<string>() ?? "";
                if (message.StartsWith('/')) return false;
                Log("prompt (planning) " + message);
                respond(id, type, new { inputDisposition = "admitted", agentInvoked = true }, null, null);
                if (message.Contains("SLOW", StringComparison.Ordinal)) SlowTurn(message);
                else PlanningTurn(message);
                return true;
            }
            if (type != "plan_mode") return false;
            var op = cmd?["op"]?.GetValue<string>() ?? "";
            var feedback = cmd?["feedback"]?.GetValue<string>();
            Log("plan_mode " + op + (string.IsNullOrEmpty(feedback) ? "" : " " + feedback));
            if (op is "approve" or "reject")
            {
                // Like rpc-plan.ts: only the review shown, and never while a turn runs
                if (_planFilePath is null || _review is null || cmd?["revision"]?.GetValue<string>() != $"r{_revisions}")
                {
                    respond(id, type, null, "This plan is no longer the one awaiting review; review the current plan.", "plan_review_stale");
                    return true;
                }
                if (_running)
                {
                    respond(id, type, null, "omp is still working in plan mode; stop it or wait before approving or rejecting the plan.", "session_busy");
                    return true;
                }
            }
            switch (op)
            {
                case "get":
                    respond(id, type, Snapshot(), null, null);
                    break;
                case "enter":
                    _planFilePath ??= cmd?["planFilePath"]?.GetValue<string>() ?? "local://PLAN.md";
                    respond(id, type, Snapshot(), null, null);
                    Updated();
                    break;
                case "exit":
                    _planFilePath = null;
                    _review = null;
                    respond(id, type, Snapshot(), null, null);
                    Updated();
                    break;
                case "approve":
                {
                    if (_review is not { } approved) break;
                    _planFilePath = null;
                    _review = null;
                    respond(id, type, Snapshot(), null, null);
                    Updated();
                    Turn(null, "Executing the approved plan: " + approved.Split('\n')[0].TrimStart('#', ' '));
                    break;
                }
                case "reject":
                    _review = null;
                    respond(id, type, Snapshot(), null, null);
                    Updated();
                    if (!string.IsNullOrWhiteSpace(feedback)) PlanningTurn(feedback.Trim());
                    break;
                default:
                    respond(id, type, null, $"Unknown plan_mode op: {op}", null);
                    break;
            }
            return true;
        }
    }

    /// <summary>A planning turn: the agent drafts the plan and proposes it; the turn stops and the review opens.</summary>
    private void PlanningTurn(string message)
    {
        _planFilePath = PlanFile;
        Turn(message, "Drafted the plan.");
        _review = _revisions == 0 ? Plan : Plan + $"3. {message}\n";
        _revisions++;
        Updated();
    }

    /// <summary>A planning turn that takes a while (no new proposal): the session runs, then ends.</summary>
    private void SlowTurn(string message)
    {
        _running = true;
        emit(new { type = "agent_start" });
        emit(new { type = "message_start", message = new { role = "user", content = new[] { new { type = "text", text = message } } } });
        emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            lock (_gate)
            {
                _running = false;
                emit(new { type = "message_end", message = new { role = "assistant", content = new[] { new { type = "text", text = "Looked further." } }, stopReason = "stop" } });
                emit(new { type = "agent_end", messages = Array.Empty<object>() });
            }
        });
    }

    private void Turn(string? user, string reply)
    {
        emit(new { type = "agent_start" });
        if (user is not null) emit(new { type = "message_start", message = new { role = "user", content = new[] { new { type = "text", text = user } } } });
        emit(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });
        emit(new { type = "message_update", assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = reply } });
        emit(new { type = "message_end", message = new { role = "assistant", content = new[] { new { type = "text", text = reply } }, stopReason = "stop" } });
        emit(new { type = "agent_end", messages = Array.Empty<object>() });
    }
}
