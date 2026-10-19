using System.Text;
using Avalonia.Headless.XUnit;
using Ombra.App.Localization;
using Ombra.App.ViewModels;

namespace Ombra.Tests;

public sealed class FilesEditorTests
{
    [Fact]
    public void Code_mark_provenance_round_trips()
    {
        var anchor = new Ombra.ClientCore.CodeCommentAnchor("file:///src/a.cs", 7, true, "review-hash", "@@ -1 +1 @@");
        var mark = new Ombra.ClientCore.LocalMark("m", "rename", Code: new Ombra.ClientCore.CodeAnchor("src/a.cs", 3, false, "value", Snapshot: anchor));
        var recovered = System.Text.Json.JsonSerializer.Deserialize<Ombra.ClientCore.LocalMark>(System.Text.Json.JsonSerializer.Serialize(mark))!;
        Assert.Equal(anchor, recovered.Code!.Snapshot);
        Assert.Equal(mark.Note, recovered.Note);
        Assert.True(recovered.IsValid);
    }

    [AvaloniaFact]
    public async Task Explicit_editor_context_is_a_versioned_unsaved_snapshot_not_a_disk_reference()
    {
        var root = TestProcesses.TempDir("editor-context");
        var path = Path.Combine(root, "context.txt");
        File.WriteAllText(path, "disk");
        await using var vm = FileTestRepo.Vm(root);
        var document = await vm.Documents.OpenAsync(path);
        document.Buffer.Insert(0, "unsaved ");
        var revision = document.Revision;
        Assert.True(vm.AddEditorContext(document, document.Buffer.Text, 1, 1));
        var attachment = Assert.Single(vm.PastedTexts);
        Assert.Contains("\"source\": \"user-selected-editor-buffer\"", attachment.Text);
        Assert.Contains("\"documentVersion\": " + revision, attachment.Text);
        Assert.Contains("\"unsaved\": true", attachment.Text);
        document.Buffer.Insert(0, "later ");
        Assert.DoesNotContain("later", attachment.Text);
        Assert.Equal("disk", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Files_pane_edits_the_complete_buffer_not_the_shortened_preview()
    {
        var root = TestProcesses.TempDir("editor-complete");
        var path = Path.Combine(root, "wide.txt");
        var original = new string('x', FileViewerViewModel.MaxLineChars + 100) + "\r\n\tend\r\n";
        File.WriteAllText(path, original, new UTF8Encoding(true));
        await using var vm = FileTestRepo.Vm(root);
        var files = await FileTestRepo.Shown(vm, false);
        await files.OpenFileAsync(path);
        var view = files.Viewer!;
        var document = Assert.IsType<Ombra.App.Services.Documents.WorkspaceDocument>(view.Document);
        Assert.Equal(original, document.Buffer.Text);
        Assert.Equal("", view.PreviewText);
        Assert.True(view.ShowEditor);
        document.Buffer.Insert(0, "edited\r\n");
        Assert.True(view.IsDirty);
        await view.SaveDocumentCommand.ExecuteAsync(null);
        Assert.Equal("", view.DocumentError);
        Assert.Equal("edited\r\n" + original, File.ReadAllText(path));
        Assert.True(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        document.Buffer.UndoStack.Undo();
        Assert.Equal(original, document.Buffer.Text);
        Assert.True(document.IsDirty); // undo after save is another unsaved revision
    }

    [AvaloniaFact]
    public async Task Closing_and_reopening_a_file_keeps_its_buffer_and_undo_history()
    {
        var root = TestProcesses.TempDir("editor-navigation");
        var path = Path.Combine(root, "first.txt");
        File.WriteAllText(path, "disk\n");
        File.WriteAllText(Path.Combine(root, "other.txt"), "other\n");
        await using var vm = FileTestRepo.Vm(root);
        var files = await FileTestRepo.Shown(vm, false);
        await files.OpenFileAsync(path);
        var document = files.Viewer!.Document!;
        document.Buffer.Insert(0, "unsaved ");
        await files.OpenFileAsync(Path.Combine(root, "other.txt"));
        var unsaved = Assert.Single(files.UnsavedDocuments);
        Assert.True(vm.Documents.TryGet(unsaved.FullPath, out var listed));
        Assert.Same(document, listed);
        files.CloseViewerCommand.Execute(null);
        await files.OpenEditorBufferCommand.ExecuteAsync(unsaved);
        Assert.Same(document, files.Viewer!.Document);
        Assert.Equal("first.txt", files.Viewer.RelativePath);
        var reopened = files.Viewer;
        await files.OpenFileAsync(document.FullPath); // canonical and user-facing aliases reuse the same view
        Assert.Same(reopened, files.Viewer);
        Assert.Equal("unsaved disk\n", files.Viewer.Text);
        Assert.Equal("disk\n", File.ReadAllText(path));
        document.Buffer.UndoStack.Undo();
        Assert.Equal("disk\n", document.Buffer.Text);
    }

    [AvaloniaFact]
    public async Task Dirty_external_change_is_visible_and_cannot_be_saved_over()
    {
        var root = TestProcesses.TempDir("editor-conflict-ui");
        var path = Path.Combine(root, "file.txt");
        File.WriteAllText(path, "base\n");
        await using var vm = FileTestRepo.Vm(root);
        var files = await FileTestRepo.Shown(vm, false);
        await files.OpenFileAsync(path);
        var view = files.Viewer!;
        view.Document!.Buffer.Insert(0, "mine ");
        File.WriteAllText(path, "external\n");
        await vm.Documents.ObserveExternalChangeAsync(path);
        Assert.True(view.HasConflict);
        Assert.False(view.CanSaveDocument);
        Assert.Equal("mine base\n", view.Text);
        await view.SaveDocumentCommand.ExecuteAsync(null);
        Assert.Equal("external\n", File.ReadAllText(path));
        view.AskReloadDocumentCommand.Execute(null);
        Assert.True(view.IsConfirmingReload);
        view.CancelReloadDocumentCommand.Execute(null);
        Assert.Equal("mine base\n", view.Text);
        await view.ReloadDocumentCommand.ExecuteAsync(null);
        Assert.Equal("external\n", view.Text);
        Assert.False(view.IsDirty);
    }

    [AvaloniaFact]
    public async Task Omp_change_banner_reload_keeps_a_copy_and_keep_mine_saves_over()
    {
        var root = TestProcesses.TempDir("editor-omp-banner");
        var path = Path.Combine(root, "file.txt");
        File.WriteAllText(path, "base\n");
        await using var vm = FileTestRepo.Vm(root);
        var files = await FileTestRepo.Shown(vm, false);
        await files.OpenFileAsync(path);
        var view = files.Viewer!;
        view.Document!.Buffer.Insert(0, "mine ");
        File.WriteAllText(path, "omp\n");
        await vm.Documents.ObserveExternalChangeAsync(path);
        Assert.True(view.HasConflict);
        Assert.True(view.CanKeepMine);
        await view.ReloadKeepingCopyCommand.ExecuteAsync(null);
        Assert.Equal("omp\n", view.Text);
        Assert.False(view.HasConflict);
        var copy = Assert.Single(Directory.GetFiles(root, "file.my-unsaved-*.txt"));
        Assert.Equal("mine base\n", File.ReadAllText(copy));
        Assert.Equal(Loc.F("Your unsaved text was saved as {0}.", Path.GetFileName(copy)), view.DocumentNotice);

        view.Document.Buffer.Insert(0, "again ");
        File.WriteAllText(path, "omp 2\n");
        await vm.Documents.ObserveExternalChangeAsync(path);
        view.KeepMineDocumentCommand.Execute(null);
        Assert.False(view.HasConflict);
        Assert.True(view.CanSaveDocument);
        await view.SaveDocumentCommand.ExecuteAsync(null);
        Assert.Equal("again omp\n", File.ReadAllText(path));
    }

    [AvaloniaFact]
    public async Task Large_file_head_is_never_an_editable_document()
    {
        var root = TestProcesses.TempDir("editor-large-preview");
        var path = Path.Combine(root, "large.txt");
        File.WriteAllText(path, new string('x', (int)FileViewerViewModel.MaxTextBytes + 1));
        await using var vm = FileTestRepo.Vm(root);
        var files = await FileTestRepo.Shown(vm, false);
        await files.OpenFileAsync(path);
        Assert.Null(files.Viewer!.Document);
        await files.Viewer.ShowHeadCommand.ExecuteAsync(null);
        Assert.Null(files.Viewer.Document);
        Assert.False(files.Viewer.CanSaveDocument);
        Assert.True(files.Viewer.ShowPreview);
        Assert.Equal(FileViewerViewModel.MaxTextBytes + 1, new FileInfo(path).Length);
    }

    [AvaloniaFact]
    public async Task Independent_windows_do_not_share_mutable_buffers()
    {
        var root = TestProcesses.TempDir("editor-independent");
        var path = Path.Combine(root, "file.txt");
        File.WriteAllText(path, "disk");
        await using var first = FileTestRepo.Vm(root);
        await using var second = FileTestRepo.Vm(root);
        await first.Files.OpenFileAsync(path);
        await second.Files.OpenFileAsync(path);
        Assert.NotSame(first.Files.Viewer!.Document, second.Files.Viewer!.Document);
        first.Files.Viewer.Document!.Buffer.Insert(0, "one ");
        Assert.Equal("disk", second.Files.Viewer.Text);
        Assert.False(second.Files.Viewer.IsDirty);
    }
}
