using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>One line of a subagent's own conversation, shown when its row is opened.</summary>
public sealed record SubagentMessageViewModel(string Role, string Text)
{
    public string Label => Role switch
    {
        "user" => "Instructions",
        "assistant" => "Subagent",
        "tool" => "Tool call",
        "result" => "Result",
        "error" => "Failed",
        _ => Role,
    };
    public bool IsTool => Role is "tool" or "result" or "error";
}

/// <summary>A subagent (omp's task tool) in the Background tasks pane: what it does, how far, how long.</summary>
public sealed partial class SubagentRowViewModel : ObservableObject
{
    private readonly TasksViewModel _owner;
    private SubagentInfo _info;

    public SubagentRowViewModel(TasksViewModel owner, SubagentInfo info, DateTimeOffset now)
    {
        _owner = owner;
        _info = info;
        Update(info, now);
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
    }

    public bool ShowTaskFull => HasTask && IsExpanded;
    public bool ShowTaskShort => HasTask && !IsExpanded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Reads the subagent's own conversation from omp (<c>get_subagent_messages</c>).</summary>
    [RelayCommand]
    private async Task LoadMessagesAsync()
    {
        if (IsLoadingMessages) return;
        IsLoadingMessages = true;
        MessagesNote = "";
        HasMessagesNote = false;
        try
        {
            var (messages, error) = await _owner.ReadMessagesAsync(Id);
            Messages.Clear();
            foreach (var m in messages) Messages.Add(new SubagentMessageViewModel(m.Role, m.Text));
            MessagesLoaded = error is null;
            MessagesNote = error is not null ? "Could not read its messages: " + error : messages.Count == 0 ? "No messages yet." : "";
            HasMessagesNote = MessagesNote.Length > 0;
        }
        finally
        {
            IsLoadingMessages = false;
        }
    }

    public void Update(SubagentInfo info, DateTimeOffset now)
    {
        _info = info;
        var firstLine = info.Task?.Split('\n', 2)[0].Trim();
        Title = info.Description is { Length: > 0 } d ? d : firstLine is { Length: > 0 } f ? Shorten(f, 120) : info.Agent;
        Agent = info.Agent;
        TaskText = info.Task is { Length: > 0 } t ? Shorten(t.Trim(), 2000) : "";
        HasTask = TaskText.Length > 0 && TaskText != Title;
        IsBackground = info.Detached;
        IsRunning = info.IsRunning;
        IsDone = info.Status == "completed";
        IsFailed = info.Status == "failed";
        StatusText = info.Status switch
        {
            "running" => "Running",
            "pending" => "Waiting",
            "completed" => "Done",
            "failed" => "Failed",
            "aborted" => "Stopped",
            "ended" => "Ended",
            var s => char.ToUpperInvariant(s[0]) + s[1..],
        };
        // Running: what it does now. Ended: what it last said it did — for a failure, its last words (the reason)
        ActivityText = info.IsRunning
            ? info.CurrentTool is { Length: > 0 } tool ? tool + (info.CurrentToolArgs is { Length: > 0 } a ? ": " + OneLine(a) : "") : info.LastIntent ?? "Working…"
            : info.Status is "failed" or "aborted" && info.RecentOutput is [var last, ..] ? OneLine(last)
            : info.LastIntent ?? "";
        HasActivity = ActivityText.Length > 0;
        var meta = new List<string>();
        if (info.ToolCount > 0) meta.Add(info.ToolCount == 1 ? "1 tool call" : $"{info.ToolCount} tool calls");
        if (info.Tokens > 0) meta.Add(TokensText(info.Tokens));
        if (info.Model is { Length: > 0 } model) meta.Add(model);
        MetaText = string.Join(" · ", meta);
        HasMeta = MetaText.Length > 0;
        // omp lists the newest line first; read top to bottom here
        OutputText = string.Join("\n", info.RecentOutput.Reverse());
        HasOutput = OutputText.Length > 0;
        ToolsText = string.Join("\n", info.RecentTools.Select(t => t.Args.Length > 0 ? $"{t.Tool}: {t.Args}" : t.Tool));
        HasTools = ToolsText.Length > 0;
        RetryNote = info.RetryNote ?? "";
        HasRetryNote = RetryNote.Length > 0;
        OnPropertyChanged(nameof(ShowTaskFull));
        OnPropertyChanged(nameof(ShowTaskShort));
        OnPropertyChanged(nameof(AccessibleName));
        Tick(now);
    }

    /// <summary>The running time: omp's own at the last progress, plus the time since (live while it runs).</summary>
    public void Tick(DateTimeOffset now)
    {
        var took = _info.IsRunning
            ? (_info.Duration ?? TimeSpan.Zero) + (now - (_info.Duration is null ? _info.FirstSeenAt : _info.UpdatedAt))
            : _info.Duration ?? (_info.EndedAt ?? now) - _info.FirstSeenAt;
        ElapsedText = TasksViewModel.Duration(took);
    }

    internal static string TokensText(long tokens) => tokens >= 1000
        ? string.Create(CultureInfo.InvariantCulture, $"{tokens / 1000.0:0.#}k tokens")
        : string.Create(CultureInfo.InvariantCulture, $"{tokens} tokens");

    private static string OneLine(string s) => Shorten(string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim(), 200);

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}

/// <summary>A background job (omp's <c>/jobs</c>: long bash commands, async tasks) as the pane shows it.</summary>
public sealed record JobRowViewModel(string Id, string Type, string Status, string Label, string Ago, bool IsRunningSection)
{
    public bool IsRunning => IsRunningSection && Status is "running";
    public bool IsDone => Status is "completed";
    public bool IsFailed => Status is "failed";
    public string Title => Label.Length > 0 ? Label : $"{Type} {Id}";
    public string StatusText => Status switch
    {
        "running" => "Running",
        "completed" => "Done",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        { Length: > 0 } s => char.ToUpperInvariant(s[0]) + s[1..],
        _ => "Unknown",
    };
    /// <summary>"bash · running for 12s" / "task · started 5m ago".</summary>
    public string Detail => IsRunningSection ? $"{Type} · running for {Ago}" : $"{Type} · started {Ago} ago";
    public string AccessibleName => $"{Title} — {StatusText}";
}

/// <summary>
/// The Background tasks pane: omp's subagents (live over RPC, kept after they end) and background jobs (omp's
/// <c>/jobs</c>, read while the pane is open, every few seconds and after each run). The count of what runs goes to
/// the Views menu.
/// </summary>
public sealed partial class TasksViewModel : ObservableObject
{
    private static readonly TimeSpan JobsInterval = TimeSpan.FromSeconds(5);
    private readonly MainViewModel? _owner;
    private IReadOnlyList<SubagentInfo>? _lastSubagents;
    private DispatcherTimer? _timer;
    private DateTimeOffset _jobsReadAt;
    private bool _readingJobs;

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
    /// <summary><c>/jobs</c> printed something the client does not recognize: shown as omp printed it.</summary>
    [ObservableProperty] private string _jobsText = "";
    [ObservableProperty] private bool _hasJobsText;
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
        _ = RefreshAsync();
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnTick());
        _timer.Start();
    }

    private void Stop() => _timer?.Stop();

    /// <summary>The window is closing.</summary>
    internal void Shutdown() => Stop();

    private void OnTick()
    {
        if (_owner is { IsClosing: true })
        {
            Stop(); // the window is closing: nothing left to read
            return;
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var r in Subagents.Where(r => r.IsRunning)) r.Tick(now);
        if (now - _jobsReadAt >= JobsInterval) _ = RefreshJobsAsync();
    }

    /// <summary>Asks omp what runs now: subagents and jobs.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_owner is not null) await _owner.RefreshSubagentsAsync();
        await RefreshJobsAsync();
    }

    /// <summary>Opening the Views menu refreshes the jobs count (subagents arrive by themselves).</summary>
    public void OnMenuOpened() => _ = RefreshJobsAsync();

    /// <summary>A run ended: its background jobs may have started or finished (the count in the Views menu too).</summary>
    public void OnRunEnded() => _ = RefreshJobsAsync();

    /// <summary>
    /// Reads omp's <c>/jobs</c> — only from an omp that lists it among its commands (omp lists the builtins it runs
    /// over RPC): anything else would take the text for a message to the model.
    /// </summary>
    public async Task RefreshJobsAsync()
    {
        if (_owner is not { CanRunOmpCommands: true, OmpListsJobs: true } owner || _readingJobs) return;
        _readingJobs = true;
        _jobsReadAt = DateTimeOffset.UtcNow;
        try
        {
            var r = await owner.RunOmpCommandAsync("/jobs", TimeSpan.FromSeconds(20));
            _jobsReadAt = DateTimeOffset.UtcNow;
            if (!r.Ok)
            {
                JobsError = "Could not read the background jobs: " + (r.Error ?? "omp did not answer");
                HasJobsError = true;
                return;
            }
            JobsError = "";
            HasJobsError = false;
            ApplyJobs(r.Output);
        }
        finally
        {
            _readingJobs = false;
        }
    }

    /// <summary>What <c>/jobs</c> printed: rows when it is omp's usual listing, else the text as it is.</summary>
    public void ApplyJobs(string output)
    {
        _parsedJobs = ParseJobs(output);
        JobsText = _parsedJobs is null ? output.Trim() : "";
        HasJobsText = JobsText.Length > 0;
        ShowJobs();
    }

    private List<JobRowViewModel>? _parsedJobs;
    private bool _subagentsReported;

    /// <summary>
    /// An async task job is the subagents it runs, which omp reports and the pane lists above: it is not listed (or
    /// counted) twice. An omp that does not report subagents keeps it among the jobs.
    /// </summary>
    private void ShowJobs()
    {
        Jobs.Clear();
        foreach (var j in _parsedJobs ?? []) if (!_subagentsReported || j.Type != "task") Jobs.Add(j);
        Recount();
    }

    public void Apply(SessionSnapshot s)
    {
        SetSupport(s.SubagentsSupported);
        if (ReferenceEquals(s.Subagents, _lastSubagents)) return;
        _lastSubagents = s.Subagents;
        ApplySubagents(s.Subagents, DateTimeOffset.UtcNow);
    }

    /// <summary>Whether omp reports its subagents (null: not known yet).</summary>
    public void SetSupport(bool? supported)
    {
        IsUnsupported = supported == false;
        if (_subagentsReported == (supported == true)) return;
        _subagentsReported = supported == true;
        ShowJobs();
    }

    public void ApplySubagents(IReadOnlyList<SubagentInfo> list, DateTimeOffset now)
    {
        var order = list.Where(a => a.IsRunning).OrderBy(a => a.FirstSeenAt).ThenBy(a => a.Index)
            .Concat(list.Where(a => !a.IsRunning).OrderByDescending(a => a.EndedAt ?? a.UpdatedAt).ThenByDescending(a => a.Index)).ToList();
        var rows = Subagents.ToDictionary(r => r.Id);
        for (var i = 0; i < order.Count; i++)
        {
            var info = order[i];
            if (rows.TryGetValue(info.Id, out var row))
            {
                if (!ReferenceEquals(row.Info, info)) row.Update(info, now);
                var at = Subagents.IndexOf(row);
                if (at != i) Subagents.Move(at, i);
            }
            else Subagents.Insert(i, new SubagentRowViewModel(this, info, now));
        }
        while (Subagents.Count > order.Count) Subagents.RemoveAt(Subagents.Count - 1);
        Recount();
    }

    private void Recount()
    {
        HasSubagents = Subagents.Count > 0;
        HasJobs = Jobs.Count > 0;
        var agents = Subagents.Count(r => r.IsRunning);
        RunningCount = agents + Jobs.Count(j => j.IsRunning);
        var finished = Subagents.Count - agents + Jobs.Count(j => !j.IsRunning);
        Summary = RunningCount == 0 && finished == 0 ? "" : RunningCount > 0
            ? finished > 0 ? $"{RunningCount} running · {finished} finished" : $"{RunningCount} running"
            : $"{finished} finished";
        IsEmpty = !HasSubagents && !HasJobs && !HasJobsText;
    }

    internal Task<(IReadOnlyList<SubagentMessage> Messages, string? Error)> ReadMessagesAsync(string id) =>
        _owner?.GetSubagentMessagesAsync(id) ?? Task.FromResult<(IReadOnlyList<SubagentMessage>, string?)>(([], "omp is not running"));

    // omp's /jobs (slash-commands/builtin-session.ts): "  [id] type (status) — 12s" then the label, indented.
    [GeneratedRegex(@"^\s*\[(?<id>[^\]]+)\]\s+(?<type>\S+)\s+\((?<status>[^)]*)\)\s+[—–-]\s+(?<ago>.+?)\s*$")]
    private static partial Regex JobLine();

    /// <summary>omp's <c>/jobs</c> listing as rows; null when the text is not in that shape.</summary>
    public static List<JobRowViewModel>? ParseJobs(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("No background jobs", StringComparison.Ordinal)) return [];
        if (!trimmed.StartsWith("Background Jobs", StringComparison.Ordinal)) return null;
        var list = new List<JobRowViewModel>();
        var running = true;
        JobRowViewModel? job = null;
        foreach (var raw in trimmed.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim() is "Running Jobs") { running = true; continue; }
            if (line.Trim() is "Recent Jobs") { running = false; continue; }
            if (JobLine().Match(line) is { Success: true } m)
            {
                if (job is not null) list.Add(job);
                job = new JobRowViewModel(m.Groups["id"].Value, m.Groups["type"].Value, m.Groups["status"].Value.Trim(), "", m.Groups["ago"].Value, running);
                continue;
            }
            if (job is { Label.Length: 0 } && line.Trim().Length > 0 && raw.StartsWith("   ", StringComparison.Ordinal))
                job = job with { Label = line.Trim() };
        }
        if (job is not null) list.Add(job);
        return list;
    }

    /// <summary>"8s", "1m 05s", "1h 02m".</summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}h {t.Minutes:00}m")
            : t.TotalMinutes >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}m {t.Seconds:00}s")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalSeconds}s");
    }
}
