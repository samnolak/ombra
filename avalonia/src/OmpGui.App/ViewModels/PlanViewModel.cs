using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>One task of the plan: omp's todo item (pending, in_progress, completed, abandoned, blocked).</summary>
public sealed partial class PlanTaskViewModel(string content, string status, string? blocker, bool isCurrent) : ObservableObject
{
    public string Content { get; } = content;
    public string Status { get; } = status;
    public string? Blocker { get; } = blocker;
    public bool IsDone => Status is "completed" or "done";
    public bool IsActive => Status is "in_progress" or "active";
    public bool IsDropped => Status is "abandoned" or "cancelled" or "canceled";
    public bool IsBlocked => Status == "blocked";
    public bool IsOpen => !IsDone && !IsDropped;

    /// <summary>The task the agent works on now (the first in progress): emphasized, with the live activity under it.</summary>
    public bool IsCurrent { get; } = isCurrent;

    public string BlockerText => Blocker is { Length: > 0 } b ? "Waiting on: " + b : "Blocked";

    public string StatusName => Status switch
    {
        "completed" or "done" => "Done",
        "in_progress" or "active" => "In progress",
        "abandoned" or "cancelled" or "canceled" => "Dropped",
        "blocked" => "Blocked",
        _ => "To do",
    };

    public string AccessibleName => $"{Content} — {StatusName}";

    /// <summary>What the run does right now (the activity line), on the current task while omp works.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity))]
    private string _activity = "";

    public bool HasActivity => Activity.Length > 0;
}

public sealed record PlanPhaseViewModel(string Name, IReadOnlyList<PlanTaskViewModel> Tasks)
{
    public bool HasName => Name.Length > 0;
    public string Count => string.Create(CultureInfo.InvariantCulture, $"{Tasks.Count(t => t.IsDone)}/{Tasks.Count(t => !t.IsDropped)}");
}

/// <summary>A plan the agent replaced with a new one, or that was cleared: kept, collapsed, for the record.</summary>
public sealed partial class EarlierPlanViewModel(string when, string summary, IReadOnlyList<PlanPhaseViewModel> phases) : ObservableObject
{
    /// <summary>"Replaced at 14:32" / "Cleared at 14:40".</summary>
    public string When { get; } = when;
    /// <summary>"3 of 5 done".</summary>
    public string Summary { get; } = summary;
    public IReadOnlyList<PlanPhaseViewModel> Phases { get; } = phases;
    [ObservableProperty] private bool _isExpanded;
}

/// <summary>A change to the plan, as it happened ("14:32 · Completed · Read greet").</summary>
public sealed record PlanChangeViewModel(string Time, string What, string Content)
{
    public bool HasContent => Content.Length > 0;
}

/// <summary>
/// The Plan pane (Claude Code's plan with checkboxes): omp's todo list from <c>get_state.todoPhases</c>, grouped by
/// phase, with the progress and the task in progress. Nothing is lost when it changes: finished tasks stay, a plan
/// the agent replaces or clears moves under "Earlier plan", and every change is listed with its time (History).
/// The history is every list the todo tool committed in the session (its saved results, so a session opened later
/// has it too), plus changes omp reports otherwise (e.g. /todo), from when the session is open in this window.
/// </summary>
public sealed partial class PlanViewModel : ObservableObject
{
    private const int MaxChanges = 300;
    private const int MaxEarlierPlans = 20;
    /// <summary>How far back a list reported by get_state is compared: an older answer arriving late is no change.</summary>
    private const int StaleWindow = 5;
    private IReadOnlyList<TodoPhase>? _last;
    private IReadOnlyList<PlanSnapshot>? _recorded;
    /// <summary>The recorded lists taken into the timeline so far (to tell a reloaded transcript from a new one).</summary>
    private readonly List<List<TodoPhase>> _seenRecorded = [];
    private List<TodoPhase> _current = [];
    private string? _session;
    private bool _seen;
    private bool _observed;
    /// <summary>The plan's states in order: what the todo tool committed, and what get_state reported besides.</summary>
    private readonly List<Entry> _timeline = [];

    public PlanViewModel(MainViewModel? owner = null)
    {
        Owner = owner;
        if (owner is not null)
            owner.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.StatusText) or nameof(MainViewModel.Phase)) UpdateActivity();
            };
    }

    public MainViewModel? Owner { get; }

    public ObservableCollection<PlanPhaseViewModel> Phases { get; } = [];
    public ObservableCollection<EarlierPlanViewModel> EarlierPlans { get; } = [];
    /// <summary>Newest first.</summary>
    public ObservableCollection<PlanChangeViewModel> Changes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowEmptyHint))]
    private bool _hasPlan;

    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _totalCount;
    /// <summary>"3 of 7 done".</summary>
    [ObservableProperty] private string _progressText = "";
    /// <summary>"3/7" (the Views menu).</summary>
    [ObservableProperty] private string _shortProgress = "";
    /// <summary>0–100.</summary>
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isHistoryOpen;
    [ObservableProperty] private bool _hasHistory;
    [ObservableProperty] private string _historyTitle = "History";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyHint))]
    private bool _hasEarlierPlans;

    public bool IsEmpty => !HasPlan;

    /// <summary>No plan and nothing earlier either: the pane says when one appears.</summary>
    public bool ShowEmptyHint => !HasPlan && !HasEarlierPlans;

    [RelayCommand]
    private void ToggleHistory() => IsHistoryOpen = !IsHistoryOpen;

    /// <summary>The current list only (no recorded history), e.g. an omp that does not report todo results.</summary>
    public void Apply(IReadOnlyList<TodoPhase> todos, string? sessionKey, DateTimeOffset now) => Apply(todos, [], sessionKey, now);

    /// <summary>
    /// Takes the todo list of <paramref name="sessionKey"/> (omp's session file) and what the todo tool committed in
    /// it (<paramref name="recorded"/>, oldest first). Another session starts its own history.
    /// </summary>
    public void Apply(IReadOnlyList<TodoPhase> todos, IReadOnlyList<PlanSnapshot> recorded, string? sessionKey, DateTimeOffset now)
    {
        if (ReferenceEquals(todos, _last) && ReferenceEquals(recorded, _recorded) && sessionKey == _session && _seen) return;
        if (sessionKey != _session || !_seen)
        {
            // Another session (or the first look): its history starts again, from what it recorded
            _session = sessionKey;
            _seen = true;
            _observed = false;
            _timeline.Clear();
            _seenRecorded.Clear();
            _recorded = null;
            foreach (var e in EarlierPlans) e.IsExpanded = false;
        }
        var changed = false;
        // A transcript being replaced (omp restarting, the session reloading) is empty for a moment: that is no change
        if (!ReferenceEquals(recorded, _recorded) && !(recorded.Count == 0 && _seenRecorded.Count > 0))
        {
            _recorded = recorded;
            // A reloaded transcript records the same lists again: those seen already stay (with their live times) and
            // only the new ones are added. A history that went another way (rewound) is taken as it is now.
            var known = 0;
            while (known < Math.Min(recorded.Count, _seenRecorded.Count) && Same(NonEmpty(recorded[known].Phases), _seenRecorded[known])) known++;
            if (known < _seenRecorded.Count)
            {
                _timeline.Clear();
                _seenRecorded.Clear();
                _observed = false;
                known = 0;
            }
            foreach (var r in recorded.Skip(known))
            {
                var phases = NonEmpty(r.Phases);
                _timeline.Add(new Entry(r.At, phases));
                _seenRecorded.Add(phases);
            }
            changed = true;
        }
        if (!ReferenceEquals(todos, _last) || !_observed)
        {
            _last = todos;
            var phases = NonEmpty(todos);
            // What get_state reports besides the tool's results (a /todo edit, a cleared list). The first look is where
            // this window came in (no time); a late answer repeating a recent state is no change.
            if (!_observed)
            {
                if (phases.Count > 0 && (_timeline.Count == 0 || !Same(_timeline[^1].Phases, phases))) _timeline.Add(new Entry(null, phases));
                _observed = true;
            }
            else if (_timeline.Count == 0 ? phases.Count > 0 : !_timeline.TakeLast(StaleWindow).Any(e => Same(e.Phases, phases)))
                _timeline.Add(new Entry(now, phases));
            _current = phases;
            changed = true;
        }
        if (changed) Rebuild();
    }

    /// <summary>One state of the plan in the timeline, and when (null: already there when this window came in).</summary>
    private sealed record Entry(DateTimeOffset? At, List<TodoPhase> Phases);

    private static List<TodoPhase> NonEmpty(IEnumerable<TodoPhase> phases) => [.. phases.Where(p => p.Tasks.Count > 0)];

    /// <summary>The history from the timeline: each state against the one before it.</summary>
    private void Replay()
    {
        var expanded = EarlierPlans.Select((e, i) => (e.IsExpanded, FromOldest: EarlierPlans.Count - 1 - i)).Where(x => x.IsExpanded).Select(x => x.FromOldest).ToHashSet();
        EarlierPlans.Clear();
        Changes.Clear();
        List<TodoPhase> before = [];
        for (var i = 0; i < _timeline.Count; i++)
        {
            var after = _timeline[i].Phases;
            var at = _timeline[i].At;
            var time = Time(at);
            var was = Flatten(before);
            var now = Flatten(after);
            if (i == 0 && at is null) { before = after; continue; } // already there when this window came in
            if (was.Count > 0 && now.Count == 0)
            {
                Retire(before, "Cleared" + When(at));
                Log(time, "Plan cleared", "");
            }
            else if (was.Count > 0 && !now.Any(a => was.Any(b => b.Content == a.Content)))
            {
                // Nothing in common: the agent wrote a new plan; the old one stays readable below
                Retire(before, "Replaced" + When(at));
                Log(time, $"New plan · {Tasks(now.Count)}", "");
            }
            else if (was.Count == 0)
            {
                if (now.Count > 0) Log(time, $"New plan · {Tasks(now.Count)}", "");
            }
            else
            {
                foreach (var a in now)
                {
                    var b = was.FirstOrDefault(x => x.Content == a.Content);
                    if (b is null) Log(time, "Added", a.Content);
                    else if (b.Status != a.Status) Log(time, Verb(a.Status, b.Status), a.Content + (a.Status == "blocked" && a.Blocker is { Length: > 0 } w ? $" (waiting on: {w})" : ""));
                }
                foreach (var b in was.Where(b => now.All(a => a.Content != b.Content))) Log(time, "Removed", b.Content);
            }
            before = after;
        }
        for (var i = 0; i < EarlierPlans.Count; i++) EarlierPlans[i].IsExpanded = expanded.Contains(EarlierPlans.Count - 1 - i);
    }

    /// <summary>"14:32" today, "Sep 24 14:32" before; empty when unknown.</summary>
    private static string Time(DateTimeOffset? at)
    {
        if (at is not { } t) return "";
        var local = t.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
    }

    private static string When(DateTimeOffset? at) => at is null ? "" : " at " + Time(at);

    private static string Verb(string now, string was) => now switch
    {
        "completed" or "done" => "Completed",
        "in_progress" or "active" => was == "blocked" ? "Unblocked" : "Started",
        "abandoned" or "cancelled" or "canceled" => "Dropped",
        "blocked" => "Blocked",
        _ => was == "blocked" ? "Unblocked" : "Reopened",
    };

    private static string Tasks(int n) => n == 1 ? "1 task" : $"{n} tasks";

    private void Log(string time, string what, string content)
    {
        Changes.Insert(0, new PlanChangeViewModel(time, what, content));
        while (Changes.Count > MaxChanges) Changes.RemoveAt(Changes.Count - 1);
    }

    private void Retire(List<TodoPhase> plan, string when)
    {
        var (done, total) = Count(plan);
        EarlierPlans.Insert(0, new EarlierPlanViewModel(when, $"{done} of {total} done", Build(plan, current: false)));
        while (EarlierPlans.Count > MaxEarlierPlans) EarlierPlans.RemoveAt(EarlierPlans.Count - 1);
    }

    private void Rebuild()
    {
        Replay();
        Phases.Clear();
        foreach (var p in Build(_current, current: true)) Phases.Add(p);
        var (done, total) = Count(_current);
        DoneCount = done;
        TotalCount = total;
        HasPlan = _current.Any(p => p.Tasks.Count > 0);
        ProgressText = !HasPlan ? "" : $"{done} of {total} done";
        ShortProgress = !HasPlan ? "" : string.Create(CultureInfo.InvariantCulture, $"{done}/{total}");
        Progress = total == 0 ? 0 : 100.0 * done / total;
        HasEarlierPlans = EarlierPlans.Count > 0;
        // Nothing current: the last plan is open (a finished plan omp cleared is still there to read)
        if (!HasPlan && EarlierPlans.Count > 0 && EarlierPlans.All(e => !e.IsExpanded)) EarlierPlans[0].IsExpanded = true;
        HasHistory = Changes.Count > 0;
        HistoryTitle = Changes.Count == 0 ? "History" : $"History · {(Changes.Count == 1 ? "1 change" : $"{Changes.Count} changes")}";
        UpdateActivity();
    }

    /// <summary>
    /// Done of what is still to do: a dropped task counts neither way (struck through in the list, it had read as done in
    /// "3 of 6 done" while only two were).
    /// </summary>
    internal static (int Done, int Total) Count(IEnumerable<TodoTask> tasks)
    {
        var kept = tasks.Where(t => !IsDropped(t.Status)).ToList();
        return (kept.Count(t => IsDone(t.Status)), kept.Count);
    }

    private static (int Done, int Total) Count(IEnumerable<TodoPhase> plan) => Count(plan.SelectMany(p => p.Tasks));

    internal static bool IsDone(string? status) => status is "completed" or "done";

    internal static bool IsDropped(string? status) => status is "abandoned" or "cancelled" or "canceled";

    private static List<PlanPhaseViewModel> Build(List<TodoPhase> plan, bool current)
    {
        var active = current ? plan.SelectMany(p => p.Tasks).FirstOrDefault(t => t.Status is "in_progress" or "active") : null;
        // Phase names only when there is more than one (a single "Todos" phase is just the list)
        return [.. plan.Select(p => new PlanPhaseViewModel(plan.Count > 1 ? p.Name : "",
            [.. p.Tasks.Select(t => new PlanTaskViewModel(t.Content, t.Status, t.Blocker, ReferenceEquals(t, active)))]))];
    }

    private static List<TodoTask> Flatten(List<TodoPhase> plan) => [.. plan.SelectMany(p => p.Tasks)];

    private static bool Same(List<TodoPhase> a, List<TodoPhase> b) =>
        a.Count == b.Count && a.Zip(b).All(x => x.First.Name == x.Second.Name && x.First.Tasks.SequenceEqual(x.Second.Tasks));

    /// <summary>The current task carries what omp does now while a run is going.</summary>
    private void UpdateActivity()
    {
        var text = Owner is { IsRunning: true } o ? o.StatusText : "";
        foreach (var t in Phases.SelectMany(p => p.Tasks)) t.Activity = t.IsCurrent ? text : "";
    }
}
