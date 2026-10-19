using Avalonia.Threading;
using Ombra.ClientCore.Teams;

namespace Ombra.App.ViewModels;

/// <summary>Project progress over the signed-in account of <see cref="TeamViewModel"/>: one <see cref="TeamProgress"/> per session.</summary>
public sealed class TeamProgressContext : ITeamProgressContext
{
    private readonly TeamViewModel _team;
    private SessionProgress? _service;

    public TeamProgressContext(TeamViewModel team)
    {
        _team = team;
        _team.SessionChanged += s => Dispatcher.UIThread.Post(() => Attach(s));
        _team.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TeamViewModel.HasTeam) or nameof(TeamViewModel.IsAdmin) or nameof(TeamViewModel.Members)) Changed?.Invoke();
        };
        Attach(_team.Session);
    }

    public string? TeamId => _team.SelectedTeamId;
    public bool IsAdmin => _team.IsAdmin;
    public IReadOnlyList<TeamMember> Members => _team.Members;
    public string Root => _team.Root;
    public ITeamProgressService? Service => _service;
    public event Action? Changed;

    private void Attach(TeamSession? session)
    {
        if (ReferenceEquals(_service?.Session, session)) return;
        _service?.Dispose();
        _service = session is null ? null : new SessionProgress(session, () => Dispatcher.UIThread.Post(() => Changed?.Invoke()));
        Changed?.Invoke();
    }

    /// <summary>The account's stream listener; team operations run serialized with the session's refresh.</summary>
    private sealed class SessionProgress : ITeamProgressService, IDisposable
    {
        public static readonly TimeSpan KeyPackageEvery = TimeSpan.FromMinutes(10);
        private readonly TeamProgress _progress;
        private readonly Action<TeamSnapshot> _refreshed;
        private DateTimeOffset _keyPackageAt = DateTimeOffset.MinValue;

        public SessionProgress(TeamSession session, Action refreshed)
        {
            Session = session;
            _progress = new TeamProgress(session.Client);
            _progress.Start();
            _refreshed = _ => refreshed();
            session.Changed += _refreshed;
        }

        public TeamSession Session { get; }
        public IReadOnlyList<ProgressEntry> Entries => _progress.Entries;

        public event Action? Changed
        {
            add => _progress.Changed += value;
            remove => _progress.Changed -= value;
        }

        public IReadOnlyList<TeamProject> Projects(string teamId) => Session.Client.ProjectGroups(teamId);
        public Task PublishAsync(TeamProject project, string projectName, ProgressStatus status, string? task) => _progress.PublishAsync(project, projectName, status, task);
        public Task StopAsync(TeamProject project) => _progress.StopAsync(project);
        public Task<TeamProject> CreateProjectAsync(string teamId) => Session.RunAsync(c => c.CreateProjectAsync(teamId));

        public async Task MaintainAsync(string teamId, bool admin)
        {
            if (admin)
            {
                foreach (var p in Projects(teamId)) await Session.RunAsync(c => c.AddProjectMembersAsync(p));
            }
            else if (DateTimeOffset.Now - _keyPackageAt > KeyPackageEvery && MissingProjectGroup(Session.Client, teamId))
            {
                _keyPackageAt = DateTimeOffset.Now;
                await Session.RunAsync(async c => { await c.UploadKeyPackageAsync(); return 0; });
            }
        }

        /// <summary>The roster grants this member a project whose group this device is not in yet (the admin needs a key package).</summary>
        private static bool MissingProjectGroup(TeamClient c, string teamId)
        {
            var me = TeamsHex.Of(c.Device.Identity!.MemberId);
            var mine = c.ProjectGroups(teamId).Select(p => p.ProjectId).ToHashSet();
            return c.Head(teamId)["projection"]!["access"]!.AsArray()
                .Any(a => a!["memberId"]!.GetValue<string>() == me && !mine.Contains(a["projectId"]!.GetValue<string>()));
        }

        public void Dispose()
        {
            Session.Changed -= _refreshed;
            _ = _progress.DisposeAsync().AsTask();
        }
    }
}
