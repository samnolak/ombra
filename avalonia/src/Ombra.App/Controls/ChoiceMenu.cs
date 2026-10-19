using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;

namespace Ombra.App.Controls;

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

    private static readonly AttachedProperty<bool> FocusHookedProperty =
        AvaloniaProperty.RegisterAttached<ChoiceMenu, Flyout, bool>("FocusHooked");
    static ChoiceMenu()
    {
        CloseOnChoiceProperty.Changed.AddClassHandler<Flyout>((flyout, e) =>
        {
            if (e.NewValue is true)
            {
                flyout.Opened += OnOpened;
                RestoreFocusOnClose(flyout);
            }
            else flyout.Opened -= OnOpened;
        });
    }

    public static bool GetCloseOnChoice(Flyout flyout) => flyout.GetValue(CloseOnChoiceProperty);
    public static void SetCloseOnChoice(Flyout flyout, bool value) => flyout.SetValue(CloseOnChoiceProperty, value);
    /// <summary>Menus return the keyboard to their opener, but never take it from an editor their action opened.</summary>
    public static void RestoreFocusOnClose(Flyout flyout)
    {
        if (flyout.GetValue(FocusHookedProperty)) return;
        flyout.SetValue(FocusHookedProperty, true);
        Control? previous = null;
        TopLevel? window = null;
        flyout.Opening += (_, _) =>
        {
            window = flyout.Target is { } target ? TopLevel.GetTopLevel(target) : null;
            previous = window?.FocusManager?.GetFocusedElement() as Control;
        };
        flyout.Closed += (_, _) =>
        {
            var owner = previous;
            var top = window;
            Dispatcher.UIThread.Post(() =>
            {
                if (top is null || top is Window { IsActive: false }) return;
                if (top.FocusManager?.GetFocusedElement() is Control { IsEffectivelyVisible: true } focused
                    && TopLevel.GetTopLevel(focused) == top) return;
                if (owner is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true, Focusable: true }
                    && TopLevel.GetTopLevel(owner) == top) owner.Focus(NavigationMethod.Unspecified);
            }, DispatcherPriority.Background);
        };
    }

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
