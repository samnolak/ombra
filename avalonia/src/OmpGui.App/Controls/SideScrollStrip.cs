using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace OmpGui.App.Controls;

/// <summary>
/// A one-row strip that scrolls sideways without a scroll bar (its <see cref="ScrollViewer"/> has
/// <c>HorizontalScrollBarVisibility="Hidden"</c>: Fluent's bar would lie over the row): a mouse wheel scrolls it, and an
/// edge with more items past it fades, which says the strip scrolls. Used by the narrow settings page list and the
/// preview's tabs.
/// </summary>
public static class SideScrollStrip
{
    public static void Attach(ScrollViewer strip)
    {
        strip.ScrollChanged += (_, _) => UpdateFade(strip);
        strip.SizeChanged += (_, _) => UpdateFade(strip);
        // A mouse wheel scrolls the strip sideways (it has no vertical scrolling); a trackpad's sideways swipe already does
        strip.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Delta.X != 0 || e.Delta.Y == 0) return;
            var max = Math.Max(0, strip.Extent.Width - strip.Viewport.Width);
            if (max <= 0) return;
            strip.Offset = new Vector(Math.Clamp(strip.Offset.X - e.Delta.Y * 48, 0, max), 0);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private static void UpdateFade(ScrollViewer strip)
    {
        var max = strip.Extent.Width - strip.Viewport.Width;
        var left = strip.Offset.X > 0.5;
        var right = strip.Offset.X < max - 0.5;
        if (!left && !right || strip.Bounds.Width <= 0)
        {
            strip.OpacityMask = null;
            return;
        }
        var fade = Math.Min(0.4, 40 / strip.Bounds.Width);
        strip.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(left ? Colors.Transparent : Colors.Black, 0),
                new GradientStop(Colors.Black, fade),
                new GradientStop(Colors.Black, 1 - fade),
                new GradientStop(right ? Colors.Transparent : Colors.Black, 1),
            },
        };
    }
}
