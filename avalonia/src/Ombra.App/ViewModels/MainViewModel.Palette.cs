using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Controls;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// The command palette (Controls/CommandPalette), Codex's ⌘K: one box that finds and runs any action of the window, a
/// panel, a settings page, a theme, a chat (unread ones first) or a file of the project. ⌘P opens it on the files only.
/// Every entry runs the window's own command, so it does what the button or menu item does, and is greyed with why when
/// that command cannot run now. What was run last comes first. The typed text matches the titles fuzzily (prefixes
/// above word starts above substrings above scattered letters), the matched letters highlighted; the files come from
/// the index the Files pane and the "@" menu use, searched off the UI thread, bounded and cancelled by the next key.
/// Keys: ⌘K and ⌘P are free (⇧⌘F/G/P/T/B/S/M are the panes and the preview, ⌘F/⌘G find in chat), and ⌘P is Codex's
/// and VS Code's "Go to file".
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Entries the palette remembers as run last.</summary>
    private const int PaletteRecentCount = 12;

    /// <summary>Files listed at most: below the other entries while everything is searched, alone in files mode.</summary>
    private const int PaletteFilesInAll = 20, PaletteFilesOnly = 60;

    public static string PaletteShortcut { get; } = CommandKey("K");
    public static string FilePaletteShortcut { get; } = CommandKey("P");

    [ObservableProperty] private bool _isPaletteOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PalettePlaceholder), nameof(PaletteHint))]
    private PaletteMode _paletteMode;

    [ObservableProperty] private string _paletteQuery = "";

    /// <summary>The entry Enter runs.</summary>
    [ObservableProperty] private PaletteItemViewModel? _selectedPaletteItem;

    /// <summary>Said in place of the list: nothing matches, the files are being searched, no project is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPaletteNotice))]
    private string _paletteNotice = "";
    public bool HasPaletteNotice => PaletteNotice.Length > 0;

    /// <summary>What the palette lists for <see cref="PaletteQuery"/>, best first.</summary>
    public ObservableCollection<PaletteItemViewModel> PaletteItems { get; } = [];

    public string PalettePlaceholder => PaletteMode == PaletteMode.Files
        ? Loc.T("Go to a file of the project")
        : Loc.T("Search actions, panels, settings, chats and files");

    public string PaletteHint => PaletteMode == PaletteMode.Files
        ? Loc.F("↑↓ to choose, Enter to open, Esc to close. {0}: everything.", PaletteShortcut)
        : Loc.F("↑↓ to choose, Enter to run, Esc to close. {0}: files only.", FilePaletteShortcut);

    /// <summary>Everything but the files, made when the palette opens.</summary>
    private List<PaletteItemViewModel> _paletteEntries = [];
    /// <summary>The entries that match the query (no files), and the files found for it.</summary>
    private List<PaletteItemViewModel> _paletteMatched = [], _paletteFiles = [];
    private List<string>? _paletteRecent;
    private CancellationTokenSource? _paletteSession, _paletteFileSearch;
    private Task<ProjectFiles.SearchIndex>? _paletteIndex;
    private bool _paletteSearchingFiles, _resettingPalette;

    /// <summary>⌘K (Ctrl+K): everything.</summary>
    [RelayCommand]
    private void OpenPalette() => ShowPalette(PaletteMode.All);

    /// <summary>⌘P (Ctrl+P): the project's files.</summary>
    [RelayCommand]
    private void OpenFilePalette() => ShowPalette(PaletteMode.Files);

    /// <summary>The palette open on <paramref name="mode"/> (already open on it: left as it is, so the native menu and
    /// the window's key binding may both deliver the key).</summary>
    private void ShowPalette(PaletteMode mode)
    {
        if (IsImageViewerOpen) return; // over the palette: its keys are the viewer's
        if (IsPaletteOpen && PaletteMode == mode) return;
        IsShortcutsOpen = false;
        _paletteSession?.Cancel();
        _paletteSession = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        _paletteIndex = null;
        PaletteMode = mode;
        _paletteEntries = mode == PaletteMode.All ? PaletteEntries() : [];
        _resettingPalette = true; // no search for the old query while it is cleared
        PaletteQuery = "";
        _resettingPalette = false;
        SelectedPaletteItem = null;
        IsPaletteOpen = true;
        UpdatePalette();
    }

    /// <summary>Esc, a click outside the card, or an entry chosen. The view hands the keyboard back to where it was.</summary>
    [RelayCommand]
    private void ClosePalette()
    {
        if (!IsPaletteOpen) return;
        _paletteSession?.Cancel();
        _paletteFileSearch?.Cancel();
        _paletteIndex = null; // the files may change before the next time
        _paletteEntries = [];
        _paletteMatched = [];
        _paletteFiles = [];
        IsPaletteOpen = false;
        PaletteItems.Clear();
        SelectedPaletteItem = null;
        PaletteNotice = "";
    }

    /// <summary>The view: the palette closed and the keyboard had nowhere to go back to.</summary>
    public void FocusComposer() => FocusComposerRequested?.Invoke();

    /// <summary>Enter or a click: runs the entry (the palette closes first); one that cannot run now does nothing.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunPaletteItemAsync(PaletteItemViewModel? item)
    {
        if (item is null) return;
        item.Refresh();
        if (!item.IsEnabled) return;
        RememberPaletteItem(item.Id);
        ClosePalette();
        await item.Run();
    }

    /// <summary>↑ / ↓ (Page Up / Page Down by more): the selection moves, and stops at the ends.</summary>
    public void MovePaletteSelection(int delta)
    {
        if (PaletteItems.Count == 0) return;
        var at = SelectedPaletteItem is { } s ? PaletteItems.IndexOf(s) : -1;
        SelectedPaletteItem = PaletteItems[Math.Clamp(at + delta, 0, PaletteItems.Count - 1)];
    }

    partial void OnPaletteQueryChanged(string value)
    {
        if (IsPaletteOpen && !_resettingPalette) UpdatePalette();
    }

    private void UpdatePalette()
    {
        var q = PaletteQuery.Trim();
        _paletteMatched = PaletteMode == PaletteMode.All ? MatchPaletteEntries(q) : [];
        _paletteFiles = PaletteMode == PaletteMode.Files && q.Length == 0 ? RecentPaletteFiles() : [];
        SearchPaletteFiles(q);
        ShowPaletteList(q, keepSelection: false);
    }

    /// <summary>The entries that match <paramref name="q"/>, best first; with nothing typed, those run last first, then
    /// the unread chats, the actions, panels, settings pages, themes and the other chats, newest first.</summary>
    private List<PaletteItemViewModel> MatchPaletteEntries(string q)
    {
        var recent = PaletteRecent();
        int RecentRank(PaletteItemViewModel item) => recent.IndexOf(item.Id) is var r and >= 0 ? r : int.MaxValue;
        var scored = new List<(PaletteItemViewModel Item, int Score, int Order)>();
        var positions = new List<int>();
        for (var i = 0; i < _paletteEntries.Count; i++)
        {
            var item = _paletteEntries[i];
            var score = PaletteMatch.Score(item.Title, q, positions);
            var marked = score >= 0;
            // The English name, in another language: found, but nothing of the shown title is marked
            if (!marked && item.EnglishTitle is { } english && english != item.Title) score = PaletteMatch.Score(english, q, positions);
            if (score < 0) continue;
            item.Matches = q.Length == 0 || !marked ? [] : [.. positions];
            item.Refresh();
            // Within a tier of the match: run lately, then an unread chat (neither lifts an entry over a better match)
            var rank = RecentRank(item);
            if (q.Length > 0) score += (rank == int.MaxValue ? 0 : 100 - rank * 5) + (item.IsUnread ? 50 : 0);
            scored.Add((item, score, i));
        }
        IEnumerable<(PaletteItemViewModel Item, int Score, int Order)> ordered = q.Length == 0
            ? scored.OrderBy(x => RecentRank(x.Item)).ThenBy(x => x.Order)
            : scored.OrderByDescending(x => x.Score).ThenBy(x => x.Order);
        return [.. ordered.Select(x => x.Item)];
    }

    /// <summary>The matched entries, then the files. <paramref name="keepSelection"/>: the files for the same query came
    /// in, so the entry chosen with the arrows meanwhile stays chosen.</summary>
    private void ShowPaletteList(string q, bool keepSelection)
    {
        var selected = SelectedPaletteItem;
        PaletteItems.Clear();
        foreach (var item in _paletteMatched) PaletteItems.Add(item);
        foreach (var item in _paletteFiles) PaletteItems.Add(item);
        SelectedPaletteItem = keepSelection && selected is not null && PaletteItems.Contains(selected)
            ? selected
            : PaletteItems.FirstOrDefault(i => i.IsEnabled) ?? PaletteItems.FirstOrDefault();
        PaletteNotice = PaletteItems.Count > 0 ? ""
            : _paletteSearchingFiles ? Loc.T("Searching files…")
            : PaletteMode == PaletteMode.Files && ProjectFolder is null ? Loc.T("Open a project folder to search its files")
            : q.Length == 0 ? (PaletteMode == PaletteMode.Files ? Loc.T("Type part of a file name") : "")
            : Loc.F("Nothing matches “{0}”", q);
    }

    // ───────────────────────── Files ─────────────────────────

    /// <summary>
    /// The project's files for <paramref name="q"/>: after the same short pause as the Files pane's "Go to file", from
    /// the index it and the "@" menu share (read once per opening of the palette), searched off the UI thread. A newer
    /// key cancels the search before it.
    /// </summary>
    private void SearchPaletteFiles(string q)
    {
        _paletteFileSearch?.Cancel();
        _paletteSearchingFiles = false;
        if (q.Length == 0 || ProjectFolder is not { } root || _paletteSession is not { } session) return;
        var cts = _paletteFileSearch = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        _paletteSearchingFiles = true;
        _ = SearchPaletteFilesAsync(root, q, session.Token, cts.Token);
    }

    private async Task SearchPaletteFilesAsync(string root, string q, CancellationToken session, CancellationToken ct)
    {
        try
        {
            await Task.Delay(FilesViewModel.SearchDelay, ct);
            var index = await (_paletteIndex ??= LoadPaletteIndexAsync(root, session)).WaitAsync(ct);
            var max = PaletteMode == PaletteMode.Files ? PaletteFilesOnly : PaletteFilesInAll;
            var hits = await Task.Run(() => ProjectFiles.Search(index, q, max, ct), ct);
            if (ct.IsCancellationRequested) return;
            _paletteSearchingFiles = false;
            _paletteFiles = [.. hits.Select(h => PaletteFile(root, h.RelativePath, q))];
            ShowPaletteList(q, keepSelection: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (ct.IsCancellationRequested) return;
            _paletteSearchingFiles = false;
            ShowPaletteList(q, keepSelection: true);
            if (PaletteItems.Count == 0) PaletteNotice = Loc.F("Can't read the project's files: {0}", e.Message);
        }
    }

    private async Task<ProjectFiles.SearchIndex> LoadPaletteIndexAsync(string root, CancellationToken ct)
    {
        var paths = await Files.ProjectIndexAsync(root, ct);
        return await Task.Run(() => new ProjectFiles.SearchIndex(paths), ct);
    }

    /// <summary>A file of the project ('/'-separated, relative): its name, highlighted, and its folder.</summary>
    private PaletteItemViewModel PaletteFile(string root, string relative, string q)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var slash = relative.LastIndexOf('/');
        var name = slash < 0 ? relative : relative[(slash + 1)..];
        var positions = new List<int>();
        if (q.Length > 0 && PaletteMatch.Score(name, q, positions) < 0)
            PaletteMatch.Score(name, q.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1].Split('/')[^1], positions);
        return new PaletteItemViewModel("file:" + full, PaletteKind.File, name, () => OpenPaletteFileAsync(full))
        {
            Detail = slash < 0 ? "" : relative[..slash],
            Matches = [.. positions],
        };
    }

    /// <summary>Files mode with nothing typed: the files opened from the palette last, when still in this project.</summary>
    private List<PaletteItemViewModel> RecentPaletteFiles()
    {
        if (ProjectFolder is not { } root) return [];
        var files = new List<PaletteItemViewModel>();
        foreach (var id in PaletteRecent())
        {
            if (!id.StartsWith("file:", StringComparison.Ordinal)) continue;
            var full = id["file:".Length..];
            if (ProjectFiles.Relative(root, full) is { Length: > 0 } relative && File.Exists(full)) files.Add(PaletteFile(root, relative, ""));
        }
        return files;
    }

    /// <summary>In the Files tab (taking the new-tab page's place), selected in its tree.</summary>
    private Task OpenPaletteFileAsync(string fullPath)
    {
        LeaveSettings();
        if (IsNewTabShown) OpenTool(SidePane.Files);
        return Files.OpenLinkCommand.ExecuteAsync(new FileLink(fullPath));
    }

    // ───────────────────────── The entries ─────────────────────────

    /// <summary>Everything the palette offers but the files, in the order an empty palette lists them.</summary>
    private List<PaletteItemViewModel> PaletteEntries()
    {
        var chats = PaletteChats();
        var entries = new List<PaletteItemViewModel>();
        entries.AddRange(chats.Where(c => c.IsUnread));
        entries.AddRange(PaletteCommands());
        entries.AddRange(PalettePanels());
        // P5: the Browser page's open pages as one collection (MainViewModel.Share.cs)
        if (Browser.Tabs.Any(t => t.Url is { Scheme: "http" or "https" }))
            entries.Add(new PaletteItemViewModel("share:browser-pages", PaletteKind.Command, Loc.T("Share open browser pages with team…"), () =>
            {
                ShareBrowserTabsCommand.Execute(null);
                return Task.CompletedTask;
            }) { EnglishTitle = "Share open browser pages with team…" });
        foreach (var page in SettingsCategories)
        {
            var key = page.Key;
            entries.Add(new PaletteItemViewModel("settings:" + key, PaletteKind.Settings, Loc.F("Settings: {0}", page.Title), () => OpenSettingsAtAsync(key))
            {
                EnglishTitle = "Settings: " + Loc.Invariant(page.Title),
            });
        }
        foreach (var theme in Themes)
            entries.Add(new PaletteItemViewModel("theme:" + theme, PaletteKind.Theme, Loc.F("Theme: {0}", ThemeName(theme)), () => UsePaletteTheme(theme))
            {
                EnglishTitle = "Theme: " + Loc.Invariant(ThemeName(theme)),
            });
        entries.AddRange(chats.Where(c => !c.IsUnread));
        return entries;
    }

    private static string ThemeName(string theme) => theme switch
    {
        "light" => Loc.T("Light"),
        "dark" => Loc.T("Dark"),
        _ => Loc.T("System"),
    };

    /// <summary>The theme now, and kept (the settings page saves its own choice only while it is open).</summary>
    private Task UsePaletteTheme(string theme)
    {
        // The page's choice is read from the settings when the page opens: it may still say "system" while another shows
        if (SettingsTheme == theme) ApplyTheme(theme);
        else SetThemeCommand.Execute(theme);
        if (!IsSettingsOpen) Persist(o => o with { Theme = theme });
        return Task.CompletedTask;
    }

    /// <summary>The window's actions, each through its own command (and that command's reason not to run).</summary>
    private IEnumerable<PaletteItemViewModel> PaletteCommands()
    {
        var idle = Loc.T("Available when omp is idle");
        var readOnly = Loc.T("Not in a read-only chat");
        var unsaved = Loc.T("Available once the chat is saved");
        PaletteItemViewModel C(string id, string title, ICommand command, string? shortcut = null, string? reason = null) =>
            new(id, PaletteKind.Command, title, () => RunCommand(command))
            {
                EnglishTitle = Loc.Invariant(title),
                Shortcut = shortcut,
                CanRun = () => command.CanExecute(null),
                DisabledReason = reason,
            };
        return
        [
            C("new-chat", Loc.T("New chat"), NewChatCommand, CommandKey("N")),
            C("open-folder", Loc.T("Open folder…"), OpenFolderCommand, CommandKey("O")),
            C("find", Loc.T("Find in chat"), OpenFindCommand, CommandKey("F")),
            C("sidebar", Loc.T("Show or hide the sidebar"), ToggleSidebarCommand, CommandKey("B"), Loc.T("Available in Chats")),
            C("settings", Loc.T("Settings"), OpenSettingsCommand, CommandKey(",")),
            C("shortcuts", Loc.T("Keyboard shortcuts"), ToggleShortcutsCommand, CommandKey("/")),
            C("stop", Loc.T("Stop omp"), AbortCommand, "Esc", Loc.T("omp is not working")),
            C("dictate", Loc.T("Dictate"), DictateCommand, ShiftCommandKey("Space")),
            C("attach-images", Loc.T("Attach images"), AttachImagesCommand),
            C("rename", Loc.T("Rename"), StartRenameCommand, reason: idle),
            C("pin", PinLabel, TogglePinCommand, reason: idle),
            C("copy-id", Loc.T("Copy session ID"), CopySessionIdCommand, reason: unsaved),
            C("copy-file", Loc.T("Copy session file path"), CopySessionFileCommand, reason: unsaved),
            C("compact", Loc.T("Compact conversation…"), CompactConversationCommand, reason: idle),
            C("handoff", Loc.T("Hand off…"), HandOffCommand, reason: idle),
            C("fresh-provider", Loc.T("Fresh provider session"), FreshProviderSessionCommand, reason: idle),
            C("retry", Loc.T("Retry the last turn"), RetryLastTurnCommand, reason: idle),
            C("rewind", Loc.T("Rewind…"), RewindCommand, reason: idle),
            C("fork", Loc.T("Fork this session"), ForkSessionCommand, reason: idle),
            C("export", Loc.T("Export as HTML"), ExportHtmlCommand, reason: idle),
            C("share", Loc.T("Share a link…"), ShareSessionCommand, reason: idle),
            C("workspace-folders", Loc.T("Workspace folders…"), WorkspaceFoldersCommand, reason: idle),
            C("move", Loc.T("Move to another folder…"), MoveSessionCommand, reason: idle),
            C("memory", Loc.T("Memory"), ShowMemoryCommand, reason: idle),
            C("usage", Loc.T("Usage dashboard"), OpenUsageDashboardCommand, reason: idle),
            C("delete", Loc.T("Delete session…"), DeleteSessionCommand, reason: idle),
            C("new-shell", Loc.T("New shell"), NewShellCommand, reason: readOnly),
            C("omp-tui", Loc.T("omp TUI"), OpenOmpTuiCommand, reason: readOnly),
            C("comment-page", Loc.T("Comment on the page"), SelectElementCommand, ShiftCommandKey("S")),
            C("device-size", Loc.T("Device size in the preview"), ToggleDeviceModeCommand, ShiftCommandKey("M")),
            C("events", Loc.T("Events (for bug reports)"), ToggleDebugCommand, "F12"),
            C("check-updates", Loc.T("Check for updates"), CheckForUpdatesCommand),
            C("diagnostics", Loc.T("Save diagnostics…"), SaveDiagnosticsCommand),
        ];
    }

    /// <summary>The right panel's tools, the browser and the terminal: shown (never hidden, as their toggles would).</summary>
    private IEnumerable<PaletteItemViewModel> PalettePanels()
    {
        PaletteItemViewModel P(string id, string name, SidePane pane, string shortcut) =>
            new("panel:" + id, PaletteKind.Panel, Loc.F("Panel: {0}", name), () =>
            {
                LeaveSettings();
                if (IsNewTabShown) OpenTool(pane);
                else ShowPane(pane);
                return Task.CompletedTask;
            }) { Shortcut = shortcut, EnglishTitle = "Panel: " + Loc.Invariant(name) };
        return
        [
            P("files", Loc.T("Files"), SidePane.Files, ShiftCommandKey("F")),
            P("changes", Loc.T("Changes"), SidePane.Changes, ShiftCommandKey("G")),
            P("tasks", Loc.T("Background tasks"), SidePane.Tasks, ShiftCommandKey("T")),
            P("plan", Loc.T("Plan"), SidePane.Plan, ShiftCommandKey("P")),
            P("browser", Loc.T("Browser"), SidePane.Browser, ShiftCommandKey("B")),
            new("panel:terminal", PaletteKind.Panel, Loc.F("Panel: {0}", Loc.T("Terminal")), () =>
            {
                LeaveSettings();
                if (!IsTerminalOpen) ToggleTerminal();
                return Task.CompletedTask;
            }) { Shortcut = "Ctrl+`", EnglishTitle = "Panel: Terminal" },
        ];
    }

    /// <summary>The chats the sidebar lists (whatever its search shows now), the shown one left out: unread ones are
    /// marked, newest first.</summary>
    private List<PaletteItemViewModel> PaletteChats()
    {
        var shown = ShownFile;
        var opens = OpenChatsByFile();
        var rows = new Dictionary<string, SessionItemViewModel>(Sessions.Count, StringComparer.Ordinal);
        foreach (var row in Sessions) rows[row.Model.Path] = row;
        var now = DateTimeOffset.Now;
        var chats = new List<PaletteItemViewModel>();
        foreach (var c in ListableSessions().OrderByDescending(c => c.LastMessageAt))
        {
            if (shown is not null && SessionCatalog.SamePath(c.Path, shown)) continue;
            var (path, cwd) = (c.Path, c.Cwd);
            var title = rows.TryGetValue(path, out var row) ? row.Title : c.Title;
            chats.Add(new PaletteItemViewModel("chat:" + path, PaletteKind.Chat, title, () => OpenPaletteChatAsync(path, cwd))
            {
                Detail = SessionItemViewModel.ProjectName(cwd) + " · " + SessionItemViewModel.Relative(c.LastMessageAt, now),
                IsUnread = StatusOfFile(opens, path) == SessionStatus.Unread,
            });
        }
        return chats;
    }

    private Task OpenPaletteChatAsync(string file, string cwd)
    {
        LeaveSettings();
        SidebarShownWhileNarrow = false;
        return OpenSavedSessionAsync(file, cwd); // MainViewModel.OpenSessions.cs
    }

    /// <summary>A panel, chat or file chosen while the settings page is shown: the page gives way to the window.</summary>
    private void LeaveSettings()
    {
        if (IsSettingsOpen) CloseSettingsCommand.Execute(null);
    }

    private static async Task RunCommand(ICommand command)
    {
        if (!command.CanExecute(null)) return;
        if (command is IAsyncRelayCommand asyncCommand) await asyncCommand.ExecuteAsync(null);
        else command.Execute(null);
    }

    private static string ShiftCommandKey(string key) => OperatingSystem.IsMacOS() ? "⇧⌘" + key : "Ctrl+Shift+" + key;

    // ───────────────────────── Recent ─────────────────────────

    private List<string> PaletteRecent()
    {
        if (_paletteRecent is not null) return _paletteRecent;
        string[]? saved = null;
        try { saved = _settings?.Load().PaletteRecent; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // None remembered; the settings page reports the file's problem when it opens.
        }
        return _paletteRecent = [.. (saved ?? []).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).Take(PaletteRecentCount)];
    }

    private void RememberPaletteItem(string id)
    {
        var recent = PaletteRecent();
        recent.Remove(id);
        recent.Insert(0, id);
        if (recent.Count > PaletteRecentCount) recent.RemoveRange(PaletteRecentCount, recent.Count - PaletteRecentCount);
        string[] kept = [.. recent];
        Persist(o => o with { PaletteRecent = kept });
    }
}
