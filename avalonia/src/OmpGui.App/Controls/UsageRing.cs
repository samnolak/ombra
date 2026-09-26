using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OmpGui.App.Controls;

/// <summary>
/// A small gauge for <see cref="Value"/> (0–100), the context in use, drawn next to the model (Claude Code's context
/// indicator): an outlined circle filled clockwise from the top like a pie. A pie, not an arc on a faint track, so a
/// low value never reads as a loading spinner. Colours come from styles (Styles.Session.axaml), per level.
/// </summary>
public sealed class UsageRing : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<UsageRing, double>(nameof(Value));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<UsageRing, double>(nameof(Size), 16);
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<UsageRing, double>(nameof(Thickness), 1.5);
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<UsageRing, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> ArcBrushProperty = AvaloniaProperty.Register<UsageRing, IBrush?>(nameof(ArcBrush));

    static UsageRing()
    {
        AffectsRender<UsageRing>(ValueProperty, ThicknessProperty, TrackBrushProperty, ArcBrushProperty);
        AffectsMeasure<UsageRing>(SizeProperty);
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    /// <summary>The outline's width.</summary>
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    /// <summary>The outline.</summary>
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    /// <summary>The filled part.</summary>
    public IBrush? ArcBrush { get => GetValue(ArcBrushProperty); set => SetValue(ArcBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var r = (Size - Thickness) / 2;
        var c = new Point(Size / 2, Size / 2);
        if (TrackBrush is { } track) context.DrawEllipse(null, new Pen(track, Thickness), c, r, r);
        var v = Math.Clamp(Value, 0, 100) / 100;
        if (ArcBrush is not { } fill || v <= 0) return;
        // The pie sits inside the outline with a hairline gap
        var pr = r - Thickness / 2 - 1.25;
        if (pr <= 0) return;
        if (v >= 0.995)
        {
            context.DrawEllipse(fill, null, c, pr, pr);
            return;
        }
        // At least a visible sliver, so 1 % still reads as "something is used"
        var angle = Math.Max(v, 0.05) * 2 * Math.PI;
        var start = new Point(c.X, c.Y - pr);
        var end = new Point(c.X + pr * Math.Sin(angle), c.Y - pr * Math.Cos(angle));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(c, true);
            ctx.LineTo(start);
            ctx.ArcTo(end, new Size(pr, pr), 0, angle > Math.PI, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }
        context.DrawGeometry(fill, null, g);
    }
}
