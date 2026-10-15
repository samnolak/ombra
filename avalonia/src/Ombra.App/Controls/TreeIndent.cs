using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ombra.App.Controls;

/// <summary>
/// A conversation row's place in the tree of lines that fold it (<c>RowViewModel.Depth</c>): its content moved right by
/// <see cref="Indent"/> per level, and in that room one vertical guide per level, under the icon of the line above that
/// folds it. A run of rows at one level draws one unbroken guide (the rows touch), so what a line folds is seen at a
/// glance. Colours from styles (Styles.Session.axaml).
/// </summary>
public sealed class TreeIndent : Decorator
{
    /// <summary>The row's level; set on the ListBoxItem by the transcript's style, read by its template.</summary>
    public static readonly AttachedProperty<int> DepthProperty =
        AvaloniaProperty.RegisterAttached<TreeIndent, Control, int>("Depth");
    public static readonly StyledProperty<double> IndentProperty = AvaloniaProperty.Register<TreeIndent, double>(nameof(Indent), 22);
    /// <summary>Where a guide runs inside its level: under the centre of a 16 px icon at the level's edge.</summary>
    public static readonly StyledProperty<double> GuideOffsetProperty = AvaloniaProperty.Register<TreeIndent, double>(nameof(GuideOffset), 8);
    public static readonly StyledProperty<IBrush?> GuideBrushProperty = AvaloniaProperty.Register<TreeIndent, IBrush?>(nameof(GuideBrush));

    static TreeIndent()
    {
        AffectsMeasure<TreeIndent>(DepthProperty, IndentProperty);
        AffectsRender<TreeIndent>(DepthProperty, GuideOffsetProperty, GuideBrushProperty);
    }

    public static int GetDepth(Control c) => c.GetValue(DepthProperty);
    public static void SetDepth(Control c, int value) => c.SetValue(DepthProperty, value);

    public int Depth { get => GetValue(DepthProperty); set => SetValue(DepthProperty, value); }
    public double Indent { get => GetValue(IndentProperty); set => SetValue(IndentProperty, value); }
    public double GuideOffset { get => GetValue(GuideOffsetProperty); set => SetValue(GuideOffsetProperty, value); }
    public IBrush? GuideBrush { get => GetValue(GuideBrushProperty); set => SetValue(GuideBrushProperty, value); }

    private double Left => Math.Max(0, Depth) * Indent;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        child.Measure(availableSize.Deflate(new Thickness(Left, 0, 0, 0)));
        return new Size(child.DesiredSize.Width + Left, child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(Left, 0, Math.Max(0, finalSize.Width - Left), finalSize.Height));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        // A row folded away is 0 high: nothing to draw
        if (GuideBrush is not { } brush || Depth <= 0 || Bounds.Height <= 0) return;
        var pen = new Pen(brush, 1);
        for (var level = 0; level < Depth; level++)
        {
            var x = Math.Round(level * Indent + GuideOffset) + 0.5;
            context.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
        }
    }
}
