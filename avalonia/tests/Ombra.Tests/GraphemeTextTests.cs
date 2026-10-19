using System.Globalization;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class GraphemeTextTests
{
    [Theory]
    [InlineData("")]
    [InlineData("plain ASCII")]
    [InlineData("😀e\u0301👩‍💻 end")]
    [InlineData("🇫🇷🇫🇮🇺")]
    [InlineData("\u0600a\u0301\u0600😀end")]
    [InlineData("before\r\n\u0600a\u0301👩‍💻🇫🇷🇫🇮\r\nafter")]
    [InlineData("x\n\u0301\u0300\n👩‍💻\n🇫🇷🇫🇮🇺")]
    public void Budgets_round_inward_to_reference_grapheme_boundaries(string text)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToArray();
        for (var max = 0; max <= text.Length + 1; max++)
        {
            Assert.Equal(boundaries.Last(b => b <= max), GraphemeText.PrefixLength(text, max));
            Assert.Equal(boundaries.First(b => b >= text.Length - max), GraphemeText.SuffixStart(text, max));
        }
    }

    [Theory]
    [InlineData("abc\u0600 ", 3)]
    [InlineData("abc  ", 3)]
    [InlineData("abc\r\n", 3)]
    [InlineData("abc \u0301", 5)]
    public void Trailing_whitespace_trim_never_leaves_a_partial_cluster(string text, int expected)
    {
        Assert.Equal(expected, GraphemeText.PrefixLength(text, text.Length, trimEnd: true));
    }

    [Fact]
    public void Large_multiline_output_keeps_the_same_suffix_when_more_discarded_lines_arrive()
    {
        var prefix = string.Concat(Enumerable.Repeat("discarded 👩‍💻 e\u0301\r\n", 20_000));
        const string lastLine = "\u0600a\u0301 🇫🇷🇫🇮 👩‍💻 tail";
        var boundaries = StringInfo.ParseCombiningCharacters(lastLine).Append(lastLine.Length).ToArray();
        foreach (var earlier in new[] { prefix, prefix + prefix })
        {
            var text = earlier + lastLine;
            for (var max = 0; max < lastLine.Length; max++)
            {
                var expected = boundaries.First(b => b >= lastLine.Length - max);
                var start = GraphemeText.SuffixStart(text, max);
                Assert.Equal(earlier.Length + expected, start);
                Assert.Equal(lastLine[expected..], text[start..]);
            }
        }
    }
}
