using CommunityToolkit.Mvvm.ComponentModel;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isPasting;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartRenameCommand))] private bool _isSavingRename;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoSessionMatch))] private bool _isLoadingSessions;

    public bool IsOpeningSession => Phase == SessionPhase.Starting && Session.CurrentLaunch.ResumeSessionFile is not null;

    internal WindowLayoutOptions? LoadWindowLayout()
    {
        try { return _settings?.Load().WindowLayout; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    internal void SaveWindowLayout(WindowLayoutOptions layout) => Persist(o => o with
    {
        WindowLayout = layout,
        SidebarWidth = SidebarWidth == SidebarDefaultWidth ? null : SidebarWidth,
    });

    internal void RestorePanelVisibility(WindowLayoutOptions layout)
    {
        IsSidebarVisible = layout.SidebarVisible;
        IsDebugVisible = layout.EventsVisible;
        // Showing a saved terminal must not start a shell or replay commands on launch.
        IsTerminalOpen = layout.TerminalVisible;
        if (Enum.TryParse<SidePane>(layout.ActivePane, out var pane) && Enum.IsDefined(pane)) ActivePane = pane;
    }
}
