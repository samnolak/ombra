using Avalonia.Controls;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views.Settings;

public partial class SshSettings : UserControl
{
    private SshSettingsViewModel? _page;

    public SshSettings()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm || vm.SshSettings == _page) return;
            _page = vm.SshSettings;
            WorkspaceReveal.On(_page, nameof(_page.Message), () => _page.HasMessage, MessageBar);
            WorkspaceReveal.On(_page, nameof(_page.IsAdding), () => _page.IsAdding, AddHostForm, SshNameBox);
        };
    }
}
