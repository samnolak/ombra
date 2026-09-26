using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OmpGui.App.Controls;

/// <summary>
/// <c>ctl:ChoiceMenu.CloseOnChoice="True"</c> on a <see cref="Flyout"/> of <c>Button.menu-item</c>s: the menu closes
/// once one is clicked (a click inside a popup does not reach the page around it, and a menu in a data template has no
/// code-behind to do it).
/// </summary>
public sealed class ChoiceMenu
{
    public static readonly AttachedProperty<bool> CloseOnChoiceProperty =
        AvaloniaProperty.RegisterAttached<ChoiceMenu, Flyout, bool>("CloseOnChoice");

    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<ChoiceMenu, Control, bool>("Hooked");

    static ChoiceMenu()
    {
        CloseOnChoiceProperty.Changed.AddClassHandler<Flyout>((flyout, e) =>
        {
            if (e.NewValue is true) flyout.Opened += OnOpened;
            else flyout.Opened -= OnOpened;
        });
    }

    public static bool GetCloseOnChoice(Flyout flyout) => flyout.GetValue(CloseOnChoiceProperty);
    public static void SetCloseOnChoice(Flyout flyout, bool value) => flyout.SetValue(CloseOnChoiceProperty, value);

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is not Flyout { Content: Control content } flyout || content.GetValue(HookedProperty)) return;
        content.SetValue(HookedProperty, true);
        content.AddHandler(Button.ClickEvent, (_, args) =>
        {
            if (args.Source is Button { Classes: var c } && c.Contains("menu-item"))
                Dispatcher.UIThread.Post(flyout.Hide, DispatcherPriority.Background);
        }, RoutingStrategies.Bubble);
    }
}
