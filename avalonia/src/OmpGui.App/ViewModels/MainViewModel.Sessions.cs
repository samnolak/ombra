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
            var item = new SessionItemViewModel(c) { IsCurrent = c.Path == current, IsPinned = _pinnedIds.Contains(c.Id) };
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
        SessionGroups.Clear();
        // Sessions is newest first, so the first appearance of a folder orders the groups by recency.
        var groups = new List<SessionGroupViewModel>();
        foreach (var item in Sessions)
        {
            var g = groups.FirstOrDefault(x => string.Equals(x.Cwd, item.Cwd, StringComparison.Ordinal));
            if (g is null) groups.Add(g = new SessionGroupViewModel(item.Cwd, string.Equals(item.Cwd, project, StringComparison.Ordinal)));
            g.Items.Add(item);
        }
        foreach (var g in groups.OrderByDescending(x => x.IsCurrentProject)) SessionGroups.Add(g);
    }
}
