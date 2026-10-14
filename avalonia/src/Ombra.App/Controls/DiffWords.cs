using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Ombra.App.Controls;

/// <summary>
/// What changed inside a changed line (GitHub's and Codex's word highlight): a run of removed lines directly followed by
/// added lines is paired line by line (the first removed with the first added…; the rest of the longer side stays
/// unpaired), and each pair is compared word by word. The words only one side has are marked, so a line that changed
/// one argument shows that argument, not the whole line. Pairs too long or too different are left unmarked: there the
/// marks would cover nearly everything and say nothing.
/// </summary>
public static class DiffWords
{
    /// <summary>A marked part of a line: characters [Start, Start + Length).</summary>
    public readonly record struct Span(int Start, int Length);

    /// <summary>Lines longer than this are not compared (a minified file's one line).</summary>
    public const int MaxLineLength = 500;

    /// <summary>Token pairs the comparison may look at (the middle of two lines, after their common start and end).</summary>
    private const int MaxCells = 40_000;

    /// <summary>Below this share of the shorter line's characters kept, the two are different lines, not one line changed.</summary>
    private const double MinSimilarity = 0.5;

    /// <summary>
    /// The marks of every row: <paramref name="sign"/> says whether row i was removed (−1), added (+1) or neither (0).
    /// A row without marks has null.
    /// </summary>
    public static IReadOnlyList<Span>?[] Mark(int count, Func<int, int> sign, Func<int, string> text)
    {
        var marks = new IReadOnlyList<Span>?[count];
        var i = 0;
        while (i < count)
        {
            if (sign(i) != -1) { i++; continue; }
            var removedStart = i;
            while (i < count && sign(i) == -1) i++;
            var addedStart = i;
            while (i < count && sign(i) == 1) i++;
            var pairs = Math.Min(addedStart - removedStart, i - addedStart);
            for (var p = 0; p < pairs; p++)
                if (Compare(text(removedStart + p), text(addedStart + p)) is var (old, @new))
                {
                    marks[removedStart + p] = old;
                    marks[addedStart + p] = @new;
                }
        }
        return marks;
    }

    /// <summary>The changed parts of a line before and after; null when the lines are equal, too long or too different.</summary>
    public static (IReadOnlyList<Span> Old, IReadOnlyList<Span> New)? Compare(string before, string after)
    {
        if (before == after || before.Length > MaxLineLength || after.Length > MaxLineLength) return null;
        var a = Tokens(before);
        var b = Tokens(after);
        // The common start and end are unchanged; only the middle is compared token by token
        var head = 0;
        while (head < a.Count && head < b.Count && Same(a[head], b[head], before, after)) head++;
        var tail = 0;
        while (tail < a.Count - head && tail < b.Count - head && Same(a[^(tail + 1)], b[^(tail + 1)], before, after)) tail++;
        var aMid = a.GetRange(head, a.Count - head - tail);
        var bMid = b.GetRange(head, b.Count - head - tail);
        var keepA = new bool[aMid.Count];
        var keepB = new bool[bMid.Count];
        if ((long)aMid.Count * bMid.Count <= MaxCells) Lcs(aMid, bMid, before, after, keepA, keepB);
        // How much of the line stayed, in visible characters (indentation says nothing about it)
        var common = 0;
        for (var k = 0; k < head; k++) common += Visible(a[k], before);
        for (var k = 0; k < tail; k++) common += Visible(a[^(k + 1)], before);
        for (var k = 0; k < aMid.Count; k++) if (keepA[k]) common += Visible(aMid[k], before);
        // Against the shorter line: words inserted into a line keep all of it (one marked insertion, not a new line)
        var shorter = Math.Min(before.Count(c => !char.IsWhiteSpace(c)), after.Count(c => !char.IsWhiteSpace(c)));
        if (shorter == 0 || (double)common / shorter < MinSimilarity) return null;
        var old = Spans(aMid, keepA);
        var @new = Spans(bMid, keepB);
        if (old.Count == 0 && @new.Count == 0) return null; // only whitespace moved
        return (old, @new);
    }

    /// <summary>A line as words (letters, digits, '_'), runs of spaces, and single other characters.</summary>
    private static List<Span> Tokens(string s)
    {
        var list = new List<Span>();
        var i = 0;
        while (i < s.Length)
        {
            var start = i;
            if (IsWord(s[i])) while (i < s.Length && IsWord(s[i])) i++;
            else if (char.IsWhiteSpace(s[i])) while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            else i++;
            list.Add(new Span(start, i - start));
        }
        return list;

        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    private static bool Same(Span x, Span y, string a, string b) =>
        x.Length == y.Length && a.AsSpan(x.Start, x.Length).SequenceEqual(b.AsSpan(y.Start, y.Length));

    private static int Visible(Span t, string s) => char.IsWhiteSpace(s[t.Start]) ? 0 : t.Length;

    /// <summary>Marks the tokens of the longest common subsequence as kept.</summary>
    private static void Lcs(List<Span> a, List<Span> b, string sa, string sb, bool[] keepA, bool[] keepB)
    {
        var n = a.Count;
        var m = b.Count;
        var len = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                len[i, j] = Same(a[i], b[j], sa, sb) ? len[i + 1, j + 1] + 1 : Math.Max(len[i + 1, j], len[i, j + 1]);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (Same(a[x], b[y], sa, sb)) { keepA[x++] = true; keepB[y++] = true; }
            else if (len[x + 1, y] >= len[x, y + 1]) x++;
            else y++;
        }
    }

    /// <summary>The changed tokens as character spans, neighbours joined (a space between two changed words too).</summary>
    private static List<Span> Spans(List<Span> tokens, bool[] keep)
    {
        var spans = new List<Span>();
        for (var k = 0; k < tokens.Count; k++)
        {
            if (keep[k]) continue;
            var t = tokens[k];
            if (spans.Count > 0 && spans[^1].Start + spans[^1].Length == t.Start)
                spans[^1] = spans[^1] with { Length = spans[^1].Length + t.Length };
            else spans.Add(t);
        }
        return spans;
    }

    /// <summary>
    /// Appends a line's runs: coloured by its language (SyntaxHighlighter), and the marked parts on
    /// <paramref name="markBrush"/>.
    /// </summary>
    public static void AddRuns(InlineCollection inlines, string line, SyntaxHighlighter.Language? lang, ref bool inBlock, IReadOnlyList<Span>? marks, IBrush? markBrush)
    {
        if (marks is not { Count: > 0 } || markBrush is null)
        {
            SyntaxHighlighter.AddRuns(inlines, line, lang, ref inBlock);
            return;
        }
        var at = 0;
        var m = 0;
        foreach (var (kind, text) in SyntaxHighlighter.Tokenize(line, lang, ref inBlock))
        {
            var brush = SyntaxHighlighter.BrushFor(kind);
            var offset = 0;
            while (offset < text.Length)
            {
                var pos = at + offset;
                while (m < marks.Count && marks[m].Start + marks[m].Length <= pos) m++;
                var marked = m < marks.Count && marks[m].Start <= pos;
                // Up to where this token ends, or the mark starts or ends
                var end = at + text.Length;
                if (m < marks.Count) end = Math.Min(end, marked ? marks[m].Start + marks[m].Length : marks[m].Start);
                var run = new Run(text.Substring(offset, end - pos));
                if (brush is not null) run.Foreground = brush;
                if (kind == SyntaxHighlighter.Token.Comment) run.FontStyle = FontStyle.Italic;
                if (marked) run.Background = markBrush;
                inlines.Add(run);
                offset = end - at;
            }
            at += text.Length;
        }
    }
}
