using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Ombra.App.Localization;
using Ombra.App.Services;

namespace Ombra.App.Controls;

/// <summary>Go to line (⌃G) and the git change gutter.</summary>
public sealed partial class DocumentEditor
{
    public static readonly StyledProperty<IReadOnlyList<LineChange>?> LineChangesProperty =
        AvaloniaProperty.Register<DocumentEditor, IReadOnlyList<LineChange>?>(nameof(LineChanges));

    /// <summary>The git change gutter's marks (null or empty: no gutter marks).</summary>
    public IReadOnlyList<LineChange>? LineChanges { get => GetValue(LineChangesProperty); set => SetValue(LineChangesProperty, value); }

    internal GitChangeMargin ChangeMargin { get; } = new() { Name = "EditorChangeMargin" };

    internal TextBox GoToLineBox { get; } = new() { Name = "EditorGoToLine", Width = 140, IsVisible = false, Margin = new Thickness(1) };

    private void BuildNavigationUi(Panel toolbar)
    {
        Editor.TextArea.LeftMargins.Add(ChangeMargin);
        ToolTip.SetTip(GoToLineBox, Loc.T("Line number, or line:column. Enter goes there, Esc closes."));
        GoToLineBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (GoToLine(GoToLineBox.Text)) CloseGoToLine();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseGoToLine();
            }
        };
        GoToLineBox.LostFocus += (_, _) => GoToLineBox.IsVisible = false;
        toolbar.Children.Add(GoToLineBox);
    }

    internal void ShowGoToLine()
    {
        GoToLineBox.Text = "";
        GoToLineBox.PlaceholderText = Loc.F("Go to line (1–{0})", Math.Max(1, Editor.Document.LineCount));
        GoToLineBox.IsVisible = true;
        GoToLineBox.Focus();
    }

    private void CloseGoToLine()
    {
        GoToLineBox.IsVisible = false;
        Editor.TextArea.Focus();
    }

    /// <summary>"42" or "42:7": the caret goes there (clamped to the file) and the line scrolls into view.</summary>
    internal bool GoToLine(string? text)
    {
        if (WorkspaceDocument is not { } document || string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':', 2);
        if (!int.TryParse(parts[0], out var line)) return false;
        var column = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 1;
        line = Math.Clamp(line, 1, document.Buffer.LineCount);
        var length = document.Buffer.GetLineByNumber(line).Length;
        Editor.TextArea.Caret.Line = line;
        Editor.TextArea.Caret.Column = Math.Clamp(column, 1, length + 1);
        if (_folding is not null)
            foreach (var fold in _folding.AllFoldings)
                if (fold.StartOffset < Editor.CaretOffset && Editor.CaretOffset < fold.EndOffset) fold.IsFolded = false;
        _pendingTargetLine = line;
        CompleteTargetNavigation();
        return true;
    }
}
