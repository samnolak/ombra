using Avalonia.Controls;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Settings;

public partial class CustomProvidersSettings : UserControl
{
    private CustomProvidersViewModel? _page;

    public CustomProvidersSettings()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm || vm.CustomProviders == _page) return;
            _page = vm.CustomProviders;
            WorkspaceReveal.On(_page, nameof(_page.Message), () => _page.HasMessage, CustomProvidersMessageBar);
            WorkspaceReveal.On(_page, nameof(_page.IsFormOpen), () => _page.IsFormOpen, CustomProviderForm, CustomProviderNameBox);
        };
    }
}
