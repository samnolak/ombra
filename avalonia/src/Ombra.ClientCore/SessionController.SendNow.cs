using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// Send now (rpc-mode.ts): <c>promote_queued_message</c> moves a queued follow-up to omp's steering queue, and
/// <c>abort_and_prompt</c> stops the run and sends a new prompt in its place.
/// </summary>
public sealed partial class SessionController
{
    /// <summary>
    /// Sends a queued follow-up at omp's next step instead of after the run. Its chip stays (as a steer, in the recovery
    /// journal too) until omp's echo delivers it, so a crash before delivery still recovers it, and only once.
    /// False when omp no longer holds it (already delivered or withdrawn) or did not answer. An omp without the
    /// command throws <see cref="RpcCommandException"/>.
    /// </summary>
    public async Task<bool> SendQueuedNowAsync(QueuedMessage message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (message.Kind != QueueKind.FollowUp || message.Recovered) return false;
        var conn = RequireConnection();
        bool promoted;
        try { promoted = await conn.PromoteQueuedMessageAsync(message.Text, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException) { return false; }
        if (!promoted) return false;
        Mutate(s =>
        {
            // omp promotes the first follow-up with that text, not the chip's local sequence number. Its echo may already
            // have removed the chip: then nothing is left to mark.
            var target = s.Queued.FirstOrDefault(q => !q.Recovered && q.Kind == QueueKind.FollowUp && q.Text == message.Text);
            if (target is not null) s.PromoteQueued(target.Seq);
        });
        return true;
    }

    /// <summary>An omp builtin runs only as a new message; <c>abort_and_prompt</c> would hand its text to the model.</summary>
    public bool IsRuntimeCommand(string text)
    {
        lock (_lock) return OmpBuiltins.RunsOverRpc(text, _state.Commands);
    }

    /// <summary>
    /// Interrupt and send: stops the current run and sends this prompt in its place. Messages still queued stay queued
    /// in omp and follow the new prompt. Returns false when omp did not take the prompt; the caller restores the input.
    /// </summary>
    public async Task<bool> InterruptAndSendAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken ct = default, string? submissionId = null)
    {
        if (IsRuntimeCommand(text)) throw new ArgumentException("omp runs commands only as a new message; send it with Send or Queue instead.");
        var conn = RequireConnection();
        if (RefuseWhileSigningIn()) return false;
        List<PendingDialog> open = [];
        long ticket = 0, key = 0;
        Mutate(s =>
        {
            // A prompt still being admitted is superseded: omp may cancel it, and its acknowledgement then settles only
            // its own row (ApplyPromptAcknowledgement), never this replacement's run or delivery.
            ticket = ++_promptTicket;
            long? superseded = _promptAwaitingAck != 0 ? _promptAwaitingAck : null;
            open = s.CloseAllDialogs("the run was stopped");
            key = s.InterruptRunWithPrompt(text, images, submissionId, superseded, Now);
            _promptAwaitingAck = key;
        });
        try
        {
            // An unanswered tool approval would hold omp's abort (see AbortAsync): answer open dialogs first.
            foreach (var d in open) await conn.CancelExtensionUiAsync(d.Id, ct).ConfigureAwait(false);
            return ApplyPromptAcknowledgement(await conn.AbortAndPromptAsync(text, Wire(images), ct).ConfigureAwait(false), ticket, key, submissionId);
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or ArgumentException)
        {
            // Like a failed prompt (ConversationState ends the run on an unmatched abort_and_prompt failure too). A refused
            // prompt's row goes: the caller returns its input to the message box.
            Mutate(s =>
            {
                s.AddNotice(NoticeLevel.Error, "Interrupt and send failed: " + e.Message);
                if (e is RpcCommandException or ArgumentException) s.DropPrompt(key);
                else if (_promptTicket != ticket) s.SettlePrompt(key);
                if (_promptTicket == ticket)
                {
                    _promptAwaitingAck = 0;
                    s.EndRun(interrupted: true, Now);
                }
            });
            return false;
        }
    }
}
