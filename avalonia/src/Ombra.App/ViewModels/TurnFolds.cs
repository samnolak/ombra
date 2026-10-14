using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>
/// Codex's "Worked for 1m 18s ›" over a finished turn: right under your message, a grey line over a hairline that folds
/// away how the turn got to its answer — the replies written along the way and every step (tool call) — and leaves the
/// answer, its actions and the files it changed under it. Opened, they show where they were. While the turn runs the line
/// reads "Activity", has no chevron and folds nothing. The folded rows stay rows of the conversation (their templates
/// hide them), as a step group's do. Built and kept up to date by <see cref="TurnFolds.Reconcile"/>.
/// </summary>
public sealed partial class TurnFoldRowViewModel : RowViewModel
{
    /// <summary>The turn's steps show under the line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChevron))]
    private bool _isExpanded;
    /// <summary>The turn still runs: "Activity", nothing folded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChevron))]
    private bool _isLive;
    /// <summary>"Worked for 1m 18s", "Stopped after 12s", "Activity".</summary>
    [ObservableProperty] private string _label = "";
    /// <summary>There is something to fold: replies along the way, or steps.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChevron))]
    private bool _canExpand;

    private HashSet<RowViewModel> _span = new(ReferenceEqualityComparer.Instance);

    /// <summary>A turn's line is keyed by its message, whose key it takes (it has no item of its own).</summary>
    internal TurnFoldRowViewModel(UserRowViewModel user) : base(user.Model) => User = user;

    /// <summary>The message that starts the turn: the line sits right after it.</summary>
    internal UserRowViewModel User { get; }

    /// <summary>The chevron that says the line opens: a finished turn with something folded.</summary>
    public bool ShowChevron => CanExpand && !IsLive;

    [RelayCommand]
    private void Toggle()
    {
        if (ShowChevron) IsExpanded = !IsExpanded;
    }

    /// <summary>What the turn's line says and folds (<see cref="TurnFolds.Reconcile"/>).</summary>
    internal void Set(bool live, string label, IReadOnlyList<RowViewModel> span)
    {
        // A turn that has just ended folds (Codex): what it did is one click away, the answer is what is read
        if (IsLive && !live) IsExpanded = false;
        IsLive = live;
        Label = label;
        CanExpand = span.Count > 0;
        foreach (var gone in _span)
            if (!span.Contains(gone) && gone.IsTurnFolded) gone.IsTurnFolded = false;
        _span = new HashSet<RowViewModel>(span, ReferenceEqualityComparer.Instance);
        Apply();
    }

    /// <summary>Shows every row it folded (the line goes away).</summary>
    internal void Release()
    {
        foreach (var row in _span)
            if (row.IsTurnFolded) row.IsTurnFolded = false;
        _span.Clear();
    }

    partial void OnIsExpandedChanged(bool value) => Apply();

    private void Apply()
    {
        var fold = !IsLive && !IsExpanded;
        foreach (var row in _span)
            if (row.IsTurnFolded != fold) row.IsTurnFolded = fold;
    }

    protected override void OnUpdated()
    {
    }
}

/// <summary>
/// Which rows a turn's "Worked for…" line folds, and what it says. A turn is the rows after one of your messages up to
/// the next. Folded: the other replies and the tool calls and their step lines. Not folded: the answer (the turn's last
/// reply with text), a reply that failed or was stopped, notices, command output and the turn's
/// end (its changed files). How long the turn took comes from its end (the run as the client saw it) or, in a loaded
/// history, from the times omp saved with the message and the last reply; unknown, the line says "Activity".
/// </summary>
internal static class TurnFolds
{
    /// <summary>
    /// Puts each turn's line right after its message, with what it says and folds. <paramref name="all"/>: every turn
    /// (rows came or went, a run started or ended); otherwise only the last one, the only turn a streaming reply changes.
    /// </summary>
    internal static void Reconcile(IList<RowViewModel> rows, bool running, bool all)
    {
        // A line whose message is no longer right before it goes (its rows show again)
        if (all)
            for (var i = rows.Count - 1; i >= 0; i--)
                if (rows[i] is TurnFoldRowViewModel f && (i == 0 || !ReferenceEquals(rows[i - 1], f.User)))
                {
                    f.Release();
                    rows.RemoveAt(i);
                }

        var end = rows.Count;
        var last = true;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i] is not UserRowViewModel user) continue;
            Turn(rows, user, i, end, running && last);
            end = i;
            if (!all) return;
            last = false;
        }
    }

    private static void Turn(IList<RowViewModel> rows, UserRowViewModel user, int at, int end, bool live)
    {
        var fold = at + 1 < end && rows[at + 1] is TurnFoldRowViewModel f && ReferenceEquals(f.User, user) ? f : null;
        var first = fold is null ? at + 1 : at + 2;

        // The answer: the last reply with text (calls after it, a last todo update say, fold with the rest)
        var answer = -1;
        for (var i = end - 1; i >= first && answer < 0; i--)
            if (rows[i] is AssistantRowViewModel { HasText: true }) answer = i;
        var span = new List<RowViewModel>();
        TurnEndRowViewModel? turnEnd = null;
        DateTimeOffset? lastAt = null;
        var any = false;
        for (var i = first; i < end; i++)
        {
            var row = rows[i];
            any = true;
            switch (row)
            {
                case ToolRowViewModel or StepGroupRowViewModel:
                    span.Add(row);
                    break;
                case AssistantRowViewModel a:
                    if (a.At is { } t && (lastAt is null || t > lastAt)) lastAt = t;
                    if (i != answer && !a.HasError && !a.HasFooter) span.Add(row);
                    break;
                case TurnEndRowViewModel te:
                    turnEnd = te;
                    break;
            }
        }

        TimeSpan? took = turnEnd?.Took ?? (user.At is { } sent && lastAt is { } replied && replied > sent ? replied - sent : null);
        var stopped = turnEnd?.Interrupted == true;
        var label = live ? Loc.T("Activity")
            : took is { } d ? Loc.F(stopped ? "Stopped after {0}" : "Worked for {0}", TurnEndRowViewModel.TurnDuration(d))
            : stopped ? Loc.T("Stopped")
            : Loc.T("Activity");
        // A line for a turn with steps, a finished turn whose length is known, or a running one that has begun
        var wanted = span.Count > 0 || (!live && took is not null) || (live && any);
        if (!wanted)
        {
            if (fold is not null)
            {
                fold.Release();
                rows.RemoveAt(at + 1);
            }
            return;
        }
        if (fold is null)
        {
            fold = new TurnFoldRowViewModel(user);
            rows.Insert(at + 1, fold);
        }
        fold.Set(live, label, span);
    }
}
