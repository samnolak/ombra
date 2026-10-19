using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>How a fork ended.</summary>
public enum ForkOutcome
{
    /// <summary>The chat now shows the new session; the old one is kept as it was.</summary>
    Forked,
    /// <summary>An extension declined it.</summary>
    Cancelled,
    /// <summary>omp was not idle (a reply, a command or a background task could still write into the transcript).</summary>
    Busy,
    /// <summary>omp's session has no such message (on the current path) to fork from.</summary>
    NotFound,
    /// <summary>The request failed; the conversation shows why.</summary>
    Failed,
}

public sealed partial class SessionController
{
    /// <summary>
    /// omp's <c>fork</c>: a new session file the chat goes on in, the old one staying as it was. With
    /// <paramref name="itemKey"/> (a finished message row) the new session holds the conversation up to that message,
    /// inclusive; without, all of it (the session menu's Fork). omp refuses while it works (<c>session_busy</c>).
    /// </summary>
    public async Task<ForkOutcome> ForkAsync(long? itemKey = null, CancellationToken ct = default)
    {
        if (_omp?.Connection is null) return ForkOutcome.Failed;
        if (Snapshot().Phase is SessionPhase.Running or SessionPhase.Aborting) return ForkOutcome.Busy;
        var outcome = ForkOutcome.Failed;
        await SessionChangeAsync(async conn =>
        {
            string? entryId = null;
            if (itemKey is { } key)
            {
                if (ForkPoints.Target(Snapshot().Items, key) is not { } target)
                {
                    outcome = ForkOutcome.NotFound;
                    return;
                }
                var path = ForkPoints.ActivePath(await conn.GetEntriesAsync(ct).ConfigureAwait(false));
                entryId = ForkPoints.Find(path, target.Role, target.Text, target.At, target.OccurrenceFromEnd);
                if (entryId is null)
                {
                    outcome = ForkOutcome.NotFound;
                    return;
                }
            }
            try
            {
                if (!await conn.ForkAsync(entryId, ct).ConfigureAwait(false))
                {
                    outcome = ForkOutcome.Cancelled;
                    Mutate(s => s.AddNotice(NoticeLevel.Warning, "An extension cancelled the fork."));
                    return;
                }
            }
            catch (RpcCommandException e) when (e.Code == "session_busy")
            {
                outcome = ForkOutcome.Busy;
                Mutate(s => s.Log(Now, "fork", "refused: " + e.Error));
                return;
            }
            await LoadSessionAsync(conn, _request.WorkingDirectory, ct, newProcess: false).ConfigureAwait(false);
            outcome = ForkOutcome.Forked;
        }, ct).ConfigureAwait(false);
        return outcome;
    }
}
