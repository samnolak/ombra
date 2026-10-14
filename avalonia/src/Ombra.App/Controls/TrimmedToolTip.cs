using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Ombra.App.Controls;

/// <summary>
/// A tooltip that only says what its control does not: it does not open while its text is shown whole by a text block
/// inside the control (a tool line whose argument fits). Cut with an ellipsis, or not on the line at all, it opens.
/// </summary>
public static class TrimmedToolTip
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(TrimmedToolTip));

    public static bool GetIsEnabled(Control c) => c.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control c, bool value) => c.SetValue(IsEnabledProperty, value);

    static TrimmedToolTip()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.RemoveHandler(ToolTip.ToolTipOpeningEvent, OnOpening);
            if (e.NewValue is true) c.AddHandler(ToolTip.ToolTipOpeningEvent, OnOpening);
        });
    }

    private static void OnOpening(object? sender, CancelRoutedEventArgs e)
    {
        if (sender is Control c && Repeats(c)) e.Cancel = true;
    }

    /// <summary>The control's tooltip is text that one of its visible text blocks shows without cutting it.</summary>
    internal static bool Repeats(Control c) =>
        ToolTip.GetTip(c) is string tip
        && c.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == tip && !IsTrimmed(t));

    /// <summary>The text block cuts its text (an ellipsis, or fewer lines than it has).</summary>
    internal static bool IsTrimmed(TextBlock t) => t.TextLayout.TextLines.Any(l => l.HasCollapsed);
}
