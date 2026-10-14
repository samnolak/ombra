using CommunityToolkit.Mvvm.ComponentModel;
using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

/// <summary>
/// Codex's step row: a run of tool calls in one turn as one grey line that says what they did ("Read files, ran
/// commands"), opened to show each call on its own line. The steps stay rows of the conversation, right after this one:
/// folded, they take no room (their template hides them), so everything that walks the rows (the Files pane, the turn
/// ends, copying) still finds every tool row. Built and kept up to date by <see cref="StepGroups.Regroup"/>.
/// </summary>
public sealed partial class StepGroupRowViewModel : RowViewModel
{
    /// <summary>The steps show under the line.</summary>
    [ObservableProperty] private bool _isExpanded;
    /// <summary>What the steps did ("Read files, ran a command"); while one runs, that step ("Running npm test").</summary>
    [ObservableProperty] private string _summary = "";
    /// <summary>The icon of the line: the kind of its steps when they are all one kind, else of the first.</summary>
    [ObservableProperty] private StepKind _kind;
    [ObservableProperty] private bool _isRunning;

    private IReadOnlyList<RowViewModel> _span = [];

    /// <summary>A group is keyed by its first step, whose key it takes (it has no item of its own).</summary>
    internal StepGroupRowViewModel(ToolRowViewModel first) : base(first.Model) => Leader = first;

    /// <summary>The first step: the group's line sits right before it.</summary>
    internal ToolRowViewModel Leader { get; }

    /// <summary>The steps, in order.</summary>
    public IReadOnlyList<ToolRowViewModel> Steps { get; private set; } = [];

    public bool IsDone => !IsRunning;

    /// <summary>The rows the group covers, its first step to its last (with the replies between them), and what they say.</summary>
    internal void SetSpan(IReadOnlyList<RowViewModel> span)
    {
        _span = span;
        Steps = [.. span.OfType<ToolRowViewModel>()];
        var running = Steps.LastOrDefault(s => s.IsRunning);
        IsRunning = running is not null;
        Kind = running?.Kind ?? Steps[0].Kind;
        Summary = running is not null ? $"{running.Verb} {running.ArgText}".Trim() : StepKinds.Describe(Steps);
        OnPropertyChanged(nameof(IsDone));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        foreach (var row in _span) row.IsGroupFolded = !value;
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsDone));

    protected override void OnUpdated()
    {
    }
}

/// <summary>
/// Which conversation rows make a group of steps. Steps are tool calls that run or succeeded; two or more in a row
/// inside one turn make a group, with nothing between them but replies with nothing to read (only tool calls, or only
/// thinking). Anything else ends it: a reply with text (Codex's narration between step rows), a failed or stopped call
/// (it stays a line of its own, where its failure shows), a notice, your next message. A lone step stays a row of its own.
/// <para>
/// The rows are reconciled in place, as the conversation's rows are: a group's line is inserted right before its first
/// step when the second one comes, kept (with its open or folded state) while steps join, and removed if the group falls
/// back to one step (the second failed). Only the run around the rows that changed is looked at, so a reply streaming in
/// after a long conversation costs nothing here; a history loaded at once is grouped as the live run was.
/// </para>
/// </summary>
internal static class StepGroups
{
    /// <summary>A row whose change may change the groups: a tool call (whatever its state), or a row in a group.</summary>
    internal static bool Matters(RowViewModel row) => row is ToolRowViewModel || row.Group is not null;

    private static bool IsStep(RowViewModel row) => row is ToolRowViewModel { IsFailed: false, IsInterrupted: false };

    /// <summary>A reply a group passes over: nothing to read in it (only tool calls, or thinking).</summary>
    private static bool PassesOver(RowViewModel row) => row is AssistantRowViewModel { HasText: false, HasError: false, HasFooter: false };

    /// <summary>The index of <paramref name="row"/>, looked for from the end (the rows that change are the last ones).</summary>
    internal static int IndexFromEnd(IList<RowViewModel> rows, RowViewModel row)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
            if (ReferenceEquals(rows[i], row)) return i;
        return -1;
    }

    /// <summary>Groups the rows again from the run that holds the row at <paramref name="from"/> to the end.</summary>
    internal static void Regroup(IList<RowViewModel> rows, int from)
    {
        var start = Math.Clamp(from, 0, rows.Count);
        while (start > 0 && rows[start - 1] is var before && (before is StepGroupRowViewModel || IsStep(before) || PassesOver(before))) start--;

        // The groups the rows make now, each as the rows it covers
        var existing = new Dictionary<RowViewModel, StepGroupRowViewModel>(ReferenceEqualityComparer.Instance);
        var wanted = new List<List<RowViewModel>>();
        List<RowViewModel>? run = null;
        int steps = 0, covered = 0;
        for (var i = start; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row is StepGroupRowViewModel g) existing[g.Leader] = g;
            else if (IsStep(row))
            {
                (run ??= []).Add(row);
                steps++;
                covered = run.Count;
            }
            else if (run is not null && PassesOver(row)) run.Add(row);
            else Close();
        }
        Close();
        if (existing.Count == 0 && wanted.Count == 0) return;

        // Lines kept for the same first step (with their state), new ones before their first step, the others removed
        var groupOf = new Dictionary<RowViewModel, StepGroupRowViewModel>(ReferenceEqualityComparer.Instance);
        var groups = new List<(StepGroupRowViewModel Group, List<RowViewModel> Span)>(wanted.Count);
        foreach (var span in wanted)
        {
            var leader = (ToolRowViewModel)span[0];
            if (!existing.Remove(leader, out var group)) group = new StepGroupRowViewModel(leader);
            groups.Add((group, span));
            foreach (var row in span) groupOf[row] = group;
        }
        for (var i = rows.Count - 1; i >= start; i--)
            if (rows[i] is StepGroupRowViewModel g && existing.ContainsKey(g.Leader)) rows.RemoveAt(i);
        foreach (var (group, span) in groups)
            if (IndexFromEnd(rows, group) < 0)
            {
                // A group born inside a folded turn is folded with it
                var at = IndexFromEnd(rows, span[0]);
                group.IsTurnFolded = rows[at].IsTurnFolded;
                rows.Insert(at, group);
            }

        for (var i = start; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row is StepGroupRowViewModel or TurnFoldRowViewModel) continue;
            var group = groupOf.GetValueOrDefault(row);
            if (!ReferenceEquals(row.Group, group)) row.Group = group;
            var folded = group is { IsExpanded: false };
            if (row.IsGroupFolded != folded) row.IsGroupFolded = folded;
        }
        foreach (var (group, span) in groups) group.SetSpan(span);

        void Close()
        {
            if (run is not null && steps >= 2) wanted.Add(run.GetRange(0, covered));
            run = null;
            steps = covered = 0;
        }
    }
}
