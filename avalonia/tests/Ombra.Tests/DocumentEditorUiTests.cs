using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Ombra.App.Controls;
using Ombra.App.Services.Documents;

namespace Ombra.Tests;

public sealed class DocumentEditorUiTests
{
    [AvaloniaFact]
    public async Task Editor_uses_canonical_buffer_and_native_input_undo_redo_and_save()
    {
        var root = TestProcesses.TempDir("document-editor");
        var path = Path.Combine(root, "example.txt");
        await File.WriteAllTextAsync(path, "first\nsecond\nthird\n");
        await using var documents = new WorkspaceDocumentService(watchFiles: false);
        var document = await documents.OpenAsync(path);
        var editor = new DocumentEditor { WorkspaceDocument = document, TargetLine = 2 };
        var window = new Window { Width = 720, Height = 480, Content = editor };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Same(document.Buffer, editor.Editor.Document);
            Assert.Equal(2, editor.Editor.TextArea.Caret.Line);
            editor.Editor.TextArea.PerformTextInput("changed ");
            Assert.True(document.IsDirty);
            Assert.Contains("changed second", document.Buffer.Text);
            Assert.DoesNotContain("changed", await File.ReadAllTextAsync(path));
            Assert.True(editor.Editor.Undo());
            Assert.Equal("first\nsecond\nthird\n", document.Buffer.Text);
            Assert.True(editor.Editor.Redo());
            await editor.SaveAsync();
            Assert.False(document.IsDirty);
            Assert.Equal(document.Buffer.Text, await File.ReadAllTextAsync(path));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Native_search_replace_is_undoable_and_manual_plaintext_survives_reattach()
    {
        var root = TestProcesses.TempDir("document-search");
        var path = Path.Combine(root, "script");
        await File.WriteAllTextAsync(path, "#!/usr/bin/env python3\nprint('old old')\n");
        await using var documents = new WorkspaceDocumentService(watchFiles: false);
        var document = await documents.OpenAsync(path);
        var editor = new DocumentEditor { WorkspaceDocument = document };
        var window = new Window { Width = 720, Height = 480, Content = editor };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("python", document.LanguageId);
            editor.ShowSearch(true);
            var search = editor.Editor.SearchPanel;
            Assert.NotNull(search);
            search.SearchPattern = "old";
            search.ReplacePattern = "new";
            search.ReplaceAll();
            Assert.Contains("new new", document.Buffer.Text);
            Assert.True(editor.Editor.Undo());
            Assert.Contains("old old", document.Buffer.Text);
            document.LanguageId = "plaintext";
            window.Content = null;
            window.Content = editor;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("plaintext", document.LanguageId);
            Assert.Same(document.Buffer, editor.Editor.Document);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Discontiguous_carets_insert_at_each_position_in_one_native_undo_group()
    {
        var editor = new TextEditor { Document = new TextDocument("alpha\nbeta\ngamma") };
        editor.TextArea.Caret.Offset = 1;
        MultiSelection.AddCaret(editor.TextArea, 8);
        MultiSelection.AddCaret(editor.TextArea, 14);
        editor.TextArea.PerformTextInput("X");
        Assert.Equal("aXlpha\nbeXta\ngamXma", editor.Document.Text);
        Assert.IsType<MultiSelection>(editor.TextArea.Selection);
        Assert.Equal(3, editor.TextArea.Selection.Segments.Count());
        editor.Document.UndoStack.Undo();
        Assert.Equal("alpha\nbeta\ngamma", editor.Document.Text);
        editor.Document.UndoStack.Redo();
        Assert.Equal("aXlpha\nbeXta\ngamXma", editor.Document.Text);
    }

    [AvaloniaFact]
    public void Discontiguous_selections_replace_without_touching_intervening_text_and_respect_readonly()
    {
        var editor = new TextEditor { Document = new TextDocument("one KEEP two") };
        editor.TextArea.Selection = new MultiSelection(editor.TextArea,
            new[] { new SelectionSegment(0, 3), new SelectionSegment(9, 12) }, 12);
        editor.TextArea.PerformTextInput("word");
        Assert.Equal("word KEEP word", editor.Document.Text);
        editor.IsReadOnly = true;
        editor.TextArea.PerformTextInput("blocked");
        Assert.Equal("word KEEP word", editor.Document.Text);
    }

    [AvaloniaFact]
    public async Task Column_selection_remains_available_alongside_discontiguous_carets()
    {
        var root = TestProcesses.TempDir("editor-column");
        var path = Path.Combine(root, "columns.txt");
        await File.WriteAllTextAsync(path, "one\ntwo");
        await using var documents = new WorkspaceDocumentService(watchFiles: false);
        var document = await documents.OpenAsync(path);
        var view = new DocumentEditor { WorkspaceDocument = document };
        var window = new Window { Width = 720, Height = 480, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            // Rectangles use visual X positions, not character-column indexes.
            // Exercise the actual configured monospace editor after layout.
            var editor = view.Editor;
            Assert.True(editor.Options.EnableRectangularSelection);
            editor.TextArea.Selection = new RectangleSelection(editor.TextArea, new TextViewPosition(1, 2), new TextViewPosition(2, 2));
            Assert.All(editor.TextArea.Selection.Segments, segment => Assert.Equal(0, segment.Length));
            editor.TextArea.PerformTextInput("X");
            Assert.Equal("oXne\ntXwo", editor.Document.Text);
            editor.Document.UndoStack.Undo();
            Assert.Equal("one\ntwo", editor.Document.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Folding_regions_are_sorted_and_cover_indented_blocks()
    {
        var buffer = new TextDocument("root\n    child\n        grandchild\n    sibling\nend\n");
        var folds = DocumentEditor.CreateIndentFoldings(buffer).ToArray();
        Assert.Equal(2, folds.Length);
        Assert.Equal(buffer.GetLineByNumber(1).EndOffset, folds[0].StartOffset);
        Assert.Equal(buffer.GetLineByNumber(4).EndOffset, folds[0].EndOffset);
        Assert.Equal(buffer.GetLineByNumber(2).EndOffset, folds[1].StartOffset);
        Assert.Equal(buffer.GetLineByNumber(3).EndOffset, folds[1].EndOffset);
    }

    [AvaloniaFact]
    public void Runtime_ranges_use_utf16_and_reject_out_of_bounds_positions()
    {
        var buffer = new TextDocument("a😀z\nnext");
        using var valid = JsonDocument.Parse("{\"start\":{\"line\":0,\"character\":1},\"end\":{\"line\":0,\"character\":3}}");
        Assert.True(DocumentEditor.TryRange(buffer, valid.RootElement, out var offset, out var length));
        Assert.Equal("😀", buffer.GetText(offset, length));
        using var invalid = JsonDocument.Parse("{\"start\":{\"line\":0,\"character\":1},\"end\":{\"line\":0,\"character\":99}}");
        Assert.False(DocumentEditor.TryRange(buffer, invalid.RootElement, out _, out _));
    }

    [AvaloniaFact]
    public async Task Rejected_input_is_reported_and_partial_native_transactions_roll_back()
    {
        var root = TestProcesses.TempDir("document-rejection");
        var path = Path.Combine(root, "example.txt");
        await File.WriteAllTextAsync(path, "safe text");
        await using var documents = new WorkspaceDocumentService(watchFiles: false);
        var document = await documents.OpenAsync(path);
        var editor = new DocumentEditor { WorkspaceDocument = document };
        editor.Editor.TextArea.PerformTextInput("\0");
        Assert.Equal("safe text", document.Buffer.Text);
        Assert.False(document.IsDirty);
        Assert.False(editor.Editor.TextArea.ExecuteEdit(() =>
        {
            document.Buffer.Insert(0, "accepted ");
            document.Buffer.Insert(0, "\0");
        }));
        Assert.Equal("safe text", document.Buffer.Text);
        Assert.False(document.IsDirty);
        Assert.False(document.Buffer.UndoStack.CanRedo);
        Assert.False(document.Buffer.IsInUpdate);
    }

    [AvaloniaFact]
    public async Task Ime_composition_is_shown_inline_without_touching_the_buffer_until_commit()
    {
        var root = TestProcesses.TempDir("document-ime");
        var path = Path.Combine(root, "names.txt");
        await File.WriteAllTextAsync(path, "name: \nnext\n");
        await using var documents = new WorkspaceDocumentService(watchFiles: false);
        var document = await documents.OpenAsync(path);
        var editor = new DocumentEditor { WorkspaceDocument = document };
        var window = new Window { Width = 720, Height = 480, Content = editor };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var area = editor.Editor.TextArea;
            Assert.True(area.Focus());
            area.Caret.Offset = "name: ".Length;
            // The platform's routed client request, as the native input method does it.
            var request = new Avalonia.Input.TextInput.TextInputMethodClientRequestedEventArgs
            {
                RoutedEvent = Avalonia.Input.InputElement.TextInputMethodClientRequestedEvent,
            };
            area.RaiseEvent(request);
            var client = Assert.IsAssignableFrom<Avalonia.Input.TextInput.TextInputMethodClient>(request.Client);
            Assert.True(client.SupportsPreedit);
            Assert.Equal("name: ", client.SurroundingText);
            Assert.Equal(6, client.Selection.Start);
            Assert.Equal(6, client.Selection.End);
            var revision = document.Revision;

            client.SetPreeditText("にほん", 3);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("にほん", area.PreeditText);
            Assert.Equal("name: \nnext\n", document.Buffer.Text);
            Assert.Equal(revision, document.Revision);
            Assert.False(document.IsDirty);
            Assert.False(document.Buffer.UndoStack.CanUndo);
            var line = area.TextView.GetOrConstructVisualLine(document.Buffer.GetLineByNumber(1));
            Assert.Contains(line.Elements, e => e.GetType().Name == "PreeditElement");

            client.SetPreeditText(null);
            area.PerformTextInput("日本");
            Dispatcher.UIThread.RunJobs();
            Assert.Null(area.PreeditText);
            Assert.Equal("name: 日本\nnext\n", document.Buffer.Text);
            Assert.True(document.IsDirty);
            Assert.True(editor.Editor.Undo());
            Assert.Equal("name: \nnext\n", document.Buffer.Text);
            line = area.TextView.GetOrConstructVisualLine(document.Buffer.GetLineByNumber(1));
            Assert.DoesNotContain(line.Elements, e => e.GetType().Name == "PreeditElement");
        }
        finally { window.Close(); }
    }
}
