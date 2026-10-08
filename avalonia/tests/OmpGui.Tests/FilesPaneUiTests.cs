using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OmpGui.App;
using OmpGui.App.Controls;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.App.Views.Panes;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// The Files pane and the project menu on screen: every state of the pane (tree, lists, viewer, empty) and the header's
/// menu, light and dark, wide and narrow, measured by <see cref="LayoutAudit"/> (zero findings); the paths in the
/// conversation open the file. With OMPGUI_REVIEW_DIR set, screenshots go to its "files" folder.
/// </summary>
public sealed class FilesPaneUiTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("OMPGUI_REVIEW_DIR");

    private static async Task Settle(int ms = 250)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(15);
        }
    }

    private sealed class Audit
    {
        public readonly StringBuilder Report = new();
        public int Count;

        public void Take(Window w, string screen)
        {
            using (var frame = w.CaptureRenderedFrame())
            {
                if (Dir is not null)
                {
                    Directory.CreateDirectory(Path.Combine(Dir, "files"));
                    using var f = File.Create(Path.Combine(Dir, "files", screen + ".png"));
                    frame?.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
            }
            var found = LayoutAudit.Run(w);
            Count += found.Count;
            Report.Append(LayoutAudit.Report(screen, found));
        }
    }

    /// <summary>The pane beside a stand-in for the conversation, as the side-pane host shows it.</summary>
    private static (Window w, FilesPane pane) Host(MainViewModel vm, double width, double height, double paneWidth)
    {
        var pane = new FilesPane { DataContext = vm.Files };
        var line = new Border();
        line.Bind(Border.BackgroundProperty, line.GetResourceObservable("GuiBorderSoft"));
        Grid.SetColumn(line, 1);
        Grid.SetColumn(pane, 2);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"*,1,{paneWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}"), Children = { line, pane } };
        var w = new Window { Width = width, Height = height, Content = grid };
        w.Bind(Window.BackgroundProperty, w.GetResourceObservable("GuiWindow"));
        w.Show();
        return (w, pane);
    }

    [AvaloniaTheory]
    [MemberData(nameof(DesignMatrix.FilesPane), MemberType = typeof(DesignMatrix))]
    public async Task The_files_pane_is_aligned_in_every_state(string theme, double width, double height, double paneWidth)
    {
        MainViewModel.ApplyTheme(theme);
        var a = new Audit();
        var tag = $"{theme}-{width}";
        var git = FileTestRepo.HasGit();
        try
        {
            var root = FileTestRepo.Create(git);
            var vm = FileTestRepo.Vm(root);
            var (w, pane) = Host(vm, width, height, paneWidth);
            var f = await FileTestRepo.Shown(vm, git);
            vm.Rows.Add(RowViewModel.Create(new ToolItem(1, "t1", "edit", "{}", ToolStatus.Succeeded, "ok", "src/app.py", "+  41|print('changed')\n")));
            vm.Rows.Add(RowViewModel.Create(new ToolItem(2, "t2", "write", "{}", ToolStatus.Succeeded, "ok", "src/new_file.py", "+   1|x = 1\n")));
            f.OnTranscriptChanged();
            await f.ToggleAsync(FileTestRepo.Node(f, "src"));
            await f.ToggleAsync(FileTestRepo.Node(f, "docs"));
            await Settle();
            a.Take(w, $"{tag}-tree");

            var tree = pane.FindControl<ListBox>("Tree")!;
            f.SelectedNode = FileTestRepo.Node(f, "src/app.py");
            await Settle(100);
            var appRow = (Control)tree.ContainerFromItem(f.SelectedNode)!;
            appRow.ContextFlyout!.ShowAt(appRow);
            await Settle();
            a.Take(w, $"{tag}-tree-menu");
            appRow.ContextFlyout.Hide();

            await f.OpenFileAsync(Path.Combine(root, "src", "app.py"), line: 12);
            await Settle(400);
            a.Take(w, $"{tag}-viewer-code");
            var code = pane.GetVisualDescendants().OfType<CodeView>().Single();
            Assert.True(code.Scroller.Offset.Y > 0, "the viewer scrolled to line 12");

            var more = pane.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ViewerMoreButton");
            more.Flyout!.ShowAt(more);
            await Settle();
            a.Take(w, $"{tag}-viewer-menu");
            more.Flyout.Hide();

            f.ToggleViewerExpandedCommand.Execute(null);
            await Settle();
            a.Take(w, $"{tag}-viewer-expanded");
            f.ToggleViewerExpandedCommand.Execute(null);

            await f.OpenFileAsync(Path.Combine(root, "README.md"));
            await Settle(400);
            a.Take(w, $"{tag}-viewer-markdown");
            await f.OpenFileAsync(Path.Combine(root, "images", "pixel.png"));
            await Settle();
            a.Take(w, $"{tag}-viewer-image");
            await f.OpenFileAsync(Path.Combine(root, "big.txt"));
            await Settle();
            a.Take(w, $"{tag}-viewer-too-large");
            f.CloseViewerCommand.Execute(null);

            f.Filter = "py";
            await FileTestRepo.Until(() => f.Results.Count > 0 && !f.IsSearching, "results");
            await Settle();
            a.Take(w, $"{tag}-search");
            f.Filter = "";
            if (git)
            {
                f.Mode = FilesMode.Changed;
                await FileTestRepo.Until(() => f.Results.Count > 0, "changed");
                await Settle();
                a.Take(w, $"{tag}-changed");
            }
            f.Mode = FilesMode.Session;
            await FileTestRepo.Until(() => f.Results.Count == 2, "this session");
            await Settle();
            a.Take(w, $"{tag}-session");
            f.Mode = FilesMode.All;
            w.Close();

            // No project: say so, offer a folder
            var none = new MainViewModel(new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("files-sessions")), new LaunchRequest()), new AppArgs());
            (w, _) = Host(none, width, height, paneWidth);
            none.ActivePane = SidePane.Files;
            await Settle();
            Assert.True(none.Files.ShowNoProject);
            a.Take(w, $"{tag}-no-project");
            w.Close();
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
        if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "files-audit.tsv"), a.Report.ToString());
        Assert.True(a.Count == 0, $"{a.Count} layout findings:\n{a.Report}");
    }

    /// <summary>A real click (pointer down and up over its middle), as the user makes it.</summary>
    private static void ClickAt(Window w, Control c)
    {
        var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w)!.Value;
        w.MouseMove(p);
        w.MouseDown(p, Avalonia.Input.MouseButton.Left);
        w.MouseUp(p, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Presses a button the way assistive technology does (runs its command and raises Click).</summary>
    private static void Invoke(Button b)
    {
        var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(b);
        ((Avalonia.Automation.Provider.IInvokeProvider)peer).Invoke();
        Dispatcher.UIThread.RunJobs();
    }

    private static Func<LaunchRequest, OmpLaunchSpec> PacedFake(string scenario, string sessions) => req =>
    {
        var spec = TestProcesses.FakeFactory(scenario, sessions)(req);
        return spec with { Environment = new Dictionary<string, string?>(spec.Environment!) { ["FAKE_OMP_PACE_MS"] = "1" } };
    };

    private static async Task<(MainWindow w, MainViewModel vm)> OpenWindow(string scenario, string root, double width, double height)
    {
        var s = new SessionController(PacedFake(scenario, TestProcesses.TempDir("files-ui-sessions")), new LaunchRequest(root));
        var vm = new MainViewModel(s, new AppArgs());
        var w = new MainWindow { DataContext = vm, Width = width, Height = height };
        w.Show();
        vm.OnWindowOpened();
        await FileTestRepo.Until(() => vm.Phase is SessionPhase.Ready && vm.ProjectName.Length > 0, "ready");
        await Settle();
        return (w, vm);
    }

    [AvaloniaTheory]
    [MemberData(nameof(DesignMatrix.Standard), MemberType = typeof(DesignMatrix))]
    public async Task The_project_menu_is_aligned_and_offers_the_project(string theme, double width, double height)
    {
        MainViewModel.ApplyTheme(theme);
        var a = new Audit();
        var tag = $"{theme}-{width}";
        try
        {
            var root = FileTestRepo.Create(git: false);
            var (w, vm) = await OpenWindow("normal", root, width, height);
            // An editor on PATH is offered; none, nothing
            var bin = TestProcesses.TempDir("files-editors");
            var code = Path.Combine(bin, OperatingSystem.IsWindows() ? "code.cmd" : "code");
            File.WriteAllText(code, "#!/bin/sh\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(code, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await vm.Files.DetectEditorsAsync(bin);
            var menu = w.FindControl<ProjectMenuButton>("ProjectMenu")!;
            Assert.True(menu.IsEffectivelyVisible);
            a.Take(w, $"{tag}-header");
            var flyout = (Flyout)menu.Button.Flyout!;
            flyout.ShowAt(menu.Button);
            await Settle(400);
            a.Take(w, $"{tag}-project-menu");
            foreach (var item in new[] { "BrowseFilesItem", "FileManagerItem", "TerminalItem", "CopyPathItem", "OpenFolderItem" })
                Assert.True(menu.FindControl<Button>(item)!.IsEffectivelyVisible, item);
            var editors = menu.FindControl<ItemsControl>("EditorItems")!;
            Assert.Equal(["Open in VS Code"], editors.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            // A choice closes the menu and does what it says
            Invoke(menu.FindControl<Button>("BrowseFilesItem")!);
            await Settle(200);
            Assert.False(flyout.IsOpen);
            Assert.Equal(SidePane.Files, vm.ActivePane);
            w.Close();
            await vm.DisposeAsync();
        }
        finally
        {
            MainViewModel.ApplyTheme("light");
        }
        if (Dir is not null) File.AppendAllText(Path.Combine(Dir, "files-audit.tsv"), a.Report.ToString());
        Assert.True(a.Count == 0, $"{a.Count} layout findings:\n{a.Report}");
    }

    [AvaloniaFact]
    public async Task The_keyboard_walks_the_tree_and_menus_close_once_a_choice_is_made()
    {
        var root = FileTestRepo.Create(git: false);
        var vm = FileTestRepo.Vm(root);
        var (w, pane) = Host(vm, 900, 700, 400);
        var f = await FileTestRepo.Shown(vm, git: false);
        var copied = new List<string>();
        vm.CopyTextRequested += copied.Add;
        void Key(Avalonia.Input.Key k, Avalonia.Input.PhysicalKey p)
        {
            w.KeyPress(k, Avalonia.Input.RawInputModifiers.None, p, null);
            Dispatcher.UIThread.RunJobs();
        }

        // → opens a folder, ← closes it, Enter opens a file
        var tree = pane.FindControl<ListBox>("Tree")!;
        f.SelectedNode = FileTestRepo.Node(f, "docs");
        tree.ContainerFromItem(f.SelectedNode)!.Focus();
        Key(Avalonia.Input.Key.Right, Avalonia.Input.PhysicalKey.ArrowRight);
        await FileTestRepo.Until(() => f.Nodes.Any(n => n.RelativePath == "docs/guide.md"), "docs open");
        Key(Avalonia.Input.Key.Left, Avalonia.Input.PhysicalKey.ArrowLeft);
        Assert.False(FileTestRepo.Node(f, "docs").IsExpanded);
        f.SelectedNode = FileTestRepo.Node(f, "README.md");
        Key(Avalonia.Input.Key.Enter, Avalonia.Input.PhysicalKey.Enter);
        await FileTestRepo.Until(() => f.Viewer is { State: ViewerState.Text }, "README open");
        Assert.Equal("README.md", f.Viewer!.RelativePath);

        // "Go to file": Enter opens the first match, Esc clears
        var filter = pane.FindControl<TextBox>("FilterBox")!;
        filter.Focus();
        f.Filter = "guide";
        await FileTestRepo.Until(() => f.Results.Count > 0 && !f.IsSearching, "a match");
        Key(Avalonia.Input.Key.Enter, Avalonia.Input.PhysicalKey.Enter);
        await FileTestRepo.Until(() => f.Viewer?.RelativePath == "docs/guide.md" && f.Viewer.State == ViewerState.Text, "the match open");
        Key(Avalonia.Input.Key.Escape, Avalonia.Input.PhysicalKey.Escape);
        Assert.Equal("", f.Filter);

        // The viewer's ⋯ menu and the tree's context menu close after a choice
        await Settle();
        var more = pane.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ViewerMoreButton");
        var moreMenu = (Flyout)more.Flyout!;
        moreMenu.ShowAt(more);
        await Settle();
        Invoke(((Control)moreMenu.Content!).GetLogicalDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Copy path"));
        await Settle(200);
        Assert.False(moreMenu.IsOpen);
        Assert.Equal(Path.Combine(root, "docs", "guide.md"), copied[^1]);
        var srcRow = (Control)tree.ContainerFromItem(FileTestRepo.Node(f, "src"))!;
        var treeMenu = (Flyout)srcRow.ContextFlyout!;
        treeMenu.ShowAt(srcRow);
        await Settle();
        Invoke(((Control)treeMenu.Content!).GetLogicalDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Copy relative path"));
        await Settle(200);
        Assert.False(treeMenu.IsOpen);
        Assert.Equal("src", copied[^1]);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_large_file_lays_out_only_the_lines_on_screen_and_selects_across_lines()
    {
        var root = TestProcesses.TempDir("files-large");
        File.WriteAllText(Path.Combine(root, "big.py"), FileTestRepo.Code(FileViewerViewModel.MaxLines));
        var vm = FileTestRepo.Vm(root);
        var (w, pane) = Host(vm, 900, 700, 420);
        var f = await FileTestRepo.Shown(vm, git: false);
        await f.OpenFileAsync(Path.Combine(root, "big.py"), line: 9990);
        await Settle(300);
        Assert.Null(f.Viewer!.MarkdownText); // a Markdown view never renders code
        var code = pane.GetVisualDescendants().OfType<CodeView>().Single();
        var surface = code.Surface;
        Assert.Equal(FileViewerViewModel.MaxLines, surface.LineCount);
        Assert.InRange(surface.LaidOutLines, 1, 60); // the lines on screen, not ten thousand
        Assert.True(code.Scroller.Offset.Y > 9900 * CodeSurface.LineHeight, "scrolled to line 9990");
        surface.Select(0, 4, 1, 3);
        Assert.Equal("step_1(x):  return x + 1\ndef", surface.SelectedText);
        surface.SelectAll();
        Assert.StartsWith("def step_1", surface.SelectedText);
        Assert.EndsWith("return x + 9999\n    # step 10000", surface.SelectedText);
        w.Close();
    }

    [AvaloniaFact]
    public async Task A_narrow_header_trims_the_project_name_and_the_narrowest_keeps_its_folder_icon()
    {
        var root = FileTestRepo.Create(git: false);
        var (w, vm) = await OpenWindow("normal", root, 520, 600);
        var menu = w.FindControl<ProjectMenuButton>("ProjectMenu")!;
        var header = w.FindControl<Border>("HeaderBar")!;
        TextBlock Name() => menu.Button.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("project-name"));
        // Narrow (a side pane open, a small window): the name stays, trimmed — a bare icon beside free room looked broken
        Assert.Contains("tight", header.Classes);
        Assert.True(menu.Button.IsEffectivelyVisible);
        Assert.True(Name().IsEffectivelyVisible);
        Assert.True(menu.Button.Bounds.Width <= 170);
        Assert.Empty(LayoutAudit.Run(w, header));
        // The narrowest header: the folder icon alone, the menu one click away
        header.Classes.Set("narrow", true);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.True(menu.Button.IsEffectivelyVisible);
        Assert.False(Name().IsEffectivelyVisible);
        w.Close();
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Paths_in_the_conversation_open_the_file_in_the_files_pane()
    {
        var root = FileTestRepo.Create(git: false);
        FileTestRepo.Write(root, "src/hello.py", "def greet():\n    print(\"Hello\")  # new greeting\n\n\n" + FileTestRepo.Code(12));
        var (w, vm) = await OpenWindow("session", root, 1180, 1300);
        var a = new Audit();
        vm.ComposerText = "change the greeting";
        vm.SendCommand.Execute(null);
        await FileTestRepo.Until(() => vm.Phase == SessionPhase.Ready && vm.Rows.OfType<ToolRowViewModel>().Count(t => t.HasDiff) >= 2, "the run", 60);
        var tools = vm.Rows.OfType<ToolRowViewModel>().ToList();
        Assert.True(tools.First(t => t.Name == "read").HasFile);
        Assert.All(tools.Where(t => t.Name == "bash"), t => Assert.False(t.HasFile));
        var edit = tools.First(t => t.Name == "edit");
        // Every line on screen: the long written file folded
        foreach (var t in tools.Where(t => t != edit)) t.IsExpanded = false;
        await Settle(400);
        a.Take(w, "conversation-paths");
        // The edit's path in its tool line, clicked: the file at the change's first line; the line does not fold
        var open = edit.IsExpanded;
        ClickAt(w, w.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tool-path") && ReferenceEquals(b.DataContext, edit)));
        await FileTestRepo.Until(() => vm.Files.Viewer is { State: ViewerState.Text }, "the file open");
        Assert.Equal(SidePane.Files, vm.ActivePane);
        Assert.Equal("src/hello.py", vm.Files.Viewer!.RelativePath);
        Assert.Equal(2, vm.Files.Viewer.TargetLine);
        Assert.Equal(open, edit.IsExpanded);
        // The change card's path does the same
        vm.Files.CloseViewerCommand.Execute(null);
        ClickAt(w, w.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("diff-path-link") && ReferenceEquals(b.DataContext, edit)));
        await FileTestRepo.Until(() => vm.Files.Viewer is { State: ViewerState.Text }, "the file open again");
        Assert.Equal("src/hello.py", vm.Files.Viewer!.RelativePath);
        Assert.Equal(2, vm.Files.Viewer.TargetLine);
        Assert.Equal(open, edit.IsExpanded);
        Assert.True(a.Count == 0, $"{a.Count} layout findings:\n{a.Report}");
        // omp changed these files in this session (named from omp's working directory: links resolved, /private/var on macOS)
        Assert.Contains(vm.Files.SessionChangedFiles, f => SessionCatalog.SamePath(f, Path.Combine(root, "src", "hello.py")));
        Assert.Contains(vm.Files.SessionChangedFiles, f => SessionCatalog.SamePath(f, Path.Combine(root, "tests", "test_greet.py")));
        w.Close();
        await vm.DisposeAsync();
    }
}
