using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>One transcript row. Rows are updated in place so the list keeps its containers while text streams in.</summary>
public abstract partial class RowViewModel : ObservableObject
{
    public long Key { get; }
    public TranscriptItem Model { get; private set; }

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
        _ => throw new ArgumentOutOfRangeException(nameof(item)),
    };
}

public sealed partial class UserRowViewModel(UserItem item) : RowViewModel(item)
{
    [ObservableProperty] private string _text = item.Text;
    [ObservableProperty] private bool _confirmed = item.Confirmed;
    [ObservableProperty] private int _imageCount = item.ImageCount;

    public string ImagesText => ImageCount == 1 ? "1 image" : $"{ImageCount} images";
    public bool HasImages => ImageCount > 0;

    private const int FoldLines = 8, FoldChars = 600;

    /// <summary>A long message (pasted text, page comments) shows its start until opened (Claude Code folds them too).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText), nameof(FoldLabel), nameof(HasOwnText))]
    private bool _isUnfolded;

    /// <summary>Where the page comments sent with this message begin (-1: none). They show as a chip, not as text.</summary>
    private int CommentsAt
    {
        get
        {
            if (Text.StartsWith(PreviewViewModel.PromptMarker, StringComparison.Ordinal)) return 0;
            var i = Text.IndexOf("\n\n" + PreviewViewModel.PromptMarker, StringComparison.Ordinal);
            return i < 0 ? -1 : i + 2;
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
            return own == PreviewViewModel.CommentsOnlyLine ? "" : own;
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
            var what = n == 1 ? "1 comment" : $"{n} comments";
            return hosts.Count == 1 ? $"{what} on {hosts[0]}" : hosts.Count > 1 ? $"{what} on {hosts.Count} pages" : what + " on the page";
        }
    }

    public bool HasOwnText => OwnText.Length > 0 || IsUnfolded;
    public bool IsLong => HasComments || OwnText.Length > FoldChars + 200 || OwnText.Count(c => c == '\n') > FoldLines + 4;
    public string DisplayText => IsUnfolded ? Text : HasComments ? OwnText : IsLong ? Head(OwnText) + "…" : OwnText;
    public string FoldLabel => IsUnfolded ? (HasComments ? "Hide what was sent" : "Show less") : HasComments ? "Show what was sent" : "Show more";

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
        OnPropertyChanged(nameof(FoldLabel));
    }

    protected override void OnUpdated()
    {
        var u = (UserItem)Model;
        Text = u.Text;
        Confirmed = u.Confirmed;
        ImageCount = u.ImageCount;
        OnPropertyChanged(nameof(ImagesText));
        OnPropertyChanged(nameof(HasImages));
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
    /// <summary>The last reply of a finished turn: it carries the message actions (copy) below it, like Claude Code.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    private bool _isTurnEnd;

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
    /// <summary>"Thinking…" while it thinks, then "Thought for 4s" (Claude Code); loaded history just says "Thinking".</summary>
    public string ThinkingLabel => IsThinkingLive ? "Thinking…"
        : _thinkingFrom is { } from && _thinkingTo is { } to ? $"Thought for {Math.Max(1, (int)Math.Round((to - from).TotalSeconds))}s"
        : "Thinking";
    public bool ShowActions => HasFooter || (IsTurnEnd && HasText && !Streaming);

    /// <summary>Raised when the user copies the message; the view puts it on the clipboard.</summary>
    public static event Action<string>? CopyRequested;

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke(Text);

    protected override void OnUpdated()
    {
        var a = (AssistantItem)Model;
        Text = a.Text;
        Thinking = a.Thinking;
        Streaming = a.Streaming;
        // An abort is the user's own stop, not an error, although omp also sets errorMessage ("Interrupted by user").
        Footer = a.StopReason == "aborted" ? "Interrupted" : null;
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

public sealed partial class ToolRowViewModel : RowViewModel
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _diff;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isDone;
    /// <summary>How long it ran ("2.3s"), once it ended.</summary>
    [ObservableProperty] private string _tookText = "";

    public ToolRowViewModel(ToolItem item) : base(item) => OnUpdated();

    protected override void OnUpdated()
    {
        var t = (ToolItem)Model;
        Title = $"{t.Name}  {t.Args}";
        Name = t.Name;
        Summary = t.Summary;
        Diff = t.Diff;
        (Added, Removed) = OmpGui.App.Controls.DiffView.Stats(t.Diff);
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(HasStats));
        OnPropertyChanged(nameof(ShowDiff));
        OnPropertyChanged(nameof(ShowRowStats));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(AddedText));
        OnPropertyChanged(nameof(RemovedText));
        // A file change is shown as it happens (Claude Code), once; the user can fold it away
        if (HasDiff && !_diffOpened)
        {
            _diffOpened = true;
            IsExpanded = true;
        }
        OnPropertyChanged(nameof(ShowOutput));
        StatusText = t.Status switch
        {
            ToolStatus.Running => "running…",
            ToolStatus.Succeeded => "done",
            ToolStatus.Failed => "failed",
            _ => "interrupted",
        };
        Output = t.Output ?? "";
        IsRunning = t.Status == ToolStatus.Running;
        TookText = t.Took is { } took && t.Status != ToolStatus.Running ? TurnEndRowViewModel.Duration(took) : "";
        Tail = IsRunning ? LastLines(Output, 4) : "";
        OnPropertyChanged(nameof(ShowTail));
        IsFailed = t.Status == ToolStatus.Failed;
        IsDone = t.Status == ToolStatus.Succeeded;
        // Failures stay open so the error is visible without a click.
        if (IsFailed) IsExpanded = true;
    }

    /// <summary>The newest output lines of a running tool, under its line (Claude Code shows a running command's output live).</summary>
    [ObservableProperty] private string _tail = "";
    public bool ShowTail => IsRunning && !IsExpanded && Tail.Length > 0;

    private static string LastLines(string text, int n)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToArray();
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - n)));
    }

    public bool HasDiff => Diff is { Length: > 0 };
    private bool _diffOpened;
    public int Added { get; private set; }
    public int Removed { get; private set; }
    public bool HasStats => Added + Removed > 0;
    public string AddedText => Added > 0 ? $"+{Added}" : "";
    public string RemovedText => Removed > 0 ? $"−{Removed}" : "";
    /// <summary>The file a file tool acts on (its summary is the path), for the change card's header.</summary>
    public string FilePath => Summary;
    public bool ShowDiff => HasDiff && IsExpanded;
    /// <summary>+4 −1 on the line while the change is folded; open, the change's own header says it (not twice).</summary>
    public bool ShowRowStats => HasStats && !ShowDiff;
    /// <summary>The diff says it better than the tool's text output for edits.</summary>
    public bool ShowOutput => IsExpanded && !HasDiff && Output.Length > 0;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowOutput));
        OnPropertyChanged(nameof(ShowDiff));
        OnPropertyChanged(nameof(ShowRowStats));
        OnPropertyChanged(nameof(ShowTail));
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

/// <summary>"Worked for 1m 12s · 6 tools · 2 files changed": the end of a run, kept in the conversation.</summary>
public sealed partial class TurnEndRowViewModel(TurnEndItem item) : RowViewModel(item)
{
    public string Text
    {
        get
        {
            var e = (TurnEndItem)Model;
            var parts = new List<string> { (e.Interrupted ? "Stopped after " : "Worked for ") + Duration(e.Took) };
            if (e.Tools > 0) parts.Add(e.Tools == 1 ? "1 tool" : $"{e.Tools} tools");
            if (e.FilesChanged > 0) parts.Add(e.FilesChanged == 1 ? "1 file changed" : $"{e.FilesChanged} files changed");
            return string.Join(" · ", parts);
        }
    }

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 10 ? $"{t.TotalSeconds:0.0}s".Replace(',', '.')
        : t.TotalMinutes < 1 ? $"{(int)t.TotalSeconds}s"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s"
        : $"{(int)t.TotalHours}h {t.Minutes:00}m";

    protected override void OnUpdated() => OnPropertyChanged(nameof(Text));
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
            ? r is { TokensBefore: { } b, TokensAfter: { } a } ? $"Conversation compacted · {SessionOutputs.Tokens(b)} → {SessionOutputs.Tokens(a)} tokens" : "Conversation compacted"
            : null;
    public bool IsMarker => Marker is not null;

    protected override void OnUpdated()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Marker));
        OnPropertyChanged(nameof(IsMarker));
    }
}
