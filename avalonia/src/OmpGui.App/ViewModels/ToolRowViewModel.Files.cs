using OmpGui.App.Controls;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// A tool row's files: what the call changed (one card each under the row) and the file the conversation lets the user
/// open in the Files pane (Files area).
/// </summary>
public sealed partial class ToolRowViewModel
{
    /// <summary>Tools whose summary is the one file they read or write.</summary>
    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal) { "read", "edit", "write", "create", "write_file", "ast_edit", "notebook_edit" };

    /// <summary>The files the call changed, one per file (a patch may change several); empty when none or unknown.</summary>
    public IReadOnlyList<FileChange> Changes { get; private set; } = [];

    /// <summary>What the call did to its file when it changed exactly one; null otherwise.</summary>
    public FileChangeKind? ChangeKind => Changes.Count == 1 ? Changes[0].Kind : null;

    /// <summary>The old path of the one file the call moved; null otherwise.</summary>
    public string? MovedFrom => Changes.Count == 1 ? Changes[0].MovedFrom : null;

    /// <summary>
    /// The change cards under the row: one per changed file; a diff omp gave without saying which files (an older omp,
    /// another agent's tool) is one card for the row's file.
    /// </summary>
    public IReadOnlyList<FileChange> DiffCards =>
        Changes.Count > 0 ? Changes : HasDiff ? [new FileChange(Summary, FileChangeKind.Modified, Diff)] : [];

    /// <summary>The file the row opens: the first file it changed, else the one file its summary names.</summary>
    public string FilePath => Changes.Count > 0 ? Changes[0].Path : Summary;

    /// <summary>The row names a file (read, edit, write, or any change with a diff): its path opens the file.</summary>
    public bool HasFile =>
        Changes.Count > 0
        || ((HasDiff || FileTools.Contains(Name)) && Summary.Length > 0 && !Summary.StartsWith("$ ", StringComparison.Ordinal)
            && !Summary.Contains("://", StringComparison.Ordinal) && (Summary.Contains('/') || Summary.Contains('\\') || Summary.Contains('.')));

    /// <summary>Where the change starts in the row's file (its first added line), when the row has a diff.</summary>
    public int? FirstChangedLine => DiffView.FirstChangedLine(Changes.Count > 0 ? Changes[0].Diff : Diff);

    /// <summary>Takes the files from the model (each update of the row).</summary>
    partial void UpdateFiles()
    {
        var changes = ((ToolItem)Model).Changes ?? [];
        if (ReferenceEquals(changes, Changes) || (changes.Count == 0 && Changes.Count == 0)) return;
        Changes = changes;
        OnPropertyChanged(nameof(Changes));
        OnPropertyChanged(nameof(ChangeKind));
        OnPropertyChanged(nameof(MovedFrom));
        OnPropertyChanged(nameof(DiffCards));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(FirstChangedLine));
    }

    partial void OnSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(DiffCards));
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(HasFile));

    partial void OnDiffChanged(string? value)
    {
        OnPropertyChanged(nameof(HasFile));
        OnPropertyChanged(nameof(FirstChangedLine));
        OnPropertyChanged(nameof(DiffCards));
    }
}
