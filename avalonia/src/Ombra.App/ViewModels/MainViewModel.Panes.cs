using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>What the right panel shows: one of its tools, the browser or the new-tab page; None while it is hidden.</summary>
public enum SidePane { None, Plan, Tasks, Files, Changes, Browser, NewTab }

/// <summary>
/// The right panel (Views/Panes/RightPanel), right of the conversation: a strip of tabs — the tools (Files, Changes,
/// Background tasks, Plan) and the browser's pages — with + for the new-tab page (the tools and the session's recent
/// files) and a toggle that hides and shows the panel. One tab is shown at a time; hiding the panel keeps its tabs. The
/// view fits the panel beside the conversation or over it (MainWindow.FitPanels).
/// </summary>
public partial class MainViewModel
{
    /// <summary>The tab the right panel shows, or None while the panel is hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidePaneOpen), nameof(IsPlanPaneShown), nameof(IsTasksPaneShown), nameof(IsFilesPaneShown),
        nameof(IsChangesPaneShown), nameof(IsFilesViewShown), nameof(IsNewTabShown), nameof(IsPreviewOpen), nameof(IsToolBarShown),
        nameof(ShowTodoCard))]
    private SidePane _activePane = SidePane.None;

    /// <summary>The right panel is on screen.</summary>
    public bool IsSidePaneOpen => ActivePane != SidePane.None;
    public bool IsPlanPaneShown => ActivePane == SidePane.Plan;
    public bool IsTasksPaneShown => ActivePane == SidePane.Tasks;
    public bool IsFilesPaneShown => ActivePane == SidePane.Files;
    public bool IsChangesPaneShown => ActivePane == SidePane.Changes;
    /// <summary>The Files pane is on screen: as the Files tab, or as the Changes tab (its changed files only).</summary>
    public bool IsFilesViewShown => ActivePane is SidePane.Files or SidePane.Changes;
    public bool IsNewTabShown => ActivePane == SidePane.NewTab;
    /// <summary>A tool with a line of its own under the strip (what it counts, Refresh): not the browser or the new-tab page.</summary>
    public bool IsToolBarShown => ActivePane is SidePane.Plan or SidePane.Tasks or SidePane.Files or SidePane.Changes;

    /// <summary>The strip's tabs, in the order they were opened (the browser's pages together, in the browser's order).</summary>
    public ObservableCollection<PanelTab> PanelTabs { get; } = [];

    /// <summary>The browser's pages are in the strip.</summary>
    public bool HasBrowserTabs => PanelTabs.Any(t => t.IsPage);

    /// <summary>What the panel showed last (the toggle brings it back).</summary>
    private SidePane _lastPane = SidePane.None;

    /// <summary>Where a tool chosen on the new-tab page takes the page's place in the strip (-1: at the end).</summary>
    private int _insertTabAt = -1;

    /// <summary>
    /// The one-line task card above the composer, unless the Plan pane shows the same list (and more) already: two
    /// copies of one list only take room from the conversation.
    /// </summary>
    public bool ShowTodoCard => HasTodos && !IsPlanPaneShown;

    partial void OnHasTodosChanged(bool value) => OnPropertyChanged(nameof(ShowTodoCard));

    /// <summary>The Plan pane's state.</summary>
    public PlanViewModel Plan => field ??= new PlanViewModel(this);

    /// <summary>The Background tasks pane's state (and the count in the header's menus).</summary>
    public TasksViewModel Tasks => field ??= NewTasks();

    private bool _tasksCreated;

    private TasksViewModel NewTasks()
    {
        _tasksCreated = true;
        var tasks = new TasksViewModel(this);
        tasks.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TasksViewModel.HasRunning)) OnPropertyChanged(nameof(HasNoBackgroundWork));
        };
        return tasks;
    }

    partial void OnActivePaneChanged(SidePane value)
    {
        Tasks.IsShown = value == SidePane.Tasks;
        if (value != SidePane.None)
        {
            EnsureTab(value);
            _lastPane = value;
        }
        if (value == SidePane.NewTab) RefreshSessionChanges();
        SyncActiveTab();
    }

    /// <summary>Shows <paramref name="pane"/>; the one already shown hides the panel (a shortcut or menu item works as a toggle).</summary>
    [RelayCommand]
    private void TogglePane(SidePane pane) => ActivePane = ActivePane == pane ? SidePane.None : pane;

    /// <summary>Shows <paramref name="pane"/> (keeps it shown when it already is).</summary>
    [RelayCommand]
    private void ShowPane(SidePane pane) => ActivePane = pane;

    /// <summary>Hides the panel; its tabs stay for the next time it is shown.</summary>
    [RelayCommand]
    private void ClosePane() => ActivePane = SidePane.None;

    /// <summary>The panel toggle (the strip's right end, the header while the panel is hidden): hides the panel, or shows
    /// what it showed last (the new-tab page when it has no tab).</summary>
    [RelayCommand]
    private void ToggleRightPanel()
    {
        if (IsSidePaneOpen)
        {
            ActivePane = SidePane.None;
            return;
        }
        ActivePane = HasTabFor(_lastPane) && _lastPane != SidePane.None ? _lastPane : PanelTabs.FirstOrDefault(t => t.ShowsInStrip)?.Pane ?? SidePane.NewTab;
    }

    /// <summary>A tab of the strip clicked: its tool, or its page in the browser.</summary>
    [RelayCommand]
    private void SelectPanelTab(PanelTab? tab)
    {
        if (tab is null || !PanelTabs.Contains(tab)) return;
        if (tab.Page is { } page && page != Preview.ActiveTab) Preview.SelectTabCommand.Execute(page);
        ActivePane = tab.Pane;
        SyncActiveTab(); // a page of the browser already shown: only the page changed
    }

    /// <summary>
    /// A tab's ×: a tool leaves the strip; a page closes in the browser, except the user's own first page, which takes
    /// the browser out of the panel (as the browser's close button did). The neighbour is shown when the tab closed was.
    /// </summary>
    [RelayCommand]
    private void ClosePanelTab(PanelTab? tab)
    {
        if (tab is null || !PanelTabs.Contains(tab)) return;
        var at = PanelTabs.IndexOf(tab);
        if (tab.Page is { CanClose: true } page) Preview.CloseTabCommand.Execute(page); // the strip follows (SyncPageTabs)
        else if (tab.IsPage) RemoveBrowserTabs();
        else
        {
            PanelTabs.RemoveAt(at);
            tab.Dispose();
        }
        if (!HasTabFor(ActivePane)) ShowNeighbour(at);
    }

    /// <summary>
    /// A tool on the new-tab page: it takes the page's place in the strip. The browser, already in the panel, opens a
    /// new page of its own.
    /// </summary>
    [RelayCommand]
    private void OpenTool(SidePane pane)
    {
        if (pane is SidePane.None or SidePane.NewTab) return;
        if (PanelTabs.FirstOrDefault(t => t.Pane == SidePane.NewTab) is { } blank)
        {
            _insertTabAt = PanelTabs.IndexOf(blank);
            PanelTabs.Remove(blank);
            blank.Dispose();
        }
        if (pane == SidePane.Browser && HasBrowserTabs) Preview.NewTabCommand.Execute(null);
        ActivePane = pane;
        _insertTabAt = -1;
        SyncActiveTab();
    }

    /// <summary>Opening the header's menu or the summary reads the count of background jobs again.</summary>
    [RelayCommand]
    private void RefreshRunningTasks() => Tasks.OnMenuOpened();

    private bool HasTabFor(SidePane pane) => pane switch
    {
        SidePane.None => true,
        SidePane.Browser => HasBrowserTabs,
        _ => PanelTabs.Any(t => !t.IsPage && t.Pane == pane),
    };

    /// <summary>The tab now at <paramref name="at"/> (or the one before), else the panel hides.</summary>
    private void ShowNeighbour(int at)
    {
        var visible = PanelTabs.Where(t => t.ShowsInStrip).ToList();
        if (visible.Count == 0)
        {
            ActivePane = SidePane.None;
            return;
        }
        var next = PanelTabs.Skip(at).FirstOrDefault(t => t.ShowsInStrip) ?? visible[^1];
        SelectPanelTab(next);
    }

    /// <summary>A tab for <paramref name="pane"/> is in the strip (the browser: all its pages).</summary>
    private void EnsureTab(SidePane pane)
    {
        if (HasTabFor(pane)) return;
        if (pane == SidePane.Browser)
        {
            var at = _insertTabAt >= 0 ? _insertTabAt : PanelTabs.Count;
            foreach (var page in Preview.Tabs) PanelTabs.Insert(at++, new PanelTab(page));
            return;
        }
        var tab = new PanelTab(pane);
        if (_insertTabAt >= 0 && _insertTabAt <= PanelTabs.Count) PanelTabs.Insert(_insertTabAt, tab);
        else PanelTabs.Add(tab);
    }

    /// <summary>The browser's pages leave the strip (its last tab's ×, or the browser closed).</summary>
    private void RemoveBrowserTabs()
    {
        Preview.IsAnnotating = false;
        foreach (var t in PanelTabs.Where(t => t.IsPage).ToList())
        {
            PanelTabs.Remove(t);
            t.Dispose();
        }
    }

    /// <summary>The browser's own list of pages changed (omp opened one, a pop-up closed…): the strip's pages follow it.</summary>
    private void SyncPageTabs()
    {
        if (!HasBrowserTabs) return;
        foreach (var gone in PanelTabs.Where(t => t.Page is { } p && !Preview.Tabs.Contains(p)).ToList())
        {
            PanelTabs.Remove(gone);
            gone.Dispose();
        }
        PanelTab? previous = null;
        foreach (var page in Preview.Tabs)
        {
            var mine = PanelTabs.FirstOrDefault(t => t.Page == page);
            if (mine is null)
            {
                mine = new PanelTab(page);
                var at = previous is not null ? PanelTabs.IndexOf(previous) + 1
                    : PanelTabs.FirstOrDefault(t => t.IsPage) is { } first ? PanelTabs.IndexOf(first) : PanelTabs.Count;
                PanelTabs.Insert(at, mine);
            }
            previous = mine;
        }
        SyncActiveTab();
    }

    /// <summary>The tab shown is marked active: the tool shown, or the browser's active page while the browser is shown.</summary>
    private void SyncActiveTab()
    {
        foreach (var t in PanelTabs)
            t.IsActive = t.Page is { } page ? ActivePane == SidePane.Browser && page.IsActive : t.Pane == ActivePane;
    }

    /// <summary>What a tab is called in the strip and the menus.</summary>
    public static string PaneTitle(SidePane pane) => pane switch
    {
        SidePane.Files => Loc.T("Files"),
        SidePane.Changes => Loc.T("Changes"),
        SidePane.Tasks => Loc.T("Background tasks"),
        SidePane.Plan => Loc.T("Plan"),
        SidePane.Browser => Loc.T("Browser"),
        SidePane.NewTab => Loc.T("New tab"),
        _ => "",
    };

    public static string PaneTip(SidePane pane) => pane switch
    {
        SidePane.Files => FilesTip,
        SidePane.Changes => ChangesTip,
        SidePane.Tasks => TasksTip,
        SidePane.Plan => PlanTip,
        SidePane.Browser => BrowserTip,
        _ => PaneTitle(pane),
    };

    /// <summary>The icon resource (Views/Icons*.axaml) of a tab.</summary>
    public static string PaneIconKey(SidePane pane) => pane switch
    {
        SidePane.Files => "IconFiles",
        SidePane.Changes => "IconChanges",
        SidePane.Tasks => "IconLayers",
        SidePane.Plan => "IconChecklist",
        SidePane.Browser => "IconGlobe",
        _ => "IconTools",
    };

    // Shortcut hints in the menus, the new-tab page and the tabs' tooltips, as the platform writes them (⌘ on macOS,
    // Ctrl elsewhere). The terminal's is Control on every platform: spelled out (the ⌃ glyph read as a caret).
    public static string FilesShortcut { get; } = CommandShortcut("F");
    public static string ChangesShortcut { get; } = CommandShortcut("G");
    public static string PlanShortcut { get; } = CommandShortcut("P");
    public static string TasksShortcut { get; } = CommandShortcut("T");
    public static string BrowserShortcut { get; } = CommandShortcut("B");
    public static string TerminalShortcut { get; } = "Ctrl+`";

    public static string FilesTip { get; } = Loc.F("Files ({0})", FilesShortcut);
    public static string ChangesTip { get; } = Loc.F("Changes ({0})", ChangesShortcut);
    public static string PlanTip { get; } = Loc.F("Plan ({0})", PlanShortcut);
    public static string TasksTip { get; } = Loc.F("Background tasks ({0})", TasksShortcut);
    public static string BrowserTip { get; } = Loc.F("Browser preview ({0})", BrowserShortcut);

    private static string CommandShortcut(string key) => OperatingSystem.IsMacOS() ? "⇧⌘" + key : "Ctrl+Shift+" + key;

    /// <summary>The shown chat's count of ended runs at the last apply (-1 after another chat is shown: its own count
    /// is no news).</summary>
    private long _paneRunsEnded = -1;

    /// <summary>Called from <see cref="Apply"/> for every snapshot: the plan and the background tasks follow it.</summary>
    private void ApplyPanes(SessionSnapshot s)
    {
        Plan.Apply(s.Todos, s.PlanHistory, s.SessionFile, DateTimeOffset.Now);
        Tasks.Apply(s);
        if (_paneRunsEnded >= 0 && s.RunsEnded > _paneRunsEnded) Tasks.OnRunEnded();
        _paneRunsEnded = Math.Max(_paneRunsEnded, s.RunsEnded);
    }

    /// <summary>omp lists /jobs among the commands it runs over RPC (MainViewModel.Commands.cs keeps its catalog).</summary>
    internal bool OmpListsJobs => _commands.Any(c => c.Name == "jobs");

    /// <summary>The window is closing (disposed or disposing): the panes stop reading from omp.</summary>
    internal bool IsClosing => Volatile.Read(ref _disposed) != 0;

    internal async Task<bool> RefreshSubagentsAsync()
    {
        try { return !IsClosing && await Session.RefreshSubagentsAsync(_cts.Token); }
        catch (ObjectDisposedException) { return false; }
    }

    internal async Task<(IReadOnlyList<SubagentMessage> Messages, string? Error)> GetSubagentMessagesAsync(string id)
    {
        try { return IsClosing ? ([], Loc.T("the window is closing")) : await Session.GetSubagentMessagesAsync(id, _cts.Token); }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return ([], Loc.T("the window is closing")); }
    }
}
