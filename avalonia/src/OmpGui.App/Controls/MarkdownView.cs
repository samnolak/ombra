using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace OmpGui.App.Controls;

/// <summary>
/// Renders the agent's Markdown (CommonMark + pipe tables via Markdig) into native, selectable Avalonia text.
/// Only http/https links are clickable (the platform opens them); code blocks get a Copy button; both have a context menu
/// (<see cref="ContextMenus"/>).
/// A streaming reply is rendered at most once a frame, and incrementally: text appended to a Markdown document can only
/// change its last top-level block, so only that block is parsed again and the blocks above keep their controls — their
/// height, and a selection made in them, never change while the reply grows. The last block is updated in place where it
/// can be (a paragraph's or a heading's text, a code block's new lines, a list's new or changed items) and built again
/// otherwise. Text beyond <see cref="MaxMarkdownChars"/> is shown as plain text.
/// A block is built only once it comes within half a view of what is on screen; until then it is a placeholder as tall
/// as its text is estimated to be (<see cref="MarkdownHeight"/>), so a long reply costs what is shown of it, not all of
/// it (a 26 KB reply took 400 ms to build whole, the frame a chat ending with one was opened in). The blocks on screen of
/// a view taller than its viewport are the scroll anchoring's candidates, so blocks built (or changing) above them
/// move nothing the reader sees.
/// Looks follow the design system: control colors and sizes come from the "Markdown" styles (md-* classes in
/// Views/Styles.axaml, which follow the theme); the one inline color (code spans) is resolved when the text is built,
/// so a theme change rebuilds the view.
/// </summary>
public sealed class MarkdownView : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));
    public static readonly StyledProperty<bool> IsStreamingProperty = AvaloniaProperty.Register<MarkdownView, bool>(nameof(IsStreaming));

    /// <summary>Beyond this the text stays plain: a control tree for megabytes of Markdown costs more than it gives.</summary>
    private const int MaxMarkdownChars = 200_000;
    /// <summary>Plain text is laid out whole on every change: while such a reply streams, a few times a second is enough.</summary>
    private const long PlainIntervalMs = 250;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().UseAutoLinks().UseTaskLists().Build();

    /// <summary>The top-level blocks on screen, in order: the children of <see cref="_root"/>.</summary>
    private readonly List<RenderedBlock> _blocks = [];
    private StackPanel? _root;
    private string? _rendered;
    private bool _frameRequested;
    /// <summary>The link reference definitions of the text on screen ("[d]: url"), as a key: "" when it has none.</summary>
    private string _definitions = "";
    /// <summary>The text block of a reply shown plain, and when it last took text.</summary>
    private SelectableTextBlock? _plain;
    private long _plainAt;
    /// <summary>The part of the view on screen (its own coordinates) at the last layout; null before it has been laid
    /// out in a window, or while none of it can be seen.</summary>
    private Rect? _viewport;
    private IScrollAnchorProvider? _anchors;
    /// <summary>The blocks registered with the scroll anchoring (on screen, in a view taller than its viewport).</summary>
    private List<Control> _anchored = [];
    /// <summary>The views with blocks registered as anchors: a transcript row hidden takes its views' anchors away.</summary>
    private static readonly HashSet<MarkdownView> Anchoring = [];

    public MarkdownView() => EffectiveViewportChanged += OnEffectiveViewportChanged;

    public string? Markdown { get => GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    public bool IsStreaming { get => GetValue(IsStreamingProperty); set => SetValue(IsStreamingProperty, value); }

    /// <summary>Raised when a Copy button asks to put text on the clipboard (tests observe it).</summary>
    public static event Action<string>? CopyRequested;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty || change.Property == IsStreamingProperty) Schedule();
        else if (change.Property == ThemeVariantScope.ActualThemeVariantProperty && _rendered is not null && !Equals(_renderedTheme, AppTheme))
        {
            // The inline brushes were resolved for another theme: every block is built again. (Only a real theme change
            // does it: the variant also "changes" each time a recycled transcript row is attached.)
            _rendered = null;
            _root = null;
            _blocks.Clear();
            ClearAnchors();
            Schedule();
        }
    }

    private Avalonia.Styling.ThemeVariant? _renderedTheme;

    private static Avalonia.Styling.ThemeVariant? AppTheme => Application.Current?.ActualThemeVariant;

    /// <summary>
    /// The first text and a finished reply are rendered at once; a streaming reply once a frame, just before the frame is
    /// laid out and drawn, however many tokens came in since the last. A view in a window but hidden (a transcript row
    /// kept built while scrolled away, Controls/TranscriptPanel) renders when it is next laid out, shown again: a reply
    /// streaming out of sight, or a row given to a hidden container, builds nothing nobody sees.
    /// </summary>
    private void Schedule()
    {
        if (!IsEffectivelyVisible && TopLevel.GetTopLevel(this) is not null)
        {
            _renderOnMeasure = true;
            InvalidateMeasure();
            return;
        }
        if (!IsStreaming || _rendered is null || TopLevel.GetTopLevel(this) is not { } top)
        {
            Render();
            return;
        }
        if (_frameRequested) return;
        _frameRequested = true;
        var wait = (Markdown?.Length ?? 0) > MaxMarkdownChars ? PlainIntervalMs - (Environment.TickCount64 - _plainAt) : 0;
        if (wait > 0)
        {
            DispatcherTimer.RunOnce(() =>
            {
                _frameRequested = false;
                Render();
            }, TimeSpan.FromMilliseconds(wait));
            return;
        }
        top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            Render();
        });
    }

    private bool _renderOnMeasure;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_renderOnMeasure)
        {
            _renderOnMeasure = false;
            Render();
        }
        return base.MeasureOverride(availableSize);
    }

    private void Render()
    {
        var text = Markdown ?? "";
        if (text == _rendered && Content is not null) return;
        if (text.Length > MaxMarkdownChars || !RenderBlocks(text)) ShowPlain(text);
        _rendered = text; // only once it is on screen: a failed render is retried with the next text
        _renderedTheme = AppTheme;
        // Laid out now, at the width it last had: a view hidden before the next layout (a reply that ends as its chat is
        // left) is still drawn by Avalonia, which would build the text's layout in the render pass and template its links
        // there ("Visual was invalidated during the render pass"). Blocks that did not change are measured already.
        if (Content is Control content && TopLevel.GetTopLevel(this) is not null && content.Bounds.Width > 0)
            content.Measure(new Size(content.Bounds.Width, double.PositiveInfinity));
    }

    private void ShowPlain(string text)
    {
        _root = null;
        _blocks.Clear();
        ClearAnchors();
        // The same block takes the new text: a selection in it stays, and no new control is made for each change
        if (_plain is not null && ReferenceEquals(Content, _plain)) _plain.Text = text;
        else Content = _plain = Paragraph(text);
        _plainAt = Environment.TickCount64;
    }

    /// <summary>
    /// Brings the blocks on screen up to <paramref name="text"/>, keeping every block whose text did not change; false
    /// when the text has to be shown plain (Markdig refused it, or it nests too deep).
    /// </summary>
    private bool RenderBlocks(string text)
    {
        // Text that only grew (a streaming reply) is parsed from the line its last block starts on: nothing before it can
        // change. Not once the text has link reference definitions: a "[docs][d]" in the new text needs the "[d]: url"
        // of a block above, which a parse of the new text alone does not see.
        var keep = 0;
        var from = 0;
        if (_definitions.Length == 0 && _root is not null && ReferenceEquals(Content, _root) && _blocks.Count > 0 && _rendered is { } shown
            && text.StartsWith(shown, StringComparison.Ordinal))
        {
            keep = _blocks.Count - 1;
            from = _blocks[^1].Start;
        }
        if (Parse(text[from..]) is not { } doc) return false;
        var definitions = Definitions(doc);
        if (definitions != _definitions)
        {
            // A link reference definition came, changed or went: links anywhere, before it too, may read differently, so
            // all of it is built again
            _root = null;
            _blocks.Clear();
            if (from > 0)
            {
                if (Parse(text) is not { } whole) return false;
                doc = whole;
                definitions = Definitions(doc);
            }
            (keep, from) = (0, 0);
            _definitions = definitions;
        }
        Services.PerfLog.Count("markdown_build");
        var root = _root ??= new StackPanel { Spacing = 12 };
        // Each block's text runs from the line it starts on to the next block's (the last one's to the end): Markdig's
        // span of a block still open at the end of the text does not cover what was written into it. The definitions'
        // group draws nothing and Markdig gives it the span of the document's start, after the blocks before it: left
        // in, it would end the block before it ahead of where that block starts.
        var parsed = doc.Where(b => b is not LinkReferenceDefinitionGroup).ToList();
        var starts = parsed.Select(b => LineStart(text, from + b.Span.Start)).Append(text.Length).ToList();
        var i = keep;
        for (var k = 0; k < parsed.Count; k++)
        {
            var (block, start, end) = (parsed[k], starts[k], starts[k + 1]);
            var source = text[start..end].TrimEnd();
            var old = i < _blocks.Count ? _blocks[i] : null;
            if (old is not null && old.Start == start && old.Source == source) { i++; continue; }
            if (old?.Update is { } update && update(block, text, from, end))
            {
                (old.Start, old.Source) = (start, source);
                i++;
                continue;
            }
            // Built once it comes near the view (BuildNearView, below): until then a placeholder of its estimated height
            var rendered = new RenderedBlock { Start = start, Source = source, Control = new Unbuilt(source, atTop: i == 0), Pending = (block, text, from, end) };
            if (old is not null)
            {
                _blocks[i] = rendered;
                root.Children[i] = rendered.Control;
            }
            else
            {
                _blocks.Add(rendered);
                root.Children.Add(rendered.Control);
            }
            i++;
        }
        if (_blocks.Count > i)
        {
            _blocks.RemoveRange(i, _blocks.Count - i);
            root.Children.RemoveRange(i, root.Children.Count - i);
        }
        if (!ReferenceEquals(Content, root)) Content = root;
        BuildNearView();
        UpdateAnchors();
        return true;
    }

    /// <summary>
    /// Builds the blocks within half a view of the part on screen (as the last layout placed them, the blocks not built
    /// yet at their estimated height). Before the view has been laid out nothing is built: its first layout tells what
    /// is on screen (<see cref="OnEffectiveViewportChanged"/>).
    /// </summary>
    private void BuildNearView()
    {
        if (_root is null || _viewport is not { } view || !ReferenceEquals(Content, _root)) return;
        var (from, to) = (view.Top - view.Height / 2, view.Bottom + view.Height / 2);
        var width = _root.Bounds.Width > 0 ? _root.Bounds.Width : Bounds.Width > 0 ? Bounds.Width : 600;
        var y = 0.0;
        for (var i = 0; i < _blocks.Count && y <= to; i++)
        {
            var block = _blocks[i];
            var height = block.Control is Unbuilt unbuilt ? unbuilt.Estimate(width) : block.Control.DesiredSize.Height;
            if (block.Pending is { } pending && y + height >= from)
            {
                var (control, update) = TopBlock(pending.Block, pending.Text, pending.From, pending.End);
                if (i == 0 && pending.Block is HeadingBlock) control.Margin = default; // a heading at the top drops its extra space above
                (block.Control, block.Update, block.Pending) = (control, update, null);
                _root.Children[i] = control;
            }
            y += height + _root.Spacing;
        }
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var view = e.EffectiveViewport;
        _viewport = view.Height > 0 && double.IsFinite(view.Height) && double.IsFinite(view.Top) ? view : null;
        if (!IsEffectivelyVisible) return;
        BuildNearView();
        UpdateAnchors();
    }

    /// <summary>
    /// In a view taller than its viewport (a long reply), the built blocks on screen are the scroll anchoring's
    /// candidates: a block built above them (from its estimate to its real height) or changing moves the view with them,
    /// not them. The row the view is in is a candidate already (Controls/TranscriptPanel) but does not move when its own
    /// blocks do.
    /// </summary>
    private void UpdateAnchors()
    {
        _anchors ??= this.FindAncestorOfType<IScrollAnchorProvider>();
        List<Control> wanted = [];
        // Taller than the scroller's viewport (the effective viewport is cut to what is visible of the view's ancestors,
        // so it is shorter than any view only partly on screen)
        if (_anchors is not null && _viewport is { } view && _root is not null && ReferenceEquals(Content, _root) && IsEffectivelyVisible
            && _anchors is Avalonia.Controls.Primitives.IScrollable scroller && Bounds.Height > scroller.Viewport.Height)
            foreach (var block in _blocks)
                if (block.Pending is null && block.Control.IsArrangeValid && block.Control.Bounds.Intersects(view)) wanted.Add(block.Control);
        foreach (var block in _anchored)
            if (!wanted.Contains(block)) _anchors?.UnregisterAnchorCandidate(block);
        foreach (var block in wanted)
            if (!_anchored.Contains(block)) _anchors!.RegisterAnchorCandidate(block);
        _anchored = wanted;
        if (wanted.Count > 0) Anchoring.Add(this);
        else Anchoring.Remove(this);
    }

    private void ClearAnchors()
    {
        foreach (var block in _anchored) _anchors?.UnregisterAnchorCandidate(block);
        _anchored = [];
        Anchoring.Remove(this);
    }

    /// <summary>A transcript row hidden (Controls/TranscriptPanel): the blocks of the views in it stop being anchors.</summary>
    internal static void ForgetAnchorsIn(Visual row)
    {
        if (Anchoring.Count == 0) return;
        foreach (var view in Anchoring.ToList())
            if (row.IsVisualAncestorOf(view)) view.ClearAnchors();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ClearAnchors();
        _anchors = null;
        _viewport = null;
    }

    /// <summary>A block not built yet: as tall as its text is estimated to be at the width it is given.</summary>
    private sealed class Unbuilt(string source, bool atTop) : Control
    {
        private double _width = double.NaN;
        private double _height;

        public double Estimate(double width)
        {
            if (!width.Equals(_width)) (_width, _height) = (width, MarkdownHeight.Estimate(source, width, atTop));
            return _height;
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new(0, Estimate(double.IsFinite(availableSize.Width) ? availableSize.Width : 600));
    }

    /// <summary>The start of the line <paramref name="at"/> is on.</summary>
    private static int LineStart(string text, int at) => at <= 0 ? 0 : text.LastIndexOf('\n', Math.Min(at, text.Length) - 1) + 1;

    /// <summary>The document's link reference definitions as one key ("" without any).</summary>
    private static string Definitions(MarkdownDocument doc) =>
        string.Join('\n', doc.OfType<LinkReferenceDefinitionGroup>().SelectMany(g => g.OfType<LinkReferenceDefinition>())
            .Select(d => $"{d.Label}\u0001{d.Url}\u0001{d.Title}"));

    /// <summary>Takes a block's next version (the same block, grown; its text ends at <paramref name="end"/>) into the
    /// controls built for it; false: build it again.</summary>
    private delegate bool BlockUpdate(MdBlock next, string text, int from, int end);

    /// <summary>A top-level block on screen: the line it starts on, the text it was built from, its control (a
    /// placeholder until it is built: then the parsed block and the text it is built from are pending), and how it
    /// takes its next version in place (for the kinds that can).</summary>
    private sealed class RenderedBlock
    {
        public required int Start { get; set; }
        public required string Source { get; set; }
        public required Control Control { get; set; }
        public BlockUpdate? Update { get; set; }
        public (MdBlock Block, string Text, int From, int End)? Pending { get; set; }
    }

    /// <summary>A top-level block's control, and for a paragraph, a heading, a fenced code block or a list, its update.</summary>
    private static (Control, BlockUpdate?) TopBlock(MdBlock block, string text, int from, int end)
    {
        switch (block)
        {
            case HeadingBlock h:
            {
                var tb = Heading(h);
                return (tb, (next, _, _, _) => next is HeadingBlock grown && grown.Level == h.Level && Retext(tb, grown.Inline));
            }
            case ParagraphBlock p:
            {
                var tb = Text(p.Inline);
                return (tb, (next, _, _, _) => next is ParagraphBlock grown && Retext(tb, grown.Inline));
            }
            case FencedCodeBlock f:
            {
                var code = new CodeBlockView(Lines(f), f.Info);
                return (code.Root, (next, _, _, _) => next is FencedCodeBlock grown && grown.Info == f.Info && code.Update(Lines(grown)));
            }
            case ListBlock l:
            {
                var list = new ListView(l, text, from, end);
                return (list.Root, (next, t, o, e) => next is ListBlock grown && list.Update(grown, t, o, e));
            }
            default:
                return (BlockControl(block), null);
        }
    }

    /// <summary>
    /// Puts a grown paragraph's or heading's text into its control. A selection made in it stays (taking new inlines
    /// would collapse it to the start).
    /// </summary>
    private static bool Retext(MdText tb, ContainerInline? inline)
    {
        var (start, end) = (tb.SelectionStart, tb.SelectionEnd);
        tb.Inlines!.Clear();
        if (inline is not null) AddInlines(tb.Inlines, inline, tb.CodeSize);
        if (start != end)
        {
            tb.SelectionStart = start;
            tb.SelectionEnd = end;
        }
        return true;
    }

    /// <summary>Deeper block nesting than this is shown as plain text (each level is a nested control to lay out).</summary>
    private const int MaxNesting = 24;

    /// <summary>A Markdown text's blocks; null for text that Markdig refuses (it throws past its own nesting limit) or that
    /// nests deeper than <see cref="MaxNesting"/>.</summary>
    private static MarkdownDocument? Parse(string markdown)
    {
        MarkdownDocument doc;
        try { doc = Markdig.Markdown.Parse(markdown, Pipeline); }
        catch (ArgumentException) { return null; }
        return Depth(doc) > MaxNesting ? null : doc;
    }

    /// <summary>The control tree for a Markdown text (public for tests), plain text when it cannot be parsed.</summary>
    public static Control Build(string markdown) =>
        Parse(markdown) is { } doc ? Blocks(doc, 12) : Paragraph(markdown);

    /// <summary>A container's blocks one under the other; a heading at the top drops its extra space above.</summary>
    private static StackPanel Blocks(ContainerBlock container, double spacing)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var block in container)
        {
            var control = BlockControl(block);
            if (panel.Children.Count == 0 && block is HeadingBlock) control.Margin = default;
            panel.Children.Add(control);
        }
        return panel;
    }

    private static Control BlockControl(MdBlock block) => block switch
    {
        HeadingBlock h => Heading(h),
        ParagraphBlock p => Text(p.Inline),
        FencedCodeBlock f => new CodeBlockView(Lines(f), f.Info).Root,
        CodeBlock c => new CodeBlockView(Lines(c), null).Root,
        ListBlock l => List(l),
        QuoteBlock q => Quote(q),
        ThematicBreakBlock => new Border { Classes = { "md-rule" } },
        Table t => TableControl(t),
        LeafBlock leaf => Paragraph(Lines(leaf)),
        ContainerBlock container => Stack(container),
        _ => new Control(),
    };

    private static int Depth(ContainerBlock root)
    {
        var max = 0;
        var pending = new Stack<(ContainerBlock Block, int Level)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (block, level) = pending.Pop();
            max = Math.Max(max, level);
            if (max > MaxNesting) break;
            foreach (var child in block)
                if (child is ContainerBlock c) pending.Push((c, level + 1));
        }
        return max;
    }

    private static StackPanel Stack(ContainerBlock container) => Blocks(container, 8);

    /// <summary>H1 22, H2 19, H3 and deeper 16 (the message size), all SemiBold, with more space above than below.</summary>
    private static MdText Heading(HeadingBlock h)
    {
        var (size, lineHeight, above) = h.Level switch { 1 => (22.0, 30.0, 10.0), 2 => (19.0, 26.0, 8.0), _ => (16.0, 24.0, 4.0) };
        var tb = Text(h.Inline, size, FontWeight.SemiBold);
        tb.Classes.Add("md-heading");
        tb.LineHeight = lineHeight;
        tb.Margin = new Thickness(0, above, 0, 0);
        return tb;
    }

    private static string Lines(LeafBlock leaf) => leaf.Lines.ToString().TrimEnd('\n');

    private static SelectableTextBlock Paragraph(string text) => new() { Text = text, Classes = { "body" } };

    /// <summary>The message text size (GuiFontMessage; 16 if the theme has none): inline code is 0.875 of the text around it.</summary>
    private static double BodySize =>
        Application.Current?.TryFindResource("GuiFontMessage", out var v) == true && v is double d ? d : 16;

    private static MdText Text(ContainerInline? inline, double? size = null, FontWeight? weight = null, string textClass = "body")
    {
        var codeSize = Math.Round((size ?? (textClass == "body" ? BodySize : 14)) * 0.875 * 4) / 4;
        var tb = new MdText { CodeSize = codeSize, Classes = { textClass } };
        if (size is { } s) tb.FontSize = s;
        if (weight is { } w) tb.FontWeight = w;
        if (inline is not null) AddInlines(tb.Inlines!, inline, codeSize);
        return tb;
    }

    /// <summary>
    /// Text with inline code drawn as chips (Claude Code): a rounded tint behind each code run, painted under the text, so
    /// the code stays part of the selectable text (copying a sentence keeps its <c>code</c>). The code runs are the ones
    /// at <see cref="CodeSize"/>, which only inline code has in a block of text.
    /// </summary>
    private sealed class MdText : SelectableTextBlock
    {
        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

        public double CodeSize { get; init; }

        protected override void RenderTextLayout(DrawingContext context, Point origin)
        {
            if (HasCode && Brush("GuiInlineCodeBg") is { } fill)
            {
                var height = Math.Round(CodeSize * 1.5);
                var y = origin.Y;
                foreach (var line in TextLayout.TextLines)
                {
                    var x = origin.X + line.Start;
                    foreach (var run in line.TextRuns)
                    {
                        if (run is not DrawableTextRun drawable) continue;
                        var width = drawable.Size.Width;
                        if (run is ShapedTextRun shaped && Math.Abs(shaped.Properties.FontRenderingEmSize - CodeSize) < 0.01 && width > 0)
                        {
                            // Centred on the line's text, a little wider than the code (the spaces around it take the padding)
                            var top = y + Math.Round((line.Height - height) / 2) + 1;
                            context.DrawRectangle(fill, null, new RoundedRect(new Rect(x - 3, top, width + 6, height), 4));
                        }
                        x += width;
                    }
                    y += line.Height;
                }
            }
            base.RenderTextLayout(context, origin);
        }

        private bool HasCode => Inlines is { Count: > 0 } inlines && inlines.Any(IsCode);

        private bool IsCode(Avalonia.Controls.Documents.Inline inline) => inline switch
        {
            Run r => r.FontSize is var s && Math.Abs(s - CodeSize) < 0.01 && r.IsSet(TextElement.FontSizeProperty),
            Span span => span.Inlines.Any(IsCode),
            _ => false,
        };

        // ── Paths (PathRun): the pointer resting on one previews it, a click (not a drag) opens it ──

        private static Avalonia.Input.Cursor? HandCursor;
        private PathRun? _hovered;
        private PathTarget? _target;
        private Point? _pressedAt;

        protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; // selecting
            Hover(PathAt(e.GetPosition(this)));
        }

        protected override void OnPointerExited(Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerExited(e);
            Hover(null);
        }

        protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
        {
            _pressedAt = _target is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e.GetPosition(this) : null;
            base.OnPointerPressed(e);
        }

        protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            var at = e.GetPosition(this);
            // A click: released where it was pressed, nothing selected (a drag over the path selects it instead)
            if (_pressedAt is { } from && _target is { } target && Math.Abs(at.X - from.X) < 4 && Math.Abs(at.Y - from.Y) < 4
                && SelectionStart == SelectionEnd && PathLinks.GetHandler(this) is { } handler)
            {
                ToolTip.SetIsOpen(this, false);
                handler.OpenPath(target);
                e.Handled = true;
            }
            _pressedAt = null;
        }

        private void Hover(PathRun? run)
        {
            if (ReferenceEquals(run, _hovered)) return;
            if (_hovered is { IsLink: false }) _hovered.TextDecorations = null;
            _hovered = run;
            var handler = PathLinks.GetHandler(this);
            _target = run is null || handler is null ? null : handler.ResolvePath(run.Path);
            if (_target is null)
            {
                ToolTip.SetTip(this, null);
                Cursor = null;
                return;
            }
            run!.TextDecorations = Avalonia.Media.TextDecorations.Underline;
            Cursor = HandCursor ??= new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            ToolTip.SetTip(this, new ToolTip { Classes = { "path-peek" }, Content = PathLinks.Preview(_target, handler!.ProjectRoot) });
            ToolTip.SetPlacement(this, PlacementMode.Pointer);
        }

        /// <summary>The path run under <paramref name="point"/>, by the layout's hit test and the runs' text offsets.</summary>
        private PathRun? PathAt(Point point)
        {
            if (Inlines is not { Count: > 0 } inlines || !inlines.Any(HasPath)) return null;
            var hit = TextLayout.HitTestPoint(point - new Point(Padding.Left, Padding.Top));
            if (!hit.IsInside) return null;
            var offset = 0;
            return Find(inlines, hit.TextPosition, ref offset);
        }

        private static bool HasPath(Avalonia.Controls.Documents.Inline inline) => inline switch
        {
            PathRun => true,
            Span s => s.Inlines.Any(HasPath),
            _ => false,
        };

        /// <summary>Walks the inlines in text order: a run counts its text, a line break and an embedded control one character each.</summary>
        private static PathRun? Find(InlineCollection inlines, int position, ref int offset)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case PathRun p:
                        if (position >= offset && position < offset + (p.Text?.Length ?? 0)) return p;
                        offset += p.Text?.Length ?? 0;
                        break;
                    case Run r:
                        offset += r.Text?.Length ?? 0;
                        break;
                    case Span s:
                        if (Find(s.Inlines, position, ref offset) is { } found) return found;
                        break;
                    default:
                        offset += 1; // LineBreak "\n", InlineUIContainer U+FFFC
                        break;
                }
            }
            return null;
        }
    }

    /// <summary>Text that names a path (inline code, a local link's label); <see cref="Path"/> is what is looked up.</summary>
    private sealed class PathRun(string text, string path) : Run(text)
    {
        public string Path { get; } = path;
        /// <summary>A link's label is underlined always; inline code only while the pointer is on it.</summary>
        public bool IsLink { get; init; }
    }

    private static void AddInlines(InlineCollection target, ContainerInline container, double codeSize)
    {
        foreach (var inline in container)
        {
            // A task item's text follows its checkbox with a space; the box is the list marker, so the space goes too.
            if (inline is LiteralInline lit && inline.PreviousSibling is TaskList) target.Add(new Run(lit.Content.ToString().TrimStart()));
            else target.Add(InlineOf(inline, codeSize));
        }
    }

    private static Avalonia.Controls.Documents.Inline InlineOf(MdInline inline, double codeSize)
    {
        switch (inline)
        {
            case LiteralInline lit:
                return new Run(lit.Content.ToString());
            case TaskList:
                return new Run(""); // shown as the list marker
            case EmphasisInline em:
            {
                var span = em.DelimiterChar == '~' ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : em.DelimiterCount >= 2 ? new Bold() : (Span)new Italic();
                AddInlines(span.Inlines, em, codeSize);
                return span;
            }
            case CodeInline code:
            {
                // Inline code: mono at 0.875 of the text around it; MdText draws its chip. A narrow no-break space on
                // each side keeps the chip's padding clear of the words around it (a plain space alone is eaten by it).
                // One that looks like a path is looked up when the pointer rests on it (MdText, PathLinks).
                var run = PathLinks.LooksLikePath(code.Content) ? new PathRun(code.Content, code.Content) : new Run(code.Content);
                run.FontFamily = Mono;
                run.FontSize = codeSize;
                return new Span { Inlines = { new Run("\u202F"), run, new Run("\u202F") } };
            }
            case LineBreakInline br:
                return br.IsHard ? new LineBreak() : new Run(" ");
            case LinkInline link when !link.IsImage:
            {
                var label = string.Concat(link.Select(i => i is LiteralInline l ? l.Content.ToString() : i is CodeInline c ? c.Content : ""));
                if (label.Length == 0) label = link.Url ?? "";
                if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    return new InlineUIContainer(Link(label, uri)) { BaselineAlignment = BaselineAlignment.Baseline };
                // A link to a local file or folder ("[app.ts](src/app.ts#L12)"): previewed and opened like a path in code
                if (link.Url is { } url && PathLinks.LooksLikePath(Uri.UnescapeDataString(url)))
                    return new PathRun(label, Uri.UnescapeDataString(url)) { IsLink = true, TextDecorations = TextDecorations.Underline };
                return new Run(label) { TextDecorations = TextDecorations.Underline };
            }
            case LinkInline image:
                return new Run($"[image: {image.Url}]");
            case AutolinkInline auto when Uri.TryCreate(auto.Url, UriKind.Absolute, out var au) && au.Scheme is "https" or "http":
                return new InlineUIContainer(Link(auto.Url, au)) { BaselineAlignment = BaselineAlignment.Baseline };
            case HtmlInline html:
                return new Run(html.Tag);
            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case ContainerInline c:
            {
                var span = new Span();
                AddInlines(span.Inlines, c, codeSize);
                return span;
            }
            default:
                return new Run(inline.ToString() ?? "");
        }
    }

    /// <summary>A link the size of the surrounding text in the action color (the platform opens it; http/https only); its
    /// context menu opens or copies it.</summary>
    private static HyperlinkButton Link(string label, Uri uri) => new InlineLink
    {
        Content = new TextBlock { Text = label, TextDecorations = TextDecorations.Underline },
        NavigateUri = uri,
        Padding = new Thickness(0),
        Margin = new Thickness(0),
        MinHeight = 0,
        BorderThickness = new Thickness(0),
        Classes = { "md-link" },
        ContextFlyout = ContextMenus.ForLink(uri.AbsoluteUri),
    };

    /// <summary>
    /// A HyperlinkButton that sits in a line of text: it takes the size of the text around it, and reports the
    /// baseline of its label, so the label lines up with the text instead of the button's bottom edge sitting on the
    /// baseline (which lifted links by the font's descent and clipped the line's descenders).
    /// </summary>
    private sealed class InlineLink : HyperlinkButton
    {
        protected override Type StyleKeyOverride => typeof(HyperlinkButton);

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (this.GetVisualParent() is TextBlock host) FontSize = host.FontSize;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var size = base.MeasureOverride(availableSize);
            if (Content is TextBlock { TextLayout.TextLines: [var first, ..] }) TextBlock.SetBaselineOffset(this, Padding.Top + first.Baseline);
            return size;
        }

        // A right press stays with the link: the text around it would take the pointer (to select), and the release that
        // asks for a menu would then come from the text, not the link
        protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) e.Handled = true;
        }
    }

    /// <summary>
    /// A code block on its own surface: the language top left, Copy top right (shown on hover), the code scrolling sideways;
    /// its context menu copies the code too. A block that grows (streaming) highlights only its new lines.
    /// </summary>
    private sealed class CodeBlockView
    {
        private readonly SelectableTextBlock _text = new() { Classes = { "md-code" }, TextWrapping = TextWrapping.NoWrap };
        private readonly SyntaxHighlighter.Language? _language;
        private string _code = "";
        /// <summary>Where the last line starts in the code, how many inlines come before it, and the highlighter's state
        /// there (inside a block comment or not): the lines before it are final, the last one may still grow.</summary>
        private int _lastLineStart;
        private int _lastLineInline;
        private bool _lastLineInBlock;

        public CodeBlockView(string code, string? language)
        {
            _language = SyntaxHighlighter.ForName(language?.Trim().Split(' ')[0]);
            // The DS copy icon, a check for a moment once copied (Claude Code)
            var glyph = new Icon { Data = Glyph("IconCopy"), Size = 14 };
            var copy = new Button
            {
                Content = glyph,
                Classes = { "icon", "md-copy" },
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(copy, "Copy the code");
            Avalonia.Automation.AutomationProperties.SetName(copy, "Copy code");
            copy.Click += (_, _) =>
            {
                CopyRequested?.Invoke(_code);
                if (TopLevel.GetTopLevel(copy)?.Clipboard is { } clipboard) _ = Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, _code);
                glyph.Data = Glyph("IconCheck");
                DispatcherTimer.RunOnce(() => glyph.Data = Glyph("IconCopy"), TimeSpan.FromSeconds(1.5));
            };
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 26 };
            header.Children.Add(new TextBlock
            {
                Text = language?.Trim().Split(' ')[0].ToLowerInvariant() ?? "",
                Classes = { "md-code-lang" },
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(copy, 1);
            header.Children.Add(copy);
            Root = new Border
            {
                Classes = { "codeblock" },
                Child = new StackPanel { Spacing = 2, Children = { header, new SideScroller { Content = _text } } },
                ContextFlyout = ContextMenus.ForCodeBlock(() => _code),
            };
            Update(code);
        }

        public Border Root { get; }

        /// <summary>Shows the block's code, coloured by its language (the info string after the fence) as in diffs and the
        /// file viewer. A selection made in it stays.</summary>
        public bool Update(string code)
        {
            var (start, end) = (_text.SelectionStart, _text.SelectionEnd);
            if (_language is null) _text.Text = code;
            else Highlight(code);
            _code = code;
            if (start != end)
            {
                _text.SelectionStart = start;
                _text.SelectionEnd = end;
            }
            return true;
        }

        private void Highlight(string code)
        {
            var inlines = _text.Inlines!;
            var from = 0;
            var inBlock = false;
            if (_code.Length > 0 && code.Length >= _lastLineStart && string.CompareOrdinal(code, 0, _code, 0, _lastLineStart) == 0)
            {
                // The lines before the last are as they were: the last line on is coloured again
                while (inlines.Count > _lastLineInline) inlines.RemoveAt(inlines.Count - 1);
                (from, inBlock) = (_lastLineStart, _lastLineInBlock);
            }
            else inlines.Clear();
            for (var lineStart = from; ; )
            {
                if (lineStart > from) inlines.Add(new LineBreak());
                var lineEnd = code.IndexOf('\n', lineStart);
                if (lineEnd < 0) (_lastLineStart, _lastLineInline, _lastLineInBlock) = (lineStart, inlines.Count, inBlock);
                SyntaxHighlighter.AddRuns(inlines, code[lineStart..(lineEnd < 0 ? code.Length : lineEnd)].TrimEnd('\r'), _language, ref inBlock);
                if (lineEnd < 0) break;
                lineStart = lineEnd + 1;
            }
        }
    }

    private static Control List(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 6 };
        var n = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>()) panel.Children.Add(ListRow(list, item, ref n));
        return panel;
    }

    /// <summary>A list item: a light marker in a short column (bullets 18 px, numbers as wide as they need, at least 22; a
    /// task's box), its blocks beside it. Numbers count the items that are not tasks.</summary>
    private static Grid ListRow(ListBlock list, ListItemBlock item, ref int n)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var task = item.Descendants<TaskList>().FirstOrDefault();
        if (task is null)
            row.Children.Add(list.IsOrdered
                ? new TextBlock { Text = $"{n++}.", Classes = { "body", "md-marker" }, MinWidth = 22, Margin = new Thickness(0, 0, 4, 0) }
                : new TextBlock { Text = "•", Classes = { "body", "md-marker" }, Width = 18, Margin = new Thickness(2, 0, 0, 0) });
        else
            row.Children.Add(CheckBox(task.Checked));
        var content = Stack(item);
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        return row;
    }

    /// <summary>A top-level list that grows (streaming): the rows of the items whose text did not change are kept, only a
    /// changed or new item (the last, while it is written) is built.</summary>
    private sealed class ListView
    {
        private readonly bool _ordered;
        private readonly char _bullet;
        private readonly string? _orderedStart;
        private readonly List<(int Number, string Source)> _items = [];

        public ListView(ListBlock list, string text, int from, int end)
        {
            (_ordered, _bullet, _orderedStart) = (list.IsOrdered, list.BulletType, list.OrderedStart);
            Update(list, text, from, end);
        }

        public StackPanel Root { get; } = new() { Spacing = 6 };

        /// <summary>Takes the list's next version (its text ending at <paramref name="end"/>); false for another kind of
        /// list (built again).</summary>
        public bool Update(ListBlock list, string text, int from, int end)
        {
            if (list.IsOrdered != _ordered || list.BulletType != _bullet || list.OrderedStart != _orderedStart) return false;
            var n = int.TryParse(list.OrderedStart, out var start) ? start : 1;
            var items = list.OfType<ListItemBlock>().ToList();
            for (var i = 0; i < items.Count; i++)
            {
                // An item's text: from its line to the next item's (the last one's to the list's end), as for the blocks
                var at = LineStart(text, from + items[i].Span.Start);
                var until = i + 1 < items.Count ? LineStart(text, from + items[i + 1].Span.Start) : end;
                var key = (n, text[at..Math.Max(at, until)].TrimEnd());
                if (i < _items.Count && _items[i] == key)
                {
                    if (_ordered && items[i].Descendants<TaskList>().FirstOrDefault() is null) n++;
                    continue;
                }
                var row = ListRow(list, items[i], ref n);
                if (i < _items.Count)
                {
                    _items[i] = key;
                    Root.Children[i] = row;
                }
                else
                {
                    _items.Add(key);
                    Root.Children.Add(row);
                }
            }
            if (_items.Count > items.Count)
            {
                _items.RemoveRange(items.Count, _items.Count - items.Count);
                Root.Children.RemoveRange(items.Count, Root.Children.Count - items.Count);
            }
            return true;
        }
    }

    /// <summary>A task list item's box, drawn like the DS checkbox (a rounded square; checked: filled with a check),
    /// centred on the first 24 px line — not the ☑ / ☐ glyphs, which every font draws differently.</summary>
    private static Control CheckBox(bool isChecked)
    {
        var box = new Border { Classes = { "md-check" }, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        if (isChecked)
        {
            box.Classes.Add("checked");
            box.Child = new Icon { Data = Glyph("IconCheck"), Size = 12, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        Avalonia.Automation.AutomationProperties.SetName(box, isChecked ? "done" : "not done");
        return new Panel { Height = 24, Width = 28, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Children = { box } };
    }

    private static Geometry? Glyph(string key) =>
        Application.Current?.TryFindResource(key, out var g) == true ? g as Geometry : null;

    /// <summary>A 3 px rounded bar in the strong border color beside the quoted blocks, which read in secondary text.</summary>
    private static Control Quote(QuoteBlock quote)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Classes = { "md-quote" } };
        grid.Children.Add(new Border { Classes = { "md-quote-bar" } });
        var content = Stack(quote);
        content.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>
    /// A rounded, hairline-framed table with the header row on fill 1 and pale hairlines between cells. The grid
    /// holds exactly one Border per cell; the frame is a Border around the grid and the header fill is on its cells.
    /// </summary>
    private static Control TableControl(Table table)
    {
        var grid = new Grid { Classes = { "md-table" } };
        var rows = table.OfType<TableRow>().ToList();
        var columns = rows.Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var cells = row.OfType<TableCell>().ToList();
            var textClass = row.IsHeader ? "md-th-text" : "md-td-text";
            for (var c = 0; c < columns; c++)
            {
                // A short row is filled up with empty cells, so the hairlines and the header fill run the full width.
                var content = c < cells.Count && cells[c].FirstOrDefault() is ParagraphBlock p
                    ? Text(p.Inline, weight: row.IsHeader ? FontWeight.SemiBold : null, textClass: textClass)
                    : new SelectableTextBlock { Classes = { textClass } };
                var border = new Border
                {
                    Classes = { "md-cell" },
                    BorderThickness = new Thickness(0, 0, c < columns - 1 ? 1 : 0, r < rows.Count - 1 ? 1 : 0),
                    Child = content,
                };
                if (row.IsHeader) border.Classes.Add("md-th");
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return new FittedTable(grid, columns)
        {
            Child = new SideScroller
            {
                Content = new Border { Classes = { "md-table-frame" }, Child = grid, HorizontalAlignment = HorizontalAlignment.Left },
            },
        };
    }

    /// <summary>
    /// Fits a table to the reply's width the way a browser's automatic table layout does: each column gets at least its
    /// longest word and at most its unwrapped text, the room between shared by how much each column would like to grow,
    /// so cells wrap instead of the table running off the side. Only a table whose words alone are wider than the reply
    /// scrolls sideways (the <see cref="SideScroller"/> inside).
    /// </summary>
    private sealed class FittedTable(Grid grid, int columns) : Decorator
    {
        /// <summary>The frame's hairline on both sides.</summary>
        private const double Frame = 2;
        private double _fittedFor = double.NaN;

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = availableSize.Width;
            if (double.IsFinite(width) && !width.Equals(_fittedFor))
            {
                _fittedFor = width;
                Fit(width - Frame);
            }
            return base.MeasureOverride(availableSize);
        }

        private void Fit(double room)
        {
            var min = new double[columns];
            var max = new double[columns];
            foreach (var cell in grid.Children)
            {
                var c = Grid.GetColumn(cell);
                cell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                max[c] = Math.Max(max[c], cell.DesiredSize.Width);
                cell.Measure(new Size(0, double.PositiveInfinity)); // wrapped at every word: the longest word
                min[c] = Math.Max(min[c], cell.DesiredSize.Width);
            }
            var (sumMin, sumMax) = (min.Sum(), max.Sum());
            // All fits unwrapped: natural widths. Not even the words fit: the words' widths, and the table scrolls.
            var share = sumMax <= room || sumMin >= room || sumMax <= sumMin ? (sumMax <= room ? 1 : 0) : (room - sumMin) / (sumMax - sumMin);
            // Natural widths round up (a pixel short would wrap the last word); shared ones down, to stay within the room
            for (var c = 0; c < columns; c++)
            {
                var w = min[c] + (max[c] - min[c]) * share;
                grid.ColumnDefinitions[c].Width = new GridLength(share >= 1 ? Math.Ceiling(w) : Math.Floor(w), GridUnitType.Pixel);
            }
        }
    }

    /// <summary>The design system's code font (GuiFontMono), with the same list as a fallback.</summary>
    private static FontFamily Mono =>
        Application.Current?.TryFindResource("GuiFontMono", out var v) == true && v is FontFamily f ? f : FallbackMono;

    private static readonly FontFamily FallbackMono = new("SF Mono, Menlo, Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");

    private static IBrush? Brush(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) == true ? v as IBrush : null;
}
