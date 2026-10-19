using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// A background job (omp's <c>get_jobs</c>: a long bash command, an async task) in the Background tasks pane: Stop
/// (<c>cancel_job</c>), its output on request (<c>get_job_output</c> from a byte cursor, a bounded tail kept here),
/// and "waiting for input" when omp sees it quiet on what looks like a prompt.
/// </summary>
public sealed partial class JobRowViewModel : ObservableObject
{
    /// <summary>Characters of output the row keeps; older lines give way to new ones.</summary>
    public const int OutputLimit = 64 * 1024;

    private readonly TasksViewModel? _owner;
    private JobInfo _info;
    private CancellationTokenSource? _outputRead;
    private long? _nextByte;
    private bool _trimmed;
    private bool _finalRead;
    private bool _retired;

    public JobRowViewModel(TasksViewModel? owner, JobInfo info, DateTimeOffset now)
    {
        _owner = owner;
        _info = info;
        Update(info, now);
    }

    public string Id => _info.Id;
    public string Type => _info.Type;
    public JobInfo Info => _info;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isWaitingForInput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    private bool _isStopping;

    /// <summary>Why the last Stop failed; shown on the row until the next attempt.</summary>
    [ObservableProperty] private string _actionError = "";
    [ObservableProperty] private bool _hasActionError;

    [ObservableProperty] private bool _isOutputShown;
    [ObservableProperty] private bool _isLoadingOutput;
    [ObservableProperty] private string _outputText = "";
    [ObservableProperty] private bool _hasOutput;
    [ObservableProperty] private string _outputNote = "";
    [ObservableProperty] private bool _hasOutputNote;

    public bool CanStop => IsRunning && !IsStopping;
    public string OutputToggleText => IsOutputShown ? Loc.T("Hide output") : Loc.T("Show output");
    public string AccessibleName => $"{Title} — {StatusText}";

    public void Update(JobInfo info, DateTimeOffset now)
    {
        var settled = _info.IsRunning && !info.IsRunning;
        _info = info;
        Title = Ombra.App.Controls.VisibleText.FromUntrustedOutput(info.Label.Length > 0 ? info.Label : $"{info.Type} {info.Id}");
        IsRunning = info.IsRunning;
        IsDone = info.Status is "completed";
        IsFailed = info.Status is "failed";
        IsWaitingForInput = info.IsRunning && info.WaitingForInput;
        StatusText = info.Status switch
        {
            "running" => info.WaitingForInput ? Loc.T("Waiting for input") : Loc.T("Running"),
            "completed" => Loc.T("Done"),
            "failed" => info.ExitCode is { } code ? Loc.F("Failed (exit {0})", code) : Loc.T("Failed"),
            "cancelled" => Loc.T("Cancelled"),
            { Length: > 0 } s => Ombra.App.Controls.VisibleText.FromUntrustedOutput(char.ToUpperInvariant(s[0]) + s[1..]),
            _ => Loc.T("Unknown"),
        };
        OnPropertyChanged(nameof(AccessibleName));
        Tick(now);
        // The last bytes it printed before it ended are read once more
        if (settled) _ = PollOutputAsync();
    }

    /// <summary>"bash · running for 12s" / "bash · started 5m ago".</summary>
    public void Tick(DateTimeOffset now) => Detail = Ombra.App.Controls.VisibleText.FromUntrustedOutput(_info.IsRunning
        ? Loc.F("{0} · running for {1}", _info.Type, TasksViewModel.Duration(now - _info.StartedAt))
        : Loc.F("{0} · started {1} ago", _info.Type, TasksViewModel.Duration(now - _info.StartedAt)));

    partial void OnIsOutputShownChanged(bool value)
    {
        OnPropertyChanged(nameof(OutputToggleText));
        if (value) _ = PollOutputAsync();
        else CancelOutput();
    }

    [RelayCommand]
    private void ToggleOutput() => IsOutputShown = !IsOutputShown;

    [RelayCommand]
    public async Task StopAsync()
    {
        if (!CanStop || _owner is null) return;
        IsStopping = true;
        SetActionError("");
        try
        {
            if (await _owner.CancelJobAsync(Id) is { } error) SetActionError(Loc.T("Could not stop it: ") + error);
        }
        finally
        {
            IsStopping = false;
        }
    }

    internal void CancelOutput(bool retire = false)
    {
        _retired |= retire;
        var pending = _outputRead;
        _outputRead = null;
        pending?.Cancel();
        IsLoadingOutput = false;
    }

    /// <summary>Reads what the job printed since the last read (its tail the first time), while the output is shown.</summary>
    internal async Task PollOutputAsync()
    {
        if (_retired || !IsOutputShown || _owner is not { CanReadMessages: true } owner || _outputRead is not null) return;
        if (!_info.IsRunning && _finalRead) return; // an ended job prints no more
        using var pending = new CancellationTokenSource();
        _outputRead = pending;
        var session = owner.MessageSession;
        var wasRunning = _info.IsRunning;
        IsLoadingOutput = !HasOutput;
        try
        {
            var (page, error) = await owner.ReadJobOutputAsync(Id, _nextByte, pending.Token);
            if (pending.IsCancellationRequested || !ReferenceEquals(_outputRead, pending) || session != owner.MessageSession || !owner.CanReadMessages) return;
            if (error is not null || page is null)
            {
                SetOutputNote(Loc.T("Could not read its output: ") + (error ?? Loc.T("omp did not answer")));
                return; // the cursor stays: the same bytes are asked again
            }
            Apply(page);
            if (!wasRunning) _finalRead = true;
        }
        catch (OperationCanceledException) { } // hide, switch and shutdown are not read failures
        finally
        {
            if (ReferenceEquals(_outputRead, pending))
            {
                _outputRead = null;
                IsLoadingOutput = false;
            }
        }
    }

    private void Apply(JobOutputPage page)
    {
        var text = Ombra.App.Controls.VisibleText.FromUntrustedOutput(page.Text);
        if (page.Snapshot) OutputText = text;
        else
        {
            // The first read is the tail; a skipped or restarted cursor loses what was between
            if (_nextByte is null ? page.FromByte > 0 : page.SkippedBytes > 0 || page.Reset) _trimmed = true;
            OutputText = page.Reset ? text : OutputText + text;
            _nextByte = page.NextByte;
        }
        if (OutputText.Length > OutputLimit)
        {
            var cut = OutputText.Length - OutputLimit;
            var line = OutputText.IndexOf('\n', cut);
            OutputText = OutputText[(line >= 0 && line + 1 < OutputText.Length ? line + 1 : cut)..];
            _trimmed = true;
        }
        HasOutput = OutputText.Length > 0;
        IsWaitingForInput = IsRunning && page.WaitingForInput;
        SetOutputNote(!HasOutput ? (IsRunning ? Loc.T("No output yet.") : Loc.T("It printed nothing."))
            : _trimmed ? Loc.T("Showing the latest output; earlier lines are not kept here.") : "");
    }

    private void SetOutputNote(string note)
    {
        OutputNote = Ombra.App.Controls.VisibleText.FromUntrustedOutput(note);
        HasOutputNote = OutputNote.Length > 0;
    }

    private void SetActionError(string error)
    {
        ActionError = Ombra.App.Controls.VisibleText.FromUntrustedOutput(error);
        HasActionError = ActionError.Length > 0;
    }
}

public sealed partial class TasksViewModel
{
    private IReadOnlyList<JobInfo> _jobInfos = [];
    /// <summary>The list read under way, if any: only it may finish the read; switching chats or hiding cancels it.</summary>
    private CancellationTokenSource? _jobsRead;
    private bool _readJobsAgain;

    /// <summary>
    /// Reads omp's background jobs (<c>get_jobs</c>) for the chat shown now. <paramref name="again"/>: a read already
    /// under way may predate a change (a Stop), so one more follows it.
    /// </summary>
    public async Task RefreshJobsAsync(bool again = false)
    {
        if (_shutdown || _owner is not { CanRunOmpCommands: true } owner) return;
        if (_jobsRead is not null)
        {
            _readJobsAgain |= again;
            return;
        }
        using var read = new CancellationTokenSource();
        _jobsRead = read;
        _jobsReadAt = DateTimeOffset.UtcNow;
        // The chat asked: another one may be shown by the time omp answers, and its jobs are not these
        var session = owner.Session;
        try
        {
            var (jobs, error) = await owner.GetJobsAsync(read.Token);
            if (read.IsCancellationRequested || !ReferenceEquals(_jobsRead, read) || owner.Session != session) return;
            _jobsReadAt = DateTimeOffset.UtcNow;
            if (jobs is null)
            {
                JobsError = Loc.T("Could not read the background jobs: ") + (error ?? Loc.T("omp did not answer"));
                HasJobsError = true;
                return;
            }
            JobsError = "";
            HasJobsError = false;
            ApplyJobs(jobs, DateTimeOffset.UtcNow);
        }
        finally
        {
            if (ReferenceEquals(_jobsRead, read))
            {
                _jobsRead = null;
                if (_readJobsAgain)
                {
                    _readJobsAgain = false;
                    _ = RefreshJobsAsync();
                }
            }
        }
    }

    /// <summary>Cancels the list read under way (its answer is dropped) and any read queued behind it.</summary>
    private void CancelJobsRead()
    {
        var pending = _jobsRead;
        _jobsRead = null;
        _readJobsAgain = false;
        pending?.Cancel();
    }

    /// <summary>The jobs omp listed: rows are kept by id, so an open output keeps its cursor across reads.</summary>
    public void ApplyJobs(IReadOnlyList<JobInfo> jobs, DateTimeOffset now)
    {
        _jobInfos = jobs;
        ShowJobs(now);
    }

    /// <summary>
    /// An async task job is the subagent it runs, which omp reports and the pane lists above: it is not listed (or
    /// counted) twice. An omp that does not report subagents keeps it among the jobs.
    /// </summary>
    private void ShowJobs(DateTimeOffset now)
    {
        var shown = _jobInfos.Where(j => !_subagentsReported || j.Type != "task").ToList();
        var rows = Jobs.ToDictionary(r => r.Id);
        for (var i = 0; i < shown.Count; i++)
        {
            if (rows.Remove(shown[i].Id, out var row))
            {
                if (!ReferenceEquals(row.Info, shown[i])) row.Update(shown[i], now);
                var at = Jobs.IndexOf(row);
                if (at != i) Jobs.Move(at, i);
            }
            else Jobs.Insert(i, new JobRowViewModel(this, shown[i], now));
        }
        foreach (var gone in rows.Values)
        {
            gone.CancelOutput(retire: true);
            Jobs.Remove(gone);
        }
        Recount();
    }

    private void ResetJobs()
    {
        _jobInfos = [];
        foreach (var row in Jobs) row.CancelOutput(retire: true);
        Jobs.Clear();
    }

    /// <summary>Stops one job, then reads the list again so its row shows how it ended. Null on success.</summary>
    internal async Task<string?> CancelJobAsync(string id)
    {
        if (_owner is null) return Loc.T("omp is not running");
        var error = await _owner.CancelJobAsync(id);
        if (error is null) await RefreshJobsAsync(again: true);
        return error;
    }

    internal Task<(JobOutputPage? Page, string? Error)> ReadJobOutputAsync(string id, long? fromByte, CancellationToken ct) =>
        _owner?.GetJobOutputAsync(id, fromByte, ct) ?? Task.FromResult<(JobOutputPage?, string?)>((null, Loc.T("omp is not running")));
}
