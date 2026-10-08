namespace OmpGui.ClientCore;

public enum SessionPhase { NotStarted, Starting, Ready, Running, Aborting, Stopping, Stopped, Faulted }

public enum ToolStatus { Running, Succeeded, Failed, Interrupted }

public enum NoticeLevel { Info, Warning, Error }

/// <summary>Immutable transcript rows handed to the UI. <see cref="Key"/> is stable for the row's lifetime.</summary>
public abstract record TranscriptItem(long Key);

public sealed record UserItem(long Key, string Text, bool Confirmed) : TranscriptItem(Key)
{
    /// <summary>The images sent with the message (none: an empty list).</summary>
    public IReadOnlyList<ImageAttachment> Images { get; init; } = [];
}

/// <summary>
/// An image sent with a prompt (<c>ImageContent</c>: base64 data + MIME type). <see cref="Data"/> is empty when omp
/// returned a message without the image's bytes (a history whose stored image is gone).
/// </summary>
public sealed record ImageAttachment(string Name, string MimeType, byte[] Data);

public enum QueueKind { Steer, FollowUp }

/// <summary>A message sent while the agent was working; it joins the transcript when omp delivers it.</summary>
public sealed record QueuedMessage(long Seq, QueueKind Kind, string Text, IReadOnlyList<ImageAttachment> Images);

public sealed record AssistantItem(long Key, string Text, string Thinking, bool Streaming, string? StopReason, string? Error) : TranscriptItem(Key);

/// <param name="Summary">What the call does in a few words (the command, the path, the query), from its arguments.</param>
/// <param name="Diff">Unified diff of a file edit (omp's edit tool puts it in the result details).</param>
/// <param name="Took">How long it ran (start to end event), once it ended; null for history loaded from a file.</param>
/// <param name="ResultNote">What a search found, from the result's details ("Found 12 files"); null when it says nothing.</param>
public sealed record ToolItem(long Key, string ToolCallId, string Name, string Args, ToolStatus Status, string? Output,
    string Summary = "", string? Diff = null, DateTimeOffset? StartedAt = null, TimeSpan? Took = null, string? ResultNote = null) : TranscriptItem(Key);

/// <summary>The end of a run, kept in the conversation (Claude Code's "Worked for 4m 46s"): how long, what it did.</summary>
public sealed record TurnEndItem(long Key, TimeSpan Took, bool Interrupted, int Tools, int FilesChanged) : TranscriptItem(Key);

public sealed record NoticeItem(long Key, NoticeLevel Level, string Text) : TranscriptItem(Key);

/// <summary>Text a slash command printed (<c>command_output</c>).</summary>
public sealed record CommandOutputItem(long Key, string Text) : TranscriptItem(Key);

/// <summary>A slash command omp can run over RPC (<c>get_available_commands</c> / <c>available_commands_update</c>).</summary>
public sealed record SlashCommand(string Name, string? Description, string? Hint, string Source, IReadOnlyList<string> Aliases, IReadOnlyList<SlashCommand> Subcommands);

/// <param name="Blocker">What a blocked task waits for (omp's todo tool, optional).</param>
public sealed record TodoTask(string Content, string Status, string? Blocker = null);

public sealed record TodoPhase(string Name, IReadOnlyList<TodoTask> Tasks);

public enum DialogKind { Approval, Select, Confirm, Input, Editor }

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
    /// <summary>First line of the title (for approvals: "Allow tool: bash").</summary>
    public string Headline => Title.Split('\n', 2)[0].Trim() is { Length: > 0 } h ? h : "Question";

    /// <summary>The title after its first line (for approvals: the tool's own details, e.g. the command).</summary>
    public string Details => Title.Split('\n', 2) is [_, var rest] ? rest.Trim() : "";
}

/// <summary>How the user (or the client on the user's behalf) answered a dialog.</summary>
public abstract record DialogAnswer
{
    public sealed record Value(string Text) : DialogAnswer;
    public sealed record Confirmed(bool Yes) : DialogAnswer;
    public sealed record Cancelled : DialogAnswer;
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
public sealed record OpenUrlRequest(long Seq, string Url, string? LaunchUrl, string? Instructions)
{
    public string Target => LaunchUrl ?? Url;
}

public sealed record DebugEntry(long Seq, DateTimeOffset At, string Type, string Summary);

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
    /// <summary>Runs that ended so far (<see cref="ConversationState.RunsEnded"/>).</summary>
    public long RunsEnded { get; init; }
    public IReadOnlyList<PendingDialog> Dialogs { get; init; } = [];
    public IReadOnlyList<QueuedMessage> Queued { get; init; } = [];
    public IReadOnlyList<SlashCommand> Commands { get; init; } = [];
    public IReadOnlyList<TodoPhase> Todos { get; init; } = [];
    /// <summary>Share of the model's context window in use (0–100), when omp reports it.</summary>
    public double? ContextPercent { get; init; }
    /// <summary>omp is running a sign-in flow for this provider; its command queue is blocked until it ends.</summary>
    public string? SigningIn { get; init; }
    public IReadOnlyDictionary<string, string> ExtensionStatus { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<ExtensionWidget> Widgets { get; init; } = [];
    public EditorTextRequest? EditorText { get; init; }
    public OpenUrlRequest? OpenUrl { get; init; }
    public long TranscriptEpoch { get; init; }
    /// <summary>Faulted while starting: most likely the runtime or its settings (first run), not a crash mid-session.</summary>
    public bool StartFailed { get; init; }
    /// <summary>Why the start failed, when the client recognizes it (see <see cref="StartProblem"/>).</summary>
    public StartProblem StartProblem { get; init; }
    public string? SessionFile { get; init; }
    public string? SessionName { get; init; }
    public string? Cwd { get; init; }
    public string? ThinkingLevel { get; init; }
    /// <summary>The <c>--approval-mode</c> omp runs with (null: omp's configured default).</summary>
    public string? ApprovalMode { get; init; }
    /// <summary>Subagents of this session, running and ended, in the order they started (ConversationState.Subagents.cs).</summary>
    public IReadOnlyList<SubagentInfo> Subagents { get; init; } = [];
    /// <summary>Whether this omp reports subagents (null: not known yet).</summary>
    public bool? SubagentsSupported { get; init; }
    /// <summary>Every todo list the todo tool committed in this conversation, oldest first (ConversationState.Plan.cs).</summary>
    public IReadOnlyList<PlanSnapshot> PlanHistory { get; init; } = [];
}

/// <summary>Start failures the client can offer a way out of.</summary>
public enum StartProblem
{
    None,
    /// <summary>The omp command could not be launched (not installed, wrong path).</summary>
    NotFound,
    /// <summary>omp started but exited because no model is available (no provider signed in or configured).</summary>
    NoModel,
    Other,
}
