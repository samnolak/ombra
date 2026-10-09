using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace OmpGui.App.Controls;

/// <summary>
/// A row of tabs that share the strip's width, as browsers do: while they all fit each keeps its own width; when they
/// do not, the widest shrink alike (their titles trimmed, their close buttons kept) down to <see cref="MinItemWidth"/>,
/// and only past that the row is wider than the strip and its <see cref="ScrollViewer"/> scrolls. Inside a ScrollViewer
/// the row is measured with unlimited width, so the room is the ScrollViewer's viewport.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<TabStripPanel, double>(nameof(MinItemWidth), 120);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<TabStripPanel, double>(nameof(Spacing), 4);

    static TabStripPanel() => AffectsMeasure<TabStripPanel>(MinItemWidthProperty, SpacingProperty);

    /// <summary>How narrow a tab may get before the row scrolls instead (a tab already narrower keeps its width).</summary>
    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private ScrollViewer? _scroller;
    private double[] _widths = [];

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null) _scroller.PropertyChanged += OnScrollerPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroller is not null) _scroller.PropertyChanged -= OnScrollerPropertyChanged;
        _scroller = null;
    }

    // The strip got wider or narrower: the tabs share the new width
    private void OnScrollerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ViewportProperty
            && Math.Abs(((Size)e.OldValue!).Width - ((Size)e.NewValue!).Width) > 0.5) InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        var n = children.Count;
        if (_widths.Length != n) _widths = new double[n];
        if (n == 0) return default;
        var spacing = Spacing * (n - 1);
        var room = double.IsFinite(availableSize.Width) ? availableSize.Width
            : _scroller is { Viewport.Width: > 0 } s ? s.Viewport.Width : double.PositiveInfinity;
        var unlimited = new Size(double.PositiveInfinity, availableSize.Height);
        for (var i = 0; i < n; i++)
        {
            children[i].Measure(unlimited);
            _widths[i] = children[i].DesiredSize.Width;
        }
        var cap = WidthCap(_widths, room - spacing, MinItemWidth);
        double width = spacing, height = 0;
        for (var i = 0; i < n; i++)
        {
            if (_widths[i] > cap)
            {
                _widths[i] = cap;
                children[i].Measure(new Size(cap, availableSize.Height));
            }
            width += _widths[i];
            height = Math.Max(height, children[i].DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        var children = Children;
        for (var i = 0; i < children.Count && i < _widths.Length; i++)
        {
            children[i].Arrange(new Rect(x, 0, _widths[i], finalSize.Height));
            x += _widths[i] + Spacing;
        }
        return finalSize;
    }

    /// <summary>
    /// The widest a tab may be so that all fit in <paramref name="room"/>: unlimited when they fit as they are; else the
    /// width that, given to every tab wider than it, fills the room exactly, but never below <paramref name="min"/>.
    /// </summary>
    internal static double WidthCap(IReadOnlyList<double> widths, double room, double min)
    {
        if (!double.IsFinite(room) || widths.Sum() <= room) return double.PositiveInfinity;
        var sorted = widths.Order().ToArray();
        var left = room;
        for (var i = 0; i < sorted.Length; i++)
        {
            var share = left / (sorted.Length - i);
            if (sorted[i] > share) return Math.Max(share, min);
            left -= sorted[i];
        }
        return Math.Max(left, min); // not reached: the sum is over the room
    }
}
