using System.Globalization;

namespace Ombra.ClientCore;

/// <summary>UTF-16 display budgets rounded inward to whole extended grapheme clusters; no normalization.</summary>
public static class GraphemeText
{
    /// <summary>The next cluster's UTF-16 length, without allocating a substring or boundary array.</summary>
    public static int ElementLength(ReadOnlySpan<char> text) => StringInfo.GetNextTextElementLength(text);

    /// <summary>The longest whole-cluster prefix within the UTF-16 budget; optional trailing whitespace
    /// trimming removes entire clusters, including any Prepend characters attached to the whitespace.</summary>
    public static int PrefixLength(string text, int max, bool trimEnd = false) =>
        PrefixLength(text.AsSpan(), max, trimEnd);

    /// <summary>Finds the same boundary in a source slice without copying the omitted text.</summary>
    public static int PrefixLength(ReadOnlySpan<char> text, int max, bool trimEnd = false)
    {
        if (!trimEnd && max >= text.Length) return text.Length;
        max = Math.Min(max, text.Length);
        var offset = 0;
        var end = 0;
        while (offset < max)
        {
            var next = offset + ElementLength(text[offset..]);
            if (next > max) break;
            offset = next;
            if (!trimEnd || !char.IsWhiteSpace(text[offset - 1])) end = offset;
        }
        return end;
    }

    /// <summary>The start of the longest whole-cluster suffix within the UTF-16 budget.</summary>
    public static int SuffixStart(string text, int max)
    {
        var target = Math.Max(0, text.Length - max);
        // LF is a hard grapheme break, including after CRLF. Restart after the nearest preceding LF
        // so streamed multiline output does not rescan its discarded prefix on every update.
        // Without that delimiter, retain the context needed by regional indicators and Prepend characters.
        var offset = text.AsSpan(0, target).LastIndexOf('\n') + 1;
        while (offset < target) offset += ElementLength(text.AsSpan(offset));
        return offset;
    }
}
