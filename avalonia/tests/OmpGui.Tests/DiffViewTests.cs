using System.Diagnostics;
using System.Text.Json;
using OmpGui.App.Controls;
using OmpGui.ClientCore;
using OmpGui.Rpc;
using static OmpGui.App.Controls.DiffView;

namespace OmpGui.Tests;

/// <summary>
/// File changes as omp reports them (its own code-frame lines, its edit details: op / move / perFileResults) and as
/// unified diffs; new files as added lines; the words that changed inside a changed line.
/// </summary>
public sealed class DiffViewTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.Parse("2026-10-12T12:00:00Z");

    private static RpcFrame F(string json)
    {
        var e = JsonDocument.Parse(json).RootElement.Clone();
        return new RpcFrame(e.GetProperty("type").GetString()!, null, e, Stopwatch.GetTimestamp(), json.Length + 1);
    }

    [Fact]
    public void Omp_code_frame_lines_get_old_and_new_numbers_with_gaps_between_parts()
    {
        // omp numbers removed and unchanged lines in the old file, added lines in the new one
        var rows = Parse(" 1|def greet():\n-2|    print('Hi')\n+2|    print('Hello')\n 3|\n 12|def main():\n+13|    greet()\n 13|    return 0\n");
        Assert.Equal([Kind.Context, Kind.Removed, Kind.Added, Kind.Context, Kind.Gap, Kind.Context, Kind.Added, Kind.Context], rows.Select(r => r.Kind));
        Assert.Equal([1, 2, null, 3, null, 12, null, 13], rows.Select(r => r.Old));
        Assert.Equal([1, null, 2, 3, null, 12, 13, 14], rows.Select(r => r.New));
        Assert.Equal("    print('Hello')", rows[2].Text);
        // A real omp 18.8 frame: two lines inserted, the closing brace after them is old line 10, new line 12
        var real = Parse(" 8|export interface GlobalSettings {\n 9|  readonly theme: ThemePreference;\n+10|  /** easter eggs */\n+11|  readonly gamification: boolean;\n 10|}\n");
        Assert.Equal((10, 12), (real[^1].Old!.Value, real[^1].New!.Value));
        Assert.Equal((2, 1), Stats(" 1|def greet():\n-2|    print('Hi')\n+2|    print('Hello')\n+3|x\n"));
    }

    [Fact]
    public void Unified_diffs_number_lines_in_both_files()
    {
        var rows = Parse("--- a/x.py\n+++ b/x.py\n@@ -1,3 +1,3 @@\n def greet():\n-    print('Hi')\n+    print('Hello')\n@@ -20,1 +20,2 @@\n end\n+more\n");
        Assert.Equal([Kind.Context, Kind.Removed, Kind.Added, Kind.Gap, Kind.Context, Kind.Added], rows.Select(r => r.Kind));
        Assert.Equal([1, 2, null, null, 20, null], rows.Select(r => r.Old));
        Assert.Equal([1, null, 2, null, 20, 21], rows.Select(r => r.New));
        Assert.Equal(3, FirstChangedLine("--- a\n+++ b\n@@ -1,2 +1,2 @@\n x\n y\n-z\n+w\n"));
    }

    [Fact]
    public void A_written_file_is_its_content_as_added_lines()
    {
        var args = JsonDocument.Parse("""{"path":"a.py","content":"x = 1\ny = 2\n"}""").RootElement;
        var diff = ConversationState.WrittenContent("write", args)!;
        var rows = Parse(diff);
        Assert.Equal([Kind.Added, Kind.Added], rows.Select(r => r.Kind));
        Assert.Equal(("x = 1", 1), (rows[0].Text, rows[0].New));
        Assert.Null(ConversationState.WrittenContent("read", args));
    }

    /// <summary>One edit call, as omp 18.8 sends it: the call's start, then its end with the result's details.</summary>
    private static void Edit(ConversationState s, string id, string args, string details)
    {
        s.Apply(F($$$"""{"type":"tool_execution_start","toolCallId":"{{{id}}}","toolName":"edit","args":{{{args}}}}"""), T);
        s.Apply(F($$$"""{"type":"tool_execution_end","toolCallId":"{{{id}}}","toolName":"edit","isError":false,"result":{"content":[{"type":"text","text":"ok"}],"details":{{{details}}}}}"""), T);
    }

    private static ToolItem Tool(ConversationState s, string id) => s.Snapshot().Items.OfType<ToolItem>().Single(t => t.ToolCallId == id);

    [Fact]
    public void Edit_details_become_one_change_per_file_with_what_happened_to_it()
    {
        var s = new ConversationState { Cwd = "/work/proj" };
        s.SetPhase(SessionPhase.Ready, T);
        s.AddUserPrompt("rename greet");
        s.SetPhase(SessionPhase.Running, T);
        // A plain edit: omp's absolute path, shown from the project
        Edit(s, "e1", """{"i":"Changing the greeting","input":"[src/hello.py#1A2B]\nPUT 2:\n+    print('Hello')"}""",
            """{"op":"update","path":"/work/proj/src/hello.py","firstChangedLine":2,"diff":" 1|def greet():\n-2|    print('Hi')\n+2|    print('Hello')\n"}""");
        var e1 = Tool(s, "e1");
        Assert.Equal("src/hello.py", e1.Summary); // the hashline section names the file, not the intent
        var c1 = Assert.Single(e1.Changes!);
        Assert.Equal(("src/hello.py", FileChangeKind.Modified, (string?)null), (c1.Path, c1.Kind, c1.MovedFrom));
        Assert.Contains("print('Hello')", c1.Diff);
        // op create / delete
        Edit(s, "e2", """{"path":"tests/test_greet.py"}""", """{"op":"create","path":"/work/proj/tests/test_greet.py","diff":"+1|import pytest\n"}""");
        Assert.Equal(FileChangeKind.Created, Assert.Single(Tool(s, "e2").Changes!).Kind);
        Edit(s, "e3", """{"path":"src/legacy_greet.py"}""", """{"op":"delete","path":"/work/proj/src/legacy_greet.py","diff":"-1|# old\n-2|def legacy():\n"}""");
        Assert.Equal(FileChangeKind.Deleted, Assert.Single(Tool(s, "e3").Changes!).Kind);
        // A move: the new path, and where it came from; the summary keeps the path the call named
        Edit(s, "e4", """{"path":"src/util.py"}""",
            """{"op":"update","path":"/work/proj/src/helpers.py","move":"/work/proj/src/helpers.py","sourcePath":"/work/proj/src/util.py","diff":"-1|\"\"\"Odds.\"\"\"\n+1|\"\"\"Helpers.\"\"\"\n"}""");
        var moved = Assert.Single(Tool(s, "e4").Changes!);
        Assert.Equal(("src/helpers.py", FileChangeKind.Moved, "src/util.py"), (moved.Path, moved.Kind, moved.MovedFrom));
        Assert.Equal("src/util.py", Tool(s, "e4").Summary);
        // One call over two files (apply_patch): one change each, the joined diff kept for the row, "2 files" on its line
        Edit(s, "e5", """{"input":"*** Begin Patch"}""",
            """{"diff":" 1|import sys\n-2|from src.util import clamp\n+2|from src.helpers import clamp\n","perFileResults":[{"path":"/work/proj/src/cli.py","op":"update","diff":" 1|import sys\n-2|from src.util import clamp\n+2|from src.helpers import clamp\n"},{"path":"/work/proj/tests/test_cli.py","op":"update","diff":" 2|\n-3|from src.util import clamp\n+3|from src.helpers import clamp\n"}]}""");
        var patch = Tool(s, "e5");
        Assert.Equal(["src/cli.py", "tests/test_cli.py"], patch.Changes!.Select(c => c.Path));
        Assert.All(patch.Changes!, c => Assert.Equal(FileChangeKind.Modified, c.Kind));
        Assert.Equal("2 files", patch.Summary);
        Assert.NotNull(patch.Diff);
        // A todo's details (op, no diff) are no file change
        s.Apply(F("""{"type":"tool_execution_start","toolCallId":"t1","toolName":"todo","args":{"op":"done","task":"x"}}"""), T);
        s.Apply(F("""{"type":"tool_execution_end","toolCallId":"t1","toolName":"todo","result":{"content":[],"details":{"op":"done","phases":[]}}}"""), T);
        Assert.Null(Tool(s, "t1").Changes);
        // The run's end counts files, each once: hello, test_greet, legacy_greet, helpers, cli, test_cli
        s.Apply(F("""{"type":"session_settled"}"""), T);
        Assert.Equal(6, s.Snapshot().Items.OfType<TurnEndItem>().Single().FilesChanged);
    }

    [Fact]
    public void A_loaded_history_reads_the_same_details()
    {
        var s = new ConversationState { Cwd = "/work/proj" };
        s.Hydrate(JsonDocument.Parse("""
            [{"role":"user","content":[{"type":"text","text":"go"}]},
             {"role":"assistant","content":[{"type":"toolCall","id":"a","name":"edit","arguments":{"path":"src/util.py"}},
                                            {"type":"toolCall","id":"b","name":"write","arguments":{"path":"docs/NEW.md","content":"# New\n"}}]},
             {"role":"toolResult","toolCallId":"a","toolName":"edit","content":[{"type":"text","text":"Moved"}],
              "details":{"path":"/work/proj/src/helpers.py","move":"/work/proj/src/helpers.py","sourcePath":"/work/proj/src/util.py","diff":""}},
             {"role":"toolResult","toolCallId":"b","toolName":"write","content":[{"type":"text","text":"Wrote"}]}]
            """).RootElement);
        var move = Assert.Single(Tool(s, "a").Changes!);
        Assert.Equal(("src/helpers.py", FileChangeKind.Moved, "src/util.py", (string?)null), (move.Path, move.Kind, move.MovedFrom, move.Diff));
        // Nobody looked at the disk before this write ran: it is a changed file, never a guessed new one
        var write = Assert.Single(Tool(s, "b").Changes!);
        Assert.Equal(("docs/NEW.md", FileChangeKind.Modified), (write.Path, write.Kind));
    }

    [Fact]
    public void A_write_is_a_new_file_only_when_nothing_was_there_before_it_ran()
    {
        var dir = TestProcesses.TempDir("write-probe");
        File.WriteAllText(Path.Combine(dir, "old.txt"), "x");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        Assert.True(SessionController.IsNewFile(dir, "docs/NEW.md"));
        Assert.False(SessionController.IsNewFile(dir, "old.txt"));
        Assert.False(SessionController.IsNewFile(dir, "sub"));
        Assert.True(SessionController.IsNewFile(null, Path.Combine(dir, "absent.txt")));
        Assert.False(SessionController.IsNewFile(null, "relative.txt")); // nowhere to look: not new

        var start = F("""{"type":"tool_execution_start","toolCallId":"w1","toolName":"write","args":{"path":"docs/NEW.md","content":"# New\n"}}""");
        Assert.Equal("docs/NEW.md", ConversationState.WriteTarget(start.Json));
        Assert.Null(ConversationState.WriteTarget(F("""{"type":"tool_execution_start","toolCallId":"w","toolName":"write","args":{"path":"local://NOTES.md"}}""").Json));
        Assert.Null(ConversationState.WriteTarget(F("""{"type":"tool_execution_start","toolCallId":"r","toolName":"read","args":{"path":"a.md"}}""").Json));

        var s = new ConversationState { Cwd = dir };
        s.ExpectNewFile("w1");
        s.Apply(start, T);
        s.Apply(F("""{"type":"tool_execution_end","toolCallId":"w1","toolName":"write","result":{"content":[{"type":"text","text":"ok"}]}}"""), T);
        Assert.Equal(FileChangeKind.Created, Assert.Single(Tool(s, "w1").Changes!).Kind);
        // Not expected (the file was there, or the look came late): changed
        s.Apply(F("""{"type":"tool_execution_start","toolCallId":"w2","toolName":"write","args":{"path":"old.txt","content":"y"}}"""), T);
        s.Apply(F("""{"type":"tool_execution_end","toolCallId":"w2","toolName":"write","result":{"content":[]}}"""), T);
        Assert.Equal(FileChangeKind.Modified, Assert.Single(Tool(s, "w2").Changes!).Kind);
        // A write that failed changed nothing
        s.ExpectNewFile("w3");
        s.Apply(F("""{"type":"tool_execution_start","toolCallId":"w3","toolName":"write","args":{"path":"x/y.md","content":"z"}}"""), T);
        s.Apply(F("""{"type":"tool_execution_end","toolCallId":"w3","toolName":"write","isError":true,"result":{"content":[{"type":"text","text":"EACCES"}]}}"""), T);
        Assert.Null(Tool(s, "w3").Changes);
    }

    private static string[] Marked(string line, IReadOnlyList<DiffWords.Span> spans) => spans.Select(m => line.Substring(m.Start, m.Length)).ToArray();

    [Fact]
    public void Words_that_changed_inside_a_line_are_marked()
    {
        const string before = "    print('Hi')  # old greeting";
        const string after = "    print(f\"Hello, {name}!\")  # new greeting";
        var (old, @new) = DiffWords.Compare(before, after)!.Value;
        Assert.Equal(["'Hi'", "old"], Marked(before, old));
        Assert.Equal(["f\"Hello, {name}!\"", "new"], Marked(after, @new));
        // An insertion marks only what was added
        var (o2, n2) = DiffWords.Compare("    greet()", "    greet(sys.argv[1])")!.Value;
        Assert.Empty(o2);
        Assert.Equal(["sys.argv[1]"], Marked("    greet(sys.argv[1])", n2));
        // Equal, too different, too long: nothing marked
        Assert.Null(DiffWords.Compare("same()", "same()"));
        Assert.Null(DiffWords.Compare("import os", "return render(page, items)"));
        Assert.Null(DiffWords.Compare(new string('a', DiffWords.MaxLineLength + 1), new string('a', DiffWords.MaxLineLength) + "b"));
    }

    [Fact]
    public void Removed_lines_pair_with_the_added_lines_right_after_them_one_by_one()
    {
        static IReadOnlyList<DiffWords.Span>?[] Mark(params (int Sign, string Text)[] rows) =>
            DiffWords.Mark(rows.Length, i => rows[i].Sign, i => rows[i].Text);
        // One removed, two added: the first added pairs, the second is a new line
        var m = Mark((-1, "x = load(a)"), (1, "x = load(b)"), (1, "y = load(c)"));
        Assert.NotNull(m[0]);
        Assert.NotNull(m[1]);
        Assert.Null(m[2]);
        // Two removed, one added: the first removed pairs with it
        m = Mark((-1, "x = load(a)"), (-1, "y = load(c)"), (1, "x = load(b)"));
        Assert.NotNull(m[0]);
        Assert.Null(m[1]);
        Assert.NotNull(m[2]);
        // A line between them: no pair
        m = Mark((-1, "x = load(a)"), (0, "pass"), (1, "x = load(b)"));
        Assert.All(m, Assert.Null);
        // An added line before the removed one is not its replacement
        m = Mark((1, "x = load(b)"), (-1, "x = load(a)"));
        Assert.All(m, Assert.Null);
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
