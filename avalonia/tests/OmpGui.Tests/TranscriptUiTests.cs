using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>Rich transcript: Markdown, code blocks, links, tables, thinking, tool summary and edit diff.</summary>
public sealed class TranscriptUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMPGUI_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public void Markdown_renders_blocks_and_only_web_links_are_clickable()
    {
        var root = MarkdownView.Build("# Title\n\nSome **bold** and `code` and a [link](https://example.com) and [bad](javascript:alert(1)).\n\n- a\n- b\n\n```cs\nvar x = 1;\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |\n");
        var w = new Window { Content = root, Width = 700, Height = 500 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var texts = root.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        Assert.Contains(texts, t => t.FontSize == 22 && t.Inlines!.OfType<Run>().Any(r => r.Text == "Title"));
        var links = root.GetVisualDescendants().OfType<HyperlinkButton>().ToList();
        Assert.Equal("https://example.com/", Assert.Single(links).NavigateUri!.AbsoluteUri);
        Assert.Contains(root.GetVisualDescendants().OfType<Button>(), b => Avalonia.Automation.AutomationProperties.GetName(b) == "Copy code");
        Assert.Contains(texts, t => (t.Text ?? string.Concat(t.Inlines!.OfType<Run>().Select(r => r.Text))) == "var x = 1;");
        Assert.Contains(root.GetVisualDescendants().OfType<Grid>(), g => g.Classes.Contains("md-table") && g.Children.Count == 4);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Assistant_markdown_thinking_and_an_edit_diff_in_the_window()
    {
        var s = new SessionController(TestProcesses.Fake("markdown"));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1200, Height = 900 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        vm.ComposerText = "change greet";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Any(t => t.StatusText == "done"), "run end");
        var a = vm.Rows.OfType<AssistantRowViewModel>().Single();
        Assert.StartsWith("# Plan", a.Text);
        Assert.True(a.HasThinking);
        var tool = vm.Rows.OfType<ToolRowViewModel>().Single();
        Assert.Equal(("edit", "hello.py"), (tool.Name, tool.Summary));
        Assert.True(tool.HasDiff);
        tool.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(w.GetVisualDescendants().OfType<DiffView>(), d => d.IsEffectivelyVisible);
        Assert.Contains(w.GetVisualDescendants().OfType<MarkdownView>(), m => m.IsEffectivelyVisible);
        Assert.Equal(0, s.Snapshot().StreamMismatches);
        Shot(w, "ui-markdown-diff");

        string? copied = null;
        void Capture(string t) => copied = t;
        AssistantRowViewModel.CopyRequested += Capture;
        a.CopyCommand.Execute(null);
        AssistantRowViewModel.CopyRequested -= Capture;
        Assert.Equal(a.Text, copied);
        await vm.DisposeAsync();
        w.Close();
    }

    [Fact]
    public void Tool_summaries_come_from_the_telling_argument()
    {
        static string Sum(string tool, string json) => ConversationState.ToolSummary(tool, System.Text.Json.JsonDocument.Parse(json).RootElement);
        Assert.Equal("$ ls -la", Sum("bash", """{"command":"ls -la","timeout":5}"""));
        Assert.Equal("src/a.cs", Sum("read", """{"path":"src/a.cs","offset":3}"""));
        Assert.Equal("TODO", Sum("grep", """{"pattern":"TODO"}"""));
        Assert.Equal("$ a b", Sum("bash", """{"command":"a\nb"}"""));
        Assert.Equal("", Sum("x", """{"n":1}"""));
        // No raw JSON on the line (design review): omp's todo ops, then the first short strings
        Assert.Equal("init · 3 items", Sum("todo", """{"op":"init","items":["a","b","c"]}"""));
        Assert.Equal("done · Wire workspace", Sum("todo", """{"op":"done","task":"Wire workspace"}"""));
        Assert.Equal("view", Sum("todo", """{"op":"view"}"""));
        Assert.Equal("2 changes", Sum("todo", """{"ops":[{},{}]}"""));
        Assert.Equal("set · fast", Sum("x", """{"action":"set","mode":"fast","n":2}"""));
    }

    [Fact]
    public void Tool_rows_say_what_the_call_gave_like_Claude_Code()
    {
        static ToolRowViewModel Row(string name, string? output, string? diff = null, string? note = null, TimeSpan? took = null,
            ToolStatus status = ToolStatus.Succeeded, string summary = "x") =>
            new(new ToolItem(1, "t", name, "{}", status, output, summary, diff, DateTimeOffset.UtcNow, took, note));
        Assert.Equal("Read 3 lines", Row("read", "a\nb\nc\n").Result);
        Assert.Equal("Found 12 files", Row("glob", "a\nb", note: "Found 12 files").Result);
        // Anything else: its first three lines, then "… +N lines", which opens the whole output
        var bash = Row("bash", "1\n2\n3\n4\n5\n");
        Assert.Equal(("1\n2\n3", "… +2 lines", true, true), (bash.Result, bash.MoreText, bash.ShowMore, bash.ResultIsOutput));
        bash.ShowAllCommand.Execute(null);
        Assert.True(bash.ShowOutput);
        Assert.False(bash.ShowMore);
        Assert.Equal("(No output)", Row("bash", "").Result);
        Assert.Equal("Interrupted", Row("bash", null, status: ToolStatus.Interrupted).Result);
        Assert.True(Row("bash", "boom\nexit code 1", status: ToolStatus.Failed).IsFailed);
        Assert.Equal("Wrote 2 lines", Row("write", "ok", diff: "+   1|a\n+   2|b\n").Result);
        Assert.Equal("Added 1 line, removed 1 line", Row("edit", "ok", diff: "-   2|a\n+   2|b\n").Result);
        // Durations under a tenth of a second say nothing
        Assert.Equal("", Row("bash", "x", took: TimeSpan.FromMilliseconds(40)).TookText);
        Assert.Equal("1.2s", Row("bash", "x", took: TimeSpan.FromSeconds(1.2)).TookText);
        // The name as a person reads it; a command without the prompt the name implies
        var make = Row("bash", "", summary: "$ make");
        Assert.Equal(("Bash", "make"), (make.DisplayName, make.ArgText));
        Assert.Equal("Search", ToolRowViewModel.DisplayNameOf("grep"));
        Assert.Equal("Create issue (github)", ToolRowViewModel.DisplayNameOf("mcp__github__create_issue"));
    }

    [Fact]
    public void Search_results_are_counted_from_omps_details()
    {
        static string? Note(string tool, string json) => ConversationState.ResultNote(tool, System.Text.Json.JsonDocument.Parse(json).RootElement);
        Assert.Equal("Found 12 files", Note("glob", """{"details":{"fileCount":12}}"""));
        Assert.Equal("Found 1 file", Note("glob", """{"details":{"fileCount":1}}"""));
        Assert.Equal("No files found", Note("glob", """{"details":{"fileCount":0}}"""));
        Assert.Equal("Found 5 matches in 2 files", Note("grep", """{"details":{"matchCount":5,"fileCount":2}}"""));
        Assert.Equal("Found 100+ matches", Note("grep", """{"details":{"matchCount":100,"fileCount":1,"truncated":true}}"""));
        Assert.Null(Note("read", """{"details":{"fileCount":3}}"""));
        Assert.Null(Note("grep", """{"content":[]}"""));
    }

    [Fact]
    public void A_run_end_line_hides_an_instant_duration()
    {
        static string Text(double seconds, int tools, bool stopped = false) =>
            new TurnEndRowViewModel(new TurnEndItem(1, TimeSpan.FromSeconds(seconds), stopped, tools, 0)).Text;
        Assert.Equal("1 tool", Text(0.04, 1));
        Assert.Equal("Worked for 2.5s · 3 tools", Text(2.5, 3));
        Assert.Equal("Stopped · 1 tool", Text(0.02, 1, stopped: true));
    }

    [Fact]
    public void An_approval_says_what_omp_wants_and_shows_the_command_alone()
    {
        static DialogViewModel Approval(string title) => new(new PendingDialog("a", DialogKind.Approval, title, null,
            [new DialogOption("Approve", null, false), new DialogOption("Deny", null, false)], null, null, DateTimeOffset.UtcNow, null), (_, _) => Task.CompletedTask);
        var bash = Approval("Allow tool: bash\nReason: outside the project\nCommand: rm -rf build");
        Assert.Equal(("omp wants to run a command", "rm -rf build", "outside the project"), (bash.Title, bash.Preview, bash.Note));
        Assert.Equal("Allow tool: bash", bash.Headline); // omp's own words stay for the log
        var write = Approval("Allow tool: write\nPath: a.txt\nContent:\nhello");
        Assert.Equal(("omp wants to write a file", "Path: a.txt\nContent:\nhello"), (write.Title, write.Preview));
        Assert.Equal("omp wants to use Lookup (docs)", Approval("Allow tool: mcp__docs__lookup\nOrigin: MCP server tool").Title);
    }

    [AvaloniaTheory]
    [InlineData(130, ">")]   // past Markdig's own quote limit: it throws
    [InlineData(40, "> ")]   // parses, but too deep to lay out as nested controls
    [InlineData(70, "- ")]   // past Markdig's list limit
    public void Deeply_nested_markdown_is_shown_as_plain_text(int depth, string marker)
    {
        // Regression (review round 4): Markdig threw for deep nesting; the reply stayed blank or frozen mid-stream.
        var text = marker == "- "
            ? string.Concat(Enumerable.Range(0, depth).Select(i => new string(' ', i * 2) + "- level\n")) + new string(' ', depth * 2) + "- quoted words"
            : string.Concat(Enumerable.Repeat(marker, depth)) + "quoted words";
        var view = new OmpGui.App.Controls.MarkdownView { Markdown = text };
        var w = new Avalonia.Controls.Window { Content = view, Width = 600, Height = 400 };
        w.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var plain = Assert.IsType<Avalonia.Controls.SelectableTextBlock>(view.Content);
        Assert.Contains("quoted words", plain.Text);
        // Ordinary Markdown still renders as blocks.
        view.Markdown = "# Title\n\n> a quote\n\n- item";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.IsType<Avalonia.Controls.StackPanel>(view.Content);
        w.Close();
    }
}
