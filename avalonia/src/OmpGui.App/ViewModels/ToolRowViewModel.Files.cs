namespace OmpGui.App.ViewModels;

/// <summary>A tool row's file, which the conversation lets the user open in the Files pane (Files area).</summary>
public sealed partial class ToolRowViewModel
{
    /// <summary>Tools whose summary is the one file they read or write.</summary>
    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal) { "read", "edit", "write", "create", "write_file", "ast_edit", "notebook_edit" };

    /// <summary>The row names a file (read, edit, write, or any change with a diff): its path opens the file.</summary>
    public bool HasFile =>
        (HasDiff || FileTools.Contains(Name)) && Summary.Length > 0 && !Summary.StartsWith("$ ", StringComparison.Ordinal)
        && !Summary.Contains("://", StringComparison.Ordinal) && (Summary.Contains('/') || Summary.Contains('\\') || Summary.Contains('.'));

    /// <summary>Where the change starts in the file (its first added line), when the row has a diff.</summary>
    public int? FirstChangedLine => OmpGui.App.Controls.DiffView.FirstChangedLine(Diff);

    partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(HasFile));

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(HasFile));

    partial void OnDiffChanged(string? value)
    {
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(FirstChangedLine));
    }
}
