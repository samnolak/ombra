using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// Settings → Updates and the sidebar notice. The release feed is checked at every start and once a day (unless turned
/// off); a newer version found then is downloaded, verified and unpacked next to the app by itself, and swapped in when
/// the app restarts or quits (<see cref="UpdateInstaller"/>) — the sidebar offers the restart. A copy that cannot
/// replace itself (development build, read-only folder) only announces it; the user downloads the verified package to
/// Downloads.
/// </summary>
public sealed partial class MainViewModel
{
    public UpdateChecker? Updates { get; init; }

    /// <summary>Replaces this app in place; null when it cannot (<see cref="UpdateInstallUnavailable"/> says why).</summary>
    public UpdateInstaller? UpdateInstaller { get; init; }
    public string? UpdateInstallUnavailable { get; init; }

    /// <summary>Where a verified package is saved when it cannot be installed in place (the user's Downloads folder by default).</summary>
    public string UpdateDownloadFolder { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), "Downloads");

    /// <summary>First automatic check after start (the window and omp come first), and the time between two.</summary>
    public TimeSpan UpdateCheckDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan UpdateCheckInterval { get; init; } = TimeSpan.FromHours(24);

    private UpdateCheck? _updateCheck;
    private StagedUpdate? _stagedUpdate;
    private bool _updateApplied;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(DownloadUpdateCommand), nameof(RestartToUpdateCommand))]
    private bool _isUpdateBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadUpdateCommand))]
    [NotifyPropertyChangedFor(nameof(UpdatePill), nameof(ShowUpdatePill))]
    private bool _updateAvailable;

    /// <summary>A verified update is unpacked next to the app: it goes in at the next restart or quit.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestartToUpdateCommand), nameof(DownloadUpdateCommand), nameof(CheckForUpdatesCommand))]
    [NotifyPropertyChangedFor(nameof(UpdatePill), nameof(ShowUpdatePill))]
    private bool _updateReady;

    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private string _updateNotes = "";
    [ObservableProperty] private string? _downloadedUpdate;
    [ObservableProperty] private double _updatePercent;

    /// <summary>What the offered version does to omp, when it brings another one.</summary>
    [ObservableProperty] private string _updateOmpText = "";

    /// <summary>Set once after this version was installed by the updater ("Updated to X"), or when that failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatePill), nameof(ShowUpdatePill))]
    private string? _updateOutcome;

    [ObservableProperty] private bool _checkForUpdatesAutomatically = true;

    public string AppVersionText => Updates is { } u ? $"Ombra {u.Current} ({u.Rid})" : "Ombra";

    public bool CanInstallUpdates => UpdateInstaller is not null;

    public string UpdateActionLabel => UpdateInstaller is null ? Loc.T("Download and verify") : Loc.T("Download and install");

    public string UpdateHowText => UpdateInstaller is null
        ? Loc.F("This copy cannot replace itself ({0}): a new version is downloaded to your Downloads folder, checked against the release's signed list, and you replace the app yourself. Settings, sessions and omp stay where they are.", UpdateInstallUnavailable ?? Loc.T("not a package"))
        : Loc.T("A new version is downloaded, checked against the release's signed list and replaces this app when you restart or quit it — by itself when automatic updates are on, otherwise when you choose to. The version before stays next to the app until the next update. Settings, sessions and omp stay where they are.");

    /// <summary>The sidebar notice: an update to install or restart into, or how the last one went.</summary>
    public string UpdatePill => IsInstallingRuntime && _ompTransition is { } t ? Loc.F("Updating omp to {0}…", t)
        : UpdateReady ? Loc.F("Restart to update to {0}", _stagedUpdate?.Version)
        : UpdateAvailable ? Loc.F("Update available: {0}", _updateCheck?.Latest)
        : UpdateOutcome ?? "";

    public bool ShowUpdatePill => UpdatePill.Length > 0;

    partial void OnCheckForUpdatesAutomaticallyChanged(bool value)
    {
        if (IsSettingsOpen) Persist(o => o with { CheckForUpdates = value });
    }

    /// <summary>At window open: how the last update went, leftovers removed, then the check at start and once a day.</summary>
    private void StartUpdates()
    {
        if (UpdateInstaller is { } installer)
        {
            installer.RemoveLeftovers();
            if (UpdateInstaller.TakeLastResult(installer.StateDirectory) is { } last)
            {
                if (last.Ok && Updates is { } u && last.To == u.Current)
                {
                    UpdateOutcome = Loc.F("Updated to {0}", last.To);
                    UpdateText = Loc.F("Updated from {0} to {1}. The version before is kept in {2} until the next update.", last.From, last.To, last.Previous);
                }
                else if (!last.Ok)
                {
                    UpdateOutcome = Loc.T("Update not installed");
                    UpdateText = Loc.F("The update to {0} was not installed: {1}. Details: {2}", last.To, last.Error, Path.Combine(installer.StateDirectory, "apply.log"));
                }
            }
        }
        CheckForUpdatesAutomatically = LoadSettingsOrDefault().CheckForUpdates ?? true;
        if (Updates is not null) _ = AutoCheckLoopAsync(_cts.Token);
    }

    private async Task AutoCheckLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(UpdateCheckDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                if (CheckForUpdatesAutomatically && !IsUpdateBusy && !UpdateReady)
                {
                    await CheckAsync(quiet: true);
                    // Found one: get it ready for the next restart or quit, as a click on "Download and install" would.
                    if (UpdateAvailable && UpdateInstaller is not null && !ct.IsCancellationRequested) await DownloadUpdateAsync();
                }
                await Task.Delay(UpdateCheckInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Not while an update waits for the restart: it would only offer the same version again.</summary>
    private static string AutoCheckFailed => Loc.T("The automatic check at");

    private bool CanCheckForUpdates() => Updates is not null && !IsUpdateBusy && !UpdateReady;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdatesAsync() => CheckAsync(quiet: false);

    /// <param name="quiet">The automatic check: a failure is noted on the Updates page, never announced.</param>
    private async Task CheckAsync(bool quiet)
    {
        if (Updates is not { } updates || IsUpdateBusy) return;
        IsUpdateBusy = true;
        // What the page said before, without the note of an earlier failed automatic check (one note, not one a day).
        var had = string.Join('\n', UpdateText.Split('\n').Where(l => !l.StartsWith(AutoCheckFailed, StringComparison.Ordinal)));
        if (!quiet)
        {
            UpdateAvailable = false;
            DownloadedUpdate = null;
            UpdateText = Loc.T("Checking…");
        }
        try
        {
            var result = await updates.CheckAsync(_cts.Token);
            _updateCheck = result;
            UpdateAvailable = result.Available && !UpdateReady;
            UpdateNotes = result.Available ? result.Notes ?? "" : "";
            UpdateOmpText = result.Available && result.BringsNewOmp && result.Runtime is { } rt
                // A package that carries its engine (package.sh) brings the new one inside the update
                ? RuntimeInstaller?.FindBundled() is not null
                    ? Loc.F("This version moves omp to {0} (Bun {1}), which comes with the update: nothing more is downloaded. Until the restart omp {2} keeps working.", rt.Omp, rt.Bun, Services.Runtime.RuntimePack.OmpVersion)
                    : Loc.F("This version moves omp to {0} (Bun {1}). After the restart it installs by itself (about 550 MB, verified like the first install); until then omp {2} keeps working.", rt.Omp, rt.Bun, Services.Runtime.RuntimePack.OmpVersion)
                : "";
            // A quiet check that finds nothing keeps what the page said (for example "Updated to …").
            if (!quiet || result.Available) UpdateText = result.Message;
            OnPropertyChanged(nameof(UpdatePill));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or System.Text.Json.JsonException or TaskCanceledException or TimeoutException)
        {
            UpdateText = quiet
                ? (had.Length > 0 ? had + "\n" : "") + $"{AutoCheckFailed} {DateTime.Now:t} " + Loc.F("failed: {0}", e.Message)
                : Loc.F("Could not check for updates: {0}", e.Message);
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    private bool CanDownloadUpdate() => UpdateAvailable && !UpdateReady && !IsUpdateBusy;

    /// <summary>Downloads and verifies the offered version; installs it in place when this copy can.</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadUpdate))]
    private async Task DownloadUpdateAsync()
    {
        if (Updates is not { } updates || _updateCheck is not { Asset: { } asset, Latest: { } version }) return;
        IsUpdateBusy = true;
        UpdatePercent = 0;
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            UpdatePercent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
            UpdateText = string.Format(CultureInfo.InvariantCulture, Loc.T("Downloading {0}: {1:0} / {2:0} MB"), asset.File, p.Done / 1_000_000.0, p.Total / 1_000_000.0);
        });
        try
        {
            if (UpdateInstaller is { } installer)
            {
                var package = await updates.DownloadAsync(asset, installer.StateDirectory, progress, _cts.Token);
                UpdateText = Loc.F("Verified. Unpacking {0} next to the app…", version);
                _stagedUpdate = await installer.StageAsync(package, version, _cts.Token);
                UpdateAvailable = false;
                UpdateReady = true;
                UpdateText = Loc.F("Version {0} is ready. It replaces this version when you restart Ombra, or when you quit it.", version);
            }
            else
            {
                DownloadedUpdate = await updates.DownloadAsync(asset, UpdateDownloadFolder, progress, _cts.Token);
                UpdateText = Loc.F("Downloaded and verified (SHA-256 matches the release): {0}", DownloadedUpdate) + "\n" +
                             Loc.T("Close Ombra and replace the app with this package. Your settings and sessions are kept.");
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException
                                   or TaskCanceledException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            UpdateText = UpdateInstaller is null ? Loc.F("Download failed, nothing was kept: {0}", e.Message) : Loc.F("The update was not installed, nothing was changed: {0}", e.Message);
        }
        finally
        {
            IsUpdateBusy = false;
            OnPropertyChanged(nameof(UpdatePill));
        }
    }

    /// <summary>Asks the window to close (the app quits; the update script then swaps the new version in).</summary>
    public event Action? QuitRequested;

    private bool CanRestartToUpdate() => UpdateReady && !IsUpdateBusy;

    [RelayCommand(CanExecute = nameof(CanRestartToUpdate))]
    private void RestartToUpdate()
    {
        if (WorkingSessions().Count > 0)
        {
            UpdateText = IsRunning ? Loc.T("Stop the current run first: restarting ends it.")
                : Loc.T("omp is working in another chat (marked in the sidebar): restarting ends its run, so stop it first.");
            return;
        }
        if (!ApplyUpdate(relaunch: true)) return;
        QuitRequested?.Invoke();
    }

    /// <summary>Hands the staged update to its script, which waits for this process to end; false when that failed.</summary>
    private bool ApplyUpdate(bool relaunch)
    {
        if (_updateApplied || UpdateInstaller is not { } installer || _stagedUpdate is not { } staged || Updates is not { } updates) return false;
        try
        {
            string[] args = _args.ConfigPath is { } config ? ["--config", config] : [];
            installer.Apply(staged, updates.Current, Environment.ProcessId, relaunch, args);
            _updateApplied = true;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UpdateText = Loc.F("The update could not be started: {0}", e.Message);
            return false;
        }
    }

    /// <summary>Quitting with an update ready installs it (no restart).</summary>
    private void ApplyUpdateOnQuit()
    {
        if (UpdateReady && !_updateApplied) ApplyUpdate(relaunch: false);
    }

    [RelayCommand]
    private void ShowDownloadedUpdate()
    {
        if (DownloadedUpdate is { } file && Path.GetDirectoryName(file) is { } dir)
            OpenLink(new Uri(dir));
    }

    /// <summary>The sidebar notice opens Settings → Updates (or omp's page while omp updates); a finished outcome is dismissed.</summary>
    [RelayCommand]
    private async Task OpenUpdatesAsync()
    {
        var page = IsInstallingRuntime && _ompTransition is not null ? "advanced" : "updates";
        if (!UpdateAvailable && !UpdateReady && _ompTransition is null) UpdateOutcome = null;
        SettingsCategory = page;
        if (!IsSettingsOpen) await OpenSettingsAsync();
    }
}
