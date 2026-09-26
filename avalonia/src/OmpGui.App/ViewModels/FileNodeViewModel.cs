using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using OmpGui.App.Services;

namespace OmpGui.App.ViewModels;

/// <summary>
/// A file or folder in the Files pane: a row of the project tree (children read when it is first opened) or of a flat
/// list (search results, changed files), with git's mark and whether omp changed it in this session.
/// </summary>
public sealed partial class FileNodeViewModel : ObservableObject
{
    private FileNodeViewModel(string name, string fullPath, string relativePath, bool isDirectory, int depth, FileNodeViewModel? parent,
        bool isDeleted, string? detail)
    {
        Name = name;
        FullPath = fullPath;
        RelativePath = relativePath;
        IsDirectory = isDirectory;
        Depth = depth;
        Parent = parent;
        IsDeleted = isDeleted;
        Detail = detail;
        IsHeavy = isDirectory && ProjectFiles.Heavy.Contains(name);
    }

    /// <summary>The project folder itself: the tree's invisible top (its children are the first rows).</summary>
    internal static FileNodeViewModel Root(string fullPath) =>
        new(Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath)), fullPath, "", true, -1, null, false, null) { IsExpanded = true };

    internal static FileNodeViewModel Child(FileNodeViewModel parent, FileEntry e) =>
        new(e.Name, e.FullPath, parent.RelativePath.Length == 0 ? e.Name : parent.RelativePath + "/" + e.Name, e.IsDirectory, parent.Depth + 1, parent, e.IsDeleted, null);

    /// <summary>A row of a flat list: the name, and the folder it is in beside it.</summary>
    internal static FileNodeViewModel Result(string root, string relativePath, bool isDirectory = false)
    {
        var rel = relativePath.TrimEnd('/');
        var slash = rel.LastIndexOf('/');
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        return new(slash < 0 ? rel : rel[(slash + 1)..], full, rel, isDirectory, 0, null, !isDirectory && !File.Exists(full), slash < 0 ? "" : rel[..slash]);
    }

    /// <summary>A flat-list row for a file outside the project (the agent may change one): its full path as detail.</summary>
    internal static FileNodeViewModel Outside(string fullPath) =>
        new(Path.GetFileName(fullPath), fullPath, fullPath, false, 0, null, !File.Exists(fullPath), Path.GetDirectoryName(fullPath));

    public string Name { get; }
    public string FullPath { get; }
    /// <summary>Relative to the project, '/'-separated ("" for the project itself).</summary>
    public string RelativePath { get; }
    public bool IsDirectory { get; }
    public bool IsFile => !IsDirectory;
    public int Depth { get; }
    public FileNodeViewModel? Parent { get; }
    /// <summary>Deleted, but still in git's last commit: shown struck through.</summary>
    public bool IsDeleted { get; }
    /// <summary>Dependencies or build output (node_modules, bin, obj, .venv, dist…): dimmed, closed until asked.</summary>
    public bool IsHeavy { get; }
    /// <summary>In a flat list: the folder the file is in.</summary>
    public string? Detail { get; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>The folder's entries; null until it is first opened.</summary>
    internal List<FileNodeViewModel>? Children { get; set; }

    /// <summary>Rows step in by 16 px a level (the chevron's width), as in editors.</summary>
    public Thickness Indent => new(Math.Max(0, Depth) * 16, 0, 0, 0);

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitLetter), nameof(HasGitMark), nameof(IsModified), nameof(IsAdded), nameof(IsRemoved), nameof(IsConflicted), nameof(Tip), nameof(ShowFolderDot))]
    private GitMark _git;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDimmed), nameof(Tip))]
    private bool _isIgnored;

    /// <summary>Inside a dimmed folder (node_modules/…): dimmed too.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDimmed))]
    private bool _insideDimmed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFolderDot))]
    private bool _hasChangesInside;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tip))]
    private bool _isSessionChanged;

    public bool IsDimmed => IsHeavy || IsIgnored || InsideDimmed;
    public string GitLetter => GitSnapshot.Letter(Git);
    public bool HasGitMark => GitLetter.Length > 0;
    public bool IsModified => Git is GitMark.Modified or GitMark.Renamed;
    public bool IsAdded => Git is GitMark.Added or GitMark.Untracked;
    public bool IsRemoved => Git == GitMark.Deleted;
    public bool IsConflicted => Git == GitMark.Conflicted;
    /// <summary>A closed folder with changes somewhere inside gets a dot.</summary>
    public bool ShowFolderDot => IsDirectory && HasChangesInside && !HasGitMark;

    public string Tip
    {
        get
        {
            var parts = new List<string> { RelativePath.Length > 0 ? RelativePath : FullPath };
            if (GitSnapshot.Describe(IsIgnored && Git == GitMark.None ? GitMark.Ignored : Git) is { Length: > 0 } g) parts.Add(g);
            if (IsSessionChanged) parts.Add("Changed by omp in this session");
            if (IsHeavy && !IsIgnored) parts.Add("Dependencies or build output");
            return string.Join("\n", parts);
        }
    }
}
