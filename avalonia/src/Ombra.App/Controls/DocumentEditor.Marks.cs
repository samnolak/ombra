using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Ombra.App.Localization;

namespace Ombra.App.Controls;

/// <summary>
/// A comment gesture in the editor: the selected lines (or the caret's line), their text, two lines around them, the
/// document's version and whether it is unsaved, and the short note the user wrote.
/// </summary>
public sealed record EditorMarkRequest(string DocumentUri, long Revision, bool Unsaved, string BufferSha256, int FirstLine, int LastLine,
    string Text, string[] Before, string[] After, string Note);

public sealed partial class DocumentEditor
{
    /// <summary>The user left a comment on lines of the document (the Files pane makes it a local mark).</summary>
    public event Action<EditorMarkRequest>? MarkRequested;

    /// <summary>Opens the comment box at the selection (the editor's context menu, "Comment…").</summary>
    internal void BeginMark()
    {
        if (WorkspaceDocument is not { } document) return;
        var buffer = document.Buffer;
        var start = Editor.TextArea.Selection.IsEmpty ? Editor.CaretOffset : Editor.SelectionStart;
        var end = Editor.TextArea.Selection.IsEmpty ? Editor.CaretOffset : Editor.SelectionStart + Editor.SelectionLength;
        var first = buffer.GetLineByOffset(start).LineNumber;
        var lastLine = buffer.GetLineByOffset(end);
        // A selection that ends at the start of a line does not take that line
        var last = end > start && lastLine.Offset == end && lastLine.LineNumber > first ? lastLine.LineNumber - 1 : lastLine.LineNumber;
        string LineText(int n) => buffer.GetText(buffer.GetLineByNumber(n));
        var text = string.Join("\n", Enumerable.Range(first, last - first + 1).Select(LineText));
        string[] before = [.. Enumerable.Range(Math.Max(1, first - 2), first - Math.Max(1, first - 2)).Select(LineText)];
        string[] after = [.. Enumerable.Range(last + 1, Math.Max(0, Math.Min(buffer.LineCount, last + 2) - last)).Select(LineText)];
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(buffer.Text))).ToLowerInvariant();

        var box = new TextBox
        {
            Name = "EditorMarkNote", Width = 320, MinHeight = 64, MaxHeight = 160, AcceptsReturn = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            PlaceholderText = Loc.T("Comment on these lines"),
        };
        Avalonia.Automation.AutomationProperties.SetName(box, Loc.T("Comment"));
        var where = new TextBlock
        {
            Classes = { "tertiary" }, FontSize = 11,
            Text = last > first ? Loc.F("lines {0}-{1}", first, last) : Loc.F("line {0}", first),
        };
        var save = new Button { Classes = { "accent" }, Content = Loc.T("Comment"), HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Pointer,
            Content = new StackPanel { Spacing = 8, Margin = new Thickness(4), Children = { where, box, save } },
        };
        void Submit()
        {
            var note = box.Text?.Trim() ?? "";
            if (note.Length == 0) return;
            flyout.Hide();
            MarkRequested?.Invoke(new EditorMarkRequest(document.Uri, document.Revision, document.IsDirty, sha, first, last, text, before, after, note));
        }
        save.Click += (_, _) => Submit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; Submit(); }
            else if (e.Key == Key.Escape) { e.Handled = true; flyout.Hide(); }
        };
        flyout.Opened += (_, _) => box.Focus();
        flyout.ShowAt(Editor, showAtPointer: true);
    }
}
