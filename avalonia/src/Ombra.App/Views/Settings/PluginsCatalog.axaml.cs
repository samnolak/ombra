using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Ombra.App.Views.Settings;

public partial class PluginsCatalog : UserControl
{
    private readonly HashSet<Flyout> _menus = [];

    public PluginsCatalog()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, (_, e) => ConnectorsSettingsMenus.CloseOnChoice(e.Source, _menus), RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
