namespace Ombra.ClientCore;

/// <summary>Send now: a queued follow-up promoted to steering, and a run interrupted by a new prompt.</summary>
public sealed partial class ConversationState
{
    /// <summary>
    /// omp moved this follow-up to its steering queue. It stays pending (and in the recovery journal) as a steer until
    /// omp delivers it, so a later withdrawal or Stop addresses the queue omp now holds it in.
    /// </summary>
    public void PromoteQueued(long sequence)
    {
        var index = _queued.FindIndex(q => q.Seq == sequence && q.Kind == QueueKind.FollowUp && !q.Recovered);
        if (index < 0) return;
        _queued[index] = _queued[index] with { Kind = QueueKind.Steer };
        QueueChanged?.Invoke();
        Touch();
    }

    /// <summary>The interrupt-and-send prompt whose run has not been seen yet (its row key; 0: none). Until its echo,
    /// the replaced run's late agent_start/agent_end (omp admits the replacement only after the abort settles, and image
    /// preparation can take a while) are the previous run's and must not show the session as idle.</summary>
    private long _replacementKey;
    /// <summary>An agent_start arrived while the replacement was pending (the replaced run's, or the replacement's own
    /// when omp starts the run before echoing it).</summary>
    private bool _heldAgentStart;

    private void ReleaseReplacement(bool runStarted)
    {
        _replacementKey = 0;
        _heldAgentStart = false;
        if (runStarted) _runStartPending = false;
    }

    /// <summary>
    /// <c>abort_and_prompt</c> was sent: the current run ends as stopped and the prompt's run takes its place.
    /// <paramref name="supersededKey"/> is an earlier prompt omp was still admitting: omp may cancel it, so its row
    /// waits for its own acknowledgement and an identical replacement's echo never confirms it. Other prompts still
    /// awaiting their echo keep waiting for it. Returns the replacement's row key.
    /// </summary>
    public long InterruptRunWithPrompt(string text, IReadOnlyList<ImageAttachment> images, string? submissionId, long? supersededKey, DateTimeOffset now)
    {
        if (supersededKey is { } superseded && _rows.Exists(r => r.Key == superseded && r.Item is UserItem { Confirmed: false }))
            _supersededPrompts.Add(superseded);
        if (Phase == SessionPhase.Running) SetPhase(SessionPhase.Aborting, now);
        EndRun(interrupted: true, now, confirmPrompts: false);
        var key = AddUserPrompt(text, images, submissionId);
        BeginRun(now);
        _replacementKey = key;
        return key;
    }

    /// <summary>A superseded prompt was admitted after all: it waits for its echo like any other prompt.</summary>
    public void ReleaseSupersededPrompt(long key) => _supersededPrompts.Remove(key);

    /// <summary>A prompt omp did not take: its row goes (the input returns to the message box). A row omp already echoed
    /// stays.</summary>
    public void DropPrompt(long key)
    {
        _supersededPrompts.Remove(key);
        if (key == _replacementKey) ReleaseReplacement(runStarted: false);
        var index = _rows.FindIndex(r => r.Key == key && r.Item is UserItem { Confirmed: false });
        if (index < 0) return;
        _rows.RemoveAt(index);
        _promptSubmissions.Remove(key);
        TranscriptRevision++;
        Touch();
    }

    /// <summary>A superseded prompt that will not be echoed (consumed locally, or its delivery is unknown): it stops
    /// waiting for an echo.</summary>
    public void SettlePrompt(long key)
    {
        _supersededPrompts.Remove(key);
        var row = _rows.Find(r => r.Key == key);
        if (row?.Item is not UserItem { Confirmed: false } u) return;
        Set(row, u with { Confirmed = true });
        _promptSubmissions.Remove(key);
    }
}
