using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>
/// What the chat changed and what runs beside it, from the chat's own rows and panes: the header's summary popover
/// (project, Changes +N −M, background processes, the plan) and the new-tab page's Recent list. Read when either is
/// shown, and again as the conversation changes while one is.
/// </summary>
public partial class MainViewModel
{
    /// <summary>How many recent files the new-tab page lists.</summary>
    private const int RecentFileCount = 8;

    /// <summary>The files this chat's tool calls changed, the one changed last first.</summary>
    public IReadOnlyList<TurnFileViewModel> SessionFiles { get; private set; } = [];

    /// <summary>The new-tab page's Recent: the files changed last.</summary>
    public IReadOnlyList<TurnFileViewModel> RecentFiles { get; private set; } = [];

    public bool HasSessionFiles => SessionFiles.Count > 0;
    public string SessionAddedText { get; private set; } = "";
    public string SessionRemovedText { get; private set; } = "";
    public string SessionFilesText => SessionFiles.Count == 1 ? Loc.T("1 file changed") : Loc.F("{0} files changed", SessionFiles.Count);

    /// <summary>The header's summary popover is open: it follows the conversation.</summary>
    [ObservableProperty] private bool _isSummaryOpen;

    /// <summary>A terminal tab's process still runs (the summary lists it as a background process).</summary>
    public bool HasLiveTerminals => Terminals.Any(t => !t.HasExited);

    /// <summary>Nothing runs in the background: no subagent or job, no terminal.</summary>
    public bool HasNoBackgroundWork => !Tasks.HasRunning && !HasLiveTerminals;

    /// <summary>The rows (and their count, and how many tools had ended) the changes were last read from.</summary>
    private (object? Rows, int Count, int Done) _changesRead;
    private bool _changesFollowed;

    partial void OnIsSummaryOpenChanged(bool value)
    {
        if (!value) return;
        Tasks.OnMenuOpened();
        RefreshSessionChanges();
    }

    /// <summary>Reads the chat's changed files again when its rows changed (a tool that came or ended).</summary>
    internal void RefreshSessionChanges()
    {
        if (!_changesFollowed)
        {
            _changesFollowed = true;
            TranscriptChanged += () => { if (IsSummaryOpen || IsNewTabShown) RefreshSessionChanges(); };
        }
        var rows = Rows;
        var tools = rows.OfType<ToolRowViewModel>().ToList();
        var read = ((object?)rows, rows.Count, tools.Count(t => !t.IsRunning));
        if (read == _changesRead) return;
        _changesRead = read;
        SessionFiles = TurnEndRowViewModel.ChangedFiles(tools, latestFirst: true);
        RecentFiles = [.. SessionFiles.Take(RecentFileCount)];
        var added = SessionFiles.Sum(f => f.Added);
        var removed = SessionFiles.Sum(f => f.Removed);
        SessionAddedText = $"+{added}";
        SessionRemovedText = $"−{removed}";
        OnPropertyChanged(nameof(SessionFiles));
        OnPropertyChanged(nameof(RecentFiles));
        OnPropertyChanged(nameof(HasSessionFiles));
        OnPropertyChanged(nameof(SessionAddedText));
        OnPropertyChanged(nameof(SessionRemovedText));
        OnPropertyChanged(nameof(SessionFilesText));
    }

    /// <summary>A recent file (the new-tab page): opened in the Files tab, which takes the page's place, at its first change.</summary>
    [RelayCommand]
    private Task OpenRecentFile(TurnFileViewModel? file)
    {
        if (file is null) return Task.CompletedTask;
        if (IsNewTabShown) OpenTool(SidePane.Files);
        return Files.OpenLinkCommand.ExecuteAsync(file.Link);
    }
}
