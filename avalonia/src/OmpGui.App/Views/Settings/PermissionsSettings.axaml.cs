using Avalonia.Controls;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views.Settings;

public partial class PermissionsSettings : UserControl
{
    private PermissionRulesViewModel? _page;

    public PermissionsSettings()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm || vm.Permissions == _page) return;
            _page = vm.Permissions;
            WorkspaceReveal.On(_page, nameof(_page.Message), () => _page.HasMessage, PermissionsMessageBar);
        };
    }
}
