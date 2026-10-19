using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

/// <summary>
/// Goal mode and side questions (omp's <c>/goal</c> and <c>/btw</c>; SessionController.BtwGoal.cs), both above the
/// message box. The goal bar: the objective omp pursues across turns, its status and time, Pause/Resume, Edit and
/// Clear. The BTW panel: a question asked beside the conversation, also while omp works, answered from its context
/// as it streams (Cancel stops it), with this session's earlier topics; a topic takes follow-ups.
/// </summary>
public sealed partial class MainViewModel
{
    // ── goal ──

    /// <summary>This chat's omp has goal mode over RPC and the chat can change.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseGoalCommand), nameof(ResumeGoalCommand), nameof(SaveGoalCommand), nameof(ClearGoalCommand))]
    private bool _isGoalAvailable;
    /// <summary>The bar shows: a goal, or the editor for a new one.</summary>
    [ObservableProperty] private bool _showGoalBar;
    [ObservableProperty] private string _goalObjective = "";
    [ObservableProperty] private string _goalStatusText = "";
    [ObservableProperty] private string _goalElapsedText = "";
    [ObservableProperty] private string _goalUsageText = "";
    [ObservableProperty] private bool _isGoalActive;
    [ObservableProperty] private bool _isGoalPaused;
    [ObservableProperty] private bool _isGoalComplete;
    [ObservableProperty] private bool _isEditingGoal;
    [ObservableProperty] private string _goalDraft = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseGoalCommand), nameof(ResumeGoalCommand), nameof(SaveGoalCommand), nameof(ClearGoalCommand))]
    private bool _isGoalActionRunning;

    private OmpGoal? _appliedGoal;
    private DateTimeOffset _goalReadAt;
    private DispatcherTimer? _goalTimer;

    private void ApplyBtwGoal(SessionSnapshot s)
    {
        ApplyGoal(s);
        ApplyBtw(s);
    }

    private void ApplyGoal(SessionSnapshot s)
    {
        IsGoalAvailable = s.GoalSupported == true && !IsReadOnly;
        var goal = IsGoalAvailable ? s.Goal : null;
        _goalReadAt = s.GoalReadAt;
        if (goal != _appliedGoal)
        {
            // Another goal (or none): an edit begun on the previous one does not apply to it
            if (goal?.Id != _appliedGoal?.Id && _appliedGoal is not null) IsEditingGoal = false;
            _appliedGoal = goal;
            GoalObjective = goal?.Objective ?? "";
            IsGoalActive = goal?.IsActive == true;
            IsGoalPaused = goal?.IsPaused == true;
            IsGoalComplete = goal?.IsComplete == true;
            GoalStatusText = goal is null ? "" : GoalStatus(goal.Status);
            GoalUsageText = goal is null ? "" : goal.TokenBudget is { } budget
                ? Loc.F("{0} of {1}", SubagentRowViewModel.TokensText(goal.TokensUsed), SubagentRowViewModel.TokensText(budget))
                : goal.TokensUsed > 0 ? SubagentRowViewModel.TokensText(goal.TokensUsed) : "";
            PauseGoalCommand.NotifyCanExecuteChanged();
            ResumeGoalCommand.NotifyCanExecuteChanged();
            ClearGoalCommand.NotifyCanExecuteChanged();
        }
        if (!IsGoalAvailable) IsEditingGoal = false;
        ShowGoalBar = _appliedGoal is not null || IsEditingGoal;
        TickGoal();
        if (IsGoalActive && _goalTimer is null)
        {
            _goalTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickGoal());
            _goalTimer.Start();
        }
        else if (!IsGoalActive && _goalTimer is not null)
        {
            _goalTimer.Stop();
            _goalTimer = null;
        }
    }

    private void TickGoal() =>
        GoalElapsedText = _appliedGoal is { } goal ? TasksViewModel.Duration(GoalElapsed(goal, _goalReadAt, DateTimeOffset.UtcNow)) : "";

    /// <summary>omp counts an active goal's wall-clock time from when it became active; the bar goes on from the last reading.</summary>
    internal static TimeSpan GoalElapsed(OmpGoal goal, DateTimeOffset readAt, DateTimeOffset now)
    {
        var used = TimeSpan.FromSeconds(goal.TimeUsedSeconds);
        return goal.Status == "active" && readAt != default && now > readAt ? used + (now - readAt) : used;
    }

    internal static string GoalStatus(string status) => status switch
    {
        "active" => Loc.T("Working on it"),
        "paused" => Loc.T("Paused"),
        "budget-limited" => Loc.T("Token budget reached"),
        "complete" => Loc.T("Achieved"),
        _ => status,
    };

    /// <summary>/goal: with an objective, starts (or, with a goal, changes) it; bare, opens the editor.</summary>
    private void OpenGoalFromCommand(string objective)
    {
        if (objective.Length > 0)
        {
            GoalDraft = objective;
            _ = SaveGoalAsync();
            return;
        }
        StartGoalEdit();
    }

    [RelayCommand]
    private void StartGoalEdit()
    {
        if (!IsGoalAvailable) return;
        GoalDraft = _appliedGoal is { IsComplete: false } goal ? goal.Objective : "";
        IsEditingGoal = true;
        ShowGoalBar = true;
    }

    [RelayCommand]
    private void CancelGoalEdit()
    {
        IsEditingGoal = false;
        GoalDraft = "";
        ShowGoalBar = _appliedGoal is not null;
    }

    private bool CanChangeGoal() => IsGoalAvailable && !IsGoalActionRunning;

    /// <summary>The editor's objective: a new goal, or the goal's objective changed (omp: drop, then create).</summary>
    [RelayCommand(CanExecute = nameof(CanChangeGoal))]
    private async Task SaveGoalAsync()
    {
        var objective = GoalDraft.Trim();
        if (objective.Length == 0) return;
        var current = _appliedGoal is { IsComplete: false } goal ? goal : null;
        if (current?.Objective == objective)
        {
            CancelGoalEdit();
            return;
        }
        // A goal already achieved (omp has let it go) leaves the bar for the new one
        if (_appliedGoal is { IsComplete: true }) await Session.ClearGoalAsync(_cts.Token);
        var saved = current is null
            ? await RunGoalActionAsync(Loc.T("Could not set the goal"), ct => Session.CreateGoalAsync(objective, null, ct))
            : await RunGoalActionAsync(Loc.T("Could not change the goal"), ct => Session.EditGoalAsync(objective, ct));
        if (!saved) return;
        IsEditingGoal = false;
        GoalDraft = "";
        ShowGoalBar = _appliedGoal is not null;
    }

    private bool CanPauseGoal() => CanChangeGoal() && _appliedGoal?.IsActive == true;

    [RelayCommand(CanExecute = nameof(CanPauseGoal))]
    private Task PauseGoalAsync() => RunGoalActionAsync(Loc.T("Could not pause the goal"), Session.PauseGoalAsync);

    private bool CanResumeGoal() => CanChangeGoal() && _appliedGoal?.IsPaused == true;

    [RelayCommand(CanExecute = nameof(CanResumeGoal))]
    private Task ResumeGoalAsync() => RunGoalActionAsync(Loc.T("Could not resume the goal"), Session.ResumeGoalAsync);

    private bool CanClearGoal() => CanChangeGoal() && _appliedGoal is not null;

    /// <summary>Ends the goal (omp drops it); a goal already achieved just leaves the bar.</summary>
    [RelayCommand(CanExecute = nameof(CanClearGoal))]
    private Task ClearGoalAsync() => RunGoalActionAsync(Loc.T("Could not clear the goal"), Session.ClearGoalAsync);

    /// <summary>False when omp refused or could not be asked (a brief note says why).</summary>
    private async Task<bool> RunGoalActionAsync(string failureTitle, Func<CancellationToken, Task<string?>> action)
    {
        if (IsClosing || IsReadOnly) return false;
        IsGoalActionRunning = true;
        try
        {
            if (await action(_cts.Token) is not { } error) return true;
            ShowUrgentBrief("IconAlert", failureTitle, error);
            return false;
        }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException) { return false; }
        finally
        {
            IsGoalActionRunning = false;
            ApplyIfShown(_open);
        }
    }

    // ── side questions (/btw) ──

    /// <summary>This chat's omp answers side questions over RPC and the chat can change.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskBtwCommand), nameof(CancelBtwCommand))]
    private bool _isBtwAvailable;
    [ObservableProperty] private bool _isBtwOpen;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskBtwCommand))]
    private string _btwQuestion = "";
    /// <summary>A question is being answered: another waits until it ends or is cancelled (omp runs one at a time).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskBtwCommand), nameof(CancelBtwCommand))]
    private bool _isBtwRunning;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskBtwCommand))]
    private bool _isBtwSending;
    [ObservableProperty] private bool _hasBtwTopics;
    [ObservableProperty] private bool _isBtwLoading;
    /// <summary>The topic the next question follows up (null: a new topic).</summary>
    [ObservableProperty] private BtwTopicViewModel? _btwFollowUp;

    /// <summary>This session's side-question topics, newest first.</summary>
    public ObservableCollection<BtwTopicViewModel> BtwTopics { get; } = [];

    private IReadOnlyList<OmpBtwRecord>? _appliedBtw;
    private long _btwEpoch;

    /// <summary>The BTW chip by the message box: while omp works (a question then needs no waiting), or once there are topics.</summary>
    public bool ShowBtwChip => IsBtwAvailable && !IsBtwOpen && (IsRunning || HasBtwTopics);

    partial void OnIsBtwAvailableChanged(bool value) => OnPropertyChanged(nameof(ShowBtwChip));
    partial void OnHasBtwTopicsChanged(bool value) => OnPropertyChanged(nameof(ShowBtwChip));

    partial void OnIsBtwOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBtwChip));
        if (value) _ = LoadBtwHistoryIfNeededAsync();
        else BtwFollowUp = null;
    }

    private void ApplyBtw(SessionSnapshot s)
    {
        IsBtwAvailable = s.GoalSupported == true && !IsReadOnly;
        OnPropertyChanged(nameof(ShowBtwChip)); // follows IsRunning too
        if (_btwEpoch != s.TranscriptEpoch)
        {
            // Another session: its own topics, read when the panel shows
            _btwEpoch = s.TranscriptEpoch;
            BtwFollowUp = null;
            if (IsBtwOpen && !s.BtwHistoryLoaded) _ = LoadBtwHistoryIfNeededAsync();
        }
        if (ReferenceEquals(s.Btw, _appliedBtw)) return;
        _appliedBtw = s.Btw;
        var byId = BtwTopics.ToDictionary(t => t.Id, StringComparer.Ordinal);
        for (var i = 0; i < s.Btw.Count; i++)
        {
            var record = s.Btw[i];
            if (byId.Remove(record.Id, out var topic)) topic.Update(record);
            else topic = new BtwTopicViewModel(record, this);
            var at = BtwTopics.IndexOf(topic);
            if (at < 0) BtwTopics.Insert(i, topic);
            else if (at != i) BtwTopics.Move(at, i);
        }
        foreach (var gone in byId.Values) BtwTopics.Remove(gone);
        if (BtwFollowUp is not null && !BtwTopics.Contains(BtwFollowUp)) BtwFollowUp = null;
        HasBtwTopics = BtwTopics.Count > 0;
        IsBtwRunning = s.Btw.Any(r => r.IsRunning);
    }

    private async Task LoadBtwHistoryIfNeededAsync()
    {
        if (!IsBtwAvailable || IsBtwLoading || Session.Snapshot().BtwHistoryLoaded) return;
        IsBtwLoading = true;
        try
        {
            if (await Session.LoadBtwHistoryAsync(_cts.Token) is { } error)
                ShowUrgentBrief("IconAlert", Loc.T("Could not read the side questions"), error);
        }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            IsBtwLoading = false;
            ApplyIfShown(_open);
        }
    }

    /// <summary>/btw: with a question, asks it; bare, opens the panel with this session's topics.</summary>
    private void OpenBtwFromCommand(string question)
    {
        IsBtwOpen = true;
        if (question.Length == 0) return;
        BtwQuestion = question;
        if (AskBtwCommand.CanExecute(null)) _ = AskBtwCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void ToggleBtw() => IsBtwOpen = !IsBtwOpen;

    private bool CanAskBtw() => IsBtwAvailable && !IsBtwRunning && !IsBtwSending && BtwQuestion.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAskBtw))]
    private async Task AskBtwAsync()
    {
        if (IsClosing || IsReadOnly) return;
        var question = BtwQuestion.Trim();
        var followUp = BtwFollowUp?.Id;
        IsBtwSending = true;
        try
        {
            if (await Session.AskBtwAsync(question, followUp, _cts.Token) is { } error)
            {
                ShowUrgentBrief("IconAlert", Loc.T("Could not ask the side question"), error);
                return;
            }
            BtwQuestion = "";
            BtwFollowUp = null;
        }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            IsBtwSending = false;
            ApplyIfShown(_open);
        }
    }

    private bool CanCancelBtw() => IsBtwAvailable && IsBtwRunning;

    [RelayCommand(CanExecute = nameof(CanCancelBtw))]
    private async Task CancelBtwAsync()
    {
        try
        {
            if (await Session.CancelBtwAsync(null, _cts.Token) is { } error)
                ShowUrgentBrief("IconAlert", Loc.T("Could not cancel the side question"), error);
        }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException) { }
        finally { ApplyIfShown(_open); }
    }

    internal void FollowUpBtw(BtwTopicViewModel topic) => BtwFollowUp = ReferenceEquals(BtwFollowUp, topic) ? null : topic;

    [RelayCommand]
    private void ClearBtwFollowUp() => BtwFollowUp = null;
}

/// <summary>A side-question topic in the BTW panel: its questions and answers, the latest streaming while omp writes it.</summary>
public sealed partial class BtwTopicViewModel : ObservableObject
{
    private readonly MainViewModel _host;

    public string Id { get; }
    public ObservableCollection<BtwTurnViewModel> Turns { get; } = [];
    [ObservableProperty] private bool _isRunning;
    /// <summary>The first question: the topic's name in the follow-up line.</summary>
    [ObservableProperty] private string _title = "";

    internal BtwTopicViewModel(OmpBtwRecord record, MainViewModel host)
    {
        _host = host;
        Id = record.Id;
        Update(record);
    }

    internal void Update(OmpBtwRecord record)
    {
        for (var i = 0; i < record.Turns.Count; i++)
        {
            if (i < Turns.Count) Turns[i].Update(record.Turns[i]);
            else Turns.Add(new BtwTurnViewModel(record.Turns[i]));
        }
        while (Turns.Count > record.Turns.Count) Turns.RemoveAt(Turns.Count - 1);
        IsRunning = record.IsRunning;
        Title = record.Turns[0].Question;
    }

    /// <summary>The next question continues this topic (again: a new topic).</summary>
    [RelayCommand]
    private void FollowUp() => _host.FollowUpBtw(this);
}

public sealed partial class BtwTurnViewModel : ObservableObject
{
    [ObservableProperty] private string _question = "";
    [ObservableProperty] private string _answer = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasAnswer;
    /// <summary>How the question ended when not answered (cancelled, failed, cut off); empty otherwise.</summary>
    [ObservableProperty] private string _outcome = "";
    [ObservableProperty] private bool _hasOutcome;
    [ObservableProperty] private bool _isFailed;

    internal BtwTurnViewModel(OmpBtwTurn turn) => Update(turn);

    internal void Update(OmpBtwTurn turn)
    {
        Question = turn.Question;
        Answer = turn.Answer;
        HasAnswer = turn.Answer.Length > 0;
        IsRunning = turn.IsRunning;
        IsFailed = turn.Status == "error";
        Outcome = turn.Status switch
        {
            "cancelled" => Loc.T("Cancelled"),
            "interrupted" => Loc.T("Cut off: omp stopped while answering"),
            "error" => turn.Error is { Length: > 0 } e ? Loc.F("Failed: {0}", e) : Loc.T("Failed"),
            _ => "",
        };
        HasOutcome = Outcome.Length > 0;
    }
}
