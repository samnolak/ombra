namespace Ombra.ClientCore;

/// <summary>How a chat restarts by itself after omp stopped unexpectedly: exponential backoff from
/// <see cref="FirstDelay"/> up to <see cref="MaxDelay"/>, at most <see cref="MaxAttempts"/> attempts in a row. A session
/// that stayed up for <see cref="StablePeriod"/> starts counting again at its next unexpected exit.</summary>
public sealed record AutoRecoverPolicy(TimeSpan FirstDelay, TimeSpan MaxDelay, int MaxAttempts, TimeSpan StablePeriod)
{
    /// <summary>0.5, 1, 2, 4, 8, 16 and 30 s (seven attempts); two minutes up resets the count.</summary>
    public static AutoRecoverPolicy Default { get; } =
        new(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(30), 7, TimeSpan.FromMinutes(2));

    /// <summary>The wait before attempt <paramref name="attempt"/> (1-based).</summary>
    public TimeSpan DelayBefore(int attempt)
    {
        var delay = FirstDelay;
        for (var i = 1; i < attempt && delay < MaxDelay; i++) delay += delay;
        return delay < MaxDelay ? delay : MaxDelay;
    }
}

/// <summary>An automatic restart waiting to run: attempt <see cref="Attempt"/> of <see cref="MaxAttempts"/>, due when
/// <see cref="Delay"/> has elapsed since it was scheduled (measured on the controller's monotonic clock).</summary>
public sealed record AutoRecoverStatus(int Attempt, int MaxAttempts, TimeSpan Delay, MonotonicTime Waited)
{
    public TimeSpan Remaining
    {
        get
        {
            var left = Delay - Waited.Elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }
}

/// <summary>
/// Automatic recovery after an unexpected engine or session exit. The same session file reopens after a backoff; a run
/// that was in flight is never resent and recovered input stays with the user (it is never sent automatically). An
/// explicit stop, restart, close or settings restart cancels it. Chats of one shared engine that crashed together
/// restart one process between them: startup admission serializes their attempts, the first starts the engine and the
/// others open their sessions in it, or adopt its failure and back off with it.
/// </summary>
public sealed partial class SessionController
{
    private readonly object _autoRecoverGate = new();
    // Guarded by _autoRecoverGate (read without it, under _lock, to publish status only while still current).
    private CancellationTokenSource? _autoRecover;
    private int _autoRecoverAttempts;
    private int _autoRecoverEpoch;
    private long? _liveSince;
    // Set by StartCoreAsync: its last start failed because the shared engine process did not launch or become ready.
    private bool _engineStartFailed;

    /// <summary>Automatic restart after an unexpected exit; null keeps recovery manual. Siblings inherit it.</summary>
    public AutoRecoverPolicy? AutoRecover { get; init; }

    /// <summary>
    /// Cancels a pending or running automatic restart (the user's Cancel, or any explicit action of theirs). The chat
    /// keeps manual recovery; the next unexpected exit counts its attempts from the start.
    /// </summary>
    public void CancelAutoRecover()
    {
        CancellationTokenSource? cancelled;
        lock (_autoRecoverGate)
        {
            _autoRecoverEpoch++;
            _autoRecoverAttempts = 0;
            cancelled = _autoRecover;
            _autoRecover = null;
        }
        if (cancelled is null) return;
        cancelled.Cancel();
        Mutate(s =>
        {
            s.AutoRecover = null;
            s.Log(Now, "recover", "automatic restart cancelled");
        });
    }

    private int AutoRecoverEpoch => Volatile.Read(ref _autoRecoverEpoch);

    private void NoteLive()
    {
        lock (_autoRecoverGate) _liveSince = _clock.GetTimestamp();
    }

    /// <param name="epoch">Read with the crash decision: an explicit action since then wins over the restart.</param>
    private void OnUnexpectedExit(int epoch)
    {
        if (AutoRecover is not { } policy || _request.ReadOnly) return;
        CancellationTokenSource recovery;
        lock (_autoRecoverGate)
        {
            // A running recovery observes this exit itself (its attempt stopped again).
            if (_autoRecover is not null || epoch != _autoRecoverEpoch || Volatile.Read(ref _disposed) != 0) return;
            if (_liveSince is { } since && _clock.GetElapsedTime(since) >= policy.StablePeriod) _autoRecoverAttempts = 0;
            recovery = _autoRecover = new CancellationTokenSource();
        }
        _ = Task.Run(() => AutoRecoverLoopAsync(policy, recovery));
    }

    private bool IsCurrentRecovery(CancellationTokenSource recovery) => ReferenceEquals(Volatile.Read(ref _autoRecover), recovery);

    private async Task AutoRecoverLoopAsync(AutoRecoverPolicy policy, CancellationTokenSource recovery)
    {
        var token = recovery.Token;
        try
        {
            while (true)
            {
                int attempt;
                lock (_autoRecoverGate)
                {
                    if (!ReferenceEquals(_autoRecover, recovery)) return;
                    attempt = _autoRecoverAttempts < policy.MaxAttempts ? ++_autoRecoverAttempts : 0;
                    if (attempt == 0) _autoRecover = null;
                }
                if (attempt == 0)
                {
                    Mutate(s =>
                    {
                        s.AutoRecover = null;
                        s.AddNotice(NoticeLevel.Error, $"omp stopped unexpectedly {policy.MaxAttempts} times in a row, so it is no longer restarted automatically. Restart it when ready: this conversation and any unsent input are kept.");
                    });
                    return;
                }
                var delay = policy.DelayBefore(attempt);
                var scheduledAt = _clock.GetTimestamp();
                // The timer exists before the status is published: whoever sees the countdown can move the clock.
                var wait = Task.Delay(delay, _clock, token);
                var status = new AutoRecoverStatus(attempt, policy.MaxAttempts, delay, MonotonicTime.Start(_clock));
                Mutate(s =>
                {
                    if (!IsCurrentRecovery(recovery)) return;
                    s.AutoRecover = status;
                    s.Log(Now, "recover", $"automatic restart {attempt}/{policy.MaxAttempts} in {delay.TotalSeconds:0.#}s");
                });
                await wait.ConfigureAwait(false);
                await AutoRecoverAttemptAsync(scheduledAt, recovery, token).ConfigureAwait(false);
                bool finished;
                lock (_autoRecoverGate)
                {
                    if (!ReferenceEquals(_autoRecover, recovery)) return;
                    finished = IsLive() || NeedsUserToStart();
                    if (finished) _autoRecover = null;
                }
                if (finished) return;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            // Cancelled by an explicit action, or the chat or its window closed while waiting.
        }
        finally
        {
            // Not disposed: a concurrent CancelAutoRecover may still cancel this source after the loop has ended.
            lock (_autoRecoverGate) if (ReferenceEquals(_autoRecover, recovery)) _autoRecover = null;
        }
    }

    private async Task AutoRecoverAttemptAsync(long scheduledAt, CancellationTokenSource recovery, CancellationToken token)
    {
        var host = _host;
        if (host is not null) await host.EnterSessionStartAsync(token).ConfigureAwait(false);
        try
        {
            Mutate(s => { if (IsCurrentRecovery(recovery)) s.AutoRecover = null; });
            if (host?.EngineRecoveryFailedSince(scheduledAt) == true)
            {
                Mutate(s => s.Log(Now, "recover", "the shared engine did not come back for another chat; backing off with it"));
                return;
            }
            Mutate(s => s.Log(Now, "recover", "restarting omp automatically on the same session"));
            _engineStartFailed = false;
            await RestartCoreAsync(_request with { ResumeSessionFile = ExistingSessionFile() }, token, automatic: true).ConfigureAwait(false);
            // Only the physical engine start is shared with the other chats; this chat's own folder, session file or
            // logical open failing says nothing about whether theirs can start.
            if (host is not null && _engineStartFailed) host.NoteEngineRecoveryFailed(_clock.GetTimestamp());
        }
        finally { host?.ExitSessionStart(); }
    }

    /// <summary>The logical session is open and omp reports it usable.</summary>
    private bool IsLive()
    {
        if (_omp?.Connection is not { CloseReason: null }) return false;
        lock (_lock) return _state.Phase is SessionPhase.Ready or SessionPhase.Running or SessionPhase.Aborting;
    }

    /// <summary>A start failure that another attempt cannot fix (setup, provider or saved-model choice).</summary>
    private bool NeedsUserToStart() => Snapshot() is { StartFailed: true } failed
        && failed.StartProblem is StartProblem.NotFound or StartProblem.NoModel or StartProblem.SessionModelUnavailable;
}
