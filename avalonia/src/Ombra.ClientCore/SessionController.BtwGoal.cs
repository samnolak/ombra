using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// Goal mode and side questions over Ombra's engine (rpc-goal.ts, rpc-btw.ts). Goal commands go only to an omp that
/// reported <c>goal</c> in <c>get_state</c>: one without them would answer "Unknown command" without our id. Each
/// answer is applied at once (ConversationState.BtwGoal.cs); frames read later win over it. Results: null on success,
/// else why not.
/// </summary>
public sealed partial class SessionController
{
    /// <summary>Starts goal mode with <paramref name="objective"/> (omp refuses while another goal is active or paused).</summary>
    public Task<string?> CreateGoalAsync(string objective, long? tokenBudget = null, CancellationToken ct = default) =>
        GoalAsync(ct, ("create", objective.Trim(), tokenBudget));

    public Task<string?> PauseGoalAsync(CancellationToken ct = default) => GoalAsync(ct, ("pause", null, null));

    public Task<string?> ResumeGoalAsync(CancellationToken ct = default) => GoalAsync(ct, ("resume", null, null));

    /// <summary>Ends the goal (omp's <c>drop</c>); a completed goal omp has already let go only leaves the bar.</summary>
    public Task<string?> ClearGoalAsync(CancellationToken ct = default)
    {
        if (Snapshot().Goal is { IsComplete: true })
        {
            Mutate(s => s.DismissCompletedGoal());
            return Task.FromResult<string?>(null);
        }
        return GoalAsync(ct, ("drop", null, null));
    }

    /// <summary>
    /// A new objective for the goal: omp has no op to change one, so the goal is dropped and a new one created with
    /// the same token budget; a paused goal stays paused.
    /// </summary>
    public Task<string?> EditGoalAsync(string objective, CancellationToken ct = default)
    {
        if (Snapshot().Goal is not { } goal) return CreateGoalAsync(objective, null, ct);
        return goal.IsPaused
            ? GoalAsync(ct, ("drop", null, null), ("create", objective.Trim(), goal.TokenBudget), ("pause", null, null))
            : GoalAsync(ct, ("drop", null, null), ("create", objective.Trim(), goal.TokenBudget));
    }

    private async Task<string?> GoalAsync(CancellationToken ct, params (string Op, string? Objective, long? Budget)[] steps)
    {
        if (_omp?.Connection is not { } conn) return "omp is not running";
        var snapshot = Snapshot();
        if (snapshot.IsReadOnly) return "this chat is read-only";
        if (snapshot.GoalSupported != true) return "this omp has no goal mode";
        if (snapshot.SigningIn is { } provider) return $"omp is busy with the sign-in to {provider}";
        try
        {
            foreach (var (op, objective, budget) in steps)
            {
                var (goal, timings) = await conn.GoalAsync(op, objective, budget, ct).ConfigureAwait(false);
                Mutate(s => { if (s.TranscriptEpoch == snapshot.TranscriptEpoch) s.ApplyGoalAnswer(goal, timings.ResponseRead, Now); });
            }
            return null;
        }
        catch (RpcCommandException e) { return e.Error; }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException) { return e.Message; }
    }

    /// <summary>Asks a side question (a follow-up in topic <paramref name="recordId"/>), also while omp works.</summary>
    public Task<string?> AskBtwAsync(string question, string? recordId = null, CancellationToken ct = default) =>
        BtwAsync(async (conn, epoch) =>
        {
            var record = await conn.BtwAsync(question.Trim(), recordId, ct).ConfigureAwait(false);
            // omp wrote the btw_record frame before this answer and the deltas after it: only a topic still missing is added
            if (record is not null) Mutate(s => { if (s.TranscriptEpoch == epoch) s.ApplyBtwRecord(record, 0); });
        });

    /// <summary>Stops the side question that is being answered.</summary>
    public Task<string?> CancelBtwAsync(string? recordId = null, CancellationToken ct = default) =>
        BtwAsync((conn, _) => conn.BtwCancelAsync(recordId, ct));

    /// <summary>Reads the session's side-question topics (omp keeps them beside the session file).</summary>
    public Task<string?> LoadBtwHistoryAsync(CancellationToken ct = default) =>
        BtwAsync(async (conn, epoch) =>
        {
            var (records, timings) = await conn.GetBtwHistoryAsync(ct).ConfigureAwait(false);
            Mutate(s => { if (s.TranscriptEpoch == epoch) s.ApplyBtwHistory(records, timings.ResponseRead); });
        });

    /// <summary>Side questions come with goal mode in Ombra's engine: its <c>get_state.goal</c> tells that omp has them.</summary>
    private async Task<string?> BtwAsync(Func<RpcConnection, long, Task> action)
    {
        if (_omp?.Connection is not { } conn) return "omp is not running";
        var snapshot = Snapshot();
        if (snapshot.IsReadOnly) return "this chat is read-only";
        if (snapshot.GoalSupported != true) return "this omp has no side questions";
        if (snapshot.SigningIn is { } provider) return $"omp is busy with the sign-in to {provider}";
        try
        {
            await action(conn, snapshot.TranscriptEpoch).ConfigureAwait(false);
            return null;
        }
        catch (RpcCommandException e) { return e.Error; }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException) { return e.Message; }
    }
}
