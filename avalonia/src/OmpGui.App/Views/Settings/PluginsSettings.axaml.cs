using Avalonia.Controls;
using Avalonia.Interactivity;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views.Settings;

public partial class PluginsSettings : UserControl
{
    private readonly HashSet<Flyout> _menus = [];

    public PluginsSettings()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, (_, e) => ConnectorsSettingsMenus.CloseOnChoice(e.Source, _menus), RoutingStrategies.Bubble, handledEventsToo: true);
        // The page's model exists with the window, so it hears the page being shown the first time.
        DataContextChanged += (_, _) => _ = (DataContext as MainViewModel)?.Plugins;
    }
}
