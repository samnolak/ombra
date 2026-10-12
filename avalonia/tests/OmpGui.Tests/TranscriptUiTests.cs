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

    private static ToolRowViewModel Row(string name, string? output, string? diff = null, string? note = null, TimeSpan? took = null,
        ToolStatus status = ToolStatus.Succeeded, string summary = "x", string args = "{}", IReadOnlyList<FileChange>? changes = null) =>
        new(new ToolItem(1, "t", name, args, status, output, summary, diff, DateTimeOffset.UtcNow, took, note, changes));

    [Fact]
    public void Tool_rows_say_what_the_call_did_in_the_tense_of_its_state()
    {
        var running = Row("bash", null, status: ToolStatus.Running, summary: "$ make");
        Assert.Equal(("Running", "make"), (running.Verb, running.ArgText));
        Assert.Equal("Ran", Row("bash", "x").Verb);
        Assert.Equal(("Reading", "Read"), (Row("read", null, status: ToolStatus.Running).Verb, Row("read", "a").Verb));
        // A file change says what it did to its one file; a call that changed two is "Edited"
        Assert.Equal("Created", Row("edit", "ok", changes: [new("a.py", FileChangeKind.Created, "+1|a")]).Verb);
        Assert.Equal("Created", Row("write", "ok", changes: [new("a.py", FileChangeKind.Created, "+1|a")]).Verb);
        Assert.Equal("Wrote", Row("write", "ok").Verb);
        Assert.Equal("Deleted", Row("edit", "ok", changes: [new("a.py", FileChangeKind.Deleted, "-1|a")]).Verb);
        // A move across folders is "Moved"; within its folder, "Renamed"
        var moved = Row("edit", "ok", changes: [new("lib/b.py", FileChangeKind.Moved, null, "src/a.py")]);
        Assert.Equal(("Moved", "src/a.py → lib/b.py"), (moved.Verb, moved.ArgText));
        Assert.Equal("Renamed", Row("edit", "ok", changes: [new("src/b.py", FileChangeKind.Moved, null, "src/a.py")]).Verb);
        Assert.Equal("Edited", Row("edit", "ok", summary: "2 files", changes: [new("a", FileChangeKind.Modified, null), new("b", FileChangeKind.Created, null)]).Verb);
        // Tools without a verb keep their name (the approval card names them the same way)
        Assert.Equal("Create issue (github)", ToolRowViewModel.VerbOf("mcp__github__create_issue", running: false));
        Assert.Equal("Search", ToolRowViewModel.DisplayNameOf("grep"));
        // A search says what it looked for, and where
        Assert.Equal("greet\\( in src", Row("grep", "", summary: "src", args: """{"pattern":"greet\\(","path":"src"}""").ArgText);
        // What a read or a search found: a few words at the line's end, no second line
        var read = Row("read", "a\nb\nc\n");
        Assert.Equal(("3 lines", false), (read.Meta, read.ShowResult));
        Assert.Equal("12 files", Row("glob", "a\nb", note: "Found 12 files").Meta);
        // A file change: no words under it (its +N −M and the change say it)
        Assert.False(Row("edit", "ok", diff: "-   2|a\n+   2|b\n").ShowResult);
        Assert.Equal("Interrupted", Row("bash", null, status: ToolStatus.Interrupted).Meta);
        // Durations under a tenth of a second say nothing
        Assert.Equal("", Row("bash", "x", took: TimeSpan.FromMilliseconds(40)).TookText);
        Assert.Equal("1.2s", Row("bash", "x", took: TimeSpan.FromSeconds(1.2)).TookText);
    }

    [Fact]
    public void A_command_shows_a_console_and_says_why_it_failed_from_omps_notes()
    {
        // The command and its first lines, then "… +N lines", which opens the whole output
        var bash = Row("bash", "1\n2\n3\n4\n5\n", summary: "$ make", args: """{"command":"make"}""");
        Assert.Equal((true, false, "make", "1\n2\n3", "… +2 lines", true), (bash.ShowConsole, bash.ShowArg, bash.Command, bash.ConsoleOutput, bash.MoreText, bash.ShowConsoleMore));
        bash.ShowAllCommand.Execute(null);
        Assert.Equal(("1\n2\n3\n4\n5", false), (bash.ConsoleOutput, bash.ShowConsoleMore));
        // The whole command (the summary joins its lines)
        Assert.Equal("cd src\nmake", Row("bash", "x", summary: "$ cd src make", args: """{"command":"cd src\nmake"}""").Command);
        // A command that succeeded without a word stays one line
        var quiet = Row("bash", "", summary: "$ make");
        Assert.Equal((false, true, false), (quiet.ShowConsole, quiet.ShowArg, quiet.CanExpand));
        // omp's bash tool ends the output with its notes: they become the chip, not output lines
        var failed = Row("bash", "bash: ruff: command not found\n\n\nWall time: 0.01 seconds\n\nCommand exited with code 127", status: ToolStatus.Failed, summary: "$ ruff check");
        Assert.Equal(("exit 127", "bash: ruff: command not found", true, 0), (failed.FailureText, failed.ConsoleOutput, failed.ShowConsole, failed.MoreLines));
        Assert.Equal("ok", Row("bash", "ok\n\nWall time: 0.00 seconds").ConsoleOutput);
        // A command the user ran (the client's "exit code N"), a timeout, a tool without an exit code
        Assert.Equal("exit 1", Row("bash", "boom\nexit code 1", status: ToolStatus.Failed).FailureText);
        Assert.Equal("timed out", Row("bash", "partial\n\n[Command timed out after 30 seconds]", status: ToolStatus.Failed).FailureText);
        Assert.Equal("failed", Row("edit", "Could not find the text", status: ToolStatus.Failed).FailureText);
        Assert.Null(Row("bash", "fine").FailureText);
        // A failed command with no output still shows its console (red), with the command
        Assert.True(Row("bash", "Command exited with code 2", status: ToolStatus.Failed).ShowConsole);
    }

    /// <summary>Rows as MainViewModel.Apply builds them: added or updated in order, regrouped from the first row that matters.</summary>
    private sealed class Rows
    {
        public RowList List { get; } = [];
        private readonly Dictionary<long, RowViewModel> _byKey = [];

        public void Apply(params TranscriptItem[] items)
        {
            var from = int.MaxValue;
            foreach (var item in items)
            {
                if (_byKey.TryGetValue(item.Key, out var row))
                {
                    row.Update(item);
                    if (ExplorationGroups.Matters(row)) from = Math.Min(from, ExplorationGroups.IndexFromEnd(List, row));
                }
                else
                {
                    row = _byKey[item.Key] = RowViewModel.Create(item);
                    List.Add(row);
                    if (ExplorationGroups.Matters(row)) from = Math.Min(from, List.Count - 1);
                }
            }
            if (from != int.MaxValue) ExplorationGroups.Regroup(List, from);
        }

        /// <summary>The rows shown: "G:summary" for a group line, the tool or kind otherwise; folded rows in brackets.</summary>
        public string Shape() => string.Join(" ", List.Select(r => r switch
        {
            ExplorationGroupRowViewModel g => $"G:{g.Verb} {g.Summary}",
            ToolRowViewModel t => r.IsFolded ? $"[{t.Name}]" : t.Name,
            AssistantRowViewModel a => r.IsFolded ? "[a]" : a.HasText ? "text" : "a",
            _ => r.GetType().Name,
        }));
    }

    private static ToolItem Step(long key, string name, ToolStatus status = ToolStatus.Succeeded, string summary = "src/a.py") =>
        new(key, $"c{key}", name, "{}", status, status == ToolStatus.Running ? null : "out", summary);

    private static AssistantItem Reply(long key, string text = "", string thinking = "") => new(key, text, thinking, false, "toolUse", null);

    [Fact]
    public void Reads_and_searches_in_a_row_fold_into_one_explored_line()
    {
        var rows = new Rows();
        rows.Apply(new UserItem(1, "look", true), Reply(2, "I'll look around."), Step(3, "read"), Reply(4, thinking: "hmm"), Step(5, "grep", summary: "src"),
            Reply(6), Step(7, "read", summary: "src/b.py"), Reply(8, "Now the tests."), Step(9, "read"));
        // The replies with nothing to read between the steps fold with them; text ends the group; a lone step stays a row
        Assert.Equal("UserRowViewModel text G:Explored 2 files, 1 search [read] [a] [grep] [a] [read] text read", rows.Shape());
        var group = rows.List.OfType<ExplorationGroupRowViewModel>().Single();
        group.IsExpanded = true;
        Assert.Equal("UserRowViewModel text G:Explored 2 files, 1 search read a grep a read text read", rows.Shape());
        Assert.True(rows.List.OfType<ToolRowViewModel>().Take(3).All(t => t.InGroup));

        // A failed step ends a group (and is shown); one that fails as the second step undoes the group
        var failing = new Rows();
        failing.Apply(Step(1, "read"), Step(2, "grep", ToolStatus.Running));
        Assert.Equal("G:Exploring Searching src/a.py [read] [grep]", failing.Shape());
        failing.Apply(Step(2, "grep", ToolStatus.Failed));
        Assert.Equal("read grep", failing.Shape());
        failing.Apply(Step(3, "glob"), Step(4, "read"));
        Assert.Equal("read grep G:Explored 1 file, 1 search [glob] [read]", failing.Shape());

        // Another tool ends a group too
        var mixed = new Rows();
        mixed.Apply(Step(1, "read"), Step(2, "bash", summary: "$ make"), Step(3, "read"));
        Assert.Equal("read bash read", mixed.Shape());
    }

    [Fact]
    public void A_history_loaded_at_once_groups_as_the_live_run_did()
    {
        TranscriptItem[] final =
        [
            new UserItem(1, "look", true), Reply(2, "First a look."), Step(3, "read"), Reply(4), Step(5, "grep"), Reply(6), Step(7, "glob"),
            Reply(8, "Running the tests."), Step(9, "bash"), Reply(10), Step(11, "read", summary: "src/b.py"), Reply(12), Step(13, "ls"),
        ];
        var live = new Rows();
        foreach (var item in final)
        {
            // Each step starts running, then ends; a reply streams in empty first
            if (item is ToolItem t) live.Apply(t with { Status = ToolStatus.Running, Output = null });
            if (item is AssistantItem a) live.Apply(a with { Text = "", Streaming = true });
            live.Apply(item);
        }
        var hydrated = new Rows();
        hydrated.Apply(final);
        Assert.Equal(hydrated.Shape(), live.Shape());
        Assert.Equal("UserRowViewModel text G:Explored 1 file, 2 searches [read] [a] [grep] [a] [glob] text bash a G:Explored 1 file, 1 listing [read] [a] [ls]", live.Shape());
    }

    [Fact]
    public void A_turn_end_lists_each_file_it_changed_once()
    {
        var end = new TurnEndRowViewModel(new TurnEndItem(1, TimeSpan.FromSeconds(70), false, 5, 4));
        end.SetFiles(
        [
            Row("edit", "ok", changes: [new("src/a.py", FileChangeKind.Modified, " 1|x\n-2|a\n+2|b\n")]),
            Row("edit", "ok", changes: [new("src/a.py", FileChangeKind.Modified, "+5|c\n")]),
            Row("write", "ok", changes: [new("new.md", FileChangeKind.Created, "+1|# A\n+2|b\n")]),
            Row("edit", "ok", changes: [new("lib/helpers.py", FileChangeKind.Moved, "-1|x\n+1|y\n", "src/util.py")]),
            Row("edit", "nope", status: ToolStatus.Failed, changes: [new("broken.py", FileChangeKind.Modified, "+1|x\n")]),
            Row("write", "ok", changes: [new("tmp.txt", FileChangeKind.Created, "+1|x\n")]),
            Row("edit", "ok", changes: [new("tmp.txt", FileChangeKind.Deleted, "-1|x\n"), new("old.py", FileChangeKind.Deleted, "-1|x\n-2|y\n")]),
        ]);
        Assert.Equal(["src/a.py +2 −1", "new.md New +2", "lib/helpers.py Moved +1 −1", "old.py Deleted −2"],
            end.Files.Select(f => string.Join(" ", new[] { f.Path, f.KindText, f.AddedText, f.RemovedText }.Where(s => s.Length > 0))));
        Assert.Equal("from src/util.py", end.Files[2].MovedFromText);
        Assert.Equal(new FileLink("src/a.py", 2), end.Files[0].Link);
        Assert.Equal(("Worked for 1m 10s · 5 tools · 4 files changed", "Worked for 1m 10s · 5 tools ·"), (end.Text, end.LeadText));
    }

    /// <summary>
    /// The showcase turn, live: its first reads and searches are one "Explored" line (steps folded, still rows for the
    /// Files pane and the turn end), the failed command says "exit 127", and the turn's end lists every file it changed
    /// with what happened to it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_live_turn_groups_its_exploration_and_lists_the_files_it_changed()
    {
        var s = new SessionController(TestProcesses.Fake("showcase"));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 900 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        vm.ComposerText = "make greet take a name";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<TurnEndRowViewModel>().Any(e => e.HasFiles), "run end");
        var group = Assert.Single(vm.Rows.OfType<ExplorationGroupRowViewModel>());
        Assert.Equal(("Explored", "2 files, 2 searches", 4), (group.Verb, group.Summary, group.Steps.Count));
        Assert.All(group.Steps, t => Assert.True(t.IsFolded));
        Assert.Same(group, vm.Rows[vm.Rows.IndexOf(group.Steps[0]) - 1]);
        Assert.Equal("exit 127", Assert.Single(vm.Rows.OfType<ToolRowViewModel>(), t => t.IsFailed).FailureText);
        var end = vm.Rows.OfType<TurnEndRowViewModel>().Single();
        Assert.Equal(["src/hello.py", "tests/test_greet.py New", "docs/GREETING.md New", "src/legacy_greet.py Deleted", "src/helpers.py Renamed", "src/cli.py", "tests/test_cli.py"],
            end.Files.Select(f => (f.Path + " " + f.KindText).Trim()));
        group.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Assert.All(group.Steps, t => Assert.False(t.IsFolded));
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>A conversation loaded from its file groups as it did live: one assistant message calling three
    /// exploration tools at once is one "Explored" line.</summary>
    [AvaloniaFact]
    public async Task A_loaded_history_groups_its_exploration()
    {
        var root = TestProcesses.TempDir("explore-sessions");
        var cwd = SessionCatalog.ResolveLinks(TestProcesses.TempDir("explore-project"));
        static object Text(string t) => new { type = "text", text = t };
        static object Call(string id, string name, object args) => new { type = "toolCall", id, name, arguments = args };
        static object Result(string id, string name, string text) => new { role = "toolResult", toolCallId = id, toolName = name, content = new[] { Text(text) }, isError = false };
        var file = ChatScrollTests.WriteSession(root, cwd, "Explore", [
            new { role = "user", content = new[] { Text("where is greet?") } },
            new { role = "assistant", content = new object[] { Text("Looking."), Call("a", "grep", new { pattern = "greet", path = "src" }), Call("b", "read", new { path = "src/hello.py" }), Call("c", "read", new { path = "src/cli.py" }) }, stopReason = "toolUse" },
            Result("a", "grep", "src/hello.py:1:def greet():"), Result("b", "read", "def greet():\n    pass\n"), Result("c", "read", "import sys\n"),
            new { role = "assistant", content = new[] { Text("It is in src/hello.py.") }, stopReason = "stop" },
        ]);
        var s = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(cwd, ResumeSessionFile: file));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Count() == 3, "the history loaded");
        var group = Assert.Single(vm.Rows.OfType<ExplorationGroupRowViewModel>());
        Assert.Equal(("Explored", "2 files, 1 search"), (group.Verb, group.Summary));
        Assert.All(vm.Rows.OfType<ToolRowViewModel>(), t => Assert.True(t.IsFolded));
        await vm.DisposeAsync();
        w.Close();
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
