using Ombra.Rpc;

namespace Ombra.ClientCore;

public sealed partial class SessionController
{
    // Guarded by _lock. A claimed FIFO head stays checkpointed through admission and cannot be selected twice.
    private QueuedMessage? _dispatchingDeferred;

    public bool ShouldQueueSubmission(string text)
    {
        lock (_lock) return _state.Phase == SessionPhase.Running || OmpBuiltins.RunsOverRpc(text, _state.Commands)
            || _dispatchingDeferred is not null || _state.Queued.Any(q => q.Deferred && !q.Recovered);
    }

    private IReadOnlyList<ImageAttachment>? RemoveDeferredInput(QueuedMessage message, bool recover)
    {
        IReadOnlyList<ImageAttachment>? images = null;
        Mutate(s =>
        {
            var pending = s.Queued.FirstOrDefault(q => q.Seq == message.Seq && q.Deferred && !q.Recovered);
            if (pending is null || _dispatchingDeferred?.Seq == pending.Seq) return;
            images = pending.Images;
            if (recover) s.RecoverQueued(pending.Seq);
            else s.RemoveQueued(pending.Seq);
        });
        return images;
    }

    private void RecoverDeferredInput(ConversationState state)
    {
        // Stop recovers the entire waiting FIFO. A later Ready snapshot cannot resurrect any of its input.
        for (var i = 0; i < state.Queued.Count; i++)
        {
            var q = state.Queued[i];
            if (q.Deferred && q.Seq != _dispatchingDeferred?.Seq)
                state.RecoverQueued(q.Seq);
        }
    }

    private void DispatchDeferredInput()
    {
        QueuedMessage? command;
        Task<bool> sent;
        lock (_lock)
        {
            if (_dispatchingDeferred is not null || _restoringQueue || _stopRequested || Volatile.Read(ref _disposed) != 0
                || _state.Phase != SessionPhase.Ready || _state.SigningIn is not null
                || _omp?.Connection is not { CloseReason: null } conn) return;
            // A plugin reload can replace the catalog while commands wait. Recover removed handlers without
            // reinterpreting their input as model text or blocking the next still-supported command.
            while (true)
            {
                command = _state.Queued.FirstOrDefault(q => !q.Recovered);
                // Earlier runtime follow-ups may still be awaiting admission or idle drain.
                if (command is null || !command.Deferred) return;
                if (command.Kind == QueueKind.DeferredFollowUp || OmpBuiltins.RunsOverRpc(command.Text, _state.Commands)) break;
                _state.RecoverQueued(command.Seq);
                _state.AddNotice(NoticeLevel.Warning, "The deferred command is no longer available. Recovered input was not sent.");
                _changes.Writer.TryWrite(true);
            }
            _dispatchingDeferred = command;
            if (command.Kind == QueueKind.BuiltinCommand)
            {
                // Start under the claim lock, before Stop can recover waiting input. Keep command_output in the
                // conversation exactly as for a direct idle prompt; only builtins use this dispatch path.
                sent = PromptAsync(command.Text, command.Images, submissionId: command.SubmissionId);
            }
            else
            {
                // Preserve follow_up expansion and ordinary slash-text semantics, not builtin prompt dispatch.
                // omp 18.8.0 schedules idle queue drain after followUp admission. Stay Running until that turn
                // ends so the next FIFO head cannot overtake it between acknowledgement and agent_start.
                _state.ReleaseDeferredFollowUp(command.Seq);
                _state.BeginRun(Now);
                sent = SendDeferredFollowUpAsync(conn, command);
            }
        }
        _ = CompleteDeferredInputAsync(command, sent);
    }

    private async Task CompleteDeferredInputAsync(QueuedMessage command, Task<bool> sent)
    {
        try
        {
            var accepted = await sent.ConfigureAwait(false);
            if (command.Kind == QueueKind.BuiltinCommand)
                Mutate(s =>
                {
                    if (accepted) s.RemoveQueued(command.Seq);
                    else s.RecoverQueued(command.Seq);
                });
            // Follow-ups remain in the runtime queue until their user-message echo confirms delivery.
        }
        catch (Exception e) when (e is OmpNotRunningException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Mutate(s =>
            {
                s.RecoverQueued(command.Seq);
                s.AddNotice(NoticeLevel.Error, "Deferred input delivery was not confirmed: " + e.Message);
            });
        }
        finally
        {
            lock (_lock) _dispatchingDeferred = null;
            DispatchDeferredInput();
            _changes.Writer.TryWrite(true);
        }
    }

    private async Task<bool> SendDeferredFollowUpAsync(RpcConnection conn, QueuedMessage input)
    {
        try
        {
            var disposition = SkillTokens.HasInvocation(input.Text)
                ? await conn.QueuePromptAsync(input.Text, Wire(input.Images), steer: false).ConfigureAwait(false)
                : await conn.FollowUpAsync(input.Text, Wire(input.Images)).ConfigureAwait(false);
            return ApplyQueuedInputDisposition(input, disposition, pendingRun: true);
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or ArgumentException)
        {
            Mutate(s =>
            {
                // A timeout can still be admitted by omp. Do not recover/dispatch it again while that is unknown;
                // Stop or connection exit settles its existing queue checkpoint, just like an ordinary follow-up.
                if (e is RpcCommandException or ArgumentException)
                {
                    s.RecoverQueued(input.Seq);
                    if (s.Phase == SessionPhase.Running) s.EndRun(interrupted: true, Now);
                }
                s.AddNotice(NoticeLevel.Error, $"Could not confirm the deferred follow-up ({e.Message}). Recovered input is not resent automatically.");
            });
            return false;
        }
    }

    private bool ApplyQueuedInputDisposition(QueuedMessage input, OmpInputAcknowledgement acknowledgement, bool pendingRun)
    {
        var disposition = acknowledgement.Disposition;
        if (disposition == OmpInputDisposition.Admitted) return true;
        Mutate(s =>
        {
            switch (disposition)
            {
                case OmpInputDisposition.Local:
                    s.RemoveQueued(input.Seq);
                    if (pendingRun && !acknowledgement.AgentInvoked) s.EndPendingRun(interrupted: false, Now);
                    break;
                case OmpInputDisposition.Cancelled:
                    s.RecoverQueued(input.Seq);
                    RecoverDeferredInput(s);
                    if (pendingRun && !acknowledgement.AgentInvoked) s.EndPendingRun(interrupted: true, Now);
                    break;
                default:
                    // A malformed ACK cannot leave the FIFO permanently blocked or release later input blindly.
                    s.RecoverQueue();
                    if (pendingRun) s.EndPendingRun(interrupted: true, Now);
                    s.AddNotice(NoticeLevel.Error, "The engine returned an invalid input acknowledgement. Delivery is uncertain; recovered input is not sent automatically.");
                    break;
            }
        });
        return disposition == OmpInputDisposition.Local;
    }
}
