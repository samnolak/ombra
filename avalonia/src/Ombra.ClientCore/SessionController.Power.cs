using Ombra.Rpc;

namespace Ombra.ClientCore;

public sealed partial class SessionController
{
    private readonly CancellationTokenSource _powerLifetime = new();
    private CancellationTokenSource? _wakeWatch;
    public TimeSpan WakeResponseTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan WakeQuietPeriod { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeProvider Clock => _clock;

    /// <summary>Only reads authoritative state. Never opens a session, resends input, aborts or restarts an engine.</summary>
    public async Task ReconcileAfterResumeAsync(CancellationToken ct = default)
    {
        RpcConnection conn;
        long epoch, transition, run;
        CancellationTokenSource watch;
        lock (_lock)
        {
            if (_request.ReadOnly || _disposed != 0 || _stopRequested || Connection is not { CloseReason: null } live
                || _state.Phase is not (SessionPhase.Ready or SessionPhase.Running or SessionPhase.Aborting)) return;
            conn = live;
            epoch = _state.TranscriptEpoch;
            transition = _historyTransitionKey;
            run = _state.RunGeneration;
            _wakeWatch?.Cancel();
            watch = CancellationTokenSource.CreateLinkedTokenSource(ct, _powerLifetime.Token);
            _wakeWatch = watch;
        }
        var monitoring = false;
        try
        {
            if (await RefreshAfterWakeAsync(conn, epoch, transition, run, watch.Token).ConfigureAwait(false) is { } activeRun)
            {
                monitoring = true;
                _ = WatchAfterWakeAsync(conn, epoch, transition, activeRun, watch);
            }
        }
        finally { if (!monitoring) FinishWakeWatch(watch); }
    }

    private bool WakeIsCurrent(RpcConnection conn, long epoch, long transition, long run, CancellationToken ct) =>
        !ct.IsCancellationRequested && _disposed == 0 && !_stopRequested && !_request.ReadOnly
        && ReferenceEquals(conn, Connection) && _state.TranscriptEpoch == epoch
        && _historyTransitionKey == transition && _state.RunGeneration == run
        && _state.Phase is SessionPhase.Ready or SessionPhase.Running or SessionPhase.Aborting;

    private async Task<long?> RefreshAfterWakeAsync(RpcConnection conn, long epoch, long transition, long run, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(WakeResponseTimeout, _clock);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            lock (_lock) if (!WakeIsCurrent(conn, epoch, transition, run, ct)) return null;
            var state = await conn.GetStateAsync(request.Token).ConfigureAwait(false);
            await DrainHistoryEventsAsync(conn, state.Timings!, request.Token).ConfigureAwait(false);
            long? monitor = null;
            lock (_lock)
            {
                var s = _state;
                if (!WakeIsCurrent(conn, epoch, transition, run, ct) || !s.AcceptStateSession(state)) return null;
                var readAt = state.Timings!.ResponseRead;
                if (s.HistoryModelReadAt <= readAt && state.ModelProvider is not null) s.Model = $"{state.ModelProvider}/{state.ModelId}";
                s.ApplyUsageLimit(state);
                s.ApplyPlanModeState(state.PlanMode, readAt);
                s.ApplyGoalState(state, readAt, Now);
                if (s.HistoryThinkingReadAt <= readAt) s.ThinkingLevel = state.ThinkingLevel;
                if (s.HistoryTitleReadAt <= readAt) s.SessionName = state.SessionName ?? s.SessionName;
                if (s.HistoryMutationReadAt <= readAt)
                {
                    s.Todos = ConversationState.ParseTodos(state.TodoPhases);
                    s.ContextPercent = state.ContextPercent;
                }
                if (s.HistoryPhaseReadAt <= readAt)
                {
                    // isStreaming/isCompacting win even if an inconsistent peer also reports isSettled.
                    if (!state.IsStreaming && !state.IsCompacting && state.LooksSettled && !s.RunStartPending)
                    {
                        RecoverDeferredInput(s); // Wake recovery is never permission to send the local waiting FIFO.
                        s.EndRun(interrupted: s.Phase == SessionPhase.Aborting, Now);
                    }
                    else if (state.IsStreaming || state.IsCompacting)
                        s.SetPhase(s.Phase == SessionPhase.Aborting ? SessionPhase.Aborting : SessionPhase.Running, Now);
                }
                s.Log(Now, "wake", "get_state reconciled after resume; no input replayed");
                if (s.Phase is SessionPhase.Running or SessionPhase.Aborting) monitor = s.RunGeneration;
            }
            _changes.Writer.TryWrite(true);
            return monitor;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or RpcCommandException
            or RpcConnectionClosedException or InvalidOperationException or System.Text.Json.JsonException)
        {
            lock (_lock)
            {
                var s = _state;
                if (!WakeIsCurrent(conn, epoch, transition, run, ct)) return null;
                var detail = timeout.IsCancellationRequested ? "omp did not answer within the wake recovery timeout" : error.Message;
                s.AddNotice(NoticeLevel.Error, "Could not reconcile this chat after wake: " + detail
                    + ". Work was not stopped and no input was resent. Check the engine connection; use Stop to cancel busy work, or Restart omp once chats are idle.");
                s.Log(Now, "wake_error", detail);
            }
            _changes.Writer.TryWrite(true);
            return null;
        }
    }

    private async Task WatchAfterWakeAsync(RpcConnection conn, long epoch, long transition, long run, CancellationTokenSource watch)
    {
        try
        {
            while (true)
            {
                await Task.Delay(WakeQuietPeriod, _clock, watch.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (!WakeIsCurrent(conn, epoch, transition, run, watch.Token)
                        || _state.Phase is not (SessionPhase.Running or SessionPhase.Aborting)) return;
                    if (_clock.GetElapsedTime(_lastFrameTimestamp) < WakeQuietPeriod) continue;
                }
                if (await RefreshAfterWakeAsync(conn, epoch, transition, run, watch.Token).ConfigureAwait(false) is not { } activeRun) return;
                run = activeRun;
            }
        }
        catch (OperationCanceledException) when (watch.IsCancellationRequested) { }
        finally { FinishWakeWatch(watch); }
    }

    private void FinishWakeWatch(CancellationTokenSource watch)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_wakeWatch, watch)) _wakeWatch = null;
            watch.Dispose();
        }
    }
}
