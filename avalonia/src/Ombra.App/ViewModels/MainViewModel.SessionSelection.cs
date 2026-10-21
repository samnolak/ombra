using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// Picking several sidebar chats at once: Select in a row's menu, or ⌘-click (Ctrl-click) and Shift-click, puts the
/// rows in selection mode, a check box on each; a click (Enter, Space) then toggles a row instead of opening it. The
/// bar over the list counts the rows, selects every listed one (what a search or a chip lists), deletes them after
/// one confirmation, or leaves (Cancel, Esc). Delete goes through the single delete's own path, chat by chat; a chat
/// that works, or that another window or a terminal omp holds, is skipped and reported, never stopped or taken over.
/// A search keeps only the selected rows it lists.
/// </summary>
public sealed partial class MainViewModel
{
    public SessionSelectionViewModel Selection { get; } = new();

    /// <summary>The selected rows' session files (rows are made again when a search drops and brings them back).</summary>
    private readonly HashSet<string> _selectedSessions = new(StringComparer.Ordinal);
    /// <summary>Where a Shift-click's range starts: the row clicked or picked last.</summary>
    private string? _selectionAnchor;
    /// <summary>The confirmation asking now, closed when selection mode is left.</summary>
    private SessionCardViewModel? _selectionDeleteCard;

    /// <summary>A row's click or Enter: in selection mode it toggles the row, otherwise it opens the chat.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ActivateSessionRowAsync(SessionItemViewModel? item)
    {
        if (item is not null && Selection.IsActive)
        {
            ToggleSessionSelection(item);
            return Task.CompletedTask;
        }
        return OpenSessionCommand.ExecuteAsync(item);
    }

    /// <summary>The row menu's Select: selection mode, this row selected.</summary>
    [RelayCommand(CanExecute = nameof(CanMutateSession))]
    private void SelectSessionItem(SessionItemViewModel? item)
    {
        if (item is null || IsReadOnly) return;
        Selection.IsActive = true;
        SetSelected(item, true);
        _selectionAnchor = item.Model.Path;
    }

    /// <summary>⌘-click (Ctrl-click), a click in selection mode: the row in or out (selection mode starts if it was off).</summary>
    internal bool ToggleSessionSelection(SessionItemViewModel item)
    {
        if (IsReadOnly) return false;
        Selection.IsActive = true;
        SetSelected(item, !item.IsSelected);
        _selectionAnchor = item.Model.Path;
        return true;
    }

    /// <summary>
    /// Shift-click: every listed row from the anchor (the row picked last; outside selection mode the chat shown) to
    /// <paramref name="item"/> is added. The anchor stays, so another Shift-click reaches from the same row.
    /// </summary>
    internal bool ExtendSessionSelection(SessionItemViewModel item)
    {
        if (IsReadOnly) return false;
        var rows = ListedRows();
        var to = rows.IndexOf(item);
        if (to < 0) return false;
        var anchor = Selection.IsActive ? _selectionAnchor : ShownFile;
        var from = anchor is null ? -1 : rows.FindIndex(r => r.Model.Path == anchor);
        if (from < 0) from = to;
        Selection.IsActive = true;
        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) SetSelected(rows[i], true, count: false);
        UpdateSelectionCount();
        _selectionAnchor = rows[from].Model.Path;
        return true;
    }

    /// <summary>The bar's Select all: every row the list shows now (a search or a status chip narrows it; a folded
    /// Archived group is not shown).</summary>
    [RelayCommand]
    private void SelectAllSessions()
    {
        if (IsReadOnly) return;
        Selection.IsActive = true;
        foreach (var row in ListedRows()) SetSelected(row, true, count: false);
        UpdateSelectionCount();
    }

    /// <summary>The bar's Cancel, Esc: selection mode ends, nothing is deleted.</summary>
    [RelayCommand]
    private void CancelSessionSelection()
    {
        if (_selectionDeleteCard is { IsAsking: true } asking && ReferenceEquals(SessionCard, asking)) CloseSessionCard();
        _selectionDeleteCard = null;
        _selectedSessions.Clear();
        foreach (var row in Sessions) row.IsSelected = false;
        _selectionAnchor = null;
        Selection.IsActive = false;
        UpdateSelectionCount();
    }

    private void SetSelected(SessionItemViewModel item, bool selected, bool count = true)
    {
        item.IsSelected = selected;
        if (selected) _selectedSessions.Add(item.Model.Path);
        else _selectedSessions.Remove(item.Model.Path);
        if (count) UpdateSelectionCount();
    }

    private void UpdateSelectionCount()
    {
        Selection.Count = _selectedSessions.Count;
        DeleteSelectedSessionsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>After the list changed (a search, a rescan, a deletion): only the rows it still lists stay selected.</summary>
    private void KeepListedSelection(IReadOnlyList<SessionItemViewModel> listed)
    {
        if (_selectedSessions.Count == 0) return;
        var paths = new HashSet<string>(listed.Count, StringComparer.Ordinal);
        foreach (var row in listed) paths.Add(row.Model.Path);
        if (_selectedSessions.RemoveWhere(p => !paths.Contains(p)) > 0) UpdateSelectionCount();
    }

    private bool CanDeleteSelected() => !IsReadOnly && Selection.Count > 0;

    /// <summary>The bar's Delete, a selected row's menu, Delete or Backspace in selection mode: asks once, naming how
    /// many chats and that their files go.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelectedSessions()
    {
        if (IsReadOnly) return;
        var chats = Sessions.Where(s => s.IsSelected).ToList();
        if (chats.Count == 0) return;
        var skipped = chats.Count(c => SkipReason(c) is not null);
        var message = Loc.T("Their conversation files and artifacts are deleted from disk. This cannot be undone.");
        if (skipped > 0)
            message += " " + Loc.Plural(skipped, "{0} of them is working or open in another window: it is skipped.",
                "{0} of them are working or open in another window: they are skipped.");
        var card = new SessionCardViewModel("delete", "IconTrash", Loc.Plural(chats.Count, "Delete {0} chat?", "Delete {0} chats?"), message)
        {
            Detail = string.Join("\n", chats.Select(c => c.Title)),
            IsDetailLong = chats.Count > 6,
            State = SessionCardState.Ask,
        };
        card.Primary = new SessionCardAction(Selection.DeleteLabel, new AsyncRelayCommand(() => DeleteSelectedAsync(card, chats)));
        card.Secondary = new SessionCardAction(Loc.T("Cancel"), new RelayCommand(CloseSessionCard));
        _selectionDeleteCard = card;
        ShowCard(card);
    }

    /// <summary>Why a chat open here is left alone: it works (deleting would stop the run), or another window or a
    /// terminal omp holds it (it is read-only here). Null for one that can go.</summary>
    private string? SkipReason(SessionItemViewModel chat)
    {
        if (FindOpen(chat.Model.Path) is not { } open) return null;
        var s = open.Controller.Snapshot();
        if (s.Lease is not null) return Loc.T("open in another window");
        return SessionHost.IsIdle(s) ? null : Loc.T("working");
    }

    private async Task DeleteSelectedAsync(SessionCardViewModel card, List<SessionItemViewModel> chats)
    {
        if (IsReadOnly) return;
        card.State = SessionCardState.Running;
        card.Message = Loc.T("Deleting…");
        card.Detail = "";
        card.IsDetailLong = false;
        card.Primary = card.Secondary = null;
        _selectionDeleteCard = null;
        CancelSessionSelection();
        var deleted = 0;
        var skipped = new List<(string Title, string Reason, string? Error)>();
        foreach (var chat in chats)
        {
            if (SkipReason(chat) is { } reason)
            {
                skipped.Add((chat.Title, reason, null));
                continue;
            }
            if (await DeleteListedChatAsync(chat) is { } error)
            {
                // The runtime refuses a chat another live writer holds ("Session is busy in another live writer"): a
                // window or a terminal omp that has it open, which this window does not see
                skipped.Add(error.Contains("busy", StringComparison.OrdinalIgnoreCase)
                    ? (chat.Title, Loc.T("open in another window"), null) : (chat.Title, Loc.T("failed"), error));
                continue;
            }
            deleted++;
        }
        RequestCatalogRefresh();
        // The card was the chat's on screen when it started: deleting that chat (a new one took its place) or moving to
        // another one left it with that chat. The outcome shows on the chat shown now.
        foreach (var o in _opens.Values)
            if (ReferenceEquals(o.Card, card)) o.Card = null;
        if (skipped.Count == 0)
        {
            ShowBrief("IconTrash", Loc.Plural(deleted, "Deleted {0} chat", "Deleted {0} chats"), "");
            return;
        }
        var reasons = string.Join(", ", skipped.Select(s => s.Reason).Distinct());
        card.Finish(deleted > 0,
            deleted > 0 ? Loc.F("Deleted {0}, {1} skipped: {2}", deleted, skipped.Count, reasons) : Loc.F("Nothing deleted, {0} skipped: {1}", skipped.Count, reasons),
            Loc.T("Stop the run, or close the chat in the other window, then delete it again."),
            string.Join("\n", skipped.Select(s => s.Error is null ? $"{s.Title} — {s.Reason}" : $"{s.Title} — {s.Reason}: {s.Error}")),
            secondary: new SessionCardAction(Loc.T("Dismiss"), new RelayCommand(CloseSessionCard)));
        if (!ReferenceEquals(SessionCard, card)) ShowCard(card);
    }
}
