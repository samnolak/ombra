using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Session;

/// <summary>The header's ⋯ and its menu: opening it reads the count of background jobs again; a choice closes it.</summary>
public partial class SessionMenuButton : UserControl
{
    public SessionMenuButton()
    {
        InitializeComponent();
        if (MenuButton.Flyout is not Flyout flyout) return;
        flyout.Opened += (_, _) => (DataContext as MainViewModel)?.RefreshRunningTasksCommand.Execute(null);
        ChatMenu.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Classes: var c } && c.Contains("menu-item"))
                Dispatcher.UIThread.Post(flyout.Hide, DispatcherPriority.Background);
        }, RoutingStrategies.Bubble);
    }

    /// <summary>The button that opens the menu (tests and the layout audit open it).</summary>
    public Button Button => MenuButton;
}
