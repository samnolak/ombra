using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

public sealed partial class MainViewModel
{
    private static readonly SessionHistory EmptyHistory = new();
    private SessionHistory? _shownHistory;
    private SessionPhase _historyPhase;
    private OpenSession? _historyOpen;

    public SessionHistory History => _open.Last?.History ?? EmptyHistory;
    public bool ShowHistory => IsReadOnly || History.TotalMessages > 0 || History.IsLoading || History.Error is not null || History.NeedsReload;
    public bool CanLoadOlderMessages => (IsReadOnly || Phase == SessionPhase.Ready) && !History.IsLoading && History.HasOlder && !History.NeedsReload;
    public bool CanReloadHistory => (IsReadOnly || Phase == SessionPhase.Ready) && !History.IsLoading && (IsReadOnly || History.NeedsReload || History.Error is not null);
    public string HistoryActionText => History.Error is null ? Loc.T("Load older messages") : Loc.T("Retry older messages");
    public string HistoryStatus => History.IsLoading ? Loc.T("Loading one page of messages…")
        : History.Error is { } error ? History.NeedsReload ? $"{HistoryError(error)} {HistoryReloadExplanation}" : HistoryError(error)
        : History.NeedsReload ? Loc.T("History changed. Reload shows the latest messages and moves your view to the end.")
        : History.HasOlder && IsRunning ? Loc.F("{0} of {1} messages loaded. Older messages are available when the session is idle.", History.LoadedMessages, History.TotalMessages)
        : Loc.F("{0} of {1} messages loaded", History.LoadedMessages, History.TotalMessages);
    public string HistoryReloadExplanation => Loc.T("Reload shows only the latest page of messages and moves your view to the end.");

    /// <summary>ClientCore's own history errors (SessionController) in the UI's language; omp's errors stay as omp wrote them.</summary>
    private static string HistoryError(string error)
    {
        const string unreadable = "The saved conversation could not be read: ";
        return error switch
        {
            "Recent history is unavailable while omp is working. Live messages remain visible; reload recent messages when idle." =>
                Loc.T("Recent history is unavailable while omp is working. Live messages remain visible; reload recent messages when idle."),
            "History changed since it was loaded. Reload recent messages to browse the new history; your current view has been kept." =>
                Loc.T("History changed since it was loaded. Reload recent messages to browse the new history; your current view has been kept."),
            "History is paused while omp is working. Try again when the session is idle." =>
                Loc.T("History is paused while omp is working. Try again when the session is idle."),
            _ when error.StartsWith(unreadable, StringComparison.Ordinal) => Loc.T(unreadable) + error[unreadable.Length..],
            _ => error,
        };
    }

    private void ApplyHistory()
    {
        if (_shownHistory == History && _historyPhase == Phase && ReferenceEquals(_historyOpen, _open)) return;
        (_shownHistory, _historyPhase, _historyOpen) = (History, Phase, _open);
        OnPropertyChanged(nameof(History));
        OnPropertyChanged(nameof(ShowHistory));
        OnPropertyChanged(nameof(CanLoadOlderMessages));
        OnPropertyChanged(nameof(CanReloadHistory));
        OnPropertyChanged(nameof(HistoryStatus));
        OnPropertyChanged(nameof(HistoryActionText));
        LoadOlderMessagesCommand.NotifyCanExecuteChanged();
        ReloadHistoryCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanLoadOlderMessages))]
    private async Task LoadOlderMessagesAsync()
    {
        if (!CanLoadOlderMessages) return;
        var open = _open;
        HistoryReadingRequested?.Invoke();
        await open.Controller.LoadOlderMessagesAsync(open.Lifetime.Token);
        ApplyIfShown(open);
    }

    [RelayCommand(CanExecute = nameof(CanReloadHistory))]
    private async Task ReloadHistoryAsync()
    {
        if (!CanReloadHistory) return;
        var open = _open;
        await open.Controller.ReloadHistoryAsync(open.Lifetime.Token);
        ApplyIfShown(open);
    }

    // A reader requesting older messages must leave follow-latest even when this page fits in the view.
    public event Action? HistoryReadingRequested;

    /// <summary>Only a prepend/reorder takes this path; live deltas retain the existing append/update path.</summary>
    private void ReorderHistoryRows(IReadOnlyList<TranscriptItem> items)
    {
        var groups = Rows.OfType<StepGroupRowViewModel>().ToDictionary(g => g.Leader);
        var folds = Rows.OfType<TurnFoldRowViewModel>().ToDictionary(f => f.User);
        var ordered = new List<RowViewModel>(Rows.Count);
        var retained = new HashSet<long>();
        foreach (var item in items)
        {
            var row = _open.RowsByKey[item.Key];
            retained.Add(item.Key);
            if (row is ToolRowViewModel tool && groups.TryGetValue(tool, out var group)) ordered.Add(group);
            ordered.Add(row);
            if (row is UserRowViewModel user && folds.TryGetValue(user, out var fold)) ordered.Add(fold);
        }
        foreach (var key in _open.RowsByKey.Keys.Where(k => !retained.Contains(k)).ToArray())
        {
            (_open.RowsByKey[key] as IDisposable)?.Dispose();
            _open.RowsByKey.Remove(key);
        }
        _rows.ResetTo(ordered);
    }
}
