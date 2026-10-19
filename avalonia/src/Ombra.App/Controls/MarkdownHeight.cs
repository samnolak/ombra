using Avalonia;
using Avalonia.Controls;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace Ombra.App.Controls;

/// <summary>
/// About how tall a <see cref="MarkdownView"/> lays a Markdown text out at a width, without parsing or building it: one
/// pass over its lines with the sizes the view builds its blocks at (paragraphs wrapped by the average width of a
/// character, headings, code block lines, list items, table rows, quotes, 12 px between blocks). It is for the
/// transcript's rows not laid out yet (<see cref="TranscriptPanel.EstimateHeight"/>): within a few percent for ordinary
/// replies, from the text alone, in microseconds.
/// </summary>
internal static class MarkdownHeight
{
    /// <summary>The average advance of a character of the message font, in ems (measured on replies: about 84
    /// characters a line in the 768 px column at 16 px).</summary>
    private const double AverageCharWidth = 0.57;
    /// <summary>A code block's frame: its header row (language, Copy) and hairline, the padding around the code, the
    /// card's border.</summary>
    private const double CodeChrome = 56;
    private const double CodeLine = 20;
    /// <summary>A table's frame and one row of it (a line of cell text and the cell's padding).</summary>
    private const double TableChrome = 2;
    private const double TableRow = 37;
    private const double BlockSpacing = 12;
    private const double ItemSpacing = 6;
    private const double NestedSpacing = 8;
    /// <summary>A list item's text starts after its marker, a quote's after its bar: a few characters less a line.</summary>
    private const double MarkerChars = 3;

    private enum Open { None, Paragraph, List, Table, Quote }

    /// <summary>The height <paramref name="markdown"/> takes at <paramref name="width"/>; <paramref name="atTop"/>: it starts
    /// the view (a heading there has no space above it), else it is a block further down. An expanded code placeholder
    /// retains its full height when its controls are rebuilt for a theme change.</summary>
    public static double Estimate(string markdown, double width, bool atTop = true, bool expandedCode = false) =>
        Estimate(markdown.AsSpan(), width, atTop, expandedCode);

    /// <summary>A parsed lazy block, including each descendant code block's own fold state. Container markers are
    /// layout chrome, not code text: walking the existing tree avoids counting a quoted or listed hidden tail as prose.</summary>
    internal static double Estimate(Block block, string source, int from, double width, bool atTop, Func<CodeBlock, bool> expanded, int? end = null) =>
        Estimate(block, source, from, width, atTop, expanded, end ?? source.Length, quotes: 0);

    private static double Estimate(Block block, string source, int from, double width, bool atTop, Func<CodeBlock, bool> expanded, int until, int quotes)
    {
        if (block is CodeBlock code)
        {
            var lines = code.Lines.Count;
            while (lines > 0 && code.Lines.Lines[lines - 1].Slice.Length == 0) lines--;
            return CodeHeight(lines, expanded(code));
        }
        if (block is ContainerBlock container && block is not Table)
        {
            var spacing = block is ListBlock ? ItemSpacing : NestedSpacing;
            var childWidth = Math.Max(1, width - (block is ListBlock ? 28 : block is QuoteBlock ? 15 : 0));
            var height = 0.0;
            var first = true;
            for (var i = 0; i < container.Count; i++)
            {
                if (!first) height += spacing;
                var childEnd = i + 1 < container.Count ? from + container[i + 1].Span.Start : until;
                height += Estimate(container[i], source, from, childWidth, atTop: first, expanded, childEnd, quotes + (block is QuoteBlock ? 1 : 0));
                first = false;
            }
            return block is ListItemBlock ? Math.Max(Metrics().LineHeight, height) : height;
        }
        // Open EOF blocks can have a stale Span.End. Bound them by their next sibling (or their container's end),
        // just as RenderBlocks does for top-level sources, rather than dropping the still-streaming tail.
        var start = from + block.Span.Start;
        var slice = source.AsSpan(start, until - start);
        if (quotes == 0) return Estimate(slice, width, atTop, expandedCode: false);
        // Every continuation line still carries the enclosing quotes' markers, which the raw estimator would read as
        // separate quote blocks (Markdig has already released the leaf's marker-free lines after inline parsing).
        var text = new System.Text.StringBuilder(slice.Length);
        foreach (var line in slice.EnumerateLines())
        {
            var rest = line;
            for (var q = 0; q < quotes; q++)
            {
                var trimmed = rest.TrimStart();
                if (trimmed.IsEmpty || trimmed[0] != '>') break;
                rest = trimmed[1..];
                if (!rest.IsEmpty && rest[0] == ' ') rest = rest[1..];
            }
            if (text.Length > 0) text.Append('\n');
            text.Append(rest);
        }
        return Estimate(text.ToString().AsSpan(), width, atTop, expandedCode: false);
    }

    private static double Estimate(ReadOnlySpan<char> markdown, double width, bool atTop, bool expandedCode)
    {
        var (fontSize, lineHeight) = Metrics();
        var perLine = Math.Max(8, width / (fontSize * AverageCharWidth));
        var total = 0.0;
        var blocks = 0;
        var open = Open.None;
        var chars = 0;      // the open paragraph's or quote's text
        var height = 0.0;   // the open list's or table's height so far
        var codeLines = -1; // the open code block's lines (-1: not in one)
        var codeTrailingBlanks = 0;
        var indentedCodeLines = -1;
        var indentedBlanks = 0;
        var listIndent = 0;
        var codeQuoteDepth = 0;

        void Add(double h)
        {
            total += h + (blocks > 0 ? BlockSpacing : 0);
            blocks++;
        }

        void Close()
        {
            switch (open)
            {
                case Open.Paragraph: Add(Lines(chars, perLine) * lineHeight); break;
                case Open.Quote: Add(Lines(chars, perLine - MarkerChars) * lineHeight); break;
                case Open.List or Open.Table: Add(height); break;
            }
            (open, chars, height) = (Open.None, 0, 0);
        }

        foreach (var raw in markdown.EnumerateLines())
        {
            var indent = raw.Length - raw.TrimStart().Length;
            var line = raw.Trim();
            var codeRaw = CodeContent(raw, ref listIndent, ref codeQuoteDepth, codeLines >= 0 || indentedCodeLines >= 0);
            var codeIndent = codeRaw.Length - codeRaw.TrimStart().Length;
            var codeLine = codeRaw.Trim();
            if (indentedCodeLines >= 0)
            {
                if (codeLine.IsEmpty) { indentedBlanks++; continue; }
                if (codeIndent >= 4)
                {
                    indentedCodeLines += indentedBlanks + 1;
                    indentedBlanks = 0;
                    continue;
                }
                Add(CodeHeight(indentedCodeLines, expandedCode));
                indentedCodeLines = -1;
                indentedBlanks = 0;
            }
            if (codeLines >= 0)
            {
                if (IsFence(codeLine)) { Add(CodeHeight(codeLines - codeTrailingBlanks, expandedCode)); codeLines = -1; }
                else
                {
                    codeLines++;
                    codeTrailingBlanks = codeRaw.IsEmpty ? codeTrailingBlanks + 1 : 0;
                }
                continue;
            }
            if (codeIndent <= 3 && IsFence(codeLine))
            {
                Close();
                codeLines = 0;
                codeTrailingBlanks = 0;
                continue;
            }
            if (line.IsEmpty)
            {
                // A list goes on past a blank line when an item or its indented text follows
                if (open != Open.List) Close();
                continue;
            }
            if (codeIndent >= 4 && open == Open.None)
            {
                indentedCodeLines = 1;
                continue;
            }
            if (indent <= 3 && HeadingLevel(line) is var level and > 0)
            {
                Close();
                var (size, above) = level switch { 1 => (30.0, 10.0), 2 => (26.0, 8.0), _ => (24.0, 4.0) };
                Add(size + (blocks > 0 || !atTop ? above : 0));
                continue;
            }
            if (ItemMarker(line) is var marker and > 0)
            {
                if (open != Open.List) Close();
                var spacing = height > 0 ? indent >= 2 ? NestedSpacing : ItemSpacing : 0;
                open = Open.List;
                height += spacing + Lines(line.Length - marker, perLine - MarkerChars) * lineHeight;
                continue;
            }
            if (line[0] == '|')
            {
                if (open != Open.Table) Close();
                open = Open.Table;
                if (height == 0) height = TableChrome;
                if (!IsTableRule(line))
                {
                    if (chars < MarkdownView.MaxTableRows) height += TableRow;
                    else if (chars == MarkdownView.MaxTableRows) height += 64; // truncation notice and copy action
                    chars++;
                }
                continue;
            }
            if (line[0] == '>')
            {
                if (open != Open.Quote) Close();
                open = Open.Quote;
                chars += line.Length + 1;
                continue;
            }
            if (open == Open.List && indent >= 2)
            {
                // An item's own text on its next line
                height += Lines(line.Length, perLine - MarkerChars) * lineHeight;
                continue;
            }
            if (open != Open.Paragraph) Close();
            open = Open.Paragraph;
            chars += line.Length + 1; // lines of a paragraph join with a space
        }
        if (codeLines >= 0) Add(CodeHeight(codeLines - codeTrailingBlanks, expandedCode));
        if (indentedCodeLines >= 0) Add(CodeHeight(indentedCodeLines, expandedCode));
        Close();
        return total;
    }

    /// <summary>Removes only container prefixes for recognizing code. Prose keeps the original line estimator;
    /// code contents never establish list markers or deeper quotes. No strings or syntax trees are allocated.</summary>
    private static ReadOnlySpan<char> CodeContent(ReadOnlySpan<char> line, ref int listIndent, ref int quoteDepth, bool inCode)
    {
        var depth = 0;
        var markerIndent = 0;
        var hasMarker = false;
        while (true)
        {
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;
            if (indent <= 3 && trimmed.StartsWith(">") && (!inCode || depth < quoteDepth))
            {
                line = trimmed[1..];
                if (!line.IsEmpty && line[0] == ' ') line = line[1..];
                depth++;
                markerIndent = 0;
                hasMarker = false;
                continue;
            }
            var marker = inCode ? 0 : ItemMarker(trimmed);
            if (marker > 0 && indent <= listIndent + 3)
            {
                markerIndent += indent + marker;
                line = trimmed[marker..];
                hasMarker = true;
                continue;
            }
            break;
        }
        if (!inCode)
        {
            if (depth != quoteDepth) listIndent = 0;
            quoteDepth = depth;
            if (hasMarker)
            {
                listIndent = markerIndent;
                return line;
            }
        }
        var remainingIndent = line.Length - line.TrimStart().Length;
        if (listIndent > 0 && remainingIndent >= listIndent) return line[listIndent..];
        if (!inCode && !line.IsWhiteSpace()) listIndent = 0;
        return line;
    }

    /// <summary>The message text's size and line height (GuiFontMessage, GuiLineMessage).</summary>
    private static (double FontSize, double LineHeight) Metrics()
    {
        var app = Application.Current;
        var size = app?.TryFindResource("GuiFontMessage", out var s) == true && s is double ds ? ds : 16;
        var line = app?.TryFindResource("GuiLineMessage", out var l) == true && l is double dl ? dl : 24;
        return (size, line);
    }

    private static int Lines(double chars, double perLine) => Math.Max(1, (int)Math.Ceiling(chars / Math.Max(1, perLine)));

    private static double CodeHeight(int lines, bool expanded) =>
        CodeChrome + (lines > MarkdownView.CodeFoldThreshold ? (expanded ? lines : MarkdownView.CodePreviewLines) * CodeLine + 36 : lines * CodeLine);

    private static bool IsFence(ReadOnlySpan<char> line) => line.StartsWith("```") || line.StartsWith("~~~");

    /// <summary>1–6 for an ATX heading ("## Title"), else 0.</summary>
    private static int HeadingLevel(ReadOnlySpan<char> line)
    {
        var n = 0;
        while (n < line.Length && line[n] == '#') n++;
        return n is >= 1 and <= 6 && (n == line.Length || line[n] == ' ') ? n : 0;
    }

    /// <summary>The length of a list item's marker with the space after it ("- ", "12. "), else 0.</summary>
    private static int ItemMarker(ReadOnlySpan<char> line)
    {
        if (line.Length >= 2 && line[0] is '-' or '*' or '+' && line[1] == ' ') return 2;
        var digits = 0;
        while (digits < line.Length && digits < 9 && char.IsAsciiDigit(line[digits])) digits++;
        return digits > 0 && digits + 1 < line.Length && line[digits] is '.' or ')' && line[digits + 1] == ' ' ? digits + 2 : 0;
    }

    /// <summary>The line under a table's header ("|---|:--:|").</summary>
    private static bool IsTableRule(ReadOnlySpan<char> line)
    {
        foreach (var c in line)
            if (c is not ('|' or '-' or ':' or ' ')) return false;
        return line.Contains("---", StringComparison.Ordinal);
    }
}
