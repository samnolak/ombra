using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>Sidebar search inside the sessions' messages (<see cref="SessionContentIndex"/>): title and folder matches
/// list at once; a pause in typing then searches the messages off the UI thread, and the sessions found only there list
/// under them with the matching words. Each keystroke cancels the search before it.</summary>
public sealed partial class MainViewModel
{
    private SessionContentIndex? _contentIndex;
    private Func<string, IReadOnlyList<SessionSummary>, string, CancellationToken, Task<SessionContentSearchResult>>? _searchSessionContent;
    private CancellationTokenSource? _contentSearchCts;
    // The last completed search's matches by session file (the UI thread's only). Kept while the next one runs, so
    // typing does not blink the rows found in messages away and back.
    private Dictionary<string, SessionContentMatch> _contentMatches = new(StringComparer.Ordinal);

    /// <summary>The message search (the window's index by default; tests inject failures and gates).</summary>
    internal Func<string, IReadOnlyList<SessionSummary>, string, CancellationToken, Task<SessionContentSearchResult>> SearchSessionContentAsync
    {
        get => _searchSessionContent ??= (root, sessions, query, ct) =>
            (_contentIndex ??= new SessionContentIndex(_settings is null ? null : SessionContentIndex.PathFor(_settings.Path)))
            .SearchAsync(root, sessions, query, ct);
        set => _searchSessionContent = value;
    }

    /// <summary>The pause in typing before the messages are searched.</summary>
    internal TimeSpan ContentSearchDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    internal Task ContentSearchTask { get; private set; } = Task.CompletedTask;

    /// <summary>A message search is under way: the list does not say "no match" before it has answered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSessionMatch))]
    private bool _isSearchingContent;

    /// <summary>Some sessions could not be searched (the others' matches are listed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSessionSearchNotice))]
    private string? _sessionSearchNotice;
    public bool HasSessionSearchNotice => SessionSearchNotice is not null;

    /// <summary>Searches the listed sessions' messages for the sidebar's query, after <see cref="ContentSearchDelay"/>
    /// when <paramref name="debounce"/> (typing), at once otherwise (the list was read again).</summary>
    private void RequestContentSearch(bool debounce)
    {
        var previous = _contentSearchCts;
        _contentSearchCts = null;
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { } // that search has ended
        var query = SessionFilter.Trim();
        if (query.Length == 0 || IsClosing || _catalogRoot is not { } root)
        {
            if (query.Length == 0)
            {
                _contentMatches = new(StringComparer.Ordinal);
                SessionSearchNotice = null;
            }
            IsSearchingContent = false;
            return;
        }
        var sessions = _catalog;
        var search = SearchSessionContentAsync;
        var delay = debounce ? ContentSearchDelay : TimeSpan.Zero;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _contentSearchCts = cts;
        IsSearchingContent = true;
        ContentSearchTask = Task.Run(async () =>
        {
            var ct = cts.Token;
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
                SessionContentSearchResult? result = null;
                string? failure = null;
                try { result = await search(root, sessions, query, ct).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failure = e.Message; }
                await UiDispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested || !ReferenceEquals(_contentSearchCts, cts)) return;
                    _contentSearchCts = null;
                    IsSearchingContent = false;
                    if (result is null)
                    {
                        // The rows already listed stay: a failed search never empties the list.
                        SessionSearchNotice = Loc.F("Messages could not be searched: {0}", failure ?? "");
                    }
                    else
                    {
                        var matches = new Dictionary<string, SessionContentMatch>(result.Matches.Count, StringComparer.Ordinal);
                        foreach (var match in result.Matches) matches.TryAdd(match.Path, match);
                        _contentMatches = matches;
                        SessionSearchNotice = result.Errors.Count == 0 ? null
                            : Loc.F("Some sessions could not be searched: {0}", result.Errors[0].Message);
                    }
                    ApplySessionFilter();
                }, DispatcherPriority.Background, ct);
            }
            catch (OperationCanceledException) { }
            finally { cts.Dispose(); }
        });
    }

    /// <summary>The sidebar's rows for <paramref name="filter"/>: every candidate when it is empty; else the title and
    /// folder matches (pinned first, newest first), then the sessions found only in their messages, with the match.</summary>
    private List<(SessionSummary Summary, SessionContentMatch? Match)> FilterSessions(IEnumerable<SessionSummary> candidates, string filter)
    {
        var ordered = candidates.OrderByDescending(c => _pinnedIds.Contains(c.Id)).ThenByDescending(c => c.LastMessageAt);
        if (filter.Length == 0) return [.. ordered.Select(c => (c, (SessionContentMatch?)null))];
        var byTitle = new List<(SessionSummary, SessionContentMatch?)>();
        var byContent = new List<(SessionSummary, SessionContentMatch?)>();
        foreach (var c in ordered)
        {
            if (c.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || c.Cwd.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                byTitle.Add((c, null));
            else if (_contentMatches.TryGetValue(c.Path, out var match))
                byContent.Add((c, match));
        }
        byTitle.AddRange(byContent);
        return byTitle;
    }

    /// <summary>A row found in its messages was opened: the chat's find bar takes the query, at its newest match.</summary>
    private void RevealContentMatch(SessionItemViewModel item, string query)
    {
        if (query.Length == 0 || !SessionCatalog.SamePath(ShownFile, item.Model.Path)) return;
        OpenFindInChat(query); // MainViewModel.Find.cs
    }
}
