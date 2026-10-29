using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore.Teams;

namespace Ombra.App.ViewModels;

/// <summary>
/// The rail's Team: a full-size page over the conversation's place (Views/Team/TeamPage), and Settings › Account. The
/// signed-in team account (<see cref="TeamViewModel.Session"/>) opens on first use and refreshes in the background.
/// The teams root is machine-local (<see cref="TeamsDevice.DefaultRoot"/>; <c>OMBRA_TEAMS_DIR</c> overrides it).
/// </summary>
public partial class MainViewModel
{
    public TeamViewModel Team => field ??= NewTeam();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTeamPage))]
    private bool _isTeamPageOpen;

    public bool ShowTeamPage => IsTeamPageOpen && !IsSettingsOpen;

    public static string TeamRailTip { get; } = Loc.T("Team");

    partial void OnIsTeamPageOpenChanged(bool value) => OnRailChanged();

    private TeamViewModel NewTeam()
    {
        var root = Environment.GetEnvironmentVariable("OMBRA_TEAMS_DIR") is { Length: > 0 } d ? d : TeamsDevice.DefaultRoot;
        var t = new TeamViewModel(root, new LazyKeyStore(), InstallId.ForCurrentPlatform(), text => CopyTextAsync(text, Loc.T("Copied")));
        Dispatcher.UIThread.Post(t.Load);
        return t;
    }

    /// <summary>The rail's Team: settings and the Browser page close, the Team page shows.</summary>
    [RelayCommand]
    private void OpenTeamPage()
    {
        if (IsSettingsOpen) CloseSettingsCommand.Execute(null);
        IsBrowserPageOpen = false;
        IsEditorPageOpen = false;
        IsTeamPageOpen = true;
    }

    /// <summary>The OS key store, resolved on first use (an unsupported platform fails only when teams are used).</summary>
    private sealed class LazyKeyStore : ITeamsKeyStore
    {
        private ITeamsKeyStore? _store;
        private ITeamsKeyStore Store => _store ??= TeamsKeyStore.ForCurrentPlatform();
        public byte[]? Read(string account) => Store.Read(account);
        public void Write(string account, ReadOnlySpan<byte> secret) => Store.Write(account, secret);
        public void Delete(string account) => Store.Delete(account);
    }
}
