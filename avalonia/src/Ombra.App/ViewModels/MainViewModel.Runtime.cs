using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services.Runtime;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// Set up or repair the app-owned shared engine. When a session has no model, provider setup uses the explicit
/// management terminal: RPC sign-in is unavailable until that logical session can start.
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

    /// <summary>The compatible owned runtime is missing and the client can install it.</summary>
    public bool ShowInstallRuntime => StartProblem == StartProblem.NotFound && RuntimeInstaller is { Supported: true } && !_customCommand && !IsInstallingRuntime;

    /// <summary>omp started but has no model: its own setup (terminal UI) signs in or takes an API key.</summary>
    public bool NeedsProvider => StartProblem == StartProblem.NoModel;

    public string InstallRuntimeLabel => RuntimeInstaller?.FindInstalled() is not null ? Loc.T("Repair engine")
        : RuntimeInstaller?.FindPrevious() is not null ? Loc.T("Update engine") : Loc.T("Set up engine");

    public string RuntimeOffer =>
        Loc.T("Ombra's integrated engine needs setup or repair. Ombra manages its verified engine files here (about 1.3 GB on disk); no separate omp installation is required. Any previous engine files remain unchanged during setup.");

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
            { Supported: false } i => Loc.F("Ombra's integrated engine is unavailable on this platform ({0}). Use an Ombra build for this platform.", i.Platform ?? Loc.T("unknown")),
            { } i when i.FindInstalled() is { } rt => Loc.F("Integrated engine ready: omp {0}, Bun {1} ({2}) at {3}.", RuntimePack.OmpVersion, RuntimePack.BunVersion, rt.Platform, rt.Directory) +
                (_customCommand ? Loc.T(" A developer command override is active.") : Loc.T(" Managed by Ombra.")),
            { } i when i.FindPrevious() is { } prev => Loc.F("Previous engine files are retained at {0}. Update or repair Ombra's integrated engine here to continue.", prev.Runtime.Directory) +
                (_customCommand ? Loc.T(" A developer command override is active.") : ""),
            _ => Loc.T("Ombra's integrated engine is not ready. Set it up or repair it here (verified engine files, about 1.3 GB on disk)."),
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
    /// After an update that brings a new runtime pack, install alongside the retained earlier pack.
    /// An unpatched earlier runtime is never started as a substitute for the required history contract.
    /// </summary>
    private void MoveOmpToNewPack()
    {
        if (RuntimeInstaller is not { Supported: true } installer || _customCommand || IsInstallingRuntime) return;
        if (installer.FindInstalled() is not null || installer.FindPrevious() is null) return;
        _ = InstallRuntimeCoreAsync(transition: true);
    }

    /// <param name="transition">
    /// An earlier pack is retained: install next to it without changing it, then restart on the compatible pack.
    /// </param>
    private async Task InstallRuntimeCoreAsync(bool transition)
    {
        if (RuntimeInstaller is not { } installer) return;
        var previous = transition ? installer.FindPrevious() : null;
        if (!transition && WorkingSessions().Count > 0)
        {
            SettingsMessage = IsRunning ? Loc.T("Stop the current run first: repairing Ombra's engine restarts active chats.")
                : Loc.T("The engine is working in another chat (marked in the sidebar). Stop that run before repairing the engine.");
            return;
        }
        // Installing is an explicit retry of missing-runtime starts, not permission to revive crashed chats.
        var missingRuntimeChats = _opens.Values.Where(open =>
            open.Controller.Snapshot() is { StartFailed: true, StartProblem: StartProblem.NotFound }).ToArray();
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
            // Installation is staged beside the retained previous pack. Stop current sessions when replacing
            // the active owned pack (Windows keeps running files locked).
            if (previous is null) await ForceStopReadySessionsAsync(_installCts.Token); // MainViewModel.OpenSessions.cs
            await installer.InstallAsync(progress, _installCts.Token);
            RuntimeInstallText = "";
            IsInstallingRuntime = false;
            RefreshRuntimeStatus();
            if (previous is not null)
            {
                // Never cut a run short: switch once no chat's omp is working.
                SettingsMessage = Loc.T("Ombra's engine update is ready. It takes over when the current run ends.");
                while (WorkingSessions().Count > 0) await Task.Delay(500, _cts.Token);
            }
            SettingsMessage = Loc.T("Ombra's engine is ready. Starting it…");
            await RestartAllSessionsAsync();
            foreach (var open in missingRuntimeChats)
            {
                if (!_opens.ContainsKey(open.Controller) ||
                    open.Controller.Snapshot() is not { StartFailed: true, StartProblem: StartProblem.NotFound }) continue;
                await open.Controller.RecoverAsync(_cts.Token);
                ApplyIfShown(open);
            }
            SettingsMessage = Phase == SessionPhase.Ready ? Loc.T("Ombra's engine is ready and running.") : Loc.T("Ombra's engine did not start: see the conversation for recovery options.");
            if (previous is not null && Phase == SessionPhase.Ready)
            {
                // Keep the previous installation unchanged; migration only adds the new immutable pack.
                UpdateOutcome = Loc.T("Engine updated");
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // The window is closing: the installer removed its partial files.
        }
        catch (OperationCanceledException)
        {
            RuntimeInstallText = previous is null ? Loc.T("Engine setup cancelled. Nothing was changed.")
                : Loc.T("Engine update cancelled. Previous engine files are unchanged. Update or repair Ombra's integrated engine here to continue.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException
                                   or TimeoutException or NotSupportedException or System.ComponentModel.Win32Exception or AggregateException)
        {
            RuntimeInstallText = previous is null ? Loc.F("Engine setup failed: {0} Nothing was changed. Try repairing the engine here.", e.Message)
                : Loc.F("Engine update failed: {0} Previous engine files are unchanged. Try repairing Ombra's integrated engine here.", e.Message);
            if (previous is not null) UpdateOutcome = Loc.T("Engine not updated");
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
        Ombra.Rpc.OmpLaunchSpec spec;
        try { spec = launch(["login"], ProjectDirectory()); }
        catch (Ombra.ClientCore.Network.NetworkPrivacyException e)
        {
            ComposerMessage = Loc.F("omp's sign-in was not opened: {0}", e.Message);
            return;
        }
        var env = spec.Environment.ToDictionary(kv => kv.Key, kv => kv.Value ?? "");
        Add(new TerminalViewModel(TerminalKind.OmpTui, "omp login", spec.FileName, spec.Arguments, spec.WorkingDirectory ?? ProjectDirectory(), env)
        {
            Lease = Privacy?.AllowSignIn(Ombra.ClientCore.Network.NetworkPrivacy.AnyProvider),
        });
        ComposerMessage = Loc.T("Sign in to a model provider in the terminal beside the conversation: choose one with the arrow keys and Enter, finish in the browser (or paste an API key), then press Try again.");
    }
}
