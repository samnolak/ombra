using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace OmpGui.App.Controls;

/// <summary>
/// The header of an open block in the conversation (a console's or an output's bar, a changed file's header, the
/// thinking line) stays at the top of the view while any of its block is on screen, as CSS <c>position: sticky</c> does:
/// it is moved down inside its block (its parent) by a render transform, never out of it, so the block's end pushes it
/// up and away. Moved, it is the same control: clicking it does what it always does (fold the block).
/// <para>
/// Nothing runs per frame or per header: <see cref="TranscriptPanel"/> places the headers of the one row at the top of
/// its view, when the view moves or its rows are laid out (a header registers with its row when it enters the tree).
/// A moved header draws over the rest of its block (ZIndex) and has the class <c>stuck</c>; it must be opaque, and its
/// own RenderTransform must not be animated (Fluent's Button animates its own: wrap a button in a Border, the header).
/// </summary>
public static class StickyHeader
{
    public static readonly AttachedProperty<bool> IsStickyProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsSticky", typeof(StickyHeader));

    /// <summary>The sticky headers inside a row of the conversation (set on the row's container).</summary>
    private static readonly AttachedProperty<List<Control>?> HeadersProperty =
        AvaloniaProperty.RegisterAttached<Control, List<Control>?>("Headers", typeof(StickyHeader));

    /// <summary>The row a header registered with.</summary>
    private static readonly AttachedProperty<Control?> RowProperty =
        AvaloniaProperty.RegisterAttached<Control, Control?>("Row", typeof(StickyHeader));

    public static bool GetIsSticky(Control c) => c.GetValue(IsStickyProperty);
    public static void SetIsSticky(Control c, bool value) => c.SetValue(IsStickyProperty, value);

    static StickyHeader()
    {
        IsStickyProperty.Changed.AddClassHandler<Control>((header, e) =>
        {
            if (e.NewValue is true)
            {
                header.AttachedToVisualTree += OnAttached;
                header.DetachedFromVisualTree += OnDetached;
                if (header.IsAttachedToVisualTree()) Register(header);
            }
            else
            {
                header.AttachedToVisualTree -= OnAttached;
                header.DetachedFromVisualTree -= OnDetached;
                Unregister(header);
            }
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Register((Control)sender!);

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Unregister((Control)sender!);

    /// <summary>Registers with the row it is in: the ancestor laid out by a <see cref="TranscriptPanel"/>.</summary>
    private static void Register(Control header)
    {
        Unregister(header);
        Control? row = null;
        for (var v = header.GetVisualParent(); v is not null; v = v.GetVisualParent())
            if (v is Control c && c.GetVisualParent() is TranscriptPanel)
            {
                row = c;
                break;
            }
        if (row is null) return;
        var headers = row.GetValue(HeadersProperty);
        if (headers is null) row.SetValue(HeadersProperty, headers = []);
        headers.Add(header);
        header.SetValue(RowProperty, row);
    }

    private static void Unregister(Control header)
    {
        Place(header, 0);
        if (header.GetValue(RowProperty) is not { } row) return;
        row.GetValue(HeadersProperty)?.Remove(header);
        header.ClearValue(RowProperty);
    }

    /// <summary>
    /// Places the headers of a row for the top of the view at <paramref name="line"/> (the row's coordinates; a header
    /// stuck above the row's own, an exploration group's line, already moved it down): a header whose block crosses the
    /// line is moved onto it, pushed up by its block's end; every other header is where it was laid out.
    /// </summary>
    internal static void PlaceIn(Control row, double line)
    {
        if (row.GetValue(HeadersProperty) is not { Count: > 0 } headers) return;
        foreach (var header in headers) Place(header, Offset(header, row, line));
    }

    /// <summary>Puts every header of a row back where it was laid out.</summary>
    internal static void ReleaseIn(Control row)
    {
        if (row.GetValue(HeadersProperty) is not { Count: > 0 } headers) return;
        foreach (var header in headers) Place(header, 0);
    }

    /// <summary>How far down its block a header goes for the line: 0 unless the block crosses it.</summary>
    private static double Offset(Control header, Control row, double line)
    {
        if (!header.IsVisible || header.GetVisualParent() is not Control block || !block.IsEffectivelyVisible) return 0;
        // Where the block is in the row as laid out (render transforms aside: the header's own is what is computed here)
        var blockTop = 0.0;
        for (Visual? v = block; !ReferenceEquals(v, row); v = v.GetVisualParent())
        {
            if (v is null) return 0;
            blockTop += v.Bounds.Y;
        }
        var headerTop = blockTop + header.Bounds.Y;
        var blockBottom = blockTop + block.Bounds.Height;
        if (line <= headerTop || line >= blockBottom) return 0;
        return Math.Max(0, Math.Min(line - headerTop, blockBottom - headerTop - header.Bounds.Height));
    }

    private static void Place(Control header, double y)
    {
        var stuck = y >= 0.5;
        if (header.RenderTransform is not TranslateTransform shift)
        {
            if (!stuck) return;
            header.RenderTransform = shift = new TranslateTransform();
        }
        shift.Y = stuck ? y : 0;
        header.ZIndex = stuck ? 1 : 0;
        header.Classes.Set("stuck", stuck);
    }
}
