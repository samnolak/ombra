namespace Ombra.ClientCore;

public enum SessionPhase { NotStarted, Starting, Ready, Running, Aborting, Stopping, Stopped, Faulted }

/// <summary>
/// How a tool call ended. <see cref="Skipped"/>: omp set the call aside to handle a message that came in first (the
/// user's queued message, an agent's, a finished background job) and tells the model to retry it if still needed;
/// normal control flow, not a failure (agent-loop.ts <c>interrupt_skipped</c>).
/// </summary>
public enum ToolStatus { Running, Succeeded, Failed, Interrupted, Skipped }

public enum NoticeLevel { Info, Warning, Error }

/// <summary>Immutable transcript rows handed to the UI. <see cref="Key"/> is stable for the row's lifetime.</summary>
public abstract record TranscriptItem(long Key);

public sealed record UserItem(long Key, string Text, bool Confirmed) : TranscriptItem(Key)
{
    /// <summary>The images sent with the message (none: an empty list).</summary>
    public IReadOnlyList<ImageAttachment> Images { get; init; } = [];
    /// <summary>When it was sent: omp's own <c>timestamp</c> for the message, else when the client sent it; null when
    /// a loaded history does not say.</summary>
    public DateTimeOffset? At { get; init; }
}

/// <summary>
/// An image sent with a prompt (<c>ImageContent</c>: base64 data + MIME type). <see cref="Data"/> is empty when omp
/// returned a message without the image's bytes (a history whose stored image is gone).
/// </summary>
public sealed record ImageAttachment(string Name, string MimeType, byte[] Data);

public enum QueueKind { Steer, FollowUp, BuiltinCommand, DeferredFollowUp }

/// <summary>Pending input: a runtime steer/follow-up, or local input waiting behind a builtin until idle.</summary>
public sealed record QueuedMessage(long Seq, QueueKind Kind, string Text, IReadOnlyList<ImageAttachment> Images)
{
    public string? SubmissionId { get; init; }
    public bool Recovered { get; init; }
    /// <summary>The thinking level chosen for this message while omp was busy (null: whatever the session runs at). Ombra
    /// keeps such a message in its own FIFO and sets the level right before omp starts it, never during an earlier run.</summary>
    public string? ThinkingLevel { get; init; }
    /// <summary><see cref="ThinkingLevel"/> is set in omp; the message itself goes next.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ThinkingApplied { get; init; }
    /// <summary>Already sent into the provider's streaming response; remains visible until its transcript echo.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LiveSteered { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Deferred => Kind is QueueKind.BuiltinCommand or QueueKind.DeferredFollowUp;
}

public sealed record AssistantItem(long Key, string Text, string Thinking, bool Streaming, string? StopReason, string? Error) : TranscriptItem(Key)
{
    /// <summary>When the reply was written: omp's own <c>timestamp</c> for the message, else when the client saw it begin;
    /// null when a loaded history does not say.</summary>
    public DateTimeOffset? At { get; init; }
}

/// <summary>What a tool did to one file: changed it, created it, deleted it, or moved it (and maybe changed it).</summary>
public enum FileChangeKind { Modified, Created, Deleted, Moved }

/// <summary>One file a tool changed, with its own diff (omp's code-frame lines or a unified diff).</summary>
/// <param name="Path">The file as it is after the change (the new path of a moved file).</param>
/// <param name="MovedFrom">The path it had before a move; null otherwise.</param>
public sealed record FileChange(string Path, FileChangeKind Kind, string? Diff, string? MovedFrom = null)
{
    /// <summary>A move within the same folder: the file was renamed ("Renamed", not "Moved").</summary>
    public bool IsRename => Kind == FileChangeKind.Moved && MovedFrom is { } from && SameFolder(from, Path);

    /// <summary>Both paths are in the same folder (a move between them is a rename).</summary>
    public static bool SameFolder(string a, string b) => string.Equals(Folder(a), Folder(b), StringComparison.Ordinal);

    private static string Folder(string path) => path[..(path.LastIndexOfAny(['/', '\\']) + 1)];
}

/// <param name="Summary">What the call does in a few words (the command, the path, the query), from its arguments.</param>
/// <param name="Diff">Unified diff of a file edit (omp's edit tool puts it in the result details).</param>
/// <param name="Took">How long it ran (start to end event), once it ended; null for history loaded from a file.</param>
/// <param name="ResultNote">What a search found, from the result's details ("Found 12 files"); null when it says nothing.</param>
/// <param name="Changes">The files the call changed, one entry per file (a patch may change several); null when it changed
/// none or the client cannot tell.</param>
public sealed record ToolItem(long Key, string ToolCallId, string Name, string Args, ToolStatus Status, string? Output,
    string Summary = "", string? Diff = null, DateTimeOffset? StartedAt = null, TimeSpan? Took = null, string? ResultNote = null,
    IReadOnlyList<FileChange>? Changes = null) : TranscriptItem(Key);

/// <summary>The end of a run, kept in the conversation (Claude Code's "Worked for 4m 46s"): how long, what it did.</summary>
public sealed record TurnEndItem(long Key, TimeSpan Took, bool Interrupted, int Tools, int FilesChanged) : TranscriptItem(Key);

public sealed record NoticeItem(long Key, NoticeLevel Level, string Text) : TranscriptItem(Key)
{
    /// <summary><see cref="Text"/>'s first line as the client wrote it (format and values), for the app's language; null: omp's or the client's text as it is.</summary>
    public Ombra.Rpc.ClientText? Headline { get; init; }
    /// <summary>The connector a sign-in notice is about (<c>mcp_auth_required</c>): the notice links to the connectors.</summary>
    public string? McpServer { get; init; }
}

/// <summary>Command output, or a labelled visible runtime context message. Never contains hidden custom content.</summary>
public sealed record CommandOutputItem(long Key, string Text) : TranscriptItem(Key)
{
    public string? Label { get; init; }
    public DateTimeOffset? At { get; init; }
    public IReadOnlyList<ImageAttachment> Images { get; init; } = [];
}

/// <summary>
/// A message between agents over omp's IRC (<c>irc_message</c>; <c>irc:incoming</c> in the history): a subagent writing to
/// this chat's agent, or one omp shows passing between two others (<c>irc:relay</c>, <c>irc:workpool</c>).
/// </summary>
/// <param name="From">The sending agent's name.</param>
/// <param name="To">The agent it went to; null when it came to this chat's agent.</param>
/// <param name="Text">What it says (Markdown), without omp's envelope for the model.</param>
/// <param name="IsReply">An answer to an earlier message.</param>
public sealed record AgentMessageItem(long Key, string From, string? To, string Text, bool IsReply) : TranscriptItem(Key);

/// <summary>A slash command omp can run over RPC (<c>get_available_commands</c> / <c>available_commands_update</c>).</summary>
public sealed record SlashCommand(string Name, string? Description, string? Hint, string Source, IReadOnlyList<string> Aliases, IReadOnlyList<SlashCommand> Subcommands);

/// <param name="Blocker">What a blocked task waits for (omp's todo tool, optional).</param>
/// <param name="Details">The task's longer description (omp's todo tool, optional); kept so an edit never drops it.</param>
/// <param name="Notes">The task's notes (omp's todo tool, optional); kept so an edit never drops them.</param>
public sealed record TodoTask(string Content, string Status, string? Blocker = null, string? Details = null, IReadOnlyList<string>? Notes = null)
{
    /// <summary>By value, notes included (the list's contents, not its instance): a re-read list is the same task.</summary>
    public bool Equals(TodoTask? other) =>
        other is not null && Content == other.Content && Status == other.Status && Blocker == other.Blocker && Details == other.Details
        && (Notes ?? []).SequenceEqual(other.Notes ?? []);

    public override int GetHashCode() => HashCode.Combine(Content, Status, Blocker, Details, Notes?.Count ?? 0);
}

public sealed record TodoPhase(string Name, IReadOnlyList<TodoTask> Tasks);

public enum DialogKind { Approval, Select, Confirm, Input, Editor, Elicitation, Ask }

/// <summary>A choice of an <c>ask</c> question; <see cref="Preview"/> is the tool's rich preview text.</summary>
public sealed record AskOption(string Label, string? Description, string? Preview);

/// <summary>
/// One question of omp's <c>ask</c> form (<c>extension_ui_request</c> method <c>ask</c>, sent after <c>set_ask_dialog</c>).
/// The options never include "Other": the form always offers free text.
/// </summary>
/// <param name="Recommended">Index of the suggested option: shown, never preselected.</param>
public sealed record AskQuestion(string Id, string Question, string? Header, IReadOnlyList<AskOption> Options, bool Multi, int? Recommended);

/// <summary>The answer to one <see cref="AskQuestion"/>: the chosen labels (at most one unless multi) and the free text.</summary>
public sealed record AskAnswer(string Id, IReadOnlyList<string> SelectedOptions, string? CustomInput);

public sealed record DialogOption(string Label, string? Description, bool Recommended);

/// <summary>
/// An omp <c>extension_ui_request</c> that waits for an answer (tool approval, the ask tool, extension dialogs).
/// <see cref="Deadline"/> is when omp resolves it by itself with its default (omp sends nothing then).
/// </summary>
public sealed record PendingDialog(
    string Id,
    DialogKind Kind,
    string Title,
    string? Message,
    IReadOnlyList<DialogOption> Options,
    string? Placeholder,
    string? Prefill,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? Deadline)
{
    /// <summary>First line of the title (for approvals: omp's text prompt, "Allow tool: bash").</summary>
    public string Headline => Title.Split('\n', 2)[0].Trim() is { Length: > 0 } h ? h : "Question";

    /// <summary>The title after its first line (for approvals: omp's text details, e.g. the command).</summary>
    public string Details => Title.Split('\n', 2) is [_, var rest] ? rest.Trim() : "";

    /// <summary>
    /// For <see cref="DialogKind.Approval"/>: the engine's typed request (<c>extension_ui_request.approval</c>). Null from an
    /// omp without structured approval: the card then shows its text and offers only Allow and Deny.
    /// </summary>
    public ApprovalRequest? Approval { get; init; }

    /// <summary>For <see cref="DialogKind.Elicitation"/>: the MCP server asking.</summary>
    public string? Server { get; init; }

    /// <summary>For <see cref="DialogKind.Elicitation"/>: the form's fields, in order.</summary>
    public IReadOnlyList<ElicitField> Fields { get; init; } = [];

    /// <summary>For <see cref="DialogKind.Ask"/>: every question of the ask tool call, in order.</summary>
    public IReadOnlyList<AskQuestion> Questions { get; init; } = [];
}

/// <summary>A choice of an MCP form field: the value sent and the label shown.</summary>
public sealed record ElicitOption(string Value, string Label);

/// <summary>
/// One field of an MCP form (<c>elicit</c>), as omp normalized it from the server's schema.
/// </summary>
/// <param name="Kind">text | number | integer | boolean | select | multiselect.</param>
/// <param name="Format">For text: email | uri | date | date-time.</param>
public sealed record ElicitField(string Name, string Kind, string Title, string? Description, bool Required, string? Format,
    int? MinLength, int? MaxLength, double? Minimum, double? Maximum, IReadOnlyList<ElicitOption> Options, int? MinItems, int? MaxItems,
    System.Text.Json.Nodes.JsonNode? Default);

/// <summary>How the user (or the client on the user's behalf) answered a dialog.</summary>
public abstract record DialogAnswer
{
    public sealed record Value(string Text) : DialogAnswer;
    public sealed record Confirmed(bool Yes) : DialogAnswer;
    public sealed record Cancelled : DialogAnswer;
    /// <summary>An MCP form: accepted with its values, or declined (dismissing it is <see cref="Cancelled"/>).</summary>
    public sealed record Elicited(bool Accept, System.Text.Json.Nodes.JsonObject? Content) : DialogAnswer;
    /// <summary>A structured approval denied with the user's reason, which the agent reads in the failed tool result.</summary>
    public sealed record Denied(string Reason) : DialogAnswer;
    /// <summary>The ask form: one answer per question, in the questions' order.</summary>
    public sealed record Asked(IReadOnlyList<AskAnswer> Answers) : DialogAnswer;
}

/// <summary>A status line or widget published by an extension (<c>setStatus</c> / <c>setWidget</c>).</summary>
public sealed record ExtensionWidget(string Key, IReadOnlyList<string> Lines, bool BelowEditor);

/// <summary>omp asked to put text into the composer (<c>set_editor_text</c>); <see cref="Seq"/> makes it one-shot.</summary>
public sealed record EditorTextRequest(long Seq, string Text);

/// <summary>
/// omp asked the host to open a URL (login flows). <see cref="LaunchUrl"/> is a short loopback URL that redirects to
/// <see cref="Url"/> (the contract says to prefer it as the copy target); <see cref="Instructions"/> can carry a code
/// the user must type (device flows).
/// </summary>
/// <param name="McpServer">Set when the URL belongs to the client's own <c>mcp_login</c> for that connector.</param>
public sealed record OpenUrlRequest(long Seq, string Url, string? LaunchUrl, string? Instructions, string? McpServer = null)
{
    public string Target => LaunchUrl ?? Url;
}

/// <summary>omp says a connector needs the user to sign in (<c>signed_out</c>) or to sign in again (<c>expired</c>).</summary>
public sealed record McpAuthRequest(string Server, bool Expired);

public sealed record DebugEntry(long Seq, DateTimeOffset At, string Type, string Summary);

/// <summary>Loaded history only; live rows continue independently. A stale snapshot requires an explicit recent-tail reload.</summary>
public sealed record SessionHistory(int LoadedMessages = 0, int TotalMessages = 0, bool HasOlder = false,
    bool IsLoading = false, bool NeedsReload = false, string? Error = null);

public sealed record SessionSnapshot(
    long Version,
    SessionPhase Phase,
    IReadOnlyList<TranscriptItem> Items,
    string? Model,
    int ProtocolVersion,
    string? SessionId,
    long FramesReceived,
    long BytesReceived,
    DateTimeOffset? LastFrameAt,
    DateTimeOffset? RunStartedAt,
    string? LastError,
    IReadOnlyList<DebugEntry> DebugTail,
    int StreamMismatches = 0,
    int MessagesCompleted = 0)
{
    public bool IsReadOnly { get; init; }
    public MonotonicTime RunTime { get; init; }
    public SessionHistory History { get; init; } = new();
    /// <summary>Runs that ended so far (<see cref="ConversationState.RunsEnded"/>).</summary>
    public long RunsEnded { get; init; }
    /// <summary><see cref="LastError"/>'s first line as the client wrote it, for the app's language (null: omp's own words).</summary>
    public Ombra.Rpc.ClientText? LastErrorText { get; init; }
    public IReadOnlyList<PendingDialog> Dialogs { get; init; } = [];
    public IReadOnlyList<QueuedMessage> Queued { get; init; } = [];
    public IReadOnlyList<SlashCommand> Commands { get; init; } = [];
    public IReadOnlyList<TodoPhase> Todos { get; init; } = [];
    /// <summary>Share of the model's context window in use (0–100), when omp reports it.</summary>
    public double? ContextPercent { get; init; }
    /// <summary>Only the active account's reported wrap-up/low-priority stage; no inferred quotas.</summary>
    public Ombra.Rpc.OmpUsageLimit? UsageLimit { get; init; }
    /// <summary>omp is running a sign-in flow for this provider; its command queue is blocked until it ends.</summary>
    public string? SigningIn { get; init; }
    public IReadOnlyDictionary<string, string> ExtensionStatus { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<ExtensionWidget> Widgets { get; init; } = [];
    public EditorTextRequest? EditorText { get; init; }
    public OpenUrlRequest? OpenUrl { get; init; }
    /// <summary>Connectors that need the user to sign in (again), newest last; one per server (<c>mcp_auth_required</c>).</summary>
    public IReadOnlyList<McpAuthRequest> McpAuthRequests { get; init; } = [];
    public long TranscriptEpoch { get; init; }
    /// <summary>Faulted while starting: most likely the runtime or its settings (first run), not a crash mid-session.</summary>
    public bool StartFailed { get; init; }
    /// <summary>Why the start failed, when the client recognizes it (see <see cref="StartProblem"/>).</summary>
    public StartProblem StartProblem { get; init; }
    public string? UnavailableSessionModel { get; init; }
    /// <summary>The automatic restart waiting to run after an unexpected exit (null: none pending; SessionController.AutoRecover.cs).</summary>
    public AutoRecoverStatus? AutoRecover { get; init; }
    public string? SessionFile { get; init; }
    public string? SessionName { get; init; }
    /// <summary>Bounded catalog title, independent of which history page is loaded. An explicit name takes precedence.</summary>
    public string? SessionTitleHint { get; init; }
    public string? Cwd { get; init; }
    public string? ThinkingLevel { get; init; }
    /// <summary>The level picked while omp was busy, for the next message (null: <see cref="ThinkingLevel"/>). Queued
    /// messages take it; once nothing waits, it becomes the session's level.</summary>
    public string? NextThinkingLevel { get; init; }
    /// <summary>The <c>--approval-mode</c> omp runs with (null: omp's configured default).</summary>
    public string? ApprovalMode { get; init; }
    /// <summary>Subagents of this session, running and ended, in the order they started (ConversationState.Subagents.cs).</summary>
    public IReadOnlyList<SubagentInfo> Subagents { get; init; } = [];
    /// <summary>Whether this omp reports subagents (null: not known yet).</summary>
    public bool? SubagentsSupported { get; init; }
    /// <summary>false: this omp does not know the background job commands (get_jobs); null otherwise.</summary>
    public bool? JobsSupported { get; init; }
    /// <summary>Every todo list the todo tool committed in this conversation, oldest first (ConversationState.Plan.cs).</summary>
    public IReadOnlyList<PlanSnapshot> PlanHistory { get; init; } = [];
    /// <summary>Plan mode and the proposed plan waiting for approval (ConversationState.PlanMode.cs).</summary>
    public Ombra.Rpc.OmpPlanMode PlanMode { get; init; } = Ombra.Rpc.OmpPlanMode.Off;
    /// <summary>Whether this omp has plan mode over RPC (null: not known yet).</summary>
    public bool? PlanModeSupported { get; init; }
    /// <summary>The session's goal (goal mode; ConversationState.BtwGoal.cs); null: none.</summary>
    public Ombra.Rpc.OmpGoal? Goal { get; init; }
    /// <summary>Whether this omp has goal mode over RPC (null: not known yet).</summary>
    public bool? GoalSupported { get; init; }
    /// <summary>When <see cref="Goal"/> was read: an active goal's time goes on from then.</summary>
    public DateTimeOffset GoalReadAt { get; init; }
    /// <summary>The session's side-question topics (<c>/btw</c>), newest first.</summary>
    public IReadOnlyList<Ombra.Rpc.OmpBtwRecord> Btw { get; init; } = [];
    /// <summary>get_btw_history has answered for this session (<see cref="Btw"/> holds its older topics too).</summary>
    public bool BtwHistoryLoaded { get; init; }
    /// <summary>Another process owns this chat's session file: the chat is shown read-only (SessionController.Lease.cs).</summary>
    public SessionLease? Lease { get; init; }
}

/// <summary>Why the chat is read-only while its session file belongs to another process.</summary>
public enum SessionLeaseState
{
    /// <summary>Opening it was refused: another Ombra window or a terminal omp has it open (<c>session_lease_held</c>).</summary>
    Held,
    /// <summary>It was open here until another window took it over (<c>session_lease_lost</c>).</summary>
    Lost,
}

/// <param name="Holder">Who has it now (null: the runtime did not say).</param>
/// <param name="IsTakingOver">"Open here" is waiting for the holder to let go.</param>
/// <param name="Error">Why the last "Open here" did not work.</param>
public sealed record SessionLease(SessionLeaseState State, Ombra.Rpc.OmpSessionLeaseHolder? Holder, bool IsTakingOver = false, string? Error = null);

/// <summary>Start failures the client can offer a way out of.</summary>
public enum StartProblem
{
    None,
    /// <summary>The omp command could not be launched (not installed, wrong path).</summary>
    NotFound,
    /// <summary>omp started but exited because no model is available (no provider signed in or configured).</summary>
    NoModel,
    /// <summary>A saved provider/model is unavailable; replacement requires an explicit recovery action.</summary>
    SessionModelUnavailable,
    Other,
}
