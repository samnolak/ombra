using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Localization;
using OmpGui.App.Services.Runtime;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// First run: install the pinned Original OMP Core runtime when omp is not found, and send the user to omp's own
/// provider setup (<c>/login</c> in omp's terminal UI) when omp has no model — omp (18.2.0, 18.8.0) exits before its RPC is
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

    private StartProblem StartProblem => CanRecover ? _open.Last?.StartProblem ?? StartProblem.None : StartProblem.None;

    /// <summary>omp was not found and the client can install the pinned runtime for it.</summary>
    public bool ShowInstallRuntime => StartProblem == StartProblem.NotFound && RuntimeInstaller is { Supported: true } && !_customCommand && !IsInstallingRuntime;

    /// <summary>omp started but has no model: its own setup (terminal UI) signs in or takes an API key.</summary>
    public bool NeedsProvider => StartProblem == StartProblem.NoModel;

    public string InstallRuntimeLabel => RuntimeInstaller?.FindInstalled() is null ? Loc.F("Install omp {0}", RuntimePack.OmpVersion) : Loc.T("Reinstall");

    public string RuntimeOffer =>
        Loc.F("OMP GUI runs the omp agent, which is not on this computer yet. It can install omp {0} (with Bun {1}) for this app only: pinned versions, verified downloads from npm, about 1.3 GB on disk.",
            RuntimePack.OmpVersion, RuntimePack.BunVersion);

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
            { Supported: false } i => Loc.F("No pinned runtime for this platform ({0}): install omp yourself and set the command above.", i.Platform ?? Loc.T("unknown")),
            { } i when i.FindInstalled() is { } rt => Loc.F("Installed: omp {0} on Bun {1} ({2}) in {3}.", RuntimePack.OmpVersion, RuntimePack.BunVersion, rt.Platform, rt.Directory) +
                (_customCommand ? Loc.T(" Not in use: the command above is set.") : Loc.T(" Used while the command above is empty.")),
            { } i when i.FindPrevious() is { } prev => Loc.F("This version of the app runs omp {0}; omp {1} from the previous version is in use until it is installed ({2}).", RuntimePack.OmpVersion, prev.Omp, prev.Runtime.Directory) +
                (_customCommand ? Loc.T(" Not in use: the command above is set.") : ""),
            _ => Loc.F("Not installed. \"Install\" downloads omp {0} and Bun {1} (pinned, verified; about 1.3 GB on disk).", RuntimePack.OmpVersion, RuntimePack.BunVersion),
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
        if (!transition && WorkingSessions().Count > 0)
        {
            SettingsMessage = IsRunning ? Loc.T("Stop the current run first: installing the runtime restarts omp.")
                : Loc.T("omp is working in another chat (marked in the sidebar): installing the runtime restarts omp, so stop that run first.");
            return;
        }
        _ompTransition = previous is not null ? RuntimePack.OmpVersion : null;
        IsInstallingRuntime = true;
        OnPropertyChanged(nameof(UpdatePill));
        OnPropertyChanged(nameof(ShowUpdatePill));
        RuntimeInstallText = Loc.T("Starting…");
        RuntimeInstallIndeterminate = true;
        _installCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var progress = new Progress<RuntimeInstallProgress>(p =>
        {
            RuntimeInstallIndeterminate = p.Total <= 0;
            RuntimeInstallPercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
            RuntimeInstallText = p.Total > 0
                ? string.Format(CultureInfo.InvariantCulture, Loc.T("{0}: {1:0} / {2:0} MB"), p.Stage, p.Done / 1_000_000.0, p.Total / 1_000_000.0)
                : p.Stage + "…";
        });
        try
        {
            // omp may be running from the install being replaced (Windows keeps running files locked). An earlier
            // pack is another folder: omp keeps working from it meanwhile.
            if (previous is null) await ForceStopReadySessionsAsync(_installCts.Token); // MainViewModel.OpenSessions.cs
            await installer.InstallAsync(progress, _installCts.Token);
            RuntimeInstallText = "";
            IsInstallingRuntime = false;
            RefreshRuntimeStatus();
            if (previous is not null)
            {
                // Never cut a run short: switch once no chat's omp is working.
                SettingsMessage = Loc.F("omp {0} installed. It takes over from omp {1} when the current run ends.", RuntimePack.OmpVersion, previous.Omp);
                while (WorkingSessions().Count > 0) await Task.Delay(500, _cts.Token);
            }
            SettingsMessage = Loc.F("omp {0} installed. Starting it…", RuntimePack.OmpVersion);
            await RestartAllSessionsAsync();
            SettingsMessage = Phase == SessionPhase.Ready ? Loc.F("omp {0} installed and running.", RuntimePack.OmpVersion) : Loc.T("omp is installed but did not start: see the conversation.");
            if (previous is not null && Phase == SessionPhase.Ready)
            {
                installer.RemovePrevious(); // what cannot go yet (a file still open) goes at the next start
                UpdateOutcome = Loc.F("omp updated to {0}", RuntimePack.OmpVersion);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the installer removed its partial files.
        }
        catch (OperationCanceledException)
        {
            RuntimeInstallText = previous is null ? Loc.T("Installation cancelled. Nothing was changed.")
                : Loc.F("omp {0} was not installed (cancelled): omp {1} from the previous version keeps working. Install it here when you are ready.", RuntimePack.OmpVersion, previous.Omp);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException
                                   or TimeoutException or NotSupportedException or System.ComponentModel.Win32Exception or AggregateException)
        {
            RuntimeInstallText = previous is null ? Loc.F("Installation failed: {0} Nothing was changed.", e.Message)
                : Loc.F("omp {0} could not be installed: {1} omp {2} from the previous version keeps working; try again here.", RuntimePack.OmpVersion, e.Message, previous.Omp);
            if (previous is not null) UpdateOutcome = Loc.T("omp not updated");
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
            if (Phase is SessionPhase.Stopped or SessionPhase.Faulted) Apply(Session.Snapshot());
        }
    }

    [RelayCommand]
    private void CancelRuntimeInstall() => _installCts?.Cancel();

    /// <summary>
    /// Adding a model provider when omp cannot start without one: <c>omp login</c> in a terminal tab. It lists every
    /// provider (omp's in-session <c>/login</c> hides the ones turned off for omp, which before the first sign-in are
    /// all of them: NetworkPrivacy) and afterwards fetches the models of the chosen one only. Strict network privacy
    /// lets every provider's sign-in and API addresses through while the tab runs: the user is adding one on purpose.
    /// </summary>
    [RelayCommand]
    private void SetUpProvider()
    {
        if (OmpCliLaunch is not { } launch) return;
        OmpGui.Rpc.OmpLaunchSpec spec;
        try { spec = launch(["login"], ProjectDirectory()); }
        catch (OmpGui.ClientCore.Network.NetworkPrivacyException e)
        {
            ComposerMessage = Loc.F("omp's sign-in was not opened: {0}", e.Message);
            return;
        }
        var env = spec.Environment.ToDictionary(kv => kv.Key, kv => kv.Value ?? "");
        Add(new TerminalViewModel(TerminalKind.OmpTui, "omp login", spec.FileName, spec.Arguments, spec.WorkingDirectory ?? ProjectDirectory(), env)
        {
            Lease = Privacy?.AllowSignIn(OmpGui.ClientCore.Network.NetworkPrivacy.AnyProvider),
        });
        ComposerMessage = Loc.T("Sign in to a model provider in the terminal beside the conversation: choose one with the arrow keys and Enter, finish in the browser (or paste an API key), then press Try again.");
    }
}
