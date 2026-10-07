using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace OmpGui.App.Controls;

/// <summary>
/// <c>ctl:FocusOnShow.When="{Binding IsWritingFeedback}"</c>: the control takes the keyboard focus when the bound value
/// turns true (a box that opens to be typed in), once it has been laid out and is visible.
/// </summary>
public static class FocusOnShow
{
    public static readonly AttachedProperty<bool> WhenProperty =
        AvaloniaProperty.RegisterAttached<InputElement, bool>("When", typeof(FocusOnShow));

    static FocusOnShow()
    {
        WhenProperty.Changed.AddClassHandler<InputElement>((element, e) =>
        {
            if (e.NewValue is not true) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (GetWhen(element) && element.IsEffectivelyVisible) element.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Loaded);
        });
    }

    public static bool GetWhen(InputElement element) => element.GetValue(WhenProperty);
    public static void SetWhen(InputElement element, bool value) => element.SetValue(WhenProperty, value);
}
