using System.Globalization;
using System.Text;
using System.Text.Json;
using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// Folds omp RPC frames into a transcript. Pure state: no I/O, no threads, no clock except the one passed in.
/// Not thread-safe; <see cref="SessionController"/> serializes access.
/// Streaming text is kept in builders and materialized only for rows that changed since the last snapshot.
/// </summary>
public sealed partial class ConversationState
{
    private const int DebugCapacity = 500;
    private const int MaxToolOutputChars = 4000;
    private const int MaxArgsChars = 400;
    private readonly TimeProvider _clock;
    private MonotonicTime _runTime;
    internal long RunGeneration { get; private set; }
    internal bool RunStartPending => _runStartPending;

    public ConversationState(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    private readonly List<Row> _rows = [];
    private readonly Dictionary<string, Row> _toolsByCallId = [];
    private readonly HashSet<string> _resumedToolCallIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _liveIrcMessages = new(StringComparer.Ordinal);
    private readonly Queue<DebugEntry> _debug = new();
    private Row? _streamingAssistant;
    private Row? _retryNotice;
    /// <summary>
    /// A reply the client ended before omp's <c>message_end</c> for it was applied: the answer to <c>abort</c> (or a
    /// settle) reaches the controller directly, while the events omp wrote before it still wait in the event channel.
    /// That late <c>message_end</c> settles this row instead of adding a second copy of the reply.
    /// </summary>
    private Row? _endedBeforeMessageEnd;
    private long _nextKey;
    private long _debugSeq;

    public long Version { get; private set; }
    public SessionPhase Phase { get; private set; } = SessionPhase.NotStarted;
    private string? _model;
    public string? Model
    {
        get => _model;
        set
        {
            if (_model == value) return;
            _model = value;
            UsageLimit = null;
            Touch();
        }
    }
    public int ProtocolVersion { get; set; } = 1;
    private string? _sessionId;
    public string? SessionId
    {
        get => _sessionId;
        set
        {
            if (_sessionId == value) return;
            _sessionId = value;
            _freshProviderSessionAt = 0;
            UsageLimit = null;
        }
    }
    /// <summary>Path of omp's current session file (<c>get_state.sessionFile</c>).</summary>
    public string? SessionFile { get; set; }
    public string? SessionName { get; set; }
    public string? SessionTitleHint { get; set; }
    /// <summary>omp's working directory (the session header's <c>cwd</c>).</summary>
    public string? Cwd { get; set; }
    private string? _thinkingLevel;
    public string? ThinkingLevel
    {
        get => _thinkingLevel;
        set
        {
            if (_thinkingLevel == value) return;
            _thinkingLevel = value;
            Touch();
        }
    }
    /// <summary>Changes whenever the transcript is replaced (other session, new session, omp restarted). Unique across
    /// every conversation of the app, not only this one: the window shows several omp processes' conversations in turn,
    /// and a number two of them shared would make one look like the other.</summary>
    public long TranscriptEpoch { get; private set; }

    private static long s_epochs;

    public long FramesReceived { get; private set; }
    public long BytesReceived { get; private set; }
    public DateTimeOffset? LastFrameAt { get; private set; }
    public DateTimeOffset? RunStartedAt { get; private set; }
    public string? LastError { get; private set; }
    /// <summary><see cref="LastError"/>'s first line as the client wrote it (format and values), when the client wrote it.</summary>
    public ClientText? LastErrorText { get; private set; }
    /// <summary>Assistant messages whose streamed deltas did not add up to omp's final text (data-loss detector).</summary>
    public int StreamMismatches { get; private set; }
    public int MessagesCompleted { get; private set; }
    /// <summary>
    /// Runs that ended (running or stopping → Ready or Faulted). A count, not a phase change, so a reader that samples
    /// snapshots (the UI, ~30 per second, none while minimized) still sees a run that started and ended in between.
    /// </summary>
    public long RunsEnded { get; private set; }
    /// <summary>
    /// The last agent_end said more work was scheduled (isTerminal: false). omp 18.2.0 can then go idle
    /// without ever sending a terminal agent_end (UPSTREAM OMP ISSUE, e.g. after a paused compaction), so
    /// the controller probes get_state once the stream is quiet. Cleared by any new agent activity.
    /// </summary>
    public bool AwaitingSettle { get; private set; }

    /// <summary>
    /// omp times dialogs out on its own clock, which started before the request reached us. Close them this much
    /// earlier so a click in the last moment is never shown as the user's answer while omp already applied its default.
    /// </summary>
    public static readonly TimeSpan DialogDeadlineMargin = TimeSpan.FromSeconds(1);

    private readonly List<PendingDialog> _dialogs = [];
    private readonly Dictionary<string, string> _extensionStatus = [];
    private readonly List<ExtensionWidget> _widgets = [];
    private EditorTextRequest? _editorText;
    private OpenUrlRequest? _openUrl;
    private readonly List<McpAuthRequest> _mcpAuth = [];
    private long _requestSeq;

    /// <summary>Dialogs that arrived while stopping; the controller answers them "cancelled".</summary>
    public List<string> DialogsToCancel { get; } = [];

    /// <summary>Something changed that only get_state reports (todos, context usage, model): the controller refreshes.</summary>
    public bool StateRefreshWanted { get; set; }

    public IReadOnlyList<SlashCommand> Commands { get; set; } = [];
    public IReadOnlyList<TodoPhase> Todos { get; set; } = [];
    public double? ContextPercent { get; set; }
    private OmpUsageLimit? _usageLimit;
    public OmpUsageLimit? UsageLimit
    {
        get => _usageLimit;
        private set
        {
            if (_usageLimit == value) return;
            _usageLimit = value;
            Touch();
        }
    }

    private long _usageInvalidatedAt;
    private long _freshProviderSessionAt;

    internal void InvalidateAccountUsage(long readAt)
    {
        _usageInvalidatedAt = Math.Max(_usageInvalidatedAt, readAt);
        UsageLimit = null;
    }

    /// <summary>omp's /fresh completion rotates its provider id, not the local transcript file.</summary>
    internal bool NoteFreshProviderSession(string output, long readAt)
    {
        if (!output.StartsWith("Fresh provider session started (", StringComparison.Ordinal)) return false;
        _freshProviderSessionAt = readAt;
        InvalidateAccountUsage(readAt);
        return true;
    }

    internal bool AcceptStateSession(OmpSessionState state)
    {
        if (state.SessionId == SessionId) return true;
        // Only a confirmed /fresh may adopt a new id, from a request begun after its completion.
        // File identity and the controller's connection/epoch/transition/run guards still apply.
        if (_freshProviderSessionAt == 0 || state.Timings is not { } timings
            || timings.Sent < _freshProviderSessionAt || string.IsNullOrEmpty(state.SessionId)
            || SessionFile is null || state.SessionFile != SessionFile) return false;
        SessionId = state.SessionId;
        _freshProviderSessionAt = 0;
        Touch();
        return true;
    }

    internal void ApplyUsageLimit(OmpSessionState state)
    {
        if (HistoryModelReadAt > (state.Timings?.ResponseRead ?? 0)
            || _usageInvalidatedAt > (state.Timings?.Sent ?? 0)) return;
        UsageLimit = state.SessionId == SessionId && state.ModelProvider is not null
            && Model == $"{state.ModelProvider}/{state.ModelId}" ? state.UsageLimit : null;
    }

    internal static IReadOnlyList<SlashCommand> ParseCommands(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return [];
        var result = new List<SlashCommand>();
        foreach (var c in list.EnumerateArray())
        {
            if (Str(c, "name") is not { Length: > 0 } name) continue;
            var aliases = c.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Array
                ? al.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToList() : [];
            var subs = c.TryGetProperty("subcommands", out var sc) && sc.ValueKind == JsonValueKind.Array
                ? sc.EnumerateArray().Where(x => Str(x, "name") is not null)
                    .Select(x => new SlashCommand(Str(x, "name")!, Str(x, "description"), Str(x, "usage"), "sub", [], [])).ToList() : [];
            var hint = c.TryGetProperty("input", out var inp) ? Str(inp, "hint") : null;
            result.Add(new SlashCommand(name, Str(c, "description"), hint, Str(c, "source") ?? "", aliases, subs));
        }
        return result;
    }

    /// <summary>get_state's (or set_todos') todo phases, each task's details and notes included.</summary>
    public static IReadOnlyList<TodoPhase> ParseTodos(JsonElement phases)
    {
        if (phases.ValueKind != JsonValueKind.Array) return [];
        // Entries that are not objects are skipped (TryGetProperty throws on them).
        return [.. phases.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(p => new TodoPhase(Str(p, "name") ?? "",
            p.TryGetProperty("tasks", out var t) && t.ValueKind == JsonValueKind.Array
                ? [.. t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => new TodoTask(Str(x, "content") ?? "", Str(x, "status") ?? "pending", Str(x, "blocker"),
                    Str(x, "details"),
                    x.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.Array
                        ? [.. n.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                        : null))]
                : []))];
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    /// <summary>Dialogs with a deadline added since the controller last looked (it schedules their expiry).</summary>
    public List<PendingDialog> NewDeadlines { get; } = [];

    /// <summary>Approval requests added since the controller last looked (it answers those a "don't ask again" rule covers).</summary>
    public List<PendingDialog> NewApprovals { get; } = [];

    public IReadOnlyList<PendingDialog> Dialogs => _dialogs;

    private sealed class Row(long key, TranscriptItem item)
    {
        public readonly long Key = key;
        public TranscriptItem Item = item;
        public StringBuilder? Text;
        public StringBuilder? Thinking;
        public int ThinkingBlockStart;
        public MonotonicTime Elapsed;
        public bool Dirty;
    }

    public void SetPhase(SessionPhase phase, DateTimeOffset now)
    {
        if (phase == Phase) return;
        // Once we are shutting down, late run events (omp still sends agent_end while it drains) must not
        // bring the session back to Ready/Running; only the end of the process decides the final phase.
        if (Phase == SessionPhase.Stopping && phase is not (SessionPhase.Stopped or SessionPhase.Faulted)) return;
        if (phase == SessionPhase.Running && Phase != SessionPhase.Aborting && RunStartedAt is null)
        {
            RunStartedAt = now;
            _runTime = MonotonicTime.Start(_clock);
            RunGeneration++;
        }
        // Pending local commands are not agent turns: they must not create "Worked" rows,
        // increment usage/reset counters, or trigger completion notifications.
        var hadAgentRun = !_runStartPending && Phase is SessionPhase.Running or SessionPhase.Aborting;
        if (hadAgentRun && phase == SessionPhase.Ready && RunStartedAt is not null)
            AddTurnEnd(_runTime.Elapsed, interrupted: Phase == SessionPhase.Aborting);
        if (hadAgentRun && phase is SessionPhase.Ready or SessionPhase.Faulted) RunsEnded++;
        if (phase is SessionPhase.Ready or SessionPhase.Stopped or SessionPhase.Faulted)
        {
            RunStartedAt = null;
            _runTime = default;
            _runStartPending = false;
            _replacementKey = 0;
            _heldAgentStart = false;
        }
        Phase = phase;
        if (phase is SessionPhase.Stopped or SessionPhase.Faulted) RecoverQueue();
        if (phase is SessionPhase.Stopped or SessionPhase.Faulted && History.IsLoading)
            History = History with { IsLoading = false };
        Touch();
    }

    /// <summary>
    /// Drops the transcript of the old session; the debug log stays. <paramref name="newProcess"/>: omp itself was
    /// replaced, so its dialogs, extension status and widgets are gone too. Otherwise (session switched inside the same
    /// omp) they belong to omp and its extensions, not to the session, and stay.
    /// </summary>
    public void ResetTranscript(bool newProcess = true)
    {
        TranscriptRevision++;
        UsageLimit = null;
        _freshProviderSessionAt = 0;
        _rows.Clear();
        _retryNotice = null;
        _toolsByCallId.Clear();
        _resumedToolCallIds.Clear();
        _liveIrcMessages.Clear();
        _newFileCalls.Clear();
        _historyOrphanTools.Clear();
        _promptSubmissions.Clear();
        _supersededPrompts.Clear();
        _replacementKey = 0;
        _heldAgentStart = false;
        History = new();
        _streamingAssistant = null;
        _endedBeforeMessageEnd = null;
        _messageRows.Clear();
        RecoverQueue();
        ResetEngineQueue(newProcess); // ConversationState.QueueUpdate.cs
        ResetBtwGoal(newProcess); // ConversationState.BtwGoal.cs
        if (newProcess)
        {
            HistoryMutationReadAt = HistoryPhaseReadAt = HistoryModelReadAt = HistoryThinkingReadAt = HistoryTitleReadAt = 0;
            _usageInvalidatedAt = 0;
            ResetPlanMode();
            _dialogs.Clear();
            NewDeadlines.Clear();
            NewApprovals.Clear();
            DialogsToCancel.Clear();
            _extensionStatus.Clear();
            _widgets.Clear();
            _mcpAuth.Clear();
            SigningIn = null;
        }
        AwaitingSettle = false;
        LastError = null;
        LastErrorText = null;
        TranscriptEpoch = Interlocked.Increment(ref s_epochs);
        Touch();
    }

    public string? SigningIn { get; set; }

    /// <summary>The last failure happened while omp was starting (not found, bad settings, crashed before ready).</summary>
    public bool StartFailed { get; private set; }
    public StartProblem StartProblem { get; private set; }
    public string? UnavailableSessionModel { get; private set; }
    private AutoRecoverStatus? _autoRecover;
    /// <summary>The automatic restart after an unexpected exit, while it waits (shown only while Faulted).</summary>
    public AutoRecoverStatus? AutoRecover
    {
        get => _autoRecover;
        set
        {
            if (ReferenceEquals(_autoRecover, value)) return;
            _autoRecover = value;
            Touch();
        }
    }

    public void Fail(string error, DateTimeOffset now, StartProblem problem = StartProblem.Other, string? unavailableSessionModel = null, ClientText? text = null)
    {
        StartFailed = Phase == SessionPhase.Starting;
        StartProblem = StartFailed ? problem : StartProblem.None;
        UnavailableSessionModel = StartFailed ? unavailableSessionModel : null;
        AwaitingSettle = false;
        ConfirmPendingPrompts(includeSuperseded: true);
        LastError = error;
        LastErrorText = text;
        // A failed start is explained once, by the client's setup screen (with the error under "Show details"); a
        // crash mid-conversation also marks where in the conversation it happened.
        if (!StartFailed) AddNotice(NoticeLevel.Error, error, text);
        FinalizeOpenRows(interrupted: true);
        SetPhase(SessionPhase.Faulted, now);
    }

    /// <summary>
    /// Ends a run (Running, Aborting, or cut short by Stopping) and finalizes its rows; no-op otherwise.
    /// While Stopping the phase stays Stopping; the end of the process decides the final phase.
    /// </summary>
    public void EndRun(bool interrupted, DateTimeOffset now) => EndRun(interrupted, now, confirmPrompts: true);

    /// <param name="confirmPrompts">False when prompts still awaiting omp's echo belong to input that is not settled yet
    /// (an interrupt sent while an earlier prompt was being admitted): their echo or acknowledgement settles them.</param>
    private void EndRun(bool interrupted, DateTimeOffset now, bool confirmPrompts)
    {
        AwaitingSettle = false;
        if (Phase is not (SessionPhase.Running or SessionPhase.Aborting or SessionPhase.Stopping)) return;
        if (confirmPrompts) ConfirmPendingPrompts();
        FinalizeOpenRows(interrupted || Phase is SessionPhase.Aborting or SessionPhase.Stopping);
        SetPhase(SessionPhase.Ready, now);
    }

    /// <summary>A prompt that omp will never echo (failed, or its run is over) stops waiting for confirmation,
    /// so the next prompt's echo cannot be matched to it. Its delivery stays unconfirmed (the journal keeps it).
    /// A superseded prompt keeps waiting for its own acknowledgement unless omp is gone.</summary>
    private void ConfirmPendingPrompts(bool includeSuperseded = false)
    {
        foreach (var r in _rows)
            if (r.Item is UserItem { Confirmed: false } u && (includeSuperseded || !_supersededPrompts.Contains(r.Key)))
            {
                Set(r, u with { Confirmed = true });
                _promptSubmissions.Remove(r.Key);
                _supersededPrompts.Remove(r.Key);
            }
    }

    /// <summary>Prompt rows awaiting omp's echo, by row key: the durable submission each one confirms.</summary>
    private readonly Dictionary<long, string> _promptSubmissions = [];

    /// <summary>Prompt rows an interrupt-and-send replaced while omp was still admitting them (by row key): omp may have
    /// cancelled them, so an echo matches them only after every row it could belong to that is still current.</summary>
    private readonly HashSet<long> _supersededPrompts = [];

    /// <summary>An optimistic prompt row; its echo raises <see cref="PromptConfirmed"/> with <paramref name="submissionId"/>.
    /// Returns the row's key, which addresses this prompt alone when its acknowledgement settles it.</summary>
    public long AddUserPrompt(string text, IReadOnlyList<ImageAttachment>? images = null, string? submissionId = null)
    {
        var row = Add(new UserItem(0, text, Confirmed: false) { Images = images is { Count: > 0 } ? images : [], At = DateTimeOffset.UtcNow });
        if (submissionId is not null) _promptSubmissions[row.Key] = submissionId;
        return row.Key;
    }

    /// <summary>The prompt's acknowledgement settled it as a local command: a later echo confirms nothing, and when it
    /// replaced a run, that guard ends (any turn the command started is its own).</summary>
    public void PromptHandledLocally(long key)
    {
        _promptSubmissions.Remove(key);
        if (key == _replacementKey) ReleaseReplacement(runStarted: _heldAgentStart);
    }

    /// <summary>
    /// A prompt was sent and its run has not started yet: the end of the previous run (prompt_result,
    /// session_settled, agent_end) can still be on its way and must not end this one. omp 18.8.0 writes those three
    /// right after agent_end, and a prompt sent in that moment was shown as idle while omp worked on it.
    /// </summary>
    private bool _runStartPending;

    /// <summary>The client started a run (a prompt, or a command omp hands to the agent): Running until it ends.</summary>
    public void BeginRun(DateTimeOffset now)
    {
        _runStartPending = true;
        RunGeneration++;
        SetPhase(SessionPhase.Running, now);
    }

    /// <summary>Acknowledged local/cancelled input starts no turn; settle only the synthetic pending start.</summary>
    public void EndPendingRun(bool interrupted, DateTimeOffset now)
    {
        if (_runStartPending) EndRun(interrupted, now);
    }

    private readonly List<QueuedMessage> _queued = [];
    private long _queueSeq;

    public IReadOnlyList<QueuedMessage> Queued => _queued;
    public event Action? QueueChanged;
    /// <summary>omp echoed a prompt row that carried a durable submission: its id (delivery is confirmed).</summary>
    public event Action<string>? PromptConfirmed;

    public void RecoverQueued(long sequence, IReadOnlyList<ImageAttachment>? images = null)
    {
        var index = _queued.FindIndex(q => q.Seq == sequence);
        if (index < 0 || _queued[index].Recovered) return;
        _queued[index] = _queued[index] with { Recovered = true, LiveSteered = false, Images = images ?? _queued[index].Images };
        QueueChanged?.Invoke();
        Touch();
    }

    /// <summary>Pending input, checkpointed before runtime delivery or local builtin dispatch.</summary>
    public QueuedMessage Enqueue(QueueKind kind, string text, IReadOnlyList<ImageAttachment> images, string? submissionId = null)
    {
        var q = new QueuedMessage(++_queueSeq, kind, text, images) { SubmissionId = submissionId };
        _queued.Add(q);
        if (kind is QueueKind.Steer or QueueKind.FollowUp) _awaitingEngine.Add(q.Seq);
        QueueChanged?.Invoke();
        Touch();
        return q;
    }

    /// <summary>The local FIFO head is now being admitted to omp's ordinary follow-up queue.</summary>
    public void ReleaseDeferredFollowUp(long sequence)
    {
        var index = _queued.FindIndex(q => q.Seq == sequence && q.Kind == QueueKind.DeferredFollowUp && !q.Recovered);
        if (index < 0) return;
        _queued[index] = _queued[index] with { Kind = QueueKind.FollowUp };
        _awaitingEngine.Add(sequence);
        QueueChanged?.Invoke();
        Touch();
    }

    public void RemoveQueued(long seq)
    {
        _awaitingEngine.Remove(seq);
        if (_queued.RemoveAll(q => q.Seq == seq) > 0)
        {
            QueueChanged?.Invoke();
            Touch();
        }
    }

    /// <summary>Keep abandoned input whole, but never leave it eligible for automatic delivery.</summary>
    public void RecoverQueue(bool includeDeferred = true)
    {
        var changed = false;
        for (var i = 0; i < _queued.Count; i++)
            if (!_queued[i].Recovered && (includeDeferred || !_queued[i].Deferred))
            {
                _queued[i] = _queued[i] with { Recovered = true, LiveSteered = false };
                changed = true;
            }
        if (!changed) return;
        QueueChanged?.Invoke();
        Touch();
    }

    public void RestoreQueue(IReadOnlyList<QueuedMessage> restored, bool truncated, bool imagesDropped = false)
    {
        var pending = _queued.Where(q => !q.Recovered && !q.Deferred).ToList();
        var matched = new HashSet<long>();
        var unknown = new List<QueuedMessage>();
        foreach (var returned in restored)
        {
            // Names are client metadata. Empty image lists are also meaningful unless omp dropped images.
            var local = pending.FirstOrDefault(q => SameText(q, returned) && SameImages(q.Images, returned.Images));
            if (local is not null)
            {
                pending.Remove(local);
                matched.Add(local.Seq);
            }
            else unknown.Add(returned);
        }

        // Match complete payloads first so a stripped entry cannot consume a later exact match.
        if (imagesDropped)
            for (var i = 0; i < unknown.Count;)
            {
                var returned = unknown[i];
                var local = returned.Images.Count == 0 ? pending.FirstOrDefault(q => SameText(q, returned)) : null;
                if (local is null) { i++; continue; }
                pending.Remove(local);
                matched.Add(local.Seq);
                unknown.RemoveAt(i);
            }

        // Keep local order: omp groups the response by kind, not by the original enqueue sequence.
        // A limited response cannot prove an omitted original was delivered. Dropped images also leave
        // same-text duplicates ambiguous, so keep those originals rather than choosing one and losing others.
        for (var i = _queued.Count - 1; i >= 0; i--)
        {
            var local = _queued[i];
            if (local.Recovered || local.Deferred) continue;
            if (matched.Contains(local.Seq) || truncated ||
                (imagesDropped && restored.Any(returned => SameText(local, returned))))
                _queued[i] = local with { Recovered = true, LiveSteered = false };
            else _queued.RemoveAt(i);
        }
        foreach (var returned in unknown)
            _queued.Add(returned with { Seq = ++_queueSeq, Recovered = true, LiveSteered = false });
        QueueChanged?.Invoke();
        Touch();

        static bool SameText(QueuedMessage a, QueuedMessage b) => a.Kind == b.Kind && a.Text == b.Text;

        static bool SameImages(IReadOnlyList<ImageAttachment> a, IReadOnlyList<ImageAttachment> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (a[i].MimeType != b[i].MimeType || !a[i].Data.AsSpan().SequenceEqual(b[i].Data)) return false;
            return true;
        }
    }

    /// <param name="headline">The text's first line as the client wrote it, so the app can show it in its language.</param>
    public void AddNotice(NoticeLevel level, string text, ClientText? headline = null) => Add(new NoticeItem(0, level, text) { Headline = headline });

    public void Log(DateTimeOffset at, string type, string summary)
    {
        _debug.Enqueue(new DebugEntry(++_debugSeq, at, type, summary));
        while (_debug.Count > DebugCapacity) _debug.Dequeue();
        Touch();
    }

    /// <summary>Applies one frame from the event stream (not responses to our own requests).</summary>
    public void Apply(RpcFrame frame, DateTimeOffset now)
    {
        var historyRevision = TranscriptRevision;
        var phaseBefore = Phase;
        FramesReceived++;
        BytesReceived += frame.PhysicalBytes;
        LastFrameAt = now;
        var j = frame.Json;
        switch (frame.Type)
        {
            case "agent_start":
                _resumedToolCallIds.Clear();
                if (j.TryGetProperty("resumedToolCallIds", out var resumed) && resumed.ValueKind == JsonValueKind.Array)
                    foreach (var call in resumed.EnumerateArray())
                        if (call.ValueKind == JsonValueKind.String) _resumedToolCallIds.Add(call.GetString()!);
                AwaitingSettle = false;
                RunGeneration++;
                // A replaced run's late start (its abort is still settling) must not end the replacement's guard.
                if (_replacementKey == 0) _runStartPending = false;
                else _heldAgentStart = true;
                SetPhase(Phase == SessionPhase.Aborting ? SessionPhase.Aborting : SessionPhase.Running, now);
                break;
            case "agent_end":
                if (_runStartPending) break; // the previous run's end; the new run has not started yet
                if (j.TryGetProperty("isTerminal", out var term) && term.ValueKind == JsonValueKind.False)
                {
                    AwaitingSettle = Phase is SessionPhase.Running or SessionPhase.Aborting;
                    break;
                }
                AwaitingSettle = false;
                FinalizeOpenRows(interrupted: Phase == SessionPhase.Aborting);
                SetPhase(SessionPhase.Ready, now);
                StateRefreshWanted = true; // context usage and todos after the run
                break;
            case "session_settled":
                StateRefreshWanted = true; // authoritative indicators even when this settles the previous run
                // Upstream after 18.2.0: the session is done, whatever agent_end said (unless it is the previous
                // run's, read after the next prompt was sent).
                if (_runStartPending) break;
                AwaitingSettle = false;
                EndRun(interrupted: false, now);
                break;
            case "prompt_result":
                // agentInvoked:false is this prompt handled without a run (a slash command): no agent_start follows
                if (j.TryGetProperty("agentInvoked", out var inv) && inv.ValueKind == JsonValueKind.False)
                    EndRun(interrupted: false, now);
                break;
            case "message_start":
                OnMessageStart(j, now);
                break;
            case "message_update":
                OnMessageUpdate(j);
                break;
            case "message_end":
                OnMessageEnd(j, now);
                break;
            case "queue_update":
                OnQueueUpdate(j, frame.ReadTimestamp); // ConversationState.QueueUpdate.cs
                break;
            case "tool_execution_start":
                OnToolStart(j);
                break;
            case "tool_execution_update":
                OnToolUpdate(j);
                break;
            case "tool_execution_end":
                OnToolEnd(j);
                break;
            case "extension_ui_request":
                OnExtensionUi(j, now);
                break;
            case "extension_error":
                AddNotice(NoticeLevel.Warning, $"Extension error ({Str(j, "extensionPath")}): {Str(j, "error")}");
                break;
            case "auto_retry_start":
                var failedReply = _rows.FindLast(row => row.Item is AssistantItem or UserItem);
                if (failedReply?.Item is AssistantItem { Streaming: false, StopReason: "error" } failed
                    && (failedReply.Text?.Length ?? failed.Text.Length) == 0
                    && (failedReply.Thinking?.Length ?? failed.Thinking.Length) == 0)
                {
                    _rows.Remove(failedReply);
                    if (ReferenceEquals(_endedBeforeMessageEnd, failedReply)) _endedBeforeMessageEnd = null;
                    TranscriptRevision++;
                    Touch();
                }
                var retryText = $"Retrying after error (attempt {Raw(j, "attempt")}/{Raw(j, "maxAttempts")}): {Str(j, "errorMessage")}";
                if (_retryNotice is { } retry) Set(retry, new NoticeItem(retry.Key, NoticeLevel.Info, retryText));
                else _retryNotice = Add(new NoticeItem(0, NoticeLevel.Info, retryText));
                break;
            case "auto_compaction_start":
                AddNotice(NoticeLevel.Info, Str(j, "reason") switch
                {
                    "overflow" => "The context was full: compacting it…",
                    "threshold" => "The context is getting full: compacting it…",
                    "idle" => "Compacting the context while idle…",
                    _ => "Compacting context…",
                });
                break;
            // What omp tells its own terminal and the client used to drop: said once, in the conversation
            case "retry_fallback_succeeded":
                AddNotice(NoticeLevel.Info, $"Continued with {Str(j, "model") ?? "the fallback model"}.");
                break;
            case "ttsr_triggered":
                if (j.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array && rules.GetArrayLength() > 0)
                    AddNotice(NoticeLevel.Info, "Rules applied: " + string.Join(", ", rules.EnumerateArray().Select(r => Str(r, "name") ?? Str(r, "id") ?? "rule")));
                break;
            case "todo_reminder":
                if (j.TryGetProperty("todos", out var open) && open.ValueKind == JsonValueKind.Array)
                    AddNotice(NoticeLevel.Info, $"Reminder: {open.GetArrayLength()} tasks still open (attempt {Raw(j, "attempt")} of {Raw(j, "maxAttempts")}).");
                StateRefreshWanted = true;
                break;
            case "irc_message":
                if (j.TryGetProperty("message", out var irc)) AddVisibleContextMessage(irc, live: true);
                break;
            case "goal_updated":
                ApplyGoalFrame(j, frame.ReadTimestamp, now); // ConversationState.BtwGoal.cs: the goal bar
                break;
            case "btw_record":
                ApplyBtwRecordFrame(j, frame.ReadTimestamp);
                break;
            case "btw_delta":
                ApplyBtwDelta(j, frame.ReadTimestamp);
                break;
            case "command_output":
                var output = Str(j, "text") ?? Str(j, "output") ?? Compact(j);
                Add(new CommandOutputItem(0, output));
                if (NoteFreshProviderSession(output, frame.ReadTimestamp)) StateRefreshWanted = true;
                // Manual /compact runs in the background and does not emit auto_compaction_end.
                if (output.StartsWith("Compaction ", StringComparison.Ordinal) && SessionOutputs.ParseCompactEnd(output) is not null)
                    StateRefreshWanted = true;
                break;
            case "available_commands_update":
                if (j.TryGetProperty("commands", out var cmds)) Commands = ParseCommands(cmds);
                Touch();
                break;
            case "plan_mode_updated":
                ApplyPlanModeFrame(j, frame.ReadTimestamp);
                break;
            case "session_info_update":
                if (Str(j, "title") is { } title)
                {
                    SessionName = title.Length > 0 ? title : null;
                    HistoryTitleReadAt = frame.ReadTimestamp;
                    Touch();
                }
                break;
            case "config_update":
                if (j.TryGetProperty("model", out var cm) && Str(cm, "provider") is { } cp && Str(cm, "id") is { } cid)
                {
                    Model = $"{cp}/{cid}";
                    UsageLimit = null;
                    StateRefreshWanted = true;
                    HistoryModelReadAt = frame.ReadTimestamp;
                }
                if (j.TryGetProperty("thinkingLevel", out var ctl))
                {
                    ThinkingLevel = ctl.ValueKind == JsonValueKind.String ? ctl.GetString() : null;
                    HistoryThinkingReadAt = frame.ReadTimestamp;
                }
                Touch();
                break;
            case "thinking_level_changed":
                ThinkingLevel = Str(j, "thinkingLevel");
                HistoryThinkingReadAt = frame.ReadTimestamp;
                Touch();
                break;
            case "model_changed":
                UsageLimit = null;
                HistoryModelReadAt = frame.ReadTimestamp;
                StateRefreshWanted = true;
                Touch();
                break;
            case "todo_auto_clear":
                StateRefreshWanted = true;
                break;
            case "mcp_auth_required":
                if (Str(j, "serverName") is not { Length: > 0 } authServer) break;
                var expired = Str(j, "reason") == "expired";
                _mcpAuth.RemoveAll(a => a.Server == authServer);
                _mcpAuth.Add(new McpAuthRequest(authServer, expired));
                var authText = expired
                    ? new ClientText("Connector “{0}” needs you to sign in again.", authServer)
                    : new ClientText("Connector “{0}” needs you to sign in.", authServer);
                Add(new NoticeItem(0, NoticeLevel.Warning, authText.ToString()) { Headline = authText, McpServer = authServer });
                break;
            case "notice":
                if (Str(j, "source") == "session-persistence" && Str(j, "sessionFile") is { } movedFile
                    && Str(j, "sessionId") is { } movedId)
                {
                    SessionFile = movedFile;
                    SessionId = movedId;
                    StateRefreshWanted = true;
                }
                AddNotice(Str(j, "level") switch { "error" => NoticeLevel.Error, "warning" => NoticeLevel.Warning, _ => NoticeLevel.Info },
                    (Str(j, "source") is { Length: > 0 } src ? $"[{src}] " : "") + (Str(j, "message") ?? ""));
                break;
            case "auto_compaction_end":
                if (Bool(j, "aborted")) AddNotice(NoticeLevel.Warning, "Compaction stopped.");
                else if (Str(j, "errorMessage") is { Length: > 0 } ce) AddNotice(NoticeLevel.Error, "Compaction failed: " + ce);
                else if (!Bool(j, "skipped")) AddNotice(NoticeLevel.Info, "Context compacted.");
                StateRefreshWanted = true;
                break;
            case "auto_retry_end":
                ClearRetryNotice();
                if (!Bool(j, "success") && Str(j, "finalError") is { Length: > 0 } fe) AddNotice(NoticeLevel.Error, "Retries exhausted: " + fe);
                break;
            case "retry_fallback_applied":
                AddNotice(NoticeLevel.Warning, $"Model {Str(j, "from")} failed; continuing with {Str(j, "to")}.");
                StateRefreshWanted = true;
                break;
            case "subagent_lifecycle" or "subagent_progress" or "subagent_event":
                ApplySubagentFrame(frame.Type, j, now); // ConversationState.Subagents.cs
                break;
            case RpcFrame.Malformed:
                AddNotice(NoticeLevel.Warning, $"Ignored a malformed line from omp: {Str(j, "message")}");
                break;
            case RpcFrame.ProtocolError:
                AddNotice(NoticeLevel.Error, $"RPC framing error: {Str(j, "message")}");
                break;
            case RpcFrame.UnmatchedResponse:
                if (!(j.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True) && !OnSubagentCommandFailed(j) && !OnJobCommandFailed(j) && !IsOptionalUnknown(j))
                {
                    var cmd = Str(j, "command") ?? "?";
                    AddNotice(NoticeLevel.Error, $"{cmd} failed: {Str(j, "error") ?? "unknown error"}");
                    if (cmd is "prompt" or "abort_and_prompt") EndRun(interrupted: true, now);
                }
                break;
        }
        if (Phase != phaseBefore) HistoryPhaseReadAt = frame.ReadTimestamp;
        if (TranscriptRevision != historyRevision || Phase != phaseBefore || frame.Type is "message_update" or "auto_compaction_end")
            HistoryMutationReadAt = frame.ReadTimestamp;
        Log(now, frame.Type, Summarize(frame));
    }

    /// <summary>
    /// Queries the app makes only to show more when omp offers it; an omp without one answers "Unknown command" and the
    /// app falls back quietly (the default thinking levels, no subagents…). Never an error for the user.
    /// </summary>
    private static readonly HashSet<string> OptionalCommands = new(StringComparer.Ordinal)
    {
        "get_available_thinking_levels", "get_subagents", "get_subagent_messages", "get_session_stats", "get_login_providers",
    };

    private static bool IsOptionalUnknown(JsonElement j) =>
        Str(j, "command") is { } cmd && OptionalCommands.Contains(cmd)
        && Str(j, "error") is { } error && error.StartsWith("Unknown command", StringComparison.Ordinal);

    /// <summary>Hydrates one chronological page of AgentMessage history, including results whose calls are in an older page.</summary>
    public void Hydrate(JsonElement messages)
    {
        if (messages.ValueKind != JsonValueKind.Array) return;
        foreach (var m in messages.EnumerateArray())
        {
            switch (Str(m, "role"))
            {
                case "user":
                    Add(new UserItem(0, ContentText(m, "text"), Confirmed: true) { Images = ContentImages(m), At = MessageTime(m) });
                    break;
                case "assistant":
                    var text = ContentText(m, "text");
                    var thinking = ContentText(m, "thinking");
                    if (text.Length > 0 || thinking.Length > 0 || Str(m, "errorMessage") is not null)
                        Add(new AssistantItem(0, text, thinking, false, Str(m, "stopReason"), Str(m, "errorMessage")) { At = MessageTime(m) });
                    if (m.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array)
                        foreach (var p in parts.EnumerateArray())
                            if (Str(p, "type") == "toolCall")
                                HydrateToolCall(p);
                    break;
                case "custom" or "hookMessage" or "branchSummary" or "compactionSummary" or "fileMention" or "pythonExecution" or "bashExecution":
                    AddVisibleContextMessage(m);
                    break;
                case "toolResult":
                    if (Str(m, "toolName") == "todo") RecordPlan(m, MessageTime(m)); // ConversationState.Plan.cs
                    HydrateToolResult(m);
                    break;
            }
        }
    }

    public SessionSnapshot Snapshot()
    {
        var items = new TranscriptItem[_rows.Count];
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (r.Dirty && r.Item is AssistantItem a)
                r.Item = a with { Text = r.Text?.ToString() ?? a.Text, Thinking = r.Thinking?.ToString() ?? a.Thinking };
            r.Dirty = false;
            items[i] = r.Item;
        }
        return new SessionSnapshot(Version, Phase, items, Model, ProtocolVersion, SessionId, FramesReceived, BytesReceived,
            LastFrameAt, RunStartedAt, LastError, _debug.ToArray(), StreamMismatches, MessagesCompleted)
        {
            History = History,
            RunTime = _runTime,
            RunsEnded = RunsEnded,
            LastErrorText = LastErrorText,
            Dialogs = _dialogs.ToArray(),
            Queued = _queued.ToArray(),
            SigningIn = SigningIn,
            Commands = Commands,
            Todos = Todos,
            ContextPercent = ContextPercent,
            UsageLimit = UsageLimit,
            ExtensionStatus = new Dictionary<string, string>(_extensionStatus),
            Widgets = _widgets.ToArray(),
            EditorText = _editorText,
            OpenUrl = _openUrl,
            McpAuthRequests = [.. _mcpAuth],
            TranscriptEpoch = TranscriptEpoch,
            StartFailed = StartFailed && Phase == SessionPhase.Faulted,
            StartProblem = StartFailed && Phase == SessionPhase.Faulted ? StartProblem : StartProblem.None,
            UnavailableSessionModel = StartFailed && Phase == SessionPhase.Faulted ? UnavailableSessionModel : null,
            AutoRecover = Phase == SessionPhase.Faulted ? AutoRecover : null,
            SessionFile = SessionFile,
            SessionName = SessionName,
            SessionTitleHint = SessionTitleHint,
            Cwd = Cwd,
            ThinkingLevel = ThinkingLevel,
            Subagents = SubagentsSnapshot(),
            PlanHistory = PlansSnapshot(),
            SubagentsSupported = SubagentsSupported,
            JobsSupported = JobsSupported,
            PlanMode = PlanMode,
            PlanModeSupported = PlanModeSupported,
            Goal = Goal,
            GoalSupported = GoalSupported,
            GoalReadAt = GoalReadAt,
            Btw = BtwSnapshot(),
            BtwHistoryLoaded = BtwHistoryLoaded,
        };
    }

    private void OnMessageStart(JsonElement j, DateTimeOffset now)
    {
        if (!j.TryGetProperty("message", out var m)) return;
        switch (Str(m, "role"))
        {
            case "user":
                _endedBeforeMessageEnd = null; // a new prompt: the previous reply's message_end is not coming any more
                var text = ContentText(m, "text");
                // The first user message after our prompt is its echo (possibly expanded by a template or skill):
                // it confirms the optimistic row and supplies the text omp actually used. Later ones are new rows.
                // The images the client sent stay: the echo carries the same ones, and they need no decoding.
                // Several can wait at once (an interrupt sent while an earlier prompt was being admitted). The echoed text
                // decides, current prompts before superseded ones: omp may have cancelled a superseded prompt, so it never
                // takes an identical replacement's echo; an admitted one still gets its own.
                bool Waiting(Row r) => r.Item is UserItem { Confirmed: false };
                bool Current(Row r) => !_supersededPrompts.Contains(r.Key);
                var pending = _rows.FirstOrDefault(r => Waiting(r) && Current(r) && ((UserItem)r.Item).Text == text)
                    ?? _rows.FirstOrDefault(r => Waiting(r) && !Current(r) && ((UserItem)r.Item).Text == text)
                    ?? _rows.FirstOrDefault(r => Waiting(r) && Current(r))
                    ?? _rows.FirstOrDefault(Waiting);
                if (pending?.Item is UserItem u)
                {
                    Set(pending, u with { Text = text, Confirmed = true, Images = u.Images.Count > 0 ? u.Images : ContentImages(m), At = MessageTime(m) ?? u.At ?? now });
                    _supersededPrompts.Remove(pending.Key);
                    // The replacement's own echo: its run has begun, so its end is no longer a previous run's.
                    if (pending.Key == _replacementKey) ReleaseReplacement(runStarted: true);
                    if (_promptSubmissions.Remove(pending.Key, out var submission)) PromptConfirmed?.Invoke(submission);
                }
                else
                {
                    // A queued steer / follow-up being delivered: same text first; otherwise the oldest (omp may have
                    // expanded a template), unless this is a late second echo of the prompt that started the run.
                    // An omp that reports its queue already removed an expanded chip with its snapshot.
                    var lastUser = _rows.LastOrDefault(r => r.Item is UserItem)?.Item as UserItem;
                    var q = _queued.FirstOrDefault(x => !x.Recovered && !x.Deferred && x.Text == text)
                        ?? (lastUser?.Text == text || _engineReportsQueue ? null : _queued.FirstOrDefault(x => !x.Recovered && !x.Deferred));
                    if (q is null && lastUser?.Text == text && lastUser.Confirmed) break; // duplicate echo: nothing new
                    if (q is not null) DeliverQueued(q); // ConversationState.QueueUpdate.cs
                    Add(new UserItem(0, text, Confirmed: true) { Images = q is { Images.Count: > 0 } ? q.Images : ContentImages(m), At = MessageTime(m) ?? now });
                }
                break;
            case "assistant":
                var messageId = MessageId(j);
                // A stamped reply ends by its own id: a second message starting does not end it early.
                if (messageId is null || !StreamingRowHasId) FinalizeStreamingAssistant();
                _endedBeforeMessageEnd = null;
                _streamingAssistant = Add(new AssistantItem(0, "", "", Streaming: true, null, null) { At = MessageTime(m) ?? now });
                if (messageId is not null) _messageRows[messageId] = _streamingAssistant;
                // Start empty: omp 18.2.0 serializes the partial message when the frame is written, so
                // message_start can already contain text that still arrives as the first text_delta
                // (seen in Fixtures/omp-18.2.0-toolcall.jsonl). Deltas build the text; message_end settles it.
                _streamingAssistant.Text = new StringBuilder();
                _streamingAssistant.Thinking = new StringBuilder();
                _streamingAssistant.Dirty = true;
                break;
        }
    }

    private void OnMessageUpdate(JsonElement j)
    {
        if ((RowForMessage(j) ?? _streamingAssistant) is not { } row || !j.TryGetProperty("assistantMessageEvent", out var e)) return;
        switch (Str(e, "type"))
        {
            case "text_delta":
                row.Text!.Append(Str(e, "delta"));
                break;
            case "thinking_start":
                row.ThinkingBlockStart = row.Thinking!.Length;
                return;
            case "thinking_delta":
                row.Thinking!.Append(Str(e, "delta"));
                break;
            case "thinking_end" when Bool(e, "narration"):
                // Anthropic identifies user-facing prose only when its signature closes the block.
                row.Thinking!.Length = row.ThinkingBlockStart;
                row.Text!.Append(Str(e, "content"));
                break;
            default:
                return;
        }
        row.Dirty = true;
        Touch();
    }

    private void OnMessageEnd(JsonElement j, DateTimeOffset now)
    {
        if (!j.TryGetProperty("message", out var m)) return;
        if (Str(m, "role") != "assistant")
        {
            AddVisibleContextMessage(m, live: true);
            return;
        }
        var byId = RowForMessage(j);
        if (byId is not null) _messageRows.Remove(MessageId(j)!);
        var endedEarly = byId is not null
            ? byId.Item is AssistantItem { Streaming: false }
            : _streamingAssistant is null && _endedBeforeMessageEnd is not null;
        var row = byId ?? _streamingAssistant ?? _endedBeforeMessageEnd ?? Add(new AssistantItem(0, "", "", true, null, null) { At = MessageTime(m) ?? now });
        if (byId is null || ReferenceEquals(_endedBeforeMessageEnd, byId)) _endedBeforeMessageEnd = null;
        var final = ContentText(m, "text");
        var finalThinking = ContentText(m, "thinking");
        MessagesCompleted++;
        var stopReason = Str(m, "stopReason");
        var interrupted = stopReason is "error" or "aborted";
        if (!endedEarly && !interrupted && row.Text is { } streamed && !streamed.Equals(final.AsSpan()))
            StreamMismatches++;
        // Interrupted provider finals may omit content already shown. Keep each received
        // channel independently; successful finals remain authoritative.
        if (!interrupted || row.Text is null || final.Length >= row.Text.Length)
            row.Text = new StringBuilder(final);
        if (!interrupted || row.Thinking is null || finalThinking.Length >= row.Thinking.Length)
            row.Thinking = new StringBuilder(finalThinking);
        var a = (AssistantItem)row.Item;
        row.Item = a with { Streaming = false, StopReason = stopReason, Error = Str(m, "errorMessage"), At = MessageTime(m) ?? a.At ?? now };
        row.Dirty = true;
        if (byId is null || ReferenceEquals(_streamingAssistant, byId)) _streamingAssistant = null;
        Touch();
    }

    private void AddTurnEnd(TimeSpan took, bool interrupted)
    {
        var since = _rows.FindLastIndex(r => r.Item is UserItem);
        var tools = _rows.Skip(since + 1).Select(r => r.Item).OfType<ToolItem>().ToList();
        if (tools.Count == 0 && took < TimeSpan.FromSeconds(2)) return; // a slash command or an instant reply: nothing to sum up
        var files = FilesChanged(tools);
        Add(new TurnEndItem(0, took, interrupted, tools.Count, files));
    }

    private void OnToolStart(JsonElement j) => OnToolStart(j, _clock.GetUtcNow());

    private void OnToolStart(JsonElement j, DateTimeOffset now)
    {
        var id = Str(j, "toolCallId") ?? "";
        var hasArgs = j.TryGetProperty("args", out var a);
        var args = hasArgs ? Compact(a) : "";
        var name = Str(j, "toolName") ?? "tool";
        var written = hasArgs ? WrittenContent(name, a) : null;
        var tool = new ToolItem(0, id, name, Truncate(args, MaxArgsChars), ToolStatus.Running, null, hasArgs ? ToolSummary(name, a) : "",
            written, now) { Changes = hasArgs ? WrittenChange(id, name, a, written, live: true) : null };
        if (_resumedToolCallIds.Remove(id) && _toolsByCallId.TryGetValue(id, out var resumed))
            Set(resumed, tool with { Key = resumed.Key });
        else
            _toolsByCallId[id] = Add(tool);
        _toolsByCallId[id].Elapsed = MonotonicTime.Start(_clock);
    }

    /// <summary>
    /// A file being written is shown while it is written: its content as added lines, in omp's own diff format
    /// (<c>+  1|line</c>), until the result brings the real change (omp's write tool reports none).
    /// </summary>
    internal static string? WrittenContent(string tool, JsonElement args)
    {
        if (!IsWriteTool(tool) || args.ValueKind != JsonValueKind.Object
            || Str(args, "content") is not { } content) return null;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length && sb.Length < MaxDiffChars; i++)
            sb.Append('+').Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append('|').Append(lines[i]).Append('\n');
        return sb.ToString();
    }

    private const int MaxDiffChars = 60_000;

    /// <summary>The argument that says what a call does: command, path, pattern, query, url — whichever the tool has.</summary>
    internal static string ToolSummary(string tool, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in (string[])["command", "path", "file_path", "pattern", "query", "url", "question", "description", "prompt"])
            if (Str(args, key) is { Length: > 0 } v)
            {
                var line = string.Join(' ', v.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
                return (key == "command" ? "$ " : "") + Truncate(line, 200);
            }
        // omp's todo tool: {op, task | phase | items} (legacy {ops:[…]}) — "done · Wire workspace", "init · 3 items"
        if (Str(args, "op") is { Length: > 0 } op)
        {
            var what = Str(args, "task") ?? Str(args, "phase")
                ?? (args.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? Count(items.GetArrayLength(), "item") : null);
            return what is null ? op : $"{op} · {Truncate(what, 200)}";
        }
        if (args.TryGetProperty("ops", out var ops) && ops.ValueKind == JsonValueKind.Array) return Count(ops.GetArrayLength(), "change");
        // An edit's input: omp's hashline sections or an apply_patch envelope name the files
        if (Str(args, "input") is { Length: > 0 } input && EditedFiles(input) is { Count: > 0 } files)
            return files.Count == 1 ? Truncate(files[0], 200) : Count(files.Count, "file");
        // Otherwise the first short words it was given (never raw JSON on the line; the tooltip lists the arguments)
        var words = args.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null)
            .Where(v => v is { Length: > 0 and <= 80 } && !v.Contains('\n')).Take(2).ToArray();
        return string.Join(" · ", words);

        static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
    }

    /// <summary>omp's edit tool reports its change as a unified diff in <c>details.diff</c>.</summary>
    private static string? DiffOf(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("details", out var d) && Str(d, "diff") is { Length: > 0 } diff
            ? HeadTail(diff, MaxDiffChars)
            : null;

    /// <summary>
    /// A tool call omp set aside for a message that came in first (agent-loop.ts <c>createSkippedToolResult</c>): never
    /// started, or started and given up; omp's own terminal shows it neutrally, not as an error.
    /// </summary>
    internal static bool IsSkipped(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object
        && Str(d, "source") == "interrupt_skipped"
        && (Bool(d, "__synthetic") || (Bool(d, "__interrupted") && Str(d, "execution") == "started"));

    /// <summary>An IRC message (omp's CustomMessage) as a row: its sender and words from the details, not the model's envelope.</summary>
    internal static AgentMessageItem? AgentMessageOf(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("details", out var d) || d.ValueKind != JsonValueKind.Object
            || Str(d, "from") is not { } from || (Str(d, "message") ?? Str(d, "body")) is not { } text)
            return null;
        return new AgentMessageItem(0, from, Str(d, "to"), text, Str(d, "replyTo") is not null);
    }

    /// <summary>
    /// What a search found, from the counts omp's grep and glob put in the result's details ("Found 12 files", "Found 3
    /// matches in 2 files"); null for other tools and when the details say nothing.
    /// </summary>
    internal static string? ResultNote(string tool, JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("details", out var d) || d.ValueKind != JsonValueKind.Object) return null;
        var more = d.TryGetProperty("truncated", out var tr) && tr.ValueKind == JsonValueKind.True ? "+" : "";
        switch (tool)
        {
            case "glob" when Int(d, "fileCount") is { } files:
                return files == 0 ? "No files found" : $"Found {files}{more} {(files == 1 && more.Length == 0 ? "file" : "files")}";
            case "grep" when Int(d, "matchCount") is { } matches:
                if (matches == 0) return "No matches";
                var what = $"Found {matches}{more} {(matches == 1 && more.Length == 0 ? "match" : "matches")}";
                return Int(d, "fileCount") is { } inFiles and > 1 ? $"{what} in {inFiles} files" : what;
            default:
                return null;
        }

        static int? Int(JsonElement o, string key) =>
            o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    }

    private void OnToolUpdate(JsonElement j)
    {
        if (Str(j, "toolCallId") is not { } id || !_toolsByCallId.TryGetValue(id, out var row) || row.Item is not ToolItem t) return;
        if (j.TryGetProperty("partialResult", out var pr)) Set(row, t with { Output = HeadTail(ResultText(pr), MaxToolOutputChars) });
    }

    private void OnToolEnd(JsonElement j)
    {
        if (Str(j, "toolCallId") is not { } id || !_toolsByCallId.TryGetValue(id, out var row) || row.Item is not ToolItem t)
        {
            OnToolStart(j);
            if (Str(j, "toolCallId") is not { } id2 || !_toolsByCallId.TryGetValue(id2, out row) || row.Item is not ToolItem t2) return;
            t = t2;
        }
        if (t.Name == "todo") StateRefreshWanted = true;
        var err = j.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
        var hasResult = j.TryGetProperty("result", out var r);
        if (t.Name == "todo" && hasResult && !err) RecordPlan(r, LastFrameAt); // ConversationState.Plan.cs
        var output = hasResult ? HeadTail(ResultText(r), MaxToolOutputChars) : t.Output;
        var changes = hasResult ? ChangesAtEnd(t, r, err) : err ? null : t.Changes;
        Set(row, t with { Status = err ? hasResult && IsSkipped(r) ? ToolStatus.Skipped : ToolStatus.Failed : ToolStatus.Succeeded, Output = output, Diff = hasResult ? DiffOf(r) ?? t.Diff : t.Diff,
            Took = row.Elapsed.IsRunning ? row.Elapsed.Elapsed : t.Took, ResultNote = hasResult && !err ? ResultNote(t.Name, r) : null,
            Changes = changes, Summary = SummaryOf(t.Summary, changes) });
        row.Elapsed = row.Elapsed.Stop();
    }

    private void OnExtensionUi(JsonElement j, DateTimeOffset now)
    {
        var method = Str(j, "method");
        switch (method)
        {
            case "notify":
                var level = Str(j, "notifyType") switch { "error" => NoticeLevel.Error, "warning" => NoticeLevel.Warning, _ => NoticeLevel.Info };
                AddNotice(level, Str(j, "message") ?? "");
                break;
            case "select" or "confirm" or "input" or "editor":
                if (Str(j, "id") is not { } id || _dialogs.Any(d => d.Id == id)) break;
                if (Phase is SessionPhase.Aborting or SessionPhase.Stopping)
                {
                    // The user already pressed Stop: a question from the run being stopped (omp 18.2.0 does not tie tool
                    // approvals to the abort signal) is answered "cancelled" instead of shown, so the abort can finish
                    // and nothing runs after Stop.
                    DialogsToCancel.Add(id);
                    AddNotice(NoticeLevel.Warning, $"{(Options(j) is [{ Label: "Approve" }, { Label: "Deny" }] ? "Approval request" : "Question")} declined (the run is being stopped) — {(Str(j, "title") ?? "").Split('\n')[0]}");
                    break;
                }
                var options = Options(j);
                // Structured approval (the owned engine) names the request; an older omp only shows Approve/Deny.
                var approval = method == "select" && j.TryGetProperty("approval", out var a) ? ApprovalRequest.Parse(a) : null;
                var kind = method switch
                {
                    "select" when approval is not null => DialogKind.Approval,
                    "select" when options is [{ Label: "Approve" }, { Label: "Deny" }] => DialogKind.Approval,
                    "select" => DialogKind.Select,
                    "confirm" => DialogKind.Confirm,
                    "input" => DialogKind.Input,
                    _ => DialogKind.Editor,
                };
                var deadline = DialogDeadline(j, now);
                var dialog = new PendingDialog(id, kind, Str(j, "title") ?? "", Str(j, "message"), options,
                    Str(j, "placeholder"), Str(j, "prefill"), now, deadline) { Approval = kind == DialogKind.Approval ? approval : null };
                _dialogs.Add(dialog);
                if (deadline is not null) NewDeadlines.Add(dialog);
                if (kind == DialogKind.Approval) NewApprovals.Add(dialog);
                break;
            case "elicit":
                if (Str(j, "id") is not { } formId || _dialogs.Any(d => d.Id == formId)) break;
                var server = Str(j, "serverName") ?? "";
                if (Phase is SessionPhase.Aborting or SessionPhase.Stopping)
                {
                    DialogsToCancel.Add(formId);
                    AddNotice(NoticeLevel.Warning, $"Form from connector “{server}” dismissed (the run is being stopped)");
                    break;
                }
                _dialogs.Add(new PendingDialog(formId, DialogKind.Elicitation, Str(j, "message") ?? "", null, [], null, null, now, null)
                    { Server = server, Fields = ElicitFields(j) });
                Touch();
                break;
            case "ask":
                // Every question of one ask tool call, as one form (only after set_ask_dialog; SessionController).
                if (Str(j, "id") is not { } askId || _dialogs.Any(d => d.Id == askId)) break;
                var questions = AskQuestions(j);
                if (Phase is SessionPhase.Aborting or SessionPhase.Stopping || questions.Count == 0)
                {
                    // Stopping: answered "cancelled" like any question of the run being stopped. No usable question:
                    // cancelled too, so the tool fails instead of waiting for an answer nobody can give.
                    DialogsToCancel.Add(askId);
                    AddNotice(NoticeLevel.Warning, questions.Count == 0 ? "Question declined (omp sent no usable question)"
                        : $"Question declined (the run is being stopped) — {questions[0].Question.Split('\n')[0]}");
                    break;
                }
                var askDeadline = DialogDeadline(j, now);
                var ask = new PendingDialog(askId, DialogKind.Ask, questions[0].Question, null, [], null, null, now, askDeadline) { Questions = questions };
                _dialogs.Add(ask);
                if (askDeadline is not null) NewDeadlines.Add(ask);
                Touch();
                break;
            case "cancel":
                // omp withdrew a dialog (the tool or run that asked was aborted).
                if (Str(j, "targetId") is { } target && TakeDialog(target) is { } withdrawn)
                    AddNotice(NoticeLevel.Info, $"{Describe(withdrawn)} withdrawn by omp — {withdrawn.Headline}");
                break;
            case "setStatus":
                if (Str(j, "statusKey") is not { } sk) break;
                if (Str(j, "statusText") is { Length: > 0 } st) _extensionStatus[sk] = st;
                else _extensionStatus.Remove(sk);
                Touch();
                break;
            case "setWidget":
                if (Str(j, "widgetKey") is not { } wk) break;
                _widgets.RemoveAll(w => w.Key == wk);
                if (j.TryGetProperty("widgetLines", out var wl) && wl.ValueKind == JsonValueKind.Array && wl.GetArrayLength() > 0)
                    _widgets.Add(new ExtensionWidget(wk, [.. wl.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String ? l.GetString()! : l.GetRawText())],
                        Str(j, "widgetPlacement") == "belowEditor"));
                Touch();
                break;
            case "set_editor_text":
                _editorText = new EditorTextRequest(++_requestSeq, Str(j, "text") ?? "");
                Touch();
                break;
            case "open_url":
                if (Str(j, "url") is not { } url) break;
                _openUrl = new OpenUrlRequest(++_requestSeq, url, Str(j, "launchUrl"), Str(j, "instructions"), Str(j, "mcpServer"));
                AddNotice(NoticeLevel.Info, (_openUrl.McpServer is { } mcpServer ? $"Sign-in page of connector “{mcpServer}”: " : "omp asks to open: ")
                    + _openUrl.Target + (_openUrl.Instructions is { Length: > 0 } ins ? "\n" + ins : ""));
                break;
        }
    }

    private static List<DialogOption> Options(JsonElement j)
    {
        var list = new List<DialogOption>();
        if (!j.TryGetProperty("options", out var o) || o.ValueKind != JsonValueKind.Array) return list;
        var details = j.TryGetProperty("optionDetails", out var od) && od.ValueKind == JsonValueKind.Array ? od : default;
        var i = 0;
        foreach (var opt in o.EnumerateArray())
        {
            var label = opt.ValueKind == JsonValueKind.String ? opt.GetString()! : opt.GetRawText();
            string? description = null;
            if (details.ValueKind == JsonValueKind.Array && i < details.GetArrayLength() && Str(details[i], "description") is { Length: > 0 } desc)
                description = desc;
            // omp's ask tool marks its suggestion by appending "(Recommended)"; shown, never preselected.
            list.Add(new DialogOption(label, description, label.TrimEnd().EndsWith("(Recommended)", StringComparison.Ordinal)));
            i++;
        }
        return list;
    }

    /// <summary>When omp resolves a dialog by itself (its <c>timeout</c>, less a margin); null without one.</summary>
    private static DateTimeOffset? DialogDeadline(JsonElement j, DateTimeOffset now) =>
        j.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var ms) && ms is > 0 and < 86_400_000
            ? now + TimeSpan.FromMilliseconds(ms) - DialogDeadlineMargin
            : null;

    /// <summary>
    /// The questions of an <c>ask</c> request. A question without an id or text is left out; then the form would answer
    /// fewer questions than omp asked (which omp rejects), so any such question makes the whole request unusable.
    /// </summary>
    private static List<AskQuestion> AskQuestions(JsonElement j)
    {
        var list = new List<AskQuestion>();
        if (!j.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array) return list;
        foreach (var q in qs.EnumerateArray())
        {
            if (q.ValueKind != JsonValueKind.Object || Str(q, "id") is not { } qid || Str(q, "question") is not { } text) return [];
            var options = new List<AskOption>();
            if (q.TryGetProperty("options", out var os) && os.ValueKind == JsonValueKind.Array)
                foreach (var o in os.EnumerateArray())
                    if (o.ValueKind == JsonValueKind.Object && Str(o, "label") is { } label)
                        options.Add(new AskOption(label, Str(o, "description") is { Length: > 0 } d ? d : null, Str(o, "preview") is { Length: > 0 } p ? p : null));
            int? recommended = q.TryGetProperty("recommended", out var r) && r.TryGetInt32(out var ri) && ri >= 0 && ri < options.Count ? ri : null;
            list.Add(new AskQuestion(qid, text, Str(q, "header") is { Length: > 0 } h ? h.Trim() : null, options, Bool(q, "multi"), recommended));
        }
        return list;
    }

    /// <summary>Removes a pending dialog; null when it is no longer pending (answered, withdrawn, expired).</summary>
    public PendingDialog? TakeDialog(string id)
    {
        var i = _dialogs.FindIndex(d => d.Id == id);
        if (i < 0) return null;
        var d = _dialogs[i];
        _dialogs.RemoveAt(i);
        Touch();
        return d;
    }

    private static string WithoutRecommended(string label) =>
        label.TrimEnd() is var t && t.EndsWith("(Recommended)", StringComparison.Ordinal) && t.Length > "(Recommended)".Length
            ? t[..^"(Recommended)".Length].TrimEnd()
            : label;

    /// <summary>Records in the transcript how a dialog ended.</summary>
    public void RecordAnswer(PendingDialog d, DialogAnswer answer, string by)
    {
        var (level, text) = answer switch
        {
            DialogAnswer.Value v when d.Kind == DialogKind.Approval =>
                v.Text == "Approve" ? (NoticeLevel.Info, $"Approved {by} — {Subject(d)}") : (NoticeLevel.Warning, $"Denied {by} — {Subject(d)}"),
            DialogAnswer.Denied denied => (NoticeLevel.Warning, $"Denied {by} — {Subject(d)}: “{denied.Reason}”"),
            DialogAnswer.Value v when d.Kind is DialogKind.Input or DialogKind.Editor => (NoticeLevel.Info, $"Answered {by} — {d.Headline}"),
            // The choice as the user read it: omp's "(Recommended)" mark is a hint on the option, not part of the answer
            DialogAnswer.Value v => (NoticeLevel.Info, $"Answered {by}: “{WithoutRecommended(v.Text)}” — {d.Headline}"),
            DialogAnswer.Confirmed c => (NoticeLevel.Info, $"{(c.Yes ? "Confirmed" : "Declined")} {by} — {d.Headline}"),
            // Never the values: a form can hold personal data.
            DialogAnswer.Elicited e => (NoticeLevel.Info, $"{(e.Accept ? "Sent" : "Declined")} {by}: form from connector “{d.Server}” — {d.Headline}"),
            // The ask form: one question's chosen options as for a select (never the typed text, like an input's);
            // several questions only by the first one's words
            DialogAnswer.Asked a when a.Answers is [{ SelectedOptions.Count: > 0 } one] => (NoticeLevel.Info, $"Answered {by}: “{string.Join(", ", one.SelectedOptions)}” — {d.Headline}"),
            DialogAnswer.Asked => (NoticeLevel.Info, $"Answered {by} — {d.Headline}"),
            _ => (NoticeLevel.Warning, $"{Describe(d)} dismissed {by} — {Subject(d)}"),
        };
        AddNotice(level, text);
    }

    /// <summary>Closes dialogs whose deadline passed: omp has applied its default and will ignore a late answer.</summary>
    public void ExpireDialogs(DateTimeOffset now)
    {
        foreach (var d in _dialogs.Where(d => d.Deadline <= now).ToList())
        {
            TakeDialog(d.Id);
            AddNotice(NoticeLevel.Warning, $"{Describe(d)} timed out without an answer; omp applied its default — {Subject(d)}");
        }
    }

    /// <summary>Closes every pending dialog without answering (omp is gone or the run is being stopped).</summary>
    public List<PendingDialog> CloseAllDialogs(string reason)
    {
        var all = _dialogs.ToList();
        foreach (var d in all)
        {
            TakeDialog(d.Id);
            AddNotice(NoticeLevel.Warning, $"{Describe(d)} closed ({reason}) — {Subject(d)}");
        }
        return all;
    }

    private static string Describe(PendingDialog d) => d.Kind switch
    {
        DialogKind.Approval => "Approval request",
        DialogKind.Elicitation => $"Form from connector “{d.Server}”",
        _ => "Question",
    };

    /// <summary>The fields of an <c>elicit</c> request; malformed entries are left out (omp validates the answer anyway).</summary>
    private static List<ElicitField> ElicitFields(JsonElement j)
    {
        var fields = new List<ElicitField>();
        if (!j.TryGetProperty("fields", out var fs) || fs.ValueKind != JsonValueKind.Array) return fields;
        foreach (var f in fs.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object || Str(f, "name") is not { Length: > 0 } name || Str(f, "kind") is not { } kind) continue;
            var options = new List<ElicitOption>();
            if (f.TryGetProperty("options", out var os) && os.ValueKind == JsonValueKind.Array)
                foreach (var o in os.EnumerateArray())
                    if (Str(o, "value") is { } value) options.Add(new ElicitOption(value, Str(o, "label") ?? value));
            int? Int(string n) => f.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : null;
            double? Num(string n) => f.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            fields.Add(new ElicitField(name, kind, Str(f, "title") ?? name, Str(f, "description"), Bool(f, "required"), Str(f, "format"),
                Int("minLength"), Int("maxLength"), Num("minimum"), Num("maximum"), options, Int("minItems"), Int("maxItems"),
                f.TryGetProperty("default", out var dv) ? System.Text.Json.Nodes.JsonNode.Parse(dv.GetRawText()) : null));
        }
        return fields;
    }

    /// <summary>The user signed in to the connector (or it connected): its sign-in prompt goes away.</summary>
    public void ClearMcpAuthRequest(string server)
    {
        if (_mcpAuth.RemoveAll(a => a.Server == server) > 0) Touch();
    }

    /// <summary>What the notice is about: an approval's tool ("bash"; omp's own headline from an omp without typed approval), a question's words.</summary>
    private static string Subject(PendingDialog d) => d.Approval?.Tool ?? d.Headline;

    private void FinalizeStreamingAssistant()
    {
        if (_streamingAssistant is not { } row) return;
        row.Item = (AssistantItem)row.Item with { Streaming = false };
        row.Dirty = true;
        _streamingAssistant = null;
    }

    /// <summary>Every row reaches a final state: no spinner survives a run end, an abort or a crash.</summary>
    private void FinalizeOpenRows(bool interrupted)
    {
        ClearRetryNotice();
        if (_streamingAssistant is { } a)
        {
            _streamingAssistant = null;
            _endedBeforeMessageEnd = a;
        }
        // Every open reply, including stamped ones another message overtook; their message_end still finds them by id.
        foreach (var r in _messageRows.Values.Append(_endedBeforeMessageEnd))
            if (r?.Item is AssistantItem { Streaming: true } open)
            {
                r.Item = open with { Streaming = false, StopReason = interrupted ? "aborted" : open.StopReason };
                r.Dirty = true;
            }
        foreach (var row in _toolsByCallId.Values)
            if (row.Item is ToolItem { Status: ToolStatus.Running } t)
                Set(row, t with { Status = ToolStatus.Interrupted });
        Touch();
    }

    private void ClearRetryNotice()
    {
        if (_retryNotice is not { } row) return;
        _retryNotice = null;
        if (_rows.Remove(row))
        {
            TranscriptRevision++;
            Touch();
        }
    }

    private Row Add(TranscriptItem item)
    {
        var key = ++_nextKey;
        // Every kind of row gets its own key (the view matches rows by key: a missed kind showed only its first row).
        item = item with { Key = key };
        var row = new Row(key, item);
        _rows.Add(row);
        TranscriptRevision++;
        Touch();
        return row;
    }

    private void Set(Row row, TranscriptItem item)
    {
        row.Item = item;
        TranscriptRevision++;
        Touch();
    }

    private void Touch() => Version++;

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Raw(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.ToString() : "?";

    /// <summary>Concatenates displayed text or thinking; provider-marked narration belongs to the answer, not reasoning.</summary>
    internal static string ContentText(JsonElement message, string kind)
    {
        if (!message.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return kind == "text" ? c.GetString()! : "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in c.EnumerateArray())
        {
            var type = Str(part, "type");
            var displayedKind = type == "thinking" && Bool(part, "narration") ? "text" : type;
            if (displayedKind == kind && type is not null && Str(part, type) is { } s) sb.Append(s);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The image parts of a message (<c>{type:"image", data, mimeType}</c>, base64). omp sends the bytes in events and
    /// in get_messages (it resolves the blobs it stores sessions with); a part whose data is not base64 (a stored
    /// image that is gone, left as its <c>blob:sha256:</c> reference) keeps its place with no bytes.
    /// </summary>
    internal static IReadOnlyList<ImageAttachment> ContentImages(JsonElement message, string property = "content")
    {
        if (!message.TryGetProperty(property, out var c) || c.ValueKind != JsonValueKind.Array) return [];
        List<ImageAttachment>? images = null;
        foreach (var part in c.EnumerateArray())
        {
            if (Str(part, "type") != "image") continue;
            images ??= [];
            images.Add(new ImageAttachment($"image {images.Count + 1}", Str(part, "mimeType") ?? "image/png", Base64Bytes(Str(part, "data"))));
        }
        return images ?? (IReadOnlyList<ImageAttachment>)[];
    }

    private static byte[] Base64Bytes(string? data)
    {
        if (string.IsNullOrEmpty(data)) return [];
        try { return Convert.FromBase64String(data); }
        catch (FormatException) { return []; }
    }

    private static string ResultText(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.String) return result.GetString()!;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("content", out _)) return ContentText(result, "text");
        return Compact(result);
    }

    private static string Compact(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? "" : e.GetRawText();

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var end = GraphemeText.PrefixLength(s, max);
        return s[..end] + $"… [{s.Length - end} more chars]";
    }

    /// <summary>Keeps the start and the end: errors and summaries of tool output are usually at the end.</summary>
    private static string HeadTail(string s, int max)
    {
        if (s.Length <= max) return s;
        var head = GraphemeText.PrefixLength(s, max / 4);
        var tail = GraphemeText.SuffixStart(s, max - max / 4);
        return s[..head] + $"\n… [{tail - head} chars omitted] …\n" + s[tail..];
    }

    private static string Summarize(RpcFrame f)
    {
        var j = f.Json;
        return f.Type switch
        {
            "message_update" when j.TryGetProperty("assistantMessageEvent", out var e) =>
                Str(e, "type") + (Str(e, "delta") is { } d ? " " + Truncate(d, 60) : ""),
            "tool_execution_start" or "tool_execution_update" or "tool_execution_end" => $"{Str(j, "toolName")} {Str(j, "toolCallId")}",
            "extension_ui_request" => $"{Str(j, "method")} {Str(j, "widgetKey") ?? Str(j, "title") ?? Str(j, "statusKey")}",
            "message_start" or "message_end" when j.TryGetProperty("message", out var m) => Str(m, "role") ?? "",
            _ => Truncate(f.PhysicalBytes > 400 ? $"{f.PhysicalBytes} bytes" : Compact(j), 160),
        };
    }
}
