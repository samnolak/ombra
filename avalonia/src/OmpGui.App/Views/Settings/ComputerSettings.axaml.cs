using Avalonia.Controls;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views.Settings;

public partial class ComputerSettings : UserControl
{
    private ComputerSettingsViewModel? _page;

    public ComputerSettings()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm || vm.ComputerSettings == _page) return;
            _page = vm.ComputerSettings;
            WorkspaceReveal.On(_page, nameof(_page.Message), () => _page.HasMessage, MessageBar);
        };
    }
}
