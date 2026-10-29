using System.Text.Json;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Ombra.App.Localization;
using Ombra.App.Services.Documents;
using Ombra.Rpc;

namespace Ombra.App.Controls;

/// <summary>
/// Format document (⇧⌥F) and format selection over the language server's <c>textDocument/formatting</c> and
/// <c>rangeFormatting</c>. The server's edits land in the workspace buffer as one undo step; while the server cannot
/// format (untrusted project, no server, no such capability) the commands are disabled and say why.
/// </summary>
public sealed partial class DocumentEditor
{
    private EditorCapabilities? _capabilities;
    private CancellationTokenSource? _capabilitiesRequest;

    /// <summary>What the current file's language server can do; null while unknown or without language services.</summary>
    internal EditorCapabilities? Capabilities => _capabilities;

    /// <summary>The caret moved (1-based line, 0-based UTF-16 character), for the page's breadcrumbs.</summary>
    public event Action<int, int>? CaretMoved;

    private void BuildFormatUi()
    {
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
            CaretMoved?.Invoke(Editor.TextArea.Caret.Line, Editor.TextArea.Caret.Column - 1);
        // Autosave "on focus change" (EditorAutosave)
        Editor.TextArea.LostFocus += (_, _) => WorkspaceDocument?.NoteEditorFocusLeft();
    }

    /// <summary>Asks (once per language, cached by the service) what the server can do; again after trust or the file changes.</summary>
    internal async Task RefreshCapabilitiesAsync()
    {
        _capabilitiesRequest?.Cancel();
        _capabilities = null;
        if (!_attached || WorkspaceDocument is not { } document || LanguageService is not { IsTrusted: true } service) return;
        var request = _capabilitiesRequest = new CancellationTokenSource();
        try
        {
            var found = await service.CapabilitiesAsync(document, request.Token);
            if (!request.IsCancellationRequested && ReferenceEquals(document, WorkspaceDocument)) _capabilities = found;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or RpcConnectionClosedException or TimeoutException) { }
    }

    private void LanguageServiceChanged() => Dispatcher.UIThread.Post(() => _ = RefreshCapabilitiesAsync());

    /// <summary>Why format document (or selection) can't run now; null when it can.</summary>
    internal string? FormatUnavailableReason(bool selection)
    {
        if (WorkspaceDocument is not { } document) return Loc.T("No file open");
        if (document.IsReadOnly) return Loc.T("The file is read-only");
        if (document.HasConflict) return Loc.T("Resolve the conflict with the file on disk first");
        if (LanguageService is not { IsTrusted: true }) return Loc.T("Turn on language services for this project to format");
        if (_capabilities is not { } caps) return Loc.T("The language server isn't ready or isn't configured for this file");
        if (selection && Editor.SelectionLength == 0) return Loc.T("Select the lines to format");
        if (selection ? !caps.RangeFormatting : !caps.Formatting)
            return selection ? Loc.T("The language server can't format a selection") : Loc.T("The language server can't format this file");
        return null;
    }

    /// <summary>
    /// Formats the document (or the selection) with the server's edits, as one undo step. False when nothing was applied
    /// (unavailable, the document changed meanwhile, or the edits were invalid); the status line says why.
    /// </summary>
    internal async Task<bool> FormatAsync(bool selection)
    {
        if (FormatUnavailableReason(selection) is { } reason) { _status.Text = reason; return false; }
        var document = WorkspaceDocument!;
        var service = LanguageService!;
        CancelLanguageRequest();
        var request = _languageRequest = new CancellationTokenSource();
        var revision = document.Revision;
        var options = new EditorRequestOptions(TabSize: Editor.Options.IndentationSize, InsertSpaces: Editor.Options.ConvertTabsToSpaces);
        int line = 0, character = 0;
        if (selection)
        {
            var start = document.Buffer.GetLocation(Editor.SelectionStart);
            var end = document.Buffer.GetLocation(Editor.SelectionStart + Editor.SelectionLength);
            (line, character) = (start.Line - 1, start.Column - 1);
            options = options with { EndLine = end.Line - 1, EndCharacter = end.Column - 1 };
        }
        try
        {
            var result = await service.RequestAsync(document, selection ? "rangeFormatting" : "formatting", line, character, ct: request.Token, options: options);
            if (request.IsCancellationRequested || !ReferenceEquals(document, WorkspaceDocument)) return false;
            if (document.Revision != revision) { _status.Text = Loc.T("The file changed while formatting; format again."); return false; }
            if (result is not { ValueKind: JsonValueKind.Array } edits || edits.GetArrayLength() == 0)
            {
                _status.Text = Loc.T("Already formatted.");
                return false;
            }
            if (!ApplyTextEdits(document.Buffer, edits))
            {
                _status.Text = Loc.T("The language server's edits don't fit the file; nothing changed.");
                return false;
            }
            _status.Text = Loc.T("Formatted.");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { ShowError(error); return false; }
    }

    /// <summary>
    /// LSP <c>TextEdit[]</c> applied to <paramref name="buffer"/> as one undo step, last first so offsets hold. All or
    /// nothing: a range outside the text or two overlapping edits leave the buffer as it was.
    /// </summary>
    internal static bool ApplyTextEdits(TextDocument buffer, JsonElement edits)
    {
        var list = new List<(int Offset, int Length, string Text)>();
        foreach (var edit in edits.EnumerateArray())
        {
            if (!edit.TryGetProperty("range", out var range) || !TryRange(buffer, range, out var offset, out var length)) return false;
            list.Add((offset, length, StringProperty(edit, "newText") ?? ""));
        }
        // Stable: edits at one position keep the server's order (LSP inserts them in array order)
        list = [.. list.Select((e, i) => (e, i)).OrderBy(x => x.e.Offset).ThenBy(x => x.i).Select(x => x.e)];
        for (var i = 1; i < list.Count; i++)
            if (list[i - 1].Offset + list[i - 1].Length > list[i].Offset) return false;
        buffer.BeginUpdate();
        try
        {
            for (var i = list.Count - 1; i >= 0; i--) buffer.Replace(list[i].Offset, list[i].Length, list[i].Text);
        }
        finally { buffer.EndUpdate(); }
        return true;
    }
}
