using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>
/// Find in chat (⌘F / Ctrl+F): a query over the conversation's rows (<see cref="TranscriptFind"/>: every loaded row,
/// whether or not it is on screen), "3/17", the next and previous match. Going to a match opens what hides it — its
/// turn's "Worked for…" line, its step group, a tool's output, the thinking, a long message — only while it is the
/// match shown: the next match folds back what the last one opened. The view scrolls the match into view and selects it
/// (<see cref="FindRevealRequested"/>). While a reply streams the count follows it and the match shown stays the same,
/// without moving the view.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The find bar shows over the conversation.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindCounter), nameof(FindCounterName))]
    private bool _isFindOpen;

    /// <summary>What is looked for; kept when the bar closes (it opens again with it, selected).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindCounter), nameof(FindCounterName))]
    private string _findQuery = "";

    /// <summary>How many matches the loaded conversation has.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindCounter), nameof(FindCounterName), nameof(HasFindMatches))]
    private int _findTotal;

    /// <summary>The match shown, from 1; 0 when none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindCounter), nameof(FindCounterName))]
    private int _findCurrent;

    /// <summary>"3/17"; "0/0" when nothing matches; empty before anything is typed.</summary>
    public string FindCounter => string.IsNullOrEmpty(FindQuery) ? "" : $"{FindCurrent}/{FindTotal}";

    /// <summary>The counter in words, for screen readers.</summary>
    public string FindCounterName => string.IsNullOrEmpty(FindQuery) ? "" : FindTotal == 0 ? Loc.T("No matches") : Loc.F("Match {0} of {1}", FindCurrent, FindTotal);

    public bool HasFindMatches => FindTotal > 0;

    /// <summary>Raised when the bar opens: the view puts the keyboard in the query, its text selected.</summary>
    public event Action? FindFocusRequested;

    /// <summary>Raised with the match to bring into view and select; null when the bar closes (the selection goes).</summary>
    public event Action<FindMatch?>? FindRevealRequested;

    /// <summary>Asks the view for the row at the top of the conversation's view (an index into <see cref="Rows"/>), or -1
    /// while it follows the latest message: a new search starts from where the reader is.</summary>
    public event Func<int>? FindViewRowRequested;

    private List<FindMatch> _findMatches = [];
    private FindMatch? _findAt;
    /// <summary>The match the bar shows (null when none): the view selects it again when its text is rendered anew.</summary>
    internal FindMatch? FindShown => IsFindOpen ? _findAt : null;
    /// <summary>The row the next search starts from (the last match's, when the query changed or its match went).</summary>
    private RowViewModel? _findFrom;
    /// <summary>A match is to be shown as soon as there is one (opened with a query before the rows came).</summary>
    private bool _findRevealPending;
    private bool _findWired, _findRefreshPosted;
    /// <summary>What the match shown opened, and how to fold each back.</summary>
    private readonly List<(object Target, Action Fold)> _findUnfolded = [];

    /// <summary>
    /// Opens the find bar (⌘F) and gives it the keyboard. With a query, looks for it and shows the match nearest the
    /// view (on a chat just opened: the newest); rows still loading are searched as they come. Without one, the query
    /// typed last is searched again.
    /// </summary>
    public void OpenFindInChat(string? query = null)
    {
        WireFind();
        var wasOpen = IsFindOpen;
        IsFindOpen = true;
        FindFocusRequested?.Invoke();
        if (query is not null && !string.Equals(query, FindQuery, StringComparison.Ordinal))
        {
            _findAt = null;
            _findFrom = null;
            _findRevealPending = true;
            FindQuery = query; // OnFindQueryChanged searches
            return;
        }
        if (wasOpen && _findAt is not null) return;
        _findAt = null;
        _findFrom = null;
        _findRevealPending = !string.IsNullOrEmpty(FindQuery);
        RefreshFind(reveal: false);
    }

    [RelayCommand]
    private void OpenFind() => OpenFindInChat();

    /// <summary>Enter, ⌘G: the next match (after the last, the first).</summary>
    [RelayCommand]
    private void FindNext() => StepFind(1);

    /// <summary>Shift+Enter, ⇧⌘G: the previous match (before the first, the last).</summary>
    [RelayCommand]
    private void FindPrevious() => StepFind(-1);

    /// <summary>Esc in the bar: it closes, and the keyboard goes back to the message box. What the match shown opened
    /// stays open: the reader is looking at it.</summary>
    [RelayCommand]
    private void CloseFind()
    {
        if (!IsFindOpen) return;
        IsFindOpen = false;
        _findUnfolded.Clear();
        _findMatches = [];
        _findAt = null;
        _findFrom = null;
        _findRevealPending = false;
        FindTotal = 0;
        FindCurrent = 0;
        FindRevealRequested?.Invoke(null);
        FocusComposerRequested?.Invoke();
    }

    partial void OnFindQueryChanged(string value)
    {
        if (!IsFindOpen) return;
        // Typing on from a match keeps to it, or goes on from it
        _findFrom = _findAt?.Row ?? _findFrom;
        _findAt = null;
        RefreshFind(reveal: true);
    }

    private void StepFind(int by)
    {
        if (!IsFindOpen)
        {
            OpenFindInChat();
            return;
        }
        var count = _findMatches.Count;
        if (count == 0) return;
        var index = _findAt is { } at ? _findMatches.IndexOf(at) : -1;
        index = index < 0 ? StartIndex(_findMatches) : ((index + by) % count + count) % count;
        Show(index, reveal: true);
    }

    private void WireFind()
    {
        if (_findWired) return;
        _findWired = true;
        Rows.CollectionChanged += OnRowsChangedForFind;
        // After each applied snapshot: a streaming reply's row has new text (the count follows; nothing moves)
        TranscriptChanged += () =>
        {
            if (IsFindOpen) RefreshFind(reveal: false);
        };
    }

    private void OnRowsChangedForFind(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsFindOpen) return;
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Another chat: its rows, its own matches; what the last one opened belongs to the chat left
            _findUnfolded.Clear();
            _findAt = null;
            _findFrom = null;
            FindRevealRequested?.Invoke(null);
        }
        // Rows come by the hundred when a page loads: searched once they are in
        if (_findRefreshPosted) return;
        _findRefreshPosted = true;
        PostToUi(() =>
        {
            _findRefreshPosted = false;
            if (IsFindOpen) RefreshFind(reveal: false);
        });
    }

    /// <summary>Searches the rows again; the match shown stays the one shown (its row, its occurrence in it) when it is
    /// still there, else the nearest one is taken without moving the view.</summary>
    private void RefreshFind(bool reveal)
    {
        var matches = TranscriptFind.Search(Rows, TranscriptFind.Normalize(FindQuery));
        _findMatches = matches;
        var index = -1;
        if (_findAt is { } at)
        {
            index = matches.FindIndex(m => ReferenceEquals(m.Row, at.Row) && m.Occurrence == at.Occurrence);
            if (index < 0) _findFrom = at.Row;
        }
        if (index < 0 && matches.Count > 0) index = StartIndex(matches);
        FindTotal = matches.Count;
        Show(index, reveal || _findRevealPending);
    }

    private void Show(int index, bool reveal)
    {
        var match = index >= 0 && index < _findMatches.Count ? _findMatches[index] : null;
        _findAt = match;
        FindCurrent = index + 1;
        // No match (a query that finds nothing): the selection of the last one goes
        if (match is null && reveal) FindRevealRequested?.Invoke(null);
        if (match is null || !reveal) return;
        _findFrom = null;
        _findRevealPending = false;
        UnfoldFor(match);
        FindRevealRequested?.Invoke(match);
    }

    /// <summary>Where a search starts: the first match at or after the row it goes on from (the last match's, else the
    /// row at the top of the view); the newest match while the view follows the latest message or none comes after.</summary>
    private int StartIndex(List<FindMatch> matches)
    {
        var anchor = _findFrom is { } from ? Rows.IndexOf(from) : -1;
        if (anchor < 0) anchor = FindViewRowRequested?.Invoke() ?? -1;
        if (anchor < 0) return matches.Count - 1;
        var firstOf = new Dictionary<RowViewModel, int>(ReferenceEqualityComparer.Instance);
        for (var k = 0; k < matches.Count; k++) firstOf.TryAdd(matches[k].Row, k);
        for (var r = anchor; r < Rows.Count; r++)
            if (firstOf.TryGetValue(Rows[r], out var k)) return k;
        return matches.Count - 1;
    }

    /// <summary>Opens what hides a match and folds back what the match before opened (unless this one needs it too).</summary>
    private void UnfoldFor(FindMatch match)
    {
        var needed = new List<(object Target, bool IsOpen, Action Open, Action Fold)>();
        var row = match.Row;
        if (row.IsInTurn || row.Group is { IsInTurn: true })
        {
            for (var i = Rows.IndexOf(row) - 1; i >= 0; i--)
            {
                if (Rows[i] is TurnFoldRowViewModel fold)
                {
                    needed.Add((fold, fold.IsExpanded, () => fold.IsExpanded = true, () => { if (fold.IsExpanded) fold.IsExpanded = false; }));
                    break;
                }
                if (Rows[i] is UserRowViewModel) break;
            }
        }
        if (row.Group is { } group)
            needed.Add((group, group.IsExpanded, () => group.IsExpanded = true, () => { if (group.IsExpanded) group.IsExpanded = false; }));
        switch (row)
        {
            case ToolRowViewModel tool when tool.CanExpand:
                needed.Add((tool, tool.IsExpanded, () => tool.IsExpanded = true, () => { if (tool.IsExpanded) tool.IsExpanded = false; }));
                break;
            case AssistantRowViewModel reply when match.Field == FindField.Thinking:
                needed.Add((reply, reply.IsThinkingOpen, () => reply.IsThinkingOpen = true, () => { if (reply.IsThinkingOpen) reply.IsThinkingOpen = false; }));
                break;
            case UserRowViewModel user when user.IsLong:
                needed.Add((user, user.IsUnfolded, () => user.IsUnfolded = true, () => { if (user.IsUnfolded) user.IsUnfolded = false; }));
                break;
            case AgentMessageRowViewModel message:
                needed.Add((message, message.IsExpanded, () => message.IsExpanded = true, () => { if (message.IsExpanded) message.IsExpanded = false; }));
                break;
        }
        for (var i = _findUnfolded.Count - 1; i >= 0; i--)
        {
            var (target, fold) = _findUnfolded[i];
            if (needed.Exists(n => ReferenceEquals(n.Target, target))) continue;
            fold();
            _findUnfolded.RemoveAt(i);
        }
        foreach (var (target, isOpen, open, fold) in needed)
        {
            if (isOpen) continue;
            open();
            _findUnfolded.Add((target, fold));
        }
    }
}
