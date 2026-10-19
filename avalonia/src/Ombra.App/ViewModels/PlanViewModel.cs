using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>
/// One task of the plan: omp's todo item (pending, in_progress, completed, abandoned, blocked). A task of the current
/// plan can be edited (<paramref name="editor"/>: its place is <paramref name="phase"/>, <paramref name="index"/>);
/// an earlier plan's tasks are only a record.
/// </summary>
public sealed partial class PlanTaskViewModel(string content, string status, string? blocker, bool isCurrent,
    PlanViewModel? editor = null, int phase = -1, int index = -1) : ObservableObject
{
    public string Content { get; } = content;
    public string Status { get; } = status;
    public string? Blocker { get; } = blocker;
    internal int Phase { get; } = phase;
    internal int Index { get; } = index;
    public bool IsDone => Status is "completed" or "done";
    public bool IsActive => Status is "in_progress" or "active";
    public bool IsDropped => Status is "abandoned" or "cancelled" or "canceled";
    public bool IsBlocked => Status == "blocked";
    public bool IsOpen => !IsDone && !IsDropped;

    /// <summary>The task the agent works on now (the first in progress): emphasized, with the live activity under it.</summary>
    public bool IsCurrent { get; } = isCurrent;

    public string BlockerText => Blocker is { Length: > 0 } b ? Loc.T("Waiting on: ") + b : Loc.T("Blocked");

    public string StatusName => Status switch
    {
        "completed" or "done" => Loc.T("Done"),
        "in_progress" or "active" => Loc.T("In progress"),
        "abandoned" or "cancelled" or "canceled" => Loc.T("Dropped"),
        "blocked" => Loc.T("Blocked"),
        _ => Loc.T("To do"),
    };

    public string AccessibleName => $"{Content} — {StatusName}";

    /// <summary>What the run does right now (the activity line), on the current task while omp works.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity))]
    private string _activity = "";

    public bool HasActivity => Activity.Length > 0;

    // ── Editing (the current plan only): each change goes to omp as the whole list (set_todos) ──

    /// <summary>The task can be changed here (a task of the current plan, with an omp session to send it to).</summary>
    public bool CanEdit => editor is { IsEditable: true };

    /// <summary>The text is being edited in place.</summary>
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = "";

    /// <summary>The box: done ↔ to do (a dropped task comes back as to do).</summary>
    [RelayCommand] private Task ToggleDone() => SetStatus(IsDone ? "pending" : "completed");

    /// <summary><paramref name="value"/>: pending, in_progress, completed or abandoned.</summary>
    [RelayCommand]
    private Task SetStatus(string? value) =>
        editor is not null && value is "pending" or "in_progress" or "completed" or "abandoned" && value != Status
            ? editor.ChangeAsync(this, t => t with { Status = value!, Blocker = null })
            : Task.CompletedTask;

    [RelayCommand] private Task MoveUp() => editor?.MoveAsync(this, -1) ?? Task.CompletedTask;
    [RelayCommand] private Task MoveDown() => editor?.MoveAsync(this, +1) ?? Task.CompletedTask;
    [RelayCommand] private Task Delete() => editor?.RemoveAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void StartEdit()
    {
        if (!CanEdit) return;
        EditText = Content;
        IsEditing = true;
    }

    [RelayCommand] private void CancelEdit() => IsEditing = false;

    /// <summary>The new text replaces the task's; empty or unchanged changes nothing.</summary>
    [RelayCommand]
    private Task CommitEdit()
    {
        if (!IsEditing) return Task.CompletedTask;
        IsEditing = false;
        var text = EditText.Trim();
        return editor is null || text.Length == 0 || text == Content ? Task.CompletedTask : editor.ChangeAsync(this, t => t with { Content = text });
    }
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

    /// <param name="save">Sends an edited list to omp (set_todos); by default the window's session. None: read-only.</param>
    public PlanViewModel(MainViewModel? owner = null, Func<IReadOnlyList<TodoPhase>, Task<bool>>? save = null)
    {
        Owner = owner;
        _save = save ?? (owner is null ? null : owner.SaveTodosAsync);
        if (owner is not null)
            owner.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.StatusText) or nameof(MainViewModel.Phase)) UpdateActivity();
            };
    }

    public MainViewModel? Owner { get; }

    // ── Editing: the user's changes to omp's list (set_todos). Nothing changes here until omp has taken the change:
    // the list shown is always omp's, and the change is in History like the agent's own. ──

    private readonly Func<IReadOnlyList<TodoPhase>, Task<bool>>? _save;

    /// <summary>A change is on its way to omp: until omp answers, no other change is taken (each builds on omp's list).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isSaving;

    /// <summary>The plan can be edited here (an omp session to send it to).</summary>
    public bool IsEditable => _save is not null;

    public bool CanEdit => IsEditable && !IsSaving;

    /// <summary>Why the last change did not reach omp.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditError))]
    private string _editError = "";

    public bool HasEditError => EditError.Length > 0;

    /// <summary>The "Add a task" box.</summary>
    [ObservableProperty] private string _newTaskText = "";

    /// <summary>Adds the box's text as a task to do at the end of the plan (a new plan when there is none).</summary>
    [RelayCommand]
    private Task AddTask()
    {
        var text = NewTaskText.Trim();
        if (text.Length == 0 || !CanEdit) return Task.CompletedTask;
        var plan = Editable();
        if (plan.Count == 0) plan.Add(("Todos", []));
        plan[^1].Tasks.Add(new TodoTask(text, "pending"));
        return SaveAsync(plan, () => NewTaskText = "");
    }

    internal Task ChangeAsync(PlanTaskViewModel task, Func<TodoTask, TodoTask> change) =>
        Edit(task, (tasks, i) => tasks[i] = change(tasks[i]));

    /// <summary>Moves a task up or down within its phase.</summary>
    internal Task MoveAsync(PlanTaskViewModel task, int by) =>
        Edit(task, (tasks, i) =>
        {
            var to = Math.Clamp(i + by, 0, tasks.Count - 1);
            if (to == i) return false;
            (tasks[i], tasks[to]) = (tasks[to], tasks[i]);
            return true;
        });

    internal Task RemoveAsync(PlanTaskViewModel task) => Edit(task, (tasks, i) => tasks.RemoveAt(i));

    private Task Edit(PlanTaskViewModel task, Action<List<TodoTask>, int> change) =>
        Edit(task, (tasks, i) => { change(tasks, i); return true; });

    private Task Edit(PlanTaskViewModel task, Func<List<TodoTask>, int, bool> change)
    {
        if (!CanEdit) return Task.CompletedTask;
        var plan = Editable();
        if (task.Phase < 0 || task.Phase >= plan.Count || task.Index < 0 || task.Index >= plan[task.Phase].Tasks.Count) return Task.CompletedTask;
        return change(plan[task.Phase].Tasks, task.Index) ? SaveAsync(plan, null) : Task.CompletedTask;
    }

    /// <summary>The current plan as lists to change (a phase left empty is dropped when sent).</summary>
    private List<(string Name, List<TodoTask> Tasks)> Editable() => [.. _current.Select(p => (p.Name, p.Tasks.ToList()))];

    private async Task SaveAsync(List<(string Name, List<TodoTask> Tasks)> plan, Action? saved)
    {
        IsSaving = true;
        try
        {
            var ok = await _save!([.. plan.Where(p => p.Tasks.Count > 0).Select(p => new TodoPhase(p.Name, p.Tasks))]);
            EditError = ok ? "" : Loc.T("The change did not reach omp. Try again.");
            if (ok) saved?.Invoke();
        }
        finally { IsSaving = false; }
    }

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
    /// <summary>"3/7" (the ⋯ menu and the summary).</summary>
    [ObservableProperty] private string _shortProgress = "";
    /// <summary>0–100.</summary>
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isHistoryOpen;
    [ObservableProperty] private bool _hasHistory;
    [ObservableProperty] private string _historyTitle = Loc.T("History");

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
                Retire(before, Loc.T("Cleared") + When(at));
                Log(time, Loc.T("Plan cleared"), "");
            }
            else if (was.Count > 0 && !now.Any(a => was.Any(b => b.Content == a.Content)))
            {
                // Nothing in common: the agent wrote a new plan; the old one stays readable below
                Retire(before, Loc.T("Replaced") + When(at));
                Log(time, Loc.F("New plan · {0}", Tasks(now.Count)), "");
            }
            else if (was.Count == 0)
            {
                if (now.Count > 0) Log(time, Loc.F("New plan · {0}", Tasks(now.Count)), "");
            }
            else
            {
                foreach (var a in now)
                {
                    var b = was.FirstOrDefault(x => x.Content == a.Content);
                    if (b is null) Log(time, Loc.T("Added"), a.Content);
                    else if (b.Status != a.Status) Log(time, Verb(a.Status, b.Status), a.Content + (a.Status == "blocked" && a.Blocker is { Length: > 0 } w ? Loc.F(" (waiting on: {0})", w) : ""));
                }
                foreach (var b in was.Where(b => now.All(a => a.Content != b.Content))) Log(time, Loc.T("Removed"), b.Content);
            }
            before = after;
        }
        for (var i = 0; i < EarlierPlans.Count; i++) EarlierPlans[i].IsExpanded = expanded.Contains(EarlierPlans.Count - 1 - i);
    }

    /// <summary>"14:32" today, "Sep 24 14:32" before (the month and day as the sidebar's dates, in the user's culture);
    /// empty when unknown.</summary>
    private static string Time(DateTimeOffset? at)
    {
        if (at is not { } t) return "";
        var local = t.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", culture)
            : local.ToString(culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM") + " HH:mm", culture);
    }

    private static string When(DateTimeOffset? at) => at is null ? "" : Loc.T(" at ") + Time(at);

    private static string Verb(string now, string was) => now switch
    {
        "completed" or "done" => Loc.T("Completed"),
        "in_progress" or "active" => was == "blocked" ? Loc.T("Unblocked") : Loc.T("Started"),
        "abandoned" or "cancelled" or "canceled" => Loc.T("Dropped"),
        "blocked" => Loc.T("Blocked"),
        _ => was == "blocked" ? Loc.T("Unblocked") : Loc.T("Reopened"),
    };

    private static string Tasks(int n) => n == 1 ? Loc.T("1 task") : Loc.F("{0} tasks", n);

    private void Log(string time, string what, string content)
    {
        Changes.Insert(0, new PlanChangeViewModel(time, what, content));
        while (Changes.Count > MaxChanges) Changes.RemoveAt(Changes.Count - 1);
    }

    private void Retire(List<TodoPhase> plan, string when)
    {
        var (done, total) = Count(plan);
        EarlierPlans.Insert(0, new EarlierPlanViewModel(when, Loc.F("{0} of {1} done", done, total), Build(plan, null)));
        while (EarlierPlans.Count > MaxEarlierPlans) EarlierPlans.RemoveAt(EarlierPlans.Count - 1);
    }

    private void Rebuild()
    {
        Replay();
        Phases.Clear();
        foreach (var p in Build(_current, this)) Phases.Add(p);
        var (done, total) = Count(_current);
        DoneCount = done;
        TotalCount = total;
        HasPlan = _current.Any(p => p.Tasks.Count > 0);
        ProgressText = !HasPlan ? "" : Loc.F("{0} of {1} done", done, total);
        ShortProgress = !HasPlan ? "" : string.Create(CultureInfo.InvariantCulture, $"{done}/{total}");
        Progress = total == 0 ? 0 : 100.0 * done / total;
        HasEarlierPlans = EarlierPlans.Count > 0;
        // Nothing current: the last plan is open (a finished plan omp cleared is still there to read)
        if (!HasPlan && EarlierPlans.Count > 0 && EarlierPlans.All(e => !e.IsExpanded)) EarlierPlans[0].IsExpanded = true;
        HasHistory = Changes.Count > 0;
        HistoryTitle = Changes.Count == 0 ? Loc.T("History") : Loc.Plural(Changes.Count, "History · {0} change", "History · {0} changes");
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

    /// <summary>The view of a plan; <paramref name="editor"/> for the current one (its tasks can be edited and one is current).</summary>
    private static List<PlanPhaseViewModel> Build(List<TodoPhase> plan, PlanViewModel? editor)
    {
        var active = editor is not null ? plan.SelectMany(p => p.Tasks).FirstOrDefault(t => t.Status is "in_progress" or "active") : null;
        // Phase names only when there is more than one (a single "Todos" phase is just the list)
        return [.. plan.Select((p, pi) => new PlanPhaseViewModel(plan.Count > 1 ? p.Name : "",
            [.. p.Tasks.Select((t, ti) => new PlanTaskViewModel(t.Content, t.Status, t.Blocker, ReferenceEquals(t, active), editor, pi, ti))]))];
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
