using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using OmpGui.App.Controls;

namespace OmpGui.Tests;

/// <summary>
/// Measures what the eye catches in a laid-out window: an icon off the middle of the text beside it, texts of different
/// sizes on different baselines, a button whose content is not in its middle, siblings that overlap, clipped text, a
/// card glued to the message box. Used by the layout tests and by the design review (LayoutAuditTests).
/// </summary>
internal static class LayoutAudit
{
    public sealed record Finding(string Kind, string Where, string Detail, Rect Box);

    /// <summary>Buttons whose content is a left-aligned row on purpose (lists, menus, links).</summary>
    private static readonly string[] LeftAligned =
        ["menu-item", "session", "sidebar-action", "option", "tool-row", "link", "title", "todo-head", "thinking-toggle", "chip", "flat", "segment", "settings-nav"];

    public static List<Finding> Run(Window w, Control? scope = null)
    {
        var found = new List<Finding>();
        var root = scope ?? w;
        foreach (var c in root.GetVisualDescendants().OfType<Control>().Where(Shown))
        {
            switch (c)
            {
                case Button b: CheckButton(w, b, found); CheckCutOff(w, b, found); break;
                case TextBlock t: CheckClipped(w, t, found); break;
            }
            if (c is Panel p) CheckRow(w, p, found);
            if (c is StackPanel or WrapPanel or DockPanel) CheckOverlap(w, (Panel)c, found);
            if (c is Border frame) { CheckFrame(w, frame, found); CheckMirror(w, frame, found); }
        }
        CheckComposerGap(w, found);
        return found;
    }

    /// <summary>
    /// What an element actually draws, in window coordinates: an icon's strokes (its geometry scaled, plus half the pen),
    /// not the 24-unit square it is laid out in — a tick drawn low in its square sat 1.5 px low in a checkbox.
    /// </summary>
    private static Rect Ink(Window w, Control k)
    {
        var box = Box(w, k);
        if (k is Icon { Data: { } data } icon)
        {
            var scale = icon.Size / 24.0;
            var g = data.Bounds;
            var half = icon.Filled ? 0 : icon.StrokeThickness / 2;
            return new Rect(box.X + g.X * scale - half, box.Y + g.Y * scale - half, g.Width * scale + 2 * half, g.Height * scale + 2 * half);
        }
        if (k is TextBlock t && t.TextLayout.TextLines.Count == 1 && TextCenter(w, t) is { } tc)
        {
            // The capitals' band, where the eye puts a label
            var cap = 0.727 * t.FontSize;
            var x = box.X + t.Padding.Left + (t.TextAlignment == TextAlignment.Center ? (box.Width - t.Padding.Left - t.Padding.Right - t.TextLayout.Width) / 2 : 0);
            return new Rect(x, tc - cap / 2, t.TextLayout.Width, cap);
        }
        return box;
    }

    /// <summary>
    /// A small drawn frame — a checkbox, a disc, a badge — holds its mark in its middle and inside its border: a 14 px
    /// tick in a 13 px inside overflowed and sat 1 px right and 1.5 px low.
    /// </summary>
    private static void CheckFrame(Window w, Border b, List<Finding> found)
    {
        if (b.Bounds.Width > 40 || b.Bounds.Height > 40 || b.Child is null) return;
        if (b.BorderThickness == default && b.Background is null) return;
        var marks = b.GetVisualDescendants().OfType<Control>()
            .Where(k => Shown(k) && (k is Icon or Ellipse || k is TextBlock { Text.Length: > 0 } t && t.TextLayout.TextLines.Count == 1)).ToList();
        if (marks.Count == 0) return;
        var ink = marks.Select(k => Ink(w, k)).Aggregate((x, y) => x.Union(y));
        var bb = Box(w, b);
        var inner = bb.Deflate(b.BorderThickness);
        var dx = (ink.X + ink.Width / 2) - (bb.X + bb.Width / 2);
        var dy = (ink.Y + ink.Height / 2) - (bb.Y + bb.Height / 2);
        if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1)
            found.Add(new("frame-centre", Name(b), string.Create(CultureInfo.InvariantCulture,
                $"mark off centre by {dx:+0.0;-0.0}, {dy:+0.0;-0.0} px in a {bb.Width:0}×{bb.Height:0} frame"), bb));
        // A mark that wants more room than the inside gets squeezed there but still draws at its size, from the
        // corner: it lands off the middle
        foreach (var k in marks.Where(k => k is Icon or Ellipse))
        {
            // Avalonia clamps the desired size to the room given: the declared size says what it draws
            var want = k is Icon i ? new Size(i.Size, i.Size) : new Size(double.IsNaN(k.Width) ? 0 : k.Width, double.IsNaN(k.Height) ? 0 : k.Height);
            if (want.Width > k.Bounds.Width + 0.5 || want.Height > k.Bounds.Height + 0.5)
                found.Add(new("frame-overflow", Name(b), string.Create(CultureInfo.InvariantCulture,
                    $"mark wants {want.Width:0.0}×{want.Height:0.0}, has {k.Bounds.Width:0.0}×{k.Bounds.Height:0.0} (inside {inner.Width:0.0}×{inner.Height:0.0})"), bb));
        }
    }

    /// <summary>
    /// Round buttons at the two ends of a card mirror each other: the same distance from their edges and the same height
    /// ("+" once sat 22 px in from the message box's left edge, Send 10 px from its right).
    /// </summary>
    private static void CheckMirror(Window w, Border card, List<Finding> found)
    {
        if (card.CornerRadius.TopLeft < 16) return;
        var cb = Box(w, card);
        var round = card.GetVisualDescendants().OfType<Button>()
            .Where(b => Shown(b) && b.Classes.Any(c => c is "round" or "send")).Select(b => (b, r: Box(w, b))).ToList();
        if (round.Count < 2) return;
        var left = round.MinBy(x => x.r.X);
        var right = round.MaxBy(x => x.r.Right);
        if (left.b == right.b) return;
        var gl = left.r.X - cb.X;
        var gr = cb.Right - right.r.Right;
        var cyl = left.r.Y + left.r.Height / 2;
        var cyr = right.r.Y + right.r.Height / 2;
        if (gl > 48 || gr > 48 || Math.Abs(cyl - cyr) > 16) return; // not a pair at the two ends of one row
        if (Math.Abs(gl - gr) > 2 || Math.Abs(cyl - cyr) > 1 || Math.Abs(left.r.Height - right.r.Height) > 1)
            found.Add(new("mirror", Name(left.b) + " ⟷ " + Name(right.b), string.Create(CultureInfo.InvariantCulture,
                $"{gl:0.0} px from the left edge vs {gr:0.0} px from the right; centres {cyl:0.0} / {cyr:0.0}; heights {left.r.Height:0} / {right.r.Height:0}"),
                left.r.Union(right.r)));
    }

    private static bool Shown(Control c) => c.IsEffectivelyVisible && c.Bounds.Width > 0.5 && c.Bounds.Height > 0.5 && c.Opacity > 0.05;

    /// <summary>Where it is drawn in the window (render transforms, e.g. a rotated chevron, included).</summary>
    private static Rect Box(Window w, Control c) =>
        c.TransformToVisual(w) is { } m ? new Rect(c.Bounds.Size).TransformToAABB(m) : default;

    private static string Name(Control c)
    {
        var parts = new List<string>();
        for (Visual? v = c; v is not null and not Window && parts.Count < 4; v = v.GetVisualParent())
        {
            if (v is not Control k) continue;
            var label = k.Name is { Length: > 0 } n ? "#" + n
                : k is TextBlock tb ? $"\"{Short(tb.Text)}\""
                : k is Button { Content: string s } ? $"Button\"{Short(s)}\""
                : k.Classes.Count > 0 ? k.GetType().Name + "." + string.Join(".", k.Classes.Where(x => !x.StartsWith(':'))) : null;
            if (label is not null) parts.Insert(0, label);
        }
        return string.Join(" › ", parts);
    }

    private static string Short(string? s) => s is null ? "" : s.Length <= 28 ? s : s[..28] + "…";

    /// <summary>Where the eye puts a single line of text: the middle of its capitals (Inter: cap height 0.727 em).</summary>
    private static double? TextCenter(Window w, TextBlock t)
    {
        if (string.IsNullOrWhiteSpace(t.Text) || t.TextLayout.TextLines.Count != 1) return null;
        var top = Box(w, t).Y + t.Padding.Top;
        var baseline = t.TextLayout.TextLines[0].Baseline;
        return top + baseline - 0.727 * t.FontSize / 2;
    }

    private static double? Baseline(Window w, TextBlock t) =>
        string.IsNullOrWhiteSpace(t.Text) || t.TextLayout.TextLines.Count != 1 ? null : Box(w, t).Y + t.Padding.Top + t.TextLayout.TextLines[0].Baseline;

    /// <summary>A row's icons and dots against its text; texts of different sizes against each other's baseline.</summary>
    private static void CheckRow(Window w, Panel p, List<Finding> found)
    {
        var horizontal = p is StackPanel { Orientation: Orientation.Horizontal } or WrapPanel or DockPanel
                         // Never read Grid.ColumnDefinitions here: in Avalonia 12 its getter creates the grid's definition
                         // data, and a grid measured without definitions then fails in its next ArrangeOverride (a null
                         // cell cache). A grid is a row when its children sit in more than one column.
                         || p is Grid && p.Children.Any(k => Grid.GetColumn(k) > 0);
        if (!horizontal) return;
        var kids = p.Children.OfType<Control>().Where(Shown).ToList();
        var texts = kids.OfType<TextBlock>().Where(t => TextCenter(w, t) is not null).ToList();
        if (texts.Count == 0) return;
        foreach (var mark in kids.Where(k => k is Icon or Ellipse || k is Border { Child: TextBlock or null } b && b.Bounds.Height <= 24))
        {
            var m = Box(w, mark);
            var mc = m.Y + m.Height / 2;
            foreach (var t in texts)
            {
                var tb = Box(w, t);
                if (tb.Y > m.Bottom || tb.Bottom < m.Y) continue; // another line of a wrapping row
                var tc = TextCenter(w, t)!.Value;
                if (Math.Abs(mc - tc) > 1.5)
                    found.Add(new("icon-text", Name(mark) + " ⟷ " + Name(t),
                        string.Create(CultureInfo.InvariantCulture, $"mark centre {mc:0.0}, text centre {tc:0.0} ({mc - tc:+0.0;-0.0} px)"), m.Union(tb)));
            }
        }
        for (var i = 0; i + 1 < texts.Count; i++)
        {
            var a = texts[i];
            var b = texts[i + 1];
            if (Math.Abs(a.FontSize - b.FontSize) < 0.5) continue;
            var ba = Baseline(w, a)!.Value;
            var bb = Baseline(w, b)!.Value;
            if (Math.Abs(ba - bb) > 1.0)
                found.Add(new("baseline", Name(a) + " ⟷ " + Name(b),
                    string.Create(CultureInfo.InvariantCulture, $"{a.FontSize:0}px baseline {ba:0.0}, {b.FontSize:0}px baseline {bb:0.0}"), Box(w, a).Union(Box(w, b))));
        }
    }

    /// <summary>
    /// An action button's content sits in its middle (a MinWidth wider than the label left "Allow" on the left). What
    /// counts is what is drawn — its texts and icons — not the presenter, which may be stretched.
    /// </summary>
    private static void CheckButton(Window w, Button b, List<Finding> found)
    {
        if (b.Classes.Any(c => LeftAligned.Contains(c))) return;
        if (b.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault() is not { Child: Control content } || !Shown(content)) return;
        var ink = content.GetSelfAndVisualDescendants().OfType<Control>().Where(k => Shown(k) && k is TextBlock or Icon or Image or Ellipse)
            .Select(k => k is TextBlock t && t.TextLayout.TextLines.Count == 1
                ? new Rect(Box(w, t).X + (t.TextAlignment == TextAlignment.Left ? 0 : 0), Box(w, t).Y, t.TextLayout.Width, Box(w, t).Height)
                : k is Icon ? Ink(w, k) : Box(w, k)).ToList();
        if (ink.Count == 0) return;
        var bb = Box(w, b);
        var cb = ink.Aggregate((x, y) => x.Union(y));
        var dx = (cb.X + cb.Width / 2) - (bb.X + bb.Width / 2);
        var dy = (cb.Y + cb.Height / 2) - (bb.Y + bb.Height / 2);
        if (Math.Abs(dx) > 2 || Math.Abs(dy) > 1.5)
            found.Add(new("button-content", Name(b),
                string.Create(CultureInfo.InvariantCulture, $"content off centre by {dx:+0.0;-0.0}, {dy:+0.0;-0.0} px (button {bb.Width:0}×{bb.Height:0}, content {cb.Width:0}×{cb.Height:0})"), bb));
    }

    /// <summary>
    /// A button runs out of a container that clips (the message box once lost its send button past its right edge).
    /// Scrolling areas are not counted: what they hide is a scroll away.
    /// </summary>
    private static void CheckCutOff(Window w, Button b, List<Finding> found)
    {
        var box = Box(w, b);
        for (Visual? v = b.GetVisualParent(); v is not null and not Window; v = v.GetVisualParent())
        {
            // Scrolled content and list rows (a tool row's hover band runs past the list by design)
            if (v is ScrollContentPresenter or ScrollViewer or ItemsPresenter or VirtualizingPanel or ListBoxItem) return;
            if (v is not Control { ClipToBounds: true } clip) continue;
            var area = Box(w, clip);
            if (box.Right > area.Right + 1 || box.Left < area.Left - 1 || box.Bottom > area.Bottom + 1 || box.Top < area.Top - 1)
                found.Add(new("cut-off", Name(b), string.Create(CultureInfo.InvariantCulture,
                    $"button {box.Left:0}–{box.Right:0} × {box.Top:0}–{box.Bottom:0} outside {Name(clip)} {area.Left:0}–{area.Right:0} × {area.Top:0}–{area.Bottom:0}"), box));
            return;
        }
    }

    private static void CheckClipped(Window w, TextBlock t, List<Finding> found)
    {
        if (string.IsNullOrEmpty(t.Text) || t.TextTrimming != TextTrimming.None || t.TextWrapping != TextWrapping.NoWrap || t is Avalonia.Controls.Primitives.AccessText) return;
        var need = new TextBlock { Text = t.Text, FontSize = t.FontSize, FontFamily = t.FontFamily, FontWeight = t.FontWeight, LetterSpacing = t.LetterSpacing };
        need.Measure(Size.Infinity);
        if (need.DesiredSize.Width > t.Bounds.Width + 1)
            found.Add(new("clipped", Name(t), $"needs {need.DesiredSize.Width:0} px, has {t.Bounds.Width:0}", Box(w, t)));
    }

    /// <summary>Stacked siblings must not overlap (overlapping is only for layers: Panel, a Grid cell).</summary>
    private static void CheckOverlap(Window w, Panel p, List<Finding> found)
    {
        var kids = p.Children.OfType<Control>().Where(Shown).Select(k => (k, r: Box(w, k))).ToList();
        for (var i = 0; i < kids.Count; i++)
            for (var j = i + 1; j < kids.Count; j++)
            {
                var o = kids[i].r.Intersect(kids[j].r);
                if (o.Width > 1 && o.Height > 1)
                    found.Add(new("overlap", Name(kids[i].k) + " ⟷ " + Name(kids[j].k), $"{o.Width:0}×{o.Height:0} px overlap", o));
            }
    }

    /// <summary>A card above the message box keeps a clear gap from it (the approval card was glued to it).</summary>
    private static void CheckComposerGap(Window w, List<Finding> found)
    {
        if (w.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "ComposerBox") is not { } composer || !Shown(composer)) return;
        var top = Box(w, composer).Y;
        var cards = w.GetVisualDescendants().OfType<Border>()
            .Where(b => Shown(b) && b.Classes.Any(c => c is "dialog" or "banner" or "widgets") && Box(w, b).Bottom <= top + 1).ToList();
        if (cards.Count == 0) return;
        var last = cards.MaxBy(b => Box(w, b).Bottom)!;
        var gap = top - Box(w, last).Bottom;
        if (gap < 10)
            found.Add(new("gap", Name(last) + " ⟷ #ComposerBox", $"{gap:0.0} px between the card and the message box", Box(w, last).Union(Box(w, composer))));
    }

    public static string Report(string screen, IEnumerable<Finding> findings)
    {
        var sb = new StringBuilder();
        foreach (var f in findings) sb.AppendLine($"{screen}\t{f.Kind}\t{f.Where}\t{f.Detail}\t{f.Box.X:0},{f.Box.Y:0},{f.Box.Width:0},{f.Box.Height:0}");
        return sb.ToString();
    }
}
