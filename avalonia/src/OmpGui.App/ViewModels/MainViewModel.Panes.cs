using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>The panes Claude Code's Views menu opens beside the conversation.</summary>
public enum SidePane { None, Plan, Tasks, Files }

/// <summary>
/// The Views menu (⋮ in the header) and the side pane right of the conversation: Plan (omp's todo list), Background
/// tasks (subagents and jobs) and Files. One pane at a time; the view fits it beside the conversation or over it
/// (MainWindow.FitPanels).
/// </summary>
public partial class MainViewModel
{
    /// <summary>The side pane shown right of the conversation (one at a time), or None.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidePaneOpen), nameof(IsPlanPaneShown), nameof(IsTasksPaneShown), nameof(IsFilesPaneShown),
        nameof(SidePaneTitle), nameof(ShowTodoCard))]
    private SidePane _activePane = SidePane.None;

    public bool IsSidePaneOpen => ActivePane != SidePane.None;
    public bool IsPlanPaneShown => ActivePane == SidePane.Plan;
    public bool IsTasksPaneShown => ActivePane == SidePane.Tasks;
    public bool IsFilesPaneShown => ActivePane == SidePane.Files;

    public string SidePaneTitle => ActivePane switch
    {
        SidePane.Plan => "Plan",
        SidePane.Tasks => "Background tasks",
        SidePane.Files => "Files",
        _ => "",
    };

    /// <summary>
    /// The one-line task card above the composer, unless the Plan pane shows the same list (and more) already: two
    /// copies of one list only take room from the conversation.
    /// </summary>
    public bool ShowTodoCard => HasTodos && !IsPlanPaneShown;

    partial void OnHasTodosChanged(bool value) => OnPropertyChanged(nameof(ShowTodoCard));

    /// <summary>The Plan pane's state.</summary>
    public PlanViewModel Plan => field ??= new PlanViewModel(this);

    /// <summary>The Background tasks pane's state (and the count in the Views menu).</summary>
    public TasksViewModel Tasks => field ??= NewTasks();

    private bool _tasksCreated;

    private TasksViewModel NewTasks()
    {
        _tasksCreated = true;
        return new TasksViewModel(this);
    }

    partial void OnActivePaneChanged(SidePane value) => Tasks.IsShown = value == SidePane.Tasks;

    /// <summary>Shows <paramref name="pane"/>; the pane already shown closes (a menu item works as a toggle).</summary>
    [RelayCommand]
    private void TogglePane(SidePane pane) => ActivePane = ActivePane == pane ? SidePane.None : pane;

    /// <summary>Shows <paramref name="pane"/> (keeps it open when it already is).</summary>
    [RelayCommand]
    private void ShowPane(SidePane pane) => ActivePane = pane;

    [RelayCommand]
    private void ClosePane() => ActivePane = SidePane.None;

    /// <summary>The Views menu opened: the count of background jobs is read again.</summary>
    [RelayCommand]
    private void ViewsMenuOpened() => Tasks.OnMenuOpened();

    // Shortcut hints in the Views menu, as the platform writes them (⌘ on macOS, Ctrl elsewhere).
    public static string FilesShortcut { get; } = CommandShortcut("F");
    public static string PlanShortcut { get; } = CommandShortcut("P");
    public static string TasksShortcut { get; } = CommandShortcut("T");
    public static string BrowserShortcut { get; } = CommandShortcut("B");
    public static string TerminalShortcut { get; } = OperatingSystem.IsMacOS() ? "⌃`" : "Ctrl+`";

    private static string CommandShortcut(string key) => OperatingSystem.IsMacOS() ? "⇧⌘" + key : "Ctrl+Shift+" + key;

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
        try { return !IsClosing && await _session.RefreshSubagentsAsync(_cts.Token); }
        catch (ObjectDisposedException) { return false; }
    }

    internal async Task<(IReadOnlyList<SubagentMessage> Messages, string? Error)> GetSubagentMessagesAsync(string id)
    {
        try { return IsClosing ? ([], "the window is closing") : await _session.GetSubagentMessagesAsync(id, _cts.Token); }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return ([], "the window is closing"); }
    }
}
