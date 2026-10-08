using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.App.Controls;
using OmpGui.App.Services;

namespace OmpGui.App.ViewModels;

public enum DiffRowKind { Hunk, Context, Added, Removed }

/// <summary>
/// A row of the viewer's Changes: a hunk's header (with Revert) or one line, which takes a comment for omp (the "+" at
/// its start, as in Codex's review pane).
/// </summary>
public sealed partial class DiffRowViewModel(FileViewerViewModel owner, DiffRowKind kind, string text, int? number = null, DiffHunk? hunk = null,
    bool startsInBlock = false) : ObservableObject
{
    public FileViewerViewModel Owner { get; } = owner;
    public DiffRowKind Kind { get; } = kind;
    /// <summary>The line (a hunk row: git's "@@ -12,7 +12,9 @@" with the code around it).</summary>
    public string Text { get; } = text;
    /// <summary>Its number in the file now; a removed line's in the last commit.</summary>
    public int? Number { get; } = number;
    public DiffHunk? Hunk { get; } = hunk;
    /// <summary>The line begins inside a block comment or string that started above (colouring, SyntaxHighlighter).</summary>
    public bool StartsInBlock { get; } = startsInBlock;

    public bool IsHunk => Kind == DiffRowKind.Hunk;
    public bool IsLine => Kind != DiffRowKind.Hunk;
    public bool IsAdded => Kind == DiffRowKind.Added;
    public bool IsRemoved => Kind == DiffRowKind.Removed;
    public string NumberText => Number?.ToString(CultureInfo.InvariantCulture) ?? "";
    public string Sign => Kind switch { DiffRowKind.Added => "+", DiffRowKind.Removed => "−", _ => "" };
    public string CommentTip => Number is { } n ? $"Comment on line {n}" : "Comment on this line";

    [ObservableProperty] private bool _isCommenting;
    [ObservableProperty] private string _commentText = "";

    [RelayCommand] private void Comment() => Owner.BeginComment(this);
    [RelayCommand] private void CancelComment() => Owner.EndComment(this);
    [RelayCommand] private void SubmitComment() => Owner.SubmitComment(this);
}

/// <summary>
/// The viewer's Changes (a file git sees changed): what changed since the last commit, hunk by hunk, as Codex's and
/// Claude Code's review panes show it. A hunk reverts on its own; the whole file reverts after a question; a line
/// takes a comment that goes with the next message as a chip (like comments on the previewed page).
/// </summary>
public sealed partial class FileViewerViewModel
{
    /// <summary>Rows shown at most; beyond, the notice says the rest is left out.</summary>
    public const int MaxDiffRows = 3000;

    private CancellationTokenSource? _diffCts;
    private FileDiff? _diff;
    private DiffRowViewModel? _commenting;

    /// <summary>git's mark for the file (None: unchanged, or not in a repository).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges), nameof(RevertQuestion), nameof(RevertLabel), nameof(ConfirmRevertLabel))]
    private GitMark _gitMark;

    /// <summary>A staged rename: the file's name in the last commit (from the top folder), where Revert file puts it back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RevertQuestion))]
    private string? _renamedFrom;

    /// <summary>The changes are shown instead of the file.</summary>
    [ObservableProperty] private bool _showChanges;

    /// <summary>"Reading the changes…", "No changes since the last commit.", or why they can't be shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffMessage))]
    private string _diffMessage = "";

    /// <summary>"+12 −3".</summary>
    [ObservableProperty] private string _diffStats = "";

    [ObservableProperty] private bool _isConfirmingRevert;
    [ObservableProperty] private bool _isReverting;

    /// <summary>The first sync with git's status has happened (the Changes view is chosen then, for a changed file).</summary>
    internal bool GitSynced { get; set; }

    public ObservableCollection<DiffRowViewModel> DiffRows { get; } = [];

    public bool HasChanges => GitMark is not (GitMark.None or GitMark.Ignored);
    public bool HasDiffMessage => DiffMessage.Length > 0;
    /// <summary>A new file's one hunk is the whole file: taking it out would delete the file without asking, so only Revert file applies.</summary>
    public bool CanRevertHunks => _diff is { IsBinary: false, IsNewFile: false, TooLarge: false };
    public string RevertLabel => GitMark == GitMark.Untracked ? "Delete file…" : "Revert file…";
    public string ConfirmRevertLabel => GitMark == GitMark.Untracked ? "Delete" : "Revert";

    public string RevertQuestion => GitMark switch
    {
        GitMark.Untracked => $"Delete {Name}? git does not have it, so it can't be brought back.",
        GitMark.Added => $"Discard {Name}? It was added since the last commit, so it is deleted. This can't be undone.",
        GitMark.Renamed when RenamedFrom is { } from =>
            $"Discard every change to {Name} since the last commit? It goes back to {from}. This can't be undone.",
        _ => $"Discard every change to {Name} since the last commit? This can't be undone.",
    };

    partial void OnShowChangesChanged(bool value)
    {
        IsConfirmingRevert = false;
        if (value) _ = LoadDiffAsync();
    }

    [RelayCommand] private void ShowFile() => ShowChanges = false;
    [RelayCommand] private void ShowDiff() => ShowChanges = true;

    /// <summary>Reads the diff again (off the UI thread); the rows stay when git says the same.</summary>
    internal async Task LoadDiffAsync()
    {
        _diffCts?.Cancel();
        var cts = _diffCts = new CancellationTokenSource();
        if (Owner.GitTop is not { } top || ProjectFiles.Relative(top, FullPath) is not { Length: > 0 } rel)
        {
            Clear("This file is not in a git repository.");
            return;
        }
        if (DiffRows.Count == 0) DiffMessage = "Reading the changes…";
        FileDiff? diff;
        try { diff = await GitReview.ReadAsync(top, rel, GitMark == GitMark.Untracked, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        if (diff is null)
        {
            Clear("git could not compare this file with the last commit.");
            return;
        }
        if (_diff is { } shown && shown.Text == diff.Text && shown.TooLarge == diff.TooLarge) return;
        _diff = diff;
        OnPropertyChanged(nameof(CanRevertHunks));
        BuildRows(diff);
    }

    private void Clear(string message)
    {
        _diff = null;
        _commenting = null;
        DiffRows.Clear();
        DiffStats = "";
        DiffMessage = message;
        OnPropertyChanged(nameof(CanRevertHunks));
    }

    private void BuildRows(FileDiff diff)
    {
        // omp may change the file while a comment is being written: the comment stays on the same line if it is still there
        var writing = _commenting;
        _commenting = null;
        DiffRows.Clear();
        var lang = SyntaxHighlighter.ForName(Name);
        int added = 0, removed = 0;
        var cut = false;
        foreach (var hunk in diff.Hunks)
        {
            // Past the cap every hunk is still counted for the totals, just not shown
            if (DiffRows.Count >= MaxDiffRows) cut = true;
            else DiffRows.Add(new DiffRowViewModel(this, DiffRowKind.Hunk, hunk.Header.TrimEnd('\r'), hunk: hunk));
            int oldNo = hunk.OldStart, newNo = hunk.NewStart;
            var inBlock = false;
            foreach (var raw in hunk.Lines)
            {
                if (raw.Length == 0 || raw[0] == '\\') continue; // "\ No newline at end of file"
                var kind = raw[0] switch { '+' => DiffRowKind.Added, '-' => DiffRowKind.Removed, _ => DiffRowKind.Context };
                var number = kind == DiffRowKind.Removed ? oldNo++ : newNo++;
                if (kind == DiffRowKind.Context) oldNo++;
                if (kind == DiffRowKind.Added) added++;
                if (kind == DiffRowKind.Removed) removed++;
                if (DiffRows.Count >= MaxDiffRows)
                {
                    cut = true;
                    continue;
                }
                var text = raw[1..].TrimEnd('\r').Replace("\t", "    ");
                var starts = inBlock;
                SyntaxHighlighter.Tokenize(text, lang, ref inBlock);
                DiffRows.Add(new DiffRowViewModel(this, kind, text, number, hunk, starts));
            }
        }
        DiffStats = string.Join(" ", new[] { added > 0 ? $"+{added}" : "", removed > 0 ? $"−{removed}" : "" }.Where(s => s.Length > 0));
        DiffMessage = diff.TooLarge ? "These changes are too large to show here."
            : diff.IsBinary ? "A binary file: its changes can't be shown here."
            : diff.Hunks.Count == 0 ? "No changes since the last commit."
            : cut ? string.Create(CultureInfo.InvariantCulture, $"Showing the first {MaxDiffRows:N0} lines of the changes.")
            : "";
        if (writing is not null && DiffRows.Where(r => r.Kind == writing.Kind && r.Text == writing.Text)
                .MinBy(r => Math.Abs((r.Number ?? 0) - (writing.Number ?? 0))) is { } same)
        {
            same.CommentText = writing.CommentText;
            BeginComment(same);
        }
    }

    [RelayCommand]
    private async Task RevertHunkAsync(DiffRowViewModel? row)
    {
        if (row?.Hunk is not { } hunk || _diff is not { } diff || !CanRevertHunks || IsReverting) return;
        IsReverting = true;
        try { await Owner.RevertHunkAsync(this, diff, hunk); }
        finally { IsReverting = false; }
    }

    [RelayCommand] private void AskRevertFile() => IsConfirmingRevert = true;
    [RelayCommand] private void CancelRevertFile() => IsConfirmingRevert = false;

    [RelayCommand]
    private async Task ConfirmRevertFileAsync()
    {
        if (IsReverting) return;
        IsReverting = true;
        try { await Owner.RevertFileAsync(this); }
        finally
        {
            IsReverting = false;
            IsConfirmingRevert = false;
        }
    }

    // ── Comments on lines ──

    internal void BeginComment(DiffRowViewModel row)
    {
        if (_commenting is { } other && !ReferenceEquals(other, row)) other.IsCommenting = false;
        _commenting = row;
        row.IsCommenting = true;
    }

    internal void EndComment(DiffRowViewModel row)
    {
        row.IsCommenting = false;
        row.CommentText = "";
        if (ReferenceEquals(_commenting, row)) _commenting = null;
    }

    internal void SubmitComment(DiffRowViewModel row)
    {
        var comment = row.CommentText.Trim();
        if (comment.Length == 0) return;
        if (!Owner.Owner.AddCodeComment(RelativePath.Replace('\\', '/'), row.Number, row.Kind == DiffRowKind.Removed, row.Text, comment)) return;
        EndComment(row);
    }
}

public sealed partial class FilesViewModel
{
    /// <summary>The repository's top folder (where git's paths start), when the project is in one.</summary>
    internal string? GitTop => _git?.TopLevel;

    /// <summary>
    /// The open file's git mark, after it opens and after every refresh. Opened from the Changed or This session list,
    /// a changed file shows its changes first (that list is about them); its changes are read again while shown.
    /// </summary>
    private void SyncViewerGit(FileViewerViewModel v)
    {
        if (_git is null) return;
        var mark = _git.MarkOf(v.FullPath);
        v.GitMark = mark == GitMark.Ignored ? GitMark.None : mark;
        v.RenamedFrom = mark == GitMark.Renamed ? _git.RenamedFrom(v.FullPath) : null;
        var first = !v.GitSynced;
        v.GitSynced = true;
        if (!v.HasChanges) v.ShowChanges = false;
        else if (first && Mode is FilesMode.Changed or FilesMode.Session) v.ShowChanges = true;
        else if (v.ShowChanges) _ = v.LoadDiffAsync();
    }

    internal async Task RevertHunkAsync(FileViewerViewModel v, FileDiff diff, DiffHunk hunk)
    {
        if (GitTop is not { } top) return;
        Notice = "";
        var error = await GitReview.RevertHunkAsync(Owner.RunTool, top, diff, hunk, Owner.Lifetime);
        if (error is not null) Report("Could not revert that part: " + error);
        await AfterRevertAsync(v);
    }

    internal async Task RevertFileAsync(FileViewerViewModel v)
    {
        if (GitTop is not { } top || ProjectFiles.Relative(top, v.FullPath) is not { Length: > 0 } rel) return;
        Notice = "";
        var untracked = v.GitMark == GitMark.Untracked;
        var error = await GitReview.RevertFileAsync(Owner.RunTool, top, rel, untracked, v.RenamedFrom, Owner.Lifetime);
        if (error is not null) Report($"Could not revert {v.Name}: {error}");
        // Deleted, added since the last commit, or renamed back: the file shown is gone
        else if (!File.Exists(v.FullPath) && ReferenceEquals(Viewer, v)) CloseViewer();
        await AfterRevertAsync(v);
    }

    /// <summary>git's marks, the lists and the open file follow at once (not after the refresh delay).</summary>
    private async Task AfterRevertAsync(FileViewerViewModel v)
    {
        await RefreshAsync();
        if (ReferenceEquals(Viewer, v) && v.ShowChanges) await v.LoadDiffAsync();
    }
}
