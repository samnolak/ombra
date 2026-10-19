using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using Ombra.ClientCore;
using Xunit;

namespace Ombra.Tests;

/// <summary>
/// One menu for every path in the conversation (the reply's text, a tool's file, a change card's header, a turn's
/// changed file) and one copy: the text really on the clipboard, a check on a copy button or a brief elsewhere once it
/// is there, and an error brief (no check, no "copied") when the clipboard fails.
/// </summary>
public sealed class PathContextMenuTests
{
    private static async Task Until(Func<bool> condition, string what, int seconds = 30)
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

    private static async Task Settle(int ms = 150)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private static async Task RightClickAt(Window w, Point p)
    {
        w.MouseMove(p);
        w.MouseDown(p, MouseButton.Right);
        w.MouseUp(p, MouseButton.Right);
        await Settle();
    }

    private static Task RightClick(Window w, Control c) =>
        RightClickAt(w, c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)!.Value);

    private static FlyoutBase? OpenMenu(Control c) =>
        c.GetSelfAndVisualAncestors().OfType<Control>().Select(x => x.ContextFlyout).FirstOrDefault(f => f is { IsOpen: true });

    /// <summary>The row named by its English key (or an already shown label), as the menu shows it in the current language.</summary>
    private static Button? Item(FlyoutBase menu, string name) =>
        ((Control)((Flyout)menu).Content!).GetLogicalDescendants().OfType<Button>().SingleOrDefault(b => AutomationProperties.GetName(b) == Loc.T(name));

    /// <summary>Presses a button the way assistive technology does (raises Click).</summary>
    private static async Task Invoke(Button b)
    {
        ((IInvokeProvider)ControlAutomationPeer.CreatePeerForElement(b)).Invoke();
        await Settle();
    }

    private static Geometry? Resource(string key) => Application.Current!.TryFindResource(key, out var g) ? g as Geometry : null;

    /// <summary>What the path menu did through the window, for the reply's text without a whole window.</summary>
    private sealed class Handler(string root) : IPathLinkHandler
    {
        public string? ProjectRoot => root;
        public List<PathTarget> Opened { get; } = [];
        public List<PathTarget> Added { get; } = [];
        public List<PathTarget> Revealed { get; } = [];
        public List<string> Terminals { get; } = [];
        public List<(string Icon, string Title, string Message)> Briefs { get; } = [];
        public PathTarget? ResolvePath(string text) => PathLinks.Resolve(root, text);
        public void OpenPath(PathTarget target) => Opened.Add(target);
        public string? EditorName => null;
        public void OpenInEditor(PathTarget target) => throw new InvalidOperationException("no editor is offered");
        public void AddToMessage(PathTarget target) => Added.Add(target);
        public void Reveal(PathTarget target) => Revealed.Add(target);
        public void OpenTerminal(string folder) => Terminals.Add(folder);
        public void Notify(string iconKey, string title, string message) => Briefs.Add((iconKey, title, message));
    }

    [AvaloniaFact]
    public async Task A_path_in_the_reply_opens_the_path_menu_and_copies_its_path_for_real()
    {
        var root = TestProcesses.TempDir("path-menu-reply");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "app.ts"), "export const a = 1;\n");
        var file = PathLinks.Resolve(root, "src/app.ts")!.FullPath; // as the menu sees it (the temp folder's links resolved)
        var handler = new Handler(root);
        var view = new MarkdownView { Markdown = "Changed `src/app.ts:1` and `npm test`." };
        var w = new Window { Content = view, Width = 700, Height = 300 };
        PathLinks.SetHandler(w, handler);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var text = view.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        Point At(string part)
        {
            var i = text.Inlines!.Text!.IndexOf(part, StringComparison.Ordinal);
            var r = text.TextLayout.HitTestTextPosition(i + 1);
            return text.TranslatePoint(new Point(r.X + 1, r.Y + r.Height / 2), w)!.Value;
        }

        await RightClickAt(w, At("src/app.ts"));
        var menu = OpenMenu(text);
        Assert.True(ContextMenus.IsPathMenu(menu));
        foreach (var name in new[] { "Open in the Files pane", "Add to message", FileOpeners.RevealLabel, "Open in terminal", "Copy path", "Copy relative path" })
            Assert.NotNull(Item(menu!, name));
        Assert.Null(Item(menu!, "Open in {0}")); // no editor found: no editor item

        await Invoke(Item(menu!, "Copy path")!);
        await Until(() => handler.Briefs.Count > 0, "the copy's brief");
        Assert.Equal(file, await w.Clipboard!.TryGetTextAsync());
        Assert.Equal(("IconCopy", Loc.T("Path copied"), ""), handler.Briefs[^1]);

        await RightClickAt(w, At("src/app.ts"));
        await Invoke(Item(OpenMenu(text)!, "Copy relative path")!);
        await Until(() => handler.Briefs.Count > 1, "the second brief");
        Assert.Equal(Path.Combine("src", "app.ts"), await w.Clipboard!.TryGetTextAsync());

        await RightClickAt(w, At("src/app.ts"));
        await Invoke(Item(OpenMenu(text)!, FileOpeners.RevealLabel)!);
        await RightClickAt(w, At("src/app.ts"));
        await Invoke(Item(OpenMenu(text)!, "Open in terminal")!);
        await RightClickAt(w, At("src/app.ts"));
        await Invoke(Item(OpenMenu(text)!, "Add to message")!);
        Assert.Equal(file, Assert.Single(handler.Revealed).FullPath);
        Assert.Equal(Path.GetDirectoryName(file), Assert.Single(handler.Terminals));
        Assert.Equal(file, Assert.Single(handler.Added).FullPath);

        // Off the path the text's words are not a path: no path menu there
        w.MouseMove(At("Changed"));
        await Settle();
        Assert.False(ContextMenus.IsPathMenu(text.ContextFlyout));
        w.Close();
    }

    private static async Task<(MainWindow w, MainViewModel vm, string root)> ShowcaseAsync()
    {
        var root = TestProcesses.TempDir("path-menu-project");
        foreach (var (rel, body) in new[] { ("src/hello.py", "def greet():\n    print('Hi')\n"), ("src/cli.py", "import sys\n"), ("tests/test_greet.py", "import pytest\n") })
        {
            var full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
        }
        var s = new SessionController(TestProcesses.FakeFactory("showcase", TestProcesses.TempDir("path-menu-sessions")), new LaunchRequest(root));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = 1180, Height = 3000 };
        w.Show();
        vm.OnWindowOpened();
        await Until(() => vm.Phase == SessionPhase.Ready, "ready");
        vm.ComposerText = "make greet take a name";
        vm.SendCommand.Execute(null);
        await Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<TurnEndRowViewModel>().Any(e => e.HasFiles), "run end");
        foreach (var f in vm.Rows.OfType<TurnFoldRowViewModel>()) f.IsExpanded = true;
        foreach (var g in vm.Rows.OfType<StepGroupRowViewModel>()) g.IsExpanded = true;
        await Settle(400);
        return (w, vm, root);
    }

    private static T InRow<T>(Window w, object row, Func<T, bool> match) where T : Control =>
        w.GetVisualDescendants().OfType<T>().First(c => ReferenceEquals(c.DataContext, row) && match(c));

    [AvaloniaFact]
    public async Task A_tools_file_its_change_card_and_the_turns_files_open_the_same_path_menu()
    {
        var (w, vm, root) = await ShowcaseAsync();
        try
        {
            var transcript = w.FindControl<ListBox>("Transcript")!;
            var edit = vm.Rows.OfType<ToolRowViewModel>().First(t => t.Name == "edit" && t.FilePath == "src/hello.py");
            edit.IsExpanded = true;
            transcript.ScrollIntoView(edit);
            await Settle(300);
            var hello = PathLinks.Resolve(root, "src/hello.py")!.FullPath;

            // The tool's path: Copy path puts the absolute path on the clipboard and says so
            var toolPath = InRow<Button>(w, edit, b => b.Classes.Contains("tool-path"));
            await RightClick(w, toolPath);
            var menu = OpenMenu(toolPath);
            Assert.True(ContextMenus.IsPathMenu(menu));
            await Invoke(Item(menu!, "Copy path")!);
            await Until(() => vm.SessionCard?.Title == Loc.T("Path copied"), "the copy's brief");
            Assert.Equal(hello, await w.Clipboard!.TryGetTextAsync());

            // The change card's header: the same menu; Reveal that fails says so (the launcher is replaced)
            vm.Files.Launch = (_, _) => Task.FromResult(false);
            var diffLink = InRow<Button>(w, edit, b => b.Classes.Contains("diff-path-link"));
            await RightClick(w, diffLink);
            menu = OpenMenu(diffLink);
            Assert.True(ContextMenus.IsPathMenu(menu));
            await Invoke(Item(menu!, FileOpeners.RevealLabel)!);
            var couldNot = Loc.F("Could not {0}: {1} did not start.", "\0", "\0").Split('\0')[0];
            await Until(() => (vm.ComposerMessage + vm.Files.Notice).Contains(couldNot, StringComparison.Ordinal), "the reveal failure shown");

            // The turn's changed file
            var end = vm.Rows.OfType<TurnEndRowViewModel>().Single();
            transcript.ScrollIntoView(end);
            await Settle(300);
            var turnFile = w.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("turn-file") && b.DataContext is TurnFileViewModel { Path: "src/hello.py" });
            await RightClick(w, turnFile);
            menu = OpenMenu(turnFile);
            Assert.True(ContextMenus.IsPathMenu(menu));
            Assert.NotNull(Item(menu!, "Copy relative path"));
            menu!.Hide();
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    private static async Task<(MainWindow w, MainViewModel vm, ToolRowViewModel read, Button copy)> OutputCardAsync()
    {
        var (w, vm, _) = await ShowcaseAsync();
        // The second read gives six lines: its line shows the first few, its card all of them
        var read = vm.Rows.OfType<ToolRowViewModel>().First(t => t.Name == "read" && t.FilePath == "src/cli.py");
        read.IsExpanded = true;
        w.FindControl<ListBox>("Transcript")!.ScrollIntoView(read);
        await Settle(300);
        var copy = InRow<Button>(w, read, b => AutomationProperties.GetName(b) == Loc.T("Copy output") && b.IsEffectivelyVisible);
        return (w, vm, read, copy);
    }

    [AvaloniaFact]
    public async Task A_tool_cards_copy_output_puts_the_whole_output_on_the_clipboard_and_checks_the_button()
    {
        var (w, vm, read, copy) = await OutputCardAsync();
        try
        {
            Assert.Contains("def main():", read.Output); // past the line's preview
            await w.Clipboard!.SetTextAsync("before");
            await Invoke(copy);
            await Until(() => ((Icon)copy.Content!).Data == Resource("IconCheck"), "the check on the button");
            Assert.Equal(read.Output, await w.Clipboard!.TryGetTextAsync());
            Assert.NotEqual(Loc.T("Couldn't copy"), vm.SessionCard?.Title);
            await Settle(1700);
            Assert.Same(Resource("IconCopy"), ((Icon)copy.Content!).Data); // the copy icon again after 1.5 s
        }
        finally
        {
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_failing_clipboard_says_so_and_never_confirms_a_copy()
    {
        var (w, vm, _, copy) = await OutputCardAsync();
        var write = ContextMenus.WriteClipboard;
        try
        {
            await w.Clipboard!.SetTextAsync("before");
            ContextMenus.WriteClipboard = (_, _) => Task.FromException(new InvalidOperationException("The clipboard is locked by another app."));
            await Invoke(copy);
            await Until(() => vm.SessionCard?.Title == Loc.T("Couldn't copy"), "the error brief");
            Assert.Equal("The clipboard is locked by another app.", vm.SessionCard!.Message);
            Assert.Same(Resource("IconCopy"), ((Icon)copy.Content!).Data); // no check
            Assert.Equal("before", await w.Clipboard!.TryGetTextAsync());
        }
        finally
        {
            ContextMenus.WriteClipboard = write;
            await vm.DisposeAsync();
            w.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_folder_or_editor_that_does_not_start_says_so_whole_in_the_ui_language()
    {
        // Regression (ru): "Не удалось open the folder: open не запустился." — the action was left in English.
        var root = FileTestRepo.Create(git: false);
        var vm = FileTestRepo.Vm(root);
        var f = vm.Files;
        var started = new List<System.Diagnostics.ProcessStartInfo>();
        f.Launch = (psi, _) => { started.Add(psi); return Task.FromResult(false); };
        string Failed(string what) => Loc.F("Could not {0}: {1} did not start.", what, Path.GetFileName(started[^1].FileName));

        await f.OpenProjectInFileManagerCommand.ExecuteAsync(null);
        Assert.Equal(Failed(Loc.T("open the folder")), vm.ComposerMessage + f.Notice);
        vm.ComposerMessage = "";
        f.Notice = "";
        await f.OpenFolderInFileManagerCommand.ExecuteAsync(root);
        Assert.Equal(Failed(Loc.T("open the folder")), vm.ComposerMessage + f.Notice);
        vm.ComposerMessage = "";
        f.Notice = "";
        await f.OpenProjectInEditorCommand.ExecuteAsync(new EditorApp("Cursor", "/opt/cursor", EditorApp.Style.Goto));
        Assert.Equal(Failed(Loc.F("open {0}", "Cursor")), vm.ComposerMessage + f.Notice);
    }
}
