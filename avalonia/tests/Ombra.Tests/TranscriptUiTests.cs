using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>Rich transcript: Markdown, code blocks, links, tables, thinking, tool summary and edit diff.</summary>
public sealed class TranscriptUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");

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
        // The finished turn folds its edit under "Worked for…" (the reply, its answer, stays): open, then the change
        foreach (var fold in vm.Rows.OfType<TurnFoldRowViewModel>()) fold.IsExpanded = true;
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
    public void A_command_opens_into_a_shell_card_and_says_why_it_failed_from_omps_notes()
    {
        // One line ("Ran make"); opened, its Shell card: the command and all its output
        var bash = Row("bash", "1\n2\n3\n4\n5\n", summary: "$ make", args: """{"command":"make"}""");
        Assert.Equal((false, true, "make", true, ""), (bash.ShowConsole, bash.ShowArg, bash.Command, bash.CanExpand, bash.ConsoleOutput));
        bash.IsExpanded = true;
        Assert.Equal((true, "1\n2\n3\n4\n5"), (bash.ShowConsole, bash.ConsoleOutput));
        // The whole command (the summary joins its lines)
        Assert.Equal("cd src\nmake", Row("bash", "x", summary: "$ cd src make", args: """{"command":"cd src\nmake"}""").Command);
        // A command that succeeded without a word stays one line, with nothing to open
        var quiet = Row("bash", "", summary: "$ make");
        Assert.Equal((false, true, false), (quiet.ShowConsole, quiet.ShowArg, quiet.CanExpand));
        // omp's bash tool ends the output with its notes: they become the chip and the card's foot, not output lines
        var failed = Row("bash", "bash: ruff: command not found\n\n\nWall time: 0.01 seconds\n\nCommand exited with code 127", status: ToolStatus.Failed, summary: "$ ruff check");
        Assert.Equal(("exit 127", "Exit code 127", false, true), (failed.FailureText, failed.ExitText, failed.ShowConsole, failed.CanExpand));
        failed.IsExpanded = true;
        Assert.Equal("bash: ruff: command not found", failed.ConsoleOutput);
        var ok = Row("bash", "ok\n\nWall time: 0.00 seconds");
        ok.IsExpanded = true;
        Assert.Equal(("ok", false), (ok.ConsoleOutput, ok.HasExitText));
        // A command the user ran (the client's "exit code N"), a timeout, a tool without an exit code
        Assert.Equal("exit 1", Row("bash", "boom\nexit code 1", status: ToolStatus.Failed).FailureText);
        Assert.Equal("timed out", Row("bash", "partial\n\n[Command timed out after 30 seconds]", status: ToolStatus.Failed).FailureText);
        Assert.Equal("failed", Row("edit", "Could not find the text", status: ToolStatus.Failed).FailureText);
        Assert.Null(Row("bash", "fine").FailureText);
        // A failed command with no output still opens, to its command and how it exited
        Assert.True(Row("bash", "Command exited with code 2", status: ToolStatus.Failed).CanExpand);
        // A running command shows its newest lines live
        Assert.True(Row("bash", null, status: ToolStatus.Running).ShowConsole);
    }

    /// <summary>Rows as MainViewModel.Apply builds them: added or updated in order, regrouped from the first row that
    /// matters; <see cref="Fold"/> puts the turns' "Worked for…" lines as MarkTurnEnds does.</summary>
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
                    if (StepGroups.Matters(row)) from = Math.Min(from, StepGroups.IndexFromEnd(List, row));
                }
                else
                {
                    row = _byKey[item.Key] = RowViewModel.Create(item);
                    List.Add(row);
                    if (StepGroups.Matters(row)) from = Math.Min(from, List.Count - 1);
                }
            }
            if (from != int.MaxValue) StepGroups.Regroup(List, from);
        }

        public void Fold(bool running) => TurnFolds.Reconcile(List, running, all: true);

        /// <summary>The rows shown: "F:label ›" for a turn's line, "G:summary" for a group's line, the tool or kind
        /// otherwise; folded rows in brackets.</summary>
        public string Shape() => string.Join(" ", List.Select(r => r switch
        {
            TurnFoldRowViewModel f => $"F:{f.Label}" + (f.ShowChevron ? " ›" : ""),
            StepGroupRowViewModel g => r.IsFolded ? $"[G:{g.Summary}]" : $"G:{g.Summary}",
            ToolRowViewModel t => r.IsFolded ? $"[{t.Name}]" : t.Name,
            AssistantRowViewModel a => r.IsFolded ? (a.HasText ? "[text]" : "[a]") : a.HasText ? "text" : "a",
            _ => r.GetType().Name,
        }));
    }

    private static ToolItem Step(long key, string name, ToolStatus status = ToolStatus.Succeeded, string summary = "src/a.py") =>
        new(key, $"c{key}", name, "{}", status, status == ToolStatus.Running ? null : "out", summary);

    private static AssistantItem Reply(long key, string text = "", string thinking = "", DateTimeOffset? at = null) =>
        new(key, text, thinking, false, "toolUse", null) { At = at };

    [Fact]
    public void Steps_in_a_row_fold_into_one_line_that_says_what_they_did()
    {
        var rows = new Rows();
        rows.Apply(new UserItem(1, "look", true), Reply(2, "I'll look around."), Step(3, "read"), Reply(4, thinking: "hmm"), Step(5, "grep", summary: "src"),
            Reply(6), Step(7, "read", summary: "src/b.py"), Reply(8, "Now the tests."), Step(9, "read"));
        // The replies with nothing to read between the steps fold with them; text ends the group; a lone step stays a row
        Assert.Equal("UserRowViewModel text G:Read files, searched the code [read] [a] [grep] [a] [read] text read", rows.Shape());
        var group = rows.List.OfType<StepGroupRowViewModel>().Single();
        Assert.Equal(StepKind.Read, group.Kind);
        group.IsExpanded = true;
        Assert.Equal("UserRowViewModel text G:Read files, searched the code read a grep a read text read", rows.Shape());
        Assert.Equal([1, 1, 1, 1, 1, 0, 0], rows.List.Skip(3).Select(r => r.Depth)); // the steps and the replies between them hang under the line

        // A failed step ends a group (and is shown); one that fails as the second step undoes the group
        var failing = new Rows();
        failing.Apply(Step(1, "read"), Step(2, "grep", ToolStatus.Running));
        Assert.Equal("G:Searching src/a.py [read] [grep]", failing.Shape());
        failing.Apply(Step(2, "grep", ToolStatus.Failed));
        Assert.Equal("read grep", failing.Shape());
        failing.Apply(Step(3, "glob"), Step(4, "read"));
        Assert.Equal("read grep G:Searched the code, read a file [glob] [read]", failing.Shape());

        // Any tools group, each kind said once in the order it came: one file read twice is "a file"
        var mixed = new Rows();
        mixed.Apply(Step(1, "read"), Step(2, "bash", summary: "$ make"), Step(3, "read"), Step(4, "bash", summary: "$ make test"));
        Assert.Equal("G:Read a file, ran commands [read] [bash] [read] [bash]", mixed.Shape());
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
        Assert.Equal("UserRowViewModel text G:Read a file, searched the code [read] [a] [grep] [a] [glob] text G:Ran a command, read a file, listed a folder [bash] [a] [read] [a] [ls]", live.Shape());
    }

    /// <summary>
    /// A finished turn folds what it did before its answer under "Worked for 1m 18s" (from the times omp saved with the
    /// message and the answer); while it runs the line says "Activity" and folds nothing; opened, its steps show again.
    /// A turn that only answered has no line; a stopped one says so.
    /// </summary>
    [Fact]
    public void A_finished_turn_folds_its_steps_under_how_long_it_worked()
    {
        var sent = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var rows = new Rows();
        rows.Apply(new UserItem(1, "look", true) { At = sent }, Reply(2, "Looking.", at: sent.AddSeconds(3)), Step(3, "read"), Step(4, "grep"),
            Reply(5, "It is in src/a.py.", at: sent.AddSeconds(78)));
        rows.Fold(running: true);
        Assert.Equal("UserRowViewModel F:Activity text G:Read a file, searched the code [read] [grep] text", rows.Shape());
        // "Activity" folds nothing, so nothing hangs under it; a group's steps hang under the group's line
        int[] Depths() => [.. rows.List.Select(r => r.Depth)];
        Assert.Equal([0, 0, 0, 0, 1, 1, 0], Depths());
        rows.Fold(running: false);
        Assert.Equal("UserRowViewModel F:Worked for 1m 18s › [text] [G:Read a file, searched the code] [read] [grep] text", rows.Shape());
        var fold = rows.List.OfType<TurnFoldRowViewModel>().Single();
        fold.IsExpanded = true;
        Assert.Equal("UserRowViewModel F:Worked for 1m 18s › text G:Read a file, searched the code [read] [grep] text", rows.Shape());
        // A tree under the finished turn's line: what it folds one level in, the group's steps two; the answer stays out
        Assert.Equal([0, 0, 1, 1, 2, 2, 0], Depths());
        // Reconciled again (rows came, a run ended), the line keeps its place and its state
        rows.Fold(running: false);
        Assert.Same(fold, rows.List[1]);
        Assert.True(fold.IsExpanded);

        // A turn that only answered: no line; the length of a turn the client ran comes from its end (stopped here)
        var plain = new Rows();
        plain.Apply(new UserItem(1, "hi", true), Reply(2, "Hello."));
        plain.Fold(running: false);
        Assert.Equal("UserRowViewModel text", plain.Shape());
        var stopped = new Rows();
        stopped.Apply(new UserItem(1, "go", true), Step(2, "bash"), new TurnEndItem(3, TimeSpan.FromSeconds(12), true, 1, 0));
        stopped.Fold(running: false);
        Assert.Equal("UserRowViewModel F:Stopped after 12s › [bash] TurnEndRowViewModel", stopped.Shape());
    }

    [Fact]
    public void A_turns_length_is_said_in_whole_units()
    {
        Assert.Equal("1s", TurnEndRowViewModel.TurnDuration(TimeSpan.FromSeconds(0.4)));
        Assert.Equal("17s", TurnEndRowViewModel.TurnDuration(TimeSpan.FromSeconds(17.2)));
        Assert.Equal("1m 18s", TurnEndRowViewModel.TurnDuration(TimeSpan.FromSeconds(78)));
        Assert.Equal("1h 1m", TurnEndRowViewModel.TurnDuration(TimeSpan.FromSeconds(3700)));
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
        Assert.Equal(("lib/", "helpers.py"), (end.Files[2].FolderPrefix, end.Files[2].Name));
        // Codex's card: its header sums the files up; four rows show whole, more fold behind "Show N more files"
        Assert.Equal(("Edited 4 files", "+5", "−4", 4, false), (end.Title, end.AddedText, end.RemovedText, end.ShownFiles.Count, end.HasMoreFiles));
        var five = new TurnEndRowViewModel(new TurnEndItem(2, TimeSpan.FromSeconds(9), false, 5, 5));
        five.SetFiles([.. Enumerable.Range(1, 5).Select(i => Row("write", "ok", changes: [new($"f{i}.txt", FileChangeKind.Created, "+1|x\n")]))]);
        Assert.Equal((3, true, "Show 2 more files"), (five.ShownFiles.Count, five.HasMoreFiles, five.MoreFilesText));
        five.ShowAllFilesCommand.Execute(null);
        Assert.Equal((5, false), (five.ShownFiles.Count, five.HasMoreFiles));
        var one = new TurnEndRowViewModel(new TurnEndItem(3, TimeSpan.FromSeconds(9), false, 1, 1));
        one.SetFiles([Row("edit", "ok", changes: [new("src/serve.cjs", FileChangeKind.Modified, "+1|x\n")])]);
        Assert.Equal(("Edited serve.cjs", false), (one.Title, one.HasFileRows));
    }

    /// <summary>
    /// The showcase turn, live: its steps make step lines (still rows for the Files pane and the turn end), the failed
    /// command says "exit 127", the turn's end lists every file it changed with what happened to it; finished, the turn
    /// folds its steps under "Worked for…" over its answer, and they open again from there.
    /// </summary>
    [AvaloniaFact]
    public async Task A_finished_turn_folds_its_steps_and_lists_the_files_it_changed()
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
        var fold = Assert.Single(vm.Rows.OfType<TurnFoldRowViewModel>());
        Assert.StartsWith("Worked for ", fold.Label);
        Assert.Same(fold, vm.Rows[vm.Rows.IndexOf(vm.Rows.OfType<UserRowViewModel>().Single()) + 1]);
        Assert.All(vm.Rows.OfType<ToolRowViewModel>(), t => Assert.True(t.IsFolded));
        Assert.False(vm.Rows.OfType<AssistantRowViewModel>().Last(a => a.HasText).IsFolded);
        var group = vm.Rows.OfType<StepGroupRowViewModel>().First();
        Assert.Same(group, vm.Rows[vm.Rows.IndexOf(group.Steps[0]) - 1]);
        Assert.Equal("exit 127", Assert.Single(vm.Rows.OfType<ToolRowViewModel>(), t => t.IsFailed).FailureText);
        var end = vm.Rows.OfType<TurnEndRowViewModel>().Single();
        Assert.Equal(["src/hello.py", "tests/test_greet.py New", "docs/GREETING.md New", "src/legacy_greet.py Deleted", "src/helpers.py Renamed", "src/cli.py", "tests/test_cli.py"],
            end.Files.Select(f => (f.Path + " " + f.KindText).Trim()));
        fold.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(group.IsFolded);
        Assert.All(group.Steps, t => Assert.True(t.IsFolded));
        group.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Assert.All(group.Steps, t => Assert.False(t.IsFolded));
        await vm.DisposeAsync();
        w.Close();
    }

    /// <summary>A conversation loaded from its file groups as it did live: one assistant message calling three tools at
    /// once is one step line, folded with the reply before it under the turn's line.</summary>
    [AvaloniaFact]
    public async Task A_loaded_history_groups_its_steps_and_folds_its_turn()
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
        var group = Assert.Single(vm.Rows.OfType<StepGroupRowViewModel>());
        Assert.Equal("Searched the code, read files", group.Summary);
        Assert.All(vm.Rows.OfType<ToolRowViewModel>(), t => Assert.True(t.IsFolded));
        Assert.True(group.IsFolded);
        Assert.Single(vm.Rows.OfType<TurnFoldRowViewModel>());
        Assert.Equal(["Looking."], vm.Rows.OfType<AssistantRowViewModel>().Where(a => a.IsFolded).Select(a => a.Text));
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
    public void Messages_say_when_they_were_written_as_codex_does()
    {
        var now = new DateTimeOffset(2026, 10, 14, 15, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 14)));
        var was = MessageTimes.Now;
        MessageTimes.Now = () => now;
        try
        {
            Assert.Equal("14:08", MessageTimes.Short(now.Date.AddHours(14).AddMinutes(8)));
            Assert.Equal("29 Sep, 12:13", MessageTimes.Short(new DateTimeOffset(2026, 9, 29, 12, 13, 0, now.Offset)));
            Assert.Equal("", MessageTimes.Short(null));
            Assert.Equal("Today, 14:08", MessageTimes.Day(now.Date.AddHours(14).AddMinutes(8)));
            Assert.Equal("Yesterday, 09:00", MessageTimes.Day(now.Date.AddDays(-1).AddHours(9)));
            Assert.Equal("Monday, 11:06", MessageTimes.Day(new DateTimeOffset(2026, 10, 12, 11, 6, 0, now.Offset)));
            Assert.Equal("Tue, 29 Sep at 12:09", MessageTimes.Day(new DateTimeOffset(2026, 9, 29, 12, 9, 0, now.Offset)));
            Assert.Equal("Mon, 6 Oct 2025 at 08:00", MessageTimes.Day(new DateTimeOffset(2025, 10, 6, 8, 0, 0, now.Offset)));
        }
        finally { MessageTimes.Now = was; }
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
        var view = new Ombra.App.Controls.MarkdownView { Markdown = text };
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
