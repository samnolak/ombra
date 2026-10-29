using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.App.Services.Documents;

namespace Ombra.App.ViewModels;

/// <summary>One line found by the Editor page's find in files; <see cref="Replaced"/> is the line after Replace all.</summary>
public sealed record EditorSearchHit(string FullPath, string RelativePath, int Line, string Text, int Matches = 1, string? Replaced = null)
{
    public string Location => $"{RelativePath}:{Line}";
    public bool HasReplaced => Replaced is not null;
}

/// <summary>A file Replace all would change: its lines before and after, kept in or left out of the replace.</summary>
public sealed partial class EditorReplaceFile(string fullPath, string relativePath, IReadOnlyList<EditorSearchHit> hits) : ObservableObject
{
    public string FullPath { get; } = fullPath;
    public string RelativePath { get; } = relativePath;
    public IReadOnlyList<EditorSearchHit> Hits { get; } = hits;
    public int Matches { get; } = hits.Sum(h => h.Matches);
    public string MatchesText => Matches == 1 ? Loc.T("1 match") : Loc.F("{0} matches", Matches);

    [ObservableProperty] private bool _isIncluded = true;
}

public sealed partial class EditorPageViewModel
{
    /// <summary>Find in files stops at this many lines found.</summary>
    public const int MaxSearchHits = 500;
    /// <summary>Find in files skips files larger than this.</summary>
    public const long MaxSearchFileBytes = 1024 * 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _searchCts;
    /// <summary>The search the replace preview was made for (pattern and replacement): a change makes it stale.</summary>
    private (Regex Regex, string Replacement)? _previewed;

    public ObservableCollection<EditorSearchHit> SearchHits { get; } = [];
    public ObservableCollection<EditorReplaceFile> ReplaceFiles { get; } = [];

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearchShown;
    [ObservableProperty] private bool _isSearchRunning;
    [ObservableProperty] private bool _searchRegex;
    [ObservableProperty] private bool _searchMatchCase;
    [ObservableProperty] private bool _searchWholeWord;

    /// <summary>Replace in files: the replace field and its preview are shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHits))]
    private bool _isReplaceShown;

    /// <summary>What matches become; with Regex on, <c>$1</c> / <c>${name}</c> insert groups.</summary>
    [ObservableProperty] private string _replaceText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchNote))]
    private string _searchNote = "";

    /// <summary>Replace all asked "are you sure": the question shows with Replace / Cancel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHits))]
    private bool _isConfirmingReplace;

    [ObservableProperty] private string _replaceConfirmation = "";

    public bool HasSearchNote => SearchNote.Length > 0;
    public bool HasReplacePreview => ReplaceFiles.Count > 0;
    /// <summary>The plain list of hits (find), not the per-file replace preview.</summary>
    public bool ShowHits => !IsReplaceShown;

    partial void OnSearchQueryChanged(string value) => ForgetPreview();
    partial void OnReplaceTextChanged(string value) => ForgetPreview();
    partial void OnSearchRegexChanged(bool value) => ForgetPreview();
    partial void OnSearchMatchCaseChanged(bool value) => ForgetPreview();
    partial void OnSearchWholeWordChanged(bool value) => ForgetPreview();

    private void ForgetPreview()
    {
        _previewed = null;
        IsConfirmingReplace = false;
        if (ReplaceFiles.Count == 0) return;
        ReplaceFiles.Clear();
        OnPropertyChanged(nameof(HasReplacePreview));
    }

    [RelayCommand] private void ToggleSearch() => IsSearchShown = !IsSearchShown;
    [RelayCommand] private void ToggleReplace() { IsSearchShown = true; IsReplaceShown = !IsReplaceShown; }
    [RelayCommand] private void ToggleSearchRegex() => SearchRegex = !SearchRegex;
    [RelayCommand] private void ToggleSearchMatchCase() => SearchMatchCase = !SearchMatchCase;
    [RelayCommand] private void ToggleSearchWholeWord() => SearchWholeWord = !SearchWholeWord;

    [RelayCommand]
    private Task OpenHitAsync(EditorSearchHit? hit) => hit is null ? Task.CompletedTask : OpenAsync(hit.FullPath, hit.Line);

    /// <summary>Enter in the search box: find, or with the replace field shown, the replace preview.</summary>
    [RelayCommand]
    private Task SearchAsync() => IsReplaceShown ? PreviewReplaceAsync() : FindInFilesAsync(SearchQuery);

    /// <summary>The search as a regular expression: the query itself (Regex on) or literally; whole words; case.</summary>
    internal Regex? BuildRegex(string query, out string? error)
    {
        error = null;
        var pattern = SearchRegex ? query : Regex.Escape(query);
        if (SearchWholeWord) pattern = $@"(?<!\w)(?:{pattern})(?!\w)";
        var options = RegexOptions.CultureInvariant | (SearchMatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        try { return new Regex(pattern, options, RegexTimeout); }
        catch (ArgumentException e)
        {
            error = Loc.F("Not a valid regular expression: {0}", e.Message);
            return null;
        }
    }

    /// <summary>
    /// Find in files: every line of the project's text files matching <paramref name="query"/> (with the Regex, Match
    /// case and Whole word choices), the files git lists (ignored ones left out; without git the folder's files but
    /// .git, node_modules, bin, obj and the like), open files as their buffers have them, off the UI thread, at most
    /// <see cref="MaxSearchHits"/> lines.
    /// </summary>
    public Task FindInFilesAsync(string query) => RunSearchAsync(query, replacement: null);

    /// <summary>Replace all's preview: every file the replace would change with its lines after it, each kept in.</summary>
    public Task PreviewReplaceAsync() => RunSearchAsync(SearchQuery, ReplaceText);

    private async Task RunSearchAsync(string query, string? replacement)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        SearchHits.Clear();
        ForgetPreview();
        SearchNote = "";
        if (!SearchRegex) query = query.Trim();
        if (query.Length == 0) return;
        if ((Files.Root ?? Owner.ProjectFolder) is not { } root)
        {
            SearchNote = Loc.T("Open a project to search its files.");
            return;
        }
        if (BuildRegex(query, out var error) is not { } regex)
        {
            SearchNote = error!;
            return;
        }
        var substitution = replacement is null ? null : SearchRegex ? replacement : replacement.Replace("$", "$$");
        // Open files are searched as their buffers are (unsaved edits too): Replace all changes those buffers
        var open = Owner.Documents.Documents.ToDictionary(d => d.FullPath, d => d.Buffer.Text, ProjectFiles.PathComparer);
        IsSearchRunning = true;
        try
        {
            var (paths, truncated) = await ProjectFiles.IndexAsync(root, useGit: true, ct);
            var hits = await Task.Run(() => Search(root, paths, regex, substitution, open, ct), ct);
            if (ct.IsCancellationRequested) return;
            foreach (var h in hits) SearchHits.Add(h);
            if (substitution is not null)
            {
                foreach (var file in hits.GroupBy(h => h.FullPath, ProjectFiles.PathComparer))
                    ReplaceFiles.Add(new EditorReplaceFile(file.Key, file.First().RelativePath, [.. file]));
                _previewed = (regex, substitution);
                OnPropertyChanged(nameof(HasReplacePreview));
            }
            var matches = hits.Sum(h => h.Matches);
            SearchNote = hits.Count == 0 ? Loc.F("Nothing found for “{0}”.", query)
                : hits.Count >= MaxSearchHits ? Loc.F("Showing the first {0} matches", MaxSearchHits)
                : truncated ? Loc.F("Searched the first {0:N0} files", ProjectFiles.MaxIndexedFiles)
                : substitution is not null ? Loc.F("{0} matches in {1} files", matches, ReplaceFiles.Count)
                : Loc.F("{0} matches", hits.Count);
        }
        catch (RegexMatchTimeoutException) { SearchNote = Loc.T("The regular expression took too long; make it simpler."); }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) IsSearchRunning = false;
        }
    }

    internal static List<EditorSearchHit> Search(string root, IEnumerable<string> relativePaths, Regex regex, string? replacement,
        IReadOnlyDictionary<string, string> open, CancellationToken ct)
    {
        var hits = new List<EditorSearchHit>();
        foreach (var relative in relativePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (relative.Split('/').Any(s => ProjectFiles.Heavy.Contains(s) || ProjectFiles.Hidden.Contains(s))) continue;
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var text = open.TryGetValue(full, out var buffered) ? buffered : ReadText(full);
            if (text is null) continue;
            var number = 0;
            foreach (var raw in text.Split('\n'))
            {
                number++;
                var line = raw.TrimEnd('\r');
                var count = regex.Count(line);
                if (count == 0) continue;
                hits.Add(new EditorSearchHit(full, relative, number, Shown(line), count, replacement is null ? null : Shown(regex.Replace(line, replacement))));
                if (hits.Count >= MaxSearchHits) return hits;
            }
        }
        return hits;

        static string Shown(string line)
        {
            var shown = line.Trim();
            return shown.Length > 200 ? shown[..200] + "…" : shown;
        }
    }

    private static string? ReadText(string full)
    {
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > MaxSearchFileBytes) return null;
            var bytes = File.ReadAllBytes(full);
            if (FileViewerViewModel.IsBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) return null;
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Replace all: asks first, naming how many matches in how many files and what happens to unsaved files.</summary>
    [RelayCommand]
    private void ReplaceAll()
    {
        var files = ReplaceFiles.Where(f => f.IsIncluded).ToList();
        if (_previewed is null || files.Count == 0) { SearchNote = Loc.T("Preview the replace first, and keep at least one file in it."); return; }
        var unsaved = files.Count(f => Owner.Documents.TryGet(f.FullPath, out var d) && d.IsDirty);
        ReplaceConfirmation = Loc.F("Replace {0} matches in {1} files?", files.Sum(f => f.Matches), files.Count)
            + (unsaved > 0 ? " " + Loc.F("{0} of them have unsaved edits: they change in the editor and stay unsaved.", unsaved) : "")
            + " " + Loc.T("The others are saved. Each file's change can be undone in its tab.");
        IsConfirmingReplace = true;
    }

    [RelayCommand] private void CancelReplace() => IsConfirmingReplace = false;

    /// <summary>
    /// Replace all, confirmed: each kept file's lines change in its workspace buffer (the tabs showing it follow) as
    /// one undo step; files that were saved are saved again, files with unsaved edits stay unsaved. A file changed on
    /// disk meanwhile (conflict) or read-only is left alone and named.
    /// </summary>
    [RelayCommand]
    public async Task ConfirmReplaceAsync()
    {
        IsConfirmingReplace = false;
        if (_previewed is not { } previewed) return;
        var (regex, replacement) = previewed;
        int files = 0, matches = 0, keptUnsaved = 0;
        var skipped = new List<string>();
        foreach (var file in ReplaceFiles.Where(f => f.IsIncluded).ToList())
        {
            WorkspaceDocument document;
            try { document = await Owner.Documents.OpenAsync(file.FullPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
            {
                skipped.Add(file.RelativePath);
                continue;
            }
            if (document.IsReadOnly || document.HasConflict) { skipped.Add(file.RelativePath); continue; }
            var wasDirty = document.IsDirty;
            int count;
            try { count = ReplaceInBuffer(document.Buffer, regex, replacement); }
            catch (RegexMatchTimeoutException) { skipped.Add(file.RelativePath); continue; }
            if (count == 0) continue;
            files++;
            matches += count;
            if (wasDirty) { keptUnsaved++; continue; }
            try { await document.SaveAsync(); }
            catch (Exception e) when (e is not OutOfMemoryException) { skipped.Add(file.RelativePath); }
        }
        ForgetPreview();
        SearchHits.Clear();
        SearchNote = Loc.F("Replaced {0} matches in {1} files.", matches, files)
            + (keptUnsaved > 0 ? " " + Loc.F("{0} files with unsaved edits were changed in the editor; save them when ready.", keptUnsaved) : "")
            + (skipped.Count > 0 ? " " + Loc.F("Left alone (read-only, changed on disk, or not saved): {0}", string.Join(", ", skipped)) : "");
    }

    /// <summary>Every line's matches replaced in <paramref name="buffer"/>, as one undo step; the number replaced.</summary>
    internal static int ReplaceInBuffer(TextDocument buffer, Regex regex, string replacement)
    {
        var edits = new List<(int Offset, int Length, string Text)>();
        var total = 0;
        foreach (var line in buffer.Lines)
        {
            var text = buffer.GetText(line.Offset, line.Length);
            var count = regex.Count(text);
            if (count == 0) continue;
            total += count;
            edits.Add((line.Offset, line.Length, regex.Replace(text, replacement)));
        }
        if (edits.Count == 0) return 0;
        buffer.BeginUpdate();
        try
        {
            for (var i = edits.Count - 1; i >= 0; i--) buffer.Replace(edits[i].Offset, edits[i].Length, edits[i].Text);
        }
        finally { buffer.EndUpdate(); }
        return total;
    }
}
