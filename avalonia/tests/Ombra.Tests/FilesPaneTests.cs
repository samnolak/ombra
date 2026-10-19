using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.Controls;
using Ombra.App.Services;
using Ombra.App.ViewModels;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// A small project for the Files pane: a git repository (when git is installed) with a committed, a modified, an
/// untracked and a deleted file, ignored and heavy folders, Markdown, an image, a binary and a large file.
/// </summary>
internal static class FileTestRepo
{
    public static bool HasGit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p!.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    public static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in (string[])["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main", .. args])
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {err.Result}");
    }

    public static void Write(string root, string rel, string text)
    {
        var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static string Code(int lines) =>
        string.Join("\n", Enumerable.Range(1, lines).Select(i => i % 5 == 0 ? $"    # step {i}" : $"def step_{i}(x):  return x + {i}")) + "\n";

    /// <summary>The project; with <paramref name="git"/> a repository with one commit and changes after it.</summary>
    public static string Create(bool git = true)
    {
        var root = TestProcesses.TempDir("files-repo");
        Write(root, ".gitignore", "*.log\nnode_modules/\n");
        Write(root, "README.md", "# Demo\n\nA **small** project for the Files pane.\n\n- one\n- two\n");
        Write(root, "src/app.py", Code(40));
        Write(root, "src/util.py", "def util():\n    return 1\n");
        Write(root, "src/gone.py", "print('bye')\n");
        Write(root, "src/my notes.txt", "notes\n");
        Write(root, "docs/guide.md", "# Guide\n\nRead me.\n");
        Write(root, "node_modules/pkg/index.js", "module.exports = 1;\n");
        Write(root, "bin/tool.txt", "built\n");
        Write(root, "file2.txt", "2\n");
        Write(root, "file10.txt", "10\n");
        File.WriteAllBytes(Path.Combine(root, "data.bin"), [0x00, 0x01, 0x02, 0xFF, 0x00, 0x10]);
        File.WriteAllText(Path.Combine(root, "big.txt"), string.Concat(Enumerable.Repeat(new string('x', 99) + "\n", 12_000)));
        Directory.CreateDirectory(Path.Combine(root, "images"));
        Png(Path.Combine(root, "images", "pixel.png"), 24, 16);
        if (git && HasGit())
        {
            Git(root, "init", "-q");
            Git(root, "add", "-A");
            Git(root, "commit", "-q", "-m", "init");
            File.AppendAllText(Path.Combine(root, "src", "app.py"), "print('changed')\n");
            File.Delete(Path.Combine(root, "src", "gone.py"));
            Write(root, "src/new_file.py", "x = 1\n");
        }
        else Write(root, "src/new_file.py", "x = 1\n");
        Write(root, "debug.log", "log\n");
        return root;
    }

    public static void Png(string path, int w, int h)
    {
        using var bmp = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(w, h), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        bmp.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>A window-less MainViewModel whose project is <paramref name="root"/> (omp is never started).</summary>
    public static MainViewModel Vm(string root) =>
        new(new SessionController(TestProcesses.FakeFactory("normal", TestProcesses.TempDir("files-sessions")), new LaunchRequest(root)), new AppArgs());

    public static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The pane shown, its tree read and (in a repository) git's status in.</summary>
    public static async Task<FilesViewModel> Shown(MainViewModel vm, bool git)
    {
        vm.ActivePane = SidePane.Files;
        var f = vm.Files;
        await Until(() => f.Nodes.Count > 0 && (!git || f.IsGitRepo && f.ChangedCount > 0), "the tree");
        return f;
    }

    public static FileNodeViewModel Node(FilesViewModel f, string rel) => f.Nodes.Single(n => n.RelativePath == rel);
}

/// <summary>The Files pane's logic: the tree, git's marks, "Go to file", the viewer's states, the actions and the project menu.</summary>
public sealed class FilesPaneTests
{
    private static void NeedGit()
    {
        if (!FileTestRepo.HasGit()) Assert.Skip("git is not installed");
    }


    [Fact]
    public async Task Mac_search_index_does_not_descend_into_home_or_ancestor_roots()
    {
        var home = TestProcesses.TempDir("index-home");
        File.WriteAllText(Path.Combine(home, "top.txt"), "top");
        foreach (var name in new[] { "nested", "Library", "Containers", "Trips.photoslibrary" })
        {
            Directory.CreateDirectory(Path.Combine(home, name));
            File.WriteAllText(Path.Combine(home, name, "private.txt"), "private");
        }

        var ownHome = await ProjectFiles.IndexAsync(home, useGit: true, CancellationToken.None, home, macOS: true);
        Assert.True(ownHome.Truncated);

        var parent = TestProcesses.TempDir("index-home-parent");
        var nestedHome = Path.Combine(parent, "Users", "alice");
        Directory.CreateDirectory(nestedHome);
        File.WriteAllText(Path.Combine(parent, "top.txt"), "top");
        File.WriteAllText(Path.Combine(nestedHome, "private.txt"), "private");
        var ancestor = await ProjectFiles.IndexAsync(parent, useGit: true, CancellationToken.None, nestedHome, macOS: true);
        Assert.True(ancestor.Truncated);

        var otherRoot = await ProjectFiles.IndexAsync(parent, useGit: false, CancellationToken.None, nestedHome, macOS: false);
        Assert.Contains("Users/alice/private.txt", otherRoot.Paths);
    }

    [Fact]
    public async Task A_project_folder_its_enclosing_repository_ignores_is_walked()
    {
        NeedGit();
        var repo = TestProcesses.TempDir("index-ignored");
        FileTestRepo.Git(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "scratch/\n");
        var project = Path.Combine(repo, "scratch", "project");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "notes-3.md"), "n");
        File.WriteAllText(Path.Combine(project, "src", "app.ts"), "a");

        // git ls-files answers with nothing (exit 0) inside an ignored folder: the folder's own files are listed anyway
        var index = await ProjectFiles.IndexAsync(project, useGit: true, CancellationToken.None, "/nonexistent-home", macOS: false);
        Assert.Contains("notes-3.md", index.Paths);
        Assert.Contains("src/app.ts", index.Paths);
    }
    [AvaloniaFact]
    public async Task The_tree_lists_folders_first_in_natural_order_without_git_internals()
    {
        var root = FileTestRepo.Create(git: false);
        var f = await FileTestRepo.Shown(FileTestRepo.Vm(root), git: false);
        Assert.Equal(root, f.Root);
        Assert.Equal(["bin", "docs", "images", "node_modules", "src", ".gitignore", "big.txt", "data.bin", "debug.log", "file2.txt", "file10.txt", "README.md"],
            f.Nodes.Select(n => n.Name));
        Assert.All(f.Nodes, n => Assert.Equal(0, n.Depth));
        // Heavy folders are there, dimmed and closed; opening one reads it on demand
        var nm = FileTestRepo.Node(f, "node_modules");
        Assert.True(nm.IsHeavy && nm.IsDimmed && !nm.IsExpanded);
        Assert.True(FileTestRepo.Node(f, "bin").IsDimmed);
        Assert.False(FileTestRepo.Node(f, "src").IsDimmed);
        await f.ToggleAsync(nm);
        var pkg = FileTestRepo.Node(f, "node_modules/pkg");
        Assert.Equal(1, pkg.Depth);
        Assert.True(pkg.IsDimmed); // inside a dimmed folder
        await f.ToggleAsync(nm);
        Assert.DoesNotContain(f.Nodes, n => n.RelativePath.StartsWith("node_modules/", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Git_marks_changed_new_deleted_and_ignored_files()
    {
        NeedGit();
        var root = FileTestRepo.Create();
        Directory.CreateDirectory(Path.Combine(root, ".git", "x")); // never listed
        var f = await FileTestRepo.Shown(FileTestRepo.Vm(root), git: true);
        Assert.DoesNotContain(f.Nodes, n => n.Name == ".git");
        Assert.True(FileTestRepo.Node(f, "debug.log").IsIgnored);
        Assert.True(FileTestRepo.Node(f, "node_modules").IsIgnored);
        var src = FileTestRepo.Node(f, "src");
        Assert.True(src.HasChangesInside && src.ShowFolderDot);
        Assert.False(FileTestRepo.Node(f, "docs").HasChangesInside);
        await f.ToggleAsync(src);
        Assert.Equal(GitMark.Modified, FileTestRepo.Node(f, "src/app.py").Git);
        Assert.Equal("M", FileTestRepo.Node(f, "src/app.py").GitLetter);
        Assert.Equal(GitMark.Untracked, FileTestRepo.Node(f, "src/new_file.py").Git);
        var gone = FileTestRepo.Node(f, "src/gone.py");
        Assert.True(gone.IsDeleted);
        Assert.Equal(GitMark.Deleted, gone.Git);
        Assert.Equal(GitMark.None, FileTestRepo.Node(f, "src/util.py").Git);
        Assert.Equal(3, f.ChangedCount);
        // The Changed list: what git reports, relative to the project
        f.Mode = FilesMode.Changed;
        await FileTestRepo.Until(() => f.Results.Count == 3, "the changed list");
        Assert.Equal(["src/app.py", "src/gone.py", "src/new_file.py"], f.Results.Select(r => r.RelativePath));
        Assert.Equal(["M", "D", "U"], f.Results.Select(r => r.GitLetter));
        Assert.True(f.ShowResults);
    }

    [Fact]
    public void Git_status_is_read_from_porcelain_with_renames_and_folders()
    {
        var top = Path.Combine(Path.GetTempPath(), "repo");
        var s = GitSnapshot.Parse(top, " M a/b.txt\0R  new.txt\0old.txt\0?? fresh/\0!! build/\0!! x.log\0UU c.txt\0A  d.txt\0 D e/f.txt\0");
        string P(string rel) => Path.GetFullPath(Path.Combine(top, rel));
        Assert.Equal(GitMark.Modified, s.MarkOf(P("a/b.txt")));
        Assert.Equal(GitMark.Renamed, s.MarkOf(P("new.txt")));
        Assert.Equal(GitMark.None, s.MarkOf(P("old.txt")));
        Assert.Equal(GitMark.Untracked, s.MarkOf(P("fresh/deep/one.txt")));
        Assert.Equal(GitMark.Ignored, s.MarkOf(P("build/out.o")));
        Assert.True(s.IsIgnored(P("x.log")));
        Assert.Equal(GitMark.Conflicted, s.MarkOf(P("c.txt")));
        Assert.Equal(GitMark.Added, s.MarkOf(P("d.txt")));
        Assert.Equal([P("e/f.txt")], s.DeletedIn(P("e")));
        Assert.True(s.HasChangesInside(P("a")));
        Assert.True(s.HasChangesInside(top));
        Assert.False(s.HasChangesInside(P("build")));
        Assert.Contains(s.Changes, c => c.Path == P("fresh") + Path.DirectorySeparatorChar && c.Mark == GitMark.Untracked);
        Assert.DoesNotContain(s.Changes, c => c.Mark == GitMark.Ignored);
    }

    [Fact]
    public void Go_to_file_ranks_the_name_first_and_matches_letters_in_order()
    {
        var paths = new ProjectFiles.SearchIndex(["src/app.py", "src/application/main.py", "tests/test_app.py", "docs/apple.md", "lib/zap.py", "src/util/pa.py"]);
        var hits = ProjectFiles.Search(paths, "app.py", 10, CancellationToken.None).Select(h => h.RelativePath).ToList();
        Assert.Equal("src/app.py", hits[0]); // the name itself
        Assert.Equal("tests/test_app.py", hits[1]); // the name contains it
        Assert.DoesNotContain("docs/apple.md", hits);
        Assert.Equal(["src/application/main.py"], ProjectFiles.Search(paths, "application", 10, CancellationToken.None).Select(h => h.RelativePath));
        Assert.Equal(["src/application/main.py"], ProjectFiles.Search(paths, "mnpy", 10, CancellationToken.None).Select(h => h.RelativePath)); // m…n…p…y in order
        Assert.Equal(["tests/test_app.py"], ProjectFiles.Search(paths, "tests app", 10, CancellationToken.None).Select(h => h.RelativePath));
        Assert.Equal(["src/util/pa.py"], ProjectFiles.Search(paths, "util/pa", 10, CancellationToken.None).Select(h => h.RelativePath));
        Assert.Equal(2, ProjectFiles.Search(paths, "p", 2, CancellationToken.None).Count);
        Assert.Throws<OperationCanceledException>(() => ProjectFiles.Search(paths, "a", 10, new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData("caf\u00e9", "cafe\u0301")]
    [InlineData("cafe\u0301", "caf\u00e9")]
    public void Go_to_file_matches_canonical_equivalents_without_changing_the_path(string query, string spelling)
    {
        var original = $"docs/{spelling}/{spelling}.txt";
        var index = new ProjectFiles.SearchIndex([original]);
        foreach (var q in new[] { query + ".txt", query + "/" + query, query + " " + query, "cf\u00e9" })
        {
            var hit = Assert.Single(ProjectFiles.Search(index, q, 10, CancellationToken.None));
            Assert.Same(original, hit.RelativePath);
        }
        Assert.Equal(0, Assert.Single(ProjectFiles.Search(index, query + ".txt", 10, CancellationToken.None)).Score);
    }

    [Fact]
    public void Go_to_file_keeps_ascii_scores_and_tie_order()
    {
        var index = new ProjectFiles.SearchIndex(["z/app", "app/file", "x/myapp", "x/a_p_p", "b/app2", "a/app", "app"]);
        Assert.Equal(
            [new FileHit("app", 0), new FileHit("a/app", 0), new FileHit("z/app", 0),
                new FileHit("b/app2", 1), new FileHit("x/myapp", 2), new FileHit("app/file", 3), new FileHit("x/a_p_p", 4)],
            ProjectFiles.Search(index, "app", 10, CancellationToken.None));
    }

    [Fact]
    public void Go_to_file_keeps_malformed_utf16_searchable()
    {
        const string original = "src/bad\uD800.txt";
        var index = new ProjectFiles.SearchIndex([original]);
        Assert.Equal(original, Assert.Single(ProjectFiles.Search(index, "bad\uD800", 10, CancellationToken.None)).RelativePath);
        Assert.Equal(original, Assert.Single(ProjectFiles.Search(index, "bad", 10, CancellationToken.None)).RelativePath);
    }

    [AvaloniaFact]
    public async Task Go_to_file_searches_the_whole_project_but_not_what_git_ignores()
    {
        NeedGit();
        var root = FileTestRepo.Create();
        var f = await FileTestRepo.Shown(FileTestRepo.Vm(root), git: true);
        f.Filter = "util";
        await FileTestRepo.Until(() => f.Results.Count > 0 && !f.IsSearching, "results");
        Assert.Equal("src/util.py", f.Results[0].RelativePath);
        Assert.Equal("src", f.Results[0].Detail);
        Assert.False(f.ShowTree);
        f.Filter = "index";
        await FileTestRepo.Until(() => f.HasResultsMessage, "no match");
        Assert.Empty(f.Results); // node_modules is ignored
        Assert.Equal(Loc.F("No files match “{0}”.", "index"), f.ResultsMessage);
        f.Filter = "nfpy";
        await FileTestRepo.Until(() => f.Results.Any(r => r.Name == "new_file.py"), "letters in order");
        Assert.Equal(GitMark.Untracked, f.Results.Single(r => r.Name == "new_file.py").Git);
        f.Filter = "";
        Assert.True(f.ShowTree);
        Assert.Empty(f.Results);
    }

    [AvaloniaFact]
    public async Task Without_git_the_search_walks_the_folder_and_skips_heavy_ones()
    {
        var root = FileTestRepo.Create(git: false);
        var f = await FileTestRepo.Shown(FileTestRepo.Vm(root), git: false);
        Assert.False(f.IsGitRepo);
        f.Filter = "index";
        await FileTestRepo.Until(() => f.HasResultsMessage || f.Results.Count > 0, "search");
        Assert.Empty(f.Results);
        f.Filter = "guide";
        await FileTestRepo.Until(() => f.Results.Count > 0, "search");
        Assert.Equal("docs/guide.md", f.Results.Single().RelativePath);
    }

    [AvaloniaFact]
    public async Task The_tree_follows_the_disk_while_the_pane_is_shown()
    {
        var root = FileTestRepo.Create(git: false);
        var vm = FileTestRepo.Vm(root);
        var f = await FileTestRepo.Shown(vm, git: false);
        Assert.True(f.WatcherCount >= 1);
        await f.ToggleAsync(FileTestRepo.Node(f, "src"));
        Assert.Equal(2, f.WatcherCount);
        File.WriteAllText(Path.Combine(root, "src", "added.py"), "y = 2\n");
        File.Delete(Path.Combine(root, "file2.txt"));
        await FileTestRepo.Until(() => f.Nodes.Any(n => n.RelativePath == "src/added.py") && f.Nodes.All(n => n.Name != "file2.txt"), "the new and the deleted file");
        // Hidden: nothing is watched
        vm.ActivePane = SidePane.None;
        Assert.Equal(0, f.WatcherCount);
    }

    [AvaloniaFact]
    public async Task The_tree_keeps_up_while_files_keep_changing()
    {
        // A build or a long command writes into the project without a pause: the tree shows new files as they come,
        // not once the writing stops (a refresh put off again by every change never ran while omp worked)
        var root = FileTestRepo.Create(git: false);
        var f = await FileTestRepo.Shown(FileTestRepo.Vm(root), git: false);
        using var cts = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            for (var i = 0; !cts.IsCancellationRequested; i++)
            {
                File.WriteAllText(Path.Combine(root, "build.out"), i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await Task.Delay(50);
            }
        });
        try
        {
            // The pane's own first refresh is over before the file comes: only the disk events can show it
            var settled = DateTime.UtcNow.AddSeconds(1.5);
            while (DateTime.UtcNow < settled)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(15);
            }
            File.WriteAllText(Path.Combine(root, "first.txt"), "1\n");
            await FileTestRepo.Until(() => f.Nodes.Any(n => n.Name == "first.txt"), "the new file while the writes go on", seconds: 5);
        }
        finally
        {
            await cts.CancelAsync();
            await writer;
        }
    }

    [AvaloniaFact]
    public async Task Files_omp_changed_in_this_session_come_from_its_edit_and_write_rows()
    {
        var root = FileTestRepo.Create(git: false);
        var vm = FileTestRepo.Vm(root);
        var f = await FileTestRepo.Shown(vm, git: false);
        vm.Rows.Add(RowViewModel.Create(new ToolItem(1, "t1", "edit", "{}", ToolStatus.Succeeded, "ok", "src/app.py", "+  3|x\n")));
        vm.Rows.Add(RowViewModel.Create(new ToolItem(2, "t2", "write", "{}", ToolStatus.Succeeded, "ok", Path.Combine(root, "docs", "new.md"), "+  1|# New\n")));
        vm.Rows.Add(RowViewModel.Create(new ToolItem(3, "t3", "read", "{}", ToolStatus.Succeeded, "ok", "src/util.py")));
        vm.Rows.Add(RowViewModel.Create(new ToolItem(4, "t4", "edit", "{}", ToolStatus.Failed, "no", "README.md", "+  1|x\n")));
        vm.Rows.Add(RowViewModel.Create(new ToolItem(5, "t5", "bash", "{}", ToolStatus.Succeeded, "", "$ ls")));
        f.OnTranscriptChanged();
        Assert.Equal(2, f.SessionCount);
        Assert.Equal(Loc.F("This session {0}", 2), f.SessionLabel);
        Assert.Contains(Path.Combine(root, "src", "app.py"), f.SessionChangedFiles);
        f.Mode = FilesMode.Session;
        await FileTestRepo.Until(() => f.Results.Count == 2, "this session's list");
        Assert.Equal(["docs/new.md", "src/app.py"], f.Results.Select(r => r.RelativePath));
        Assert.All(f.Results, r => Assert.True(r.IsSessionChanged));
        Assert.True(f.Results[0].IsDeleted); // written by the agent, not on disk (any more)
        f.Mode = FilesMode.All;
        await f.ToggleAsync(FileTestRepo.Node(f, "src"));
        Assert.True(FileTestRepo.Node(f, "src/app.py").IsSessionChanged);
        Assert.False(FileTestRepo.Node(f, "src/util.py").IsSessionChanged);
    }

    [AvaloniaFact]
    public async Task Changed_and_session_filters_match_canonical_equivalents_and_preserve_paths()
    {
        NeedGit();
        var root = FileTestRepo.Create();
        FileTestRepo.Git(root, "config", "core.precomposeunicode", "false");
        const string decomposed = "cafe\u0301.txt";
        const string composed = "r\u00e9sum\u00e9.txt";
        File.WriteAllText(Path.Combine(root, decomposed), "one");
        File.WriteAllText(Path.Combine(root, composed), "two");
        var vm = FileTestRepo.Vm(root);
        var f = await FileTestRepo.Shown(vm, git: true);
        vm.Rows.Add(RowViewModel.Create(new ToolItem(1, "nfd", "write", "{}", ToolStatus.Succeeded, "ok", decomposed, "+  1|one\n")));
        vm.Rows.Add(RowViewModel.Create(new ToolItem(2, "nfc", "write", "{}", ToolStatus.Succeeded, "ok", composed, "+  1|two\n")));
        f.OnTranscriptChanged();
        foreach (var mode in new[] { FilesMode.Changed, FilesMode.Session })
        {
            f.Mode = mode;
            foreach (var (query, original) in new[] { ("caf\u00e9", decomposed), ("re\u0301sume\u0301", composed) })
            {
                f.Filter = query;
                await FileTestRepo.Until(() => !f.IsSearching && f.Results.Count == 1 && f.Results[0].RelativePath == original,
                    $"{mode} canonical filter {query}");
                Assert.Equal(original, f.Results[0].Name);
                Assert.Equal(Path.Combine(root, original), f.Results[0].FullPath);
            }
        }
    }

    [Fact]
    public void A_tool_row_names_its_file_when_it_reads_or_changes_one()
    {
        ToolRowViewModel Row(string name, string summary, string? diff = null) =>
            (ToolRowViewModel)RowViewModel.Create(new ToolItem(1, "t", name, "{}", ToolStatus.Succeeded, "", summary, diff));
        Assert.True(Row("read", "src/app.py").HasFile);
        Assert.True(Row("edit", "src/app.py", "   1|a\n+   2|b\n").HasFile);
        Assert.Equal(2, Row("edit", "src/app.py", "   1|a\n+   2|b\n").FirstChangedLine);
        // A patch over two files opens the first file it changed, never its summary
        var patch = (ToolRowViewModel)RowViewModel.Create(new ToolItem(1, "t", "edit", "{}", ToolStatus.Succeeded, "", "2 files", " 1|x\n+2|y\n",
            Changes: [new FileChange("src/cli.py", FileChangeKind.Modified, " 1|x\n+2|y\n"), new FileChange("tests/test_cli.py", FileChangeKind.Modified, "+3|z\n")]));
        Assert.Equal((true, "src/cli.py", 2, (FileChangeKind?)null), (patch.HasFile, patch.FilePath, patch.FirstChangedLine, patch.ChangeKind));
        Assert.False(Row("bash", "$ cat src/app.py").HasFile);
        Assert.False(Row("fetch", "https://example.com/a.js").HasFile);
        Assert.False(Row("todo", "init · 3 items").HasFile);
        Assert.Null(Row("read", "src/app.py").FirstChangedLine);
    }

    [AvaloniaFact]
    public async Task The_viewer_shows_text_markdown_images_and_says_why_it_cannot()
    {
        var root = FileTestRepo.Create(git: false);
        var f = await FileTestRepo.Shown(FileTestRepo.Vm(root), git: false);
        async Task<FileViewerViewModel> Open(string rel)
        {
            await f.OpenFileAsync(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            return f.Viewer!;
        }
        var code = await Open("src/app.py");
        Assert.Equal(ViewerState.Text, code.State);
        Assert.True(code.ShowCode);
        Assert.StartsWith("def step_1(x):", code.Text);
        Assert.Equal("src · " + Loc.F("{0:N0} lines", 40) + " · " + code.Document!.EncodingName, code.Subtitle);
        var md = await Open("README.md");
        Assert.True(md.ShowMarkdown && md.CanToggleSource && !md.ShowCode);
        md.ToggleSourceCommand.Execute(null);
        Assert.True(md.ShowCode);
        var img = await Open("images/pixel.png");
        Assert.Equal(ViewerState.Image, img.State);
        Assert.NotNull(img.Image);
        Assert.StartsWith("24 × 16 · ", img.Info);
        var bin = await Open("data.bin");
        Assert.Equal(ViewerState.Binary, bin.State);
        Assert.Equal(Loc.T("Binary file"), bin.StateTitle);
        var big = await Open("big.txt");
        Assert.Equal(ViewerState.TooLarge, big.State);
        Assert.True(big.CanShowHead);
        await big.ShowHeadCommand.ExecuteAsync(null);
        Assert.Equal(ViewerState.Text, big.State);
        Assert.StartsWith(Loc.F("Showing the first {0} of {1}", "256 KB", "1.1 MB"), big.Notice);
        var gone = await Open("src/nothing.py");
        Assert.Equal(ViewerState.Missing, gone.State);
        Assert.True(gone.ShowState);
        f.CloseViewerCommand.Execute(null);
        Assert.False(f.HasViewer);
    }

    [Fact]
    public void Text_is_capped_in_lines_and_line_length()
    {
        var many = Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(1, FileViewerViewModel.MaxLines + 5)) + "\n");
        var (text, lines, notes) = FileViewerViewModel.Decode(many);
        Assert.Equal(FileViewerViewModel.MaxLines, lines);
        Assert.EndsWith("\n10000", text);
        Assert.Contains(notes, n => n.StartsWith(Loc.F("Showing the first {0:N0} of {1:N0} lines", 10_000, 10_005), StringComparison.Ordinal));
        var (wide, _, wideNotes) = FileViewerViewModel.Decode(Encoding.UTF8.GetBytes(new string('a', 5000) + "\n\tb\r\n"));
        Assert.Equal(new string('a', FileViewerViewModel.MaxLineChars) + " …\n    b", wide);
        Assert.Contains(wideNotes, n => n.StartsWith(Loc.F("Lines longer than {0:N0} characters are cut", FileViewerViewModel.MaxLineChars), StringComparison.Ordinal));
        Assert.True(FileViewerViewModel.IsBinary([0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]));
        Assert.False(FileViewerViewModel.IsBinary(Encoding.UTF8.GetBytes("plain text, ünïcode\n")));
        Assert.False(FileViewerViewModel.IsBinary([0xFF, 0xFE, 0x41, 0x00])); // UTF-16 with its mark
        Assert.Equal("é", FileViewerViewModel.Decode([0xEF, 0xBB, 0xBF, 0xC3, 0xA9]).Text); // the UTF-8 mark goes
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("e\u0301")]
    [InlineData("👩‍💻")]
    public void File_preview_line_limit_does_not_split_graphemes(string glyph)
    {
        for (var cut = 1; cut < glyph.Length; cut++)
        {
            var head = new string('a', FileViewerViewModel.MaxLineChars - cut);
            var (text, _, notes) = FileViewerViewModel.Decode(Encoding.UTF8.GetBytes(head + glyph + " rest\nnext"));
            Assert.Equal(head + " …\nnext", text);
            Assert.NotEmpty(notes);
        }
    }

    [AvaloniaFact]
    public async Task A_path_from_the_conversation_opens_in_the_pane_at_its_line()
    {
        var root = FileTestRepo.Create(git: false);
        var vm = FileTestRepo.Vm(root);
        Assert.Equal(SidePane.None, vm.ActivePane);
        await vm.Files.OpenLinkCommand.ExecuteAsync(new FileLink("src/app.py", 12));
        var f = vm.Files;
        Assert.Equal(SidePane.Files, vm.ActivePane);
        Assert.Equal("src/app.py", f.Viewer!.RelativePath);
        Assert.Equal(12, f.Viewer.TargetLine);
        Assert.Equal(ViewerState.Text, f.Viewer.State);
        // Shown in the tree: its folder opened, the file selected
        Assert.True(FileTestRepo.Node(f, "src").IsExpanded);
        Assert.Equal("src/app.py", f.SelectedNode?.RelativePath);
        // A tool row's file: the change's first line
        var row = (ToolRowViewModel)RowViewModel.Create(new ToolItem(1, "t", "edit", "{}", ToolStatus.Succeeded, "", "src/util.py", "   1|def util():\n-   2|    return 1\n+   2|    return 2\n"));
        await f.OpenToolFileCommand.ExecuteAsync(row);
        Assert.Equal("src/util.py", f.Viewer!.RelativePath);
        Assert.Equal(2, f.Viewer.TargetLine);
        // A folder is shown open in the tree
        await f.OpenLinkCommand.ExecuteAsync(new FileLink("docs"));
        Assert.True(FileTestRepo.Node(f, "docs").IsExpanded);
        Assert.Equal("docs", f.SelectedNode?.RelativePath);
    }

    [AvaloniaFact]
    public async Task Add_to_message_puts_an_at_mention_omp_reads_at_the_caret()
    {
        var root = FileTestRepo.Create(git: false);
        var vm = FileTestRepo.Vm(root);
        var f = await FileTestRepo.Shown(vm, git: false);
        vm.ComposerText = "look at";
        vm.ComposerCaretIndex = vm.ComposerText.Length;
        await f.ToggleAsync(FileTestRepo.Node(f, "src"));
        f.AddToMessageCommand.Execute(FileTestRepo.Node(f, "src/app.py"));
        Assert.Equal("look at @src/app.py", vm.ComposerText);
        f.AddToMessageCommand.Execute(FileTestRepo.Node(f, "src/my notes.txt"));
        Assert.Equal("look at @src/app.py @\"src/my notes.txt\"", vm.ComposerText);
        Assert.Equal("@a/b.cs", FilesViewModel.Mention("a\\b.cs"));
        Assert.Equal("@\"notes.\"", FilesViewModel.Mention("notes."));
        Assert.Equal("@'say \"hi\".txt'", FilesViewModel.Mention("say \"hi\".txt"));
    }

    [AvaloniaFact]
    public async Task Copy_and_open_actions_use_the_clipboard_and_the_system()
    {
        var root = FileTestRepo.Create(git: false);
        var vm = FileTestRepo.Vm(root);
        var f = await FileTestRepo.Shown(vm, git: false);
        var copied = new List<string>();
        vm.CopyTextRequested += text => { copied.Add(text); return Task.FromResult(true); };
        var started = new List<System.Diagnostics.ProcessStartInfo>();
        var dbusWorks = false;
        f.Launch = (psi, wait) =>
        {
            started.Add(psi);
            return Task.FromResult(psi.FileName != "dbus-send" || dbusWorks);
        };
        var file = Path.Combine(root, "src", "app.py");
        await f.OpenFileAsync(file, line: 7);
        f.CopyPathCommand.Execute(f.Viewer);
        f.CopyRelativePathCommand.Execute(f.Viewer);
        Assert.Equal([file, Path.Combine("src", "app.py")], copied);
        await f.OpenInEditorCommand.ExecuteAsync(new EditorApp("VS Code", "/opt/code", EditorApp.Style.Goto));
        Assert.Equal(["-g", file + ":7"], started[^1].ArgumentList);
        await f.OpenInEditorCommand.ExecuteAsync(new EditorApp("Zed", "/opt/zed", EditorApp.Style.Suffix));
        Assert.Equal([file + ":7"], started[^1].ArgumentList);
        await f.OpenInDefaultAppCommand.ExecuteAsync(f.Viewer);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("xdg-open", started[^1].FileName);
            Assert.Equal([file], started[^1].ArgumentList);
            // Reveal: the file manager's D-Bus service selects it; without the service, its folder opens
            await f.RevealInFileManagerCommand.ExecuteAsync(f.Viewer);
            Assert.Equal("dbus-send", started[^2].FileName);
            Assert.Contains(started[^2].ArgumentList, a => a == "array:string:" + new Uri(file).AbsoluteUri);
            Assert.Equal(("xdg-open", Path.Combine(root, "src")), (started[^1].FileName, started[^1].ArgumentList[0]));
            dbusWorks = true;
            var before = started.Count;
            await f.RevealInFileManagerCommand.ExecuteAsync(f.Viewer);
            Assert.Equal(before + 1, started.Count);
        }
        // A program that cannot start is said in the pane
        f.Launch = (_, _) => Task.FromResult(false);
        await f.OpenInDefaultAppCommand.ExecuteAsync(f.Viewer);
        var notice = Loc.F("Could not {0}: {1} did not start.", Loc.F("open {0}", "app.py"), "\0");
        Assert.StartsWith(notice[..notice.IndexOf('\0')], f.Notice);
        f.DismissNoticeCommand.Execute(null);
        Assert.False(f.HasNotice);
    }

    [Fact]
    public void Editors_are_offered_only_when_their_command_is_on_PATH()
    {
        var dir = TestProcesses.TempDir("editors");
        var other = TestProcesses.TempDir("editors-other");
        var code = Path.Combine(dir, OperatingSystem.IsWindows() ? "code.cmd" : "code");
        File.WriteAllText(code, "#!/bin/sh\n");
        var zed = Path.Combine(other, "zed");
        File.WriteAllText(zed, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(code, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(zed, UnixFileMode.UserRead | UnixFileMode.UserWrite); // not executable: not a command
        }
        var found = FileOpeners.FindEditors(dir + Path.PathSeparator + other);
        Assert.Equal(OperatingSystem.IsWindows() ? ["VS Code", "Zed"] : ["VS Code"], found.Select(e => e.Name));
        Assert.Equal(Loc.F("Open in {0}", "VS Code"), found[0].Label);
        Assert.Empty(FileOpeners.FindEditors(TestProcesses.TempDir("editors-none")));
        var open = found[0].Open("/p/a.cs", 3);
        Assert.Equal(["-g", "/p/a.cs:3"], open.ArgumentList);
        Assert.Equal(["/p"], found[0].Open("/p").ArgumentList);
    }

    [AvaloniaFact]
    public async Task The_project_menu_browses_copies_opens_and_starts_a_terminal()
    {
        var root = FileTestRepo.Create(git: false);
        var vm = FileTestRepo.Vm(root);
        var f = vm.Files;
        var copied = new List<string>();
        vm.CopyTextRequested += text => { copied.Add(text); return Task.FromResult(true); };
        var started = new List<System.Diagnostics.ProcessStartInfo>();
        f.Launch = (psi, _) => { started.Add(psi); return Task.FromResult(true); };
        f.BrowseFilesCommand.Execute(null);
        Assert.Equal(SidePane.Files, vm.ActivePane);
        f.CopyProjectPathCommand.Execute(null);
        Assert.Equal([root], copied);
        await f.OpenProjectInFileManagerCommand.ExecuteAsync(null);
        Assert.Contains(root, OperatingSystem.IsWindows() ? [started[^1].FileName] : started[^1].ArgumentList);
        await f.OpenProjectInEditorCommand.ExecuteAsync(new EditorApp("Cursor", "/opt/cursor", EditorApp.Style.Goto));
        Assert.Equal(("/opt/cursor", root), (started[^1].FileName, started[^1].ArgumentList.Single()));
        // A shell tab in the terminal panel (in the folder omp reports; omp is not started here)
        f.OpenProjectTerminalCommand.Execute(null);
        Assert.True(vm.IsTerminalOpen);
        Assert.Equal(TerminalKind.Shell, vm.Terminals.Single().Kind);
        await f.DetectEditorsAsync(TestProcesses.TempDir("no-editors"));
        Assert.False(f.HasEditors);
    }

    [AvaloniaFact]
    public void The_change_card_path_opens_the_file_at_its_first_change()
    {
        int? opened = -1;
        var card = (Border)DiffView.Build(new FileChange("src/x.py", FileChangeKind.Modified, " 1|a\n 2|b\n+3|c\n"), openFile: line => opened = line);
        var link = card.GetLogicalDescendants().OfType<Button>().Single(b => b.Classes.Contains("diff-path-link"));
        Assert.Equal("src/x.py", string.Concat(((TextBlock)link.Content!).Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)));
        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(3, opened);
        // Without a handler the path is plain text
        var plain = (Border)DiffView.Build(new FileChange("y.py", FileChangeKind.Created, "+1|a\n"));
        Assert.DoesNotContain(plain.GetLogicalDescendants().OfType<Button>(), b => b.Classes.Contains("diff-path-link"));
        // A deleted file is its header (with its badge) and "Show N deleted lines" until asked
        var gone = (Border)DiffView.Build(new FileChange("old.py", FileChangeKind.Deleted, "-1|a\n-2|b\n"), unfolded: false, toggleFold: () => { });
        Assert.DoesNotContain(gone.GetLogicalDescendants().OfType<Grid>(), g => g.Classes.Contains("diff-row"));
        Assert.Contains(gone.GetLogicalDescendants().OfType<Button>(), b => b.Content as string == Loc.Plural(2, "Show {0} deleted line", "Show {0} deleted lines"));
        Assert.Contains(gone.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Deleted"));
    }
}
