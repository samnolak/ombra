using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ombra.App.Localization;
using Ombra.ClientCore.Teams;

namespace Ombra.App.ViewModels;

/// <summary>A local project's current state as this machine sees it (from its open sessions).</summary>
public readonly record struct LocalProgress(ProgressStatus Status, string? Task);

/// <summary>One member device's progress on a team project (Team page).</summary>
public sealed class ProgressRow(ProgressEntry entry, string member)
{
    public ProgressEntry Entry { get; } = entry;
    public string Member { get; } = member;
    public string Project => Entry.ProjectName.Length > 0 ? Entry.ProjectName : TeamProgressViewModel.ShortProject(Entry.ProjectId);
    public string StatusText => TeamProgressViewModel.StatusText(Entry.Status);
    public bool IsWorking => Entry.Status == ProgressStatus.Working;
    public bool IsWaiting => Entry.Status == ProgressStatus.Waiting;
    public bool IsIdle => Entry.Status == ProgressStatus.Idle;
    public string? Task => Entry.Task;
    public bool HasTask => !string.IsNullOrEmpty(Entry.Task);
    public string Updated => SessionItemViewModel.Relative(Entry.Updated, DateTimeOffset.Now);
    public bool IsMine => Entry.IsMine;
}

/// <summary>A choice of the "Team project" box: none, or one of the team projects this device is in.</summary>
public sealed record ProgressProjectChoice(TeamProject? Project, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Settings › Account: one local project, the team project it belongs to and its two opt-in switches.</summary>
public sealed partial class ProgressProjectRow : ObservableObject
{
    private readonly TeamProgressViewModel _owner;
    private bool _loading;

    public ProgressProjectRow(TeamProgressViewModel owner, string path, string name)
    {
        _owner = owner;
        Path = path;
        Name = name;
    }

    public string Path { get; }
    /// <summary>The folder for reading: the home folder as ~ (the full path is the tooltip).</summary>
    public string ShownPath => GitProbe.Tilde(Path);
    public string Name { get; }
    public ObservableCollection<ProgressProjectChoice> Choices { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBound))] private ProgressProjectChoice? _selected;
    [ObservableProperty] private bool _share;
    [ObservableProperty] private bool _shareTask;

    public bool IsBound => Selected?.Project is not null;
    public bool CanCreate => _owner.CanCreateProjects && !IsBound;

    internal void Load(IReadOnlyList<ProgressProjectChoice> choices, ProgressBinding? binding)
    {
        _loading = true;
        Choices.Clear();
        foreach (var c in choices) Choices.Add(c);
        Selected = Choices.FirstOrDefault(c => c.Project is { } p && binding is not null && p.GroupId == binding.GroupId) ?? Choices[0];
        Share = binding is not null && IsBound && binding.Share;
        ShareTask = binding is not null && IsBound && binding.ShareTask;
        _loading = false;
        OnPropertyChanged(nameof(CanCreate));
    }

    partial void OnSelectedChanged(ProgressProjectChoice? value)
    {
        if (!IsBound) { Share = false; ShareTask = false; }
        if (!_loading) _owner.SaveBinding(this);
        OnPropertyChanged(nameof(CanCreate));
    }

    partial void OnShareChanged(bool value)
    {
        if (!value) ShareTask = false;
        if (!_loading) _owner.SaveBinding(this);
    }

    partial void OnShareTaskChanged(bool value)
    {
        if (!_loading) _owner.SaveBinding(this);
    }

    [RelayCommand] private Task Create() => _owner.CreateProjectAsync(this);
}

/// <summary>What project progress needs of the team (production: <see cref="TeamProgressContext"/> over <see cref="TeamViewModel"/>).</summary>
public interface ITeamProgressContext
{
    string? TeamId { get; }
    bool IsAdmin { get; }
    IReadOnlyList<TeamMember> Members { get; }
    /// <summary>The teams root (progress.json lives there).</summary>
    string Root { get; }
    /// <summary>Signed in: the progress channel of the account; null when signed out.</summary>
    ITeamProgressService? Service { get; }
    /// <summary>The session, the team or its roster changed (UI thread).</summary>
    event Action? Changed;
}

/// <summary>The account's progress channel (production: <see cref="TeamProgress"/> plus serialized team operations).</summary>
public interface ITeamProgressService
{
    IReadOnlyList<ProgressEntry> Entries { get; }
    /// <summary>Entries changed (any thread).</summary>
    event Action? Changed;
    IReadOnlyList<TeamProject> Projects(string teamId);
    Task PublishAsync(TeamProject project, string projectName, ProgressStatus status, string? task);
    Task StopAsync(TeamProject project);
    Task<TeamProject> CreateProjectAsync(string teamId);
    /// <summary>Keeps project groups complete (admin adds later members; a member publishes a key package when missing).</summary>
    Task MaintainAsync(string teamId, bool admin);
}

/// <summary>
/// P3 project progress (decision §8): what this machine publishes per local project (opt-in in Settings › Account, off by
/// default; the task title is a second opt-in) and the team's progress on the Team page with a project filter. Received
/// progress lives in memory only (<see cref="TeamProgress"/>).
/// </summary>
public sealed partial class TeamProgressViewModel : ObservableObject
{
    private readonly ITeamProgressContext _team;
    private readonly Func<IReadOnlyList<(string Path, string Name)>> _localProjects;
    private readonly Func<string, LocalProgress> _local;
    private readonly Dictionary<string, string> _names = []; // projectId → name seen
    private ProgressSettings _settings = new();
    private ITeamProgressService? _service;

    public TeamProgressViewModel(ITeamProgressContext team, Func<IReadOnlyList<(string Path, string Name)>> localProjects, Func<string, LocalProgress> local)
    {
        _team = team;
        _localProjects = localProjects;
        _local = local;
        _team.Changed += OnTeamChanged;
        OnTeamChanged();
    }

    public static string AllProjects => Loc.T("All projects");

    public ObservableCollection<ProgressRow> Rows { get; } = [];
    public ObservableCollection<string> Filters { get; } = [AllProjects];
    public ObservableCollection<ProgressProjectRow> Projects { get; } = [];

    [ObservableProperty] private string _filter = AllProjects;
    [ObservableProperty] private string? _error;

    public bool HasRows => Rows.Count > 0;
    public bool IsEmpty => Rows.Count == 0;
    public bool IsAvailable => _service is not null && _team.TeamId is not null;
    public bool HasProjects => Projects.Count > 0;
    public bool CanCreateProjects => _team.IsAdmin;

    partial void OnFilterChanged(string value) => RebuildRows();

    internal static string ShortProject(string projectId) => Loc.F("Project {0}", projectId[..Math.Min(8, projectId.Length)]);

    internal static string StatusText(ProgressStatus s) => s switch
    {
        ProgressStatus.Working => Loc.T("Working"),
        ProgressStatus.Waiting => Loc.T("Waiting for input"),
        _ => Loc.T("Idle"),
    };

    private void OnTeamChanged()
    {
        if (!ReferenceEquals(_team.Service, _service))
        {
            if (_service is not null) _service.Changed -= OnServiceChanged;
            _service = _team.Service;
            if (_service is not null) _service.Changed += OnServiceChanged;
            _settings = _service is null ? new() : ProgressSettings.Load(_team.Root);
        }
        RefreshProjects();
        RebuildRows();
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(CanCreateProjects));
        if (_service is { } s && _team.TeamId is { } t) _ = MaintainAsync(s, t);
    }

    private async Task MaintainAsync(ITeamProgressService s, string teamId)
    {
        try { await s.MaintainAsync(teamId, _team.IsAdmin); }
        catch (Exception) { } // offline or the team changed meanwhile: the next refresh retries
    }

    private void OnServiceChanged() => Dispatcher.UIThread.Post(RebuildRows);

    private IReadOnlyList<TeamProject> TeamProjects()
    {
        if (_service is null || _team.TeamId is not { } t) return [];
        try { return _service.Projects(t); }
        catch (Exception) { return []; } // the state is closed (signed out meanwhile)
    }

    private string NameOf(TeamProject p) =>
        _names.TryGetValue(p.ProjectId, out var n) ? n
        : _settings.Projects.Values.FirstOrDefault(b => b.ProjectId == p.ProjectId) is { ProjectName.Length: > 0 } b ? b.ProjectName
        : ShortProject(p.ProjectId);

    /// <summary>Settings rows: every local project with its binding; the choices are this device's team projects.</summary>
    public void RefreshProjects()
    {
        var choices = new List<ProgressProjectChoice> { new(null, Loc.T("Not shared with the team")) };
        choices.AddRange(TeamProjects().Select(p => new ProgressProjectChoice(p, NameOf(p))));
        var local = _localProjects();
        var keep = Projects.ToList();
        Projects.Clear();
        foreach (var (path, name) in local)
        {
            var row = keep.FirstOrDefault(r => r.Path == path) ?? new ProgressProjectRow(this, path, name);
            row.Load(choices, _settings.Projects.GetValueOrDefault(path));
            Projects.Add(row);
        }
        OnPropertyChanged(nameof(HasProjects));
    }

    internal void SaveBinding(ProgressProjectRow row)
    {
        if (_service is null) return;
        var was = _settings.Projects.GetValueOrDefault(row.Path);
        if (row.Selected?.Project is { } p)
            _settings.Projects[row.Path] = new ProgressBinding(p.TeamId, p.ProjectId, p.GroupId,
                _names.GetValueOrDefault(p.ProjectId) ?? (was?.ProjectId == p.ProjectId ? was.ProjectName : ""),
                row.Share, row.ShareTask);
        else _settings.Projects.Remove(row.Path);
        try { _settings.Save(_team.Root); }
        catch (IOException e) { Error = e.Message; }
        // opting out (or moving to another project) ends what was published for the old one at once
        if (was is { Share: true } && (!row.Share || was.GroupId != row.Selected?.Project?.GroupId))
            _ = _service.StopAsync(new TeamProject(was.TeamId, was.ProjectId, was.GroupId));
        _ = PublishAsync();
    }

    /// <summary>Admin: a new team project for a local project (every member gets access), bound to it at once.</summary>
    internal async Task CreateProjectAsync(ProgressProjectRow row)
    {
        if (_service is null || _team.TeamId is not { } t) return;
        try
        {
            var p = await _service.CreateProjectAsync(t);
            _names[p.ProjectId] = row.Name;
            _settings.Projects[row.Path] = new ProgressBinding(p.TeamId, p.ProjectId, p.GroupId, row.Name);
            _settings.Save(_team.Root);
            Error = null;
        }
        catch (Exception e) { Error = e.Message; }
        RefreshProjects();
    }

    /// <summary>The publisher's tick (about every second): the current state of every opted-in project.</summary>
    public async Task PublishAsync()
    {
        if (_service is not { } svc) return;
        foreach (var (path, b) in _settings.Projects.ToList())
        {
            if (!b.Share) continue;
            var now = _local(path);
            var name = _names.GetValueOrDefault(b.ProjectId) ?? b.ProjectName;
            try { await svc.PublishAsync(new TeamProject(b.TeamId, b.ProjectId, b.GroupId), name, now.Status, b.ShareTask ? now.Task : null); }
            catch (Exception e) when (e is not OperationCanceledException) { Error = e.Message; }
        }
    }

    private void RebuildRows()
    {
        var entries = _service?.Entries ?? [];
        var learned = false;
        foreach (var e in entries.Where(e => e.ProjectName.Length > 0 && _names.GetValueOrDefault(e.ProjectId) != e.ProjectName))
        {
            _names[e.ProjectId] = e.ProjectName;
            learned = true;
        }
        if (learned) RefreshProjects(); // the team projects' boxes show the names now known
        var members = _team.Members;
        string MemberName(ProgressEntry e) =>
            members.FirstOrDefault(m => m.MemberId == e.MemberId) is { } m ? (m.IsMe ? Loc.T("You") : m.Name ?? TeamViewModel.ShortId(m.MemberId)) : TeamViewModel.ShortId(e.MemberId);
        var rows = entries.Select(e => new ProgressRow(e, MemberName(e))).ToList();
        var projects = rows.Select(r => r.Project).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        var filter = Filter;
        for (var i = Filters.Count - 1; i >= 1; i--) if (!projects.Contains(Filters[i]) && Filters[i] != filter) Filters.RemoveAt(i);
        foreach (var p in projects) if (!Filters.Contains(p)) Filters.Add(p);
        Rows.Clear();
        foreach (var r in rows.Where(r => filter == AllProjects || r.Project == filter)
                     .OrderBy(r => r.Entry.Status).ThenByDescending(r => r.Entry.Updated)) Rows.Add(r);
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
