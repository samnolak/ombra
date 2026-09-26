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
        Assert.Contains(texts, t => t.FontSize == 20 && t.Inlines!.OfType<Run>().Any(r => r.Text == "Title"));
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
