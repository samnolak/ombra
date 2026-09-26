using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OmpGui.App.Views.Session;

/// <summary>The title button and its session menu; a choice closes the menu.</summary>
public partial class SessionTitle : UserControl
{
    public SessionTitle()
    {
        InitializeComponent();
        SessionMenu.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Classes: var c } && c.Contains("menu-item"))
                Dispatcher.UIThread.Post(() => TitleButton.Flyout?.Hide(), DispatcherPriority.Background);
        }, RoutingStrategies.Bubble);
    }

    /// <summary>The button that opens the menu (tests and the layout audit open it).</summary>
    public Button Button => TitleButton;
}
