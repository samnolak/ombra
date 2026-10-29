using Avalonia.Headless.XUnit;
using Ombra.App.Controls;
using Ombra.App.Services;
using Ombra.App.ViewModels;

namespace Ombra.Tests;

public sealed class EditorPageTests
{
    [AvaloniaFact]
    public async Task Rail_editor_page_hides_the_sidebar_and_chats_bring_it_back()
    {
        var root = TestProcesses.TempDir("editor-page-rail");
        await using var vm = FileTestRepo.Vm(root);
        Assert.True(vm.IsChatsRailOn);
        vm.OpenEditorPageCommand.Execute(null);
        Assert.True(vm.ShowEditorPage);
        Assert.True(vm.IsEditorRailOn);
        Assert.False(vm.IsChatsRailOn);
        Assert.False(vm.ShowSidebar);
        vm.OpenBrowserPageCommand.Execute(null);
        Assert.False(vm.ShowEditorPage);
        vm.ToggleEditorPageCommand.Execute(null);
        Assert.True(vm.ShowEditorPage);
        Assert.False(vm.ShowBrowserPage);
        vm.ToggleEditorPageCommand.Execute(null);
        Assert.False(vm.ShowEditorPage);
        Assert.True(vm.IsChatsRailOn);
        vm.OpenEditorPageCommand.Execute(null);
        vm.ShowChatsCommand.Execute(null);
        Assert.False(vm.ShowEditorPage);
        Assert.True(vm.IsChatsRailOn);
    }

    [AvaloniaFact]
    public async Task Links_open_in_page_tabs_while_the_page_is_shown_and_in_the_panel_otherwise()
    {
        var root = TestProcesses.TempDir("editor-page-tabs");
        FileTestRepo.Write(root, "a.txt", "one\ntwo\nthree\n");
        FileTestRepo.Write(root, "b.txt", "bee\n");
        await using var vm = FileTestRepo.Vm(root);
        vm.OpenEditorPageCommand.Execute(null);
        await FileTestRepo.Until(() => vm.Files.Nodes.Count > 0, "the page's tree");
        await vm.Files.OpenLinkCommand.ExecuteAsync(new FileLink("a.txt", 2));
        await vm.Files.OpenLinkCommand.ExecuteAsync(new FileLink("b.txt"));
        var page = vm.EditorPage;
        Assert.Equal(["a.txt", "b.txt"], page.Tabs.Select(t => t.Name));
        Assert.Equal("b.txt", page.ActiveTab!.Name);
        Assert.Null(vm.Files.Viewer); // the right panel was not used
        await vm.Files.OpenLinkCommand.ExecuteAsync(new FileLink("a.txt", 3));
        Assert.Equal(2, page.Tabs.Count);
        Assert.Equal(3, page.ActiveTab!.TargetLine);

        // Dirty dot, reorder, close: the buffer stays the workspace's
        page.ActiveTab.Document.Buffer.Insert(0, "x");
        Assert.True(page.ActiveTab.IsDirty);
        page.MoveTabRightCommand.Execute(page.ActiveTab);
        Assert.Equal(["b.txt", "a.txt"], page.Tabs.Select(t => t.Name));
        var a = page.ActiveTab;
        a.CloseCommand.Execute(null);
        Assert.Equal("b.txt", Assert.Single(page.Tabs).Name);
        Assert.Equal("b.txt", page.ActiveTab!.Name);
        Assert.True(vm.Documents.TryGet(Path.Combine(root, "a.txt"), out var kept) && kept.IsDirty);

        // Quick open is the palette's files mode: its pick lands in a tab too
        await page.OpenAsync(Path.Combine(root, "a.txt"));
        Assert.Same(kept, page.ActiveTab!.Document);

        // Off the page, links go to the right panel as before
        vm.ShowChatsCommand.Execute(null);
        await vm.Files.OpenLinkCommand.ExecuteAsync(new FileLink("b.txt"));
        Assert.Equal("b.txt", vm.Files.Viewer!.Name);
    }

    [AvaloniaFact]
    public async Task Find_in_files_lists_lines_and_skips_dependencies_and_binaries()
    {
        var root = TestProcesses.TempDir("editor-page-search");
        FileTestRepo.Write(root, "src/app.py", "def main():\n    return Needle()\n");
        FileTestRepo.Write(root, "docs/x.md", "no match\nthe needle here\n");
        FileTestRepo.Write(root, "node_modules/pkg/index.js", "needle\n");
        File.WriteAllBytes(Path.Combine(root, "data.bin"), [0x6E, 0x65, 0x65, 0x64, 0x6C, 0x65, 0x00, 0x01]);
        await using var vm = FileTestRepo.Vm(root);
        vm.OpenEditorPageCommand.Execute(null);
        await FileTestRepo.Until(() => vm.Files.Root is not null, "the project");
        var page = vm.EditorPage;
        await page.FindInFilesAsync("needle");
        var hits = page.SearchHits.OrderBy(h => h.RelativePath, StringComparer.Ordinal).ToList();
        Assert.Equal(["docs/x.md:2", "src/app.py:2"], hits.Select(h => h.Location));
        Assert.Equal("return Needle()", hits[1].Text);
        await page.OpenHitCommand.ExecuteAsync(hits[1]);
        Assert.Equal("app.py", page.ActiveTab!.Name);
        Assert.Equal(2, page.ActiveTab.TargetLine);
        await page.FindInFilesAsync("absent-text");
        Assert.Empty(page.SearchHits);
        Assert.True(page.HasSearchNote);
    }

    [AvaloniaFact]
    public async Task Go_to_line_moves_the_caret_and_clamps_to_the_file()
    {
        var root = TestProcesses.TempDir("editor-page-goto");
        var path = Path.Combine(root, "lines.txt");
        File.WriteAllText(path, "a\nbb\nccc\n");
        await using var vm = FileTestRepo.Vm(root);
        var document = await vm.Documents.OpenAsync(path);
        var editor = new DocumentEditor { WorkspaceDocument = document };
        var window = new Avalonia.Controls.Window { Content = editor, Width = 600, Height = 400 };
        window.Show();
        try
        {
            Assert.True(editor.GoToLine("3:2"));
            Assert.Equal((3, 2), (editor.Editor.TextArea.Caret.Line, editor.Editor.TextArea.Caret.Column));
            Assert.True(editor.GoToLine("99"));
            Assert.Equal(document.Buffer.LineCount, editor.Editor.TextArea.Caret.Line);
            Assert.False(editor.GoToLine("abc"));
            editor.ShowGoToLine();
            Assert.True(editor.GoToLineBox.IsVisible);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Line_diff_becomes_added_modified_and_deleted_marks()
    {
        const string head = "a\nb\nc\nd\ne\nf\ng\n";
        const string now = "a\nB\nc\nd\nx\ny\ne\ng\n";
        Assert.Equal(
            [new LineChange(2, LineChangeKind.Modified), new LineChange(5, LineChangeKind.Added), new LineChange(6, LineChangeKind.Added), new LineChange(7, LineChangeKind.Deleted)],
            GitLineChanges.Compute(head, now));
        Assert.Empty(GitLineChanges.Compute(head, head.Replace("\n", "\r\n")));
        Assert.Equal([new LineChange(0, LineChangeKind.Deleted)], GitLineChanges.Compute("a\nb\n", "b\n"));
        Assert.Equal([new LineChange(1, LineChangeKind.Added), new LineChange(2, LineChangeKind.Added)], GitLineChanges.Compute("", "x\ny"));
    }

    [AvaloniaFact]
    public async Task Gutter_follows_unsaved_edits_against_HEAD()
    {
        if (!FileTestRepo.HasGit()) return;
        var root = TestProcesses.TempDir("editor-page-gutter");
        FileTestRepo.Write(root, "f.txt", "one\ntwo\nthree\n");
        FileTestRepo.Git(root, "init", "-q");
        FileTestRepo.Git(root, "add", "-A");
        FileTestRepo.Git(root, "commit", "-q", "-m", "init");
        File.WriteAllText(Path.Combine(root, "f.txt"), "one\nTWO\nthree\nfour\n");
        await using var vm = FileTestRepo.Vm(root);
        vm.OpenEditorPageCommand.Execute(null);
        await vm.EditorPage.OpenAsync(Path.Combine(root, "f.txt"));
        var tab = vm.EditorPage.ActiveTab!;
        await FileTestRepo.Until(() => tab.LineChanges.Count == 2, "the gutter");
        Assert.Equal([new LineChange(2, LineChangeKind.Modified), new LineChange(4, LineChangeKind.Added)], tab.LineChanges);

        // An unsaved line at the top: the marks move with their lines, before any save
        tab.Document.Buffer.Insert(0, "zero\n");
        Assert.True(tab.IsDirty);
        await FileTestRepo.Until(() => tab.LineChanges.Count == 3, "the gutter after typing");
        Assert.Equal([new LineChange(1, LineChangeKind.Added), new LineChange(3, LineChangeKind.Modified), new LineChange(5, LineChangeKind.Added)], tab.LineChanges);

        // Saved and committed: HEAD is read again on save, and the same text has no marks against it
        await tab.Document.SaveAsync();
        FileTestRepo.Git(root, "commit", "-q", "-am", "next");
        await tab.RefreshGutterAsync();
        Assert.Empty(tab.LineChanges);
    }
}
