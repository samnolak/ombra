using Ombra.App.ViewModels;

namespace Ombra.App.Controls;

/// <summary>The selected lines of a document, whole (the caret's line without a selection).</summary>
public sealed record EditorLines(string FullPath, int FirstLine, int LastLine, string Text, string? Language);

public sealed partial class DocumentEditor
{
    /// <summary>
    /// The lines the selection touches, whole; a selection that ends at the start of a line does not take that line.
    /// Null without a document.
    /// </summary>
    internal EditorLines? SelectedLines()
    {
        if (WorkspaceDocument is not { } document) return null;
        var buffer = document.Buffer;
        var start = Editor.TextArea.Selection.IsEmpty ? Editor.CaretOffset : Editor.SelectionStart;
        var end = Editor.TextArea.Selection.IsEmpty ? Editor.CaretOffset : Editor.SelectionStart + Editor.SelectionLength;
        var first = buffer.GetLineByOffset(start).LineNumber;
        var lastLine = buffer.GetLineByOffset(end);
        var last = end > start && lastLine.Offset == end && lastLine.LineNumber > first ? lastLine.LineNumber - 1 : lastLine.LineNumber;
        var text = string.Join("\n", Enumerable.Range(first, last - first + 1).Select(n => buffer.GetText(buffer.GetLineByNumber(n))));
        return new EditorLines(document.FullPath, first, last, text, document.LanguageId);
    }

    /// <summary>"Share with team…" (the context menu, the Edit list): the selected lines go to the Share dialog as a snippet.</summary>
    internal void BeginShare()
    {
        if (SelectedLines() is { } lines && ConversationMenus.Owner(this) is MainViewModel owner)
            owner.ShareSnippet(lines.FullPath, lines.FirstLine, lines.LastLine, lines.Text, lines.Language);
    }
}
