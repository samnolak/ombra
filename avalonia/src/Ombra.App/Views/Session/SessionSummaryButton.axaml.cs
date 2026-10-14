using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ombra.App.ViewModels;

namespace Ombra.App.Views.Session;

/// <summary>The header's summary toggle and its popover: open, it follows the chat; a choice closes it.</summary>
public partial class SessionSummaryButton : UserControl
{
    public SessionSummaryButton()
    {
        InitializeComponent();
        if (SummaryButton.Flyout is not Flyout flyout) return;
        flyout.Opened += (_, _) => { if (DataContext is MainViewModel vm) vm.IsSummaryOpen = true; };
        flyout.Closed += (_, _) => { if (DataContext is MainViewModel vm) vm.IsSummaryOpen = false; };
        SummaryList.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Classes: var c } && c.Contains("menu-item"))
                Dispatcher.UIThread.Post(flyout.Hide, DispatcherPriority.Background);
        }, RoutingStrategies.Bubble);
    }

    /// <summary>The button that opens the popover (tests).</summary>
    public Button Button => SummaryButton;
}
