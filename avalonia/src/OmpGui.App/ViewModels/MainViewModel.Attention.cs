using CommunityToolkit.Mvvm.ComponentModel;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>Getting the user's attention when a run ends or omp asks something while the window is in the background.</summary>
public sealed partial class MainViewModel
{
    private bool _windowActive = true;
    private string? _lastDialogId;
    private long _runsEndedSeen = -1;

    /// <summary>Something happened while the window was in the background (cleared when it is activated).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool _needsAttention;

    [ObservableProperty] private bool _notificationsEnabled = true;

    public string WindowTitle => (NeedsAttention ? "● " : "") + SessionTitle + " — OMP GUI";

    /// <summary>Raised on the UI thread with (title, message) for an OS notification; the view knows the window handle.</summary>
    public event Action<string, string>? AttentionRequested;

    partial void OnSessionTitleChanged(string value) => OnPropertyChanged(nameof(WindowTitle));

    public void SetWindowActive(bool active)
    {
        _windowActive = active;
        if (active) NeedsAttention = false;
    }

    private void CheckAttention(SessionSnapshot s)
    {
        var dialog = s.Dialogs.Count > 0 ? s.Dialogs[0] : null;
        // A count of ended runs, not a running → ready change between two snapshots: a short run can start and end
        // between two applies (or while minimized, between two checks) and must still be noticed.
        var runEnded = _runsEndedSeen >= 0 && s.RunsEnded > _runsEndedSeen;
        string? title = null, message = null;
        if (dialog is not null && dialog.Id != _lastDialogId)
        {
            title = dialog.Kind == DialogKind.Approval ? "omp needs your approval" : "omp is asking";
            message = dialog.Headline;
        }
        else if (runEnded && s.Phase == SessionPhase.Ready)
        {
            title = "omp finished";
            var answer = s.Items.OfType<AssistantItem>().LastOrDefault()?.Text ?? "";
            message = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() is { Length: > 0 } line
                ? (line.Length > 140 ? line[..140] + "…" : line)
                : SessionTitle;
        }
        else if (runEnded && s.Phase == SessionPhase.Faulted)
        {
            title = "omp stopped";
            message = s.LastError?.Split('\n')[0] ?? "omp is not running";
        }
        _lastDialogId = dialog?.Id;
        _runsEndedSeen = Math.Max(_runsEndedSeen, s.RunsEnded); // an older snapshot applied late must not count a run twice
        if (title is null || (_windowActive && _windowState != "Minimized")) return;
        NeedsAttention = true;
        if (NotificationsEnabled) AttentionRequested?.Invoke(title, message ?? "");
    }
}
