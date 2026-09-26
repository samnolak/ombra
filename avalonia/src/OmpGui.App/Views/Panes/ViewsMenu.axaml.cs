using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OmpGui.App.ViewModels;

namespace OmpGui.App.Views.Panes;

/// <summary>The ⋮ Views button and its menu; the menu closes once a view is chosen.</summary>
public partial class ViewsMenu : UserControl
{
    public ViewsMenu()
    {
        InitializeComponent();
        if (ViewsButton.Flyout is not Flyout { Content: Control list } flyout) return;
        flyout.Opened += (_, _) => (DataContext as MainViewModel)?.ViewsMenuOpenedCommand.Execute(null);
        list.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Classes: var c } && c.Contains("menu-item"))
                Dispatcher.UIThread.Post(flyout.Hide, DispatcherPriority.Background);
        });
    }

    /// <summary>The ⋮ button (tests, the window's keyboard handling).</summary>
    public Button Button => ViewsButton;
}
