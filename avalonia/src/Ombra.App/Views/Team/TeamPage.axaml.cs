using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Ombra.App.Localization;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Team;

public partial class TeamPage : UserControl
{
    private TeamViewModel? _team;

    public TeamPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm || vm.Team == _team) return;
            if (_team is not null) _team.PickSshKeyFile = null;
            _team = vm.Team;
            _team.PickSshKeyFile = PickSshKeyAsync;
        };
    }

    /// <summary>"Choose…" next to the SSH key: the system's picker, starting in ~/.ssh, a local file only.</summary>
    private async Task<string?> PickSshKeyAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return null;
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Choose your SSH private key"),
            AllowMultiple = false,
            SuggestedStartLocation = Directory.Exists(ssh) ? await storage.TryGetFolderFromPathAsync(ssh) : null,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
