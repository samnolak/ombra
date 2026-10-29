using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Ombra.App.Services.Documents;

namespace Ombra.App.Controls;

public sealed partial class DocumentEditor
{
    private readonly ListBox _diagnostics = new() { Name = "EditorDiagnostics", MaxHeight = 150, IsVisible = false };
    private readonly StackPanel _renamePanel = new() { Orientation = Orientation.Horizontal, IsVisible = false, Spacing = 4, Margin = new Thickness(4) };
    private readonly TextBox _renameText = new() { Name = "EditorRename", PlaceholderText = "New symbol name", MinWidth = 180 };
    private readonly Flyout _hover = new();
    private readonly DiagnosticRenderer _diagnosticRenderer = new();
    private CompletionWindow? _completion;
    private CancellationTokenSource? _languageRequest;
    private WorkspaceDocument? _renameDocument;
    private long _renameRevision;
    private int _renameLine;
    private int _renameCharacter;

    private void BuildLanguageUi(Grid body)
    {
        Grid.SetRow(_diagnostics, 2);
        body.Children.Add(_diagnostics);
        _diagnostics.SelectionChanged += (_, _) =>
        {
            if (_diagnostics.SelectedItem is not DiagnosticRow diagnostic) return;
            Editor.TextArea.Caret.Offset = diagnostic.Offset;
            Editor.ScrollToLine(diagnostic.Line);
            Editor.Focus();
        };
        _renamePanel.Children.Add(_renameText);
        AddButton(_renamePanel, "Rename", async () => await RenameAsync());
        AddButton(_renamePanel, "Cancel", () => { _renamePanel.IsVisible = false; Editor.Focus(); });
        _renameText.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await RenameAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _renamePanel.IsVisible = false; Editor.Focus(); }
        };
        Grid.SetRow(_renamePanel, 3);
        body.Children.Add(_renamePanel);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
    }

    private void SubscribeLanguageService()
    {
        if (_subscribedService is not null)
        {
            _subscribedService.DiagnosticsChanged -= DiagnosticsChanged;
            _subscribedService.Changed -= LanguageServiceChanged;
        }
        _subscribedService = _attached ? LanguageService : null;
        if (_subscribedService is not null)
        {
            _subscribedService.DiagnosticsChanged += DiagnosticsChanged;
            _subscribedService.Changed += LanguageServiceChanged;
        }
        LoadDiagnostics();
        _ = RefreshCapabilitiesAsync();
    }

    private void LoadDiagnostics()
    {
        if (_attached && WorkspaceDocument is { } document && LanguageService is { } service && service.TryGetDiagnostics(document.Uri, out var diagnostics))
            SetDiagnostics(diagnostics);
        else SetDiagnostics(default);
    }

    private void DiagnosticsChanged(string uri, JsonElement diagnostics)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            var copy = diagnostics.Clone();
            Dispatcher.UIThread.Post(() => DiagnosticsChanged(uri, copy));
            return;
        }
        if (WorkspaceDocument?.Uri == uri) SetDiagnostics(diagnostics);
    }

    private void SetDiagnostics(JsonElement diagnostics)
    {
        var rows = new List<DiagnosticRow>();
        if (WorkspaceDocument is { } document && diagnostics.ValueKind == JsonValueKind.Array)
            foreach (var diagnostic in diagnostics.EnumerateArray())
            {
                if (!diagnostic.TryGetProperty("range", out var range) || !TryRange(document.Buffer, range, out var start, out var length)) continue;
                var message = StringProperty(diagnostic, "message") ?? "Diagnostic";
                var line = document.Buffer.GetLineByOffset(start).LineNumber;
                var severity = diagnostic.TryGetProperty("severity", out var severityValue) ? severityValue.GetInt32() : 1;
                rows.Add(new DiagnosticRow(start, length, line, severity, message));
            }
        _diagnostics.ItemsSource = rows;
        _diagnostics.IsVisible = rows.Count > 0;
        _diagnosticRenderer.Diagnostics = rows;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void CancelLanguageRequest()
    {
        _languageRequest?.Cancel();
        _languageRequest?.Dispose();
        _languageRequest = null;
    }

    private async Task RequestLanguageAsync(string method)
    {
        if (WorkspaceDocument is not { } document) return;
        if (LanguageService is not { } service) { _status.Text = "Language services are unavailable for this workspace."; return; }
        CancelLanguageRequest();
        var request = _languageRequest = new CancellationTokenSource();
        var revision = document.Revision;
        var line = Editor.TextArea.Caret.Line - 1;
        var character = Editor.TextArea.Caret.Column - 1;
        try
        {
            var result = await service.RequestAsync(document, method, line, character, ct: request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(document, WorkspaceDocument) || revision != document.Revision) return;
            if (result is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                _status.Text = $"No {method} result.";
                return;
            }
            switch (method)
            {
                case "completion": ShowCompletions(value, document, revision); break;
                case "hover": ShowHover(value); break;
                case "definition": ShowDefinitions(value); break;
                case "diagnostics":
                    SetDiagnostics(value.ValueKind == JsonValueKind.Object && value.TryGetProperty("items", out var items) ? items : value);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); }
    }

    private void ShowRename()
    {
        if (WorkspaceDocument is not { IsReadOnly: false } document) return;
        _renameDocument = document;
        _renameRevision = document.Revision;
        _renameLine = Editor.TextArea.Caret.Line - 1;
        _renameCharacter = Editor.TextArea.Caret.Column - 1;
        _renameText.Text = Editor.SelectedText;
        _renamePanel.IsVisible = true;
        _renameText.Focus();
        _renameText.SelectAll();
    }

    private async Task RenameAsync()
    {
        if (_renameDocument is not { } document || !ReferenceEquals(document, WorkspaceDocument)) return;
        if (document.Revision != _renameRevision) { _status.Text = "Document changed; start Rename again."; return; }
        if (string.IsNullOrWhiteSpace(_renameText.Text)) { _status.Text = "Enter a new symbol name."; return; }
        if (LanguageService is not { } service) { _status.Text = "Language services are unavailable for this workspace."; return; }
        CancelLanguageRequest();
        var request = _languageRequest = new CancellationTokenSource();
        try
        {
            // The runtime bridge validates and applies the entire workspace edit to canonical buffers.
            var result = await service.RequestAsync(document, "rename", _renameLine, _renameCharacter, _renameText.Text, request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(document, WorkspaceDocument)) return;
            _renamePanel.IsVisible = false;
            _status.Text = result is null || result.Value.ValueKind == JsonValueKind.Null ? "No rename result." : "Rename applied to workspace buffers; save to write changes.";
            Editor.Focus();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); }
    }

    private void ShowHover(JsonElement value)
    {
        var text = value.TryGetProperty("contents", out var contents) ? MarkupText(contents) : MarkupText(value);
        if (string.IsNullOrWhiteSpace(text)) { _status.Text = "No hover result."; return; }
        _hover.Content = new ScrollViewer
        {
            MaxHeight = 320, MaxWidth = 560,
            Content = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) }
        };
        _hover.ShowAt(Editor);
    }

    private void ShowDefinitions(JsonElement value)
    {
        var locations = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : new[] { value };
        var targets = new List<(string Path, int Line)>();
        foreach (var location in locations)
        {
            var uriText = StringProperty(location, "uri") ?? StringProperty(location, "targetUri");
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile) continue;
            if (!location.TryGetProperty("targetSelectionRange", out var range) && !location.TryGetProperty("range", out range)) continue;
            if (!range.TryGetProperty("start", out var start) || !start.TryGetProperty("line", out var line)) continue;
            targets.Add((uri.LocalPath, line.GetInt32() + 1));
        }
        if (targets.Count == 0) { _status.Text = "No file definition found."; return; }
        if (targets.Count == 1) { NavigateRequested?.Invoke(targets[0].Path, targets[0].Line); return; }
        var choices = new StackPanel { Spacing = 4 };
        foreach (var target in targets)
            AddButton(choices, $"{target.Path}:{target.Line}", () => { _hover.Hide(); NavigateRequested?.Invoke(target.Path, target.Line); });
        _hover.Content = new ScrollViewer { MaxHeight = 320, MaxWidth = 600, Content = choices };
        _hover.ShowAt(Editor);
    }

    private void ShowCompletions(JsonElement value, WorkspaceDocument document, long revision)
    {
        if (document.IsReadOnly) return;
        var items = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("items", out var list) ? list : value;
        if (items.ValueKind != JsonValueKind.Array) return;
        _completion?.Close();
        var window = new CompletionWindow(Editor.TextArea);
        var caret = Editor.CaretOffset;
        var start = caret;
        while (start > 0 && (char.IsLetterOrDigit(document.Buffer.GetCharAt(start - 1)) || document.Buffer.GetCharAt(start - 1) == '_')) start--;
        window.StartOffset = start;
        foreach (var item in items.EnumerateArray())
        {
            if (StringProperty(item, "label") is not { } label) continue;
            // Client capabilities deliberately advertise no snippet support.
            if (item.TryGetProperty("insertTextFormat", out var format) && format.GetInt32() == 2) continue;
            window.CompletionList.CompletionData.Add(new CompletionItem(item.Clone(), label, document, revision, ShowError));
        }
        if (window.CompletionList.CompletionData.Count == 0) { _status.Text = "No completions."; return; }
        _completion = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_completion, window)) _completion = null; };
        window.Show();
    }

    private static string? StringProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string MarkupText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Array => string.Join("\n\n", value.EnumerateArray().Select(MarkupText)),
        JsonValueKind.Object => StringProperty(value, "value") ?? StringProperty(value, "message") ?? "",
        _ => ""
    };

    internal static bool TryRange(TextDocument buffer, JsonElement range, out int offset, out int length)
    {
        offset = length = 0;
        if (!range.TryGetProperty("start", out var start) || !range.TryGetProperty("end", out var end) ||
            !TryPosition(buffer, start, out offset) || !TryPosition(buffer, end, out var last) || last < offset) return false;
        length = last - offset;
        return true;
    }

    private static bool TryPosition(TextDocument buffer, JsonElement position, out int offset)
    {
        offset = 0;
        if (!position.TryGetProperty("line", out var lineProperty) || !position.TryGetProperty("character", out var characterProperty) ||
            !lineProperty.TryGetInt32(out var line) || !characterProperty.TryGetInt32(out var character) || line < 0 || line >= buffer.LineCount) return false;
        var documentLine = buffer.GetLineByNumber(line + 1);
        if (character < 0 || character > documentLine.Length) return false;
        offset = documentLine.Offset + character;
        return true;
    }

    private sealed class CompletionItem(JsonElement item, string label, WorkspaceDocument document, long revision, Action<Exception> showError) : ICompletionData
    {
        public IImage? Image => null;
        public string Text => label;
        public object Content => label;
        public object Description => StringProperty(item, "detail") ?? (item.TryGetProperty("documentation", out var documentation) ? MarkupText(documentation) : "");
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            if (document.IsReadOnly || document.HasConflict || !ReferenceEquals(textArea.Document, document.Buffer) || document.Revision != revision)
            {
                showError(new InvalidOperationException("Document changed; request completion again."));
                return;
            }
            var edits = new List<(int Offset, int Length, string Text)>();
            var text = StringProperty(item, "insertText") ?? label;
            if (item.TryGetProperty("textEdit", out var edit))
            {
                if (!edit.TryGetProperty("range", out var range) && !edit.TryGetProperty("replace", out range)) return;
                if (!TryRange(document.Buffer, range, out var offset, out var length)) return;
                edits.Add((offset, length, StringProperty(edit, "newText") ?? text));
            }
            else edits.Add((completionSegment.Offset, completionSegment.Length, text));
            if (item.TryGetProperty("additionalTextEdits", out var additional) && additional.ValueKind == JsonValueKind.Array)
                foreach (var extra in additional.EnumerateArray())
                {
                    if (!extra.TryGetProperty("range", out var range) || !TryRange(document.Buffer, range, out var offset, out var length)) return;
                    edits.Add((offset, length, StringProperty(extra, "newText") ?? ""));
                }
            edits.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            for (var i = 1; i < edits.Count; i++)
                if (edits[i - 1].Offset + edits[i - 1].Length > edits[i].Offset) return;
            textArea.ExecuteEdit(() =>
            {
                using (document.Buffer.RunUpdate())
                    for (var i = edits.Count - 1; i >= 0; i--) document.Buffer.Replace(edits[i].Offset, edits[i].Length, edits[i].Text);
            });
        }
    }

    private sealed record DiagnosticRow(int Offset, int Length, int Line, int Severity, string Message)
    {
        public override string ToString() => $"{Line}: {(Severity == 1 ? "Error" : Severity == 2 ? "Warning" : "Info")}: {Message}";
    }

    private sealed class DiagnosticRenderer : IBackgroundRenderer
    {
        public IReadOnlyList<DiagnosticRow> Diagnostics { get; set; } = Array.Empty<DiagnosticRow>();
        public KnownLayer Layer => KnownLayer.Selection;
        private static readonly Pen ErrorPen = new(Brushes.IndianRed, 1.5);
        private static readonly Pen WarningPen = new(Brushes.DarkGoldenrod, 1.5);
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid) return;
            foreach (var diagnostic in Diagnostics)
            {
                var segment = new SimpleSegment(diagnostic.Offset, diagnostic.Length);
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                    drawingContext.DrawLine(diagnostic.Severity == 1 ? ErrorPen : WarningPen, rect.BottomLeft, rect.BottomRight);
            }
        }
    }
}
