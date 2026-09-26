using Avalonia.Controls;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views.Settings;

public partial class GitSettings : UserControl
{
    private GitSettingsViewModel? _page;

    public GitSettings()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm || vm.GitSettings == _page) return;
            _page = vm.GitSettings;
            WorkspaceReveal.On(_page, nameof(_page.Message), () => _page.HasMessage, MessageBar);
            WorkspaceReveal.On(_page, nameof(_page.IsCreating), () => _page.IsCreating, NewWorktreeForm, NewWorktreeBranch);
        };
    }
}
