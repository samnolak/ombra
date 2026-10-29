using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.App.Services.Documents;

namespace Ombra.App.ViewModels;

/// <summary>One line found by the Editor page's find in files.</summary>
public sealed record EditorSearchHit(string FullPath, string RelativePath, int Line, string Text)
{
    public string Location => $"{RelativePath}:{Line}";
}

/// <summary>A file open in a tab of the Editor page: a view onto the workspace's one buffer for it.</summary>
public sealed partial class EditorTabViewModel : ObservableObject
{
    /// <summary>Edits are compared this long after the last one.</summary>
    internal static readonly TimeSpan GutterDelay = TimeSpan.FromMilliseconds(300);

    private bool _wasDirty;
    private long _comparedRevision = -1;
    private string? _base;
    private CancellationTokenSource? _gutterCts;
    private CancellationTokenSource? _baseCts;

    public EditorTabViewModel(EditorPageViewModel page, string fullPath, string relativePath, WorkspaceDocument document)
    {
        Page = page;
        FullPath = fullPath;
        RelativePath = relativePath;
        Name = Path.GetFileName(fullPath);
        Document = document;
        _wasDirty = document.IsDirty;
        document.Changed += OnDocumentChanged;
    }

    public EditorPageViewModel Page { get; }
    public string FullPath { get; }
    public string RelativePath { get; }
    public string Name { get; }
    public WorkspaceDocument Document { get; }
    public bool IsDirty => Document.IsDirty;

    [ObservableProperty] private int? _targetLine;
    [ObservableProperty] private bool _isActive;
    /// <summary>The git change gutter: the buffer (unsaved edits too) against the file at HEAD.</summary>
    [ObservableProperty] private IReadOnlyList<LineChange> _lineChanges = [];

    [RelayCommand] private void Activate() { if (Page.Tabs.Contains(this)) Page.ActiveTab = this; }
    [RelayCommand] private void Close() => Page.CloseTab(this);

    private void OnDocumentChanged(WorkspaceDocument document)
    {
        OnPropertyChanged(nameof(IsDirty));
        if (_wasDirty && !document.IsDirty) _ = RefreshGutterAsync(); // saved, or reloaded from disk: HEAD read again
        else if (document.Revision != _comparedRevision) _ = CompareAsync(GutterDelay);
        _wasDirty = document.IsDirty;
    }

    /// <summary>Reads the HEAD text again (open, activation, save: a commit may have moved HEAD), then compares.</summary>
    internal async Task RefreshGutterAsync()
    {
        _baseCts?.Cancel();
        var cts = _baseCts = new CancellationTokenSource();
        try
        {
            var head = await GitLineChanges.ReadBaseAsync(FullPath, cts.Token);
            if (cts.IsCancellationRequested) return;
            _base = head;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _base = null; }
        await CompareAsync(TimeSpan.Zero);
    }

    /// <summary>The buffer against the cached HEAD text, after <paramref name="delay"/> without another edit.</summary>
    private async Task CompareAsync(TimeSpan delay)
    {
        _gutterCts?.Cancel();
        var cts = _gutterCts = new CancellationTokenSource();
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cts.Token);
            if (_base is not { } head)
            {
                LineChanges = [];
                return;
            }
            var revision = Document.Revision;
            var text = Document.Buffer.Text;
            var changes = await Task.Run(() => GitLineChanges.Compute(head, text), cts.Token);
            if (cts.IsCancellationRequested) return;
            _comparedRevision = revision;
            LineChanges = changes;
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>"Comment…" on lines (DocumentEditor.Marks): a mark for the agent, as from the Files pane's editor.</summary>
    internal bool AddEditorMark(Controls.EditorMarkRequest r)
    {
        var snapshot = new Ombra.ClientCore.CodeCommentAnchor(r.DocumentUri, r.Revision, r.Unsaved, r.BufferSha256, null);
        var anchor = new Ombra.ClientCore.CodeAnchor(RelativePath.Replace('\\', '/'), r.FirstLine, false, r.Text, Ombra.ClientCore.CodeMarkOrigin.Editor,
            EndLine: r.LastLine > r.FirstLine ? r.LastLine : null, Before: r.Before, After: r.After, Snapshot: snapshot);
        return Page.Owner.AddCodeMark(anchor, r.Note);
    }

    internal void Release()
    {
        _gutterCts?.Cancel();
        _baseCts?.Cancel();
        Document.Changed -= OnDocumentChanged;
    }
}

/// <summary>
/// The rail's Editor page: the project's tree (the Files pane's, <see cref="FilesViewModel.Nodes"/>), tabs of open
/// files and find in files. Its tabs are views onto the same <see cref="WorkspaceDocument"/> buffers as the right
/// panel's Files viewer (<see cref="MainViewModel.Documents"/>): an edit, its undo and its unsaved state are one, wherever
/// the file is shown; each place keeps its own caret, scroll and editor control, so the two never fight over one.
/// </summary>
public sealed partial class EditorPageViewModel : ObservableObject
{
    /// <summary>Find in files stops at this many lines found.</summary>
    public const int MaxSearchHits = 500;
    /// <summary>Find in files skips files larger than this.</summary>
    public const long MaxSearchFileBytes = 1024 * 1024;

    private CancellationTokenSource? _searchCts;

    public EditorPageViewModel(MainViewModel owner) => Owner = owner;

    public MainViewModel Owner { get; }
    public FilesViewModel Files => Owner.Files;
    public ObservableCollection<EditorTabViewModel> Tabs { get; } = [];
    public ObservableCollection<EditorSearchHit> SearchHits { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveTab), nameof(ShowEmpty))]
    private EditorTabViewModel? _activeTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearchShown;
    [ObservableProperty] private bool _isSearchRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchNote))]
    private string _searchNote = "";

    public bool HasActiveTab => ActiveTab is not null;
    public bool ShowEmpty => ActiveTab is null;
    public bool HasNotice => Notice.Length > 0;
    public bool HasSearchNote => SearchNote.Length > 0;
    public string QuickOpenTip => Loc.F("Go to file ({0})", MainViewModel.FilePaletteShortcut);

    partial void OnActiveTabChanged(EditorTabViewModel? oldValue, EditorTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null)
        {
            newValue.IsActive = true;
            _ = newValue.RefreshGutterAsync();
        }
    }

    /// <summary>Opens <paramref name="fullPath"/> in a tab (the one it has, else a new one after the current) at <paramref name="line"/>.</summary>
    public async Task OpenAsync(string fullPath, int? line = null)
    {
        if (Directory.Exists(fullPath))
        {
            await Files.ShowInTreeAsync(fullPath, expand: true);
            return;
        }
        Notice = "";
        var tab = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, fullPath, ProjectFiles.PathComparison));
        if (tab is null)
        {
            WorkspaceDocument document;
            try { document = await Owner.Documents.OpenAsync(fullPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
            {
                Notice = Loc.F("Can't open {0} in the editor: {1}", Path.GetFileName(fullPath), e.Message);
                return;
            }
            tab = Tabs.FirstOrDefault(t => ReferenceEquals(t.Document, document));
            if (tab is null)
            {
                var root = Files.Root ?? Owner.ProjectFolder;
                var relative = root is null ? null : ProjectFiles.Relative(root, fullPath);
                tab = new EditorTabViewModel(this, fullPath, string.IsNullOrEmpty(relative) ? fullPath : relative, document);
                var at = ActiveTab is null ? Tabs.Count : Tabs.IndexOf(ActiveTab) + 1;
                Tabs.Insert(at, tab);
                Files.RememberDocumentNavigationPath(document, fullPath);
            }
        }
        // Again the same line: the editor goes there again
        if (line is not null && tab.TargetLine == line) tab.TargetLine = null;
        tab.TargetLine = line;
        ActiveTab = tab;
    }

    /// <summary>Closes a tab; its buffer stays the workspace's (unsaved edits are kept and listed as unsaved).</summary>
    internal void CloseTab(EditorTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.RemoveAt(index);
        tab.Release();
        if (ReferenceEquals(ActiveTab, tab)) ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    /// <summary>Moves a tab to another place in the strip (drag, or ⌥⇧← / ⌥⇧→ on the active one).</summary>
    internal void MoveTab(EditorTabViewModel tab, int to)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0) return;
        to = Math.Clamp(to, 0, Tabs.Count - 1);
        if (to != from) Tabs.Move(from, to);
    }

    [RelayCommand] private void MoveTabLeft(EditorTabViewModel? tab) { if ((tab ?? ActiveTab) is { } t) MoveTab(t, Tabs.IndexOf(t) - 1); }
    [RelayCommand] private void MoveTabRight(EditorTabViewModel? tab) { if ((tab ?? ActiveTab) is { } t) MoveTab(t, Tabs.IndexOf(t) + 1); }

    [RelayCommand] private void CloseActiveTab() { if (ActiveTab is { } t) CloseTab(t); }

    [RelayCommand] private void CloseOtherTabs(EditorTabViewModel? keep)
    {
        keep ??= ActiveTab;
        foreach (var t in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList()) CloseTab(t);
    }

    [RelayCommand] private void DismissNotice() => Notice = "";

    /// <summary>A row of the page's tree: a folder opens or closes, a file opens in a tab.</summary>
    [RelayCommand]
    private async Task OpenNodeAsync(FileNodeViewModel? node)
    {
        if (node is null) return;
        if (node.IsDirectory) await Files.ToggleAsync(node);
        else await OpenAsync(node.FullPath);
    }

    /// <summary>Go to file: the command palette's files mode (⌘P), which opens the chosen file here while the page is shown.</summary>
    [RelayCommand] private void QuickOpen() => Owner.OpenFilePaletteCommand.Execute(null);

    [RelayCommand] private void ToggleSearch() => IsSearchShown = !IsSearchShown;

    [RelayCommand]
    private Task OpenHitAsync(EditorSearchHit? hit) => hit is null ? Task.CompletedTask : OpenAsync(hit.FullPath, hit.Line);

    [RelayCommand]
    private Task SearchAsync() => FindInFilesAsync(SearchQuery);

    /// <summary>
    /// Find in files: every line of the project's text files containing <paramref name="query"/> (case-insensitive), the
    /// files git lists (ignored ones left out; without git the folder's files but .git, node_modules, bin, obj and the
    /// like), off the UI thread, at most <see cref="MaxSearchHits"/> lines.
    /// </summary>
    public async Task FindInFilesAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        SearchHits.Clear();
        SearchNote = "";
        query = query.Trim();
        if (query.Length == 0) return;
        if ((Files.Root ?? Owner.ProjectFolder) is not { } root)
        {
            SearchNote = Loc.T("Open a project to search its files.");
            return;
        }
        IsSearchRunning = true;
        try
        {
            var (paths, truncated) = await ProjectFiles.IndexAsync(root, useGit: true, ct);
            var hits = await Task.Run(() => Search(root, paths, query, ct), ct);
            if (ct.IsCancellationRequested) return;
            foreach (var h in hits) SearchHits.Add(h);
            SearchNote = hits.Count == 0 ? Loc.F("Nothing found for “{0}”.", query)
                : hits.Count >= MaxSearchHits ? Loc.F("Showing the first {0} matches", MaxSearchHits)
                : truncated ? Loc.F("Searched the first {0:N0} files", ProjectFiles.MaxIndexedFiles)
                : Loc.F("{0} matches", hits.Count);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) IsSearchRunning = false;
        }
    }

    internal static List<EditorSearchHit> Search(string root, IEnumerable<string> relativePaths, string query, CancellationToken ct)
    {
        var hits = new List<EditorSearchHit>();
        foreach (var relative in relativePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (relative.Split('/').Any(s => ProjectFiles.Heavy.Contains(s) || ProjectFiles.Hidden.Contains(s))) continue;
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var info = new FileInfo(full);
                if (!info.Exists || info.Length > MaxSearchFileBytes) continue;
                var bytes = File.ReadAllBytes(full);
                if (FileViewerViewModel.IsBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 8000)))) continue;
                using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                var number = 0;
                foreach (var line in text.Split('\n'))
                {
                    number++;
                    if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        var shown = line.TrimEnd('\r').Trim();
                        hits.Add(new EditorSearchHit(full, relative, number, shown.Length > 200 ? shown[..200] + "…" : shown));
                        if (hits.Count >= MaxSearchHits) return hits;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return hits;
    }
}

public sealed partial class MainViewModel
{
    public EditorPageViewModel EditorPage => field ??= new EditorPageViewModel(this);

    /// <summary>The Editor page is the one shown (unless settings are open over it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEditorPage))]
    private bool _isEditorPageOpen;

    public bool ShowEditorPage => IsEditorPageOpen && !IsSettingsOpen;

    public static string EditorRailTip { get; } = Loc.F("Editor ({0})", KeyboardShortcuts.EditorPage);

    partial void OnIsEditorPageOpenChanged(bool value) => OnRailChanged();

    /// <summary>The rail's Editor: settings, the Team and the Browser pages close, the Editor page shows.</summary>
    [RelayCommand]
    private void OpenEditorPage()
    {
        if (IsSettingsOpen) CloseSettingsCommand.Execute(null);
        IsTeamPageOpen = false;
        IsBrowserPageOpen = false;
        IsEditorPageOpen = true;
    }

    /// <summary>⌘⇧O: the Editor page, or back to the chats from it.</summary>
    [RelayCommand]
    private void ToggleEditorPage()
    {
        if (ShowEditorPage) ShowChatsCommand.Execute(null);
        else OpenEditorPage();
    }
}
