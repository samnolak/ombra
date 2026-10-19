using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

/// <summary>Sessions: sidebar list, new / open / rename, project folder.</summary>
public sealed partial class MainViewModel
{
    private IReadOnlyList<SessionSummary> _catalog = [];
    // Requests counted, scans run one at a time; a request that arrives during a scan causes one more scan.
    private int _catalogRequests;
    private sealed record CatalogRequest(string Root, string File);
    private CatalogRequest? _catalogRequest;
    private string? _catalogRoot;
    private bool _catalogLoaded;
    internal Func<string, CancellationToken, Task<SessionCatalogScanResult>> ScanCatalogAsync { get; set; }
    internal Task CatalogRefreshTask { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCatalogNotice))]
    private string? _catalogNotice;
    public bool HasCatalogNotice => CatalogNotice is not null;

    [RelayCommand]
    private void RetryCatalog() => RequestCatalogRefresh();

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

    /// <summary>Wide: the user's choice. Narrow: hidden unless the user opened it. Only beside the conversation
    /// (<see cref="IsChatsRailOn"/>): never beside the settings page, which has its own navigation in its place, nor
    /// beside the Team or Browser page, pages of their own that take the sidebar's width too.</summary>
    public bool ShowSidebar => IsChatsRailOn && (IsNarrow ? SidebarShownWhileNarrow : IsSidebarVisible);

    // Shortcuts as the platform writes them (⌘ on macOS, Ctrl elsewhere), for tooltips and the sidebar's hints.
    public static string HideSidebarTip { get; } = Loc.F("Hide sidebar ({0})", CommandKey("B"));
    public static string ShowSidebarTip { get; } = Loc.F("Show sidebar ({0})", CommandKey("B"));
    public static string NewChatShortcut { get; } = CommandKey("N");
    public static string SettingsShortcut { get; } = CommandKey(",");
    public static string SettingsTip { get; } = Loc.F("Settings ({0})", SettingsShortcut);

    private static string CommandKey(string key) => OperatingSystem.IsMacOS() ? "⌘" + key : "Ctrl+" + key;

    /// <summary>A search that matches no session: the list says so instead of standing empty (once the messages are searched too).</summary>
    public bool ShowNoSessionMatch => (SessionFilter.Trim().Length > 0 || StatusFilter != SessionStatusFilter.All) && SessionGroups.Count == 0 && !IsSearchingContent && !IsLoadingSessions;

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
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowSessionSearch), nameof(ShowStatusFilters))] private string _sessionFilter = "";
    /// <summary>The sidebar header's search icon opened the search field (it also shows while a search is typed).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowSessionSearch), nameof(ShowStatusFilters))] private bool _isSessionSearchOpen;
    public bool ShowSessionSearch => IsSessionSearchOpen || SessionFilter.Length > 0;
    [ObservableProperty] private string _sessionTitle = UntitledSession;
    [ObservableProperty] private string _projectName = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShownProjectPath))] private string _projectPath = "";
    /// <summary>The project folder as the user reads it (~/…, /var not /private/var), for tooltips and menus.</summary>
    public string ShownProjectPath => GitProbe.ShownPath(ProjectPath);
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";

    /// <summary>Raised when the user asked to open a folder; the view shows the platform folder picker.</summary>
    public event Func<Task<string?>>? PickFolderRequested;

    /// <summary>The folder the user picks in the platform folder picker; null when cancelled or there is no view.</summary>
    internal Task<string?> PickFolderAsync() => PickFolderRequested is { } pick ? pick() : Task.FromResult<string?>(null);

    partial void OnSessionFilterChanged(string value)
    {
        RequestContentSearch(debounce: true); // MainViewModel.SessionSearch.cs
        ApplySessionFilter();
    }

    /// <summary>The sidebar header's search icon: opens the search field, or closes it and clears the search.</summary>
    [RelayCommand]
    private void ToggleSessionSearch()
    {
        if (ShowSessionSearch) { IsSessionSearchOpen = false; SessionFilter = ""; }
        else IsSessionSearchOpen = true;
    }

    /// <summary>⌘B and the panel buttons: only where there is a sidebar (the conversation); off on the pages.</summary>
    [RelayCommand(CanExecute = nameof(IsChatsRailOn))]
    private void ToggleSidebar()
    {
        if (IsNarrow) SidebarShownWhileNarrow = !SidebarShownWhileNarrow;
        else IsSidebarVisible = !IsSidebarVisible;
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
        // Found in its messages: opened at the match (MainViewModel.SessionSearch.cs)
        var query = item.ContentMatch is null ? "" : SessionFilter.Trim();
        await OpenSavedSessionAsync(item.Model.Path, item.Model.Cwd); // MainViewModel.OpenSessions.cs
        if (SessionCatalog.PathComparer.Equals(ShownFile, item.Model.Path))
        {
            _open.Unread = false;
            ClearMarkedUnread(item.Model.Path);
            UpdateSessionStatuses();
        }
        RevealContentMatch(item, query);
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

    /// <summary>A new session in a group of the sidebar: a new chat in that project (beside the chats already open), or a
    /// new chat outside any project from the Chats group.</summary>
    [RelayCommand]
    private async Task NewSessionInProjectAsync(SessionGroupViewModel? group)
    {
        if (group is null) return;
        if (group.IsChatsGroup) await NewChatAsync(); // MainViewModel.Chats.cs
        else if (group.IsCurrentProject) await NewSessionHereAsync();
        else await OpenProjectAsync(group.Cwd);
    }

    /// <summary>omp renames the session it has open while it is idle.</summary>
    private bool CanRename() => !IsReadOnly && Phase == SessionPhase.Ready && !IsSigningIn && !IsSavingRename;

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void StartRename()
    {
        // Start from what the header shows (a session named after its first message has no explicit name yet)
        RenameText = _open.Last?.SessionName ?? (IsUntitled(SessionTitle) ? "" : SessionTitle);
        IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (IsReadOnly) return;
        var open = _open;
        try
        {
            IsRenaming = false;
            var name = RenameText.Trim();
            if (name.Length == 0 || name == open.Last?.SessionName) return;
            IsSavingRename = true;
            await open.Controller.RenameSessionAsync(name, _cts.Token);
            ApplyIfShown(open);
            RequestCatalogRefresh();
            if (open == _open && open.Controller.Snapshot().SessionName == name)
                ShowBrief("IconPencil", Loc.T("Session renamed"), name);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the request was abandoned on purpose.
        }
        finally { IsSavingRename = false; }
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    private void AfterSessionChange()
    {
        Apply(Session.Snapshot());
        RequestCatalogRefresh();
    }

    private static string? KnownSessionTitle(SessionSnapshot s) =>
        s.SessionName is { Length: > 0 } name ? name
        : s.SessionTitleHint is { Length: > 0 } hint ? hint
        : !s.History.HasOlder && s.Items.OfType<UserItem>().FirstOrDefault()?.Text is { Length: > 0 } first
            ? SessionCatalogTitle(first) : null;

    /// <summary>Header text and the current-session mark; called from Apply.</summary>
    private void ApplySessionInfo(SessionSnapshot s)
    {
        // A chat outside any project is one from its start (before omp reports its folder)
        IsProjectless = Chats.Contains(s.Cwd ?? Session.CurrentLaunch.WorkingDirectory);
        SessionTitle = KnownSessionTitle(s) ?? (IsProjectless ? UntitledChat : UntitledSession);
        ProjectName = IsProjectless ? "" : ProjectDisplayName(s.Cwd) ?? SessionItemViewModel.ProjectName(s.Cwd);
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

    /// <summary>What the header and the sidebar call a session nothing was said in yet.</summary>
    private static string UntitledSession => Loc.T("New session");
    private static string UntitledChat => Loc.T("New chat");

    /// <summary>Also the English names: the session catalog (ClientCore) calls an untitled session "New session".</summary>
    private static bool IsUntitled(string title) => title == UntitledSession || title == UntitledChat || title is "New session" or "New chat";

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
        // A chip narrows the list by status: a status that changed may add or take out a row
        if (StatusFilter != SessionStatusFilter.All)
        {
            ApplySessionFilter();
            return;
        }
        SetSessionStatuses();
    }

    private void SetSessionStatuses()
    {
        var opens = OpenChatsByFile();
        foreach (var item in Sessions)
        {
            item.Status = StatusOfFile(opens, item.Model.Path);
            item.IsMarkedUnread = MarkedUnread().Contains(item.Model.Path) || opens?.GetValueOrDefault(item.Model.Path)?.Unread == true;
        }
    }

    /// <summary>Each open chat by its session file (a live chat over a read-only view of the same file).</summary>
    private Dictionary<string, OpenSession>? OpenChatsByFile()
    {
        Dictionary<string, OpenSession>? opens = null;
        foreach (var o in _opens.Values)
            if ((o.Controller.SessionFile ?? o.Controller.CurrentLaunch.ResumeSessionFile) is { } file)
            {
                opens ??= new(SessionCatalog.PathComparer);
                // A read-only view of the same source must not hide a live chat's running/waiting dot.
                if (!o.Controller.CurrentLaunch.ReadOnly || !opens.ContainsKey(file)) opens[file] = o;
            }
        return opens;
    }

    private SessionStatus StatusOfFile(Dictionary<string, OpenSession>? opens, string file)
    {
        var status = opens?.GetValueOrDefault(file) is { } open ? StatusOf(open) : SessionStatus.None;
        return status == SessionStatus.None && MarkedUnread().Contains(file) ? SessionStatus.Unread : status;
    }

    private static string SessionCatalogTitle(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= 80 ? line : line[..GraphemeText.PrefixLength(line, 80)] + "…";
    }

    /// <summary>Rescans omp's session folder off the UI thread; overlapping requests collapse into one more scan.</summary>
    internal void RequestCatalogRefresh()
    {
        if (IsClosing) return;
        var ct = _cts.Token;
        // omp keeps every project's sessions under one folder: any open chat's file says where (the shown one may not
        // have reported its own yet)
        var file = _open.Last?.SessionFile ?? _opens.Values.Select(o => o.Controller.SessionFile).FirstOrDefault(f => f is not null);
        if (file is null || SessionCatalog.SessionsRootOf(file) is not { } root) return;
        if (_catalogRoot is null || !SessionCatalog.SamePath(_catalogRoot, root))
        {
            // A different profile/account must never inherit rows or pins from the old root, even on failure.
            _catalog = [];
            _catalogLoaded = false;
            _catalogRoot = root;
            _pinnedIds = [];
            CatalogNotice = null;
            ApplySessionFilter();
        }
        Volatile.Write(ref _catalogRequest, new CatalogRequest(root, file));
        IsLoadingSessions = !_catalogLoaded;
        // Only the request that finds the counter at zero starts the worker; the worker scans until it has
        // covered every request made before its last scan began.
        if (Interlocked.Increment(ref _catalogRequests) != 1) return;
        CatalogRefreshTask = Task.Run(async () =>
        {
            try
            {
                CatalogRequest? previousRequest = null;
                var retries = 0;
                while (true)
                {
                    var seen = Volatile.Read(ref _catalogRequests);
                    var request = Volatile.Read(ref _catalogRequest)!;
                    if (!ReferenceEquals(previousRequest, request)) retries = 0;
                    previousRequest = request;
                    var result = await ScanCatalogAsync(request.Root, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    var pinsVersion = Volatile.Read(ref _pinsVersion);
                    var pins = ReadPinnedSessionIds(request.File); // omp's pinned sessions come first (MainViewModel.Session.cs)
                    await UiDispatcher.InvokeAsync(() =>
                    {
                        if (IsClosing || ct.IsCancellationRequested || !ReferenceEquals(request, Volatile.Read(ref _catalogRequest))) return;
                        _catalogLoaded = true;
                        IsLoadingSessions = false;
                        _catalog = result.Merge(_catalogRoot, _catalog);
                        _catalogRoot = result.Root;
                        CanShowOlder = result.IsCapped;
                        CatalogNotice = result.IsAuthoritative || result.Errors.Count == 0 ? null
                            : Loc.F("Sessions may be out of date. Keeping previously listed sessions. {0}", result.Errors[0].Message);
                        // Not over a pin made from the sidebar after this read (the next scan reads it)
                        if (pinsVersion == _pinsVersion) _pinnedIds = pins;
                        ApplySessionFilter();
                        // The messages of sessions read again are searched again (unchanged journals are not re-read)
                        if (SessionFilter.Trim().Length > 0) RequestContentSearch(debounce: false);
                    }, DispatcherPriority.Background, ct);
                    // Retry inside this same coalescing worker, with the window's lifetime token.
                    // A cap is not transient; only failures receive the three bounded retries.
                    if (result.Errors.Count > 0 && retries < 3 && ReferenceEquals(request, Volatile.Read(ref _catalogRequest)))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << (2 * retries++))), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (Interlocked.CompareExchange(ref _catalogRequests, 0, seen) == seen) return;
                }
            }
            catch (OperationCanceledException) { Interlocked.Exchange(ref _catalogRequests, 0); }
            finally { await UiDispatcher.InvokeAsync(() => IsLoadingSessions = false); }
        });
    }

    private void ApplySessionFilter()
    {
        var f = SessionFilter.Trim();
        var current = ShownFile;
        var candidates = ListableSessions();
        var visible = FilterSessions(candidates, f); // MainViewModel.SessionSearch.cs
        if (StatusFilter != SessionStatusFilter.All)
        {
            var opens = OpenChatsByFile();
            visible = [.. visible.Where(v => Passes(StatusFilter, StatusOfFile(opens, v.Summary.Path)))];
        }
        var archived = Archived();
        EnsureAgeTimer();
        // Keyed by the session's file (unique, unlike an id a copied file shares): a listed session keeps its row and
        // only its fields change, so a rescan during a run (new LastMessageAt, size, title) does not recreate the rows
        // under the pointer, close a row's menu or move the list's scroll.
        var rows = new Dictionary<string, SessionItemViewModel>(Sessions.Count, StringComparer.Ordinal);
        foreach (var item in Sessions) rows[item.Model.Path] = item;
        var target = new List<SessionItemViewModel>(visible.Count);
        foreach (var (c, match) in visible)
        {
            if (rows.Remove(c.Path, out var item)) item.Model = c;
            else item = new SessionItemViewModel(c)
            {
                OpenCommand = OpenSessionCommand, DeleteCommand = DeleteSessionItemCommand, RenameCommand = RenameSessionItemCommand,
                TogglePinCommand = TogglePinItemCommand, CopyPathCommand = CopySessionPathCommand, CopyIdCommand = CopySessionItemIdCommand,
                ExportCommand = ExportHtmlCommand, ShareCommand = ShareSessionCommand, ToggleArchiveCommand = ToggleArchiveCommand,
                ToggleReadCommand = ToggleReadItemCommand, MarkAllReadCommand = MarkAllReadCommand,
                RevealCommand = RevealSessionItemCommand, OpenTerminalCommand = TerminalSessionItemCommand,
            };
            item.IsCurrent = c.Path == current;
            item.ContentMatch = match;
            item.IsPinned = _pinnedIds.Contains(c.Id);
            item.IsArchived = archived.Contains(c.Path);
            item.LiveTitle = LiveTitleFor(item);
            item.RefreshWhen();
            target.Add(item);
        }
        Reconcile(Sessions, target);
        SetSessionStatuses();
        RebuildSessionGroups();
    }

    /// <summary>
    /// What the sidebar lists before its search narrows it: the catalog, newest first (SessionCatalog.ScanAsync), and
    /// the open chats omp has no file for yet, ordered in by the same key. Empty sessions (omp creates one per start) are
    /// noise, except an open chat's: a new chat with only a draft in it, left for another, has to stay in reach (it would
    /// have kept its omp with no way back to it).
    /// </summary>
    private IEnumerable<SessionSummary> ListableSessions()
    {
        var open = OpenFiles();
        var listed = new HashSet<string>(_catalog.Count, SessionCatalog.PathComparer);
        foreach (var c in _catalog) listed.Add(c.Path);
        return _catalog.Concat(UnsavedChats(listed)).Where(c => !c.IsEmpty || open.Contains(c.Path));
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
            listed.Add(file);
            var title = KnownSessionTitle(s);
            yield return new SessionSummary(file, s.SessionId ?? Path.GetFileNameWithoutExtension(file), cwd,
                title ?? (Chats.Contains(cwd) ? UntitledChat : UntitledSession), o.OpenedAt,
                IsEmpty: title is null && s.History.TotalMessages == 0 && !s.Items.OfType<UserItem>().Any());
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
        item.IsCurrent && !IsUntitled(SessionTitle) && SessionTitle != item.Model.Title ? SessionTitle : null;

    private SessionGroupViewModel? _chatsGroup;

    /// <summary>Groups the listed sessions: pinned projects first (in the order they were pinned), then the chats outside
    /// any project, then the other projects by their newest message. A pinned session stays in its own group, at its top
    /// (<see cref="Sessions"/> lists pinned ones first). The groups and their rows that stay are kept (see
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
            if (!known.Remove(cwd, out var group)) group = NewGroup(cwd, project);
            group.IsCurrentProject = SessionCatalog.PathComparer.Equals(cwd, project);
            group.IsPinned = PinnedIndex(cwd) >= 0;
            group.Name = ProjectDisplayName(cwd) is { Length: > 0 } n ? n : group.FolderName;
            return group;
        }
        var groups = new List<(SessionGroupViewModel Group, List<SessionItemViewModel> Items, DateTimeOffset Newest)>();
        var chats = new List<SessionItemViewModel>();
        var archived = new List<SessionItemViewModel>();
        foreach (var item in Sessions)
        {
            var isChat = Chats.Contains(item.Cwd);
            // A removed project stays out of the list, the open chat's too (it had stayed until the app restarted)
            if (!isChat && hidden.Contains(item.Cwd)) continue;
            if (item.IsArchived)
            {
                archived.Add(item);
                continue;
            }
            if (isChat)
            {
                chats.Add(item);
                continue;
            }
            var i = groups.FindIndex(x => SessionCatalog.PathComparer.Equals(x.Group.Cwd, item.Cwd));
            if (i < 0) groups.Add((GroupFor(item.Cwd), [], item.Model.LastMessageAt));
            else if (item.Model.LastMessageAt > groups[i].Newest) groups[i] = groups[i] with { Newest = item.Model.LastMessageAt };
            groups[i < 0 ? ^1 : i].Items.Add(item);
        }
        foreach (var g in groups) Reconcile(g.Group.Items, g.Items);
        var ordered = groups.OrderByDescending(g => g.Newest).Select(g => g.Group).ToList();
        var unfiltered = SessionFilter.Trim().Length == 0 && StatusFilter == SessionStatusFilter.All;
        if (unfiltered)
        {
            // Folders added from the sidebar that have no session yet go first, as just added; pinned ones are listed
            // even without a session
            foreach (var p in added.Concat(_pinnedProjects!))
                if (!hidden.Contains(p) && !ordered.Any(g => SessionCatalog.PathComparer.Equals(g.Cwd, p)))
                {
                    var group = GroupFor(p);
                    Reconcile(group.Items, []);
                    ordered.Insert(0, group);
                }
        }
        // Chats keep their place under the top button that makes them; the group stays while one is shown, for its "+"
        _chatsGroup ??= new SessionGroupViewModel(Chats.Root, false, isChatsGroup: true) { NewSessionCommand = NewChatCommand };
        _chatsGroup.IsCurrentProject = IsProjectless;
        Reconcile(_chatsGroup.Items, chats);
        if (chats.Count > 0 || (IsProjectless && unfiltered)) ordered.Insert(0, _chatsGroup);
        // Pinned projects above everything, in the order they were pinned
        var pinnedGroups = ordered.Where(g => g.IsProject && g.IsPinned).OrderBy(g => PinnedIndex(g.Cwd)).ToList();
        ordered.RemoveAll(g => g.IsProject && g.IsPinned);
        ordered.InsertRange(0, pinnedGroups);
        if (ArchiveGroup(archived) is { } archive) ordered.Add(archive); // MainViewModel.SidebarNav.cs
        Reconcile(SessionGroups, ordered);
        OnPropertyChanged(nameof(ShowNoSessionMatch));
    }

    private SessionGroupViewModel NewGroup(string cwd, string? project) =>
        new(cwd, SessionCatalog.PathComparer.Equals(cwd, project))
        {
            NewSessionCommand = NewSessionInProjectCommand,
            RemoveCommand = RemoveProjectCommand,
            CopyPathCommand = CopyProjectPathCommand,
            TogglePinCommand = TogglePinProjectCommand,
            RevealCommand = RevealProjectCommand,
            RenameCommand = RenameProjectCommand,
        };

    // ───────────── Projects added, removed, pinned and named in the sidebar (the client's settings file) ─────────────

    private List<string>? _addedProjects;
    private HashSet<string>? _hiddenProjects;
    private List<string>? _pinnedProjects;
    private Dictionary<string, string>? _projectNames;

    private (List<string> Added, HashSet<string> Hidden) ProjectPrefs()
    {
        if (_addedProjects is null || _hiddenProjects is null || _pinnedProjects is null || _projectNames is null)
        {
            OmpRuntimeOptions? o = null;
            try { o = _settings?.Load(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Listed as if nothing was added or removed; the settings page reports the file's problem when it opens.
            }
            _addedProjects = [.. o?.SidebarProjects ?? []];
            _hiddenProjects = new HashSet<string>(o?.HiddenProjects ?? [], SessionCatalog.PathComparer);
            _pinnedProjects = [.. o?.PinnedProjects ?? []];
            _projectNames = new Dictionary<string, string>(o?.ProjectNames ?? [], SessionCatalog.PathComparer);
        }
        return (_addedProjects, _hiddenProjects);
    }

    private int PinnedIndex(string cwd)
    {
        ProjectPrefs();
        return _pinnedProjects!.FindIndex(p => SessionCatalog.PathComparer.Equals(p, cwd));
    }

    /// <summary>The name the user gave the project folder in the sidebar, if any.</summary>
    private string? ProjectDisplayName(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return null;
        ProjectPrefs();
        return _projectNames!.GetValueOrDefault(Path.TrimEndingDirectorySeparator(cwd));
    }

    private void SaveProjectPrefs()
    {
        var (added, hidden) = ProjectPrefs();
        string[]? addedOut = added.Count > 0 ? [.. added] : null;
        string[]? hiddenOut = hidden.Count > 0 ? [.. hidden.Order(StringComparer.Ordinal)] : null;
        string[]? pinnedOut = _pinnedProjects!.Count > 0 ? [.. _pinnedProjects] : null;
        Dictionary<string, string>? namesOut = _projectNames!.Count > 0 ? new(_projectNames, StringComparer.Ordinal) : null;
        Persist(o => o with { SidebarProjects = addedOut, HiddenProjects = hiddenOut, PinnedProjects = pinnedOut, ProjectNames = namesOut });
        RebuildSessionGroups();
        if (!IsProjectless) ProjectName = ProjectDisplayName(ProjectPath) ?? SessionItemViewModel.ProjectName(ProjectPath);
    }

    /// <summary>Group header: pin the project to the top of the sidebar, or unpin it.</summary>
    [RelayCommand]
    private void TogglePinProject(SessionGroupViewModel? group)
    {
        if (group is not { IsProject: true }) return;
        var at = PinnedIndex(group.Cwd);
        if (at >= 0) _pinnedProjects!.RemoveAt(at);
        else _pinnedProjects!.Add(Path.TrimEndingDirectorySeparator(group.Cwd));
        SaveProjectPrefs();
        if (at >= 0) ShowUnpinUndo(group.Name, () =>
        {
            if (PinnedIndex(group.Cwd) < 0) _pinnedProjects!.Insert(Math.Min(at, _pinnedProjects.Count), Path.TrimEndingDirectorySeparator(group.Cwd));
            SaveProjectPrefs();
            return Task.CompletedTask;
        });
    }

    /// <summary>Group header: the name the sidebar and the header show for the project (the folder is not renamed); an
    /// empty name or the folder's own goes back to the folder's name.</summary>
    [RelayCommand]
    private void RenameProject((SessionGroupViewModel Group, string Name) arg)
    {
        var (group, name) = arg;
        if (group is not { IsProject: true }) return;
        ProjectPrefs();
        var key = Path.TrimEndingDirectorySeparator(group.Cwd);
        if (name.Length == 0 || name == group.FolderName) _projectNames!.Remove(key);
        else _projectNames![key] = name;
        SaveProjectPrefs();
    }

    /// <summary>Group header: the project's folder in Finder / Explorer / the file manager.</summary>
    [RelayCommand]
    private async Task RevealProjectAsync(SessionGroupViewModel? group)
    {
        if (group is { IsProject: true }) await Files.OpenFolderInFileManagerCommand.ExecuteAsync(group.Cwd);
    }

    /// <summary>Sidebar header: pick a folder and list it as a project (its sessions, or an empty group with its
    /// "new session" button). Removing it earlier is undone.</summary>
    [RelayCommand]
    private async Task AddProjectAsync()
    {
        if (PickFolderRequested is not { } pick || await pick() is not { } picked) return;
        AddProjectFolder(picked);
    }

    /// <summary>The folder picker and a folder dropped on the sidebar share the same project-list operation.</summary>
    internal void AddProjectFolder(string picked)
    {
        if (!Directory.Exists(picked)) return;
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
    [RelayCommand(CanExecute = nameof(CanMutateSession))]
    private void RemoveProject(SessionGroupViewModel? group)
    {
        if (IsReadOnly) return;
        if (group is not { IsProject: true }) return;
        var chats = Sessions.Where(s => SessionCatalog.PathComparer.Equals(s.Cwd, group.Cwd)).ToList();
        if (chats.Count == 0)
        {
            HideProject(group);
            return;
        }
        var card = new SessionCardViewModel("delete", "IconFolder", Loc.F("Remove “{0}”?", group.Name),
            (chats.Count == 1 ? Loc.T("It has 1 chat.") : Loc.F("It has {0} chats.", chats.Count))
            + " " + Loc.T("Remove it from the sidebar and keep the chats on disk, or delete the chats too (their files and artifacts; this cannot be undone). The folder itself is never deleted."))
        {
            Detail = group.Cwd,
            State = SessionCardState.Ask,
        };
        card.Primary = new SessionCardAction(Loc.T("Remove from sidebar"), new RelayCommand(() =>
        {
            CloseSessionCard();
            HideProject(group);
        }));
        card.Tertiary = new SessionCardAction(chats.Count == 1 ? Loc.T("Delete the chat too") : Loc.F("Delete {0} chats too", chats.Count),
            new AsyncRelayCommand(() => DeleteProjectChatsAsync(card, group, chats)));
        card.Secondary = new SessionCardAction(Loc.T("Cancel"), new RelayCommand(CloseSessionCard));
        ShowCard(card);
    }

    /// <summary>Deletes a project's chats (each open one closed first; the one on screen gets a new session), then hides it.</summary>
    private async Task DeleteProjectChatsAsync(SessionCardViewModel card, SessionGroupViewModel group, List<SessionItemViewModel> chats)
    {
        if (IsReadOnly) return;
        card.State = SessionCardState.Running;
        card.Message = Loc.T("Deleting…");
        card.Primary = card.Secondary = card.Tertiary = null;
        var failed = new List<string>();
        foreach (var chat in chats)
        {
            var open = FindOpen(chat.Model.Path);
            var draftId = open?.DraftId;
            var composer = open == _open ? CaptureDraft(_open) : null;
            if (open is not null)
            {
                if (open == _open) await NewChatAsync(); // off the chat on screen first (to a chat: the project goes), so it can be closed
                if (FindOpen(chat.Model.Path) is { } still) await CloseSessionAsync(still);
            }
            try
            {
                if (FindOpen(chat.Model.Path) is not null) throw new IOException(Loc.T("The session could not be closed safely."));
                await _host.DeleteSessionAsync(chat.Model.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or RpcConnectionClosedException or TimeoutException)
            {
                failed.Add($"{chat.Title}: {e.Message}");
                continue;
            }
            RemoveDeletedDrafts(chat.Model.Path, chat.Model.Cwd, draftId, closedComposer: composer);
        }
        RequestCatalogRefresh();
        HideProject(group, announce: false);
        if (failed.Count > 0)
            card.Finish(false, Loc.T("Some chats were not deleted"), string.Join("\n", failed), secondary: new SessionCardAction(Loc.T("Dismiss"), new RelayCommand(CloseSessionCard)));
        else ShowBrief("IconTrash", Loc.F("“{0}” removed", group.Name), chats.Count == 1 ? Loc.T("1 chat deleted") : Loc.F("{0} chats deleted", chats.Count));
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
        var card = new SessionCardViewModel("brief", "IconFolder", Loc.F("“{0}” removed from the sidebar", group.Name),
            Loc.T("Nothing was deleted: add the folder again to list its sessions."));
        card.Primary = new SessionCardAction(Loc.T("Undo"), new RelayCommand(() =>
        {
            var (a, h) = ProjectPrefs();
            h.Remove(group.Cwd);
            if (wasAdded >= 0 && !a.Contains(group.Cwd, SessionCatalog.PathComparer)) a.Insert(Math.Min(wasAdded, a.Count), group.Cwd);
            SaveProjectPrefs();
            CloseSessionCard();
        }));
        ShowTimedCard(card, UndoCardDuration);
    }

    [RelayCommand]
    private Task CopyProjectPath(SessionGroupViewModel? group) =>
        group is { IsProject: true } ? CopyTextAsync(group.Cwd, Loc.T("Project path copied")) : Task.CompletedTask;

    // ───────────── Row menu: rename, pin, copy path; cycling with Ctrl+Tab ─────────────

    /// <summary>Rename from the sidebar: omp renames the open session only, so another one is opened first; the
    /// header's name field then takes the new name.</summary>
    [RelayCommand(CanExecute = nameof(CanMutateSession))]
    private async Task RenameSessionItemAsync(SessionItemViewModel? item)
    {
        if (IsReadOnly) return;
        if (item is null) return;
        if (!item.IsCurrent)
        {
            await OpenSessionCommand.ExecuteAsync(item);
            if (_open.Last?.SessionFile != item.Model.Path) return;
        }
        if (StartRenameCommand.CanExecute(null)) StartRenameCommand.Execute(null);
        else ShowBrief("IconPencil", Loc.T("Not now"), Loc.T("Rename the session once omp has finished the current run."));
    }

    /// <summary>Pin or unpin in omp's own pin list, so omp's session picker agrees.</summary>
    private int _pinsVersion;

    [RelayCommand(CanExecute = nameof(CanMutateSession))]
    private async Task TogglePinItemAsync(SessionItemViewModel? item)
    {
        if (item is null) return;
        var pin = !item.IsPinned;
        if (await SetSessionPinAsync(item.Model, pin) && !pin)
            ShowUnpinUndo(item.Title, async () => { await SetSessionPinAsync(item.Model, true); });
    }

    private async Task<bool> SetSessionPinAsync(SessionSummary model, bool pin)
    {
        if (IsReadOnly || SessionPins.PathFor(model.Path) is not { } file) return false;
        if (FindOpen(model.Path, readOnly: true) is not null && FindOpen(model.Path, readOnly: false) is null) return false;
        try
        {
            var pins = await Task.Run(() => SessionPins.Set(file, model.Id, pin));
            Interlocked.Increment(ref _pinsVersion);
            _pinnedIds = pins;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowBrief("IconPin", pin ? Loc.T("Not pinned") : Loc.T("Not unpinned"), e.Message);
            return false;
        }
        if (model.Path == _open.Last?.SessionFile) IsSessionPinned = pin;
        ApplySessionFilter();
        RequestCatalogRefresh(); // a scan under way read the pins before this change: one more scan reads them again
        return true;
    }

    private void ShowUnpinUndo(string title, Func<Task> restore)
    {
        var card = new SessionCardViewModel("brief", "IconPinOff", Loc.T("Unpinned"), title);
        card.Primary = new SessionCardAction(Loc.T("Undo"), new AsyncRelayCommand(async () =>
        {
            CloseSessionCard();
            await restore();
        }));
        ShowTimedCard(card, UndoCardDuration);
    }

    [RelayCommand]
    private Task CopySessionPath(SessionItemViewModel? item) => item is null ? Task.CompletedTask
        : CopyTextAsync(item.Model.Path, Loc.T("Session file path copied"), Loc.T("omp keeps the whole conversation in this file (JSON lines)."));

    /// <summary>Sidebar row: the session's id (its file's header id: what omp --resume and /pin take).</summary>
    [RelayCommand]
    private Task CopySessionItemId(SessionItemViewModel? item) => item is { Model.Id: { Length: > 0 } id }
        ? CopyTextAsync(id, Loc.T("Session ID copied"), Loc.T("omp --resume takes it (a unique start of it is enough).")) : Task.CompletedTask;

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: the next or previous session in the sidebar's order (wrapping).</summary>
    internal bool CycleSession(int direction)
    {
        var order = ListedRows(); // MainViewModel.SidebarNav.cs: a folded archive is skipped
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
    [RelayCommand(CanExecute = nameof(CanMutateSession))]
    private void DeleteSessionItem(SessionItemViewModel? item)
    {
        if (IsReadOnly) return;
        if (item is null) return;
        if (item.Model.Path == ShownFile)
        {
            if (DeleteSessionCommand.CanExecute(null)) DeleteSessionCommand.Execute(null);
            else ShowBrief("IconTrash", Loc.T("Not now"), Loc.T("Stop the current run first; then the open session can be deleted."));
            return;
        }
        var working = FindOpen(item.Model.Path) is { } open && !SessionHost.IsIdle(open.Controller.Snapshot());
        var card = new SessionCardViewModel("delete", "IconTrash", Loc.F("Delete “{0}”?", item.Title),
            working ? Loc.T("omp is working in it: deleting stops its run, then the conversation's file and its artifacts are deleted. This cannot be undone.")
                : Loc.T("The conversation's file and its artifacts are deleted. This cannot be undone."))
        {
            Detail = item.Model.Path,
            State = SessionCardState.Ask,
        };
        card.Primary = new SessionCardAction(working ? Loc.T("Stop and delete") : Loc.T("Delete session"), new AsyncRelayCommand(async () =>
        {
            card.State = SessionCardState.Running;
            card.Message = Loc.T("Deleting…");
            card.Primary = null;
            card.Secondary = null;
            var draftId = FindOpen(item.Model.Path)?.DraftId;
            if (FindOpen(item.Model.Path) is { } still)
            {
                if (still == _open)
                {
                    // Opened meanwhile: the shown chat is deleted from its own menu, where omp starts a new session after it
                    card.Finish(false, Loc.T("Not deleted"), Loc.T("The session is on screen now: use Delete session in its menu (the title)."),
                        secondary: new SessionCardAction(Loc.T("Dismiss"), new RelayCommand(CloseSessionCard)));
                    return;
                }
                await CloseSessionAsync(still); // MainViewModel.OpenSessions.cs
            }
            try
            {
                if (FindOpen(item.Model.Path) is not null) throw new IOException(Loc.T("The session could not be closed safely."));
                await _host.DeleteSessionAsync(item.Model.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or RpcConnectionClosedException or TimeoutException)
            {
                card.Finish(false, Loc.T("Not deleted"), e.Message, secondary: new SessionCardAction(Loc.T("Dismiss"), new RelayCommand(CloseSessionCard)));
                return;
            }
            RemoveDeletedDrafts(item.Model.Path, item.Model.Cwd, draftId);
            RequestCatalogRefresh();
            ShowBrief("IconTrash", Loc.T("Session deleted"), item.Title);
        }));
        card.Secondary = new SessionCardAction(Loc.T("Cancel"), new RelayCommand(CloseSessionCard));
        ShowCard(card);
    }
}
