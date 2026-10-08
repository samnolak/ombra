using Avalonia;
using Avalonia.Controls;

namespace OmpGui.App.Controls;

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
    /// <summary>A code block's frame: its header (language, Copy), the space under it, the padding around the code.</summary>
    private const double CodeChrome = 48;
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
    /// the view (a heading there has no space above it), else it is a block further down.</summary>
    public static double Estimate(string markdown, double width, bool atTop = true)
    {
        var (fontSize, lineHeight) = Metrics();
        var perLine = Math.Max(8, width / (fontSize * AverageCharWidth));
        var total = 0.0;
        var blocks = 0;
        var open = Open.None;
        var chars = 0;      // the open paragraph's or quote's text
        var height = 0.0;   // the open list's or table's height so far
        var codeLines = -1; // the open code block's lines (-1: not in one)

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

        foreach (var raw in markdown.AsSpan().EnumerateLines())
        {
            var indent = raw.Length - raw.TrimStart().Length;
            var line = raw.Trim();
            if (codeLines >= 0)
            {
                if (IsFence(line)) { Add(CodeChrome + codeLines * CodeLine); codeLines = -1; }
                else codeLines++;
                continue;
            }
            if (indent <= 3 && IsFence(line))
            {
                Close();
                codeLines = 0;
                continue;
            }
            if (line.IsEmpty)
            {
                // A list goes on past a blank line when an item or its indented text follows
                if (open != Open.List) Close();
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
                if (!IsTableRule(line)) height += TableRow;
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
        if (codeLines >= 0) Add(CodeChrome + codeLines * CodeLine);
        Close();
        return total;
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
