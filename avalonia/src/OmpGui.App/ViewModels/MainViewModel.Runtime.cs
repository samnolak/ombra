using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Services.Runtime;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// First run: install the pinned Original OMP Core runtime when omp is not found, and send the user to omp's own
/// provider setup (<c>/login</c> in omp's terminal UI) when omp has no model — omp 18.2.0 exits before its RPC is
/// ready in that case, so the RPC sign-in cannot be used yet.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Installs <see cref="RuntimePack"/>; null when the client cannot install one (tests, unknown platform).</summary>
    public RuntimeInstaller? RuntimeInstaller { get; init; }

    private CancellationTokenSource? _installCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstallRuntime), nameof(InstallRuntimeLabel), nameof(ShowRuntimeInstallResult))]
    [NotifyCanExecuteChangedFor(nameof(InstallRuntimeCommand))]
    private bool _isInstallingRuntime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRuntimeInstallResult))]
    private string _runtimeInstallText = "";

    /// <summary>How the last install ended (failed, cancelled), shown until the next attempt.</summary>
    public bool ShowRuntimeInstallResult => !IsInstallingRuntime && RuntimeInstallText.Length > 0;
    [ObservableProperty] private double _runtimeInstallPercent;
    [ObservableProperty] private bool _runtimeInstallIndeterminate = true;
    [ObservableProperty] private string _runtimeStatusText = "";

    /// <summary>The user set their own omp command: installing the pinned runtime would not change what starts.</summary>
    private bool _customCommand;

    private StartProblem StartProblem => CanRecover ? _last?.StartProblem ?? StartProblem.None : StartProblem.None;

    /// <summary>omp was not found and the client can install the pinned runtime for it.</summary>
    public bool ShowInstallRuntime => StartProblem == StartProblem.NotFound && RuntimeInstaller is { Supported: true } && !_customCommand && !IsInstallingRuntime;

    /// <summary>omp started but has no model: its own setup (terminal UI) signs in or takes an API key.</summary>
    public bool NeedsProvider => StartProblem == StartProblem.NoModel;

    public string InstallRuntimeLabel => RuntimeInstaller?.FindInstalled() is null ? $"Install omp {RuntimePack.OmpVersion}" : "Reinstall";

    public string RuntimeOffer =>
        $"OMP GUI runs the omp agent, which is not on this computer yet. It can install omp {RuntimePack.OmpVersion} (with Bun " +
        $"{RuntimePack.BunVersion}) for this app only: pinned versions, verified downloads from npm, about 1.3 GB on disk.";

    private void ApplyRuntimeState()
    {
        OnPropertyChanged(nameof(ShowInstallRuntime));
        OnPropertyChanged(nameof(ShowRecoverButton));
        OnPropertyChanged(nameof(NeedsProvider));
        OnPropertyChanged(nameof(RecoverHint));
        InstallRuntimeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What Settings shows about the pinned runtime.</summary>
    private void RefreshRuntimeStatus()
    {
        _customCommand = LoadSettingsOrDefault() is { } o && (o.Command is not null || o.PrefixArgs.Length > 0);
        RuntimeStatusText = RuntimeInstaller switch
        {
            null => "",
            { Supported: false } i => $"No pinned runtime for this platform ({i.Platform ?? "unknown"}): install omp yourself and set the command above.",
            { } i when i.FindInstalled() is { } rt => $"Installed: omp {RuntimePack.OmpVersion} on Bun {RuntimePack.BunVersion} ({rt.Platform}) in {rt.Directory}." +
                (_customCommand ? " Not in use: the command above is set." : " Used while the command above is empty."),
            { } i when i.FindPrevious() is { } prev => $"This version of the app runs omp {RuntimePack.OmpVersion}; omp {prev.Omp} from the previous version is in use until it is installed ({prev.Runtime.Directory})." +
                (_customCommand ? " Not in use: the command above is set." : ""),
            _ => $"Not installed. \"Install\" downloads omp {RuntimePack.OmpVersion} and Bun {RuntimePack.BunVersion} (pinned, verified; about 1.3 GB on disk).",
        };
        OnPropertyChanged(nameof(InstallRuntimeLabel));
        ApplyRuntimeState();
    }

    private bool CanInstallRuntime() => RuntimeInstaller is { Supported: true } && !IsInstallingRuntime;

    [RelayCommand(CanExecute = nameof(CanInstallRuntime))]
    private Task InstallRuntimeAsync() => InstallRuntimeCoreAsync(transition: RuntimeInstaller?.FindInstalled() is null && RuntimeInstaller?.FindPrevious() is not null);

    /// <summary>omp version being installed because an update of the client brought a new runtime pack; else null.</summary>
    private string? _ompTransition;

    /// <summary>
    /// After an update that brings a new runtime pack: omp keeps running from the pack the previous version installed
    /// (App picks it) while the new one installs; omp then restarts on it once no run is active, and the old pack is removed.
    /// </summary>
    private void MoveOmpToNewPack()
    {
        if (RuntimeInstaller is not { Supported: true } installer || _customCommand || IsInstallingRuntime) return;
        if (installer.FindInstalled() is not null || installer.FindPrevious() is null) return;
        _ = InstallRuntimeCoreAsync(transition: true);
    }

    /// <param name="transition">
    /// An earlier pack is in use: install next to it without stopping omp, and switch when omp is idle.
    /// </param>
    private async Task InstallRuntimeCoreAsync(bool transition)
    {
        if (RuntimeInstaller is not { } installer) return;
        var previous = transition ? installer.FindPrevious() : null;
        if (!transition && Phase is SessionPhase.Running or SessionPhase.Aborting)
        {
            SettingsMessage = "Stop the current run first: installing the runtime restarts omp.";
            return;
        }
        _ompTransition = previous is not null ? RuntimePack.OmpVersion : null;
        IsInstallingRuntime = true;
        OnPropertyChanged(nameof(UpdatePill));
        OnPropertyChanged(nameof(ShowUpdatePill));
        RuntimeInstallText = "Starting…";
        RuntimeInstallIndeterminate = true;
        _installCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var progress = new Progress<RuntimeInstallProgress>(p =>
        {
            RuntimeInstallIndeterminate = p.Total <= 0;
            RuntimeInstallPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
            RuntimeInstallText = p.Total > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{p.Stage}: {p.Done / 1_000_000.0:0} / {p.Total / 1_000_000.0:0} MB")
                : p.Stage + "…";
        });
        try
        {
            // omp may be running from the install being replaced (Windows keeps running files locked). An earlier
            // pack is another folder: omp keeps working from it meanwhile.
            if (previous is null && Phase == SessionPhase.Ready) await _session.ForceStopAsync(_installCts.Token);
            await installer.InstallAsync(progress, _installCts.Token);
            RuntimeInstallText = "";
            IsInstallingRuntime = false;
            RefreshRuntimeStatus();
            if (previous is not null)
            {
                // Never cut a run short: switch once omp is idle.
                SettingsMessage = $"omp {RuntimePack.OmpVersion} installed. It takes over from omp {previous.Omp} when the current run ends.";
                while (Phase is SessionPhase.Running or SessionPhase.Aborting) await Task.Delay(500, _cts.Token);
            }
            SettingsMessage = $"omp {RuntimePack.OmpVersion} installed. Starting it…";
            await _session.RecoverAsync(_cts.Token);
            Apply(_session.Snapshot());
            SettingsMessage = Phase == SessionPhase.Ready ? $"omp {RuntimePack.OmpVersion} installed and running." : "omp is installed but did not start: see the conversation.";
            if (previous is not null && Phase == SessionPhase.Ready)
            {
                installer.RemovePrevious(); // what cannot go yet (a file still open) goes at the next start
                UpdateOutcome = $"omp updated to {RuntimePack.OmpVersion}";
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the installer removed its partial files.
        }
        catch (OperationCanceledException)
        {
            RuntimeInstallText = previous is null ? "Installation cancelled. Nothing was changed."
                : $"omp {RuntimePack.OmpVersion} was not installed (cancelled): omp {previous.Omp} from the previous version keeps working. Install it here when you are ready.";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException
                                   or TimeoutException or NotSupportedException or System.ComponentModel.Win32Exception or AggregateException)
        {
            RuntimeInstallText = previous is null ? "Installation failed: " + e.Message + " Nothing was changed."
                : $"omp {RuntimePack.OmpVersion} could not be installed: {e.Message} omp {previous.Omp} from the previous version keeps working; try again here.";
            if (previous is not null) UpdateOutcome = "omp not updated";
        }
        finally
        {
            IsInstallingRuntime = false;
            _ompTransition = null;
            _installCts?.Dispose();
            _installCts = null;
            RefreshRuntimeStatus();
            OnPropertyChanged(nameof(UpdatePill));
            OnPropertyChanged(nameof(ShowUpdatePill));
            if (Phase is SessionPhase.Stopped or SessionPhase.Faulted) Apply(_session.Snapshot());
        }
    }

    [RelayCommand]
    private void CancelRuntimeInstall() => _installCts?.Cancel();

    /// <summary>omp's own setup: its terminal UI, where <c>/login</c> signs in to a provider (or an API key is set).</summary>
    [RelayCommand]
    private void SetUpProvider()
    {
        OpenOmpTui();
        ComposerMessage = "In omp's terminal below: type /login and pick a provider (or set an API key), then press Restart omp.";
    }
}
