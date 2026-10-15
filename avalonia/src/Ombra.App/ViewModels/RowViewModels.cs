using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>One transcript row. Rows are updated in place so the list keeps its containers while text streams in.</summary>
public abstract partial class RowViewModel : ObservableObject
{
    public long Key { get; }
    public TranscriptItem Model { get; private set; }

    /// <summary>The step group this row is a step of (StepGroups.cs), or null: tool steps and the replies with nothing
    /// to read between them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Depth))]
    private StepGroupRowViewModel? _group;

    /// <summary>One of the rows a finished turn's "Worked for…" line folds away (TurnFolds.cs).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Depth))]
    private bool _isInTurn;

    /// <summary>Hidden inside its folded group of steps: the group's one line stands for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolded))]
    private bool _isGroupFolded;

    /// <summary>Hidden inside its finished turn, whose "Worked for…" line is folded (TurnFolds.cs).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolded))]
    private bool _isTurnFolded;

    /// <summary>Takes no room: its group or its turn is folded.</summary>
    public bool IsFolded => IsGroupFolded || IsTurnFolded;

    /// <summary>
    /// How many lines that fold it stand above it: a finished turn's "Worked for…" line, then its step group's line. The
    /// conversation indents it that far, with a guide down from each of those lines (Controls/TreeIndent), so what a
    /// line folds away is seen before it is folded.
    /// </summary>
    public int Depth => (IsInTurn ? 1 : 0) + (Group is not null ? 1 : 0);

    protected RowViewModel(TranscriptItem model)
    {
        Key = model.Key;
        Model = model;
    }

    public void Update(TranscriptItem model)
    {
        Model = model;
        OnUpdated();
    }

    protected abstract void OnUpdated();

    public static RowViewModel Create(TranscriptItem item) => item switch
    {
        UserItem u => new UserRowViewModel(u),
        AssistantItem a => new AssistantRowViewModel(a),
        ToolItem t => new ToolRowViewModel(t),
        NoticeItem n => new NoticeRowViewModel(n),
        CommandOutputItem c => new CommandOutputRowViewModel(c),
        TurnEndItem e => new TurnEndRowViewModel(e),
        AgentMessageItem m => new AgentMessageRowViewModel(m),
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };
}

/// <summary>Your message. It owns the previews of the images sent with it: dispose the row when it leaves the list.</summary>
public sealed partial class UserRowViewModel(UserItem item) : RowViewModel(item), IDisposable
{
    [ObservableProperty] private string _text = item.Text;
    [ObservableProperty] private bool _confirmed = item.Confirmed;
    /// <summary>When it was sent (null when the history does not say).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText), nameof(HasTime))]
    private DateTimeOffset? _at = item.At;

    /// <summary>The day and time over the message when it starts the conversation or follows a pause
    /// (MainViewModel.MarkTurnEnds); empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDateLabel))]
    private string _dateLabel = "";

    public bool HasDateLabel => DateLabel.Length > 0;
    /// <summary>"14:08" today, "29 Sep, 14:08" before (Codex's line under a message).</summary>
    public string TimeText => MessageTimes.Short(At);
    public bool HasTime => At is not null;

    /// <summary>The images sent with the message, as thumbnails (each opens the viewer, ←/→ through the rest).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImages))]
    private IReadOnlyList<ImagePreview> _previews = PreviewsOf(item.Images);

    public bool HasImages => Previews.Count > 0;

    private static IReadOnlyList<ImagePreview> PreviewsOf(IReadOnlyList<ImageAttachment> images) =>
        [.. images.Select(i => new ImagePreview(i, () => images))];

    public void Dispose()
    {
        foreach (var p in Previews) p.Dispose();
    }

    private const int FoldLines = 8, FoldChars = 600;

    /// <summary>A long message (pasted text, page comments) shows its start until opened (Claude Code folds them too).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText), nameof(FoldLabel), nameof(HasOwnText), nameof(HasBubble))]
    private bool _isUnfolded;

    /// <summary>Where the code or page comments sent with this message begin (-1: none). They show as a chip, not as text.</summary>
    private int CommentsAt
    {
        get
        {
            // Code comments come before the page's (MainViewModel.WithComments)
            foreach (var marker in (string[])[MainViewModel.CodeCommentsMarker, PreviewViewModel.PromptMarker])
            {
                if (Text.StartsWith(marker, StringComparison.Ordinal)) return 0;
                var i = Text.IndexOf("\n\n" + marker, StringComparison.Ordinal);
                if (i >= 0) return i + 2;
            }
            return -1;
        }
    }

    public bool HasComments => CommentsAt >= 0;

    /// <summary>What the user wrote (without the comments block, and without the line added when there were no words).</summary>
    private string OwnText
    {
        get
        {
            if (CommentsAt is var at && at < 0) return Text;
            var own = Text[..at].Trim();
            return own is PreviewViewModel.CommentsOnlyLine or MainViewModel.CodeCommentsOnlyLine or MainViewModel.AllCommentsOnlyLine ? "" : own;
        }
    }

    public string CommentsLabel
    {
        get
        {
            if (CommentsAt is var at && at < 0) return "";
            var block = Text[at..];
            var n = block.Split('\n').Count(l => l.Length > 2 && char.IsAsciiDigit(l[0]) && l.IndexOf(". ", StringComparison.Ordinal) is > 0 and < 4);
            var hosts = block.Split('\n').Where(l => l.StartsWith(PreviewViewModel.PromptMarker, StringComparison.Ordinal))
                .Select(l => Uri.TryCreate(l[PreviewViewModel.PromptMarker.Length..].Split(' ')[0].TrimEnd(','), UriKind.Absolute, out var u) ? u.Authority + u.AbsolutePath.TrimEnd('/') : "")
                .Where(h => h.Length > 0).Distinct().ToList();
            var what = n == 1 ? Loc.T("1 comment") : Loc.F("{0} comments", n);
            if (block.StartsWith(MainViewModel.CodeCommentsMarker, StringComparison.Ordinal))
                return block.Contains("\n\n" + PreviewViewModel.PromptMarker, StringComparison.Ordinal) ? what : Loc.F("{0} on the code", what);
            return hosts.Count == 1 ? Loc.F("{0} on {1}", what, hosts[0]) : hosts.Count > 1 ? Loc.F("{0} on {1} pages", what, hosts.Count) : Loc.F("{0} on the page", what);
        }
    }

    public bool HasOwnText => OwnText.Length > 0 || IsUnfolded;
    /// <summary>The blue bubble: words to show, or a fold link (images and comments alone sit without one).</summary>
    public bool HasBubble => HasOwnText || IsLong;
    public bool IsLong => HasComments || OwnText.Length > FoldChars + 200 || OwnText.Count(c => c == '\n') > FoldLines + 4;
    public string DisplayText => IsUnfolded ? Text : HasComments ? OwnText : IsLong ? Head(OwnText) + "…" : OwnText;
    public string FoldLabel => IsUnfolded ? (HasComments ? Loc.T("Hide what was sent") : Loc.T("Show less")) : HasComments ? Loc.T("Show what was sent") : Loc.T("Show more");

    private static string Head(string text)
    {
        var lines = text.Split('\n');
        var head = string.Join('\n', lines.Take(FoldLines));
        return (head.Length > FoldChars ? head[..FoldChars] : head).TrimEnd();
    }

    [RelayCommand]
    private void ToggleFold() => IsUnfolded = !IsUnfolded;

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsLong));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(HasComments));
        OnPropertyChanged(nameof(CommentsLabel));
        OnPropertyChanged(nameof(HasOwnText));
        OnPropertyChanged(nameof(HasBubble));
        OnPropertyChanged(nameof(FoldLabel));
    }

    protected override void OnUpdated()
    {
        var u = (UserItem)Model;
        Text = u.Text;
        Confirmed = u.Confirmed;
        At = u.At;
        // The same images (the echo of a prompt keeps the ones sent) keep their decoded thumbnails
        if (!Previews.Select(p => p.Model).SequenceEqual(u.Images))
        {
            Dispose();
            Previews = PreviewsOf(u.Images);
        }
    }
}

public sealed partial class AssistantRowViewModel : RowViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _thinking = "";
    [ObservableProperty] private bool _streaming;
    [ObservableProperty] private string? _footer;
    /// <summary>The provider's error when the reply failed (not the user's own stop).</summary>
    [ObservableProperty] private string? _errorText;
    /// <summary>The failed reply is the last thing in the conversation and nothing runs: it offers "Retry" (omp's /retry).</summary>
    [ObservableProperty] private bool _canRetry;
    /// <summary>The "Thinking" block is open (closed by default).</summary>
    [ObservableProperty] private bool _isThinkingOpen;
    /// <summary>The last reply of a finished turn: it carries the message actions (copy, the time) below it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    private bool _isTurnEnd;
    /// <summary>When the reply was written (null when the history does not say).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText), nameof(HasTime))]
    private DateTimeOffset? _at;

    private DateTimeOffset? _thinkingFrom, _thinkingTo;

    public AssistantRowViewModel(AssistantItem item) : base(item) => OnUpdated();

    public bool HasThinking => Thinking.Length > 0;
    public bool HasFooter => Footer is not null;
    public bool HasError => ErrorText is not null;
    public bool HasText => Text.Length > 0;
    /// <summary>A reply with nothing to show yet (or only tool calls) takes no room in the transcript.</summary>
    public bool HasContent => HasText || HasThinking || HasFooter || HasError;
    /// <summary>The model is still thinking: the label pulses until the answer starts.</summary>
    public bool IsThinkingLive => Streaming && HasThinking && !HasText;
    /// <summary>"Thinking… 4s" while it thinks, then "Thought for 4s" (Claude Code); loaded history just says "Thinking".</summary>
    public string ThinkingLabel => IsThinkingLive
            ? _thinkingFrom is { } since && (DateTimeOffset.UtcNow - since).TotalSeconds >= 1 ? Loc.F("Thinking… {0}s", (int)(DateTimeOffset.UtcNow - since).TotalSeconds) : Loc.T("Thinking…")
        : _thinkingFrom is { } from && _thinkingTo is { } to ? Loc.F("Thought for {0}s", Math.Max(1, (int)Math.Round((to - from).TotalSeconds)))
        : Loc.T("Thinking");
    /// <summary>The actions line under the reply: the last reply of a finished turn, or a stopped one.</summary>
    public bool ShowActions => HasFooter || (IsTurnEnd && HasText && !Streaming);
    /// <summary>"14:09" today, "29 Sep, 12:13" before, at the end of the actions line.</summary>
    public string TimeText => MessageTimes.Short(At);
    public bool HasTime => At is not null;

    /// <summary>The once-a-second tick while a run goes on: the live thinking label counts up.</summary>
    public void Tick()
    {
        if (IsThinkingLive) OnPropertyChanged(nameof(ThinkingLabel));
    }

    /// <summary>Raised when the user copies the message; the view puts it on the clipboard.</summary>
    public static event Action<string>? CopyRequested;

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke(Text);

    /// <summary>Copies the reply as it reads, without the Markdown marks (Copy as Markdown is <see cref="CopyCommand"/>).</summary>
    [RelayCommand]
    private void CopyText() => CopyRequested?.Invoke(Markdig.Markdown.ToPlainText(Text).TrimEnd());

    protected override void OnUpdated()
    {
        var a = (AssistantItem)Model;
        Text = a.Text;
        Thinking = a.Thinking;
        Streaming = a.Streaming;
        At = a.At;
        // An abort is the user's own stop, not an error, although omp also sets errorMessage ("Interrupted by user").
        Footer = a.StopReason == "aborted" ? Loc.T("Interrupted") : null;
        ErrorText = a.StopReason == "aborted" ? null : a.Error;
        // Thinking time as seen here: from its first delta to the first answer text (or the end of the message)
        if (a.Streaming && a.Thinking.Length > 0) _thinkingFrom ??= DateTimeOffset.UtcNow;
        if (_thinkingFrom is not null && _thinkingTo is null && (a.Text.Length > 0 || !a.Streaming)) _thinkingTo = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(HasThinking));
        OnPropertyChanged(nameof(HasFooter));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasText));
        OnPropertyChanged(nameof(HasContent));
        OnPropertyChanged(nameof(IsThinkingLive));
        OnPropertyChanged(nameof(ThinkingLabel));
        OnPropertyChanged(nameof(ShowActions));
    }
}

/// <summary>
/// A tool call, Codex style: a status mark, what it does as a verb in the tense of its state ("Running" / "Ran",
/// "Reading" / "Read", "Created"), what it acts on, and what it gave. A command shows its command line and output in a
/// console block; a failure says why on its line ("exit 127").
/// </summary>
public sealed partial class ToolRowViewModel : RowViewModel
{
    /// <summary>Output lines shown under a finished call before "… +N lines" (Claude Code shows three).</summary>
    public const int PreviewLines = 3;
    /// <summary>A command's lines in its console before it is cut with an ellipsis (open, all of it).</summary>
    public const int CommandLines = 3;

    /// <summary>The line's tooltip in words, never the raw arguments: a command's whole command line (none while its
    /// console shows it), the file, the search, or another tool's arguments one per line; null when there is nothing to say.
    /// The view drops it while it only repeats text the line shows whole (Controls/TrimmedToolTip).</summary>
    [ObservableProperty] private string? _tooltip;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _diff;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isDone;
    /// <summary>Stopped before it ended (the user's stop, or omp went away): neither done nor failed.</summary>
    [ObservableProperty] private bool _isInterrupted;
    /// <summary>What the call does, in the tense of its state: "Running" / "Ran", "Reading" / "Read", "Created".</summary>
    [ObservableProperty] private string _verb = "";
    /// <summary>What it acts on, after the verb: the path, the pattern and where, the command's first line.</summary>
    [ObservableProperty] private string _argText = "";
    /// <summary>A few quiet words at the line's end: what a read or search found ("120 lines", "3 files"), "Interrupted".</summary>
    [ObservableProperty] private string _meta = "";
    /// <summary>omp asks to allow this running call (the approval card under the conversation): "Waiting for approval",
    /// its mark in the waiting colour (MainViewModel.MarkAwaitingApproval).</summary>
    [ObservableProperty] private bool _awaitsApproval;
    /// <summary>Why it failed, at a glance on its line: "exit 127", "timed out", "failed"; null unless it failed.</summary>
    [ObservableProperty] private string? _failureText;
    /// <summary>A command's whole command line, in its console after the prompt.</summary>
    [ObservableProperty] private string _command = "";
    /// <summary>The output in a command's console: its newest lines while it runs, then its first ones; open, all of it.</summary>
    [ObservableProperty] private string _consoleOutput = "";
    /// <summary>How long it ran ("2.3s"), once it ended; empty under a tenth of a second (an instant call says nothing).</summary>
    [ObservableProperty] private string _tookText = "";
    /// <summary>What a tool other than a command gave, under its line: its first output lines, or the error.</summary>
    [ObservableProperty] private string _result = "";
    /// <summary>Output lines past the preview ("… +12 lines" opens the rest).</summary>
    [ObservableProperty] private int _moreLines;
    /// <summary>The result is the tool's own output lines (mono), not words.</summary>
    [ObservableProperty] private bool _resultIsOutput;

    /// <summary>The output without omp's own notes at its end ("Wall time: …", "Command exited with code 1").</summary>
    private string _shownOutput = "";
    private string[] _lines = [];
    private string? _parsedArgs;
    private string _pattern = "", _searchPath = "", _commandArg = "", _argLines = "";

    public ToolRowViewModel(ToolItem item) : base(item) => OnUpdated();

    /// <summary>A shell command (bash, shell): its command line and output show in a console block.</summary>
    public bool IsCommand => Name is "bash" or "shell";

    public static string DisplayNameOf(string tool)
    {
        switch (tool)
        {
            case "bash": return "Bash";
            case "shell": return "Shell";
            case "grep": return Loc.T("Search");
            case "glob": return Loc.T("Find files");
            case "ast_grep": return Loc.T("Search code");
            case "ast_edit": return Loc.T("Edit code");
            case "todo": return Loc.T("Update todos");
            case "eval": return Loc.T("Run code");
            case "lsp": return "LSP";
        }
        // An MCP server's tool (mcp__server__tool): the tool, then its server
        if (tool.StartsWith("mcp__", StringComparison.Ordinal) && tool.Split("__", 3) is [_, var server, var name] && name.Length > 0)
            return $"{Capitalized(name)} ({server})";
        return Capitalized(tool);

        static string Capitalized(string id)
        {
            var words = id.Replace('_', ' ').Replace('-', ' ').Trim();
            return words.Length == 0 ? Loc.T("Tool") : char.ToUpperInvariant(words[0]) + words[1..];
        }
    }

    /// <summary>
    /// The verb on a call's line (Codex: "Ran", "Read", "Searched"), in the tense of its state; a file change says what
    /// it did to its one file (a move within its folder is a rename). Tools without a verb of their own keep their name
    /// ("Create issue (github)").
    /// </summary>
    public static string VerbOf(string tool, bool running, FileChangeKind? change = null, bool rename = false)
    {
        string V(string doing, string done) => Loc.T(running ? doing : done);
        switch (tool)
        {
            case "bash" or "shell": return V("Running", "Ran");
            case "read": return V("Reading", "Read");
            case "grep" or "ast_grep": return V("Searching", "Searched");
            case "glob" or "find": return V("Finding files", "Found files");
            case "ls": return V("Listing", "Listed");
            case "lsp": return V("Looking up", "Looked up");
            case "edit" or "ast_edit" or "notebook_edit" or "write" or "create" or "write_file":
                return change switch
                {
                    FileChangeKind.Created => V("Creating", "Created"),
                    FileChangeKind.Deleted => V("Deleting", "Deleted"),
                    FileChangeKind.Moved => rename ? V("Renaming", "Renamed") : V("Moving", "Moved"),
                    _ => tool is "write" or "create" or "write_file" ? V("Writing", "Wrote") : V("Editing", "Edited"),
                };
            case "todo": return V("Updating todos", "Updated todos");
            case "task": return V("Delegating", "Delegated");
            case "ask": return V("Asking", "Asked");
            case "eval": return V("Running code", "Ran code");
            case "fetch": return V("Fetching", "Fetched");
            case "web_search": return V("Searching the web", "Searched the web");
            case "browser": return V("Browsing", "Browsed");
            default: return DisplayNameOf(tool);
        }
    }

    partial void UpdateFiles(); // ToolRowViewModel.Files.cs: the files the call changed

    protected override void OnUpdated()
    {
        var t = (ToolItem)Model;
        Name = t.Name;
        Summary = t.Summary;
        Diff = t.Diff;
        UpdateFiles();
        (Added, Removed) = Ombra.App.Controls.DiffView.Stats(t.Diff);
        OnPropertyChanged(nameof(IsCommand));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(HasStats));
        OnPropertyChanged(nameof(ShowDiff));
        OnPropertyChanged(nameof(ShowRowStats));
        OnPropertyChanged(nameof(AddedText));
        OnPropertyChanged(nameof(RemovedText));
        // A file change is shown as it happens (Claude Code), once; the user can fold it away
        if (HasDiff && !_diffOpened)
        {
            _diffOpened = true;
            IsExpanded = true;
        }
        StatusText = t.Status switch
        {
            ToolStatus.Running => Loc.T("running…"),
            ToolStatus.Succeeded => Loc.T("done"),
            ToolStatus.Failed => Loc.T("failed"),
            ToolStatus.Skipped => Loc.T("skipped"),
            _ => Loc.T("interrupted"),
        };
        Output = t.Output ?? "";
        IsRunning = t.Status == ToolStatus.Running;
        IsFailed = t.Status == ToolStatus.Failed;
        IsDone = t.Status == ToolStatus.Succeeded;
        // Set aside for a message that came first: the stopped mark, not a failure (omp retries it if still needed)
        IsInterrupted = t.Status is ToolStatus.Interrupted or ToolStatus.Skipped;
        TookText = t.Took is { TotalSeconds: >= 0.1 } took && t.Status != ToolStatus.Running ? TurnEndRowViewModel.Duration(took) : "";
        ParseArgs(t.Args);
        Verb = CurrentVerb();
        ArgText = ChangeKind == FileChangeKind.Moved && MovedFrom is { } from ? $"{from} → {FilePath}"
            : Name is "grep" or "ast_grep" && _pattern.Length > 0 ? (_searchPath.Length > 0 ? Loc.F("{0} in {1}", _pattern, _searchPath) : _pattern)
            : Summary.StartsWith("$ ", StringComparison.Ordinal) ? Summary[2..] : Summary;
        // omp ends a command's output with its own notes, which the line says its own way: the time, and an exit chip
        var (shown, exit, timedOut) = IsCommand ? SplitNotices(Output) : (Output, null, false);
        _shownOutput = shown;
        _lines = OutputLines(shown);
        FailureText = !IsFailed ? null : exit is { } code ? Loc.F("exit {0}", code) : timedOut ? Loc.T("timed out") : Loc.T("failed");
        ExitText = exit is { } exitCode ? Loc.F("Exit code {0}", exitCode) : "";
        Kind = StepKinds.Of(this);
        Command = _commandArg.Length > 0 ? _commandArg.Trim() : ArgText;
        Tooltip = TooltipText();
        UpdateResult(t);
        OnPropertyChanged(nameof(ShowArg));
        OnFailureTextChanged(FailureText);
        OnPropertyChanged(nameof(HasMeta));
        OnPropertyChanged(nameof(ShowOutput));
        OnPropertyChanged(nameof(ShownOutput));
        OnPropertyChanged(nameof(CanExpand));
        OnPropertyChanged(nameof(HasExitText));
    }

    /// <summary>The call's arguments a line words better than its summary does: a search's pattern and where, the whole
    /// command (the summary joins its lines). Parsed when they change; cut-off arguments leave the summary.</summary>
    private void ParseArgs(string args)
    {
        if (args == _parsedArgs) return;
        _parsedArgs = args;
        (_pattern, _searchPath, _commandArg, _argLines) = ("", "", "", "");
        if (args.Length == 0) return;
        try
        {
            using var doc = JsonDocument.Parse(args);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            _pattern = Str(root, "pattern");
            _searchPath = Str(root, "path");
            _commandArg = Str(root, "command");
            _argLines = ArgLines(root);
        }
        catch (JsonException)
        {
        }

        static string Str(JsonElement o, string key) => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    /// <summary>A tool's plain arguments as "key: value" lines (a few, each on one line and cut), for its tooltip.</summary>
    private static string ArgLines(JsonElement args)
    {
        const int MaxLines = 8, MaxValue = 160;
        var lines = new List<string>(MaxLines);
        foreach (var p in args.EnumerateObject())
        {
            var value = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? "",
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => p.Value.GetRawText(),
                _ => "",
            };
            value = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (value.Length == 0) continue;
            if (value.Length > MaxValue) value = value[..(MaxValue - 1)] + "…";
            lines.Add($"{p.Name.Replace('_', ' ')}: {value}");
            if (lines.Count == MaxLines) break;
        }
        return string.Join('\n', lines);
    }

    /// <summary>What the line's tooltip says (see <see cref="Tooltip"/>).</summary>
    private string? TooltipText()
    {
        if (IsCommand) return ShowConsole || Command.Length == 0 ? null : Command;
        // A file, a move, a search: the line's own words, whole
        if (HasFile || Name is "grep" or "ast_grep" || _argLines.Length == 0) return ArgText.Length > 0 ? ArgText : null;
        return _argLines;
    }

    /// <summary>
    /// omp's notes at the end of a command's output: "Wall time: 0.02 seconds", "Command exited with code 1" (omp's bash
    /// tool), "exit code 1" (a command the user ran, ConversationState.Session), "[Command timed out after 30 seconds]".
    /// </summary>
    [GeneratedRegex(@"^(?:Wall time: [\d.]+ seconds|Command exited with code (?<code>-?\d+)|exit code (?<code>-?\d+)|\[?Command timed out[^\]]*\]?)$")]
    private static partial Regex OmpNotice();

    /// <summary>The output without omp's notes at its end, the exit code they gave, and whether the command timed out.</summary>
    internal static (string Output, int? Exit, bool TimedOut) SplitNotices(string output)
    {
        var lines = output.Replace("\r", "").Split('\n');
        var n = lines.Length;
        int? exit = null;
        var timedOut = false;
        while (n > 0)
        {
            var line = lines[n - 1].Trim();
            if (line.Length > 0)
            {
                if (OmpNotice().Match(line) is not { Success: true } m) break;
                if (m.Groups["code"].Success && int.TryParse(m.Groups["code"].Value, out var code)) exit ??= code;
                else if (line.Contains("timed out", StringComparison.Ordinal)) timedOut = true;
            }
            n--;
        }
        return (string.Join('\n', lines, 0, n), exit, timedOut);
    }

    /// <summary>
    /// What the row says beyond its line. A command: its console (newest lines while it runs, then the first ones and how
    /// many more). A read or search: what it found, in a few words at the line's end. A file change: nothing (its +N −M and
    /// the change itself say it). Anything else: its first output lines, or the error, under the line.
    /// </summary>
    private void UpdateResult(ToolItem t)
    {
        var lines = _lines;
        var more = 0;
        var isOutput = false;
        var result = "";
        var meta = "";
        if (IsCommand) { } // its output is in its Shell card
        else if (IsRunning) (result, isOutput) = (string.Join('\n', lines.Skip(Math.Max(0, lines.Length - PreviewLines))), true);
        else if (HasDiff && !IsFailed) { }
        else if (t.ResultNote is { } note) meta = note.StartsWith("Found ", StringComparison.Ordinal) ? note[6..] : note;
        else if (Name == "read" && !IsFailed && lines.Length > 0) meta = lines.Length == 1 ? Loc.T("1 line") : Loc.F("{0} lines", lines.Length);
        else if (Name == "ls" && !IsFailed && lines.Length > 0) meta = lines.Length == 1 ? Loc.T("1 entry") : Loc.F("{0} entries", lines.Length);
        else if (lines.Length > 0)
        {
            result = string.Join('\n', lines.Take(PreviewLines));
            more = Math.Max(0, lines.Length - PreviewLines);
            // A command's output is mono; what a subagent, the todo list or a question answered is words
            isOutput = Name is not ("task" or "todo" or "ask");
        }
        if (IsInterrupted && t.StartedAt is not null) meta = Loc.T("Interrupted");
        if (t.Status == ToolStatus.Skipped) (result, more, meta) = ("", 0, SkippedFor(t.Output ?? ""));
        Result = result;
        ResultIsOutput = isOutput;
        MoreLines = more;
        Meta = meta;
        UpdateConsole();
        OnPropertyChanged(nameof(ShowResult));
        OnPropertyChanged(nameof(ShowMore));
        OnPropertyChanged(nameof(MoreText));
    }

    /// <summary>Why omp set the call aside, from its result ("Skipped due to a queued user message. …", agent-loop.ts).</summary>
    internal static string SkippedFor(string output) => output switch
    {
        _ when output.Contains("queued user message", StringComparison.Ordinal) => Loc.T("Skipped for your queued message"),
        _ when output.Contains("background completion", StringComparison.Ordinal) => Loc.T("Skipped: a background job finished first"),
        _ when output.Contains("peer interrupt", StringComparison.Ordinal) => Loc.T("Skipped for another agent’s message"),
        _ when output.Contains("parent steering", StringComparison.Ordinal) => Loc.T("Skipped for the parent agent’s message"),
        _ when output.Contains("system advisory", StringComparison.Ordinal) => Loc.T("Skipped for an omp advisory"),
        _ => Loc.T("Skipped for a message that came first"),
    };

    /// <summary>The Shell card's output: open, all of it; running, its newest lines (shown live).</summary>
    private void UpdateConsole()
    {
        var lines = _lines;
        ConsoleOutput = !IsCommand ? ""
            : IsExpanded ? string.Join('\n', lines)
            : IsRunning ? string.Join('\n', lines.Skip(Math.Max(0, lines.Length - PreviewLines)))
            : "";
        OnPropertyChanged(nameof(ShowConsole));
        OnPropertyChanged(nameof(ShowConsoleOutput));
        OnPropertyChanged(nameof(CommandMaxLines));
    }

    /// <summary>The output's lines without carriage returns and without the blank lines around it.</summary>
    private static string[] OutputLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r", "").Split('\n');
        var n = lines.Length;
        while (n > 0 && lines[n - 1].Trim().Length == 0) n--;
        var start = 0;
        while (start < n && lines[start].Trim().Length == 0) start++;
        return lines[start..n];
    }

    public bool HasDiff => Diff is { Length: > 0 };
    private bool _diffOpened;
    public int Added { get; private set; }
    public int Removed { get; private set; }
    public bool HasStats => Added + Removed > 0;
    public string AddedText => Added > 0 ? $"+{Added}" : "";
    public string RemovedText => Removed > 0 ? $"−{Removed}" : "";
    public bool ShowDiff => HasDiff && IsExpanded;
    /// <summary>+4 −1 on the line while the change is folded; open, the change's own header says it (not twice).</summary>
    public bool ShowRowStats => HasStats && !ShowDiff;
    /// <summary>The failure chip right after the verb when nothing follows it on the line, else at the line's end after
    /// the argument.</summary>
    public bool ShowFailureAfterVerb => FailureText is not null && !ShowArg;
    public bool ShowFailureAtEnd => FailureText is not null && ShowArg;
    public bool HasMeta => Meta.Length > 0;
    /// <summary>The argument on the line: the path, the pattern, the command.</summary>
    public bool ShowArg => ArgText.Length > 0;
    /// <summary>The output without omp's notes, as the opened row shows it.</summary>
    public string ShownOutput => _shownOutput;

    /// <summary>The kind of step (a command, a read, a search, an edit…): its icon, and the words of a group of steps.</summary>
    [ObservableProperty] private StepKind _kind;
    /// <summary>"Exit code 127" at the foot of the Shell card, when omp said how the command exited.</summary>
    [ObservableProperty] private string _exitText = "";
    public bool HasExitText => ExitText.Length > 0;

    /// <summary>
    /// A command is one line ("Ran make", Codex); opened, its Shell card: "$ command", all its output, how it exited.
    /// While it runs the card shows its newest lines live.
    /// </summary>
    public bool ShowConsole => IsCommand && (IsExpanded || IsRunning);
    public bool ShowConsoleOutput => ShowConsole && ConsoleOutput.Length > 0;
    /// <summary>The command's lines in the card: a few while it runs, all of them once opened.</summary>
    public int CommandMaxLines => IsExpanded ? 0 : CommandLines;

    /// <summary>The whole output of a tool other than a command, opened (a command opens in its console).</summary>
    public bool ShowOutput => IsExpanded && !HasDiff && !IsCommand && _shownOutput.Length > 0;
    /// <summary>There is more than the line says: the change, a command's Shell card (its output, why it failed, or a
    /// command longer than the line shows), the output.</summary>
    public bool CanExpand => HasDiff || (IsCommand
        ? _lines.Length > 0 || IsFailed || Command.Contains('\n') || Command.Length > 120
        : _shownOutput.Trim().Length > 0);
    /// <summary>The words or lines under the call, unless the whole output or the change is open in its place.</summary>
    public bool ShowResult => Result.Length > 0 && !ShowOutput && !ShowDiff;
    public bool ShowMore => MoreLines > 0 && ShowResult;
    public string MoreText => MoreLines == 1 ? Loc.T("… +1 line") : Loc.F("… +{0} lines", MoreLines);

    /// <summary>"… +N lines": the whole output.</summary>
    [RelayCommand]
    private void ShowAll() => IsExpanded = true;

    /// <summary>The open output's own header (a console's, an output's, a changed file's): folds the call back to its line.</summary>
    [RelayCommand]
    private void Collapse() => IsExpanded = false;

    partial void OnIsExpandedChanged(bool value)
    {
        UpdateConsole();
        OnPropertyChanged(nameof(ShowOutput));
        OnPropertyChanged(nameof(ShowDiff));
        OnPropertyChanged(nameof(ShowRowStats));
        OnPropertyChanged(nameof(ShowResult));
        OnPropertyChanged(nameof(ShowMore));
    }

    partial void OnMetaChanged(string value) => OnPropertyChanged(nameof(HasMeta));

    partial void OnAwaitsApprovalChanged(bool value) => Verb = CurrentVerb();

    private string CurrentVerb() => AwaitsApproval && IsRunning ? Loc.T("Waiting for approval")
        : VerbOf(Name, IsRunning, ChangeKind, Changes.Count == 1 && Changes[0].IsRename);

    partial void OnFailureTextChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowFailureAfterVerb));
        OnPropertyChanged(nameof(ShowFailureAtEnd));
    }
}

public sealed partial class NoticeRowViewModel(NoticeItem item) : RowViewModel(item)
{
    public string Text => ((NoticeItem)Model).Text;
    public bool IsError => ((NoticeItem)Model).Level == NoticeLevel.Error;
    public bool IsWarning => ((NoticeItem)Model).Level == NoticeLevel.Warning;

    protected override void OnUpdated()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsWarning));
    }
}

/// <summary>
/// The end of a run, kept in the conversation: how long it took and whether it was stopped (said by the turn's
/// "Worked for…" line at its top, TurnFolds.cs), and Codex's card of the files the turn changed under its last reply —
/// "Changed 3 files +40 −2", "Review changes", then a row per file with its +N −M, each opening in the Files pane.
/// </summary>
public sealed partial class TurnEndRowViewModel(TurnEndItem item) : RowViewModel(item)
{
    /// <summary>The files the card lists before "Show N more files".</summary>
    public const int ShownFileCount = 3;

    /// <summary>Every changed file is listed (the card shows <see cref="ShownFileCount"/> until asked for the rest).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownFiles), nameof(HasMoreFiles))]
    private bool _isFilesOpen;

    /// <summary>How long the run took (from its start to its end, as the client saw them).</summary>
    public TimeSpan Took => ((TurnEndItem)Model).Took;
    /// <summary>The run was stopped before it ended.</summary>
    public bool Interrupted => ((TurnEndItem)Model).Interrupted;

    /// <summary>The files the turn changed, in the order it first changed them (from its tool rows' changes).</summary>
    public IReadOnlyList<TurnFileViewModel> Files { get; private set; } = [];
    public bool HasFiles => Files.Count > 0;
    /// <summary>The rows under the card's header: one changed file is the header alone.</summary>
    public bool HasFileRows => Files.Count > 1;
    /// <summary>The rows the card shows: the first few until opened; one more than that is shown rather than folded.</summary>
    public IReadOnlyList<TurnFileViewModel> ShownFiles => IsFilesOpen || Files.Count <= ShownFileCount + 1 ? Files : [.. Files.Take(ShownFileCount)];
    public bool HasMoreFiles => HasFileRows && !IsFilesOpen && Files.Count > ShownFileCount + 1;
    public string MoreFilesText => Loc.Plural(Files.Count - ShownFileCount, "Show {0} more file", "Show {0} more files");
    /// <summary>"Edited app.py" for one file, "Edited 3 files" for more.</summary>
    public string Title => Files.Count == 1 ? Loc.F("Edited {0}", Files[0].Name) : Loc.Plural(Files.Count, "Edited {0} file", "Edited {0} files");
    public string AddedText => $"+{Files.Sum(f => f.Added)}";
    public string RemovedText => $"−{Files.Sum(f => f.Removed)}";
    /// <summary>The card as a screen reader says it.</summary>
    public string Text => HasFiles ? $"{Title} {AddedText} {RemovedText}" : "";
    /// <summary>The files were taken from the turn's rows (once, when the turn had ended: MainViewModel.MarkTurnEnds).</summary>
    internal bool FilesKnown { get; private set; }

    /// <summary>The files the turn's tool calls changed (<paramref name="tools"/>, in order): <see cref="ChangedFiles"/>.</summary>
    internal void SetFiles(IEnumerable<ToolRowViewModel> tools)
    {
        FilesKnown = true;
        Files = ChangedFiles(tools);
        OnPropertyChanged(nameof(Files));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasFileRows));
        OnPropertyChanged(nameof(ShownFiles));
        OnPropertyChanged(nameof(HasMoreFiles));
        OnUpdated();
    }

    [RelayCommand]
    private void ShowAllFiles() => IsFilesOpen = true;

    /// <summary>
    /// The files <paramref name="tools"/> (in order) changed, one entry per file: its changes added up, a move followed
    /// to the new path, a file created and then changed still new, one created and deleted again left out. Failed calls
    /// changed nothing. In the order the files were first changed, or (<paramref name="latestFirst"/>) the file changed
    /// last first — the session's recent files (the right panel's new-tab page).
    /// </summary>
    internal static IReadOnlyList<TurnFileViewModel> ChangedFiles(IEnumerable<ToolRowViewModel> tools, bool latestFirst = false)
    {
        var files = new List<TurnFile>();
        var step = 0;
        foreach (var t in tools)
        {
            if (t.IsFailed) continue;
            foreach (var c in t.Changes)
            {
                step++;
                var (added, removed) = Ombra.App.Controls.DiffView.Stats(c.Diff);
                var line = Ombra.App.Controls.DiffView.FirstChangedLine(c.Diff);
                var was = files.Find(f => f.Path == (c.MovedFrom ?? c.Path)) ?? files.Find(f => f.Path == c.Path);
                if (was is null)
                {
                    files.Add(new TurnFile { Path = c.Path, Kind = c.Kind, MovedFrom = c.MovedFrom, Added = added, Removed = removed, Line = line, Last = step });
                    continue;
                }
                if (was.Kind == FileChangeKind.Created && c.Kind == FileChangeKind.Deleted)
                {
                    files.Remove(was);
                    continue;
                }
                was.Kind = (was.Kind, c.Kind) switch
                {
                    (FileChangeKind.Created, not FileChangeKind.Deleted) => FileChangeKind.Created,
                    (_, FileChangeKind.Deleted) => FileChangeKind.Deleted,
                    (FileChangeKind.Moved, _) or (_, FileChangeKind.Moved) => FileChangeKind.Moved,
                    (_, var now) => now,
                };
                if (c.MovedFrom is not null && was.Kind == FileChangeKind.Moved) was.MovedFrom ??= c.MovedFrom;
                was.Path = c.Path;
                was.Added += added;
                was.Removed += removed;
                was.Line ??= line;
                was.Last = step;
            }
        }
        if (latestFirst) files.Sort((a, b) => b.Last.CompareTo(a.Last));
        return [.. files.Select(f => new TurnFileViewModel(f.Path, f.Kind, f.MovedFrom, f.Added, f.Removed, f.Line))];
    }

    private sealed class TurnFile
    {
        public required string Path;
        public FileChangeKind Kind;
        public string? MovedFrom;
        public int Added, Removed;
        public int? Line;
        /// <summary>When the file was changed last (the count of changes before it).</summary>
        public int Last;
    }

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 10 ? Loc.F("{0}s", $"{t.TotalSeconds:0.0}".Replace(',', '.'))
        : t.TotalMinutes < 1 ? Loc.F("{0}s", (int)t.TotalSeconds)
        : t.TotalHours < 1 ? Loc.F("{0}m {1:00}s", (int)t.TotalMinutes, t.Seconds)
        : Loc.F("{0}h {1:00}m", (int)t.TotalHours, t.Minutes);

    /// <summary>A turn's length in whole units, as Codex's "Worked for" line says it: "17s", "1m 18s", "1h 5m".</summary>
    public static string TurnDuration(TimeSpan t) =>
        t.TotalMinutes < 1 ? Loc.F("{0}s", Math.Max(1, (int)Math.Round(t.TotalSeconds)))
        : t.TotalHours < 1 ? Loc.F("{0}m {1}s", (int)t.TotalMinutes, t.Seconds)
        : Loc.F("{0}h {1}m", (int)t.TotalHours, t.Minutes);

    protected override void OnUpdated()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Took));
        OnPropertyChanged(nameof(Interrupted));
    }
}

/// <summary>A file on a turn's list of changes: its path, what happened to it ("New", "Deleted", "Renamed", "Moved"), +N −M.</summary>
public sealed class TurnFileViewModel(string path, FileChangeKind kind, string? movedFrom, int added, int removed, int? line)
{
    public string Path { get; } = path;
    public FileChangeKind Kind { get; } = kind;
    public string KindText { get; } = kind switch
    {
        FileChangeKind.Created => Loc.T("New"),
        FileChangeKind.Deleted => Loc.T("Deleted"),
        FileChangeKind.Moved => movedFrom is not null && FileChange.SameFolder(movedFrom, path) ? Loc.T("Renamed") : Loc.T("Moved"),
        _ => "",
    };
    public bool HasKind => KindText.Length > 0;
    public bool IsCreated => Kind == FileChangeKind.Created;
    public bool IsDeleted => Kind == FileChangeKind.Deleted;
    public bool IsMoved => Kind == FileChangeKind.Moved;
    /// <summary>Where a moved file was: "from src/util.py".</summary>
    public string MovedFromText { get; } = movedFrom is null ? "" : Loc.F("from {0}", movedFrom);
    public int Added { get; } = added;
    public int Removed { get; } = removed;
    public string AddedText { get; } = added > 0 ? $"+{added}" : "";
    public string RemovedText { get; } = removed > 0 ? $"−{removed}" : "";
    /// <summary>The file's name and the folder it is in, for a list of files (the new-tab page's Recent).</summary>
    public string Name { get; } = System.IO.Path.GetFileName(path);
    public string Folder { get; } = System.IO.Path.GetDirectoryName(path) ?? "";
    /// <summary>The folder with its separator, before the name in a row ("src/views/" in grey, then the name).</summary>
    public string FolderPrefix => Folder.Length > 0 ? Folder.Replace('\\', '/') + "/" : "";
    /// <summary>The file at its first change, for the Files pane (a deleted file opens to say it is gone).</summary>
    public Ombra.App.Controls.FileLink Link { get; } = new(path, kind == FileChangeKind.Deleted ? null : line);
    public string OpenName => Loc.F("Open {0}", Path);
}

public sealed partial class CommandOutputRowViewModel(CommandOutputItem item) : RowViewModel(item)
{
    public string Text => ((CommandOutputItem)Model).Text;

    /// <summary>
    /// A line that marks a point in the conversation rather than output to read ("Compaction complete. Tokens: 48210 ->
    /// 9120 …"): a divider saying what happened, not a block of raw text under the card that already reported it.
    /// </summary>
    public string? Marker => Text.Trim() is var t && !t.Contains('\n') && t.StartsWith("Compaction complete", StringComparison.Ordinal)
        && SessionOutputs.ParseCompactEnd(t) is { Ok: true } r
            ? r is { TokensBefore: { } b, TokensAfter: { } a } ? Loc.F("Conversation compacted · {0} → {1} tokens", SessionOutputs.Tokens(b), SessionOutputs.Tokens(a)) : Loc.T("Conversation compacted")
            : null;
    public bool IsMarker => Marker is not null;

    protected override void OnUpdated()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Marker));
        OnPropertyChanged(nameof(IsMarker));
    }
}

/// <summary>
/// A message between agents (omp's IRC): who wrote to whom on a card's bar, its first lines under it; opened, the whole
/// message as Markdown. Folded at first: it is addressed to an agent, and the reply that follows says what came of it.
/// </summary>
public sealed partial class AgentMessageRowViewModel(AgentMessageItem item) : RowViewModel(item)
{
    private AgentMessageItem Item => (AgentMessageItem)Model;
    public string From => Item.From;
    public string Text => Item.Text;

    /// <summary>"→ omp" for a message to this chat's agent, "→ Name" between two others; "· reply" for an answer.</summary>
    public string Route => (Item.To is { } to ? $"→ {to}" : Loc.T("→ omp")) + (Item.IsReply ? " · " + Loc.T("reply") : "");

    /// <summary>The words in one run (the view shows two lines of it), with Markdown's marks taken out.</summary>
    public string Preview => string.Join(' ', Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => MarkdownMarks().Replace(line, "")));

    public string AccessibleName => Loc.F("Message from {0}", From);

    [ObservableProperty] private bool _isExpanded;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    protected override void OnUpdated()
    {
        OnPropertyChanged(nameof(From));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Route));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(AccessibleName));
    }

    [GeneratedRegex(@"\*\*|__|`{1,3}|^#{1,6} ")]
    private static partial Regex MarkdownMarks();
}
