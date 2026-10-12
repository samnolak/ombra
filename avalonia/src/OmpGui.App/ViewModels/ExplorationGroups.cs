using CommunityToolkit.Mvvm.ComponentModel;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// Codex's "Explored": a run of reads and searches in one turn as one line ("Explored 3 files, 2 searches"), opened to
/// show them. The steps stay rows of the conversation, right after this one: folded, they take no room (their template
/// hides them), so everything that walks the rows (the Files pane, the turn ends, copying) still finds every tool row.
/// Built and kept up to date by <see cref="ExplorationGroups.Regroup"/>.
/// </summary>
public sealed partial class ExplorationGroupRowViewModel : RowViewModel
{
    /// <summary>The steps show under the line.</summary>
    [ObservableProperty] private bool _isExpanded;
    /// <summary>"Explored" once every step ended, "Exploring" while one runs.</summary>
    [ObservableProperty] private string _verb = "";
    /// <summary>"2 files, 1 search"; while a step runs, that step ("Reading src/app.py").</summary>
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isRunning;

    private IReadOnlyList<RowViewModel> _span = [];

    /// <summary>A group is keyed by its first step, whose key it takes (it has no item of its own).</summary>
    internal ExplorationGroupRowViewModel(ToolRowViewModel first) : base(first.Model) => Leader = first;

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
        Verb = IsRunning ? "Exploring" : "Explored";
        Summary = running is not null ? $"{running.Verb} {running.ArgText}".Trim() : Counts(Steps);
        OnPropertyChanged(nameof(IsDone));
    }

    /// <summary>"3 files, 2 searches, 1 listing": files read (each once), searches, listings and lookups.</summary>
    private static string Counts(IReadOnlyList<ToolRowViewModel> steps)
    {
        var files = steps.Where(s => s.Name == "read").Select(s => s.Summary).Distinct(StringComparer.Ordinal).Count();
        var searches = steps.Count(s => s.Name is "grep" or "ast_grep" or "glob" or "find");
        var listings = steps.Count(s => s.Name == "ls");
        var lookups = steps.Count(s => s.Name == "lsp");
        var parts = new List<string>(4);
        if (files > 0) parts.Add(files == 1 ? "1 file" : $"{files} files");
        if (searches > 0) parts.Add(searches == 1 ? "1 search" : $"{searches} searches");
        if (listings > 0) parts.Add(listings == 1 ? "1 listing" : $"{listings} listings");
        if (lookups > 0) parts.Add(lookups == 1 ? "1 lookup" : $"{lookups} lookups");
        return string.Join(", ", parts);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        foreach (var row in _span) row.IsFolded = !value;
    }

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsDone));

    protected override void OnUpdated()
    {
    }
}

/// <summary>
/// Which conversation rows make an exploration group. Steps are read, grep, glob, find, ls, lsp and ast_grep calls that
/// run or succeeded; two or more in a row inside one turn make a group, with nothing between them but replies with nothing
/// to read (only tool calls, or only thinking). Anything else ends it: a reply with text, a failed or stopped step,
/// another tool, a notice, your next message. A lone step stays a row of its own.
/// <para>
/// The rows are reconciled in place, as the conversation's rows are: a group's line is inserted right before its first
/// step when the second one comes, kept (with its open or folded state) while steps join, and removed if the group falls
/// back to one step (the second failed). Only the run around the rows that changed is looked at, so a reply streaming in
/// after a long conversation costs nothing here; a history loaded at once is grouped as the live run was.
/// </para>
/// </summary>
internal static class ExplorationGroups
{
    private static readonly HashSet<string> StepTools = new(StringComparer.Ordinal) { "read", "grep", "glob", "find", "ls", "lsp", "ast_grep" };

    /// <summary>A row whose change may change the groups: an exploration call (whatever its state), or a row in a group.</summary>
    internal static bool Matters(RowViewModel row) => row is ToolRowViewModel t && StepTools.Contains(t.Name) || row.Group is not null;

    private static bool IsStep(RowViewModel row) => row is ToolRowViewModel { IsFailed: false, IsInterrupted: false } t && StepTools.Contains(t.Name);

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
        while (start > 0 && rows[start - 1] is var before && (before is ExplorationGroupRowViewModel || IsStep(before) || PassesOver(before))) start--;

        // The groups the rows make now, each as the rows it covers
        var existing = new Dictionary<RowViewModel, ExplorationGroupRowViewModel>(ReferenceEqualityComparer.Instance);
        var wanted = new List<List<RowViewModel>>();
        List<RowViewModel>? run = null;
        int steps = 0, covered = 0;
        for (var i = start; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row is ExplorationGroupRowViewModel g) existing[g.Leader] = g;
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
        var groupOf = new Dictionary<RowViewModel, ExplorationGroupRowViewModel>(ReferenceEqualityComparer.Instance);
        var groups = new List<(ExplorationGroupRowViewModel Group, List<RowViewModel> Span)>(wanted.Count);
        foreach (var span in wanted)
        {
            var leader = (ToolRowViewModel)span[0];
            if (!existing.Remove(leader, out var group)) group = new ExplorationGroupRowViewModel(leader);
            groups.Add((group, span));
            foreach (var row in span) groupOf[row] = group;
        }
        for (var i = rows.Count - 1; i >= start; i--)
            if (rows[i] is ExplorationGroupRowViewModel g && existing.ContainsKey(g.Leader)) rows.RemoveAt(i);
        foreach (var (group, span) in groups)
            if (IndexFromEnd(rows, group) < 0) rows.Insert(IndexFromEnd(rows, span[0]), group);

        for (var i = start; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row is ExplorationGroupRowViewModel) continue;
            var group = groupOf.GetValueOrDefault(row);
            if (!ReferenceEquals(row.Group, group)) row.Group = group;
            var folded = group is { IsExpanded: false };
            if (row.IsFolded != folded) row.IsFolded = folded;
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
