using System.Text.Json;
using OmpGui.App.Controls;
using OmpGui.ClientCore;
using static OmpGui.App.Controls.DiffView;

namespace OmpGui.Tests;

/// <summary>File changes as omp reports them (its own code-frame lines) and as unified diffs; new files as added lines.</summary>
public sealed class DiffViewTests
{
    [Fact]
    public void Omp_code_frame_lines_become_numbered_rows_with_gaps_between_parts()
    {
        var rows = Parse("   1|def greet():\n-   2|    print('Hi')\n+   2|    print('Hello')\n   3|\n  12|def main():\n+  13|    greet()\n");
        Assert.Equal([Kind.Context, Kind.Removed, Kind.Added, Kind.Context, Kind.Gap, Kind.Context, Kind.Added], rows.Select(r => r.Kind));
        Assert.Equal([1, 2, 2, 3, null, 12, 13], rows.Select(r => r.Number));
        Assert.Equal("    print('Hello')", rows[2].Text);
        Assert.Equal((2, 1), Stats("   1|def greet():\n-   2|    print('Hi')\n+   2|    print('Hello')\n+   3|x\n"));
    }

    [Fact]
    public void Unified_diffs_number_added_lines_by_the_new_file_and_removed_by_the_old()
    {
        var rows = Parse("--- a/x.py\n+++ b/x.py\n@@ -1,3 +1,3 @@\n def greet():\n-    print('Hi')\n+    print('Hello')\n@@ -20,1 +20,2 @@\n end\n+more\n");
        Assert.Equal([Kind.Context, Kind.Removed, Kind.Added, Kind.Gap, Kind.Context, Kind.Added], rows.Select(r => r.Kind));
        Assert.Equal([1, 2, 2, null, 20, 21], rows.Select(r => r.Number));
    }

    [Fact]
    public void A_written_file_is_its_content_as_added_lines()
    {
        var args = JsonDocument.Parse("""{"path":"a.py","content":"x = 1\ny = 2\n"}""").RootElement;
        var diff = ConversationState.WrittenContent("write", args)!;
        var rows = Parse(diff);
        Assert.Equal([Kind.Added, Kind.Added], rows.Select(r => r.Kind));
        Assert.Equal(("x = 1", 1), (rows[0].Text, rows[0].Number));
        Assert.Null(ConversationState.WrittenContent("read", args));
    }

    [Fact]
    public void Code_is_coloured_by_its_language()
    {
        var inBlock = false;
        var py = SyntaxHighlighter.Tokenize("def greet(name):  # say hi", SyntaxHighlighter.ForName("hello.py"), ref inBlock);
        Assert.Contains(py, t => t.Kind == SyntaxHighlighter.Token.Keyword && t.Text == "def");
        Assert.Contains(py, t => t.Kind == SyntaxHighlighter.Token.Function && t.Text == "greet");
        Assert.Contains(py, t => t.Kind == SyntaxHighlighter.Token.Comment && t.Text == "# say hi");
        var cs = SyntaxHighlighter.Tokenize("var s = \"a\"; /* open", SyntaxHighlighter.ForName("A.cs"), ref inBlock);
        Assert.Contains(cs, t => t.Kind == SyntaxHighlighter.Token.String && t.Text == "\"a\"");
        Assert.True(inBlock); // the block comment goes on to the next line
        Assert.Equal(SyntaxHighlighter.Token.Comment, SyntaxHighlighter.Tokenize("still */ int x", SyntaxHighlighter.ForName("A.cs"), ref inBlock)[0].Kind);
        Assert.False(inBlock);
        Assert.Equal("still */ int x", string.Concat(SyntaxHighlighter.Tokenize("still */ int x", null, ref inBlock).Select(t => t.Text)));
    }
}
