using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.App.Services.Documents;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.ViewModels;

/// <summary>One segment of a tab's breadcrumbs: a folder or the file, revealed in the page's tree when clicked.</summary>
public sealed record EditorBreadcrumb(string Label, string FullPath, bool IsFile);

/// <summary>A file open in a tab of the Editor page: a view onto the workspace's one buffer for it.</summary>
public sealed partial class EditorTabViewModel : ObservableObject
{
    /// <summary>Edits are compared this long after the last one.</summary>
    internal static readonly TimeSpan GutterDelay = TimeSpan.FromMilliseconds(300);
    /// <summary>The breadcrumbs' symbols are asked for again this long after the last edit.</summary>
    internal static readonly TimeSpan SymbolDelay = TimeSpan.FromMilliseconds(500);

    private bool _wasDirty;
    private long _comparedRevision = -1;
    private string? _base;
    private bool _baseRead;
    private CancellationTokenSource? _gutterCts;
    private CancellationTokenSource? _baseCts;
    private CancellationTokenSource? _symbolCts;
    private JsonElement? _symbols;
    private int _caretLine = 1, _caretCharacter;

    public EditorTabViewModel(EditorGroupViewModel group, string fullPath, string relativePath, WorkspaceDocument document)
    {
        Group = group;
        FullPath = fullPath;
        RelativePath = relativePath;
        Name = Path.GetFileName(fullPath);
        Document = document;
        Breadcrumbs = BuildBreadcrumbs(group.Page.Files.Root ?? group.Page.Owner.ProjectFolder, fullPath, relativePath);
        _wasDirty = document.IsDirty;
        document.Changed += OnDocumentChanged;
    }

    public EditorGroupViewModel Group { get; }
    public EditorPageViewModel Page => Group.Page;
    public string FullPath { get; }
    public string RelativePath { get; }
    public string Name { get; }
    public WorkspaceDocument Document { get; }
    public bool IsDirty => Document.IsDirty;
    /// <summary>The path from the project's folder to the file, each segment revealed in the tree when clicked.</summary>
    public IReadOnlyList<EditorBreadcrumb> Breadcrumbs { get; }

    [ObservableProperty] private int? _targetLine;
    [ObservableProperty] private bool _isActive;
    /// <summary>The git change gutter: the buffer (unsaved edits too) against the file at HEAD.</summary>
    [ObservableProperty] private IReadOnlyList<LineChange> _lineChanges = [];

    /// <summary>The symbol at the caret, outermost first ("EditorPage › OpenAsync"), from the language server; empty without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSymbol))]
    private string _symbol = "";

    public bool HasSymbol => Symbol.Length > 0;

    /// <summary>Diff vs HEAD shown instead of the editor (read-only, the app's diff card).</summary>
    [ObservableProperty] private bool _isDiffShown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiff))]
    private FileChange? _diff;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffNote))]
    private string _diffNote = "";

    public bool HasDiff => Diff is not null;
    public bool HasDiffNote => DiffNote.Length > 0;

    [RelayCommand]
    private void Activate()
    {
        if (!Group.Tabs.Contains(this)) return;
        Group.ActiveTab = this;
        Page.ActiveGroup = Group;
    }

    [RelayCommand] private void Close() => Group.CloseTab(this);
    [RelayCommand] private void ToggleDiff() => IsDiffShown = !IsDiffShown;
    [RelayCommand] private Task RevealAsync(EditorBreadcrumb? crumb) => crumb is null ? Task.CompletedTask : Page.RevealInTreeAsync(crumb.FullPath);

    partial void OnIsDiffShownChanged(bool value)
    {
        if (value) _ = RefreshGutterAsync(); // HEAD read again: the diff is against the commit as it is now
        else { Diff = null; DiffNote = ""; }
    }

    internal static IReadOnlyList<EditorBreadcrumb> BuildBreadcrumbs(string? root, string fullPath, string relativePath)
    {
        if (root is null || Path.IsPathRooted(relativePath)) return [new EditorBreadcrumb(Path.GetFileName(fullPath), fullPath, true)];
        var segments = relativePath.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<EditorBreadcrumb>(segments.Length);
        var path = root;
        for (var i = 0; i < segments.Length; i++)
        {
            path = Path.Combine(path, segments[i]);
            list.Add(new EditorBreadcrumb(segments[i], path, i == segments.Length - 1));
        }
        return list;
    }

    private void OnDocumentChanged(WorkspaceDocument document)
    {
        OnPropertyChanged(nameof(IsDirty));
        if (_wasDirty && !document.IsDirty) _ = RefreshGutterAsync(); // saved, or reloaded from disk: HEAD read again
        else if (document.Revision != _comparedRevision) _ = CompareAsync(GutterDelay);
        _wasDirty = document.IsDirty;
        if (IsActive) _ = RefreshSymbolsAsync(SymbolDelay);
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
        _baseRead = true;
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
                if (IsDiffShown && _baseRead) { Diff = null; DiffNote = Loc.T("This file has no version at HEAD: it isn't in a git repository, or isn't committed yet."); }
                return;
            }
            var revision = Document.Revision;
            var text = Document.Buffer.Text;
            var diffShown = IsDiffShown;
            var (changes, diff) = await Task.Run(() => (GitLineChanges.Compute(head, text), diffShown ? GitLineChanges.UnifiedDiff(head, text) : null), cts.Token);
            if (cts.IsCancellationRequested) return;
            _comparedRevision = revision;
            LineChanges = changes;
            if (IsDiffShown && diff is not null)
            {
                Diff = diff.Length == 0 ? null : new FileChange(RelativePath.Replace('\\', '/'), FileChangeKind.Modified, diff);
                DiffNote = diff.Length == 0 ? Loc.T("No changes against HEAD.") : "";
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The page's editor moved the caret (1-based line, 0-based character): the breadcrumbs' symbol follows.</summary>
    internal void CaretMoved(int line, int character)
    {
        (_caretLine, _caretCharacter) = (line, character);
        Symbol = _symbols is { } symbols ? SymbolPath(symbols, line - 1, character) : "";
    }

    /// <summary>Asks the language server for the file's symbols (when it has them) after <paramref name="delay"/>.</summary>
    internal async Task RefreshSymbolsAsync(TimeSpan delay)
    {
        _symbolCts?.Cancel();
        var cts = _symbolCts = new CancellationTokenSource();
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cts.Token);
            if (Page.Files.LanguageService is not { IsTrusted: true } service
                || await service.CapabilitiesAsync(Document, cts.Token) is not { DocumentSymbol: true })
            {
                if (!cts.IsCancellationRequested) SetSymbols(null);
                return;
            }
            var revision = Document.Revision;
            var result = await service.RequestAsync(Document, "documentSymbol", 0, 0, ct: cts.Token);
            if (cts.IsCancellationRequested || revision != Document.Revision) return;
            SetSymbols(result?.Clone());
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or RpcCommandException or RpcConnectionClosedException or TimeoutException)
        {
            SetSymbols(null);
        }
    }

    private void SetSymbols(JsonElement? symbols)
    {
        _symbols = symbols;
        CaretMoved(_caretLine, _caretCharacter);
    }

    /// <summary>
    /// The names of the symbols around (<paramref name="line"/>, <paramref name="character"/>), outermost first, from
    /// a <c>documentSymbol</c> answer: nested <c>DocumentSymbol</c>s or a flat <c>SymbolInformation</c> list.
    /// </summary>
    internal static string SymbolPath(JsonElement symbols, int line, int character)
    {
        var around = new List<(int StartLine, int StartCharacter, int EndLine, int EndCharacter, string Name)>();
        Collect(symbols);
        around.Sort((a, b) => a.StartLine != b.StartLine ? a.StartLine.CompareTo(b.StartLine)
            : a.StartCharacter != b.StartCharacter ? a.StartCharacter.CompareTo(b.StartCharacter)
            : b.EndLine != a.EndLine ? b.EndLine.CompareTo(a.EndLine) : b.EndCharacter.CompareTo(a.EndCharacter));
        return string.Join(" › ", around.Select(s => s.Name));

        void Collect(JsonElement level)
        {
            if (level.ValueKind != JsonValueKind.Array) return;
            foreach (var symbol in level.EnumerateArray())
            {
                if (symbol.ValueKind != JsonValueKind.Object || !symbol.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                var range = symbol.TryGetProperty("range", out var r) ? r
                    : symbol.TryGetProperty("location", out var location) && location.TryGetProperty("range", out var lr) ? lr : default;
                if (!Position(range, "start", out var sl, out var sc) || !Position(range, "end", out var el, out var ec)) continue;
                if ((line, character).CompareTo((sl, sc)) < 0 || (line, character).CompareTo((el, ec)) > 0) continue;
                around.Add((sl, sc, el, ec, name.GetString()!));
                if (symbol.TryGetProperty("children", out var children)) Collect(children);
            }
        }

        static bool Position(JsonElement range, string which, out int l, out int c)
        {
            l = c = 0;
            return range.ValueKind == JsonValueKind.Object && range.TryGetProperty(which, out var p)
                && p.TryGetProperty("line", out var pl) && pl.TryGetInt32(out l) && p.TryGetProperty("character", out var pc) && pc.TryGetInt32(out c);
        }
    }

    /// <summary>"Comment…" on lines (DocumentEditor.Marks): a mark for the agent, as from the Files pane's editor.</summary>
    internal bool AddEditorMark(Controls.EditorMarkRequest r)
    {
        var snapshot = new CodeCommentAnchor(r.DocumentUri, r.Revision, r.Unsaved, r.BufferSha256, null);
        var anchor = new CodeAnchor(RelativePath.Replace('\\', '/'), r.FirstLine, false, r.Text, CodeMarkOrigin.Editor,
            EndLine: r.LastLine > r.FirstLine ? r.LastLine : null, Before: r.Before, After: r.After, Snapshot: snapshot);
        return Page.Owner.AddCodeMark(anchor, r.Note);
    }

    internal void Release()
    {
        _gutterCts?.Cancel();
        _baseCts?.Cancel();
        _symbolCts?.Cancel();
        Document.Changed -= OnDocumentChanged;
    }
}

/// <summary>
/// One editor group of the Editor page: a tab strip and the editor under it. The page has one, or two side by side
/// after "Open to the side"; the same file may be open in both, as two views of its one buffer.
/// </summary>
public sealed partial class EditorGroupViewModel : ObservableObject
{
    public EditorGroupViewModel(EditorPageViewModel page) => Page = page;

    public EditorPageViewModel Page { get; }
    public ObservableCollection<EditorTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveTab), nameof(ShowEmpty))]
    private EditorTabViewModel? _activeTab;

    /// <summary>The group that opens files, the page's <see cref="EditorPageViewModel.ActiveGroup"/>.</summary>
    [ObservableProperty] private bool _isFocused;

    public bool HasActiveTab => ActiveTab is not null;
    public bool ShowEmpty => ActiveTab is null;
    /// <summary>A group can be closed while another one remains.</summary>
    public bool CanClose => Page.Groups.Count > 1;

    /// <summary>Not the first group: a hairline at its left edge divides the two.</summary>
    public bool IsSecond => Page.Groups.IndexOf(this) > 0;

    internal void GroupsChanged()
    {
        OnPropertyChanged(nameof(CanClose));
        OnPropertyChanged(nameof(IsSecond));
    }

    partial void OnActiveTabChanged(EditorTabViewModel? oldValue, EditorTabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null)
        {
            newValue.IsActive = true;
            _ = newValue.RefreshGutterAsync();
            _ = newValue.RefreshSymbolsAsync(TimeSpan.Zero);
        }
        Page.GroupActiveTabChanged(this);
    }

    internal EditorTabViewModel? Find(string fullPath, WorkspaceDocument? document = null) =>
        Tabs.FirstOrDefault(t => string.Equals(t.FullPath, fullPath, ProjectFiles.PathComparison) || (document is not null && ReferenceEquals(t.Document, document)));

    /// <summary>A new tab after the current one.</summary>
    internal EditorTabViewModel Add(string fullPath, string relativePath, WorkspaceDocument document)
    {
        var tab = new EditorTabViewModel(this, fullPath, relativePath, document);
        Tabs.Insert(ActiveTab is null ? Tabs.Count : Tabs.IndexOf(ActiveTab) + 1, tab);
        return tab;
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

    /// <summary>Moves a tab to another place in the strip (drag, or the tab menu's Move left / right).</summary>
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

    [RelayCommand]
    private void CloseOtherTabs(EditorTabViewModel? keep)
    {
        keep ??= ActiveTab;
        foreach (var t in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList()) CloseTab(t);
    }

    [RelayCommand] private void Close() => Page.CloseGroup(this);

    [RelayCommand] private void Focus() => Page.ActiveGroup = this;

    internal void Release()
    {
        foreach (var tab in Tabs) tab.Release();
        Tabs.Clear();
        ActiveTab = null;
    }
}

/// <summary>
/// The rail's Editor page: the project's tree (the Files pane's, <see cref="FilesViewModel.Nodes"/>), one or two editor
/// groups of tabs, and find / replace in files. Its tabs are views onto the same <see cref="WorkspaceDocument"/> buffers
/// as the right panel's Files viewer (<see cref="MainViewModel.Documents"/>): an edit, its undo and its unsaved state
/// are one, wherever the file is shown; each place keeps its own caret, scroll and editor control.
/// </summary>
public sealed partial class EditorPageViewModel : ObservableObject
{
    /// <summary>At most this many editor groups side by side.</summary>
    public const int MaxGroups = 2;

    public EditorPageViewModel(MainViewModel owner)
    {
        Owner = owner;
        var first = new EditorGroupViewModel(this) { IsFocused = true };
        Groups.Add(first);
        _activeGroup = first;
    }

    public MainViewModel Owner { get; }
    public FilesViewModel Files => Owner.Files;
    public ObservableCollection<EditorGroupViewModel> Groups { get; } = [];

    /// <summary>The group files open in (the one last clicked or typed in).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tabs), nameof(ActiveTab), nameof(HasActiveTab))]
    private EditorGroupViewModel _activeGroup;

    /// <summary>The active group's tabs.</summary>
    public ObservableCollection<EditorTabViewModel> Tabs => ActiveGroup.Tabs;

    /// <summary>The active group's current tab.</summary>
    public EditorTabViewModel? ActiveTab { get => ActiveGroup.ActiveTab; set => ActiveGroup.ActiveTab = value; }

    public bool HasActiveTab => ActiveTab is not null;
    public bool HasSplit => Groups.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    /// <summary>The tree row a breadcrumb revealed: the view selects and scrolls to it.</summary>
    [ObservableProperty] private FileNodeViewModel? _revealedNode;

    public bool HasNotice => Notice.Length > 0;
    public string QuickOpenTip => Loc.F("Go to file ({0})", MainViewModel.FilePaletteShortcut);
    public static string SplitTip { get; } = Loc.T("Open to the side");

    partial void OnActiveGroupChanged(EditorGroupViewModel value)
    {
        foreach (var group in Groups) group.IsFocused = ReferenceEquals(group, value);
    }

    internal void GroupActiveTabChanged(EditorGroupViewModel group)
    {
        if (!ReferenceEquals(group, ActiveGroup)) return;
        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(HasActiveTab));
    }

    /// <summary>Opens <paramref name="fullPath"/> in a tab of the active group (the one it has, else a new one after the current) at <paramref name="line"/>.</summary>
    public async Task OpenAsync(string fullPath, int? line = null)
    {
        if (Directory.Exists(fullPath))
        {
            await Files.ShowInTreeAsync(fullPath, expand: true);
            return;
        }
        Notice = "";
        var group = ActiveGroup;
        var tab = group.Find(fullPath);
        if (tab is null)
        {
            WorkspaceDocument document;
            try { document = await Owner.Documents.OpenAsync(fullPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
            {
                Notice = Loc.F("Can't open {0} in the editor: {1}", Path.GetFileName(fullPath), e.Message);
                return;
            }
            tab = group.Find(fullPath, document);
            if (tab is null)
            {
                var root = Files.Root ?? Owner.ProjectFolder;
                var relative = root is null ? null : ProjectFiles.Relative(root, fullPath);
                tab = group.Add(fullPath, string.IsNullOrEmpty(relative) ? fullPath : relative, document);
                Files.RememberDocumentNavigationPath(document, fullPath);
            }
        }
        // Again the same line: the editor goes there again
        if (line is not null && tab.TargetLine == line) tab.TargetLine = null;
        tab.TargetLine = line;
        group.ActiveTab = tab;
    }

    /// <summary>
    /// Open to the side: the active tab's file in the other group (made when there is one group), which becomes the
    /// active one. Both tabs are views of the file's one buffer.
    /// </summary>
    [RelayCommand]
    private async Task SplitRightAsync()
    {
        if (ActiveTab is not { } tab) return;
        var other = Groups.FirstOrDefault(g => !ReferenceEquals(g, ActiveGroup));
        if (other is null)
        {
            if (Groups.Count >= MaxGroups) return;
            other = new EditorGroupViewModel(this);
            Groups.Add(other);
            GroupsChanged();
        }
        ActiveGroup = other;
        await OpenAsync(tab.FullPath);
    }

    /// <summary>Closes a group and its tabs (buffers stay the workspace's); the last group stays.</summary>
    internal void CloseGroup(EditorGroupViewModel group)
    {
        if (Groups.Count <= 1 || !Groups.Contains(group)) return;
        group.Release();
        Groups.Remove(group);
        if (ReferenceEquals(ActiveGroup, group)) ActiveGroup = Groups[0];
        GroupsChanged();
    }

    private void GroupsChanged()
    {
        OnPropertyChanged(nameof(HasSplit));
        foreach (var group in Groups) group.GroupsChanged();
    }

    /// <summary>A breadcrumb's folder or file, shown in the page's tree (the search closes, the folder opens).</summary>
    internal async Task RevealInTreeAsync(string fullPath)
    {
        IsSearchShown = false;
        RevealedNode = await Files.ShowInTreeAsync(fullPath, expand: Directory.Exists(fullPath));
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
