using Avalonia;
using Avalonia.Controls;

namespace Ombra.App.Controls;

/// <summary>
/// The tab strip's row, as browsers lay it out: the tabs (the first child, a ScrollViewer around a
/// <see cref="TabStripPanel"/>), then the rest (the + button) right after the last tab. The tabs take only the width
/// they need, at most what the rest leaves (<see cref="TabsRoom"/>, which their panel shares out), so + follows them
/// while they fit and stays in sight at the end once they shrink or scroll.
/// </summary>
public sealed class TabStripRow : Panel
{
    /// <summary>The width the tabs may take: the row's, less the rest.</summary>
    public double TabsRoom { get; private set; } = double.PositiveInfinity;

    /// <summary>The tabs' panel inside the first child (it registers itself): told when <see cref="TabsRoom"/> changes,
    /// since the ScrollViewer around it measures it with unlimited width either way.</summary>
    internal TabStripPanel? Tabs { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        if (children.Count == 0) return default;
        double rest = 0, height = 0;
        for (var i = 1; i < children.Count; i++)
        {
            children[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            rest += children[i].DesiredSize.Width;
            height = Math.Max(height, children[i].DesiredSize.Height);
        }
        var room = Math.Max(0, availableSize.Width - rest);
        if (Math.Abs(room - TabsRoom) > 0.5 || double.IsFinite(room) != double.IsFinite(TabsRoom))
        {
            TabsRoom = room;
            Tabs?.InvalidateMeasure();
        }
        children[0].Measure(new Size(room, availableSize.Height));
        height = Math.Max(height, children[0].DesiredSize.Height);
        return new Size(children[0].DesiredSize.Width + rest, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (var child in Children)
        {
            var width = child == Children[0] ? Math.Min(child.DesiredSize.Width, finalSize.Width) : child.DesiredSize.Width;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}
