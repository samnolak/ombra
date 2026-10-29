using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.App.Services;
using Ombra.App.Services.Documents;
using Ombra.App.ViewModels;
using Ombra.App.Views;
using static Ombra.Tests.SessionAreaTests;

namespace Ombra.Tests;

/// <summary>The editor's VS Code-class features: format, autosave, replace in files, tab drag, breadcrumbs, split, diff vs HEAD.</summary>
public sealed class EditorFeatureTests
{
    [AvaloniaTheory]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task Editor_page_with_split_breadcrumbs_replace_preview_and_diff_lays_out_cleanly(string theme)
    {
        MainViewModel.ApplyTheme(theme);
        var e = await OpenAsync(width: 1400, height: 820);
        try
        {
            FileTestRepo.Write(e.Project, "src/app.py", "def main():\n    return greet('ann@home')\n");
            FileTestRepo.Write(e.Project, "docs/notes.md", "write to bob@work about a really long line that has to be trimmed in the preview list\n");
            var vm = e.Vm;
            vm.OpenEditorPageCommand.Execute(null);
            await Until(() => vm.Files.Root is not null, "the project");
            var page = vm.EditorPage;
            await page.OpenAsync(Path.Combine(e.Project, "src", "app.py"));
            await page.SplitRightCommand.ExecuteAsync(null);
            await page.OpenAsync(Path.Combine(e.Project, "docs", "notes.md"));
            page.ActiveTab!.ToggleDiffCommand.Execute(null);
            page.IsSearchShown = true;
            page.IsReplaceShown = true;
            page.SearchRegex = true;
            page.SearchQuery = @"(\w+)@(\w+)";
            page.ReplaceText = "$2:$1";
            await page.PreviewReplaceAsync();
            page.ReplaceAllCommand.Execute(null);
            await Settle(600);
            var root = e.W.GetVisualDescendants().OfType<EditorPage>().Single();
            Shot(e.W, $"editor-page-{theme}.png");
            Assert.Empty(LayoutAudit.Run(e.W, root));

            await vm.OpenSettingsAtAsync("general");
            await Settle(300);
            var autosave = e.W.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AutosaveDelay");
            autosave.BringIntoView();
            await Settle(200);
            Shot(e.W, $"settings-autosave-{theme}.png");
            Assert.Empty(LayoutAudit.Run(e.W, autosave.FindAncestorOfType<Grid>()));
        }
        finally
        {
            await Close(e);
            MainViewModel.ApplyTheme("light");
        }
    }

    private static void Shot(Window w, string name)
    {
        if (Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") is not { } dir) return;
        Directory.CreateDirectory(dir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(dir, name));
        frame!.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public async Task Format_document_applies_the_language_servers_edits_as_one_undo_step()
    {
        var e = await OpenAsync();
        var window = (Window?)null;
        try
        {
            var path = Path.Combine(e.Project, "greet.py");
            File.WriteAllText(path, "class Greeter:   \n    def hello(self):  \n        return 1\n");
            var service = e.Vm.EditorLanguageService;
            for (var attempt = 0; !service.IsTrusted; attempt++)
            {
                try { await service.SetTrustedAsync(true); }
                catch (InvalidOperationException) when (attempt < 200) { await Settle(50); } // the editor transport attaches after start
            }
            File.WriteAllText(Path.Combine(e.Project, "notes.txt"), "plain  \n");
            var document = await e.Vm.Documents.OpenAsync(path);
            var editor = new DocumentEditor { WorkspaceDocument = document, LanguageService = service };
            window = new Window { Content = editor, Width = 700, Height = 400 };
            window.Show();
            await Until(() => editor.Capabilities is not null, "the server's capabilities");
            Assert.True(editor.Capabilities!.Formatting);
            Assert.Null(editor.FormatUnavailableReason(false));
            Assert.NotNull(editor.FormatUnavailableReason(true)); // nothing selected

            Assert.True(await editor.FormatAsync(false));
            Assert.Equal("class Greeter:\n    def hello(self):\n        return 1\n", document.Buffer.Text);
            Assert.True(document.IsDirty);
            editor.Editor.Undo();
            Assert.Equal("class Greeter:   \n    def hello(self):  \n        return 1\n", document.Buffer.Text);

            // Format selection: only the selected lines
            editor.Editor.Select(document.Buffer.GetLineByNumber(2).Offset, document.Buffer.GetLineByNumber(2).Length);
            Assert.Null(editor.FormatUnavailableReason(true));
            Assert.True(await editor.FormatAsync(true));
            Assert.Equal("class Greeter:   \n    def hello(self):\n        return 1\n", document.Buffer.Text);

            // A file whose server can't format: disabled, with the reason
            editor.WorkspaceDocument = await e.Vm.Documents.OpenAsync(Path.Combine(e.Project, "notes.txt"));
            await Until(() => editor.Capabilities is not null, "the plain file's capabilities");
            Assert.Equal("The language server can't format this file", editor.FormatUnavailableReason(false));
            Assert.False(await editor.FormatAsync(false));
            Assert.Equal("plain  \n", editor.WorkspaceDocument!.Buffer.Text);

            // Breadcrumbs: the symbol at the caret from documentSymbol
            e.Vm.OpenEditorPageCommand.Execute(null);
            await e.Vm.EditorPage.OpenAsync(path);
            var tab = e.Vm.EditorPage.ActiveTab!;
            await tab.RefreshSymbolsAsync(TimeSpan.Zero);
            tab.CaretMoved(3, 8);
            Assert.Equal("Greeter › hello", tab.Symbol);
            tab.CaretMoved(1, 0);
            Assert.Equal("Greeter", tab.Symbol);
        }
        finally
        {
            window?.Close();
            await Close(e);
        }
    }

    [Fact]
    public void Text_edits_apply_all_or_nothing()
    {
        var buffer = new AvaloniaEdit.Document.TextDocument("ab\ncd\n");
        var edits = JsonDocument.Parse("""[{"range":{"start":{"line":1,"character":0},"end":{"line":1,"character":1}},"newText":"X"},{"range":{"start":{"line":0,"character":2},"end":{"line":0,"character":2}},"newText":"!"}]""").RootElement;
        Assert.True(DocumentEditor.ApplyTextEdits(buffer, edits));
        Assert.Equal("ab!\nXd\n", buffer.Text);
        var overlapping = JsonDocument.Parse("""[{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":2}},"newText":""},{"range":{"start":{"line":0,"character":1},"end":{"line":0,"character":3}},"newText":""}]""").RootElement;
        Assert.False(DocumentEditor.ApplyTextEdits(buffer, overlapping));
        Assert.Equal("ab!\nXd\n", buffer.Text);
        var outside = JsonDocument.Parse("""[{"range":{"start":{"line":9,"character":0},"end":{"line":9,"character":1}},"newText":""}]""").RootElement;
        Assert.False(DocumentEditor.ApplyTextEdits(buffer, outside));
    }

    [AvaloniaFact]
    public async Task Autosave_writes_a_second_after_the_last_edit_or_when_focus_leaves()
    {
        var root = TestProcesses.TempDir("editor-autosave");
        var path = Path.Combine(root, "a.txt");
        File.WriteAllText(path, "one\n");
        await using var vm = FileTestRepo.Vm(root);
        var document = await vm.Documents.OpenAsync(path);
        Assert.Equal(EditorAutosave.Off, vm.EditorAutosaveMode);

        // Off: nothing is written
        document.Buffer.Insert(0, "x");
        await Task.Delay(1300);
        Assert.Equal("one\n", File.ReadAllText(path));

        // After a delay: written about a second after the last edit, not before
        vm.SetEditorAutosaveCommand.Execute(EditorAutosave.AfterDelay);
        var started = DateTime.UtcNow;
        document.Buffer.Insert(0, "y");
        await Task.Delay(400);
        Assert.Equal("one\n", File.ReadAllText(path));
        await FileTestRepo.Until(() => File.ReadAllText(path) == "yxone\n", "the autosave", 5);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(900));
        Assert.False(document.IsDirty);

        // On focus change: written when an editor showing it loses the keyboard, not on a timer
        vm.SetEditorAutosaveCommand.Execute(EditorAutosave.OnFocusChange);
        document.Buffer.Insert(0, "z");
        await Task.Delay(1300);
        Assert.Equal("yxone\n", File.ReadAllText(path));
        document.NoteEditorFocusLeft();
        await FileTestRepo.Until(() => File.ReadAllText(path) == "zyxone\n", "the focus autosave", 5);
        document.Buffer.Insert(0, "w");
        vm.EditorAutosave.WindowDeactivated();
        await FileTestRepo.Until(() => File.ReadAllText(path) == "wzyxone\n", "the window autosave", 5);
    }

    [AvaloniaFact]
    public async Task Replace_all_in_files_uses_regex_groups_skips_excluded_files_and_undoes_per_file()
    {
        var root = TestProcesses.TempDir("editor-replace");
        FileTestRepo.Write(root, "a.txt", "mail ann@home\nnone\n");
        FileTestRepo.Write(root, "b.txt", "bob@work and cy@lab\n");
        FileTestRepo.Write(root, "c.txt", "dan@shop\n");
        await using var vm = FileTestRepo.Vm(root);
        vm.OpenEditorPageCommand.Execute(null);
        await FileTestRepo.Until(() => vm.Files.Root is not null, "the project");
        var page = vm.EditorPage;
        // b.txt is open with an unsaved edit: it changes in its buffer and stays unsaved
        await page.OpenAsync(Path.Combine(root, "b.txt"));
        var b = page.ActiveTab!.Document;
        b.Buffer.Insert(b.Buffer.TextLength, "tail\n");

        page.IsReplaceShown = true;
        page.SearchRegex = true;
        page.SearchQuery = @"(\w+)@(\w+)";
        page.ReplaceText = "$2:$1";
        await page.PreviewReplaceAsync();
        Assert.Equal(["a.txt", "b.txt", "c.txt"], page.ReplaceFiles.Select(f => f.RelativePath).Order(StringComparer.Ordinal));
        var bFile = page.ReplaceFiles.Single(f => f.RelativePath == "b.txt");
        Assert.Equal(2, bFile.Matches);
        Assert.Equal("work:bob and lab:cy", bFile.Hits.Single().Replaced);
        page.ReplaceFiles.Single(f => f.RelativePath == "c.txt").IsIncluded = false;

        // Asked first; nothing written yet
        page.ReplaceAllCommand.Execute(null);
        Assert.True(page.IsConfirmingReplace);
        Assert.Contains("3", page.ReplaceConfirmation);
        Assert.Contains("unsaved", page.ReplaceConfirmation);
        Assert.Equal("mail ann@home\nnone\n", File.ReadAllText(Path.Combine(root, "a.txt")));

        await page.ConfirmReplaceAsync();
        Assert.Equal("mail home:ann\nnone\n", File.ReadAllText(Path.Combine(root, "a.txt")));
        Assert.Equal("bob@work and cy@lab\n", File.ReadAllText(Path.Combine(root, "b.txt")));
        Assert.Equal("work:bob and lab:cy\ntail\n", b.Buffer.Text); // the open tab's buffer
        Assert.True(b.IsDirty);
        Assert.Equal("dan@shop\n", File.ReadAllText(Path.Combine(root, "c.txt")));

        // One undo step per file
        Assert.True(vm.Documents.TryGet(Path.Combine(root, "a.txt"), out var a));
        a.Buffer.UndoStack.Undo();
        Assert.Equal("mail ann@home\nnone\n", a.Buffer.Text);
        b.Buffer.UndoStack.Undo();
        Assert.Equal("bob@work and cy@lab\ntail\n", b.Buffer.Text);
    }

    [AvaloniaFact]
    public async Task Find_in_files_honours_match_case_whole_word_and_regex()
    {
        var root = TestProcesses.TempDir("editor-find-options");
        FileTestRepo.Write(root, "x.txt", "Cat\ncat\nconcat\ncat9\n");
        await using var vm = FileTestRepo.Vm(root);
        vm.OpenEditorPageCommand.Execute(null);
        await FileTestRepo.Until(() => vm.Files.Root is not null, "the project");
        var page = vm.EditorPage;
        await page.FindInFilesAsync("cat");
        Assert.Equal([1, 2, 3, 4], page.SearchHits.Select(h => h.Line));
        page.SearchMatchCase = true;
        await page.FindInFilesAsync("cat");
        Assert.Equal([2, 3, 4], page.SearchHits.Select(h => h.Line));
        page.SearchWholeWord = true;
        await page.FindInFilesAsync("cat");
        Assert.Equal([2], page.SearchHits.Select(h => h.Line));
        page.SearchWholeWord = false;
        page.SearchRegex = true;
        await page.FindInFilesAsync(@"^cat\d$");
        Assert.Equal([4], page.SearchHits.Select(h => h.Line));
        await page.FindInFilesAsync("(");
        Assert.Empty(page.SearchHits);
        Assert.StartsWith("Not a valid regular expression", page.SearchNote);
    }

    [AvaloniaFact]
    public async Task Tabs_reorder_by_dragging_along_the_strip()
    {
        var root = TestProcesses.TempDir("editor-tab-drag");
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) FileTestRepo.Write(root, name, name + "\n");
        await using var vm = FileTestRepo.Vm(root);
        vm.OpenEditorPageCommand.Execute(null);
        await FileTestRepo.Until(() => vm.Files.Root is not null, "the project");
        var page = vm.EditorPage;
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) await page.OpenAsync(Path.Combine(root, name));
        var window = new Window { Content = new EditorPage { DataContext = page }, Width = 1100, Height = 600 };
        window.Show();
        try
        {
            await Settle();
            Border Tab(string name) => window.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("editor-tab") && b.DataContext is EditorTabViewModel t && t.Name == name);
            Point Center(Border b) => b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), window)!.Value;
            var from = Center(Tab("a.txt"));
            var to = Center(Tab("c.txt"));
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(new Point(from.X + 10, from.Y), RawInputModifiers.LeftMouseButton);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.MouseUp(to, MouseButton.Left);
            await Settle(100);
            Assert.Equal(["b.txt", "c.txt", "a.txt"], page.Tabs.Select(t => t.Name));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Split_opens_the_same_document_in_a_second_group_and_closing_it_keeps_the_first()
    {
        var root = TestProcesses.TempDir("editor-split");
        FileTestRepo.Write(root, "src/app.py", "print(1)\n");
        await using var vm = FileTestRepo.Vm(root);
        vm.OpenEditorPageCommand.Execute(null);
        await FileTestRepo.Until(() => vm.Files.Root is not null, "the project");
        var page = vm.EditorPage;
        await page.OpenAsync(Path.Combine(root, "src", "app.py"));
        var left = page.ActiveGroup;
        var first = page.ActiveTab!;
        var window = new Window { Content = new EditorPage { DataContext = page }, Width = 1100, Height = 600 };
        window.Show();
        try
        {
            await page.SplitRightCommand.ExecuteAsync(null);
            Assert.True(page.HasSplit);
            Assert.Equal(2, page.Groups.Count);
            var right = page.ActiveGroup;
            Assert.NotSame(left, right);
            Assert.NotSame(first, page.ActiveTab);
            Assert.Same(first.Document, page.ActiveTab!.Document); // one buffer, two views
            await Settle();
            var editors = window.GetVisualDescendants().OfType<DocumentEditor>().Where(d => d.IsEffectivelyVisible).ToList();
            Assert.Equal(2, editors.Count);
            Assert.All(editors, d => Assert.Same(first.Document, d.WorkspaceDocument));
            first.Document.Buffer.Insert(0, "# ");
            Assert.All(editors, d => Assert.StartsWith("# ", d.Editor.Text));

            // Files open in the focused group; breadcrumbs are the path from the project folder
            Assert.Equal(["src", "app.py"], page.ActiveTab.Breadcrumbs.Select(c => c.Label));
            await page.ActiveTab.RevealCommand.ExecuteAsync(page.ActiveTab.Breadcrumbs[0]);
            Assert.Equal(Path.Combine(root, "src"), page.RevealedNode?.FullPath);

            right.CloseCommand.Execute(null);
            Assert.False(page.HasSplit);
            Assert.Same(left, page.ActiveGroup);
            Assert.Same(first, page.ActiveTab);
            Assert.False(left.CanClose);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Symbol_path_follows_nested_and_flat_symbols()
    {
        var nested = JsonDocument.Parse("""
            [{"name":"A","range":{"start":{"line":0,"character":0},"end":{"line":9,"character":0}},
              "children":[{"name":"b","range":{"start":{"line":2,"character":0},"end":{"line":4,"character":0}}}]}]
            """).RootElement;
        Assert.Equal("A › b", EditorTabViewModel.SymbolPath(nested, 3, 1));
        Assert.Equal("A", EditorTabViewModel.SymbolPath(nested, 6, 0));
        Assert.Equal("", EditorTabViewModel.SymbolPath(nested, 12, 0));
        var flat = JsonDocument.Parse("""
            [{"name":"m","location":{"uri":"file:///x","range":{"start":{"line":2,"character":0},"end":{"line":3,"character":0}}}},
             {"name":"C","location":{"uri":"file:///x","range":{"start":{"line":0,"character":0},"end":{"line":5,"character":0}}}}]
            """).RootElement;
        Assert.Equal("C › m", EditorTabViewModel.SymbolPath(flat, 2, 4));
    }

    [Fact]
    public void Unified_diff_has_hunks_with_context()
    {
        var head = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"l{i}")) + "\n";
        var now = head.Replace("l2\n", "L2\n").Replace("l15\n", "l15\nnew\n");
        var diff = GitLineChanges.UnifiedDiff(head, now);
        Assert.Equal(
            "@@ -1,5 +1,5 @@\n l1\n-l2\n+L2\n l3\n l4\n l5\n@@ -13,6 +13,7 @@\n l13\n l14\n l15\n+new\n l16\n l17\n l18\n",
            diff);
        Assert.Equal("", GitLineChanges.UnifiedDiff(head, head.Replace("\n", "\r\n")));
        Assert.Equal("@@ -0,0 +1,1 @@\n+x\n", GitLineChanges.UnifiedDiff("", "x\n"));
        var (added, removed) = DiffView.Stats(diff);
        Assert.Equal((2, 1), (added, removed));
    }

    [AvaloniaFact]
    public async Task Diff_vs_HEAD_toggle_shows_the_changes_and_says_when_there_are_none()
    {
        if (!FileTestRepo.HasGit()) return;
        var root = TestProcesses.TempDir("editor-diff");
        FileTestRepo.Write(root, "f.txt", "one\ntwo\nthree\n");
        FileTestRepo.Git(root, "init", "-q");
        FileTestRepo.Git(root, "add", "-A");
        FileTestRepo.Git(root, "commit", "-q", "-m", "init");
        await using var vm = FileTestRepo.Vm(root);
        var page = vm.EditorPage;
        await page.OpenAsync(Path.Combine(root, "f.txt"));
        var tab = page.ActiveTab!;
        var window = new Window { Content = new EditorPage { DataContext = page }, Width = 1100, Height = 600 };
        window.Show();
        try
        {
            tab.ToggleDiffCommand.Execute(null);
            await FileTestRepo.Until(() => tab.HasDiffNote, "no changes yet");
            Assert.Equal("No changes against HEAD.", tab.DiffNote);

            tab.Document.Buffer.Replace(4, 3, "TWO"); // unsaved edits count
            await FileTestRepo.Until(() => tab.HasDiff, "the diff");
            Assert.Contains("-two\n+TWO\n", tab.Diff!.Diff);
            Assert.False(tab.HasDiffNote);
            await Settle();
            var diffView = window.GetVisualDescendants().OfType<DiffView>().Single();
            Assert.True(diffView.IsEffectivelyVisible);
            Assert.False(window.GetVisualDescendants().OfType<DocumentEditor>().Single().IsVisible);

            tab.ToggleDiffCommand.Execute(null);
            Assert.Null(tab.Diff);
            await Settle();
            Assert.True(window.GetVisualDescendants().OfType<DocumentEditor>().Single().IsVisible);
        }
        finally { window.Close(); }
    }
}
