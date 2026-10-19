using Avalonia.Threading;
using Ombra.ClientCore;
using Ombra.ClientCore.Teams;

namespace Ombra.App.ViewModels;

/// <summary>
/// P3 project progress (ViewModels/Team/TeamProgressViewModel; Views/Team/TeamProgressSection on the Team page,
/// Views/Settings/ProgressSettingsSection in Settings › Account): the local side — which projects this machine has, and
/// what its open sessions in each are doing — and the publisher's one-second tick.
/// </summary>
public partial class MainViewModel
{
    public static readonly TimeSpan ProgressTick = TimeSpan.FromSeconds(1);
    private DispatcherTimer? _progressTimer;

    public TeamProgressViewModel TeamProgress
    {
        get => field ??= NewTeamProgress(new TeamProgressContext(Team));
        private set
        {
            field = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Progress over another team context (the native feature smoke's in-memory team).</summary>
    internal void UseTeamProgress(ITeamProgressContext context) => TeamProgress = NewTeamProgress(context);

    private TeamProgressViewModel NewTeamProgress(ITeamProgressContext context)
    {
        var p = new TeamProgressViewModel(context, LocalProjects, LocalProgressOf);
        _progressTimer?.Stop();
        _progressTimer = new DispatcherTimer { Interval = ProgressTick };
        _progressTimer.Tick += (_, _) => _ = p.PublishAsync();
        _progressTimer.Start();
        return p;
    }

    /// <summary>At startup: the publisher runs only when this machine opted in to share some project (else no team request).</summary>
    private void StartTeamProgressIfShared()
    {
        var root = Environment.GetEnvironmentVariable("OMBRA_TEAMS_DIR") is { Length: > 0 } d ? d : TeamsDevice.DefaultRoot;
        if (File.Exists(Path.Combine(root, TeamSession.AccountFile)) && ProgressSettings.Load(root).Projects.Values.Any(b => b.Share)) _ = TeamProgress;
    }

    /// <summary>Local projects: the folders of the listed sessions and of the open ones (chats are not projects), named as in the sidebar.</summary>
    private IReadOnlyList<(string Path, string Name)> LocalProjects()
    {
        var paths = Sessions.Select(s => s.Cwd).Concat(_opens.Values.Select(o => o.Controller.Snapshot().Cwd ?? ""))
            .Where(p => p.Length > 0 && !Chats.Contains(p)).ToList();
        var distinct = new List<string>();
        foreach (var p in paths) if (!distinct.Any(x => SessionCatalog.SamePath(x, p))) distinct.Add(p);
        return [.. distinct.Select(p => (p, SessionGroups.FirstOrDefault(g => g.IsProject && SessionCatalog.SamePath(g.Cwd, p))?.Name
            ?? SessionItemViewModel.ProjectName(p))).OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// A project's state from its open sessions: waiting when one waits on the user, working when one runs, else idle;
    /// the task is the current plan step of the first session that has one (waiting ones first).
    /// </summary>
    private LocalProgress LocalProgressOf(string path)
    {
        var snaps = _opens.Values.Select(o => o.Controller.Snapshot()).Where(s => SessionCatalog.SamePath(s.Cwd, path)).ToList();
        var waiting = snaps.Where(s => s.Dialogs.Count > 0).ToList();
        var working = snaps.Where(s => s.Phase is SessionPhase.Running or SessionPhase.Aborting && s.Dialogs.Count == 0).ToList();
        var status = waiting.Count > 0 ? ProgressStatus.Waiting : working.Count > 0 ? ProgressStatus.Working : ProgressStatus.Idle;
        var task = status == ProgressStatus.Idle ? null : waiting.Concat(working).Select(s => CurrentTask(s.Todos)).FirstOrDefault(t => t is not null);
        return new LocalProgress(status, task);
    }

    private static string? CurrentTask(IReadOnlyList<TodoPhase> phases)
    {
        var tasks = phases.SelectMany(p => p.Tasks).ToList();
        return (tasks.FirstOrDefault(t => t.Status is "in_progress" or "active") ?? tasks.FirstOrDefault(t => t.Status is "pending" or "todo" or ""))?.Content;
    }
}
