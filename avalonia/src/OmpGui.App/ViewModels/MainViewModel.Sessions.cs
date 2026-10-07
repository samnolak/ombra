using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>Sessions: sidebar list, new / open / rename, project folder.</summary>
public sealed partial class MainViewModel
{
    private IReadOnlyList<SessionSummary> _catalog = [];
    // Requests counted, scans run one at a time; a request that arrives during a scan causes one more scan.
    private int _catalogRequests;

    /// <summary>Saved sessions, newest first, filtered by <see cref="SessionFilter"/>.</summary>
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    /// <summary><see cref="Sessions"/> grouped by project folder for the sidebar: the open project first, then by the
    /// newest session in each.</summary>
    public ObservableCollection<SessionGroupViewModel> SessionGroups { get; } = [];

    /// <summary>The user's choice for wide windows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSidebar))]
    private bool _isSidebarVisible = true;

    /// <summary>Set by the view: the window is too narrow for sidebar and conversation side by side.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSidebar))]
    private bool _isNarrow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSidebar))]
    private bool _sidebarShownWhileNarrow;

    /// <summary>Wide: the user's choice. Narrow: hidden unless the user opened it.</summary>
    public bool ShowSidebar => IsNarrow ? SidebarShownWhileNarrow : IsSidebarVisible;

    partial void OnIsNarrowChanged(bool value) => SidebarShownWhileNarrow = false;
    [ObservableProperty] private string _sessionFilter = "";
    [ObservableProperty] private string _sessionTitle = "New session";
    [ObservableProperty] private string _projectName = "";
    [ObservableProperty] private string _projectPath = "";
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";

    /// <summary>Raised when the user asked to open a folder; the view shows the platform folder picker.</summary>
    public event Func<Task<string?>>? PickFolderRequested;

    private bool CanChangeSession() => Phase is SessionPhase.Ready or SessionPhase.Faulted or SessionPhase.Stopped && !IsSigningIn;

    partial void OnSessionFilterChanged(string value) => ApplySessionFilter();

    [RelayCommand]
    private void ToggleSidebar()
    {
        if (IsNarrow) SidebarShownWhileNarrow = !SidebarShownWhileNarrow;
        else IsSidebarVisible = !IsSidebarVisible;
    }

    [RelayCommand(CanExecute = nameof(CanNewSession))]
    private async Task NewSessionAsync()
    {
        SidebarShownWhileNarrow = false; // the drawer closes on a choice
        try
        {
            await _session.NewSessionAsync(_cts.Token);
            AfterSessionChange();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    private bool CanNewSession() => Phase == SessionPhase.Ready && !IsSigningIn;

    [RelayCommand(CanExecute = nameof(CanChangeSession))]
    private async Task OpenSessionAsync(SessionItemViewModel? item)
    {
        SidebarShownWhileNarrow = false; // the drawer closes on a choice
        try
        {
            if (item is null) return;
            await _session.OpenSessionAsync(item.Model.Path, item.Model.Cwd, _cts.Token);
            AfterSessionChange();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    [RelayCommand(CanExecute = nameof(CanChangeSession))]
    private async Task OpenFolderAsync()
    {
        try
        {
            if (PickFolderRequested is not { } pick || await pick() is not { } folder) return;
            await _session.OpenFolderAsync(folder, _cts.Token);
            AfterSessionChange();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    /// <summary>A new session in a project listed in the sidebar: in the open project a plain new session, elsewhere
    /// omp restarts in that folder.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeSession))]
    private async Task NewSessionInProjectAsync(SessionGroupViewModel? group)
    {
        try
        {
            if (group is null) return;
            if (group.IsCurrentProject && CanNewSession()) await _session.NewSessionAsync(_cts.Token);
            else await _session.OpenFolderAsync(group.Cwd, _cts.Token);
            AfterSessionChange();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    [RelayCommand(CanExecute = nameof(CanNewSession))]
    private void StartRename()
    {
        // Start from what the header shows (a session named after its first message has no explicit name yet)
        RenameText = _last?.SessionName ?? (SessionTitle == "New session" ? "" : SessionTitle);
        IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        try
        {
            IsRenaming = false;
            var name = RenameText.Trim();
            if (name.Length == 0 || name == _last?.SessionName) return;
            await _session.RenameSessionAsync(name, _cts.Token);
            AfterSessionChange();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    private void AfterSessionChange()
    {
        Apply(_session.Snapshot());
        RequestCatalogRefresh();
    }

    /// <summary>Header text and the current-session mark; called from Apply.</summary>
    private void ApplySessionInfo(SessionSnapshot s)
    {
        var firstUser = s.Items.OfType<UserItem>().FirstOrDefault()?.Text;
        SessionTitle = s.SessionName is { Length: > 0 } n ? n
            : firstUser is { Length: > 0 } u ? SessionCatalogTitle(u)
            : "New session";
        ProjectName = SessionItemViewModel.ProjectName(s.Cwd);
        var projectChanged = ProjectPath != (s.Cwd ?? "");
        ProjectPath = s.Cwd ?? "";
        if (projectChanged) RebuildSessionGroups(); // the open project's group goes first
        var active = s.Phase is SessionPhase.Running or SessionPhase.Aborting;
        foreach (var item in Sessions)
        {
            item.IsCurrent = item.Model.Path == s.SessionFile;
            item.IsActive = item.IsCurrent && active;
            if (LiveTitleFor(item) is var live && item.LiveTitle != live) item.LiveTitle = live;
        }
    }

    private static string SessionCatalogTitle(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= 80 ? line : line[..80] + "…";
    }

    /// <summary>Rescans omp's session folder off the UI thread; overlapping requests collapse into one more scan.</summary>
    internal void RequestCatalogRefresh()
    {
        if (_last?.SessionFile is not { } file || SessionCatalog.SessionsRootOf(file) is not { } root) return;
        // Only the request that finds the counter at zero starts the worker; the worker scans until it has
        // covered every request made before its last scan began.
        if (Interlocked.Increment(ref _catalogRequests) != 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var seen = Volatile.Read(ref _catalogRequests);
                    var list = await SessionCatalog.ScanAsync(root, ct: _cts.Token).ConfigureAwait(false);
                    var pins = ReadPinnedSessionIds(file); // omp's pinned sessions come first (MainViewModel.Session.cs)
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _catalog = list;
                        _pinnedIds = pins;
                        ApplySessionFilter();
                    }, DispatcherPriority.Background);
                    if (Interlocked.CompareExchange(ref _catalogRequests, 0, seen) == seen) return;
                }
            }
            catch (OperationCanceledException) { Interlocked.Exchange(ref _catalogRequests, 0); }
        });
    }

    private void ApplySessionFilter()
    {
        var f = SessionFilter.Trim();
        var current = _last?.SessionFile;
        // Empty sessions (omp creates one per start) are noise, except the one in use.
        var visible = _catalog.Where(c => !c.IsEmpty || c.Path == current).Where(c => f.Length == 0
            || c.Title.Contains(f, StringComparison.CurrentCultureIgnoreCase)
            || c.Cwd.Contains(f, StringComparison.CurrentCultureIgnoreCase)).OrderByDescending(c => _pinnedIds.Contains(c.Id)).ToList();
        // Replace in place only when something changed, so the list keeps its scroll position.
        if (visible.Count == Sessions.Count && visible.Zip(Sessions).All(p => p.First == p.Second.Model))
        {
            foreach (var item in Sessions)
            {
                item.IsCurrent = item.Model.Path == current;
                item.IsPinned = _pinnedIds.Contains(item.Model.Id);
                item.LiveTitle = LiveTitleFor(item);
            }
            return;
        }
        Sessions.Clear();
        foreach (var c in visible)
        {
            var item = new SessionItemViewModel(c)
            {
                IsCurrent = c.Path == current, IsPinned = _pinnedIds.Contains(c.Id),
                OpenCommand = OpenSessionCommand, DeleteCommand = DeleteSessionItemCommand,
            };
            item.LiveTitle = LiveTitleFor(item);
            Sessions.Add(item);
        }
        RebuildSessionGroups();
    }

    /// <summary>The open session's row reads like the header, before omp's file (and the list) catch up.</summary>
    private string? LiveTitleFor(SessionItemViewModel item) =>
        item.IsCurrent && SessionTitle != "New session" && SessionTitle != item.Model.Title ? SessionTitle : null;

    private void RebuildSessionGroups()
    {
        var project = _last?.Cwd ?? ProjectPath;
        var (added, hidden) = ProjectPrefs();
        SessionGroups.Clear();
        var groups = new List<SessionGroupViewModel>();
        foreach (var item in Sessions)
        {
            // A removed project stays out of the list, except while omp works in it.
            if (hidden.Contains(item.Cwd) && !string.Equals(item.Cwd, project, StringComparison.Ordinal)) continue;
            var g = groups.FirstOrDefault(x => string.Equals(x.Cwd, item.Cwd, StringComparison.Ordinal));
            if (g is null) groups.Add(g = NewGroup(item.Cwd, project));
            g.Items.Add(item);
        }
        // By the newest message in each (pinned sessions lead their group, not the list of groups); folders added
        // from the sidebar that have no session yet go first, as just added.
        var ordered = groups.OrderByDescending(g => g.Items.Max(i => i.Model.LastMessageAt)).ToList();
        if (SessionFilter.Trim().Length == 0)
            foreach (var p in added)
                if (!hidden.Contains(p) && !groups.Any(g => string.Equals(g.Cwd, p, StringComparison.Ordinal))) ordered.Insert(0, NewGroup(p, project));
        foreach (var g in ordered) SessionGroups.Add(g);
    }

    private SessionGroupViewModel NewGroup(string cwd, string? project) =>
        new(cwd, string.Equals(cwd, project, StringComparison.Ordinal))
        {
            NewSessionCommand = NewSessionInProjectCommand,
            RemoveCommand = RemoveProjectCommand,
        };

    // ───────────── Projects added to / removed from the sidebar (the client's settings file) ─────────────

    private List<string>? _addedProjects;
    private HashSet<string>? _hiddenProjects;

    private (List<string> Added, HashSet<string> Hidden) ProjectPrefs()
    {
        if (_addedProjects is null || _hiddenProjects is null)
        {
            OmpRuntimeOptions? o = null;
            try { o = _settings?.Load(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Listed as if nothing was added or removed; the settings page reports the file's problem when it opens.
            }
            _addedProjects = [.. o?.SidebarProjects ?? []];
            _hiddenProjects = new HashSet<string>(o?.HiddenProjects ?? [], StringComparer.Ordinal);
        }
        return (_addedProjects, _hiddenProjects);
    }

    private void SaveProjectPrefs()
    {
        var (added, hidden) = ProjectPrefs();
        string[]? addedOut = added.Count > 0 ? [.. added] : null;
        string[]? hiddenOut = hidden.Count > 0 ? [.. hidden.Order(StringComparer.Ordinal)] : null;
        Persist(o => o with { SidebarProjects = addedOut, HiddenProjects = hiddenOut });
        RebuildSessionGroups();
    }

    /// <summary>Sidebar header: pick a folder and list it as a project (its sessions, or an empty group with its
    /// "new session" button). Removing it earlier is undone.</summary>
    [RelayCommand]
    private async Task AddProjectAsync()
    {
        if (PickFolderRequested is not { } pick || await pick() is not { } picked) return;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(picked));
        var (added, hidden) = ProjectPrefs();
        hidden.Remove(folder);
        added.RemoveAll(p => string.Equals(p, folder, StringComparison.Ordinal));
        added.Add(folder); // the newest last: it is inserted first
        SaveProjectPrefs();
    }

    /// <summary>Group header: take a project off the sidebar after asking. Only the client's list changes.</summary>
    [RelayCommand]
    private void RemoveProject(SessionGroupViewModel? group)
    {
        if (group is null) return;
        var card = new SessionCardViewModel("remove-project", "IconFolder", $"Remove “{group.Name}” from the sidebar?",
            "Its sessions are no longer listed here. Nothing is deleted: the folder and its sessions stay on disk, and adding the folder again lists them again.")
        {
            Detail = group.Cwd,
            State = SessionCardState.Ask,
        };
        card.Primary = new SessionCardAction("Remove", new RelayCommand(() =>
        {
            var (added, hidden) = ProjectPrefs();
            added.RemoveAll(p => string.Equals(p, group.Cwd, StringComparison.Ordinal));
            hidden.Add(group.Cwd);
            SaveProjectPrefs();
            ShowBrief("IconFolder", "Project removed from the sidebar", group.IsCurrentProject
                ? "It stays listed while omp works in it. Nothing was deleted." : "Nothing was deleted.");
        }));
        card.Secondary = new SessionCardAction("Cancel", new RelayCommand(CloseSessionCard));
        ShowCard(card);
    }

    /// <summary>Sidebar row: delete a saved session after asking. The open one goes through omp (the session menu's
    /// Delete, which then starts a new session); another one is deleted as omp's own session picker does.</summary>
    [RelayCommand]
    private void DeleteSessionItem(SessionItemViewModel? item)
    {
        if (item is null) return;
        if (item.Model.Path == _last?.SessionFile)
        {
            if (DeleteSessionCommand.CanExecute(null)) DeleteSessionCommand.Execute(null);
            else ShowBrief("IconTrash", "Not now", "Stop the current run first; then the open session can be deleted.");
            return;
        }
        var card = new SessionCardViewModel("delete", "IconTrash", $"Delete “{item.Title}”?",
            "The conversation's file and its artifacts are deleted. This cannot be undone.")
        {
            Detail = item.Model.Path,
            State = SessionCardState.Ask,
        };
        card.Primary = new SessionCardAction("Delete session", new AsyncRelayCommand(async () =>
        {
            card.State = SessionCardState.Running;
            card.Message = "Deleting…";
            card.Primary = null;
            card.Secondary = null;
            try { await Task.Run(() => SessionCatalog.DeleteSession(item.Model.Path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                card.Finish(false, "Not deleted", e.Message, secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
                return;
            }
            RequestCatalogRefresh();
            ShowBrief("IconTrash", "Session deleted", item.Title);
        }));
        card.Secondary = new SessionCardAction("Cancel", new RelayCommand(CloseSessionCard));
        ShowCard(card);
    }
}
