using CommunityToolkit.Mvvm.ComponentModel;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>Getting the user's attention when a run ends or omp asks something in a chat the user is not looking at:
/// the window is in the background, or the chat is not the one shown.</summary>
public sealed partial class MainViewModel
{
    private bool _windowActive = true;

    /// <summary>Something happened while the window was in the background (cleared when it is activated).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool _needsAttention;

    [ObservableProperty] private bool _notificationsEnabled = true;

    public string WindowTitle => (NeedsAttention ? "● " : "") + SessionTitle + " — OMP GUI";

    /// <summary>
    /// Raised on the UI thread with (title, message, open) for an OS notification; the view knows the window handle.
    /// <c>open</c> shows the chat it is about (a click on the notification, where the platform reports one).
    /// </summary>
    public event Action<string, string, Action>? AttentionRequested;

    partial void OnSessionTitleChanged(string value) => OnPropertyChanged(nameof(WindowTitle));

    public void SetWindowActive(bool active)
    {
        _windowActive = active;
        if (!active) return;
        // The shown chat's reply that came while the window was away is seen now (another chat's stays unread)
        var seen = _open.Unread;
        _open.Unread = false;
        NeedsAttention = false;
        if (seen) UpdateSessionStatuses();
    }

    /// <summary>
    /// Called for every snapshot of every chat, shown or not (a chat in the background at most once a second): a new
    /// question, or a run that ended or crashed, is told when the user is not looking at that chat. A chat in the
    /// background then gets its sidebar mark (an unread reply; a question or an error shows anyway) and the notification
    /// opens it.
    /// </summary>
    private void CheckAttention(OpenSession open, SessionSnapshot s)
    {
        var dialog = s.Dialogs.Count > 0 ? s.Dialogs[0] : null;
        // A count of ended runs, not a running → ready change between two snapshots: a short run can start and end
        // between two applies (or between two background checks) and must still be noticed.
        var runEnded = open.RunsEndedSeen >= 0 && s.RunsEnded > open.RunsEndedSeen;
        string? title = null, message = null;
        if (dialog is not null && dialog.Id != open.LastDialogId)
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
                : ChatTitle(s);
        }
        else if (runEnded && s.Phase == SessionPhase.Faulted)
        {
            title = "omp stopped";
            message = s.LastError?.Split('\n')[0] ?? "omp is not running";
        }
        open.LastDialogId = dialog?.Id;
        open.RunsEndedSeen = Math.Max(open.RunsEndedSeen, s.RunsEnded); // an older snapshot applied late must not count a run twice
        if (title is null) return;
        var shown = open == _open;
        var windowInFront = _windowActive && _windowState != "Minimized";
        if (shown && windowInFront) return;
        // The dot goes on the chat whose run ended, the shown one too while the window is away. NeedsAttention is the
        // window's mark (its title) for news in any chat, so it cannot be the shown chat's dot: an empty new chat would
        // read "new reply" for another chat's reply.
        if (runEnded) open.Unread = true;
        // The title of a chat in the background says whose news it is
        if (!shown) title = $"{title} · {ChatTitle(s)}";
        if (!windowInFront) NeedsAttention = true;
        UpdateSessionStatuses();
        if (NotificationsEnabled) AttentionRequested?.Invoke(title, message ?? "", () => ShowFromNotification(open));
    }

    /// <summary>A chat's title as the header and the sidebar show it.</summary>
    private static string ChatTitle(SessionSnapshot s) =>
        s.SessionName is { Length: > 0 } n ? n
        : s.Items.OfType<UserItem>().FirstOrDefault()?.Text is { Length: > 0 } u ? SessionCatalogTitle(u)
        : "New session";

    /// <summary>The user clicked a notification: its chat is shown (on the question it asked), if it is still open.</summary>
    private void ShowFromNotification(OpenSession open)
    {
        if (!_opens.ContainsKey(open.Controller) || IsClosing) return;
        IsSettingsOpen = false;
        ShowSession(open); // MainViewModel.OpenSessions.cs
    }
}
