using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>The sidebar's status chips: which open chats the list keeps (Codex Activity, Claude Code's filters).</summary>
public enum SessionStatusFilter { All, Working, NeedsInput, Unread, Errors }

/// <summary>
/// The sidebar beyond listing: archiving sessions (the client's own list, nothing on disk changes), the status chips,
/// reading older sessions than the scan's cap, and moving between chats from the keyboard (⌘1–9, back and forward,
/// the next chat that waits on the user).
/// </summary>
public sealed partial class MainViewModel
{
    // ───────────── Archive ─────────────

    private HashSet<string>? _archived;

    /// <summary>The archived session files (<see cref="OmpRuntimeOptions.ArchivedSessions"/>).</summary>
    private HashSet<string> Archived()
    {
        if (_archived is not null) return _archived;
        OmpRuntimeOptions? o = null;
        try { o = _settings?.Load(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Listed as if nothing was archived; the settings page reports the file's problem when it opens.
        }
        return _archived = new HashSet<string>(o?.ArchivedSessions ?? [], SessionCatalog.PathComparer);
    }

    private void SaveArchived()
    {
        string[]? archived = _archived is { Count: > 0 } a ? [.. a.Order(StringComparer.Ordinal)] : null;
        Persist(o => o with { ArchivedSessions = archived });
        ApplySessionFilter();
    }

    /// <summary>Row menu: archive or unarchive. Only the client's list changes, so it is done at once, with Undo.</summary>
    [RelayCommand]
    private void ToggleArchive(SessionItemViewModel? item)
    {
        if (item is null) return;
        var path = item.Model.Path;
        var archive = !Archived().Contains(path);
        SetArchived(path, archive);
        if (!archive) return;
        var card = new SessionCardViewModel("brief", "IconArchive", Loc.T("Session archived"),
            Loc.T("Nothing was deleted: it is listed under Archived at the bottom of the sidebar."));
        card.Primary = new SessionCardAction(Loc.T("Undo"), new RelayCommand(() =>
        {
            SetArchived(path, false);
            CloseSessionCard();
        }));
        ShowTimedCard(card, UndoCardDuration);
    }

    private void SetArchived(string path, bool archived)
    {
        if (archived ? Archived().Add(path) : Archived().Remove(path)) SaveArchived();
    }

    /// <summary>Archived's header: every archived session back into its group.</summary>
    [RelayCommand]
    private void UnarchiveAll()
    {
        if (Archived().Count == 0) return;
        _archived!.Clear();
        SaveArchived();
    }

    private SessionGroupViewModel? _archiveGroup;

    /// <summary>Archived, last in the sidebar: folded until its header opens it; a search shows what matches in it.</summary>
    private SessionGroupViewModel? ArchiveGroup(List<SessionItemViewModel> archived)
    {
        if (archived.Count == 0) return null;
        _archiveGroup ??= new SessionGroupViewModel("", false, isArchiveGroup: true) { UnarchiveAllCommand = UnarchiveAllCommand };
        _archiveGroup.ArchiveLabel = Loc.F("Archived {0}", archived.Count);
        _archiveGroup.Name = Loc.T("Archived");
        if (SessionFilter.Trim().Length > 0 || StatusFilter != SessionStatusFilter.All) _archiveGroup.IsExpanded = true;
        Reconcile(_archiveGroup.Items, archived);
        return _archiveGroup;
    }

    // ───────────── Status chips ─────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll), nameof(IsFilterWorking), nameof(IsFilterNeedsInput), nameof(IsFilterUnread), nameof(IsFilterErrors), nameof(ShowStatusFilters))]
    private SessionStatusFilter _statusFilter;

    public bool IsFilterAll => StatusFilter == SessionStatusFilter.All;
    public bool IsFilterWorking => StatusFilter == SessionStatusFilter.Working;
    public bool IsFilterNeedsInput => StatusFilter == SessionStatusFilter.NeedsInput;
    public bool IsFilterUnread => StatusFilter == SessionStatusFilter.Unread;
    public bool IsFilterErrors => StatusFilter == SessionStatusFilter.Errors;
    /// <summary>The chips show with the search field, and while one narrows the list.</summary>
    public bool ShowStatusFilters => ShowSessionSearch || StatusFilter != SessionStatusFilter.All;

    partial void OnStatusFilterChanged(SessionStatusFilter value) => ApplySessionFilter();

    [RelayCommand]
    private void SetStatusFilter(SessionStatusFilter filter) => StatusFilter = StatusFilter == filter ? SessionStatusFilter.All : filter;

    private static bool Passes(SessionStatusFilter filter, SessionStatus status) => filter switch
    {
        SessionStatusFilter.Working => status == SessionStatus.Running,
        SessionStatusFilter.NeedsInput => status == SessionStatus.Waiting,
        SessionStatusFilter.Unread => status == SessionStatus.Unread,
        SessionStatusFilter.Errors => status == SessionStatus.Error,
        _ => true,
    };

    // ───────────── Older sessions than the scan reads ─────────────

    /// <summary>How many of the newest session files a scan reads; Show older reads as many more.</summary>
    internal const int CatalogPage = 500;
    private int _catalogLimit = CatalogPage;
    internal int CatalogLimit => Volatile.Read(ref _catalogLimit);

    /// <summary>The last scan stopped at <see cref="CatalogLimit"/> files: older sessions are not listed yet.</summary>
    [ObservableProperty] private bool _canShowOlder;

    [RelayCommand]
    private void ShowOlder()
    {
        Interlocked.Add(ref _catalogLimit, CatalogPage);
        CanShowOlder = false;
        RequestCatalogRefresh();
    }

    // ───────────── Moving between chats ─────────────

    private readonly List<string> _backStack = [];
    private readonly List<string> _forwardStack = [];
    /// <summary>The chat Back or Forward is moving to: the chat left for it is not recorded (cleared by that move).</summary>
    private string? _historyTarget;
    private const int MaxHistory = 50;

    /// <summary>A chat was left for another: Back returns to it (not when Back or Forward did the move).</summary>
    private void RecordLeft(OpenSession leaving)
    {
        if (_historyTarget is not null)
        {
            _historyTarget = null;
            return;
        }
        if ((leaving.OpeningFile ?? leaving.Controller.SessionFile ?? leaving.Controller.CurrentLaunch.ResumeSessionFile) is not { } file) return;
        if (_backStack.Count == 0 || _backStack[^1] != file) _backStack.Add(file);
        if (_backStack.Count > MaxHistory) _backStack.RemoveAt(0);
        _forwardStack.Clear();
    }

    /// <summary>⌘[ / ⌘]: the chat shown before (or after, once back) this one.</summary>
    internal bool NavigateHistory(bool back)
    {
        var from = back ? _backStack : _forwardStack;
        var to = back ? _forwardStack : _backStack;
        var current = ShownFile;
        while (from.Count > 0)
        {
            var file = from[^1];
            from.RemoveAt(from.Count - 1);
            if (file == current) continue;
            if (!GoTo(file)) continue; // deleted meanwhile: the next one
            if (current is not null) to.Add(current);
            return true;
        }
        return false;
    }

    private bool GoTo(string file)
    {
        var open = FindOpen(file);
        var item = open is null ? Sessions.FirstOrDefault(s => s.Model.Path == file) : null;
        if (open is null && item is null) return false;
        _historyTarget = file;
        if (open is not null)
        {
            ShowSession(open);
            _historyTarget = null; // shown at once (or it already was)
        }
        else _ = OpenSessionCommand.ExecuteAsync(item);
        return true;
    }

    /// <summary>The sidebar's rows in its order, as listed (an archive folded away is not).</summary>
    private List<SessionItemViewModel> ListedRows() =>
        SessionGroups.Where(g => g.IsExpanded).SelectMany(g => g.Items).ToList();

    /// <summary>⌘1–⌘9: the sidebar's first nine rows.</summary>
    internal bool OpenNthSession(int n)
    {
        var rows = ListedRows();
        if (n < 1 || n > rows.Count || rows[n - 1].Model.Path == ShownFile) return false;
        _ = OpenSessionCommand.ExecuteAsync(rows[n - 1]);
        return true;
    }

    /// <summary>⌥⌘A: the next chat (after the shown one, wrapping) that waits on the user, else one with a new reply.</summary>
    internal bool OpenNextWaiting()
    {
        var rows = ListedRows();
        var at = rows.FindIndex(i => i.Model.Path == ShownFile);
        foreach (var status in new[] { SessionStatus.Waiting, SessionStatus.Unread })
            for (var k = 1; k <= rows.Count; k++)
            {
                var item = rows[((at < 0 ? -1 : at) + k) % rows.Count];
                if (item.Status != status || item.Model.Path == ShownFile) continue;
                _ = OpenSessionCommand.ExecuteAsync(item);
                return true;
            }
        return false;
    }
}
