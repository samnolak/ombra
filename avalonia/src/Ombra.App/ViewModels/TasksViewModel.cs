using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>One line of a subagent's own conversation, shown when its row is opened.</summary>
public sealed record SubagentMessageViewModel(string Role, string Text)
{
    public string Label => Role switch
    {
        "user" => Loc.T("Instructions"),
        "assistant" => Loc.T("Subagent"),
        "tool" => Loc.T("Tool call"),
        "result" => Loc.T("Result"),
        "error" => Loc.T("Failed"),
        _ => Role,
    };
    public bool IsTool => Role is "tool" or "result" or "error";
}

/// <summary>A subagent (omp's task tool) in the Background tasks pane: what it does, how far, how long.</summary>
public sealed partial class SubagentRowViewModel : ObservableObject
{
    private readonly TasksViewModel _owner;
    private SubagentInfo _info;
    private CancellationTokenSource? _messagesRead;
    private bool _messagesRequested;
    private bool _inViewport;
    private bool _retired;
    private long _nextByte;
    private string? _transcriptFile;

    public SubagentRowViewModel(TasksViewModel owner, SubagentInfo info)
    {
        _owner = owner;
        _info = info;
        Update(info);
    }

    public string Id => _info.Id;
    public SubagentInfo Info => _info;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _agent = "";
    [ObservableProperty] private string _taskText = "";
    [ObservableProperty] private bool _hasTask;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _activityText = "";
    [ObservableProperty] private bool _hasActivity;
    [ObservableProperty] private string _metaText = "";
    [ObservableProperty] private bool _hasMeta;
    [ObservableProperty] private string _outputText = "";
    [ObservableProperty] private bool _hasOutput;
    [ObservableProperty] private string _toolsText = "";
    [ObservableProperty] private bool _hasTools;
    [ObservableProperty] private string _retryNote = "";
    [ObservableProperty] private bool _hasRetryNote;
    [ObservableProperty] private bool _isBackground;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoadingMessages;
    [ObservableProperty] private string _messagesNote = "";
    [ObservableProperty] private bool _hasMessagesNote;
    [ObservableProperty] private bool _messagesLoaded;

    public ObservableCollection<SubagentMessageViewModel> Messages { get; } = [];

    public string AccessibleName => $"{Title} — {StatusText}";

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTaskFull));
        OnPropertyChanged(nameof(ShowTaskShort));
        if (!value) CancelMessages();
        else _ = PollMessagesAsync();
    }

    public bool ShowTaskFull => HasTask && IsExpanded;
    public bool ShowTaskShort => HasTask && !IsExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Opts into a live, bounded conversation preview while this expanded card is on screen.</summary>
    [RelayCommand]
    private Task LoadMessagesAsync()
    {
        _messagesRequested = true;
        return PollMessagesAsync();
    }

    internal void SetInViewport(bool visible)
    {
        if (_inViewport == visible) return;
        _inViewport = visible;
        if (!visible) CancelMessages();
        else _ = PollMessagesAsync();
    }

    internal void CancelMessages(bool retire = false)
    {
        _retired |= retire;
        var pending = _messagesRead;
        _messagesRead = null;
        pending?.Cancel();
        IsLoadingMessages = false;
    }

    private void ResetMessages()
    {
        CancelMessages();
        _nextByte = 0;
        _transcriptFile = null;
        Messages.Clear();
        MessagesLoaded = false;
        MessagesNote = "";
        HasMessagesNote = false;
    }

    internal async Task PollMessagesAsync()
    {
        if (_retired || !_messagesRequested || !IsExpanded || !_inViewport || !_owner.CanReadMessages || _messagesRead is not null) return;
        using var pending = new CancellationTokenSource();
        _messagesRead = pending;
        var session = _owner.MessageSession;
        var sessionFile = session?.Snapshot().SessionFile;
        var id = Id;
        var path = _info.SessionFile;
        IsLoadingMessages = !MessagesLoaded;
        try
        {
            var (page, error) = await _owner.ReadMessagesAsync(id, _nextByte, pending.Token);
            if (pending.IsCancellationRequested || !ReferenceEquals(_messagesRead, pending) || id != Id || path != _info.SessionFile
                || session != _owner.MessageSession || sessionFile != session?.Snapshot().SessionFile || !_owner.CanReadMessages) return;
            if (error is not null || page is null)
            {
                SetMessagesNote(Loc.T("Could not read its messages: ") + (error ?? Loc.T("omp did not answer")));
                return; // retain both the preview and the last successfully consumed byte
            }
            if (_transcriptFile is { } previous && previous != page.SessionFile)
            {
                Messages.Clear();
                MessagesLoaded = false;
                _nextByte = 0;
                _transcriptFile = page.SessionFile;
                SetMessagesNote("");
                // A cursor into the old file cannot be used to read the new file, even when it happens to fit.
                if (page.FromByte != 0) return;
            }
            if (page.Reset) Messages.Clear();
            _transcriptFile = page.SessionFile;
            foreach (var message in page.Messages)
            {
                if (Messages.Count == SubagentMessagesPage.PreviewLimit) Messages.RemoveAt(0);
                Messages.Add(new SubagentMessageViewModel(message.Role, Ombra.App.Controls.VisibleText.FromUntrustedOutput(message.Text)));
            }
            _nextByte = page.NextByte; // runtime counts UTF-8 bytes of complete lines, never .NET characters
            MessagesLoaded = true;
            SetMessagesNote(Messages.Count == 0 ? Loc.T("No messages yet.") : "");
        }
        catch (OperationCanceledException) { } // collapse, hide, switch and shutdown are not read failures
        finally
        {
            if (ReferenceEquals(_messagesRead, pending))
            {
                _messagesRead = null;
                IsLoadingMessages = false;
            }
        }
    }

    private void SetMessagesNote(string note)
    {
        MessagesNote = Ombra.App.Controls.VisibleText.FromUntrustedOutput(note);
        HasMessagesNote = MessagesNote.Length > 0;
    }

    public void Update(SubagentInfo info)
    {
        var identityChanged = _info.Id != info.Id || _info.SessionFile != info.SessionFile;
        if (identityChanged) ResetMessages();
        _info = info;
        // Its description, else the name it was given (omp's id, the last part of a nested one): omp's task text is the
        // rendered subagent prompt ("Complete assignment thoroughly: …"); what the parent asked is the assignment.
        var name = info.Id[(info.Id.LastIndexOf('.') + 1)..];
        var title = info.Description is { Length: > 0 } d ? d : name.Length > 0 ? name : info.Agent;
        Title = Ombra.App.Controls.VisibleText.FromUntrustedOutput(Shorten(title.Split('\n', 2)[0].Trim(), 120));
        Agent = Ombra.App.Controls.VisibleText.FromUntrustedOutput(info.Agent);
        var task = info.Assignment is { Length: > 0 } assignment ? assignment : info.Task;
        TaskText = task is { Length: > 0 } t ? Ombra.App.Controls.VisibleText.FromUntrustedOutput(Shorten(t.Trim(), 2000)) : "";
        HasTask = TaskText.Length > 0 && TaskText != Title;
        IsBackground = info.Detached;
        IsRunning = info.IsRunning;
        IsDone = info.Status == "completed";
        IsFailed = info.Status == "failed";
        StatusText = Ombra.App.Controls.VisibleText.FromUntrustedOutput(info.Status switch
        {
            "running" => Loc.T("Running"),
            "pending" => Loc.T("Waiting"),
            "completed" => Loc.T("Done"),
            "failed" => Loc.T("Failed"),
            "aborted" => Loc.T("Stopped"),
            "ended" => Loc.T("Ended"),
            var s => char.ToUpperInvariant(s[0]) + s[1..],
        });
        // Running: what it does now. Ended: what it last said it did — for a failure, its last words (the reason)
        ActivityText = Ombra.App.Controls.VisibleText.FromUntrustedOutput(info.IsRunning
            ? info.CurrentTool is { Length: > 0 } tool ? tool + (info.CurrentToolArgs is { Length: > 0 } a ? ": " + OneLine(a) : "") : info.LastIntent ?? Loc.T("Working…")
            : info.Status is "failed" or "aborted" && info.RecentOutput is [var last, ..] ? OneLine(last)
            : info.LastIntent ?? "");
        HasActivity = ActivityText.Length > 0;
        var meta = new List<string>();
        if (info.ToolCount > 0) meta.Add(info.ToolCount == 1 ? Loc.T("1 tool call") : Loc.F("{0} tool calls", info.ToolCount));
        if (info.Tokens > 0) meta.Add(TokensText(info.Tokens));
        if (info.Model is { Length: > 0 } model) meta.Add(model);
        MetaText = Ombra.App.Controls.VisibleText.FromUntrustedOutput(string.Join(" · ", meta));
        HasMeta = MetaText.Length > 0;
        // omp lists the newest line first; read top to bottom here
        OutputText = Ombra.App.Controls.VisibleText.FromUntrustedOutput(string.Join("\n", info.RecentOutput.Reverse()));
        HasOutput = OutputText.Length > 0;
        ToolsText = Ombra.App.Controls.VisibleText.FromUntrustedOutput(string.Join("\n", info.RecentTools.Select(t => t.Args.Length > 0 ? $"{t.Tool}: {t.Args}" : t.Tool)));
        HasTools = ToolsText.Length > 0;
        RetryNote = Ombra.App.Controls.VisibleText.FromUntrustedOutput(info.RetryNote ?? "");
        HasRetryNote = RetryNote.Length > 0;
        OnPropertyChanged(nameof(ShowTaskFull));
        OnPropertyChanged(nameof(ShowTaskShort));
        OnPropertyChanged(nameof(AccessibleName));
        Tick();
        if (identityChanged) _ = PollMessagesAsync();
    }

    /// <summary>The running time: omp's own at the last progress, plus the time since (live while it runs).</summary>
    public void Tick()
    {
        ElapsedText = TasksViewModel.Duration(_info.RunningTime.Elapsed);
    }

    internal static string TokensText(long tokens) => tokens >= 1000
        ? Loc.F("{0}k tokens", (tokens / 1000.0).ToString("0.#", CultureInfo.CurrentCulture))
        : Loc.F("{0} tokens", tokens);

    private static string OneLine(string s) => Shorten(string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim(), 200);

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..GraphemeText.PrefixLength(s, max, trimEnd: true)] + "…";
}

/// <summary>
/// The Background tasks pane: omp's subagents (live over RPC, kept after they end) and background jobs
/// (<c>get_jobs</c>, read while the pane is open, every few seconds and after each run). The count of what runs goes
/// to the header's ⋯ menu and summary.
/// </summary>
public sealed partial class TasksViewModel : ObservableObject
{
    private static readonly TimeSpan JobsInterval = TimeSpan.FromSeconds(5);
    private readonly MainViewModel? _owner;
    private IReadOnlyList<SubagentInfo>? _lastSubagents;
    private DispatcherTimer? _timer;
    private DateTimeOffset _jobsReadAt;
    private bool _shutdown;
    private SessionController? _subagentsController;
    private string? _subagentsSessionFile;

    internal SessionController? MessageSession => _owner?.Session;
    internal bool CanReadMessages => IsShown && !_shutdown && _owner is not { IsClosing: true };

    public TasksViewModel(MainViewModel? owner = null) => _owner = owner;

    /// <summary>Running first (in the order they started), then the finished ones, the latest first.</summary>
    public ObservableCollection<SubagentRowViewModel> Subagents { get; } = [];
    public ObservableCollection<JobRowViewModel> Jobs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunning))]
    private int _runningCount;

    public bool HasRunning => RunningCount > 0;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasSubagents;
    [ObservableProperty] private bool _hasJobs;
    /// <summary>This omp does not report subagents (its RPC lacks the commands).</summary>
    [ObservableProperty] private bool _isUnsupported;
    [ObservableProperty] private string _jobsError = "";
    [ObservableProperty] private bool _hasJobsError;
    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>The pane is on screen: it reads jobs and ticks running times.</summary>
    public bool IsShown
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (value) Start();
            else Stop();
        }
    }

    private void Start()
    {
        if (_shutdown) return;
        foreach (var row in Subagents) _ = row.PollMessagesAsync();
        _ = RefreshAsync();
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnTick());
        _timer.Start();
    }

    private void Stop()
    {
        _timer?.Stop();
        foreach (var row in Subagents) row.CancelMessages();
        foreach (var row in Jobs) row.CancelOutput();
        CancelJobsRead();
    }

    /// <summary>The window is closing: stop the timer and cancel any in-flight preview read.</summary>
    internal void Shutdown()
    {
        _shutdown = true;
        Stop();
    }

    private void OnTick()
    {
        if (!CanReadMessages)
        {
            Stop(); // the window is closing: nothing left to read
            return;
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var row in Subagents)
        {
            if (row.IsRunning) row.Tick();
            _ = row.PollMessagesAsync();
        }
        foreach (var row in Jobs)
        {
            if (row.IsRunning) row.Tick(now);
            _ = row.PollOutputAsync();
        }
        if (now - _jobsReadAt >= JobsInterval) _ = RefreshJobsAsync();
    }

    /// <summary>Asks omp what runs now: subagents and jobs.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_owner is not null) await _owner.RefreshSubagentsAsync();
        await RefreshJobsAsync();
    }

    /// <summary>Opening the header's ⋯ menu or summary refreshes the jobs count (subagents arrive by themselves).</summary>
    public void OnMenuOpened() => _ = RefreshJobsAsync();

    /// <summary>A run ended: its background jobs may have started or finished (the count in the header too).</summary>
    public void OnRunEnded() => _ = RefreshJobsAsync();

    /// <summary>Another chat is shown: the jobs listed were the other chat's omp's; this one's are read again.</summary>
    internal void OnSessionShown()
    {
        // Apply normally detects the switch first. The controller identity also covers chats with no file yet.
        if (_subagentsController != MessageSession) ResetSubagents();
        ResetJobs();
        JobsError = "";
        HasJobsError = false;
        Recount();
        CancelJobsRead(); // a read still under way was for the other chat: cancelled, its answer dropped
        _ = RefreshJobsAsync();
    }

    private bool _subagentsReported;

    public void Apply(SessionSnapshot s)
    {
        if (_subagentsController != MessageSession || _subagentsSessionFile != s.SessionFile)
        {
            ResetSubagents();
            _subagentsController = MessageSession;
            _subagentsSessionFile = s.SessionFile;
        }
        SetSupport(s.SubagentsSupported);
        if (ReferenceEquals(s.Subagents, _lastSubagents)) return;
        _lastSubagents = s.Subagents;
        ApplySubagents(s.Subagents);
    }

    private void ResetSubagents()
    {
        foreach (var row in Subagents) row.CancelMessages(retire: true);
        Subagents.Clear();
        _lastSubagents = null;
    }

    /// <summary>Whether omp reports its subagents (null: not known yet).</summary>
    public void SetSupport(bool? supported)
    {
        IsUnsupported = supported == false;
        if (_subagentsReported == (supported == true)) return;
        _subagentsReported = supported == true;
        ShowJobs(DateTimeOffset.UtcNow);
    }

    public void ApplySubagents(IReadOnlyList<SubagentInfo> list)
    {
        var order = list.Where(a => a.IsRunning).OrderBy(a => a.FirstSeenAt).ThenBy(a => a.Index)
            .Concat(list.Where(a => !a.IsRunning).OrderByDescending(a => a.EndedAt ?? a.UpdatedAt).ThenByDescending(a => a.Index)).ToList();
        var rows = Subagents.ToDictionary(r => r.Id);
        for (var i = 0; i < order.Count; i++)
        {
            var info = order[i];
            if (rows.TryGetValue(info.Id, out var row))
            {
                if (!ReferenceEquals(row.Info, info)) row.Update(info);
                var at = Subagents.IndexOf(row);
                if (at != i) Subagents.Move(at, i);
            }
            else Subagents.Insert(i, new SubagentRowViewModel(this, info));
        }
        while (Subagents.Count > order.Count)
        {
            Subagents[^1].CancelMessages(retire: true);
            Subagents.RemoveAt(Subagents.Count - 1);
        }
        Recount();
    }

    private void Recount()
    {
        HasSubagents = Subagents.Count > 0;
        HasJobs = Jobs.Count > 0;
        var agents = Subagents.Count(r => r.IsRunning);
        CanStopAll = agents >= 2;
        RunningCount = agents + Jobs.Count(j => j.IsRunning);
        var finished = Subagents.Count - agents + Jobs.Count(j => !j.IsRunning);
        Summary = RunningCount == 0 && finished == 0 ? "" : RunningCount > 0
            ? finished > 0 ? Loc.F("{0} running · {1} finished", RunningCount, finished) : Loc.F("{0} running", RunningCount)
            : Loc.F("{0} finished", finished);
        IsEmpty = !HasSubagents && !HasJobs;
    }

    internal Task<(SubagentMessagesPage? Page, string? Error)> ReadMessagesAsync(string id, long fromByte, CancellationToken ct) =>
        _owner?.GetSubagentMessagesAsync(id, fromByte, ct) ?? Task.FromResult<(SubagentMessagesPage?, string?)>((null, Loc.T("omp is not running")));

    /// <summary>"8s", "1m 05s", "1h 02m".</summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? Loc.F("{0}h {1:00}m", (int)t.TotalHours, t.Minutes)
            : t.TotalMinutes >= 1 ? Loc.F("{0}m {1:00}s", (int)t.TotalMinutes, t.Seconds)
            : Loc.F("{0}s", (int)t.TotalSeconds);
    }
}
