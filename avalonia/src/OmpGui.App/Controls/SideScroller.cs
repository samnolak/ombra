using Avalonia.Controls;
using Avalonia.Input;

namespace OmpGui.App.Controls;

/// <summary>
/// A horizontal-only scroller for wide code, diffs and tables inside the conversation. A vertical wheel goes on to the
/// conversation (a plain ScrollViewer took it, so the conversation stopped scrolling under the pointer); Shift+wheel
/// and horizontal wheels scroll it sideways.
/// </summary>
public sealed class SideScroller : ScrollViewer
{
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    public SideScroller()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Room under the content for the scrollbar while there is something to scroll to: it is drawn over the content,
        // and had sat on the last line of code, the table's last row and the diff's last line.
        if (change.Property == ExtentProperty || change.Property == ViewportProperty)
        {
            var bottom = Extent.Width > Viewport.Width + 0.5 ? 10 : 0;
            if (Padding.Bottom != bottom) Padding = new Avalonia.Thickness(Padding.Left, Padding.Top, Padding.Right, bottom);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var sideways = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (sideways) base.OnPointerWheelChanged(e);
        // Otherwise not handled: it bubbles to the conversation's scroller
    }
}
