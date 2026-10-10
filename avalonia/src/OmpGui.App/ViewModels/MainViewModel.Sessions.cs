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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShownProjectPath))] private string _projectPath = "";
    /// <summary>The project folder as the user reads it (~/…, /var not /private/var), for tooltips and menus.</summary>
    public string ShownProjectPath => GitProbe.ShownPath(ProjectPath);
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";

    /// <summary>Raised when the user asked to open a folder; the view shows the platform folder picker.</summary>
    public event Func<Task<string?>>? PickFolderRequested;

    partial void OnSessionFilterChanged(string value) => ApplySessionFilter();

    [RelayCommand]
    private void ToggleSidebar()
    {
        if (IsNarrow) SidebarShownWhileNarrow = !SidebarShownWhileNarrow;
        else IsSidebarVisible = !IsSidebarVisible;
    }

    /// <summary>A new chat in the shown chat's project, with an omp of its own: a run in another chat goes on. The shown
    /// chat is that already while nothing was said or typed in it.</summary>
    [RelayCommand]
    private async Task NewSessionAsync()
    {
        SidebarShownWhileNarrow = false; // the drawer closes on a choice
        if (IsUntouched(_open))
        {
            FocusComposerRequested?.Invoke();
            return;
        }
        await StartNewSessionAsync(NewLaunch(ProjectFolder ?? Session.CurrentLaunch.WorkingDirectory)); // MainViewModel.OpenSessions.cs
    }

    /// <summary>A sidebar row: shows that chat; one that is open already (working or not) is shown as it is, any other
    /// starts its own omp. Never waits for a run and never stops one, so the rows stay enabled (a disabled row greyed the
    /// whole list and dropped its hover, tooltip and right-click at the start and end of every run). Concurrent: a click
    /// on another row while one opens is taken too.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenSessionAsync(SessionItemViewModel? item)
    {
        SidebarShownWhileNarrow = false; // the drawer closes on a choice
        if (item is null) return;
        await OpenSavedSessionAsync(item.Model.Path, item.Model.Cwd); // MainViewModel.OpenSessions.cs
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (PickFolderRequested is not { } pick || await pick() is not { } folder) return;
        // A folder opened on purpose is listed again if it had been removed from the sidebar
        var (_, hidden) = ProjectPrefs();
        if (hidden.Remove(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)))) SaveProjectPrefs();
        await OpenProjectAsync(folder);
    }

    /// <summary>A new session in a project listed in the sidebar: a new chat there, beside the chats already open.</summary>
    [RelayCommand]
    private async Task NewSessionInProjectAsync(SessionGroupViewModel? group)
    {
        if (group is null) return;
        if (group.IsCurrentProject) await NewSessionAsync();
        else await OpenProjectAsync(group.Cwd);
    }

    /// <summary>omp renames the session it has open while it is idle.</summary>
    private bool CanRename() => Phase == SessionPhase.Ready && !IsSigningIn;

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void StartRename()
    {
        // Start from what the header shows (a session named after its first message has no explicit name yet)
        RenameText = _open.Last?.SessionName ?? (SessionTitle == "New session" ? "" : SessionTitle);
        IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        var open = _open;
        try
        {
            IsRenaming = false;
            var name = RenameText.Trim();
            if (name.Length == 0 || name == open.Last?.SessionName) return;
            await open.Controller.RenameSessionAsync(name, _cts.Token);
            ApplyIfShown(open);
            RequestCatalogRefresh();
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
        Apply(Session.Snapshot());
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
        var current = ShownFile;
        foreach (var item in Sessions)
        {
            item.IsCurrent = item.Model.Path == current;
            if (LiveTitleFor(item) is var live && item.LiveTitle != live) item.LiveTitle = live;
        }
        UpdateSessionStatuses();
    }

    /// <summary>
    /// The shown chat's session file: the one it is switching to, else the one its omp reports, else the one it was
    /// started to resume. An omp still starting has reported none yet, and without the last two no row was current
    /// meanwhile (Ctrl+Tab then started over from the first row).
    /// </summary>
    private string? ShownFile => _open.OpeningFile ?? Session.SessionFile ?? Session.CurrentLaunch.ResumeSessionFile;

    /// <summary>The session files of every open chat (as <see cref="ShownFile"/> finds the shown one's).</summary>
    private HashSet<string> OpenFiles()
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in _opens.Values)
            if ((o.OpeningFile ?? o.Controller.SessionFile ?? o.Controller.CurrentLaunch.ResumeSessionFile) is { } f) files.Add(f);
        return files;
    }

    /// <summary>
    /// Each open chat's dot in the sidebar: waiting on the user, working, stopped by an error, or a reply that came while
    /// it was not shown (or, for the shown one, while the window was in the background; cleared when it is back in
    /// front). A chat with no omp open has none.
    /// </summary>
    private void UpdateSessionStatuses()
    {
        Dictionary<string, OpenSession>? others = null;
        foreach (var o in _opens.Values)
            if (o != _open && (o.Controller.SessionFile ?? o.Controller.CurrentLaunch.ResumeSessionFile) is { } file)
                (others ??= new(SessionCatalog.PathComparer))[file] = o;
        foreach (var item in Sessions)
        {
            var open = item.IsCurrent ? _open : others?.GetValueOrDefault(item.Model.Path);
            item.Status = open is null ? SessionStatus.None : StatusOf(open); // MainViewModel.OpenSessions.cs
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
        // omp keeps every project's sessions under one folder: any open chat's file says where (the shown one may not
        // have reported its own yet)
        var file = _open.Last?.SessionFile ?? _opens.Values.Select(o => o.Controller.SessionFile).FirstOrDefault(f => f is not null);
        if (file is null || SessionCatalog.SessionsRootOf(file) is not { } root) return;
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
        var current = ShownFile;
        // Empty sessions (omp creates one per start) are noise, except an open chat's: a new chat with only a draft in it,
        // left for another, has to stay in reach (it would have kept its omp with no way back to it)
        var open = OpenFiles();
        var listed = new HashSet<string>(_catalog.Count, SessionCatalog.PathComparer);
        foreach (var c in _catalog) listed.Add(c.Path);
        // The catalog is newest first (SessionCatalog.ScanAsync); unsaved chats are ordered in by the same key
        var visible = _catalog.Concat(UnsavedChats(listed)).Where(c => !c.IsEmpty || open.Contains(c.Path)).Where(c => f.Length == 0
            || c.Title.Contains(f, StringComparison.CurrentCultureIgnoreCase)
            || c.Cwd.Contains(f, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(c => _pinnedIds.Contains(c.Id)).ThenByDescending(c => c.LastMessageAt).ToList();
        EnsureAgeTimer();
        // Keyed by the session's file (unique, unlike an id a copied file shares): a listed session keeps its row and
        // only its fields change, so a rescan during a run (new LastMessageAt, size, title) does not recreate the rows
        // under the pointer, close a row's menu or move the list's scroll.
        var rows = new Dictionary<string, SessionItemViewModel>(Sessions.Count, StringComparer.Ordinal);
        foreach (var item in Sessions) rows[item.Model.Path] = item;
        var target = new List<SessionItemViewModel>(visible.Count);
        foreach (var c in visible)
        {
            if (rows.Remove(c.Path, out var item)) item.Model = c;
            else item = new SessionItemViewModel(c)
            {
                OpenCommand = OpenSessionCommand, DeleteCommand = DeleteSessionItemCommand, RenameCommand = RenameSessionItemCommand,
                TogglePinCommand = TogglePinItemCommand, CopyPathCommand = CopySessionPathCommand,
            };
            item.IsCurrent = c.Path == current;
            item.IsPinned = _pinnedIds.Contains(c.Id);
            item.LiveTitle = LiveTitleFor(item);
            item.RefreshWhen();
            target.Add(item);
        }
        Reconcile(Sessions, target);
        UpdateSessionStatuses();
        RebuildSessionGroups();
    }

    /// <summary>
    /// The open chats omp has no file for yet, as rows. omp writes a new session's file only once the session has its
    /// first assistant message (session-manager.ts, "lazy gate"), so a scan of the folder misses a new chat until its
    /// first reply is complete, and one with only a draft altogether: without these rows a chat left while it works had
    /// no way back to it. Keyed by the path omp reports for the file it will write, so the scanned row takes over the
    /// same row once the file is there.
    /// </summary>
    private IEnumerable<SessionSummary> UnsavedChats(HashSet<string> listed)
    {
        foreach (var o in _opens.Values)
        {
            var c = o.Controller;
            if (o.OpeningFile is not null || c.SessionFile is not { } file || listed.Contains(file)) continue;
            var s = c.Snapshot();
            if ((s.Cwd ?? c.CurrentLaunch.WorkingDirectory) is not { Length: > 0 } cwd) continue;
            var firstUser = s.Items.OfType<UserItem>().FirstOrDefault()?.Text;
            var named = s.SessionName is { Length: > 0 };
            var title = named ? s.SessionName! : firstUser is { Length: > 0 } u ? SessionCatalogTitle(u) : "New session";
            yield return new SessionSummary(file, s.SessionId ?? Path.GetFileNameWithoutExtension(file), cwd, title, o.OpenedAt,
                IsEmpty: !named && firstUser is null);
        }
    }

    /// <summary>Brings <paramref name="list"/> to <paramref name="target"/> (the same instances, in its order) with
    /// removes, moves and inserts, never a reset: the controls of the items that stay are kept.</summary>
    private static void Reconcile<T>(ObservableCollection<T> list, IReadOnlyList<T> target) where T : class
    {
        var at = new Dictionary<T, int>(target.Count, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < target.Count; i++) at[target[i]] = i;
        for (var i = list.Count - 1; i >= 0; i--)
            if (!at.ContainsKey(list[i])) list.RemoveAt(i);
        for (var i = 0; i < target.Count;)
        {
            var want = target[i];
            if (i < list.Count && ReferenceEquals(list[i], want)) { i++; continue; }
            // The item here belongs further down and the next one is the wanted one: move this one down (one move,
            // the rest stay), instead of pulling each following item up past it.
            if (i + 1 < list.Count && ReferenceEquals(list[i + 1], want))
            {
                list.Move(i, Math.Min(at[list[i]], list.Count - 1));
                continue;
            }
            var from = -1;
            for (var j = i + 1; j < list.Count; j++)
                if (ReferenceEquals(list[j], want)) { from = j; break; }
            if (from >= 0) list.Move(from, i);
            else list.Insert(i, want);
            i++;
        }
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

    private SessionGroupViewModel? _pinnedGroup;

    /// <summary>Groups the listed sessions by project; the groups and their rows that stay are kept (see
    /// <see cref="Reconcile{T}"/>), so a refresh changes only what moved.</summary>
    private void RebuildSessionGroups()
    {
        var project = _open.Last?.Cwd ?? ProjectPath;
        var (added, hidden) = ProjectPrefs();
        var known = new Dictionary<string, SessionGroupViewModel>(SessionCatalog.PathComparer);
        foreach (var g in SessionGroups)
            if (g.IsProject) known[g.Cwd] = g;
        SessionGroupViewModel GroupFor(string cwd)
        {
            if (!known.Remove(cwd, out var group)) return NewGroup(cwd, project);
            group.IsCurrentProject = SessionCatalog.PathComparer.Equals(cwd, project);
            return group;
        }
        var groups = new List<(SessionGroupViewModel Group, List<SessionItemViewModel> Items, DateTimeOffset Newest)>();
        var pinned = new List<SessionItemViewModel>();
        foreach (var item in Sessions)
        {
            // A removed project stays out of the list, the open chat's too (it had stayed until the app restarted)
            if (hidden.Contains(item.Cwd)) continue;
            var i = groups.FindIndex(x => SessionCatalog.PathComparer.Equals(x.Group.Cwd, item.Cwd));
            if (i < 0) groups.Add((GroupFor(item.Cwd), [], item.Model.LastMessageAt));
            else if (item.Model.LastMessageAt > groups[i].Newest) groups[i] = groups[i] with { Newest = item.Model.LastMessageAt };
            // Pinned sessions are listed once, under Pinned
            if (item.IsPinned) pinned.Add(item);
            else groups[i < 0 ? ^1 : i].Items.Add(item);
        }
        foreach (var g in groups) Reconcile(g.Group.Items, g.Items);
        // By the newest message in each; a project whose sessions are all pinned keeps its header only while it is open
        // or was added from the sidebar (its "+" stays in reach). Folders added from the sidebar that have no session
        // yet go first, as just added.
        var ordered = groups.Where(g => g.Items.Count > 0 || (g.Group.IsCurrentProject && !hidden.Contains(g.Group.Cwd)) || added.Contains(g.Group.Cwd, SessionCatalog.PathComparer))
            .OrderByDescending(g => g.Newest).Select(g => g.Group).ToList();
        if (SessionFilter.Trim().Length == 0)
            foreach (var p in added)
                if (!hidden.Contains(p) && !ordered.Any(g => SessionCatalog.PathComparer.Equals(g.Cwd, p)))
                {
                    var group = GroupFor(p);
                    Reconcile(group.Items, []);
                    ordered.Insert(0, group);
                }
        _pinnedGroup ??= new SessionGroupViewModel("", false, isPinnedGroup: true);
        Reconcile(_pinnedGroup.Items, pinned);
        if (pinned.Count > 0) ordered.Insert(0, _pinnedGroup);
        Reconcile(SessionGroups, ordered);
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

    /// <summary>
    /// Group header: take a project off the sidebar. A project without chats goes at once (Undo on the card); one with
    /// chats asks first: keep its chats on disk (only the list changes), or delete them too.
    /// </summary>
    [RelayCommand]
    private void RemoveProject(SessionGroupViewModel? group)
    {
        if (group is not { IsProject: true }) return;
        var chats = Sessions.Where(s => SessionCatalog.PathComparer.Equals(s.Cwd, group.Cwd)).ToList();
        if (chats.Count == 0)
        {
            HideProject(group);
            return;
        }
        var card = new SessionCardViewModel("delete", "IconFolder", $"Remove “{group.Name}”?",
            (chats.Count == 1 ? "It has 1 chat." : $"It has {chats.Count} chats.")
            + " Remove it from the sidebar and keep the chats on disk, or delete the chats too (their files and artifacts; this cannot be undone). The folder itself is never deleted.")
        {
            Detail = group.Cwd,
            State = SessionCardState.Ask,
        };
        card.Primary = new SessionCardAction("Remove from sidebar", new RelayCommand(() =>
        {
            CloseSessionCard();
            HideProject(group);
        }));
        card.Tertiary = new SessionCardAction(chats.Count == 1 ? "Delete the chat too" : $"Delete {chats.Count} chats too",
            new AsyncRelayCommand(() => DeleteProjectChatsAsync(card, group, chats)));
        card.Secondary = new SessionCardAction("Cancel", new RelayCommand(CloseSessionCard));
        ShowCard(card);
    }

    /// <summary>Deletes a project's chats (each open one closed first; the one on screen gets a new session), then hides it.</summary>
    private async Task DeleteProjectChatsAsync(SessionCardViewModel card, SessionGroupViewModel group, List<SessionItemViewModel> chats)
    {
        card.State = SessionCardState.Running;
        card.Message = "Deleting…";
        card.Primary = card.Secondary = card.Tertiary = null;
        var failed = new List<string>();
        foreach (var chat in chats)
        {
            if (FindOpen(chat.Model.Path) is { } open)
            {
                if (open == _open) await NewSessionAsync(); // off the chat on screen first, so it can be closed
                if (FindOpen(chat.Model.Path) is { } still) await CloseSessionAsync(still);
            }
            try { await Task.Run(() => SessionCatalog.DeleteSession(chat.Model.Path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed.Add($"{chat.Title}: {e.Message}"); }
        }
        RequestCatalogRefresh();
        HideProject(group, announce: false);
        if (failed.Count > 0)
            card.Finish(false, "Some chats were not deleted", string.Join("\n", failed), secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
        else ShowBrief("IconTrash", $"“{group.Name}” removed", chats.Count == 1 ? "1 chat deleted" : $"{chats.Count} chats deleted");
    }

    /// <summary>Only the client's list changes (nothing on disk), so it is done at once, with Undo on the card that says so.</summary>
    private void HideProject(SessionGroupViewModel group, bool announce = true)
    {
        var (added, hidden) = ProjectPrefs();
        var wasAdded = added.FindIndex(p => SessionCatalog.PathComparer.Equals(p, group.Cwd));
        if (wasAdded >= 0) added.RemoveAt(wasAdded);
        hidden.Add(group.Cwd);
        SaveProjectPrefs();
        if (!announce) return;
        var card = new SessionCardViewModel("brief", "IconFolder", $"“{group.Name}” removed from the sidebar",
            "Nothing was deleted: add the folder again to list its sessions.");
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
            await OpenSessionCommand.ExecuteAsync(item);
            if (_open.Last?.SessionFile != item.Model.Path) return;
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
        if (item.Model.Path == _open.Last?.SessionFile) IsSessionPinned = pin;
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
        if (order.Count == 0) return false;
        // From the chat itself: a chat whose omp is still starting has no current row yet
        var current = ShownFile;
        var at = order.FindIndex(i => i.Model.Path == current);
        var next = at < 0 ? (direction > 0 ? 0 : order.Count - 1) : ((at + direction) % order.Count + order.Count) % order.Count;
        if (next == at) return false;
        _ = OpenSessionCommand.ExecuteAsync(order[next]);
        return true;
    }

    /// <summary>
    /// Sidebar row: delete a saved session after asking. The shown one goes through omp (the session menu's Delete, which
    /// then starts a new session); another one is deleted as omp's own session picker does, its omp stopped first when the
    /// chat is open in the background (omp would write the file again), which ends its run when it is working.
    /// </summary>
    [RelayCommand]
    private void DeleteSessionItem(SessionItemViewModel? item)
    {
        if (item is null) return;
        if (item.Model.Path == ShownFile)
        {
            if (DeleteSessionCommand.CanExecute(null)) DeleteSessionCommand.Execute(null);
            else ShowBrief("IconTrash", "Not now", "Stop the current run first; then the open session can be deleted.");
            return;
        }
        var working = FindOpen(item.Model.Path) is { } open && !SessionHost.IsIdle(open.Controller.Snapshot());
        var card = new SessionCardViewModel("delete", "IconTrash", $"Delete “{item.Title}”?",
            working ? "omp is working in it: deleting stops its run, then the conversation's file and its artifacts are deleted. This cannot be undone."
                : "The conversation's file and its artifacts are deleted. This cannot be undone.")
        {
            Detail = item.Model.Path,
            State = SessionCardState.Ask,
        };
        card.Primary = new SessionCardAction(working ? "Stop and delete" : "Delete session", new AsyncRelayCommand(async () =>
        {
            card.State = SessionCardState.Running;
            card.Message = "Deleting…";
            card.Primary = null;
            card.Secondary = null;
            if (FindOpen(item.Model.Path) is { } still)
            {
                if (still == _open)
                {
                    // Opened meanwhile: the shown chat is deleted from its own menu, where omp starts a new session after it
                    card.Finish(false, "Not deleted", "The session is on screen now: use Delete session in its menu (the title).",
                        secondary: new SessionCardAction("Dismiss", new RelayCommand(CloseSessionCard)));
                    return;
                }
                await CloseSessionAsync(still); // MainViewModel.OpenSessions.cs
            }
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
