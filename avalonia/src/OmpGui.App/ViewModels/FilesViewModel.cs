using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Controls;
using OmpGui.App.Services;

namespace OmpGui.App.ViewModels;

/// <summary>Which files the Files pane lists: the whole tree, what git says changed, or what omp changed in this session.</summary>
public enum FilesMode { All, Changed, Session }

/// <summary>
/// The Files pane (Claude Code's Files view) and the header's project menu: the project folder as a tree read one
/// folder at a time, a name search over the whole project, git's marks, the files omp changed in this session, and a
/// viewer for the open file. Everything that touches the disk or runs git happens off the UI thread; while the pane is
/// shown, the open folders are watched (debounced) and every finished tool refreshes it.
/// </summary>
public sealed partial class FilesViewModel : ObservableObject
{
    public const int MaxResults = 200;
    /// <summary>Folders watched at most (one watcher each, only while the pane is shown).</summary>
    private const int MaxWatchers = 32;
    private static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(300);
    /// <summary>While omp works it writes into the project all the time: the pane catches up at most once a second.</summary>
    private static readonly TimeSpan RunningRefreshDelay = TimeSpan.FromSeconds(1);
    /// <summary>The pause after a key before searching (the "Go to file" box and the message box's "@" menu).</summary>
    internal static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>Tools whose row names the file they change (with an edit's diff, any tool that has one counts too).</summary>
    internal static readonly IReadOnlySet<string> FileChangingTools = new HashSet<string>(StringComparer.Ordinal)
        { "edit", "write", "create", "write_file", "ast_edit", "notebook_edit", "apply_patch" };

    private FileNodeViewModel? _rootNode;
    private GitSnapshot? _git;
    private List<string>? _index;
    private bool _indexTruncated;
    private string? _loadedRoot;
    private Task _rootLoad = Task.CompletedTask;
    private readonly HashSet<string> _sessionChanged = new(ProjectFiles.PathComparer);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(ProjectFiles.PathComparer);
    private DispatcherTimer? _refreshTimer;
    private CancellationTokenSource? _loadCts, _searchCts, _viewerCts, _refreshCts;
    private int _toolsSeenDone;
    private int _rowsSeen = -1;
    private int _diskChangePosted;
    private int _editorScan;

    public FilesViewModel(MainViewModel owner)
    {
        Owner = owner;
        owner.PropertyChanged += OnOwnerChanged;
        owner.TranscriptChanged += OnTranscriptChanged;
        owner.Rows.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Reset) return;
            _rowsSeen = -1; // another conversation: its rows say which files it changed, whatever their count
            OnTranscriptChanged();
        };
        owner.SessionGroups.CollectionChanged += (_, _) => RebuildRecentProjects();
        RebuildRecentProjects();
        _ = DetectEditorsAsync();
        if (IsActive) Activate(); // shown before anything asked for this
    }

    public MainViewModel Owner { get; }

    /// <summary>The project folder shown (null: no project).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject), nameof(ShowTree), nameof(ShowResults), nameof(ShowNoProject), nameof(RootName))]
    private string? _root;

    public bool HasProject => Root is not null;
    public bool ShowNoProject => Root is null;
    public string RootName => Root is { } r ? Path.GetFileName(Path.TrimEndingDirectorySeparator(r)) : "";

    /// <summary>The tree's rows: every entry of the open folders, in order.</summary>
    public ObservableCollection<FileNodeViewModel> Nodes { get; } = [];

    /// <summary>A flat list: search results, changed files or this session's files.</summary>
    public ObservableCollection<FileNodeViewModel> Results { get; } = [];

    [ObservableProperty] private FileNodeViewModel? _selectedNode;
    [ObservableProperty] private FileNodeViewModel? _selectedResult;

    /// <summary>Find a file by name across the project.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTree), nameof(ShowResults), nameof(HasFilter))]
    private string _filter = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTree), nameof(ShowResults), nameof(IsAllMode), nameof(IsChangedMode), nameof(IsSessionMode))]
    private FilesMode _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTreeMessage))]
    private bool _isLoading;

    [ObservableProperty] private bool _isSearching;

    /// <summary>Why the tree is empty ("This folder is empty.", or why the folder cannot be read).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTreeMessage))]
    private string _treeMessage = "";

    /// <summary>Why a flat list is empty ("No files match…", "No uncommitted changes.").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultsMessage))]
    private string _resultsMessage = "";

    /// <summary>What a result list leaves out ("Showing the first 200 matches").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultsNote))]
    private string _resultsNote = "";

    [ObservableProperty] private bool _isGitRepo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangedLabel))]
    private int _changedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionLabel))]
    private int _sessionCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViewer))]
    private FileViewerViewModel? _viewer;

    /// <summary>The open file takes the whole pane (the list hides), for reading in a narrow pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandTip))]
    private bool _isViewerExpanded;

    public string ExpandTip => IsViewerExpanded ? "Show the file list again" : "Use the whole pane for the file";

    [RelayCommand]
    private void ToggleViewerExpanded() => IsViewerExpanded = !IsViewerExpanded;

    partial void OnViewerChanged(FileViewerViewModel? value)
    {
        if (value is null) IsViewerExpanded = false;
        else SyncViewerGit(value);
    }

    /// <summary>An action that failed ("Could not start xdg-open"), until dismissed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    public bool HasFilter => Filter.Length > 0;
    public bool ShowTree => HasProject && Mode == FilesMode.All && Filter.Trim().Length == 0;
    public bool ShowResults => HasProject && !ShowTree;
    public bool HasTreeMessage => TreeMessage.Length > 0 && !IsLoading;
    public bool HasResultsMessage => ResultsMessage.Length > 0;
    public bool HasResultsNote => ResultsNote.Length > 0;
    public bool HasViewer => Viewer is not null;
    public bool HasNotice => Notice.Length > 0;
    public bool IsAllMode => Mode == FilesMode.All;
    public bool IsChangedMode => Mode == FilesMode.Changed;
    public bool IsSessionMode => Mode == FilesMode.Session;
    public string ChangedLabel => ChangedCount > 0 ? $"Changed {ChangedCount}" : "Changed";
    public string SessionLabel => SessionCount > 0 ? $"This session {SessionCount}" : "This session";

    /// <summary>Editors found on PATH (VS Code, Cursor…): offered only when present.</summary>
    public ObservableCollection<EditorApp> Editors { get; } = [];
    [ObservableProperty] private bool _hasEditors;

    /// <summary>What the project menu calls the file manager ("Show in Finder").</summary>
    public string FileManagerLabel => OperatingSystem.IsMacOS() ? "Show in Finder" : OperatingSystem.IsWindows() ? "Show in Explorer" : "Open in file manager";
    public string RevealLabel => OperatingSystem.IsMacOS() ? "Reveal in Finder" : OperatingSystem.IsWindows() ? "Reveal in Explorer" : "Reveal in file manager";

    /// <summary>The projects of the sidebar, newest first (at most 6), to start a new session in.</summary>
    public ObservableCollection<SessionGroupViewModel> RecentProjects { get; } = [];
    [ObservableProperty] private bool _hasRecentProjects;

    /// <summary>Starts a program (an editor, the file manager); tests replace it.</summary>
    public Func<ProcessStartInfo, bool, Task<bool>> Launch { get; set; } = FileOpeners.StartAsync;

    /// <summary>Raised when a node should be scrolled into view in the tree (after "open" from the conversation).</summary>
    public event Action<FileNodeViewModel>? RevealRequested;

    private bool IsActive => Owner.ActivePane == SidePane.Files;

    // ───────────────────────── Showing, hiding, the project ─────────────────────────

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ActivePane):
                if (IsActive) Activate();
                else Deactivate();
                break;
            case nameof(MainViewModel.ProjectPath):
                if (IsActive && !SamePath(Owner.ProjectFolder, _loadedRoot)) _rootLoad = LoadRootAsync(Owner.ProjectFolder);
                break;
        }
    }

    private void Activate()
    {
        var root = Owner.ProjectFolder;
        if (!SamePath(root, _loadedRoot) || _rootNode is null && root is not null) _rootLoad = LoadRootAsync(root);
        else
        {
            UpdateSessionChanges();
            _toolsSeenDone = DoneTools();
            ScheduleRefresh();
        }
    }

    private void Deactivate()
    {
        _refreshTimer?.Stop();
        SyncWatchers();
    }

    private static bool SamePath(string? a, string? b) =>
        a is null ? b is null : b is not null && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), ProjectFiles.PathComparison);

    /// <summary>Reads the project folder's top level and starts git's status and the watchers.</summary>
    internal async Task LoadRootAsync(string? root)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        _loadedRoot = root;
        Root = root;
        _rootNode = null;
        _git = null;
        _index = null;
        IsGitRepo = false;
        ChangedCount = 0;
        Nodes.Clear();
        Results.Clear();
        TreeMessage = "";
        ResultsMessage = "";
        if (Viewer is { } v && (root is null || ProjectFiles.Relative(root, v.FullPath) is null)) CloseViewer();
        if (root is null)
        {
            SyncWatchers();
            return;
        }
        IsLoading = true;
        try
        {
            var entries = await Task.Run(() => ProjectFiles.List(root), cts.Token);
            if (cts.IsCancellationRequested) return;
            var node = FileNodeViewModel.Root(root);
            node.Children = [.. entries.Select(e => FileNodeViewModel.Child(node, e))];
            _rootNode = node;
            SyncFlat();
            TreeMessage = entries.Count == 0 ? "This folder is empty." : "";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            TreeMessage = "Can't read this folder: " + e.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsLoading = false;
        }
        UpdateSessionChanges();
        _toolsSeenDone = DoneTools();
        SyncWatchers();
        // git's marks and deleted files follow; the tree is usable meanwhile
        _ = RefreshAsync();
    }

    // ───────────────────────── The tree ─────────────────────────

    /// <summary>A click on a row: a folder opens or closes, a file opens in the viewer.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ActivateAsync(FileNodeViewModel? node)
    {
        if (node is null) return;
        if (node.IsDirectory)
        {
            if (Results.Contains(node))
            {
                // A folder in a flat list (an untracked folder): shown open in the tree
                Filter = "";
                Mode = FilesMode.All;
                await ShowInTreeAsync(node.FullPath, expand: true);
                return;
            }
            await ToggleAsync(node);
            return;
        }
        await OpenFileAsync(node.FullPath);
    }

    /// <summary>Opens or closes a folder (its entries are read the first time).</summary>
    public async Task ToggleAsync(FileNodeViewModel node)
    {
        if (!node.IsDirectory) return;
        if (node.IsExpanded)
        {
            node.IsExpanded = false;
            SyncFlat();
            SyncWatchers();
            return;
        }
        node.IsExpanded = true;
        if (node.Children is null) await LoadChildrenAsync(node);
        SyncFlat();
        SyncWatchers();
    }

    private async Task LoadChildrenAsync(FileNodeViewModel node)
    {
        node.IsLoading = true;
        try
        {
            var git = _git;
            var entries = await Task.Run(() => ListWithDeleted(node.FullPath, git));
            node.Children = [.. entries.Select(e => FileNodeViewModel.Child(node, e))];
            ApplyMarks(node.Children);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            node.Children = [];
            Notice = $"Can't read {node.Name}: {e.Message}";
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    /// <summary>A folder's entries plus the files git knows were deleted from it (shown struck through).</summary>
    private static List<FileEntry> ListWithDeleted(string dir, GitSnapshot? git)
    {
        var entries = ProjectFiles.List(dir);
        if (git?.DeletedIn(dir) is { Count: > 0 } deleted)
        {
            var names = entries.Select(e => e.Name).ToHashSet(ProjectFiles.PathComparer);
            foreach (var d in deleted)
                if (names.Add(Path.GetFileName(d)) && !File.Exists(d) && !Directory.Exists(d))
                    entries.Add(new FileEntry(Path.GetFileName(d), d, false, 0, IsDeleted: true));
            entries.Sort(ProjectFiles.Compare);
        }
        return entries;
    }

    /// <summary>The rows the tree shows now: a walk of the open folders.</summary>
    private List<FileNodeViewModel> Flatten()
    {
        var list = new List<FileNodeViewModel>();
        void Walk(FileNodeViewModel n)
        {
            if (n.Children is null) return;
            foreach (var c in n.Children)
            {
                list.Add(c);
                if (c.IsExpanded) Walk(c);
            }
        }
        if (_rootNode is not null) Walk(_rootNode);
        return list;
    }

    /// <summary>Brings <see cref="Nodes"/> to the walk in place (inserts and removes only), so the list keeps its scroll.</summary>
    private void SyncFlat()
    {
        var target = Flatten();
        var wanted = new HashSet<FileNodeViewModel>(target);
        var present = new HashSet<FileNodeViewModel>(Nodes);
        var i = 0;
        foreach (var node in target)
        {
            while (i < Nodes.Count && !wanted.Contains(Nodes[i])) { present.Remove(Nodes[i]); Nodes.RemoveAt(i); }
            if (i < Nodes.Count && ReferenceEquals(Nodes[i], node)) { i++; continue; }
            if (present.Contains(node))
            {
                // Order changed (not expected: listings are sorted): start over
                Nodes.Clear();
                foreach (var n in target) Nodes.Add(n);
                return;
            }
            Nodes.Insert(i++, node);
            present.Add(node);
        }
        while (Nodes.Count > i) Nodes.RemoveAt(Nodes.Count - 1);
    }

    /// <summary>
    /// Shows <paramref name="fullPath"/> in the tree: opens the folders above it (reading them as needed), selects it
    /// and asks the view to scroll to it. With <paramref name="expand"/> a folder opens too.
    /// </summary>
    public async Task<FileNodeViewModel?> ShowInTreeAsync(string fullPath, bool expand = false)
    {
        await _rootLoad;
        if (_rootNode is not { } node || Root is null || ProjectFiles.Relative(Root, fullPath) is not { Length: > 0 } rel) return null;
        var parts = rel.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            if (node.Children is null) await LoadChildrenAsync(node);
            var child = node.Children!.FirstOrDefault(c => string.Equals(c.Name, parts[i], ProjectFiles.PathComparison));
            if (child is null) break;
            if (child.IsDirectory && (i < parts.Length - 1 || expand) && !child.IsExpanded)
            {
                child.IsExpanded = true;
                if (child.Children is null) await LoadChildrenAsync(child);
            }
            node = child;
        }
        SyncFlat();
        SyncWatchers();
        if (ReferenceEquals(node, _rootNode)) return null;
        SelectedNode = node;
        RevealRequested?.Invoke(node);
        return node;
    }

    // ───────────────────────── Refresh: disk, git, the conversation ─────────────────────────

    /// <summary>The Refresh button: reads everything again (the whole project when it could not be read before).</summary>
    [RelayCommand]
    private Task RefreshNowAsync() =>
        _rootNode is null || !SamePath(Owner.ProjectFolder, _loadedRoot) ? _rootLoad = LoadRootAsync(Owner.ProjectFolder) : RefreshAsync();

    /// <summary>The window is closing: no watcher or timer may reach a window that is gone.</summary>
    internal void Shutdown()
    {
        _refreshTimer?.Stop();
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
    }

    /// <summary>
    /// A refresh soon. Events come in bursts: the first one starts the wait and the rest of the burst joins that
    /// refresh, so a build writing files for minutes still shows its progress, at most every 300 ms (every second
    /// while omp works).
    /// </summary>
    private void ScheduleRefresh()
    {
        if (Owner.IsClosing) return;
        if (_refreshTimer is null)
        {
            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer!.Stop();
                _ = RefreshAsync();
            };
        }
        if (_refreshTimer.IsEnabled) return;
        _refreshTimer.Interval = Owner.IsRunning ? RunningRefreshDelay : RefreshDelay;
        _refreshTimer.Start();
    }

    /// <summary>
    /// Reads git's status and every open folder again (off the UI thread), merges what changed into the tree, updates
    /// the marks and lists, and reloads the open file when it changed on disk.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (Root is not { } root || _rootNode is null) return;
        PerfLog.Count("files_refresh");
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource();
        var ct = cts.Token;
        var open = new List<FileNodeViewModel> { _rootNode };
        open.AddRange(Flatten().Where(n => n.IsDirectory && n.IsExpanded && n.Children is not null));
        try
        {
            var (git, listings) = await Task.Run(async () =>
            {
                var g = await Git.StatusAsync(root, ct);
                var l = new Dictionary<FileNodeViewModel, List<FileEntry>?>();
                foreach (var n in open)
                {
                    ct.ThrowIfCancellationRequested();
                    try { l[n] = ListWithDeleted(n.FullPath, g); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { l[n] = null; }
                }
                return (g, l);
            }, ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_rootNode, open[0])) return;
            _git = git;
            if (Viewer is { } shown) SyncViewerGit(shown);
            IsGitRepo = git is not null;
            if (!IsGitRepo && Mode == FilesMode.Changed) Mode = FilesMode.All;
            foreach (var (node, entries) in listings)
                if (entries is not null) Merge(node, entries);
            if (listings[_rootNode] is { } top) TreeMessage = top.Count == 0 ? "This folder is empty." : "";
            _index = null; // the search reads the project again next time
            SyncFlat();
            UpdateSessionChanges();
            ApplyMarks(Flatten());
            ChangedCount = git is null ? 0 : git.Changes.Count(c => ProjectFiles.Relative(root, c.Path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 });
            if (!ShowTree) ScheduleSearch(immediately: true);
            SyncWatchers();
            if (Viewer is { State: not ViewerState.Loading } v) await ReloadIfChangedAsync(v);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>New entries in, gone ones out; the others stay (with their open state and children).</summary>
    private static void Merge(FileNodeViewModel dir, List<FileEntry> entries)
    {
        var old = (dir.Children ?? []).ToDictionary(c => (c.Name, c.IsDirectory, c.IsDeleted));
        dir.Children = [.. entries.Select(e => old.TryGetValue((e.Name, e.IsDirectory, e.IsDeleted), out var keep) ? keep : FileNodeViewModel.Child(dir, e))];
    }

    /// <summary>git's marks, ignored state and this session's changes on <paramref name="nodes"/>.</summary>
    private void ApplyMarks(IEnumerable<FileNodeViewModel> nodes)
    {
        foreach (var n in nodes)
        {
            var mark = _git?.MarkOf(n.FullPath) ?? GitMark.None;
            n.IsIgnored = mark == GitMark.Ignored;
            n.InsideDimmed = n.Parent is { IsDimmed: true };
            n.Git = mark == GitMark.Ignored ? GitMark.None : n.IsDirectory && mark != GitMark.Untracked ? GitMark.None : mark;
            n.HasChangesInside = n.IsDirectory && _git?.HasChangesInside(n.FullPath) == true;
            n.IsSessionChanged = !n.IsDirectory && _sessionChanged.Contains(n.FullPath);
        }
    }

    internal void OnTranscriptChanged()
    {
        if (!IsActive) return;
        // A streamed token changes nothing here: only a tool row that came or ended changes which files the
        // session touched and what may have changed on disk
        var rows = Owner.Rows.Count;
        var done = DoneTools();
        if (rows == _rowsSeen && done == _toolsSeenDone) return;
        _rowsSeen = rows;
        UpdateSessionChanges();
        // Any tool that ended may have changed files (a write, a command): refresh once they settle
        if (done != _toolsSeenDone)
        {
            _toolsSeenDone = done;
            ScheduleRefresh();
        }
    }

    private int DoneTools() => Owner.Rows.Count(r => r is ToolRowViewModel { IsRunning: false });

    /// <summary>A tool row that changed a file (edit, write…) in this conversation.</summary>
    internal static bool IsFileChange(ToolRowViewModel t) =>
        !t.IsFailed && (t.HasDiff || FileChangingTools.Contains(t.Name)) && t.HasFile;

    /// <summary>The files omp changed in this conversation (from its edit and write tool rows).</summary>
    private void UpdateSessionChanges()
    {
        var root = Root ?? Owner.ProjectFolder;
        var now = new HashSet<string>(ProjectFiles.PathComparer);
        foreach (var row in Owner.Rows)
            if (row is ToolRowViewModel t && IsFileChange(t) && ProjectFiles.Resolve(root, t.FilePath) is { } full)
                now.Add(full);
        if (now.SetEquals(_sessionChanged)) return;
        _sessionChanged.Clear();
        _sessionChanged.UnionWith(now);
        SessionCount = now.Count;
        ApplyMarks(Flatten());
        ApplyMarks(Results);
        if (Mode == FilesMode.Session) ScheduleSearch(immediately: true);
    }

    /// <summary>The files omp changed in this session (full paths), for tests and the list.</summary>
    public IReadOnlyCollection<string> SessionChangedFiles => _sessionChanged;

    // ───────────────────────── Watching ─────────────────────────

    /// <summary>
    /// One non-recursive watcher per open folder (the project, the folders opened in the tree, the open file's folder),
    /// only while the pane is shown: changes elsewhere cannot show anyway, and a recursive watcher would walk
    /// node_modules. At most <see cref="MaxWatchers"/>.
    /// </summary>
    private void SyncWatchers()
    {
        var want = new HashSet<string>(ProjectFiles.PathComparer);
        if (IsActive && _rootNode is not null)
        {
            want.Add(_rootNode.FullPath);
            if (Viewer is { } v && Path.GetDirectoryName(v.FullPath) is { } vd) want.Add(vd);
            foreach (var n in Flatten())
                if (n.IsDirectory && n.IsExpanded && want.Count < MaxWatchers) want.Add(n.FullPath);
        }
        foreach (var gone in _watchers.Keys.Where(k => !want.Contains(k)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }
        foreach (var dir in want.Where(d => !_watchers.ContainsKey(d)))
        {
            try
            {
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                w.Created += OnDiskChange;
                w.Deleted += OnDiskChange;
                w.Renamed += OnDiskChange;
                w.Changed += OnDiskChange;
                w.Error += (_, _) => Dispatcher.UIThread.Post(ScheduleRefresh);
                w.EnableRaisingEvents = true;
                _watchers[dir] = w;
            }
            catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                // No more watchers here (the system's limit): finished tools and Refresh still update the pane
            }
        }
    }

    // A build writes hundreds of files a second: one job on the UI thread per burst, not one per event
    private void OnDiskChange(object sender, FileSystemEventArgs e)
    {
        if (Interlocked.Exchange(ref _diskChangePosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _diskChangePosted, 0);
            if (IsActive) ScheduleRefresh();
        });
    }

    /// <summary>Number of folders watched now (tests).</summary>
    internal int WatcherCount => _watchers.Count;

    // ───────────────────────── Search and lists ─────────────────────────

    partial void OnFilterChanged(string value) => ScheduleSearch();

    partial void OnModeChanged(FilesMode value) => ScheduleSearch(immediately: true);

    [RelayCommand]
    private void SetMode(FilesMode mode) => Mode = mode;

    [RelayCommand]
    private void ClearFilter() => Filter = "";

    private void ScheduleSearch(bool immediately = false)
    {
        _searchCts?.Cancel();
        if (ShowTree || Root is null)
        {
            Results.Clear();
            ResultsNote = "";
            ResultsMessage = "";
            IsSearching = false;
            return;
        }
        var cts = _searchCts = new CancellationTokenSource();
        _ = UpdateResultsAsync(immediately ? TimeSpan.Zero : SearchDelay, cts.Token);
    }

    /// <summary>The flat list for the current mode and filter; the search reads the project's file list once.</summary>
    private async Task UpdateResultsAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            if (Root is not { } root) return;
            var query = Filter.Trim();
            IsSearching = true;
            List<FileNodeViewModel> list;
            var note = "";
            string empty;
            switch (Mode)
            {
                case FilesMode.All:
                    if (_index is null)
                    {
                        var (paths, truncated) = await ProjectFiles.IndexAsync(root, useGit: true, ct);
                        if (ct.IsCancellationRequested) return;
                        _index = paths;
                        _indexTruncated = truncated;
                    }
                    var index = _index;
                    list = await Task.Run(() => ProjectFiles.Search(index, query, MaxResults, ct).Select(h => FileNodeViewModel.Result(root, h.RelativePath)).ToList(), ct);
                    note = list.Count >= MaxResults ? $"Showing the first {MaxResults} matches" : _indexTruncated ? $"Searched the first {ProjectFiles.MaxIndexedFiles:N0} files" : "";
                    empty = $"No files match “{query}”.";
                    break;
                case FilesMode.Changed:
                    var changes = _git?.Changes.ToList() ?? [];
                    list = await Task.Run(() => changes
                        .Select(c => (c, rel: ProjectFiles.Relative(root, c.Path.TrimEnd(Path.DirectorySeparatorChar))))
                        .Where(x => x.rel is { Length: > 0 } && Matches(x.rel, query))
                        .OrderBy(x => x.rel, Comparer<string?>.Create((a, b) => ProjectFiles.NaturalCompare(a!, b!)))
                        .Select(x => FileNodeViewModel.Result(root, x.rel!, isDirectory: x.c.Path.EndsWith(Path.DirectorySeparatorChar)))
                        .ToList(), ct);
                    empty = query.Length > 0 ? $"No changed files match “{query}”." : "No uncommitted changes.";
                    break;
                default:
                    var files = _sessionChanged.ToList();
                    list = await Task.Run(() => files
                        .Select(f => (f, rel: ProjectFiles.Relative(root, f)))
                        .Where(x => Matches(x.rel ?? x.f, query))
                        .OrderBy(x => x.rel ?? x.f, Comparer<string>.Create(ProjectFiles.NaturalCompare))
                        .Select(x => x.rel is { Length: > 0 } rel ? FileNodeViewModel.Result(root, rel) : FileNodeViewModel.Outside(x.f))
                        .ToList(), ct);
                    empty = query.Length > 0 ? $"No files omp changed match “{query}”." : "omp has not changed any files in this session yet.";
                    break;
            }
            if (ct.IsCancellationRequested) return;
            ApplyMarks(list);
            Results.Clear();
            foreach (var n in list) Results.Add(n);
            ResultsNote = note;
            ResultsMessage = list.Count == 0 ? empty : "";
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!ct.IsCancellationRequested) IsSearching = false;
        }
    }

    private static bool Matches(string path, string query) =>
        query.Length == 0 || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => path.Contains(w, StringComparison.OrdinalIgnoreCase));

    // ───────────────────────── The viewer ─────────────────────────

    /// <summary>
    /// Opens <paramref name="fullPath"/> in the viewer (at <paramref name="line"/>); a folder is shown in the tree
    /// instead. With <paramref name="reveal"/> the file is also selected in the tree.
    /// </summary>
    public async Task OpenFileAsync(string fullPath, int? line = null, bool reveal = false)
    {
        if (Directory.Exists(fullPath))
        {
            Filter = "";
            Mode = FilesMode.All;
            await ShowInTreeAsync(fullPath, expand: true);
            return;
        }
        var root = Root ?? Owner.ProjectFolder;
        if (Viewer is { } open && SamePath(open.FullPath, fullPath))
        {
            open.JumpTo(line);
            if (open.State != ViewerState.Loading) await ReloadIfChangedAsync(open);
        }
        else
        {
            var v = new FileViewerViewModel(this, fullPath, root is null ? null : ProjectFiles.Relative(root, fullPath)) { TargetLine = line };
            var old = Viewer;
            Viewer = v;
            old?.Release();
            SyncWatchers();
            await ReloadViewerAsync(v, head: false);
        }
        if (reveal) await ShowInTreeAsync(fullPath);
        else FollowInTree(fullPath);
    }

    /// <summary>
    /// The tree marks the open file when it lists it, and no other file: the highlight left on the last file clicked had
    /// said that file was open while another one was.
    /// </summary>
    private void FollowInTree(string fullPath)
    {
        if (Nodes.FirstOrDefault(n => !n.IsDirectory && SamePath(n.FullPath, fullPath)) is { } listed) SelectedNode = listed;
        else if (SelectedNode is { IsDirectory: false } other && !SamePath(other.FullPath, fullPath)) SelectedNode = null;
    }

    /// <summary>Reads the viewer's file again (off the UI thread); <paramref name="head"/>: the start of a large file.</summary>
    internal async Task ReloadViewerAsync(FileViewerViewModel v, bool head)
    {
        _viewerCts?.Cancel();
        var cts = _viewerCts = new CancellationTokenSource();
        try
        {
            var loaded = await Task.Run(() => FileViewerViewModel.Load(v.FullPath, head, cts.Token), cts.Token);
            if (ReferenceEquals(Viewer, v) && !cts.IsCancellationRequested) v.Apply(loaded);
            else loaded.Image?.Dispose(); // never shown
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReloadIfChangedAsync(FileViewerViewModel v)
    {
        var (exists, size, stamp) = await Task.Run(() =>
        {
            var fi = new FileInfo(v.FullPath);
            return fi.Exists ? (true, fi.Length, fi.LastWriteTimeUtc) : (false, 0L, default);
        });
        var gone = !exists && v.State != ViewerState.Missing;
        var changed = exists && (v.State == ViewerState.Missing || size != v.Size || stamp != v.Stamp);
        if (gone || changed) await ReloadViewerAsync(v, v.IsHead);
    }

    [RelayCommand]
    private void CloseViewer()
    {
        var old = Viewer;
        Viewer = null;
        old?.Release();
        SyncWatchers();
    }

    [RelayCommand]
    private void ClosePane() => Owner.ClosePaneCommand.Execute(null);

    [RelayCommand]
    private void DismissNotice() => Notice = "";

    // ───────────────────────── From the conversation ─────────────────────────

    /// <summary>A path in the conversation (a change card, a tool's file): the Files pane opens it at its line.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenLinkAsync(FileLink? link)
    {
        if (link is null) return;
        Owner.ShowPaneCommand.Execute(SidePane.Files);
        // Shown already, there was no change to hear about: it may still need its project
        if (!SamePath(Owner.ProjectFolder, _loadedRoot)) Activate();
        await _rootLoad;
        if (ProjectFiles.Resolve(Root ?? Owner.ProjectFolder, link.Path) is not { } full) return;
        Filter = "";
        Mode = FilesMode.All;
        await OpenFileAsync(full, link.Line, reveal: true);
    }

    /// <summary>A tool row's file (read, edit, write): opened at the change's first line when it has one.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task OpenToolFileAsync(ToolRowViewModel? row) =>
        row is { HasFile: true } ? OpenLinkAsync(new FileLink(row.FilePath, row.FirstChangedLine)) : Task.CompletedTask;

    // ───────────────────────── Actions on a file ─────────────────────────

    /// <summary>The file an action is for: a row, the viewer, a path; else the open file or the selected row.</summary>
    private string? PathOf(object? target) => target switch
    {
        FileNodeViewModel n => n.FullPath,
        FileViewerViewModel v => v.FullPath,
        string s when s.Length > 0 => s,
        _ => Viewer?.FullPath ?? SelectedNode?.FullPath ?? SelectedResult?.FullPath,
    };

    private string Display(string full) => Root is { } r && ProjectFiles.Relative(r, full) is { Length: > 0 } rel ? rel : full;

    /// <summary>
    /// A reference omp reads for itself: <c>@path</c> in a prompt makes omp attach the file's content (a folder: its
    /// listing) to the message, resolved from the project folder. Quoted when it has spaces or edge punctuation.
    /// </summary>
    public static string Mention(string path)
    {
        var p = path.Replace('\\', '/');
        var plain = p.Length > 0 && !p.Any(char.IsWhiteSpace) && !"`\"'([{<".Contains(p[0]) && !")]}>.,;:!?\"'`".Contains(p[^1]) && !p.Contains('@');
        return plain ? "@" + p : p.Contains('"') ? $"@'{p}'" : $"@\"{p}\"";
    }

    [RelayCommand]
    private void AddToMessage(object? target)
    {
        if (PathOf(target) is not { } full) return;
        Owner.InsertFileMention(Mention(Display(full)));
    }

    [RelayCommand]
    private void CopyPath(object? target)
    {
        if (PathOf(target) is { } full) Owner.CopyText(full);
    }

    /// <summary>Relative to the project, with the platform's separators (as editors copy it).</summary>
    [RelayCommand]
    private void CopyRelativePath(object? target)
    {
        if (PathOf(target) is { } full) Owner.CopyText(Display(full).Replace('/', Path.DirectorySeparatorChar));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenInDefaultAppAsync(object? target)
    {
        if (PathOf(target) is { } full) await StartAsync(FileOpeners.OpenWithSystem(full), "open " + Path.GetFileName(full));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RevealInFileManagerAsync(object? target)
    {
        if (PathOf(target) is not { } full) return;
        if (OperatingSystem.IsLinux())
        {
            // The FileManager1 service selects the file; without it, the folder opens
            if (!await Launch(FileOpeners.Reveal(full), true)) await StartAsync(FileOpeners.RevealFallback(full), "open the folder");
        }
        else await StartAsync(FileOpeners.Reveal(full), "show it in " + FileOpeners.FileManagerName);
    }

    /// <summary>Opens the viewer's file (at its line) or the selected row in <paramref name="editor"/>.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenInEditorAsync(EditorApp? editor)
    {
        if (editor is null || PathOf(null) is not { } full) return;
        var line = Viewer is { } v && SamePath(v.FullPath, full) ? v.TargetLine : null;
        await StartAsync(editor.Open(full, line), "open " + editor.Name);
    }

    private async Task StartAsync(ProcessStartInfo psi, string what)
    {
        Notice = "";
        if (!await Launch(psi, false)) Report($"Could not {what}: {Path.GetFileName(psi.FileName)} did not start.");
    }

    /// <summary>A failed action is said in the pane, or above the message box when the pane is closed.</summary>
    private void Report(string text)
    {
        if (IsActive) Notice = text;
        else Owner.ComposerMessage = text;
    }

    // ───────────────────────── The project menu (header) ─────────────────────────

    [RelayCommand]
    private void BrowseFiles() => Owner.ShowPaneCommand.Execute(SidePane.Files);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenProjectInFileManagerAsync()
    {
        if (Owner.ProjectFolder is { } root) await StartAsync(FileOpeners.OpenWithSystem(root), "open the folder");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenProjectInEditorAsync(EditorApp? editor)
    {
        if (editor is not null && Owner.ProjectFolder is { } root) await StartAsync(editor.Open(root), "open " + editor.Name);
    }

    /// <summary>A shell in the project folder, in the terminal panel.</summary>
    [RelayCommand]
    private void OpenProjectTerminal() => Owner.NewShellCommand.Execute(null);

    [RelayCommand]
    private void CopyProjectPath()
    {
        if (Owner.ProjectFolder is { } root) Owner.CopyText(root);
    }

    private void RebuildRecentProjects()
    {
        RecentProjects.Clear();
        foreach (var g in Owner.SessionGroups.Take(6)) RecentProjects.Add(g);
        HasRecentProjects = RecentProjects.Count > 0;
    }

    /// <summary>Looks for editors on <paramref name="pathVariable"/> (PATH by default), off the UI thread.</summary>
    public async Task DetectEditorsAsync(string? pathVariable = null)
    {
        var scan = Interlocked.Increment(ref _editorScan);
        var found = await Task.Run(() => FileOpeners.FindEditors(pathVariable));
        if (scan != _editorScan) return;
        Editors.Clear();
        foreach (var e in found) Editors.Add(e);
        HasEditors = Editors.Count > 0;
    }
}

public partial class MainViewModel
{
    private FilesViewModel? _files;

    /// <summary>The Files pane's state (created on first use).</summary>
    public FilesViewModel Files => _files ??= new FilesViewModel(this);

    /// <summary>
    /// "Add to message": a file reference at the caret of the message box (spaces around it as needed), then the
    /// keyboard in the message box.
    /// </summary>
    internal void InsertFileMention(string mention)
    {
        InsertDictatedText(mention);
        FocusComposerRequested?.Invoke();
    }
}
