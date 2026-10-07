using System.Globalization;
using System.Text;
using System.Text.Json;
using OmpGui.Rpc;

namespace OmpGui.ClientCore;

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

    private readonly List<Row> _rows = [];
    private readonly Dictionary<string, Row> _toolsByCallId = [];
    private readonly Queue<DebugEntry> _debug = new();
    private Row? _streamingAssistant;
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
    public string? Model { get; set; }
    public int ProtocolVersion { get; set; } = 1;
    public string? SessionId { get; set; }
    /// <summary>Path of omp's current session file (<c>get_state.sessionFile</c>).</summary>
    public string? SessionFile { get; set; }
    public string? SessionName { get; set; }
    /// <summary>omp's working directory (the session header's <c>cwd</c>).</summary>
    public string? Cwd { get; set; }
    public string? ThinkingLevel { get; set; }
    /// <summary>Bumped whenever the transcript is replaced (other session, new session, omp restarted).</summary>
    public long TranscriptEpoch { get; private set; }
    public long FramesReceived { get; private set; }
    public long BytesReceived { get; private set; }
    public DateTimeOffset? LastFrameAt { get; private set; }
    public DateTimeOffset? RunStartedAt { get; private set; }
    public string? LastError { get; private set; }
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
    private long _requestSeq;

    /// <summary>Dialogs that arrived while stopping; the controller answers them "cancelled".</summary>
    public List<string> DialogsToCancel { get; } = [];

    /// <summary>Something changed that only get_state reports (todos, context usage, model): the controller refreshes.</summary>
    public bool StateRefreshWanted { get; set; }

    public IReadOnlyList<SlashCommand> Commands { get; set; } = [];
    public IReadOnlyList<TodoPhase> Todos { get; set; } = [];
    public double? ContextPercent { get; set; }

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

    /// <summary>get_state's todo phases.</summary>
    public static IReadOnlyList<TodoPhase> ParseTodos(JsonElement phases)
    {
        if (phases.ValueKind != JsonValueKind.Array) return [];
        // Entries that are not objects are skipped (TryGetProperty throws on them).
        return [.. phases.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(p => new TodoPhase(Str(p, "name") ?? "",
            p.TryGetProperty("tasks", out var t) && t.ValueKind == JsonValueKind.Array
                ? [.. t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => new TodoTask(Str(x, "content") ?? "", Str(x, "status") ?? "pending", Str(x, "blocker")))]
                : []))];
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    /// <summary>Dialogs with a deadline added since the controller last looked (it schedules their expiry).</summary>
    public List<PendingDialog> NewDeadlines { get; } = [];

    public IReadOnlyList<PendingDialog> Dialogs => _dialogs;

    private sealed class Row(long key, TranscriptItem item)
    {
        public readonly long Key = key;
        public TranscriptItem Item = item;
        public StringBuilder? Text;
        public StringBuilder? Thinking;
        public bool Dirty;
    }

    public void SetPhase(SessionPhase phase, DateTimeOffset now)
    {
        if (phase == Phase) return;
        // Once we are shutting down, late run events (omp still sends agent_end while it drains) must not
        // bring the session back to Ready/Running; only the end of the process decides the final phase.
        if (Phase == SessionPhase.Stopping && phase is not (SessionPhase.Stopped or SessionPhase.Faulted)) return;
        if (phase == SessionPhase.Running && Phase != SessionPhase.Aborting) RunStartedAt ??= now;
        // The run's end stays in the conversation: how long it worked, how many tools, how many files changed
        if (Phase is SessionPhase.Running or SessionPhase.Aborting && phase == SessionPhase.Ready && RunStartedAt is { } started)
            AddTurnEnd(now - started, interrupted: Phase == SessionPhase.Aborting);
        if (phase is SessionPhase.Ready or SessionPhase.Stopped or SessionPhase.Faulted) RunStartedAt = null;
        if (Phase is SessionPhase.Running or SessionPhase.Aborting && phase is (SessionPhase.Ready or SessionPhase.Faulted)) RunsEnded++;
        Phase = phase;
        Touch();
    }

    /// <summary>
    /// Drops the transcript of the old session; the debug log stays. <paramref name="newProcess"/>: omp itself was
    /// replaced, so its dialogs, extension status and widgets are gone too. Otherwise (session switched inside the same
    /// omp) they belong to omp and its extensions, not to the session, and stay.
    /// </summary>
    public void ResetTranscript(bool newProcess = true)
    {
        _rows.Clear();
        _toolsByCallId.Clear();
        _streamingAssistant = null;
        _endedBeforeMessageEnd = null;
        _queued.Clear();
        if (newProcess)
        {
            _dialogs.Clear();
            NewDeadlines.Clear();
            DialogsToCancel.Clear();
            _extensionStatus.Clear();
            _widgets.Clear();
            SigningIn = null;
        }
        AwaitingSettle = false;
        LastError = null;
        TranscriptEpoch++;
        Touch();
    }

    public string? SigningIn { get; set; }

    /// <summary>The last failure happened while omp was starting (not found, bad settings, crashed before ready).</summary>
    public bool StartFailed { get; private set; }
    public StartProblem StartProblem { get; private set; }

    public void Fail(string error, DateTimeOffset now, StartProblem problem = StartProblem.Other)
    {
        StartFailed = Phase == SessionPhase.Starting;
        StartProblem = StartFailed ? problem : StartProblem.None;
        AwaitingSettle = false;
        ConfirmPendingPrompts();
        LastError = error;
        // A failed start is explained once, by the client's setup screen (with the error under "Show details"); a
        // crash mid-conversation also marks where in the conversation it happened.
        if (!StartFailed) AddNotice(NoticeLevel.Error, error);
        FinalizeOpenRows(interrupted: true);
        SetPhase(SessionPhase.Faulted, now);
    }

    /// <summary>
    /// Ends a run (Running, Aborting, or cut short by Stopping) and finalizes its rows; no-op otherwise.
    /// While Stopping the phase stays Stopping; the end of the process decides the final phase.
    /// </summary>
    public void EndRun(bool interrupted, DateTimeOffset now)
    {
        AwaitingSettle = false;
        _runStartPending = false;
        if (Phase is not (SessionPhase.Running or SessionPhase.Aborting or SessionPhase.Stopping)) return;
        ConfirmPendingPrompts();
        FinalizeOpenRows(interrupted || Phase is SessionPhase.Aborting or SessionPhase.Stopping);
        SetPhase(SessionPhase.Ready, now);
    }

    /// <summary>A prompt that omp will never echo (failed, or its run is over) stops waiting for confirmation,
    /// so the next prompt's echo cannot be matched to it.</summary>
    private void ConfirmPendingPrompts()
    {
        foreach (var r in _rows)
            if (r.Item is UserItem { Confirmed: false } u) Set(r, u with { Confirmed = true });
    }

    public void AddUserPrompt(string text, int imageCount = 0)
    {
        Add(new UserItem(0, text, Confirmed: false, imageCount));
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
        SetPhase(SessionPhase.Running, now);
    }

    private readonly List<QueuedMessage> _queued = [];
    private long _queueSeq;

    public IReadOnlyList<QueuedMessage> Queued => _queued;

    /// <summary>A steer / follow-up omp accepted while a run was active; shown apart until omp delivers it.</summary>
    public QueuedMessage Enqueue(QueueKind kind, string text, int imageCount)
    {
        var q = new QueuedMessage(++_queueSeq, kind, text, imageCount);
        _queued.Add(q);
        Touch();
        return q;
    }

    public void RemoveQueued(long seq)
    {
        if (_queued.RemoveAll(q => q.Seq == seq) > 0) Touch();
    }

    /// <summary>omp's queue is empty but ours is not: those messages were not delivered (e.g. dropped with the run).</summary>
    public void DropUndeliveredQueue()
    {
        foreach (var q in _queued) AddNotice(NoticeLevel.Warning, "Not delivered: " + q.Text);
        _queued.Clear();
        Touch();
    }

    public void AddNotice(NoticeLevel level, string text) => Add(new NoticeItem(0, level, text));

    public void Log(DateTimeOffset at, string type, string summary)
    {
        _debug.Enqueue(new DebugEntry(++_debugSeq, at, type, summary));
        while (_debug.Count > DebugCapacity) _debug.Dequeue();
        Touch();
    }

    /// <summary>Applies one frame from the event stream (not responses to our own requests).</summary>
    public void Apply(RpcFrame frame, DateTimeOffset now)
    {
        FramesReceived++;
        BytesReceived += frame.PhysicalBytes;
        LastFrameAt = now;
        var j = frame.Json;
        switch (frame.Type)
        {
            case "agent_start":
                AwaitingSettle = false;
                _runStartPending = false;
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
                OnMessageStart(j);
                break;
            case "message_update":
                OnMessageUpdate(j);
                break;
            case "message_end":
                OnMessageEnd(j);
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
                AddNotice(NoticeLevel.Warning, $"Retrying after error (attempt {Raw(j, "attempt")}/{Raw(j, "maxAttempts")}): {Str(j, "errorMessage")}");
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
                if (j.TryGetProperty("message", out var irc))
                    AddNotice(NoticeLevel.Info, ContentText(irc, "text") is { Length: > 0 } said ? said : Compact(irc));
                break;
            case "goal_updated":
                if (j.TryGetProperty("goal", out var goal))
                    AddNotice(NoticeLevel.Info, goal.ValueKind == JsonValueKind.Null ? "Goal cleared." : $"Goal: {Str(goal, "objective") ?? Compact(goal)}" + (Str(goal, "status") is { } gs ? $" ({gs})" : ""));
                StateRefreshWanted = true;
                break;
            case "command_output":
                Add(new CommandOutputItem(0, Str(j, "text") ?? Str(j, "output") ?? Compact(j)));
                break;
            case "available_commands_update":
                if (j.TryGetProperty("commands", out var cmds)) Commands = ParseCommands(cmds);
                Touch();
                break;
            case "session_info_update":
                if (Str(j, "title") is { } title) { SessionName = title.Length > 0 ? title : null; Touch(); }
                break;
            case "config_update":
                if (j.TryGetProperty("model", out var cm) && Str(cm, "provider") is { } cp && Str(cm, "id") is { } cid) Model = $"{cp}/{cid}";
                if (j.TryGetProperty("thinkingLevel", out var ctl)) ThinkingLevel = ctl.ValueKind == JsonValueKind.String ? ctl.GetString() : null;
                Touch();
                break;
            case "thinking_level_changed":
                ThinkingLevel = Str(j, "thinkingLevel");
                Touch();
                break;
            case "model_changed" or "todo_auto_clear":
                StateRefreshWanted = true;
                break;
            case "notice":
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
                if (!(j.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True) && !OnSubagentCommandFailed(j) && !IsOptionalUnknown(j))
                {
                    var cmd = Str(j, "command") ?? "?";
                    AddNotice(NoticeLevel.Error, $"{cmd} failed: {Str(j, "error") ?? "unknown error"}");
                    if (cmd is "prompt" or "abort_and_prompt") EndRun(interrupted: true, now);
                }
                break;
        }
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

    /// <summary>Rebuilds the transcript from <c>get_messages</c> (AgentMessage[]).</summary>
    public void Hydrate(JsonElement messages)
    {
        if (messages.ValueKind != JsonValueKind.Array) return;
        foreach (var m in messages.EnumerateArray())
        {
            switch (Str(m, "role"))
            {
                case "user":
                    Add(new UserItem(0, ContentText(m, "text"), Confirmed: true, ContentCount(m, "image")));
                    break;
                case "assistant":
                    var text = ContentText(m, "text");
                    var thinking = ContentText(m, "thinking");
                    if (text.Length > 0 || thinking.Length > 0 || Str(m, "errorMessage") is not null)
                        Add(new AssistantItem(0, text, thinking, false, Str(m, "stopReason"), Str(m, "errorMessage")));
                    if (m.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array)
                        foreach (var p in parts.EnumerateArray())
                            if (Str(p, "type") == "toolCall")
                            {
                                var id = Str(p, "id") ?? "";
                                var hasArgs = p.TryGetProperty("arguments", out var a);
                                var toolName = Str(p, "name") ?? "tool";
                                var row = Add(new ToolItem(0, id, toolName,
                                    Truncate(hasArgs ? Compact(a) : "", MaxArgsChars), ToolStatus.Interrupted, null, hasArgs ? ToolSummary(toolName, a) : "",
                                    hasArgs ? WrittenContent(toolName, a) : null));
                                _toolsByCallId[id] = row;
                            }
                    break;
                case "bashExecution": // a !command the user ran (ConversationState.Session.cs)
                    HydrateShell(m);
                    break;
                case "toolResult":
                    if (Str(m, "toolName") == "todo") RecordPlan(m, MessageTime(m)); // ConversationState.Plan.cs
                    if (Str(m, "toolCallId") is { } callId && _toolsByCallId.TryGetValue(callId, out var tr) && tr.Item is ToolItem ti)
                    {
                        var err = m.TryGetProperty("isError", out var ie) && ie.ValueKind == JsonValueKind.True;
                        Set(tr, ti with { Status = err ? ToolStatus.Failed : ToolStatus.Succeeded, Output = HeadTail(ContentText(m, "text"), MaxToolOutputChars), Diff = DiffOf(m) ?? ti.Diff,
                            ResultNote = err ? null : ResultNote(ti.Name, m) });
                    }
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
            RunsEnded = RunsEnded,
            Dialogs = _dialogs.ToArray(),
            Queued = _queued.ToArray(),
            SigningIn = SigningIn,
            Commands = Commands,
            Todos = Todos,
            ContextPercent = ContextPercent,
            ExtensionStatus = new Dictionary<string, string>(_extensionStatus),
            Widgets = _widgets.ToArray(),
            EditorText = _editorText,
            OpenUrl = _openUrl,
            TranscriptEpoch = TranscriptEpoch,
            StartFailed = StartFailed && Phase == SessionPhase.Faulted,
            StartProblem = StartFailed && Phase == SessionPhase.Faulted ? StartProblem : StartProblem.None,
            SessionFile = SessionFile,
            SessionName = SessionName,
            Cwd = Cwd,
            ThinkingLevel = ThinkingLevel,
            Subagents = SubagentsSnapshot(),
            PlanHistory = PlansSnapshot(),
            SubagentsSupported = SubagentsSupported,
        };
    }

    private void OnMessageStart(JsonElement j)
    {
        if (!j.TryGetProperty("message", out var m)) return;
        switch (Str(m, "role"))
        {
            case "user":
                _endedBeforeMessageEnd = null; // a new prompt: the previous reply's message_end is not coming any more
                var text = ContentText(m, "text");
                // The first user message after our prompt is its echo (possibly expanded by a template or skill):
                // it confirms the optimistic row and supplies the text omp actually used. Later ones are new rows.
                var images = ContentCount(m, "image");
                var pending = _rows.FirstOrDefault(r => r.Item is UserItem { Confirmed: false });
                if (pending?.Item is UserItem u) Set(pending, u with { Text = text, Confirmed = true });
                else
                {
                    // A queued steer / follow-up being delivered: same text first; otherwise the oldest (omp may have
                    // expanded a template), unless this is a late second echo of the prompt that started the run.
                    var lastUser = _rows.LastOrDefault(r => r.Item is UserItem)?.Item as UserItem;
                    var q = _queued.FirstOrDefault(x => x.Text == text)
                        ?? (lastUser?.Text == text ? null : _queued.FirstOrDefault());
                    if (q is null && lastUser?.Text == text && lastUser.Confirmed) break; // duplicate echo: nothing new
                    if (q is not null) _queued.Remove(q);
                    Add(new UserItem(0, text, Confirmed: true, Math.Max(images, q?.ImageCount ?? 0)));
                }
                break;
            case "assistant":
                FinalizeStreamingAssistant();
                _endedBeforeMessageEnd = null;
                _streamingAssistant = Add(new AssistantItem(0, "", "", Streaming: true, null, null));
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
        if (_streamingAssistant is not { } row || !j.TryGetProperty("assistantMessageEvent", out var e)) return;
        switch (Str(e, "type"))
        {
            case "text_delta":
                row.Text!.Append(Str(e, "delta"));
                break;
            case "thinking_delta":
                row.Thinking!.Append(Str(e, "delta"));
                break;
            default:
                return;
        }
        row.Dirty = true;
        Touch();
    }

    private void OnMessageEnd(JsonElement j)
    {
        if (!j.TryGetProperty("message", out var m) || Str(m, "role") != "assistant") return;
        var endedEarly = _streamingAssistant is null && _endedBeforeMessageEnd is not null;
        var row = _streamingAssistant ?? _endedBeforeMessageEnd ?? Add(new AssistantItem(0, "", "", true, null, null));
        _endedBeforeMessageEnd = null;
        // The final message is authoritative: it replaces whatever the deltas accumulated.
        var final = ContentText(m, "text");
        MessagesCompleted++;
        // A row ended early stopped taking deltas, so a shorter streamed text there is expected, not data loss. So is a
        // message omp itself ended with an error or an abort: it may drop what streamed (omp 18.2.0 ends a detected
        // "thinking loop" with empty content and retries).
        var stopReason = Str(m, "stopReason");
        if (!endedEarly && stopReason is not ("error" or "aborted") && row.Text is { } streamed && !streamed.Equals(final.AsSpan()))
            StreamMismatches++;
        row.Text = new StringBuilder(final);
        row.Thinking = new StringBuilder(ContentText(m, "thinking"));
        var a = (AssistantItem)row.Item;
        row.Item = a with { Streaming = false, StopReason = stopReason, Error = Str(m, "errorMessage") };
        row.Dirty = true;
        _streamingAssistant = null;
        Touch();
    }

    private void AddTurnEnd(TimeSpan took, bool interrupted)
    {
        var since = _rows.FindLastIndex(r => r.Item is UserItem);
        var tools = _rows.Skip(since + 1).Select(r => r.Item).OfType<ToolItem>().ToList();
        if (tools.Count == 0 && took < TimeSpan.FromSeconds(2)) return; // a slash command or an instant reply: nothing to sum up
        var files = tools.Where(t => t.Diff is { Length: > 0 }).Select(t => t.Summary).Distinct().Count();
        Add(new TurnEndItem(0, took, interrupted, tools.Count, files));
    }

    private void OnToolStart(JsonElement j) => OnToolStart(j, DateTimeOffset.UtcNow);

    private void OnToolStart(JsonElement j, DateTimeOffset now)
    {
        var id = Str(j, "toolCallId") ?? "";
        var hasArgs = j.TryGetProperty("args", out var a);
        var args = hasArgs ? Compact(a) : "";
        var name = Str(j, "toolName") ?? "tool";
        _toolsByCallId[id] = Add(new ToolItem(0, id, name, Truncate(args, MaxArgsChars), ToolStatus.Running, null, hasArgs ? ToolSummary(name, a) : "",
            hasArgs ? WrittenContent(name, a) : null, now));
    }

    /// <summary>
    /// A file being written is shown while it is written: its content as added lines, in omp's own diff format
    /// (<c>+  1|line</c>), until the result brings the real change (omp's write tool reports none).
    /// </summary>
    internal static string? WrittenContent(string tool, JsonElement args)
    {
        if (tool is not ("write" or "create" or "write_file") || args.ValueKind != JsonValueKind.Object
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
        // Otherwise the first short words it was given (never raw JSON on the line; the tooltip has the arguments)
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
        Set(row, t with { Status = err ? ToolStatus.Failed : ToolStatus.Succeeded, Output = output, Diff = hasResult ? DiffOf(r) ?? t.Diff : t.Diff,
            Took = t.StartedAt is { } s ? DateTimeOffset.UtcNow - s : null, ResultNote = hasResult && !err ? ResultNote(t.Name, r) : null });
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
                var kind = method switch
                {
                    "select" when options is [{ Label: "Approve" }, { Label: "Deny" }] => DialogKind.Approval,
                    "select" => DialogKind.Select,
                    "confirm" => DialogKind.Confirm,
                    "input" => DialogKind.Input,
                    _ => DialogKind.Editor,
                };
                DateTimeOffset? deadline = j.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var ms) && ms is > 0 and < 86_400_000
                    ? now + TimeSpan.FromMilliseconds(ms) - DialogDeadlineMargin
                    : null;
                var dialog = new PendingDialog(id, kind, Str(j, "title") ?? "", Str(j, "message"), options,
                    Str(j, "placeholder"), Str(j, "prefill"), now, deadline);
                _dialogs.Add(dialog);
                if (deadline is not null) NewDeadlines.Add(dialog);
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
                _openUrl = new OpenUrlRequest(++_requestSeq, url, Str(j, "launchUrl"), Str(j, "instructions"));
                AddNotice(NoticeLevel.Info, "omp asks to open: " + _openUrl.Target
                    + (_openUrl.Instructions is { Length: > 0 } ins ? "\n" + ins : ""));
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
                v.Text == "Approve" ? (NoticeLevel.Info, $"Approved {by} — {d.Headline}") : (NoticeLevel.Warning, $"Denied {by} — {d.Headline}"),
            DialogAnswer.Value v when d.Kind is DialogKind.Input or DialogKind.Editor => (NoticeLevel.Info, $"Answered {by} — {d.Headline}"),
            // The choice as the user read it: omp's "(Recommended)" mark is a hint on the option, not part of the answer
            DialogAnswer.Value v => (NoticeLevel.Info, $"Answered {by}: “{WithoutRecommended(v.Text)}” — {d.Headline}"),
            DialogAnswer.Confirmed c => (NoticeLevel.Info, $"{(c.Yes ? "Confirmed" : "Declined")} {by} — {d.Headline}"),
            _ => (NoticeLevel.Warning, $"{Describe(d)} dismissed {by} — {d.Headline}"),
        };
        AddNotice(level, text);
    }

    /// <summary>Closes dialogs whose deadline passed: omp has applied its default and will ignore a late answer.</summary>
    public void ExpireDialogs(DateTimeOffset now)
    {
        foreach (var d in _dialogs.Where(d => d.Deadline <= now).ToList())
        {
            TakeDialog(d.Id);
            AddNotice(NoticeLevel.Warning, $"{Describe(d)} timed out without an answer; omp applied its default — {d.Headline}");
        }
    }

    /// <summary>Closes every pending dialog without answering (omp is gone or the run is being stopped).</summary>
    public List<PendingDialog> CloseAllDialogs(string reason)
    {
        var all = _dialogs.ToList();
        foreach (var d in all)
        {
            TakeDialog(d.Id);
            AddNotice(NoticeLevel.Warning, $"{Describe(d)} closed ({reason}) — {d.Headline}");
        }
        return all;
    }

    private static string Describe(PendingDialog d) => d.Kind == DialogKind.Approval ? "Approval request" : "Question";

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
        if (_streamingAssistant is { } a)
        {
            a.Item = (AssistantItem)a.Item with { Streaming = false, StopReason = interrupted ? "aborted" : ((AssistantItem)a.Item).StopReason };
            a.Dirty = true;
            _streamingAssistant = null;
            _endedBeforeMessageEnd = a;
        }
        foreach (var row in _toolsByCallId.Values)
            if (row.Item is ToolItem { Status: ToolStatus.Running } t)
                Set(row, t with { Status = ToolStatus.Interrupted });
        Touch();
    }

    private Row Add(TranscriptItem item)
    {
        var key = ++_nextKey;
        // Every kind of row gets its own key (the view matches rows by key: a missed kind showed only its first row).
        item = item with { Key = key };
        var row = new Row(key, item);
        _rows.Add(row);
        Touch();
        return row;
    }

    private void Set(Row row, TranscriptItem item)
    {
        row.Item = item;
        Touch();
    }

    private void Touch() => Version++;

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Raw(JsonElement e, string name) => e.TryGetProperty(name, out var p) ? p.ToString() : "?";

    /// <summary>Concatenates content parts of one kind (<c>text</c> or <c>thinking</c>); a string content counts as text.</summary>
    private static string ContentText(JsonElement message, string kind)
    {
        if (!message.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return kind == "text" ? c.GetString()! : "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in c.EnumerateArray())
            if (Str(part, "type") == kind && Str(part, kind) is { } s)
                sb.Append(s);
        return sb.ToString();
    }

    private static int ContentCount(JsonElement message, string kind) =>
        message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Count(p => Str(p, "type") == kind)
            : 0;

    private static string ResultText(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.String) return result.GetString()!;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("content", out _)) return ContentText(result, "text");
        return Compact(result);
    }

    private static string Compact(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? "" : e.GetRawText();

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + $"… [{s.Length - max} more chars]";

    /// <summary>Keeps the start and the end: errors and summaries of tool output are usually at the end.</summary>
    private static string HeadTail(string s, int max) =>
        s.Length <= max ? s : s[..(max / 4)] + $"\n… [{s.Length - max} chars omitted] …\n" + s[^(max - max / 4)..];

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
