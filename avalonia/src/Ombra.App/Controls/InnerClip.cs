using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Ombra.App.Controls;

/// <summary>
/// Keeps a rounded card's hairline whole. A Border that clips its child (<c>ClipToBounds</c>) clips it to its outer
/// rounded edge, so on the curved corners a child with a fill of its own (a header bar, the change card's head, a hovered
/// row) paints over the border, which is drawn first: the hairline vanished on every corner. Set on such a Border (from
/// its style), its child is clipped to the border's inner edge instead — radius less the border's width at each corner —
/// and never reaches the hairline.
/// </summary>
public static class InnerClip
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Border, bool>("IsEnabled", typeof(InnerClip));

    static InnerClip()
    {
        IsEnabledProperty.Changed.AddClassHandler<Border>((border, e) =>
        {
            if (e.NewValue is true)
            {
                border.LayoutUpdated += OnLayoutUpdated;
                Apply(border);
            }
            else
            {
                border.LayoutUpdated -= OnLayoutUpdated;
                if (border.Child is { } child) child.Clip = null;
            }
        });
    }

    public static bool GetIsEnabled(Border b) => b.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Border b, bool value) => b.SetValue(IsEnabledProperty, value);

    private static void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is Border border) Apply(border);
    }

    private static void Apply(Border border)
    {
        if (border.Child is not { } child || child.Bounds.Width <= 0 || child.Bounds.Height <= 0) return;
        var t = border.BorderThickness;
        var r = border.CornerRadius;
        // The inner edge in the child's own space (the child sits inside the border's thickness and padding)
        var inner = new Rect(border.Bounds.Size).Deflate(t).Translate(-child.Bounds.Position);
        var corners = new CornerRadius(
            Math.Max(0, r.TopLeft - Math.Max(t.Left, t.Top)), Math.Max(0, r.TopRight - Math.Max(t.Right, t.Top)),
            Math.Max(0, r.BottomRight - Math.Max(t.Right, t.Bottom)), Math.Max(0, r.BottomLeft - Math.Max(t.Left, t.Bottom)));
        var key = (inner, corners);
        if (child.Clip is { } existing && Equals(existing.Tag(), key)) return;
        child.Clip = RoundedRect(inner, corners, key);
    }

    private static Geometry RoundedRect(Rect r, CornerRadius c, object key)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(r.Left + c.TopLeft, r.Top), true);
            ctx.LineTo(new Point(r.Right - c.TopRight, r.Top));
            if (c.TopRight > 0) ctx.ArcTo(new Point(r.Right, r.Top + c.TopRight), new Size(c.TopRight, c.TopRight), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(r.Right, r.Bottom - c.BottomRight));
            if (c.BottomRight > 0) ctx.ArcTo(new Point(r.Right - c.BottomRight, r.Bottom), new Size(c.BottomRight, c.BottomRight), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(r.Left + c.BottomLeft, r.Bottom));
            if (c.BottomLeft > 0) ctx.ArcTo(new Point(r.Left, r.Bottom - c.BottomLeft), new Size(c.BottomLeft, c.BottomLeft), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(r.Left, r.Top + c.TopLeft));
            if (c.TopLeft > 0) ctx.ArcTo(new Point(r.Left + c.TopLeft, r.Top), new Size(c.TopLeft, c.TopLeft), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }
        Keys.AddOrUpdate(g, key);
        return g;
    }

    /// <summary>What each clip was made for, so a layout pass that changes nothing makes no new geometry.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Geometry, object> Keys = new();

    private static object? Tag(this Geometry g) => Keys.TryGetValue(g, out var k) ? k : null;
}
