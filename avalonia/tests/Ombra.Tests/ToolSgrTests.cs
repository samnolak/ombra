using Ombra.App.Controls;

namespace Ombra.Tests;

public sealed class ToolSgrTests
{
    [Theory]
    [InlineData("\u001b[mplain\u001b[0m", "plain")]
    [InlineData("\u001b[1;31mred\u001b[0m", "red")]
    [InlineData("\u001b[38;2;12;34;56mRGB\u001b[39m", "RGB")]
    [InlineData("\u001b[38:2::12:34:56mRGB\u001b[m", "RGB")]
    public void Only_complete_sgr_is_removed(string input, string expected) =>
        Assert.Equal(expected, VisibleText.FromUntrustedOutput(input, stripSgr: true));

    [Theory]
    [InlineData("\u001b[2J\u001b[Hcursor")]
    [InlineData("\u001b]0;title\u0007")]
    [InlineData("\u001b]52;c;secret\u001b\\")]
    [InlineData("\u001b[31")]
    [InlineData("\u001b[?31m")]
    [InlineData("\u0000\u0007\u009b31m\u007f\u202e\u200b\u200d")]
    public void Every_other_control_and_hidden_character_keeps_existing_safety_representation(string input)
    {
        var expected = VisibleText.FromUntrustedOutput(input);
        Assert.Equal(expected, VisibleText.FromUntrustedOutput(input, stripSgr: true));
        Assert.Equal(expected, VisibleText.FromUntrustedOutput("\u001b[31m" + input + "\u001b[0m", stripSgr: true));
    }
}
