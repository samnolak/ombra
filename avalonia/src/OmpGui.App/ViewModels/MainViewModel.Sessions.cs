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

    /// <summary>Wide: the user's choice. Narrow: hidden unless the user opened it. Never beside the settings page,
    /// which has its own navigation in its place.</summary>
    public bool ShowSidebar => !IsSettingsOpen && (IsNarrow ? SidebarShownWhileNarrow : IsSidebarVisible);

    // Shortcuts as the platform writes them (⌘ on macOS, Ctrl elsewhere), for tooltips and the sidebar's hints.
    public static string HideSidebarTip { get; } = $"Hide sidebar ({CommandKey("B")})";
    public static string ShowSidebarTip { get; } = $"Show sidebar ({CommandKey("B")})";
    public static string NewSessionShortcut { get; } = CommandKey("N");
    public static string SettingsShortcut { get; } = CommandKey(",");
    public static string SettingsTip { get; } = $"Settings ({SettingsShortcut})";

    private static string CommandKey(string key) => OperatingSystem.IsMacOS() ? "⌘" + key : "Ctrl+" + key;

    /// <summary>A search that matches no session: the list says so instead of standing empty.</summary>
    public bool ShowNoSessionMatch => SessionFilter.Trim().Length > 0 && SessionGroups.Count == 0;

    public const double SidebarMinWidth = 220, SidebarMaxWidth = 480, SidebarDefaultWidth = 260;
    private double? _sidebarWidth;

    /// <summary>The docked sidebar's width: dragged by its edge (MainWindow), kept in the client's settings.</summary>
    public double SidebarWidth
    {
        get => _sidebarWidth ??= LoadSidebarWidth();
        set
        {
            var clamped = Math.Round(Math.Clamp(value, SidebarMinWidth, SidebarMaxWidth));
            if (_sidebarWidth == clamped) return;
            _sidebarWidth = clamped;
            OnPropertyChanged();
        }
    }

    private double LoadSidebarWidth()
    {
        double? saved = null;
        try { saved = _settings?.Load().SidebarWidth; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The default width; the settings page reports the file's problem when it opens.
        }
        return saved is { } w && double.IsFinite(w) ? Math.Clamp(w, SidebarMinWidth, SidebarMaxWidth) : SidebarDefaultWidth;
    }

    /// <summary>A drag of the sidebar's edge ended: its width is kept for the next start.</summary>
    internal void SaveSidebarWidth()
    {
        var width = SidebarWidth;
        Persist(o => o with { SidebarWidth = width == SidebarDefaultWidth ? null : width });
    }

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
        _openSessionRunning = s.Phase is SessionPhase.Running or SessionPhase.Aborting;
        _openSessionAsking = s.Dialogs.Count > 0;
        foreach (var item in Sessions)
        {
            item.IsCurrent = item.Model.Path == s.SessionFile;
            if (LiveTitleFor(item) is var live && item.LiveTitle != live) item.LiveTitle = live;
        }
        UpdateSessionStatuses();
    }

    private bool _openSessionRunning, _openSessionAsking;

    /// <summary>The open session's dot: waiting on the user, working, or a reply that came while the window was in
    /// the background (<see cref="NeedsAttention"/>, cleared when it is back in front).</summary>
    private SessionStatus OpenSessionStatus =>
        _openSessionAsking ? SessionStatus.Waiting
        : _openSessionRunning ? SessionStatus.Running
        : NeedsAttention ? SessionStatus.Unread
        : SessionStatus.None;

    private void UpdateSessionStatuses()
    {
        var status = OpenSessionStatus;
        foreach (var item in Sessions) item.Status = item.IsCurrent ? status : SessionStatus.None;
    }

    partial void OnNeedsAttentionChanged(bool value) => UpdateSessionStatuses();

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
                    var pinsVersion = Volatile.Read(ref _pinsVersion);
                    var pins = ReadPinnedSessionIds(file); // omp's pinned sessions come first (MainViewModel.Session.cs)
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _catalog = list;
                        // Not over a pin made from the sidebar after this read (the next scan reads it)
                        if (pinsVersion == _pinsVersion) _pinnedIds = pins;
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
        EnsureAgeTimer();
        // Replace in place only when something changed, so the list keeps its scroll position.
        if (visible.Count == Sessions.Count && visible.Zip(Sessions).All(p => p.First == p.Second.Model))
        {
            var pinsChanged = false;
            foreach (var item in Sessions)
            {
                item.IsCurrent = item.Model.Path == current;
                var pinned = _pinnedIds.Contains(item.Model.Id);
                pinsChanged |= item.IsPinned != pinned;
                item.IsPinned = pinned;
                item.LiveTitle = LiveTitleFor(item);
                item.RefreshWhen();
            }
            UpdateSessionStatuses();
            if (pinsChanged) RebuildSessionGroups();
            return;
        }
        Sessions.Clear();
        foreach (var c in visible)
        {
            var item = new SessionItemViewModel(c)
            {
                IsCurrent = c.Path == current, IsPinned = _pinnedIds.Contains(c.Id),
                OpenCommand = OpenSessionCommand, DeleteCommand = DeleteSessionItemCommand, RenameCommand = RenameSessionItemCommand,
                TogglePinCommand = TogglePinItemCommand, CopyPathCommand = CopySessionPathCommand,
            };
            item.LiveTitle = LiveTitleFor(item);
            Sessions.Add(item);
        }
        UpdateSessionStatuses();
        RebuildSessionGroups();
    }

    private DispatcherTimer? _ageTimer;

    /// <summary>The rows' ages ("5m") move on by themselves: re-read every minute while the window is open.</summary>
    private void EnsureAgeTimer()
    {
        if (_ageTimer is not null || _cts.IsCancellationRequested) return;
        _ageTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _ageTimer.Tick += (_, _) =>
        {
            if (_cts.IsCancellationRequested)
            {
                _ageTimer.Stop();
                return;
            }
            foreach (var item in Sessions) item.RefreshWhen();
        };
        _ageTimer.Start();
    }

    /// <summary>The open session's row reads like the header, before omp's file (and the list) catch up.</summary>
    private string? LiveTitleFor(SessionItemViewModel item) =>
        item.IsCurrent && SessionTitle != "New session" && SessionTitle != item.Model.Title ? SessionTitle : null;

    private void RebuildSessionGroups()
    {
        var project = _last?.Cwd ?? ProjectPath;
        var (added, hidden) = ProjectPrefs();
        SessionGroups.Clear();
        var groups = new List<(SessionGroupViewModel Group, DateTimeOffset Newest)>();
        var pinnedGroup = new SessionGroupViewModel("", false, isPinnedGroup: true);
        foreach (var item in Sessions)
        {
            // A removed project stays out of the list, except while omp works in it.
            if (hidden.Contains(item.Cwd) && !SessionCatalog.PathComparer.Equals(item.Cwd, project)) continue;
            var i = groups.FindIndex(x => SessionCatalog.PathComparer.Equals(x.Group.Cwd, item.Cwd));
            if (i < 0) groups.Add((NewGroup(item.Cwd, project), item.Model.LastMessageAt));
            else if (item.Model.LastMessageAt > groups[i].Newest) groups[i] = groups[i] with { Newest = item.Model.LastMessageAt };
            // Pinned sessions are listed once, under Pinned
            if (item.IsPinned) pinnedGroup.Items.Add(item);
            else groups[i < 0 ? ^1 : i].Group.Items.Add(item);
        }
        // By the newest message in each; a project whose sessions are all pinned keeps its header only while it is open
        // or was added from the sidebar (its "+" stays in reach). Folders added from the sidebar that have no session
        // yet go first, as just added.
        var ordered = groups.Where(g => g.Group.Items.Count > 0 || g.Group.IsCurrentProject || added.Contains(g.Group.Cwd, SessionCatalog.PathComparer))
            .OrderByDescending(g => g.Newest).Select(g => g.Group).ToList();
        if (SessionFilter.Trim().Length == 0)
            foreach (var p in added)
                if (!hidden.Contains(p) && !ordered.Any(g => SessionCatalog.PathComparer.Equals(g.Cwd, p))) ordered.Insert(0, NewGroup(p, project));
        if (pinnedGroup.Items.Count > 0) SessionGroups.Add(pinnedGroup);
        foreach (var g in ordered) SessionGroups.Add(g);
        OnPropertyChanged(nameof(ShowNoSessionMatch));
    }

    private SessionGroupViewModel NewGroup(string cwd, string? project) =>
        new(cwd, SessionCatalog.PathComparer.Equals(cwd, project))
        {
            NewSessionCommand = NewSessionInProjectCommand,
            RemoveCommand = RemoveProjectCommand,
            CopyPathCommand = CopyProjectPathCommand,
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
            _hiddenProjects = new HashSet<string>(o?.HiddenProjects ?? [], SessionCatalog.PathComparer);
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
        added.RemoveAll(p => SessionCatalog.PathComparer.Equals(p, folder));
        added.Add(folder); // the newest last: it is inserted first
        SaveProjectPrefs();
    }

    /// <summary>Group header: take a project off the sidebar. Only the client's list changes (nothing on disk), so it
    /// is done at once, with Undo on the card that says so.</summary>
    [RelayCommand]
    private void RemoveProject(SessionGroupViewModel? group)
    {
        if (group is not { IsProject: true }) return;
        var (added, hidden) = ProjectPrefs();
        var wasAdded = added.FindIndex(p => SessionCatalog.PathComparer.Equals(p, group.Cwd));
        if (wasAdded >= 0) added.RemoveAt(wasAdded);
        hidden.Add(group.Cwd);
        SaveProjectPrefs();
        var card = new SessionCardViewModel("brief", "IconFolder", $"“{group.Name}” removed from the sidebar",
            group.IsCurrentProject ? "It stays listed while omp works in it. Nothing was deleted." : "Nothing was deleted: add the folder again to list its sessions.");
        card.Primary = new SessionCardAction("Undo", new RelayCommand(() =>
        {
            var (a, h) = ProjectPrefs();
            h.Remove(group.Cwd);
            if (wasAdded >= 0 && !a.Contains(group.Cwd, SessionCatalog.PathComparer)) a.Insert(Math.Min(wasAdded, a.Count), group.Cwd);
            SaveProjectPrefs();
            CloseSessionCard();
        }));
        ShowCard(card);
        _cardTimer = new DispatcherTimer(TimeSpan.FromSeconds(8), DispatcherPriority.Background, (_, _) =>
        {
            if (ReferenceEquals(SessionCard, card)) CloseSessionCard();
        });
        _cardTimer.Start();
    }

    [RelayCommand]
    private void CopyProjectPath(SessionGroupViewModel? group)
    {
        if (group is not { IsProject: true }) return;
        CopyTextRequested?.Invoke(group.Cwd);
        ShowBrief("IconCopy", "Project path copied", "", group.Cwd);
    }

    // ───────────── Row menu: rename, pin, copy path; cycling with Ctrl+Tab ─────────────

    /// <summary>Rename from the sidebar: omp renames the open session only, so another one is opened first; the
    /// header's name field then takes the new name.</summary>
    [RelayCommand]
    private async Task RenameSessionItemAsync(SessionItemViewModel? item)
    {
        if (item is null) return;
        if (!item.IsCurrent)
        {
            if (!OpenSessionCommand.CanExecute(item))
            {
                ShowBrief("IconPencil", "Not now", "Stop the current run first; then another session can be opened and renamed.");
                return;
            }
            await OpenSessionCommand.ExecuteAsync(item);
            if (_last?.SessionFile != item.Model.Path) return;
        }
        if (StartRenameCommand.CanExecute(null)) StartRenameCommand.Execute(null);
        else ShowBrief("IconPencil", "Not now", "Rename the session once omp has finished the current run.");
    }

    /// <summary>Pin or unpin in omp's own pin list, so omp's session picker agrees.</summary>
    private int _pinsVersion;

    [RelayCommand]
    private async Task TogglePinItemAsync(SessionItemViewModel? item)
    {
        if (item is null || SessionPins.PathFor(item.Model.Path) is not { } file) return;
        var pin = !item.IsPinned;
        try
        {
            var pins = await Task.Run(() => SessionPins.Set(file, item.Model.Id, pin));
            Interlocked.Increment(ref _pinsVersion);
            _pinnedIds = pins;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowBrief("IconPin", pin ? "Not pinned" : "Not unpinned", e.Message);
            return;
        }
        if (item.Model.Path == _last?.SessionFile) IsSessionPinned = pin;
        ApplySessionFilter();
        RequestCatalogRefresh(); // a scan under way read the pins before this change: one more scan reads them again
    }

    [RelayCommand]
    private void CopySessionPath(SessionItemViewModel? item)
    {
        if (item is null) return;
        CopyTextRequested?.Invoke(item.Model.Path);
        ShowBrief("IconCopy", "Session file path copied", "omp keeps the whole conversation in this file (JSON lines).", item.Model.Path);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: the next or previous session in the sidebar's order (wrapping).</summary>
    internal bool CycleSession(int direction)
    {
        var order = SessionGroups.SelectMany(g => g.Items).ToList();
        if (order.Count == 0 || !CanChangeSession()) return false;
        var at = order.FindIndex(i => i.IsCurrent);
        var next = at < 0 ? (direction > 0 ? 0 : order.Count - 1) : ((at + direction) % order.Count + order.Count) % order.Count;
        if (next == at) return false;
        _ = OpenSessionCommand.ExecuteAsync(order[next]);
        return true;
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
