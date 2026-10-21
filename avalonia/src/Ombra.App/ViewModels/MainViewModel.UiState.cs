using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isPasting;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartRenameCommand))] private bool _isSavingRename;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoSessionMatch))] private bool _isLoadingSessions;

    public bool IsOpeningSession => Phase == SessionPhase.Starting && Session.CurrentLaunch.ResumeSessionFile is not null;

    /// <summary>When the shown chat's omp began starting (<see cref="Stopwatch.GetTimestamp"/>); 0 while it is not.</summary>
    private long _startingSince;

    /// <summary>What happens while the shown chat cannot take a message yet, in words and seconds so a slow start never
    /// looks like an empty, dead window: the engine install's step (with its megabytes), else how long omp has been
    /// starting or opening the chat. Null once omp is ready, when it stopped (the recover card says why and shows the
    /// install), and for a chat that is not this window's to start (read only, open in another window).</summary>
    public string? StartupProgressText
    {
        get
        {
            if (IsReadOnly || IsLeaseReadOnly || CanRecover) return null;
            if (IsInstallingRuntime && RuntimeInstallText.Length > 0) return RuntimeInstallText;
            if (Phase is not (SessionPhase.NotStarted or SessionPhase.Starting) || _startingSince == 0) return null;
            var seconds = (int)Stopwatch.GetElapsedTime(_startingSince).TotalSeconds;
            return IsOpeningSession ? Loc.F("Opening the chat… {0}s", seconds) : Loc.F("Starting omp… {0}s", seconds);
        }
    }

    public bool ShowStartupProgress => StartupProgressText is not null;

    /// <summary>The bar under <see cref="StartupProgressText"/> fills only for an install step that knows its size.</summary>
    public bool StartupProgressIndeterminate => !IsInstallingRuntime || RuntimeInstallIndeterminate;

    /// <summary>Phase changed or a second passed: the startup line follows.</summary>
    private void UpdateStartupProgress()
    {
        var starting = Phase is SessionPhase.NotStarted or SessionPhase.Starting && !IsReadOnly && !IsLeaseReadOnly;
        if (!starting) _startingSince = 0;
        else if (_startingSince == 0) _startingSince = Stopwatch.GetTimestamp();
        OnPropertyChanged(nameof(StartupProgressText));
        OnPropertyChanged(nameof(ShowStartupProgress));
        OnPropertyChanged(nameof(StartupProgressIndeterminate));
    }

    partial void OnIsInstallingRuntimeChanged(bool value) => UpdateStartupProgress();
    partial void OnRuntimeInstallTextChanged(string value) => UpdateStartupProgress();
    partial void OnRuntimeInstallIndeterminateChanged(bool value) => OnPropertyChanged(nameof(StartupProgressIndeterminate));

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
