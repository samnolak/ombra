using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Ombra.App.Services;

namespace Ombra.App.Controls;

/// <summary>
/// The editor's git change gutter (VS Code's): a bar beside each added (green) or modified (blue) line, a wedge where
/// lines were deleted (red). The marks are the saved file's against HEAD (<see cref="GitLineChanges"/>).
/// </summary>
public sealed class GitChangeMargin : AbstractMargin
{
    private static readonly IBrush Added = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly IBrush Modified = new SolidColorBrush(Color.FromRgb(0x38, 0x84, 0xE8));
    private static readonly IBrush Deleted = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private Dictionary<int, LineChangeKind> _byLine = [];

    public IReadOnlyList<LineChange> Changes { get; private set; } = [];

    public void SetChanges(IReadOnlyList<LineChange> changes)
    {
        Changes = changes;
        _byLine = [];
        foreach (var c in changes)
            if (!_byLine.TryGetValue(c.Line, out var had) || had == LineChangeKind.Deleted) _byLine[c.Line] = c.Kind;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(4, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null) oldTextView.VisualLinesChanged -= Redraw;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null) newTextView.VisualLinesChanged += Redraw;
        InvalidateVisual();
    }

    private void Redraw(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (TextView is not { VisualLinesValid: true } view || _byLine.Count == 0) return;
        foreach (var line in view.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            var top = line.VisualTop - view.VerticalOffset;
            if (_byLine.TryGetValue(number, out var kind) && kind != LineChangeKind.Deleted)
                context.FillRectangle(kind == LineChangeKind.Added ? Added : Modified, new Rect(0, top, 3, line.Height));
            if (_byLine.TryGetValue(number, out var after) && after == LineChangeKind.Deleted
                || number == 1 && _byLine.TryGetValue(0, out var first) && first == LineChangeKind.Deleted)
            {
                var y = number == 1 && _byLine.ContainsKey(0) ? top : top + line.Height;
                context.FillRectangle(Deleted, new Rect(0, y - 2, 4, 4));
            }
        }
    }
}
