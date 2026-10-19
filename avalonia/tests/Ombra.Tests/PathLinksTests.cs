using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Xunit;
using Ombra.App.Services;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>Paths in the agent's text: which text is looked up, what it resolves to, the hover preview and the click.</summary>
public sealed class PathLinksTests
{
    [Theory]
    [InlineData("src/app.ts", true)]
    [InlineData("src/app.ts:42", true)]
    [InlineData("README.md", true)]
    [InlineData("~/notes", true)]
    [InlineData("assets/", true)]
    [InlineData("C:\\work\\a.cs", true)]
    [InlineData("file:///tmp/a.png", true)]
    [InlineData("npm run build", false)] // a command, not a path
    [InlineData("https://example.com/a.js", false)]
    [InlineData("useState", false)]
    [InlineData("v1.2", false)] // a version, not a file name
    [InlineData("x", false)]
    public void Only_text_shaped_like_a_path_is_looked_up(string text, bool expected) =>
        Assert.Equal(expected, PathLinks.LooksLikePath(text));

    [Theory]
    [InlineData("src/a.ts:42", "src/a.ts", 42)]
    [InlineData("src/a.ts:42:7", "src/a.ts", 42)]
    [InlineData("src/a.ts#L12", "src/a.ts", 12)]
    [InlineData("src/a.ts#L12-L20", "src/a.ts", 12)]
    [InlineData("src/a.ts", "src/a.ts", null)]
    [InlineData("src/a.ts:0", "src/a.ts:0", null)]
    public void A_line_after_the_path_is_kept(string text, string path, int? line) =>
        Assert.Equal((path, line), PathLinks.SplitLine(text));

    [Fact]
    public void Only_what_exists_resolves_and_its_kind_comes_from_the_disk()
    {
        var root = TestProcesses.TempDir("pathlinks");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "app.ts"), "a\nb\n");
        File.WriteAllBytes(Path.Combine(root, "logo.png"), [0x89, 0x50, 0x4E, 0x47]);
        Assert.Equal(new PathTarget(Path.Combine(root, "src", "app.ts"), 2, PathKind.File), PathLinks.Resolve(root, "src/app.ts:2"));
        Assert.Equal(PathKind.Folder, PathLinks.Resolve(root, "src/")!.Kind);
        Assert.Null(PathLinks.Resolve(root, "src/")!.Line);
        Assert.Equal(PathKind.Image, PathLinks.Resolve(root, "logo.png")!.Kind);
        Assert.Null(PathLinks.Resolve(root, "src/missing.ts"));
        Assert.Null(PathLinks.Resolve(null, "src/app.ts")); // relative without a project: nothing
    }

    [Theory]
    [InlineData(@"\\server\share\missing.txt")]
    [InlineData("//server/share/missing.txt")]
    [InlineData(@"/\server/share/missing.txt")]
    [InlineData(@"\/server/share/missing.txt")]
    [InlineData(@"\\?\UNC\server\share\missing.txt")]
    [InlineData(@"\\?\C:\work\missing.txt")]
    [InlineData(@"\\.\C:\work\missing.txt")]
    [InlineData(@"\??\UNC\server\share\missing.txt")]
    [InlineData(@"\Device\Mup\server\share\missing.txt")]
    [InlineData(@"\GLOBAL??\UNC\server\share\missing.txt")]
    [InlineData(@"\GLOBALROOT\Device\Mup\server\share\missing.txt")]
    [InlineData("file://server/share/missing.txt")]
    [InlineData("FILE://server/share/missing.txt")]
    [InlineData("file:////server/share/missing.txt")]
    [InlineData("file:///\\\\server\\share\\missing.txt")]
    [InlineData("file:///%5C%5Cserver/share/missing.txt")]
    [InlineData("file:///%2Fserver/share/missing.txt")]
    [InlineData("file:///safe/../%5C%5Cserver/share/missing.txt")]
    [InlineData("%5c%5cserver/share/missing.txt")]
    [InlineData("%2f%2fserver/share/missing.txt")]
    [InlineData("`//server/share/missing.txt`")]
    public void Network_and_device_references_are_rejected_before_any_file_probe(string path)
    {
        var root = TestProcesses.TempDir("pathlinks-network");
        // Resolve is entirely lexical: rejection does not depend on the network path existing or responding.
        Assert.Null(LocalFilePaths.Resolve(root, path));
        Assert.Null(ProjectFiles.Resolve(root, path));
        Assert.Null(PathLinks.Resolve(root, path));
        Assert.False(SessionController.IsNewFile(root, path));
        var error = Assert.Throws<IOException>(() => PathLinks.ReadHead(path, null));
        Assert.Equal("Only local file paths can be previewed.", error.Message);
    }

    [Fact]
    public void Local_absolute_relative_and_encoded_file_uris_still_resolve_but_network_roots_do_not()
    {
        var root = TestProcesses.TempDir("pathlinks-local");
        var local = Path.Combine(root, "with space.txt");
        File.WriteAllText(local, "local");
        Assert.Equal(local, ProjectFiles.Resolve(root, "./with space.txt"));
        Assert.Equal(local, ProjectFiles.Resolve(null, local));
        Assert.Equal(local, ProjectFiles.Resolve(null, new Uri(local).AbsoluteUri));
        Assert.True(LocalFilePaths.IsLocal(@"C:\work\app.cs"));
        Assert.True(LocalFilePaths.IsLocal("C:/work/app.cs"));
        Assert.Null(ProjectFiles.Resolve(@"\\server\share", "missing.txt"));
        Assert.Null(ProjectFiles.Resolve("//server/share", "missing.txt"));
        Assert.False(SessionController.IsNewFile(@"\\server\share", "missing.txt"));
    }

    [Fact]
    public void The_preview_reads_the_head_or_the_lines_around_the_named_one_and_spots_binaries()
    {
        var dir = TestProcesses.TempDir("pathlinks-head");
        var text = Path.Combine(dir, "a.txt");
        File.WriteAllText(text, string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line {i}")));
        var (_, head, binary) = PathLinks.ReadHead(text, null);
        Assert.False(binary);
        Assert.Equal(12, head.Split('\n').Length);
        Assert.StartsWith("line 1\n", head);
        var (_, around, _) = PathLinks.ReadHead(text, 20);
        Assert.StartsWith(" 16  line 16", around); // from a few lines before
        Assert.Contains("›20  line 20", around); // the named line marked
        var bin = Path.Combine(dir, "a.bin");
        File.WriteAllBytes(bin, [1, 0, 2]);
        Assert.True(PathLinks.ReadHead(bin, null).Binary);
    }

    [AvaloniaFact]
    public async Task The_hover_preview_says_whats_left_out_and_the_thumbnail_size_in_the_apps_language()
    {
        var dir = TestProcesses.TempDir("pathlinks-peek");
        var folder = Path.Combine(dir, "many");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 15; i++) File.WriteAllText(Path.Combine(folder, $"f{i:00}.txt"), "x");
        var png = Path.Combine(dir, "logo.png");
        using (var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(40, 30)))
        using (var f = File.Create(png)) bmp.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

        async Task<List<string>> Shown(PathTarget target, Func<List<string>, bool> done)
        {
            var peek = PathLinks.Preview(target, dir);
            var w = new Window { Content = peek, Width = 500, Height = 400 };
            w.Show();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            List<string> texts;
            while (!done(texts = [.. peek.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")]) && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(15);
            }
            w.Close();
            return texts;
        }

        var more = Loc.F("… {0} more", 3);
        var listing = await Shown(new PathTarget(folder, null, PathKind.Folder), t => t.Any(x => x.Contains('…')));
        Assert.Contains(listing, x => x.EndsWith("\n" + more, StringComparison.Ordinal));
        var size = " · " + Loc.F("{0}×{1} preview", 40, 30);
        var image = await Shown(new PathTarget(png, null, PathKind.Image), t => t.Any(x => x.Contains('×')));
        Assert.Contains(image, x => x.EndsWith(size, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task An_image_opens_in_the_viewer_and_a_file_in_the_files_pane()
    {
        var dir = TestProcesses.TempDir("pathlinks-open");
        var png = Path.Combine(dir, "logo.png");
        using (var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(4, 3)))
        using (var f = File.Create(png)) bmp.Save(f, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        var code = Path.Combine(dir, "a.ts");
        File.WriteAllText(code, "x\ny\n");
        var vm = new Ombra.App.ViewModels.MainViewModel(new Ombra.ClientCore.SessionController(TestProcesses.Fake("normal")), new Ombra.App.AppArgs());

        vm.OpenPath(new PathTarget(png, null, PathKind.Image));
        Assert.True(vm.IsImageViewerOpen);
        Assert.Equal("logo.png", vm.ViewerTitle);
        vm.CloseImageViewerCommand.Execute(null);

        vm.OpenPath(new PathTarget(code, 2, PathKind.File));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (vm.Files.Viewer?.FullPath != code && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(15); }
        Assert.True(vm.IsFilesPaneShown);
        Assert.Equal(code, vm.Files.Viewer?.FullPath);
        await vm.DisposeAsync();
    }

    private sealed class Handler(string root) : IPathLinkHandler
    {
        public string? ProjectRoot => root;
        public List<string> Looked { get; } = [];
        public List<PathTarget> Opened { get; } = [];
        public PathTarget? ResolvePath(string text)
        {
            Looked.Add(text);
            return PathLinks.Resolve(root, text);
        }
        public void OpenPath(PathTarget target) => Opened.Add(target);
        public string? EditorName => null;
        public void OpenInEditor(PathTarget target) { }
        public void AddToMessage(PathTarget target) { }
        public void Reveal(PathTarget target) { }
        public void OpenTerminal(string folder) { }
        public void Notify(string iconKey, string title, string message) { }
    }

    [AvaloniaFact]
    public void A_path_in_the_reply_previews_on_hover_and_opens_on_a_click_but_not_on_a_drag()
    {
        var root = TestProcesses.TempDir("pathlinks-ui");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "app.ts"), "export const a = 1;\n");
        var handler = new Handler(root);
        var view = new MarkdownView { Markdown = "Changed `src/app.ts:1` and `npm test`, see [the folder](src/)." };
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

        // Plain words and code that is no path are never looked up
        w.MouseMove(At("Changed"));
        w.MouseMove(At("npm test"));
        Assert.Empty(handler.Looked);
        Assert.Null(ToolTip.GetTip(text));

        // The path: looked up, its preview as the tooltip (name, first lines), a hand
        w.MouseMove(At("src/app.ts"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["src/app.ts:1"], handler.Looked);
        var tip = Assert.IsAssignableFrom<Control>(ToolTip.GetTip(text));
        Assert.Contains("src/app.ts:1", ((TextBlock)((StackPanel)((ToolTip)tip).Content!).Children[0]).Text);
        Assert.NotNull(text.Cursor);
        // A click opens it at its line; a drag over it selects instead of opening (a second press there would also be
        // a double click, which selects a word: the click comes first)
        w.MouseDown(At("src/app.ts"), MouseButton.Left);
        w.MouseUp(At("src/app.ts"), MouseButton.Left);
        Assert.Equal(new PathTarget(Path.Combine(root, "src", "app.ts"), 1, PathKind.File), Assert.Single(handler.Opened));
        w.MouseDown(At("src/app.ts"), MouseButton.Left);
        w.MouseMove(At("npm test"));
        w.MouseUp(At("npm test"), MouseButton.Left);
        Assert.Single(handler.Opened);
        text.ClearSelection();

        // A local link opens its folder; leaving the text drops the preview
        w.MouseMove(At("the folder"));
        w.MouseDown(At("the folder"), MouseButton.Left);
        w.MouseUp(At("the folder"), MouseButton.Left);
        Assert.Equal(PathKind.Folder, handler.Opened[^1].Kind);
        w.MouseMove(At("Changed"));
        Assert.Null(ToolTip.GetTip(text));
        w.Close();
    }
}
