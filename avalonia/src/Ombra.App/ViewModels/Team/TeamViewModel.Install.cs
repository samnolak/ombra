using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore.Teams;

namespace Ombra.App.ViewModels;

/// <summary>Installs the team server (<see cref="TeamServerInstaller.InstallAsync"/>); tests pass a fake.</summary>
public delegate Task<ServerInstallResult> ServerInstall(ServerInstallRequest request, IProgress<ServerInstallProgress>? progress, CancellationToken ct);

/// <summary>A team in the header's switcher: the owner's name and the end of its ID (two teams of one owner differ).</summary>
public sealed record TeamChoice(string TeamId, string Label)
{
    internal static TeamChoice Of(TeamView t)
    {
        var owner = t.Roster.Members.FirstOrDefault(m => m.MemberId == t.Roster.Owner)?.Name;
        var name = owner is { Length: > 0 } ? Loc.F("Team of {0}", owner) : Loc.T("Team");
        return new TeamChoice(t.TeamId, name + " · " + t.TeamId[^Math.Min(6, t.TeamId.Length)..]);
    }

    public override string ToString() => Label;
}

public enum InstallStepState { Pending, Current, Done, Failed }

/// <summary>One line of the install's step list.</summary>
public sealed partial class InstallStepRow(string title) : ObservableObject
{
    public string Title { get; } = title;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPending), nameof(IsCurrent), nameof(IsDone), nameof(IsFailed))]
    private InstallStepState _state;
    public bool IsPending => State == InstallStepState.Pending;
    public bool IsCurrent => State == InstallStepState.Current;
    public bool IsDone => State == InstallStepState.Done;
    public bool IsFailed => State == InstallStepState.Failed;
}

/// <summary>
/// Create a team: Ombra installs the team server over SSH (the installer reads the TLS pin and the one-time bootstrap
/// code from install.sh's output), signs in to it and creates the team with this device as its owner.
/// </summary>
public sealed partial class TeamViewModel
{
    private const int LogLines = 200;
    private CancellationTokenSource? _installCts;
    /// <summary>The installer (the constructor's, else <see cref="TeamServerInstaller"/>); tests swap in a fake.</summary>
    internal ServerInstall Installer { get; set; }
    /// <summary>A server installed whose team creation failed: Retry only creates the team (a second install would find it).</summary>
    private ServerInstallResult? _installed;
    private readonly Queue<string> _log = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDomainAddress))] private string _installAddress = "";
    [ObservableProperty] private string _sshUser = "root";
    [ObservableProperty] private string _sshKeyPath = "";
    [ObservableProperty] private string _sshPort = "22";
    [ObservableProperty] private string _acmeEmail = "";

    /// <summary>The address is a domain (not an IP): the email for its Let's Encrypt certificate applies.</summary>
    public bool IsDomainAddress => InstallAddress.Trim() is { Length: > 0 } a && !IPAddress.TryParse(a.Trim('[', ']'), out _) && a.Any(char.IsLetter);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowInstallForm))] private bool _isInstalling;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInstallFailed), nameof(ShowInstallForm))] private string? _installError;
    public bool HasInstallFailed => InstallError is not null;
    public bool ShowInstallForm => !IsInstalling && !HasInstallFailed;
    public bool ShowInstallSteps => IsInstalling || HasInstallFailed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInstallLog))] private string _installLog = "";
    public bool HasInstallLog => InstallLog.Length > 0;

    /// <summary>"My server is already installed": the manual Server / Certificate pin / Bootstrap code fields.</summary>
    [ObservableProperty] private bool _isManualSetupOpen;
    /// <summary>"I'm already in a team on another computer": the steps and the pairing box.</summary>
    [ObservableProperty] private bool _isPairDisclosureOpen;

    public ObservableCollection<InstallStepRow> InstallSteps { get; } =
    [
        new(Loc.T("Connecting")),
        new(Loc.T("Preparing the server")),
        new(Loc.T("Downloading")),
        new(Loc.T("Verifying")),
        new(Loc.T("Installing")),
        new(Loc.T("Creating the team")),
    ];

    /// <summary>The view's file picker for "Choose…" next to the SSH key (set by TeamPage).</summary>
    public Func<Task<string?>>? PickSshKeyFile { get; set; }

    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(ShowInstallSteps));
    partial void OnInstallErrorChanged(string? value) => OnPropertyChanged(nameof(ShowInstallSteps));
    partial void OnNewDeviceWordsChanged(string? value) { if (value is not null) IsPairDisclosureOpen = true; }

    /// <summary>The first of ~/.ssh/id_ed25519, id_ecdsa, id_rsa that exists.</summary>
    internal static string? DefaultSshKey(string home) =>
        new[] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select(n => Path.Combine(home, ".ssh", n)).FirstOrDefault(File.Exists);

    [RelayCommand]
    private async Task BrowseSshKey()
    {
        if (PickSshKeyFile is { } pick && await pick() is { Length: > 0 } path) SshKeyPath = path;
    }

    [RelayCommand] private void ToggleManualSetup() => IsManualSetupOpen = !IsManualSetupOpen;
    [RelayCommand] private void TogglePairDisclosure() => IsPairDisclosureOpen = !IsPairDisclosureOpen;

    [RelayCommand]
    private async Task InstallServer()
    {
        if (IsBusy) return;
        if (!int.TryParse(SshPort.Trim(), out var port)) { InstallError = Loc.T("The SSH port must be a number."); return; }
        var request = new ServerInstallRequest(InstallAddress.Trim(), SshUser.Trim(), port, SshKeyPath.Trim(), IsDomainAddress ? NullIfBlank(AcmeEmail) : null);
        IsBusy = true;
        Error = null;
        InstallError = null;
        IsInstalling = true;
        using var cts = _installCts = new CancellationTokenSource();
        var step = 0;
        try
        {
            if (_installed is null)
            {
                _log.Clear();
                InstallLog = "";
                MarkStep(0);
                var progress = new Progress<ServerInstallProgress>(p =>
                {
                    if (cts.IsCancellationRequested || !IsInstalling || _installed is not null) return;
                    if (p.Step != ServerInstallStep.Done) MarkStep(step = (int)p.Step);
                    if (p.Line is { } line) AppendLog(line);
                });
                _installed = await Installer(request, progress, cts.Token);
            }
            MarkStep(step = InstallSteps.Count - 1);
            var r = _installed;
            await CreateOnServerAsync(r.Server, r.Pin, r.BootstrapCode);
            _installed = null;
            foreach (var s in InstallSteps) s.State = InstallStepState.Done;
            IsInstalling = false;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            IsInstalling = false;
            foreach (var s in InstallSteps) s.State = InstallStepState.Pending;
        }
        catch (Exception e)
        {
            InstallSteps[step].State = InstallStepState.Failed;
            if (e is ServerInstallException { Log: { Length: > 0 } log }) { _log.Clear(); AppendLog(log); }
            IsInstalling = false;
            if (e is ServerAlreadyInstalledException)
            {
                // A server runs there already: the manual fields take its pin and a bootstrap code.
                EditInstall();
                ServerAddress = request.Address;
                IsManualSetupOpen = true;
                Error = Loc.T("An Ombra server is already installed there. Enter its certificate pin and a bootstrap code under “My server is already installed”.");
                return;
            }
            InstallError = e.Message;
        }
        finally
        {
            _installCts = null;
            IsBusy = false;
        }
    }

    [RelayCommand] private void CancelInstall() => _installCts?.Cancel();

    [RelayCommand] private Task RetryInstall() => InstallServer();

    /// <summary>Back to the form after a failure (the address and key stay filled in).</summary>
    [RelayCommand]
    private void EditInstall()
    {
        InstallError = null;
        _installed = null;
        foreach (var s in InstallSteps) s.State = InstallStepState.Pending;
    }

    private void MarkStep(int current)
    {
        for (var i = 0; i < InstallSteps.Count; i++)
            InstallSteps[i].State = i < current ? InstallStepState.Done : i == current ? InstallStepState.Current : InstallStepState.Pending;
    }

    private void AppendLog(string text)
    {
        foreach (var line in text.Split('\n')) _log.Enqueue(line);
        while (_log.Count > LogLines) _log.Dequeue();
        var sb = new StringBuilder();
        foreach (var l in _log) sb.Append(l).Append('\n');
        InstallLog = sb.ToString(0, Math.Max(0, sb.Length - 1));
    }
}
