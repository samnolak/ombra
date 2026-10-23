using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Ombra.App.Controls;

/// <summary>
/// One selection through the text blocks of a message. A reply (<see cref="MarkdownView"/>) is a stack of separate
/// SelectableTextBlocks — its paragraphs, headings, list items, code and table cells — and Avalonia selects only inside the
/// one pressed, so a drag stopped at the end of that block, often its one line. Here the block pressed keeps the pointer
/// and the keyboard and selects in itself as before; once the pointer is past it, the blocks between are selected whole
/// and the block under the pointer up to it, as a browser selects a page. ⌘C (the keys, the Edit menu, the text's menu)
/// copies all of it as it reads: blocks of one list, quote or table a line apart, cells of a row a tab apart, other
/// blocks a blank line apart, a link its label. ⌘A in a message selects the whole message. A press elsewhere, or the
/// keyboard leaving the block pressed (not for the text's menu), clears it. It never reaches past its message.
/// </summary>
internal sealed class MessageSelection
{
    private readonly Control _scope;
    private readonly Func<Panel?> _root;
    private readonly Action _buildAll;
    /// <summary>The block pressed: it has the pointer while the drag lasts, and the keyboard.</summary>
    private SelectableTextBlock? _anchor;
    /// <summary>What the press selected in it: a click a point, a double click a word.</summary>
    private (int Low, int High) _pressed;
    /// <summary>The other blocks holding a part of the selection.</summary>
    private HashSet<SelectableTextBlock> _others = [];

    /// <param name="scope">The message: the selection stays in its text blocks.</param>
    /// <param name="root">The panel of its top-level blocks (a list, a table, a quote is one): blocks of one are a line
    /// apart in the copied text, blocks of two a blank line.</param>
    /// <param name="buildAll">Builds the blocks not built yet (far off screen) and lays them out, for ⌘A.</param>
    public MessageSelection(Control scope, Func<Panel?> root, Action buildAll)
    {
        (_scope, _root, _buildAll) = (scope, root, buildAll);
        scope.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        scope.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        scope.AddHandler(InputElement.LostFocusEvent, (_, e) => OnLostFocus(e), RoutingStrategies.Bubble);
        scope.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        scope.AddHandler(SelectableTextBlock.CopyingToClipboardEvent, OnCopying, RoutingStrategies.Bubble);
    }

    /// <summary>The selection of the message <paramref name="text"/> is in; null for text outside a message.</summary>
    internal static MessageSelection? Of(Visual text) => text.FindAncestorOfType<MarkdownView>()?.Selection;

    /// <summary>What a copy from <paramref name="block"/> puts on the clipboard: the whole selection when the block holds a
    /// part of it, else the block's own selection; as it reads either way.</summary>
    internal string SelectedText(SelectableTextBlock block) =>
        Holds(block) ? Text() : Readable(block, Math.Min(block.SelectionStart, block.SelectionEnd), Math.Max(block.SelectionStart, block.SelectionEnd));

    /// <summary>Copies the selection (the Edit menu's Copy, with the keyboard in <paramref name="block"/>).</summary>
    internal void Copy(SelectableTextBlock block)
    {
        if (SelectedText(block) is { Length: > 0 } text) _ = ContextMenus.CopyAsync(block, text);
    }

    /// <summary>The whole message selected (⌘A, the Edit menu's Select All), the keyboard staying in <paramref name="from"/>.</summary>
    internal void SelectAll(SelectableTextBlock from)
    {
        _buildAll();
        ClearOthers();
        (_anchor, _pressed) = (from, (0, 0));
        foreach (var block in Blocks())
        {
            Set(block, 0, Length(block));
            if (block != from) _others.Add(block);
        }
    }

    private bool Holds(SelectableTextBlock block) => _anchor is not null && (block == _anchor || _others.Contains(block));

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        // A right press keeps the selection for the text's menu; so does ⌃-click on macOS, a menu press the window took
        // (ContextMenus)
        if (!e.GetCurrentPoint(_scope).Properties.IsLeftButtonPressed || e.Pointer.Captured is TopLevel) return;
        ClearOthers();
        // The block took the pointer (its press was not a link's or a button's): it selects, and this goes on from it
        _anchor = e.Pointer.Captured is SelectableTextBlock block && _scope.IsVisualAncestorOf(block) ? block : null;
        if (_anchor is { } anchor)
            _pressed = (Math.Min(anchor.SelectionStart, anchor.SelectionEnd), Math.Max(anchor.SelectionStart, anchor.SelectionEnd));
    }

    /// <summary>The drag: the block pressed has already selected in itself, from the press to the pointer.</summary>
    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_anchor is not { } anchor || e.Pointer.Captured != anchor || !e.GetCurrentPoint(anchor).Properties.IsLeftButtonPressed) return;
        var blocks = Blocks();
        var from = blocks.IndexOf(anchor);
        if (from < 0) return;
        var (to, at) = Target(blocks, e.GetPosition(_scope));
        HashSet<SelectableTextBlock> selected = [];
        if (to == from)
        {
            // Back in the block pressed, its own selection: a word a double click selected stays whole when the drag turns
            // back past it (the drag out of the block moved the selection's far end)
            if (_pressed.Low < _pressed.High && anchor.SelectionStart < _pressed.Low) Set(anchor, anchor.SelectionStart, _pressed.High);
        }
        else if (to > from)
        {
            Set(anchor, _pressed.Low, Length(anchor));
            for (var i = from + 1; i < to; i++) Select(blocks[i], 0, Length(blocks[i]));
            Select(blocks[to], 0, at);
        }
        else
        {
            Set(anchor, _pressed.High, 0);
            for (var i = to + 1; i < from; i++) Select(blocks[i], Length(blocks[i]), 0);
            Select(blocks[to], Length(blocks[to]), at);
        }
        foreach (var block in _others)
            if (!selected.Contains(block)) block.ClearSelection();
        _others = selected;

        void Select(SelectableTextBlock block, int start, int end)
        {
            Set(block, start, end);
            selected.Add(block);
        }
    }

    /// <summary>
    /// The block the pointer is at and the position in it: the block across from it (the nearest of a table's row), else
    /// the end of the last block above it (between blocks, below them all), else the start of the first.
    /// </summary>
    private (int Block, int At) Target(List<SelectableTextBlock> blocks, Point p)
    {
        var (across, distance, above) = (-1, double.MaxValue, -1);
        var rects = new Rect[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            var r = rects[i] = Bounds(blocks[i]);
            if (p.Y >= r.Top && p.Y < r.Bottom)
            {
                var d = p.X < r.Left ? r.Left - p.X : p.X > r.Right ? p.X - r.Right : 0;
                if (d < distance) (across, distance) = (i, d);
            }
            else if (r.Bottom <= p.Y) above = i;
        }
        if (across >= 0)
        {
            var block = blocks[across];
            var hit = block.TextLayout.HitTestPoint(new Point(p.X - rects[across].Left - block.Padding.Left, p.Y - rects[across].Top - block.Padding.Top));
            return (across, Math.Clamp(hit.TextPosition, 0, Length(block)));
        }
        return above >= 0 ? (above, Length(blocks[above])) : (0, 0);
    }

    private void OnLostFocus(RoutedEventArgs e)
    {
        // As the block itself does: the keyboard gone elsewhere clears the selection, not when the text's menu took it
        if (_anchor is not { } anchor || e.Source != anchor
            || anchor.ContextFlyout is { IsOpen: true } || anchor.ContextMenu is { IsOpen: true }) return;
        ClearOthers();
        _anchor = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Application.Current?.PlatformSettings?.HotkeyConfiguration is not { } keys || e.Source is not SelectableTextBlock block
            || !_scope.IsVisualAncestorOf(block)) return;
        if (keys.Copy.Any(g => g.Matches(e)))
        {
            // Here, not in the block: the block's own part may be empty (a drag from its very end into the next one)
            Copy(block);
            e.Handled = true;
        }
        else if (keys.SelectAll.Any(g => g.Matches(e)))
        {
            SelectAll(block);
            e.Handled = true;
        }
    }

    /// <summary>The text's own Copy (its menu): the selection's text as it reads instead of the block's characters.</summary>
    private void OnCopying(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not SelectableTextBlock block || !_scope.IsVisualAncestorOf(block)) return;
        e.Handled = true;
        Copy(block);
    }

    /// <summary>The selection's text, block after block as the message shows them.</summary>
    private string Text()
    {
        var text = new StringBuilder();
        SelectableTextBlock? previous = null;
        foreach (var block in Blocks())
        {
            if (!Holds(block) || block.SelectionStart == block.SelectionEnd) continue;
            if (previous is not null) text.Append(Separator(previous, block));
            text.Append(Readable(block, Math.Min(block.SelectionStart, block.SelectionEnd), Math.Max(block.SelectionStart, block.SelectionEnd)));
            previous = block;
        }
        return text.ToString();
    }

    /// <summary>A tab between the cells of a table's row, a line break between blocks of one top-level block (a list's
    /// items, a table's rows, a quote's paragraphs), else a blank line (paragraphs, a heading and its text, code).</summary>
    private string Separator(SelectableTextBlock previous, SelectableTextBlock next)
    {
        var (a, b) = (Bounds(previous), Bounds(next));
        if (b.Top < a.Bottom - 1 && b.Left >= a.Right - 1) return "\t";
        return TopBlock(previous) == TopBlock(next) ? "\n" : "\n\n";
    }

    private Visual TopBlock(Visual block)
    {
        if (_root() is not { } root) return block;
        for (Visual? v = block; v is not null; v = v.GetVisualParent())
            if (v.GetVisualParent() == root) return v;
        return block;
    }

    /// <summary>The message's text blocks in reading order (built ones: one far off screen may still be a placeholder).</summary>
    private List<SelectableTextBlock> Blocks() =>
        _scope.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsEffectivelyVisible).ToList();

    private Rect Bounds(Visual block) => block.TranslatePoint(default, _scope) is { } at ? new Rect(at, block.Bounds.Size) : default;

    private void ClearOthers()
    {
        foreach (var block in _others) block.ClearSelection();
        _others = [];
    }

    private static void Set(SelectableTextBlock block, int start, int end)
    {
        if (block.SelectionStart != start) block.SetCurrentValue(SelectableTextBlock.SelectionStartProperty, start);
        if (block.SelectionEnd != end) block.SetCurrentValue(SelectableTextBlock.SelectionEndProperty, end);
    }

    /// <summary>A block's length in text positions: a run its text, a line break its characters, a control one.</summary>
    private static int Length(SelectableTextBlock block) =>
        (block.Inlines is { Count: > 0 } inlines ? inlines.Text : block.Text)?.Length ?? 0;

    /// <summary>
    /// A block's text from <paramref name="start"/> to <paramref name="end"/> (text positions) as it reads: a link, a
    /// control in the text (one position there), its label; the narrow spaces that pad an inline code's chip
    /// (<see cref="MarkdownView.ChipPadding"/>) left out.
    /// </summary>
    internal static string Readable(SelectableTextBlock block, int start, int end)
    {
        if (block.Inlines is not { Count: > 0 } inlines)
        {
            var plain = block.Text ?? "";
            return plain[Math.Min(start, plain.Length)..Math.Min(end, plain.Length)];
        }
        var text = new StringBuilder();
        var at = 0;
        Append(inlines);
        return text.ToString();

        void Append(InlineCollection items)
        {
            foreach (var inline in items)
            {
                if (at >= end) return;
                switch (inline)
                {
                    case MarkdownView.ChipPadding pad:
                        at += pad.Text?.Length ?? 0;
                        break;
                    case Run run:
                        Take(run.Text ?? "", null);
                        break;
                    case Span span:
                        Append(span.Inlines);
                        break;
                    case LineBreak:
                        Take(Environment.NewLine, "\n");
                        break;
                    case InlineUIContainer ui:
                        Take("\uFFFC", Label(ui.Child));
                        break;
                }
            }
        }

        // Takes the part of an inline (its positions) within the range: its text, or what it reads as once any of it is
        void Take(string positions, string? reads)
        {
            var (from, to) = (Math.Max(start - at, 0), Math.Min(end - at, positions.Length));
            if (to > from)
            {
                if (reads is null) text.Append(positions, from, to - from);
                else text.Append(reads);
            }
            at += positions.Length;
        }

        static string Label(Control? control) => control switch
        {
            ContentControl { Content: TextBlock label } => label.Text ?? "",
            ContentControl { Content: string label } => label,
            TextBlock label => label.Text ?? "",
            _ => "",
        };
    }
}
