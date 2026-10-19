using System.Text.Json;

namespace Ombra.Rpc;

/// <summary>
/// The session's goal (goal mode, rpc-goal.ts): the objective omp works toward across turns, its lifecycle and what it
/// used. <see cref="Status"/>: active, paused, budget-limited, complete (a dropped goal is no goal).
/// </summary>
public sealed record OmpGoal(string Id, string Objective, string Status, long TokensUsed, long? TokenBudget, long TimeUsedSeconds)
{
    public bool IsActive => Status is "active" or "budget-limited";
    public bool IsPaused => Status == "paused";
    public bool IsComplete => Status == "complete";

    /// <summary>A <c>Goal</c> object; null for anything else and for a dropped goal.</summary>
    public static OmpGoal? Parse(JsonElement g)
    {
        if (g.ValueKind != JsonValueKind.Object) return null;
        if (Str(g, "id") is not { } id || Str(g, "objective") is not { } objective || Str(g, "status") is not { } status) return null;
        if (status == "dropped") return null;
        return new OmpGoal(id, objective, status, Int(g, "tokensUsed") ?? 0, Int(g, "tokenBudget"), Int(g, "timeUsedSeconds") ?? 0);
    }

    /// <summary>A <c>GoalModeState</c> (<c>get_state.goal</c>): its <c>goal</c>; null for null.</summary>
    public static OmpGoal? ParseState(JsonElement state) =>
        state.ValueKind == JsonValueKind.Object && state.TryGetProperty("goal", out var g) ? Parse(g) : null;

    internal static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    internal static long? Int(JsonElement e, string n) =>
        e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) ? (long)d : null;
}

/// <summary>One question of a side-question topic and its answer. <see cref="Status"/>: running, complete, cancelled, error, interrupted.</summary>
public sealed record OmpBtwTurn(string Question, string Answer, string Status, string? Error)
{
    public bool IsRunning => Status == "running";
}

/// <summary>
/// A side-question topic (<c>/btw</c>, rpc-btw.ts): asked beside the conversation, answered from its context without
/// tools, kept in the session's BTW history. <see cref="Turns"/>: the first question, then its follow-ups.
/// </summary>
public sealed record OmpBtwRecord(string Id, long CreatedAt, IReadOnlyList<OmpBtwTurn> Turns)
{
    public OmpBtwTurn Latest => Turns[^1];
    public bool IsRunning => Latest.IsRunning;

    /// <summary>A <c>BtwHistoryRecord</c>; null when it is not one.</summary>
    public static OmpBtwRecord? Parse(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object || OmpGoal.Str(r, "id") is not { } id || ParseTurn(r) is not { } first) return null;
        var turns = new List<OmpBtwTurn> { first };
        if (r.TryGetProperty("followUps", out var f) && f.ValueKind == JsonValueKind.Array)
            foreach (var t in f.EnumerateArray())
                if (ParseTurn(t) is { } turn) turns.Add(turn);
        return new OmpBtwRecord(id, OmpGoal.Int(r, "createdAt") ?? 0, turns);
    }

    private static OmpBtwTurn? ParseTurn(JsonElement t) =>
        t.ValueKind == JsonValueKind.Object && OmpGoal.Str(t, "question") is { } q
            ? new OmpBtwTurn(q, OmpGoal.Str(t, "answer") ?? "", OmpGoal.Str(t, "status") ?? "complete", OmpGoal.Str(t, "error"))
            : null;

    /// <summary>The topic with <paramref name="answer"/> as the latest question's answer so far (<c>btw_delta</c> frames, joined).</summary>
    public OmpBtwRecord WithLatestAnswer(string answer)
    {
        var turns = Turns.ToArray();
        turns[^1] = turns[^1] with { Answer = answer };
        return this with { Turns = turns };
    }
}

/// <summary>The goal and side-question commands of Ombra's engine (rpc-types.ts: <c>goal</c>, <c>btw</c>, <c>btw_cancel</c>, <c>get_btw_history</c>).</summary>
public static class OmpBtwGoalCommands
{
    /// <summary>
    /// <c>goal</c> with <paramref name="op"/> get, create, resume, pause or drop; answers the goal after it (null: none).
    /// <paramref name="objective"/> and <paramref name="tokenBudget"/> only for create.
    /// </summary>
    public static async Task<(OmpGoal? Goal, RpcTimings Timings)> GoalAsync(this RpcConnection c, string op, string? objective = null,
        long? tokenBudget = null, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("goal", w =>
        {
            w.WriteString("op", op);
            if (objective is not null) w.WriteString("objective", objective);
            if (tokenBudget is { } budget) w.WriteNumber("token_budget", budget);
        }, ct: ct).ConfigureAwait(false));
        var goal = r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("goal", out var g) ? OmpGoal.Parse(g) : null;
        return (goal, r.Timings);
    }

    /// <summary>Asks a side question (a follow-up in topic <paramref name="recordId"/>); answers once it is saved as running.
    /// The answer streams as <c>btw_delta</c>, every change of the topic as <c>btw_record</c>.</summary>
    public static async Task<OmpBtwRecord?> BtwAsync(this RpcConnection c, string question, string? recordId = null, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("btw", w =>
        {
            w.WriteString("question", question);
            if (recordId is not null) w.WriteString("recordId", recordId);
        }, ct: ct).ConfigureAwait(false));
        return r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("record", out var rec) ? OmpBtwRecord.Parse(rec) : null;
    }

    /// <summary>Cancels the running side question (only topic <paramref name="recordId"/>, when given); false when none ran.</summary>
    public static async Task<bool> BtwCancelAsync(this RpcConnection c, string? recordId = null, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("btw_cancel", w => { if (recordId is not null) w.WriteString("recordId", recordId); }, ct: ct).ConfigureAwait(false));
        return r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("cancelled", out var p) && p.ValueKind == JsonValueKind.True;
    }

    /// <summary>The session's side-question topics, newest first; a running one carries its answer so far.</summary>
    public static async Task<(IReadOnlyList<OmpBtwRecord> Records, RpcTimings Timings)> GetBtwHistoryAsync(this RpcConnection c, CancellationToken ct = default)
    {
        var r = Ok(await c.RequestAsync("get_btw_history", ct: ct).ConfigureAwait(false));
        var list = new List<OmpBtwRecord>();
        if (r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
            foreach (var rec in records.EnumerateArray())
                if (OmpBtwRecord.Parse(rec) is { } parsed) list.Add(parsed);
        return (list, r.Timings);
    }

    private static RpcResponse Ok(RpcResponse r) => r.Success ? r : throw new RpcCommandException(r.Command, r.Error ?? "unknown error", r.Code, r.Data);
}
