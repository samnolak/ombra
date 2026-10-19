using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>
/// Which engine shows the pane's pages: Nightly when the user chose it (<see cref="WantsNightly"/>; the macOS default
/// after the first-run consent card) and its pinned runtime can run, else the built-in preview with the reason on a
/// banner (<see cref="NightlyFallbackReason"/>) and, when installing helps, an Install button.
/// </summary>
public sealed partial class PreviewViewModel
{
    /// <summary>The user's choice (Settings › General › Agent browser); <see cref="UseNightly"/> follows it while Nightly can run.</summary>
    [ObservableProperty]
    private bool _wantsNightly;

    /// <summary>The engine was never chosen: the pane asks once whether pages may move to Nightly.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNightlyFallback))]
    private bool _needsNightlyConsent;

    /// <summary>Why the built-in preview shows pages although Nightly was chosen; null when it does not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNightlyFallback))]
    private string? _nightlyFallbackReason;

    /// <summary>What the process said when it stopped (an exit code, a timeout), under "Show details"; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNightlyFallbackDetail))]
    private string? _nightlyFallbackDetail;

    public bool HasNightlyFallbackDetail => !string.IsNullOrEmpty(NightlyFallbackDetail);

    /// <summary>Installing the pinned runtime package would make Nightly usable.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallNightlyCommand))]
    private bool _canInstallNightly;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallNightlyCommand), nameof(RetryNightlyCommand))]
    private bool _isInstallingNightly;

    /// <summary>The install's current step, or its failure.</summary>
    [ObservableProperty]
    private string? _nightlyInstallStatus;

    public bool ShowNightlyFallback => NightlyFallbackReason is not null && !NeedsNightlyConsent;

    /// <summary>The user answered the consent card (true: Nightly); the app saves it as the engine choice.</summary>
    public Action<bool>? NightlyConsentChosen { get; set; }

    /// <summary>Whether Nightly can run now, else why and whether installing helps (the app: the pinned package; tests replace it).</summary>
    public Func<(bool Ready, string? Reason, bool CanInstall)> CheckNightly { get; set; } = DefaultCheck;

    /// <summary>Installs the pinned runtime (progress: steps to show).</summary>
    public Func<IProgress<string>, CancellationToken, Task> InstallNightlyRuntime { get; set; } = DefaultInstallAsync;

    partial void OnWantsNightlyChanged(bool value) => RefreshNightly();

    /// <summary>Uses Nightly when it is wanted and can run; otherwise the preview, saying why.</summary>
    public void RefreshNightly()
    {
        if (!WantsNightly)
        {
            NightlyFallbackReason = null;
            NightlyFallbackDetail = null;
            CanInstallNightly = false;
            UseNightly = false;
            return;
        }
        var (ready, reason, canInstall) = CheckNightly();
        NightlyFallbackReason = ready ? null : reason ?? Loc.T("Nightly cannot run.");
        NightlyFallbackDetail = null;
        CanInstallNightly = !ready && canInstall;
        UseNightly = ready;
    }

    /// <summary>Nightly was used but cannot show pages (it could not start, or kept crashing): the preview takes over.</summary>
    public void ReportNightlyUnusable(string reason)
    {
        if (!UseNightly) return;
        NightlyFallbackReason = Loc.T("Nightly is not responding.");
        NightlyFallbackDetail = reason;
        CanInstallNightly = false;
        UseNightly = false;
    }

    [RelayCommand]
    private void AcceptNightly()
    {
        NeedsNightlyConsent = false;
        NightlyConsentChosen?.Invoke(true);
    }

    [RelayCommand]
    private void DeclineNightly()
    {
        NeedsNightlyConsent = false;
        NightlyConsentChosen?.Invoke(false);
    }

    private bool CanInstall() => CanInstallNightly && !IsInstallingNightly;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallNightlyAsync()
    {
        IsInstallingNightly = true;
        NightlyInstallStatus = Loc.T("Installing Nightly…");
        try
        {
            await InstallNightlyRuntime(new Progress<string>(s => NightlyInstallStatus = s), CancellationToken.None);
            NightlyInstallStatus = null;
            RefreshNightly();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException)
        {
            NightlyInstallStatus = Loc.F("Nightly could not be installed: {0}", e.Message);
        }
        finally
        {
            IsInstallingNightly = false;
        }
    }

    private bool CanRetry() => !IsInstallingNightly;

    /// <summary>After a failure: Nightly is checked (and started) again.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void RetryNightly()
    {
        if (OperatingSystem.IsMacOS()) Nightly.NightlyHost.RetryFailed();
        RefreshNightly();
    }

    private static (bool, string?, bool) DefaultCheck()
    {
        if (!OperatingSystem.IsMacOS()) return (false, Loc.T("Nightly runs on macOS only."), false);
        var settings = Program.SettingsPath.Length > 0 ? Program.SettingsPath : Ombra.ClientCore.ClientInstanceLease.SettingsPath();
        var (options, problem) = Nightly.NightlyRuntimeOptions.Resolve(settings, out var missing);
        return (options is not null, problem,
            missing is Nightly.NightlyRuntimePackage.Problem.NotInstalled or Nightly.NightlyRuntimePackage.Problem.UpdateRequired);
    }

    private static async Task DefaultInstallAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new IOException(Loc.T("Nightly runs on macOS only."));
        await Task.Run(() => Nightly.NightlyRuntimePackage.InstallAsync(progress, app => OperatingSystem.IsMacOS() && Nightly.NightlyHost.Uses(app), ct), ct);
    }
}
