using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.Utilities;

namespace OmpGui.App.Controls;

/// <summary>
/// A file's text in the Files pane, drawn as DiffView draws code: line numbers in a gutter that stays put, the code
/// coloured by its language (SyntaxHighlighter), no wrapping, scrolling both ways. Only the lines on screen are laid
/// out (a text block with a run per token took seconds for a few thousand lines), so a 10,000-line file opens at once.
/// Text is selectable with the mouse (double click: a word, triple: a line; Shift extends) and copied with Ctrl/⌘+C;
/// Ctrl/⌘+A selects everything. <see cref="TargetLine"/> scrolls a line a third of the way down and marks it.
/// </summary>
public sealed class CodeView : ContentControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<CodeView, string?>(nameof(Text));
    public static readonly StyledProperty<string?> FileNameProperty = AvaloniaProperty.Register<CodeView, string?>(nameof(FileName));
    public static readonly StyledProperty<int?> TargetLineProperty = AvaloniaProperty.Register<CodeView, int?>(nameof(TargetLine));

    private readonly CodeSurface _surface;
    private readonly ScrollViewer _scroll;

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? FileName { get => GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
    public int? TargetLine { get => GetValue(TargetLineProperty); set => SetValue(TargetLineProperty, value); }

    /// <summary>The scroller (tests read its offset).</summary>
    internal ScrollViewer Scroller => _scroll;
    internal CodeSurface Surface => _surface;

    public CodeView()
    {
        _surface = new CodeSurface();
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _surface,
        };
        Content = _scroll;
        Avalonia.Automation.AutomationProperties.SetName(_surface, "File contents");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == FileNameProperty)
        {
            _surface.SetText(Text ?? "", SyntaxHighlighter.ForName(FileName));
            ScheduleJump();
        }
        else if (change.Property == TargetLineProperty) ScheduleJump();
        else if (change.Property == ThemeVariantScope.ActualThemeVariantProperty) _surface.ThemeChanged();
    }

    private void ScheduleJump()
    {
        _surface.Marked = TargetLine is > 0 ? TargetLine.Value - 1 : -1;
        if (TargetLine is not { } line || line < 1) return;
        // After the new text is measured: the extent must include the line
        Dispatcher.UIThread.Post(() =>
        {
            if (TargetLine != line) return;
            var y = CodeSurface.PadTop + (Math.Min(line, _surface.LineCount) - 1) * CodeSurface.LineHeight;
            var max = Math.Max(0, _surface.Extent.Height - _surface.Viewport.Height);
            _scroll.Offset = new Vector(0, Math.Clamp(y - _surface.Viewport.Height / 3, 0, max));
        }, DispatcherPriority.Loaded);
    }
}

/// <summary>
/// The drawing surface of <see cref="CodeView"/>: a logically scrolled control that lays out and draws only the lines in
/// its viewport (layouts are cached), the gutter fixed on the left, the selection and the marked line behind the text.
/// </summary>
internal sealed class CodeSurface : Control, ILogicalScrollable
{
    public const double LineHeight = 19;
    public const double PadTop = 8;
    private const double FontSize = 12.5;
    private const double GutterPadLeft = 12, GutterPadRight = 12, CodePadRight = 24;

    private static readonly Typeface Mono = new(new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace"));
    private static readonly Typeface MonoItalic = new(Mono.FontFamily, FontStyle.Italic);

    private string[] _lines = [""];
    private bool[] _blockAtStart = [false];
    private SyntaxHighlighter.Language? _lang;
    private readonly Dictionary<int, TextLayout> _layouts = [];
    private readonly Dictionary<int, TextLayout> _numbers = [];
    private double _charWidth = 7.5;
    private double _gutterWidth;
    private int _maxColumns;
    private Size _extent, _viewport;
    private Vector _offset;
    private (int Line, int Col) _anchor, _caret;
    private bool _selecting;

    public CodeSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        ContextFlyout = BuildMenu();
    }

    public int LineCount => _lines.Length;

    /// <summary>Lines laid out so far (only those drawn; tests).</summary>
    internal int LaidOutLines => _layouts.Count;

    /// <summary>The line drawn marked (0-based; -1: none).</summary>
    public int Marked
    {
        get => _marked;
        set { _marked = value; InvalidateVisual(); }
    }
    private int _marked = -1;

    public bool HasSelection => _anchor != _caret;

    public void SetText(string text, SyntaxHighlighter.Language? lang)
    {
        _lines = text.Split('\n');
        _lang = lang;
        // Where a block comment is still open at each line's start (colouring a line needs it)
        _blockAtStart = new bool[_lines.Length];
        var inBlock = false;
        _maxColumns = 0;
        for (var i = 0; i < _lines.Length; i++)
        {
            _blockAtStart[i] = inBlock;
            if (lang?.BlockComment is not null) SyntaxHighlighter.Tokenize(_lines[i], lang, ref inBlock);
            _maxColumns = Math.Max(_maxColumns, _lines[i].Length);
        }
        _anchor = _caret = (0, 0);
        ThemeChanged();
        InvalidateMeasure();
    }

    /// <summary>Brushes are resolved when a line is laid out: a theme change lays them out again.</summary>
    public void ThemeChanged()
    {
        _layouts.Clear();
        _numbers.Clear();
        InvalidateVisual();
    }

    private static IBrush Brush(string key, IBrush fallback) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) == true && v is IBrush b ? b : fallback;

    private TextLayout Layout(int i)
    {
        if (_layouts.TryGetValue(i, out var cached)) return cached;
        if (_layouts.Count > 400) _layouts.Clear();
        var line = _lines[i];
        var primary = Brush("GuiTextPrimary", Brushes.Black);
        List<ValueSpan<TextRunProperties>>? spans = null;
        if (_lang is not null && line.Length > 0)
        {
            spans = [];
            var inBlock = _blockAtStart[i];
            var at = 0;
            foreach (var (kind, text) in SyntaxHighlighter.Tokenize(line, _lang, ref inBlock))
            {
                if (SyntaxHighlighter.BrushFor(kind) is { } brush)
                    spans.Add(new ValueSpan<TextRunProperties>(at, text.Length,
                        new GenericTextRunProperties(kind == SyntaxHighlighter.Token.Comment ? MonoItalic : Mono, FontSize, foregroundBrush: brush)));
                at += text.Length;
            }
        }
        var layout = new TextLayout(line, Mono, FontSize, primary, TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.None, null,
            FlowDirection.LeftToRight, double.PositiveInfinity, double.PositiveInfinity, LineHeight, 0, 1, null, spans);
        return _layouts[i] = layout;
    }

    private TextLayout Number(int i)
    {
        if (_numbers.TryGetValue(i, out var cached)) return cached;
        if (_numbers.Count > 400) _numbers.Clear();
        return _numbers[i] = new TextLayout((i + 1).ToString(CultureInfo.InvariantCulture), Mono, FontSize, Brush("GuiTextTertiary", Brushes.Gray),
            TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.None, null, FlowDirection.LeftToRight, double.PositiveInfinity, double.PositiveInfinity, LineHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _charWidth = new TextLayout("0000000000", Mono, FontSize, Brushes.Black).WidthIncludingTrailingWhitespace / 10;
        var digits = Math.Max(3, _lines.Length.ToString(CultureInfo.InvariantCulture).Length);
        _gutterWidth = GutterPadLeft + digits * _charWidth + GutterPadRight;
        var extent = new Size(_gutterWidth + _maxColumns * _charWidth + CodePadRight, PadTop * 2 + _lines.Length * LineHeight);
        if (extent != _extent)
        {
            _extent = extent;
            ScrollInvalidated?.Invoke(this, EventArgs.Empty);
        }
        return new Size(Math.Min(availableSize.Width, extent.Width), Math.Min(availableSize.Height, extent.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize != _viewport)
        {
            _viewport = finalSize;
            Offset = _offset; // clamp to the new size
            ScrollInvalidated?.Invoke(this, EventArgs.Empty);
        }
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds); // the whole surface takes the pointer
        if (_lines.Length == 0) return;
        var first = Math.Max(0, (int)Math.Floor((_offset.Y - PadTop) / LineHeight));
        var last = Math.Min(_lines.Length - 1, (int)Math.Ceiling((_offset.Y + bounds.Height - PadTop) / LineHeight));
        var codeX = _gutterWidth - _offset.X;
        var (s, e) = Ordered();
        var selection = Brush("GuiSelection", Brushes.LightBlue);
        var mark = Brush("GuiStatusWarningSoft", Brushes.LightYellow);
        for (var i = first; i <= last; i++)
        {
            var y = PadTop + i * LineHeight - _offset.Y;
            if (i == _marked) context.FillRectangle(mark, new Rect(0, y, bounds.Width, LineHeight));
            using (context.PushClip(new Rect(_gutterWidth, 0, Math.Max(0, bounds.Width - _gutterWidth), bounds.Height)))
            {
                var layout = Layout(i);
                if (HasSelection && i >= s.Line && i <= e.Line)
                {
                    var from = i == s.Line ? X(layout, s.Col) : 0;
                    var to = i == e.Line ? X(layout, e.Col) : layout.WidthIncludingTrailingWhitespace + _charWidth / 2;
                    if (to > from) context.FillRectangle(selection, new Rect(codeX + from, y, to - from, LineHeight));
                }
                layout.Draw(context, new Point(codeX, y));
            }
            var number = Number(i);
            number.Draw(context, new Point(_gutterWidth - GutterPadRight - number.WidthIncludingTrailingWhitespace, y));
        }
    }

    private static double X(TextLayout layout, int col) => layout.HitTestTextPosition(Math.Max(0, col)).X;

    private ((int Line, int Col) S, (int Line, int Col) E) Ordered() =>
        _anchor.Line < _caret.Line || _anchor.Line == _caret.Line && _anchor.Col <= _caret.Col ? (_anchor, _caret) : (_caret, _anchor);

    // ───────────── Scrolling (ILogicalScrollable: the scroll viewer around it drives the offset) ─────────────

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public bool IsLogicalScrollEnabled => true;
    public Size ScrollSize => new(_charWidth * 4, LineHeight * 3);
    public Size PageScrollSize => new(_viewport.Width, Math.Max(LineHeight, _viewport.Height - LineHeight));
    public Size Extent => _extent;
    public Size Viewport => _viewport;

    public Vector Offset
    {
        get => _offset;
        set
        {
            var v = new Vector(Math.Clamp(value.X, 0, Math.Max(0, _extent.Width - _viewport.Width)), Math.Clamp(value.Y, 0, Math.Max(0, _extent.Height - _viewport.Height)));
            if (v == _offset) return;
            _offset = v;
            InvalidateVisual();
        }
    }

    public event EventHandler? ScrollInvalidated;

    public bool BringIntoView(Control target, Rect targetRect) => false;

    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;

    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);

    // ───────────── Selection and copy ─────────────

    private (int Line, int Col) Hit(Point p)
    {
        var line = Math.Clamp((int)Math.Floor((p.Y + _offset.Y - PadTop) / LineHeight), 0, _lines.Length - 1);
        var x = p.X + _offset.X - _gutterWidth;
        if (x <= 0) return (line, 0);
        var hit = Layout(line).HitTestPoint(new Point(x, LineHeight / 2));
        return (line, Math.Clamp(hit.TextPosition + (hit.IsTrailing ? 1 : 0), 0, _lines[line].Length));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        Focus();
        var at = Hit(point.Position);
        if (e.ClickCount == 2) SelectWord(at);
        else if (e.ClickCount >= 3) { _anchor = (at.Line, 0); _caret = at.Line + 1 < _lines.Length ? (at.Line + 1, 0) : (at.Line, _lines[at.Line].Length); }
        else
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _anchor = at;
            _caret = at;
            _selecting = true;
            e.Pointer.Capture(this);
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting) return;
        var p = e.GetPosition(this);
        // Dragging past an edge scrolls
        if (p.Y < 0) Offset = new Vector(_offset.X, _offset.Y - LineHeight);
        else if (p.Y > _viewport.Height) Offset = new Vector(_offset.X, _offset.Y + LineHeight);
        _caret = Hit(p);
        ScrollInvalidated?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_selecting) return;
        _selecting = false;
        e.Pointer.Capture(null);
    }

    private void SelectWord((int Line, int Col) at)
    {
        var line = _lines[at.Line];
        static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        int a = Math.Min(at.Col, line.Length), b = a;
        while (a > 0 && Word(line[a - 1])) a--;
        while (b < line.Length && Word(line[b])) b++;
        _anchor = (at.Line, a);
        _caret = (at.Line, b);
    }

    public void SelectAll()
    {
        _anchor = (0, 0);
        _caret = (_lines.Length - 1, _lines[^1].Length);
        InvalidateVisual();
    }

    /// <summary>The selected text ('\n' between lines).</summary>
    public string SelectedText
    {
        get
        {
            if (!HasSelection) return "";
            var (s, e) = Ordered();
            if (s.Line == e.Line) return _lines[s.Line][s.Col..e.Col];
            var sb = new StringBuilder(_lines[s.Line][s.Col..]);
            for (var i = s.Line + 1; i < e.Line; i++) sb.Append('\n').Append(_lines[i]);
            return sb.Append('\n').Append(_lines[e.Line][..e.Col]).ToString();
        }
    }

    /// <summary>Selects characters <paramref name="fromCol"/>..<paramref name="toCol"/> of lines (0-based; tests).</summary>
    internal void Select(int fromLine, int fromCol, int toLine, int toCol)
    {
        _anchor = (fromLine, fromCol);
        _caret = (toLine, toCol);
        InvalidateVisual();
    }

    private async void Copy()
    {
        if (!HasSelection || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(SelectedText); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var command = Avalonia.VisualTree.VisualExtensions.GetPlatformSettings(this)?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        if (e.KeyModifiers != command) return;
        if (e.Key == Key.C) { Copy(); e.Handled = true; }
        else if (e.Key == Key.A) { SelectAll(); e.Handled = true; }
    }

    private Flyout BuildMenu()
    {
        Button Item(string text, Action act)
        {
            var b = new Button { Classes = { "menu-item" }, Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            Avalonia.Automation.AutomationProperties.SetName(b, text);
            b.Click += (_, _) => { act(); (ContextFlyout as Flyout)?.Hide(); };
            return b;
        }
        return new Flyout
        {
            Placement = PlacementMode.Pointer,
            Content = new StackPanel { Width = 180, Spacing = 2, Children = { Item("Copy", Copy), Item("Select all", SelectAll) } },
        };
    }
}
