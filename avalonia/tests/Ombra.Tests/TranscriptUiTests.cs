using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
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
    public async Task Terminal_control_sequences_are_visible_in_notice_and_command_output()
    {
        const string source = "before \u001b[31mred\u001b[0m\u001b]0;title\u0007 after\u009b31m\r\n";
        const string expected = "before ␛[31mred␛[0m␛]0;title␇ after⟦U+009B⟧31m\r\n";
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        try
        {
            vm.Apply(new SessionSnapshot(1, SessionPhase.Ready,
                [new NoticeItem(1, NoticeLevel.Warning, source), new CommandOutputItem(2, source)],
                null, 2, null, 0, 0, null, null, null, []));
            window.Show();
            await Until(() => window.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Count(t => t.IsEffectivelyVisible && t.Text == expected) == 2, "visible terminal control markers");
            Assert.Equal(source, Assert.IsType<NoticeItem>(vm.Rows.First(r => r is NoticeRowViewModel).Model).Text);
            Assert.Equal(source, Assert.IsType<CommandOutputItem>(vm.Rows.First(r => r is CommandOutputRowViewModel).Model).Text);
        }
        finally
        {
            await vm.DisposeAsync();
            window.Close();
        }
    }
    [AvaloniaFact]
    public async Task Extension_widgets_and_status_labels_display_controls_without_changing_sources()
    {
        const string source = "日本語 e\u0301 👩‍💻 \u001b[31mred\u001b[0m\u001b]0;title\u0007\u009b31m\u009dosc\u009c";
        const string expected = "日本語 e\u0301 👩‍💻 ␛[31mred␛[0m␛]0;title␇⟦U+009B⟧31m⟦U+009D⟧osc⟦U+009C⟧";
        const string whitespace = "\r\n\tsecond line";
        var snapshot = new SessionSnapshot(1, SessionPhase.Running,
            [new ToolItem(1, "tool", source, "{}", ToolStatus.Running, null)],
            source, 2, null, 0, 0, null, null, null, [])
        {
            ExtensionStatus = new Dictionary<string, string> { ["extension"] = source + whitespace },
            Widgets = [new("above", [source + whitespace], false), new("below", [source], true)],
        };
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        try
        {
            vm.Apply(snapshot);
            window.Show();
            var widgetText = expected + whitespace + "\n" + expected;
            await Until(() => window.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Any(t => t.IsEffectivelyVisible && t.Text == widgetText), "safe extension widgets");
            var activity = window.FindControl<Grid>("ActivityLine")!;
            Assert.True(activity.IsEffectivelyVisible);
            Assert.Contains(expected, vm.StatusText);
            Assert.Contains(activity.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.StatusText);
            Assert.Equal(vm.StatusDetail, ToolTip.GetTip(activity));
            Assert.Contains(expected + whitespace, vm.StatusDetail);
            foreach (var text in new[] { vm.WidgetsText, vm.StatusText, vm.StatusDetail })
                Assert.DoesNotContain(text, c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

            vm.Apply(snapshot with { Version = 3, Phase = SessionPhase.Faulted, LastError = source + "\nnot the headline" });
            Assert.Contains(expected, vm.StatusText);
            Assert.DoesNotContain('\u001b', vm.StatusText);
            Assert.Equal(source, snapshot.Model);
            Assert.Equal(source, Assert.IsType<ToolItem>(Assert.Single(snapshot.Items)).Name);
            Assert.Equal(source + whitespace, snapshot.ExtensionStatus["extension"]);
            Assert.Equal(source + whitespace, snapshot.Widgets[0].Lines[0]);
            Assert.Equal(source, snapshot.Widgets[1].Lines[0]);
        }
        finally
        {
            await vm.DisposeAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task History_prepend_and_orphan_reorder_keep_row_and_expansion_identity()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        try
        {
            var user = new UserItem(10, "same question", true);
            var tool = Step(12, "read");
            var other = Step(13, "grep");
            SessionSnapshot Snap(long version, params TranscriptItem[] items) =>
                new(version, SessionPhase.Ready, items, null, 2, null, 0, 0, null, null, null, []);
            vm.Apply(Snap(1, user, tool, other));
            var keptUser = vm.Rows.OfType<UserRowViewModel>().Single();
            var keptTool = vm.Rows.OfType<ToolRowViewModel>().First();
            var keptGroup = vm.Rows.OfType<StepGroupRowViewModel>().Single();
            var keptFold = vm.Rows.OfType<TurnFoldRowViewModel>().Single();
            keptTool.IsExpanded = keptGroup.IsExpanded = keptFold.IsExpanded = true;
            var earlier = new UserItem(20, "earlier", true);
            vm.Apply(Snap(2, earlier, user, tool, other));
            Assert.Equal(new long[] { 20, 10, 12, 13 }, vm.Rows
                .Where(r => r is not StepGroupRowViewModel and not TurnFoldRowViewModel).Select(r => r.Model.Key));
            Assert.Same(keptUser, vm.Rows.OfType<UserRowViewModel>().Last());
            Assert.Same(keptTool, vm.Rows.OfType<ToolRowViewModel>().First());
            Assert.Same(keptGroup, vm.Rows.OfType<StepGroupRowViewModel>().Single());
            Assert.Same(keptFold, vm.Rows.OfType<TurnFoldRowViewModel>().Single());
            Assert.True(keptTool.IsExpanded && keptGroup.IsExpanded && keptFold.IsExpanded);
            // A result seen before its older call is hydrated and moved to that call's position.
            vm.Apply(Snap(3, earlier, tool with { Summary = "hydrated call" }, user, other));
            Assert.Equal(new long[] { 20, 12, 10, 13 }, vm.Rows
                .Where(r => r is not StepGroupRowViewModel and not TurnFoldRowViewModel).Select(r => r.Model.Key));
            Assert.Same(keptTool, vm.Rows.OfType<ToolRowViewModel>().First());
            Assert.True(keptTool.IsExpanded);
            Assert.Equal("hydrated call", keptTool.Summary);
            var later = new UserItem(21, "live append", true);
            vm.Apply(Snap(4, earlier, keptTool.Model, user, other, later));
            Assert.Same(keptUser, vm.Rows.OfType<UserRowViewModel>().ElementAt(1));
            Assert.Equal(21, vm.Rows.Last().Model.Key);
        }
        finally { await vm.DisposeAsync(); }
    }

    [Fact]
    public void Rewind_matches_equal_text_from_the_loaded_suffix_not_the_unloaded_prefix()
    {
        var target = new UserRowViewModel(new UserItem(10, "same\r\nquestion", true));
        var later = new UserRowViewModel(new UserItem(12, " same\nquestion ", true));
        RowList rows = [target, later, new UserRowViewModel(new UserItem(13, "same\nquestion", false))];
        Assert.Equal(1, MainViewModel.RewindOccurrenceFromEnd(rows, target));
        Assert.Equal(0, MainViewModel.RewindOccurrenceFromEnd(rows, later));
        rows.Insert(0, new UserRowViewModel(new UserItem(2, "same\nquestion", true)));
        Assert.Equal(1, MainViewModel.RewindOccurrenceFromEnd(rows, target));
        Assert.Equal(-1, MainViewModel.RewindOccurrenceFromEnd(rows, new UserRowViewModel(new UserItem(99, "missing", true))));
    }

    [Fact]
    public void Rewind_brings_back_the_images_of_the_message_it_starts_over_from()
    {
        var picture = new ImageAttachment("image 1", "image/png", [1, 2, 3]);
        RowList rows = [
            new UserRowViewModel(new UserItem(1, "look", true) { Images = [picture] }),
            new UserRowViewModel(new UserItem(2, "look", true)),
        ];
        (string, string)[] messages = [("e1", "look"), ("e2", "look")];
        Assert.Equal([picture], MainViewModel.RewindImages(rows, messages, 0));
        Assert.Empty(MainViewModel.RewindImages(rows, messages, 1));
        // Its row not loaded (an earlier page of the history): the text comes back alone
        Assert.Empty(MainViewModel.RewindImages(rows, [("e0", "earlier"), .. messages], 0));
    }

    [AvaloniaFact]
    public async Task Stale_history_requires_explicit_bounded_reload_and_disables_older_while_busy()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        try
        {
            var snapshot = new SessionSnapshot(1, SessionPhase.Ready, [new UserItem(1, "kept", true)],
                null, 2, null, 0, 0, null, null, null, [])
            {
                History = new(100, 350, true, false, false, null),
            };
            vm.Apply(snapshot);
            Assert.True(vm.LoadOlderMessagesCommand.CanExecute(null));
            var kept = vm.Rows.Single();
            vm.Apply(snapshot with { Version = 2, History = new(100, 350, true, false, true, "stale cursor") });
            Assert.False(vm.LoadOlderMessagesCommand.CanExecute(null));
            Assert.True(vm.ReloadHistoryCommand.CanExecute(null));
            Assert.Contains(Loc.T("Reload shows only the latest page of messages and moves your view to the end."), vm.HistoryStatus);
            Assert.Same(kept, vm.Rows.Single());
            vm.Apply(snapshot with { Version = 3, Phase = SessionPhase.Running });
            Assert.False(vm.LoadOlderMessagesCommand.CanExecute(null));
            vm.Apply(snapshot with { Version = 4, History = new(100, 350, true, true, false, null) });
            Assert.False(vm.LoadOlderMessagesCommand.CanExecute(null));
            vm.Apply(snapshot with { Version = 5, History = new(100, 350, true, false, false, "try again") });
            Assert.True(vm.LoadOlderMessagesCommand.CanExecute(null));
            Assert.Equal("try again", vm.HistoryStatus);
            Assert.Contains(Loc.T("Retry older messages"), vm.HistoryActionText);
        }
        finally { await vm.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task Initial_busy_history_error_remains_visible_with_the_explicit_reload_warning()
    {
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 760 };
        try
        {
            const string error = "Session is busy. Wait until it is idle, then reload history.";
            var snapshot = new SessionSnapshot(1, SessionPhase.Ready, [], null, 2, null, 0, 0, null, null, null, [])
            {
                History = new(0, 0, false, false, true, error),
            };
            vm.Apply(snapshot);
            w.Show();
            Dispatcher.UIThread.RunJobs();
            var status = w.FindControl<StackPanel>("HistoryStatusPanel")!;
            Assert.True(status.IsVisible);
            Assert.StartsWith(error, vm.HistoryStatus);
            Assert.Contains(Loc.T("Reload shows only the latest page of messages and moves your view to the end."), vm.HistoryStatus);
            Assert.Contains(status.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == vm.HistoryStatus);
            Assert.True(w.FindControl<Button>("ReloadHistory")!.IsVisible);
            Assert.True(vm.ReloadHistoryCommand.CanExecute(null));
            Assert.False(vm.LoadOlderMessagesCommand.CanExecute(null));
            // A second busy response must not turn into the unrelated generic stale-history explanation.
            vm.Apply(snapshot with { Version = 2, History = new(0, 0, false, false, true, "Still busy; retry when idle.") });
            Assert.StartsWith("Still busy; retry when idle.", vm.HistoryStatus);
            Assert.Contains(Loc.T("Reload shows only the latest page of messages and moves your view to the end."), vm.HistoryStatus);
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_clients_own_history_errors_read_in_the_ui_language()
    {
        // Regression (ru): SessionController's history errors showed in English above a Russian conversation.
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        try
        {
            var snapshot = new SessionSnapshot(1, SessionPhase.Ready, [], null, 2, null, 0, 0, null, null, null, []);
            var explanation = Loc.T("Reload shows only the latest page of messages and moves your view to the end.");
            foreach (var error in new[]
            {
                "Recent history is unavailable while omp is working. Live messages remain visible; reload recent messages when idle.",
                "History changed since it was loaded. Reload recent messages to browse the new history; your current view has been kept.",
            })
            {
                vm.Apply(snapshot with { Version = snapshot.Version + 1, History = new(0, 0, false, false, true, error) });
                Assert.Equal($"{Loc.T(error)} {explanation}", vm.HistoryStatus);
                snapshot = snapshot with { Version = snapshot.Version + 1 };
            }
            const string paused = "History is paused while omp is working. Try again when the session is idle.";
            vm.Apply(snapshot with { Version = 10, History = new(100, 350, true, false, false, paused) });
            Assert.Equal(Loc.T(paused), vm.HistoryStatus);
            vm.Apply(snapshot with { Version = 11, History = new(0, 0, false, false, true, "The saved conversation could not be read: bad line 3") });
            Assert.Equal($"{Loc.T("The saved conversation could not be read: ")}bad line 3 {explanation}", vm.HistoryStatus);
        }
        finally { await vm.DisposeAsync(); }
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
        Assert.Contains(root.GetVisualDescendants().OfType<Button>(), b => Avalonia.Automation.AutomationProperties.GetName(b) == Loc.T("Copy code"));
        Assert.Contains(texts, t => (t.Text ?? string.Concat(t.Inlines!.OfType<Run>().Select(r => r.Text))) == "var x = 1;");
        Assert.Contains(root.GetVisualDescendants().OfType<Grid>(), g => g.Classes.Contains("md-table") && g.Children.Count(c => c.Classes.Contains("md-cell")) == 4);
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
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Any(t => t.StatusText == Loc.T("done")), "run end");
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
        void Capture(AssistantRowViewModel _, string t) => copied = t;
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
        Assert.Equal((Loc.T("Running"), "make"), (running.Verb, running.ArgText));
        Assert.Equal(Loc.T("Ran"), Row("bash", "x").Verb);
        Assert.Equal((Loc.T("Reading"), Loc.T("Read")), (Row("read", null, status: ToolStatus.Running).Verb, Row("read", "a").Verb));
        // A file change says what it did to its one file; a call that changed two is "Edited"
        Assert.Equal(Loc.T("Created"), Row("edit", "ok", changes: [new("a.py", FileChangeKind.Created, "+1|a")]).Verb);
        Assert.Equal(Loc.T("Created"), Row("write", "ok", changes: [new("a.py", FileChangeKind.Created, "+1|a")]).Verb);
        Assert.Equal(Loc.T("Wrote"), Row("write", "ok").Verb);
        Assert.Equal(Loc.T("Deleted"), Row("edit", "ok", changes: [new("a.py", FileChangeKind.Deleted, "-1|a")]).Verb);
        // A move across folders is "Moved"; within its folder, "Renamed"
        var moved = Row("edit", "ok", changes: [new("lib/b.py", FileChangeKind.Moved, null, "src/a.py")]);
        Assert.Equal((Loc.T("Moved"), "src/a.py → lib/b.py"), (moved.Verb, moved.ArgText));
        Assert.Equal(Loc.T("Renamed"), Row("edit", "ok", changes: [new("src/b.py", FileChangeKind.Moved, null, "src/a.py")]).Verb);
        Assert.Equal(Loc.T("Edited"), Row("edit", "ok", summary: "2 files", changes: [new("a", FileChangeKind.Modified, null), new("b", FileChangeKind.Created, null)]).Verb);
        // Tools without a verb keep their name (the approval card names them the same way)
        Assert.Equal("Create issue (github)", ToolRowViewModel.VerbOf("mcp__github__create_issue", running: false));
        Assert.Equal(Loc.T("Search"), ToolRowViewModel.DisplayNameOf("grep"));
        // A search says what it looked for, and where
        Assert.Equal(Loc.F("{0} in {1}", "greet\\(", "src"), Row("grep", "", summary: "src", args: """{"pattern":"greet\\(","path":"src"}""").ArgText);
        // What a read or a search found: a few words at the line's end, no second line
        var read = Row("read", "a\nb\nc\n");
        Assert.Equal((Loc.F("{0} lines", 3), false), (read.Meta, read.ShowResult));
        Assert.Equal(Loc.Plural(12, "1 file", "{0} files"), Row("glob", "a\nb", note: "Found 12 files").Meta);
        // omp's other search notes (ConversationState.ResultNote) read in the UI's language too
        Assert.Equal(Loc.T("1 file"), Row("glob", "a", note: "Found 1 file").Meta);
        Assert.Equal(Loc.F("{0}+ files", 100), Row("glob", "a", note: "Found 100+ files").Meta);
        Assert.Equal(Loc.T("No files found"), Row("glob", "", note: "No files found").Meta);
        Assert.Equal(Loc.T("1 match"), Row("grep", "a", note: "Found 1 match").Meta);
        Assert.Equal(Loc.F("{0} matches in {1} files", 5, 2), Row("grep", "a", note: "Found 5 matches in 2 files").Meta);
        Assert.Equal(Loc.F("{0}+ matches", 100), Row("grep", "a", note: "Found 100+ matches").Meta);
        Assert.Equal(Loc.T("No matches"), Row("grep", "", note: "No matches").Meta);
        // A file change: no words under it (its +N −M and the change say it)
        Assert.False(Row("edit", "ok", diff: "-   2|a\n+   2|b\n").ShowResult);
        Assert.Equal(Loc.T("Interrupted"), Row("bash", null, status: ToolStatus.Interrupted).Meta);
        // Durations under a tenth of a second say nothing
        Assert.Equal("", Row("bash", "x", took: TimeSpan.FromMilliseconds(40)).TookText);
        Assert.Equal(Loc.F("{0}.{1}s", 1, 2), Row("bash", "x", took: TimeSpan.FromSeconds(1.2)).TookText);
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
        Assert.Equal((Loc.F("exit {0}", 127), Loc.F("Exit code {0}", 127), false, true), (failed.FailureText, failed.ExitText, failed.ShowConsole, failed.CanExpand));
        failed.IsExpanded = true;
        Assert.Equal("bash: ruff: command not found", failed.ConsoleOutput);
        var ok = Row("bash", "ok\n\nWall time: 0.00 seconds");
        ok.IsExpanded = true;
        Assert.Equal(("ok", false), (ok.ConsoleOutput, ok.HasExitText));
        // A command the user ran (the client's "exit code N"), a timeout, a tool without an exit code
        Assert.Equal(Loc.F("exit {0}", 1), Row("bash", "boom\nexit code 1", status: ToolStatus.Failed).FailureText);
        Assert.Equal(Loc.T("timed out"), Row("bash", "partial\n\n[Command timed out after 30 seconds]", status: ToolStatus.Failed).FailureText);
        Assert.Equal(Loc.T("failed"), Row("edit", "Could not find the text", status: ToolStatus.Failed).FailureText);
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

    /// <summary>A step group's line as StepKinds.Describe words it: the phrases in the UI's language, later ones lowercased.</summary>
    private static string Did(params string[] phrases) =>
        string.Join(", ", phrases.Select(Loc.T).Select((t, i) => i > 0 ? char.ToLower(t[0], System.Globalization.CultureInfo.CurrentCulture) + t[1..] : t));

    [Fact]
    public void Steps_in_a_row_fold_into_one_line_that_says_what_they_did()
    {
        var rows = new Rows();
        rows.Apply(new UserItem(1, "look", true), Reply(2, "I'll look around."), Step(3, "read"), Reply(4, thinking: "hmm"), Step(5, "grep", summary: "src"),
            Reply(6), Step(7, "read", summary: "src/b.py"), Reply(8, "Now the tests."), Step(9, "read"));
        // The replies with nothing to read between the steps fold with them; text ends the group; a lone step stays a row
        Assert.Equal($"UserRowViewModel text G:{Did("Read files", "Searched the code")} [read] [a] [grep] [a] [read] text read", rows.Shape());
        var group = rows.List.OfType<StepGroupRowViewModel>().Single();
        Assert.Equal(StepKind.Read, group.Kind);
        group.IsExpanded = true;
        Assert.Equal($"UserRowViewModel text G:{Did("Read files", "Searched the code")} read a grep a read text read", rows.Shape());
        Assert.Equal([1, 1, 1, 1, 1, 0, 0], rows.List.Skip(3).Select(r => r.Depth)); // the steps and the replies between them hang under the line

        // A failed step ends a group (and is shown); one that fails as the second step undoes the group
        var failing = new Rows();
        failing.Apply(Step(1, "read"), Step(2, "grep", ToolStatus.Running));
        Assert.Equal($"G:{Loc.T("Searching")} src/a.py [read] [grep]", failing.Shape());
        failing.Apply(Step(2, "grep", ToolStatus.Failed));
        Assert.Equal("read grep", failing.Shape());
        failing.Apply(Step(3, "glob"), Step(4, "read"));
        Assert.Equal($"read grep G:{Did("Searched the code", "Read a file")} [glob] [read]", failing.Shape());

        // Any tools group, each kind said once in the order it came: one file read twice is "a file"
        var mixed = new Rows();
        mixed.Apply(Step(1, "read"), Step(2, "bash", summary: "$ make"), Step(3, "read"), Step(4, "bash", summary: "$ make test"));
        Assert.Equal($"G:{Did("Read a file", "Ran commands")} [read] [bash] [read] [bash]", mixed.Shape());
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
        Assert.Equal($"UserRowViewModel text G:{Did("Read a file", "Searched the code")} [read] [a] [grep] [a] [glob] text G:{Did("Ran a command", "Read a file", "Listed a folder")} [bash] [a] [read] [a] [ls]", live.Shape());
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
        var did = Did("Read a file", "Searched the code");
        Assert.Equal($"UserRowViewModel F:{Loc.T("Activity")} text G:{did} [read] [grep] text", rows.Shape());
        // "Activity" folds nothing, so nothing hangs under it; a group's steps hang under the group's line
        int[] Depths() => [.. rows.List.Select(r => r.Depth)];
        Assert.Equal([0, 0, 0, 0, 1, 1, 0], Depths());
        rows.Fold(running: false);
        var worked = Loc.F("Worked for {0}", Loc.F("{0}m {1}s", 1, 18));
        Assert.Equal($"UserRowViewModel F:{worked} › [text] [G:{did}] [read] [grep] text", rows.Shape());
        var fold = rows.List.OfType<TurnFoldRowViewModel>().Single();
        fold.IsExpanded = true;
        Assert.Equal($"UserRowViewModel F:{worked} › text G:{did} [read] [grep] text", rows.Shape());
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
        Assert.Equal($"UserRowViewModel F:{Loc.F("Stopped after {0}", Loc.F("{0}s", 12))} › [bash] TurnEndRowViewModel", stopped.Shape());
    }

    [Fact]
    public void A_turns_length_is_said_in_whole_units()
    {
        Assert.Equal(Loc.F("{0}s", 1), TurnEndRowViewModel.TurnDuration(TimeSpan.FromSeconds(0.4)));
        Assert.Equal(Loc.F("{0}s", 17), TurnEndRowViewModel.TurnDuration(TimeSpan.FromSeconds(17.2)));
        Assert.Equal(Loc.F("{0}m {1}s", 1, 18), TurnEndRowViewModel.TurnDuration(TimeSpan.FromSeconds(78)));
        Assert.Equal(Loc.F("{0}h {1}m", 1, 1), TurnEndRowViewModel.TurnDuration(TimeSpan.FromSeconds(3700)));
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
        Assert.Equal(["src/a.py +2 −1", $"new.md {Loc.T("New")} +2", $"lib/helpers.py {Loc.T("Moved")} +1 −1", $"old.py {Loc.T("Deleted")} −2"],
            end.Files.Select(f => string.Join(" ", new[] { f.Path, f.KindText, f.AddedText, f.RemovedText }.Where(s => s.Length > 0))));
        Assert.Equal(Loc.F("from {0}", "src/util.py"), end.Files[2].MovedFromText);
        Assert.Equal(new FileLink("src/a.py", 2), end.Files[0].Link);
        Assert.Equal(("lib/", "helpers.py"), (end.Files[2].FolderPrefix, end.Files[2].Name));
        // Codex's card: its header sums the files up; four rows show whole, more fold behind "Show N more files"
        Assert.Equal((Loc.Plural(4, "Edited {0} file", "Edited {0} files"), "+5", "−4", 4, false), (end.Title, end.AddedText, end.RemovedText, end.ShownFiles.Count, end.HasMoreFiles));
        var five = new TurnEndRowViewModel(new TurnEndItem(2, TimeSpan.FromSeconds(9), false, 5, 5));
        five.SetFiles([.. Enumerable.Range(1, 5).Select(i => Row("write", "ok", changes: [new($"f{i}.txt", FileChangeKind.Created, "+1|x\n")]))]);
        Assert.Equal((3, true, Loc.Plural(2, "Show {0} more file", "Show {0} more files")), (five.ShownFiles.Count, five.HasMoreFiles, five.MoreFilesText));
        five.ShowAllFilesCommand.Execute(null);
        Assert.Equal((5, false), (five.ShownFiles.Count, five.HasMoreFiles));
        var one = new TurnEndRowViewModel(new TurnEndItem(3, TimeSpan.FromSeconds(9), false, 1, 1));
        one.SetFiles([Row("edit", "ok", changes: [new("src/serve.cjs", FileChangeKind.Modified, "+1|x\n")])]);
        Assert.Equal((Loc.F("Edited {0}", "serve.cjs"), false), (one.Title, one.HasFileRows));
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
        Assert.StartsWith(Loc.F("Worked for {0}", "\0").Split('\0')[0], fold.Label);
        Assert.Same(fold, vm.Rows[vm.Rows.IndexOf(vm.Rows.OfType<UserRowViewModel>().Single()) + 1]);
        Assert.All(vm.Rows.OfType<ToolRowViewModel>(), t => Assert.True(t.IsFolded));
        Assert.False(vm.Rows.OfType<AssistantRowViewModel>().Last(a => a.HasText).IsFolded);
        var group = vm.Rows.OfType<StepGroupRowViewModel>().First();
        Assert.Same(group, vm.Rows[vm.Rows.IndexOf(group.Steps[0]) - 1]);
        Assert.Equal(Loc.F("exit {0}", 127), Assert.Single(vm.Rows.OfType<ToolRowViewModel>(), t => t.IsFailed).FailureText);
        var end = vm.Rows.OfType<TurnEndRowViewModel>().Single();
        Assert.Equal(["src/hello.py", $"tests/test_greet.py {Loc.T("New")}", $"docs/GREETING.md {Loc.T("New")}", $"src/legacy_greet.py {Loc.T("Deleted")}", $"src/helpers.py {Loc.T("Renamed")}", "src/cli.py", "tests/test_cli.py"],
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
        Assert.Equal(Did("Searched the code", "Read files"), group.Summary);
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
            Assert.Equal(Loc.F("{0}, {1}", "29 " + Loc.T("Sep"), "12:13"), MessageTimes.Short(new DateTimeOffset(2026, 9, 29, 12, 13, 0, now.Offset)));
            Assert.Equal("", MessageTimes.Short(null));
            Assert.Equal(Loc.F("Today, {0}", "14:08"), MessageTimes.Day(now.Date.AddHours(14).AddMinutes(8)));
            Assert.Equal(Loc.F("Yesterday, {0}", "09:00"), MessageTimes.Day(now.Date.AddDays(-1).AddHours(9)));
            Assert.Equal(Loc.F("{0}, {1}", Loc.T("Monday"), "11:06"), MessageTimes.Day(new DateTimeOffset(2026, 10, 12, 11, 6, 0, now.Offset)));
            Assert.Equal(Loc.F("{0}, {1} at {2}", Loc.T("Tue"), "29 " + Loc.T("Sep"), "12:09"), MessageTimes.Day(new DateTimeOffset(2026, 9, 29, 12, 9, 0, now.Offset)));
            Assert.Equal(Loc.F("{0}, {1} at {2}", Loc.T("Mon"), "6 " + Loc.T("Oct") + " 2025", "08:00"), MessageTimes.Day(new DateTimeOffset(2025, 10, 6, 8, 0, 0, now.Offset)));
        }
        finally { MessageTimes.Now = was; }
    }

    [Fact]
    public void An_approval_says_what_omp_wants_and_shows_the_command_alone()
    {
        static DialogViewModel Approval(string title) => new(TestApprovals.Dialog(title, "a"), (_, _) => Task.CompletedTask);
        var bash = Approval("Allow tool: bash\nReason: outside the project\nCommand: rm -rf build");
        Assert.Equal((Loc.T("omp wants to run a command"), "rm -rf build", "outside the project"), (bash.Title, bash.Preview, bash.Note));
        Assert.Equal("Allow tool: bash", bash.Headline); // omp's own words stay for the log
        var write = Approval("Allow tool: write\nPath: a.txt\nContent:\nhello");
        Assert.Equal((Loc.T("omp wants to write a file"), "Path: a.txt\nContent:\nhello"), (write.Title, write.Preview));
        Assert.Equal(Loc.F("omp wants to use {0}", "Lookup (docs)"), Approval("Allow tool: mcp__docs__lookup\nOrigin: MCP server tool").Title);
        // ReviewLease P2: where a command runs is on the card, beside the command itself
        var elsewhere = Approval("Allow tool: bash\nCommand: npm test\nCwd: ../other");
        Assert.Equal(("npm test", Loc.F("Runs in {0}", "../other")), (elsewhere.Preview, elsewhere.Note));
        Assert.False(elsewhere.CanAllowWithRule);
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
