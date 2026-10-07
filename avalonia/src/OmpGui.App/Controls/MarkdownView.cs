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
/// Only http/https links are clickable (the platform opens them); code blocks get a Copy button.
/// While <see cref="IsStreaming"/> it re-renders at most 4 times a second, and very long streaming text is shown as
/// plain text until the message is complete, so a long reply never costs a full re-render per token.
/// Looks follow the design system: control colors and sizes come from the "Markdown" styles (md-* classes in
/// Views/Styles.axaml, which follow the theme); the one inline color (code spans) is resolved when the text is built,
/// so a theme change rebuilds the view.
/// </summary>
public sealed class MarkdownView : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));
    public static readonly StyledProperty<bool> IsStreamingProperty = AvaloniaProperty.Register<MarkdownView, bool>(nameof(IsStreaming));

    private const int PlainWhileStreamingChars = 30_000;
    /// <summary>Beyond this even a finished reply stays plain text: a control tree for megabytes of Markdown costs more than it gives.</summary>
    private const int MaxMarkdownChars = 200_000;
    private static readonly TimeSpan StreamingInterval = TimeSpan.FromMilliseconds(250);
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().UseAutoLinks().UseTaskLists().Build();

    private DispatcherTimer? _throttle;
    private string? _rendered;

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
            // The inline brushes were resolved for another theme. (Only a real theme change rebuilds: the variant
            // also "changes" each time a recycled transcript row is attached.)
            _rendered = null;
            Schedule();
        }
    }

    private Avalonia.Styling.ThemeVariant? _renderedTheme;

    private static Avalonia.Styling.ThemeVariant? AppTheme => Application.Current?.ActualThemeVariant;

    private void Schedule()
    {
        if (!IsStreaming || _rendered is null)
        {
            _throttle?.Stop();
            _throttle = null;
            Render();
            return;
        }
        _throttle ??= new DispatcherTimer(StreamingInterval, DispatcherPriority.Background, (_, _) =>
        {
            _throttle?.Stop();
            _throttle = null;
            Render();
        });
        _throttle.Start();
    }

    private void Render()
    {
        var text = Markdown ?? "";
        if (text == _rendered && Content is not null) return;
        Content = text.Length > MaxMarkdownChars || (IsStreaming && text.Length > PlainWhileStreamingChars) ? Paragraph(text) : Build(text);
        _rendered = text; // only once it is on screen: a failed render is retried with the next text
        _renderedTheme = AppTheme;
    }

    /// <summary>Deeper block nesting than this is shown as plain text (each level is a nested control to lay out).</summary>
    private const int MaxNesting = 24;

    /// <summary>
    /// The control tree for a Markdown text (public for tests). Text that Markdig refuses (it throws past its own
    /// nesting limit) or that nests deeper than <see cref="MaxNesting"/> is shown as plain text instead.
    /// </summary>
    public static Control Build(string markdown)
    {
        MarkdownDocument doc;
        try { doc = Markdig.Markdown.Parse(markdown, Pipeline); }
        catch (ArgumentException) { return Paragraph(markdown); }
        if (Depth(doc) > MaxNesting) return Paragraph(markdown);
        return Blocks(doc, 12);
    }

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
        FencedCodeBlock f => CodeBlock(Lines(f), f.Info),
        CodeBlock c => CodeBlock(Lines(c), null),
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
    private static SelectableTextBlock Heading(HeadingBlock h)
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

    private static SelectableTextBlock Text(ContainerInline? inline, double? size = null, FontWeight? weight = null, string textClass = "body")
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
                // Inline code: mono at 0.875 of the text around it; MdText draws its chip. A narrow no-break space on
                // each side keeps the chip's padding clear of the words around it (a plain space alone is eaten by it).
                return new Span { Inlines = { new Run("\u202F"), new Run(code.Content) { FontFamily = Mono, FontSize = codeSize }, new Run("\u202F") } };
            case LineBreakInline br:
                return br.IsHard ? new LineBreak() : new Run(" ");
            case LinkInline link when !link.IsImage:
            {
                var label = string.Concat(link.Select(i => i is LiteralInline l ? l.Content.ToString() : i is CodeInline c ? c.Content : ""));
                if (label.Length == 0) label = link.Url ?? "";
                if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    return new InlineUIContainer(Link(label, uri)) { BaselineAlignment = BaselineAlignment.Baseline };
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

    /// <summary>A link the size of the surrounding text in the action color (the platform opens it; http/https only).</summary>
    private static HyperlinkButton Link(string label, Uri uri) => new InlineLink
    {
        Content = new TextBlock { Text = label, TextDecorations = TextDecorations.Underline },
        NavigateUri = uri,
        Padding = new Thickness(0),
        Margin = new Thickness(0),
        MinHeight = 0,
        BorderThickness = new Thickness(0),
        Classes = { "md-link" },
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
    }

    /// <summary>A code block on its own surface: the language top left, Copy top right (shown on hover), the code scrolling sideways.</summary>
    private static Control CodeBlock(string code, string? language)
    {
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
            CopyRequested?.Invoke(code);
            if (TopLevel.GetTopLevel(copy)?.Clipboard is { } clipboard) _ = Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, code);
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
        var body = new SideScroller
        {
            Content = Highlighted(code, language),
        };
        return new Border
        {
            Classes = { "codeblock" },
            Child = new StackPanel { Spacing = 2, Children = { header, body } },
        };
    }

    private static Control List(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 6 };
        var n = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var task = item.Descendants<TaskList>().FirstOrDefault();
            if (task is null)
                // A light marker in a short column: bullets 18 px, numbers as wide as they need (at least 22)
                row.Children.Add(list.IsOrdered
                    ? new TextBlock { Text = $"{n++}.", Classes = { "body", "md-marker" }, MinWidth = 22, Margin = new Thickness(0, 0, 4, 0) }
                    : new TextBlock { Text = "•", Classes = { "body", "md-marker" }, Width = 18, Margin = new Thickness(2, 0, 0, 0) });
            else
                row.Children.Add(CheckBox(task.Checked));
            var content = Stack(item);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>The code coloured by its language (the info string after the fence), as in diffs and the file viewer.</summary>
    private static SelectableTextBlock Highlighted(string code, string? language)
    {
        var tb = new SelectableTextBlock { Classes = { "md-code" }, TextWrapping = TextWrapping.NoWrap };
        var lang = SyntaxHighlighter.ForName(language?.Trim().Split(' ')[0]);
        if (lang is null) { tb.Text = code; return tb; }
        var inBlock = false;
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) tb.Inlines!.Add(new LineBreak());
            SyntaxHighlighter.AddRuns(tb.Inlines!, lines[i].TrimEnd('\r'), lang, ref inBlock);
        }
        return tb;
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
        return new SideScroller
        {
            Content = new Border { Classes = { "md-table-frame" }, Child = grid, HorizontalAlignment = HorizontalAlignment.Left },
        };
    }

    /// <summary>The design system's code font (GuiFontMono), with the same list as a fallback.</summary>
    private static FontFamily Mono =>
        Application.Current?.TryFindResource("GuiFontMono", out var v) == true && v is FontFamily f ? f : FallbackMono;

    private static readonly FontFamily FallbackMono = new("SF Mono, Menlo, Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");

    private static IBrush? Brush(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) == true ? v as IBrush : null;
}
