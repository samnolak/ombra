using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// The extremes a user reaches and the demo data never does: long titles, project names and paths, wide tables,
/// long lines and commands, big diffs, failed tools, long plans, a crowded sidebar, a long message being typed —
/// in both themes, wide and narrow, laid out and audited (LayoutAudit) like every other screen.
/// </summary>
public sealed class QaEdgeCaseTests(ITestOutputHelper log)
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");

    private static async Task Settle(int ms = 300)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private static async Task Until(Func<bool> c, int seconds = 20)
    {
        var d = DateTime.UtcNow.AddSeconds(seconds);
        while (!c() && DateTime.UtcNow < d) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        Dispatcher.UIThread.RunJobs();
    }

    private static object Text(string t) => new { type = "text", text = t };

    private static object User(string t) => new { role = "user", content = new[] { Text(t) } };

    private static object Assistant(string text, params object[] calls) =>
        new { role = "assistant", content = new object[] { Text(text) }.Concat(calls).ToArray(), stopReason = calls.Length > 0 ? "toolUse" : "stop" };

    private static object Call(string id, string name, object args) => new { type = "toolCall", id, name, arguments = args };

    private static object Result(string id, string name, string output, bool error = false, object? details = null) =>
        new { role = "toolResult", toolCallId = id, toolName = name, content = new[] { Text(output) }, isError = error, details };

    /// <summary>A session file as the fake omp writes it (title, header with the project folder, messages).</summary>
    private static string Session(string root, string cwd, string title, IEnumerable<object> messages)
    {
        var project = Path.Combine(root, "-" + string.Concat(cwd.Select(c => char.IsLetterOrDigit(c) ? c : '-')));
        Directory.CreateDirectory(project);
        var id = Guid.NewGuid().ToString();
        var file = Path.Combine(project, $"{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ss-fffZ}_{id}.jsonl");
        var sb = new StringBuilder();
        sb.AppendLine(JsonSerializer.Serialize(new { type = "title", v = 1, title }));
        sb.AppendLine(JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd }));
        foreach (var m in messages) sb.AppendLine(JsonSerializer.Serialize(new { type = "message", message = m }));
        File.WriteAllText(file, sb.ToString());
        return file;
    }

    private static IEnumerable<object> Extremes(string cwd)
    {
        var longPath = "src/" + string.Join("/", Enumerable.Range(1, 9).Select(i => $"deeply-nested-module-{i}")) + "/a_file_with_a_rather_long_name_for_testing.py";
        yield return User("Please look at " + "https://example.com/" + new string('x', 180) + "?query=" + new string('y', 60) + "\n\nand also fix the build. Thanks!");
        yield return Assistant("""
            ## Findings in a rather long heading that goes on and on so it has to wrap somewhere near the end

            | Module | Owner | Status | Coverage | Last change | Notes | Size | Risk |
            |---|---|---|---|---|---|---|---|
            | parser | alice@example.com | failing | 41% | 2026-09-24 | the tokenizer drops trailing whitespace on CRLF input | 12 kB | high |
            | renderer | bob | ok | 88% | 2026-09-20 | — | 30 kB | low |

            ```python
            def an_extremely_long_function_name_that_keeps_going(argument_number_one, argument_number_two, argument_number_three, argument_number_four, and_more_arguments_until_it_is_clearly_too_long_for_one_line):
                return argument_number_one + argument_number_two
            ```

            - one level
              - two levels with `a_very_long_inline_code_span_that_never_breaks_anywhere_because_it_has_no_spaces_at_all`
                - three levels deep
            > A quote with **bold**, *italic* and a [link with a long title that wraps](https://example.com/a/very/long/path).
            """,
            Call("q1", "read", new { path = longPath }),
            Call("q2", "bash", new { command = "cd /workspace/project && " + string.Join(" && ", Enumerable.Range(1, 12).Select(i => $"python -m pytest tests/unit/test_module_{i}.py -q --maxfail=1")) }));
        yield return Result("q1", "read", string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}")));
        yield return Result("q2", "bash", string.Join("\n", Enumerable.Range(1, 60).Select(i => $"FAILED tests/unit/test_module_{i % 12}.py::test_case_{i} - AssertionError: expected {i} but got {i + 1}")), error: true);
        var diff = new StringBuilder();
        for (var i = 1; i <= 70; i++) diff.AppendLine(i % 5 == 0 ? $"-{i,4}|    old_value_{i} = compute_something_expensive(argument_{i}, flag=True)" : i % 5 == 1 && i > 1 ? $"+{i,4}|    new_value_{i} = compute_something_cheaper(argument_{i})  # a comment that makes this line very long indeed, past any reasonable width" : $" {i,4}|    context_line_{i} = {i}");
        yield return Assistant("Changing the parser now.", Call("q3", "edit", new { path = longPath, oldText = "a", newText = "b" }));
        yield return Result("q3", "edit", "Edited.", details: new { diff = diff.ToString() });
        var tasks = Enumerable.Range(1, 14).Select(i => new
        {
            content = i % 3 == 0 ? $"Task {i}: rewrite the tokenizer so that CRLF input keeps its trailing whitespace and the tests in tests/unit/test_tokenizer.py pass on Windows" : $"Task {i}: a shorter step",
            status = i < 5 ? "completed" : i == 5 ? "in_progress" : i == 6 ? "blocked" : i == 7 ? "abandoned" : "pending",
        }).ToArray();
        yield return Assistant("Here is the plan.", Call("q4", "todo", new { op = "init", items = tasks.Select(t => t.content).ToArray() }));
        yield return Result("q4", "todo", "Plan saved.", details: new { phases = new[] { new { name = "Fix the tokenizer and everything around it", tasks } } });
        yield return new { role = "assistant", content = Array.Empty<object>(), stopReason = "error", errorMessage = "Provider error 529: the model is overloaded. " + new string('z', 120) };
    }

    private async Task<(MainWindow w, MainViewModel vm)> Open(string root, string cwd, string? resume, double width, double height)
    {
        var s = new SessionController(TestProcesses.FakeFactory("normal", root), new LaunchRequest(cwd, ResumeSessionFile: resume));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase is SessionPhase.Ready or SessionPhase.Faulted);
        await Settle(600);
        return (w, vm);
    }

    private readonly StringBuilder _report = new();
    private int _findings;

    private void Take(Window w, string name)
    {
        using (var frame = w.CaptureRenderedFrame())
        {
            if (Dir is not null)
            {
                Directory.CreateDirectory(Path.Combine(Dir, "qa"));
                using var f = File.Create(Path.Combine(Dir, "qa", name + ".png"));
                frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        var found = LayoutAudit.Run(w);
        _findings += found.Count;
        _report.Append(LayoutAudit.Report(name, found));
    }

    [AvaloniaTheory]
    [MemberData(nameof(DesignMatrix.Extremes), MemberType = typeof(DesignMatrix))]
    public async Task Extremes_are_readable_and_aligned(string theme, double width, double height)
    {
        MainViewModel.ApplyTheme(theme);
        var tag = $"{theme}-{width}";
        var root = TestProcesses.TempDir("qa-sessions");
        var cwd = Path.Combine(TestProcesses.TempDir("qa"), "a-project-folder-with-a-really-long-name-that-goes-on-for-a-while");
        Directory.CreateDirectory(cwd);
        // A crowded sidebar: other projects with long names and many sessions
        for (var p = 0; p < 4; p++)
            for (var i = 0; i < 6; i++)
                Session(root, Path.Combine(Path.GetTempPath(), $"another-project-{p}-with-a-longish-name"), i % 2 == 0 ? $"Session {i} about the build pipeline and the flaky integration tests on Windows" : $"Quick fix {i}", [User("hi")]);
        var file = Session(root, cwd, "A very long session title that describes exactly what this conversation is about and then some more words", Extremes(cwd));
        var (w, vm) = await Open(root, cwd, file, width, height);
        try
        {
            // The provider error ends the conversation: it offers Retry; the dropped task counts neither way
            Assert.True(vm.Rows.OfType<AssistantRowViewModel>().Single(r => r.HasError).CanRetry);
            Assert.Equal("4 of 13 done", vm.Plan.ProgressText);
            Take(w, $"{tag}-conversation");
            // As a reader would: the wheel over the conversation (a programmatic scroll is pinned back to the end)
            var over = new Point(width / 2 + 60, 200);
            for (var i = 0; i < 80; i++) { w.MouseWheel(over, new Vector(0, 3), Avalonia.Input.RawInputModifiers.None); if (i % 10 == 0) await Settle(40); }
            await Settle(500);
            Take(w, $"{tag}-top");
            for (var i = 0; i < 12; i++) { w.MouseWheel(over, new Vector(0, -3), Avalonia.Input.RawInputModifiers.None); }
            await Settle(400);
            Take(w, $"{tag}-middle");
            for (var i = 0; i < 80; i++) w.MouseWheel(over, new Vector(0, -3), Avalonia.Input.RawInputModifiers.None);
            await Settle(300);
            var transcript = w.GetVisualDescendantsOf<ListBox>("Transcript");
            var scroll = transcript?.Scroll as ScrollViewer;
            foreach (var t in vm.Rows.OfType<ToolRowViewModel>()) t.IsExpanded = true;
            await Settle();
            // The failed command, opened: its long command line and output
            w.MouseWheel(over, new Vector(0, 3), Avalonia.Input.RawInputModifiers.None);
            if (vm.Rows.OfType<ToolRowViewModel>().FirstOrDefault(t => t.Name == "bash") is { } bash) transcript?.ScrollIntoView(bash);
            await Settle(400);
            Take(w, $"{tag}-tools-open");
            scroll?.ScrollToEnd();
            await Settle();
            Take(w, $"{tag}-end");
            vm.ShowPaneCommand.Execute(SidePane.Plan);
            await Settle(500);
            Take(w, $"{tag}-plan");
            vm.ClosePaneCommand.Execute(null);
            // A long message being typed, and six attachments
            vm.ComposerText = string.Join("\n", Enumerable.Range(1, 18).Select(i => $"Line {i} of a long request that explains in detail what should change and why it matters for the release."));
            await Settle();
            Take(w, $"{tag}-long-message");
            vm.ComposerText = "";
            vm.ToggleSidebarCommand.Execute(null);
            await Settle();
            Take(w, $"{tag}-sidebar-toggled");
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
        log.WriteLine(_report.ToString());
        if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "qa", "findings.tsv"), _report.ToString());
        Assert.True(_findings == 0, _report.ToString());
    }
}

/// <summary>
/// The README's picture of the client: a realistic conversation (a request, reads, an inline diff, tests passing, a
/// summary) with the Plan pane open, rendered like every other screen and held to the same layout audit.
/// </summary>
public sealed class ReadmeScreenshotTests
{
    private static readonly string? Out = Environment.GetEnvironmentVariable("OMPGUI_README_SHOT");

    private static object Text(string t) => new { type = "text", text = t };

    private static object Call(string id, string name, object args) => new { type = "toolCall", id, name, arguments = args };

    private static object Reply(string text, params object[] calls) =>
        new { role = "assistant", content = new object[] { Text(text) }.Concat(calls).ToArray(), stopReason = calls.Length > 0 ? "toolUse" : "stop" };

    private static object Result(string id, string name, string output, object? details = null) =>
        new { role = "toolResult", toolCallId = id, toolName = name, content = new[] { Text(output) }, isError = false, details };

    internal static string Write(string root, string cwd, string title, IEnumerable<object> messages, DateTime at)
    {
        var project = Path.Combine(root, "-" + string.Concat(cwd.Select(c => char.IsLetterOrDigit(c) ? c : '-')));
        Directory.CreateDirectory(project);
        var id = Guid.NewGuid().ToString();
        var file = Path.Combine(project, $"{at:yyyy-MM-ddTHH-mm-ss-fffZ}_{id}.jsonl");
        var sb = new StringBuilder();
        sb.AppendLine(JsonSerializer.Serialize(new { type = "title", v = 1, title }));
        sb.AppendLine(JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd }));
        foreach (var m in messages) sb.AppendLine(JsonSerializer.Serialize(new { type = "message", message = m }));
        File.WriteAllText(file, sb.ToString());
        File.SetLastWriteTimeUtc(file, at);
        return file;
    }

    internal static IEnumerable<object> Conversation()
    {
        yield return new { role = "user", content = new[] { Text("Add a --units flag to the forecast command (metric or imperial) and cover it with tests.") } };
        yield return Reply("I'll read the command and the formatter first.",
            Call("r1", "read", new { path = "src/weather/cli.py" }), Call("r2", "read", new { path = "src/weather/format.py" }));
        yield return Result("r1", "read", "import argparse\n...");
        yield return Result("r2", "read", "def temperature(celsius):\n...");
        var phases = new[]
        {
            new { name = "Understand", tasks = new[] { new { content = "Read the forecast command", status = "completed" }, new { content = "Find where temperatures are formatted", status = "completed" } } },
            new { name = "Build", tasks = new[]
            {
                new { content = "Add --units to the forecast command", status = "completed" },
                new { content = "Convert temperature and wind speed in the formatter", status = "in_progress" },
                new { content = "Tests for both unit systems", status = "pending" },
            } },
            new { name = "Ship", tasks = new[] { new { content = "Update the usage section of the README", status = "pending" } } },
        };
        yield return Reply("Here is the plan.", Call("t1", "todo", new { op = "init" }));
        yield return Result("t1", "todo", "Plan saved.", details: new { phases });
        yield return Reply("", Call("e1", "edit", new { path = "src/weather/cli.py", oldText = "a", newText = "b" }));
        yield return Result("e1", "edit", "Edited.", details: new
        {
            diff = """
                  18|    parser = argparse.ArgumentParser(prog="forecast")
                  19|    parser.add_argument("city")
                  20|    parser.add_argument("--days", type=int, default=3)
                + 21|    parser.add_argument(
                + 22|        "--units", choices=["metric", "imperial"], default="metric",
                + 23|        help="degrees Celsius and km/h, or Fahrenheit and mph")
                  24|    args = parser.parse_args(argv)
                - 25|    print(render(fetch(args.city, args.days)))
                + 25|    print(render(fetch(args.city, args.days), units=args.units))
                """,
        });
        yield return Reply("", Call("b1", "bash", new { command = "python -m pytest -q" }));
        yield return Result("b1", "bash", "............                                     [100%]\n12 passed in 0.41s");
        yield return Reply("""
            `forecast` now takes `--units`:

            - `metric` (the default): °C and km/h
            - `imperial`: °F and mph

            The formatter converts both values in one place, and the 12 tests pass. Next I'll add tests for the imperial output.
            """);
    }

    [AvaloniaFact]
    public async Task Readme_window_is_clean()
    {
        MainViewModel.ApplyTheme("light");
        var root = TestProcesses.TempDir("readme-sessions");
        var home = TestProcesses.TempDir("readme");
        var cwd = Path.Combine(home, "weather-cli");
        Directory.CreateDirectory(cwd);
        var now = DateTime.UtcNow;
        Write(root, cwd, "Retry flaky HTTP calls with backoff", [new { role = "user", content = new[] { Text("hi") } }], now.AddHours(-3));
        Write(root, cwd, "Cache forecasts for ten minutes", [new { role = "user", content = new[] { Text("hi") } }], now.AddDays(-1));
        Write(root, Path.Combine(home, "site"), "Dark mode for the docs", [new { role = "user", content = new[] { Text("hi") } }], now.AddDays(-2));
        Write(root, Path.Combine(home, "site"), "Fix broken links in the changelog", [new { role = "user", content = new[] { Text("hi") } }], now.AddDays(-4));
        var file = Write(root, cwd, "Add a --units flag to forecast", Conversation(), now.AddMinutes(-2));

        var spec = TestProcesses.Fake("normal");
        var s = new SessionController(req => spec with
        {
            Arguments = [.. spec.Arguments, "--resume", file],
            WorkingDirectory = req.WorkingDirectory,
            Environment = new Dictionary<string, string?> { ["FAKE_SESSION_DIR"] = root, ["FAKE_MODEL"] = "local/coder" },
        }, new LaunchRequest(cwd, ResumeSessionFile: file, ApprovalMode: "write")); // as the app starts omp: an explicit mode
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        w.Show();
        vm.OnWindowOpened();
        try
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (vm.Phase is not (SessionPhase.Ready or SessionPhase.Faulted) && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
            vm.ShowPaneCommand.Execute(SidePane.Plan);
            var settle = DateTime.UtcNow.AddMilliseconds(1200);
            while (DateTime.UtcNow < settle) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(15); }
            using (var frame = w.CaptureRenderedFrame())
            {
                if (Out is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Out))!);
                    using var f = File.Create(Out);
                    frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
            }
            var found = LayoutAudit.Run(w);
            Assert.True(found.Count == 0, LayoutAudit.Report("readme", found));
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }
}

internal static class QaVisualExtensions
{
    public static ListBox? GetVisualDescendantsOf<T>(this Window w, string name) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<ListBox>().FirstOrDefault(l => l.Name == name);
}
