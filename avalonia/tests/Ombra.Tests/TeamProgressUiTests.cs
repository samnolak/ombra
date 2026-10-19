using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Localization;
using Ombra.App.ViewModels;
using Ombra.ClientCore.Teams;

namespace Ombra.Tests;

/// <summary>P3 project progress in the app: the Team page's section with the project filter, the opt-in switches of Settings › Account and the publisher.</summary>
public sealed class TeamProgressUiTests
{
    private static readonly string ShotDir = Environment.GetEnvironmentVariable("OMBRA_SHOT_DIR") is { } d
        ? Path.Combine(d, "TeamProgress")
        : Path.Combine(AppContext.BaseDirectory, "screenshots", "TeamProgress");

    private static readonly Guid Me = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Ann = Guid.Parse("01900000-0000-7000-8000-000000000002");
    private static readonly Guid Bob = Guid.Parse("01900000-0000-7000-8000-000000000003");
    private static readonly TeamProject Ombra = new("t1", "aa11", "g1");
    private static readonly TeamProject Docs = new("t1", "bb22", "g2");

    private sealed class FakeService : ITeamProgressService
    {
        public List<ProgressEntry> Received { get; } = [];
        public List<(TeamProject Project, string Name, ProgressStatus Status, string? Task)> Published { get; } = [];
        public List<TeamProject> Stopped { get; } = [];
        public List<TeamProject> All { get; } = [Ombra, Docs];
        public IReadOnlyList<ProgressEntry> Entries => Received;
        public event Action? Changed;
        public void Raise() => Changed?.Invoke();
        public IReadOnlyList<TeamProject> Projects(string teamId) => All;
        public Task PublishAsync(TeamProject project, string projectName, ProgressStatus status, string? task)
        {
            Published.Add((project, projectName, status, task));
            return Task.CompletedTask;
        }
        public Task StopAsync(TeamProject project) { Stopped.Add(project); return Task.CompletedTask; }
        public Task<TeamProject> CreateProjectAsync(string teamId)
        {
            var p = new TeamProject(teamId, "cc33", "g3");
            All.Add(p);
            return Task.FromResult(p);
        }
        public Task MaintainAsync(string teamId, bool admin) => Task.CompletedTask;
    }

    private sealed class FakeContext(string root, ITeamProgressService? service) : ITeamProgressContext
    {
        public string? TeamId => "t1";
        public bool IsAdmin => true;
        public IReadOnlyList<TeamMember> Members { get; } =
        [
            new(Me, "owner", "Igor", [], true, ""),
            new(Ann, "member", "Ann", [], false, ""),
            new(Bob, "member", "Bob", [], false, ""),
        ];
        public string Root => root;
        public ITeamProgressService? Service => service;
        public event Action? Changed;
        public void Raise() => Changed?.Invoke();
    }

    private static ProgressEntry Entry(TeamProject p, string name, Guid member, ProgressStatus s, string? task, int minutesAgo) =>
        new(p.TeamId, p.ProjectId, name, member, Guid.NewGuid(), s, task, DateTimeOffset.Now.AddMinutes(-minutesAgo), member == Me);

    private static (TeamProgressViewModel Vm, FakeService Service, Dictionary<string, LocalProgress> Local, string Root) Make(string name)
    {
        var root = TestProcesses.TempDir("team-progress-" + name);
        var svc = new FakeService();
        var local = new Dictionary<string, LocalProgress> { ["/src/ombra"] = new(ProgressStatus.Working, "Fix the login form"), ["/src/docs"] = new(ProgressStatus.Idle, null) };
        var vm = new TeamProgressViewModel(new FakeContext(root, svc), () => [("/src/docs", "docs"), ("/src/ombra", "ombra")], p => local.GetValueOrDefault(p));
        svc.Received.AddRange([
            Entry(Ombra, "Ombra", Ann, ProgressStatus.Working, "Port the sidebar", 1),
            Entry(Ombra, "Ombra", Bob, ProgressStatus.Waiting, null, 3),
            Entry(Docs, "Docs", Ann, ProgressStatus.Idle, null, 20),
        ]);
        svc.Raise();
        Dispatcher.UIThread.RunJobs();
        return (vm, svc, local, root);
    }

    [AvaloniaFact]
    public void Rows_show_member_project_status_task_and_filter_by_project()
    {
        var (vm, _, _, _) = Make("rows");
        Assert.True(vm.IsAvailable);
        Assert.Equal([TeamProgressViewModel.AllProjects, "Docs", "Ombra"], vm.Filters);
        Assert.Equal(["Ann", "Bob", "Ann"], vm.Rows.Select(r => r.Member));
        Assert.Equal([ProgressStatus.Working, ProgressStatus.Waiting, ProgressStatus.Idle], vm.Rows.Select(r => r.Entry.Status));
        Assert.Equal("Port the sidebar", vm.Rows[0].Task);
        Assert.False(vm.Rows[1].HasTask);

        vm.Filter = "Docs";
        Assert.Equal(["Docs"], vm.Rows.Select(r => r.Project).Distinct());
        vm.Filter = TeamProgressViewModel.AllProjects;
        Assert.Equal(3, vm.Rows.Count);
    }

    [AvaloniaFact]
    public async Task Nothing_is_published_until_opted_in_and_the_task_only_with_its_own_switch()
    {
        var (vm, svc, local, root) = Make("optin");
        await vm.PublishAsync();
        Assert.Empty(svc.Published);

        var row = vm.Projects.Single(p => p.Path == "/src/ombra");
        Assert.False(row.IsBound);
        row.Selected = row.Choices.Single(c => c.Project == Ombra);
        Assert.False(row.Share); // binding alone shares nothing
        await vm.PublishAsync();
        Assert.Empty(svc.Published);

        row.Share = true;
        await vm.PublishAsync();
        Assert.Equal((Ombra, ProgressStatus.Working, (string?)null), (svc.Published[^1].Project, svc.Published[^1].Status, svc.Published[^1].Task));

        row.ShareTask = true;
        await vm.PublishAsync();
        Assert.Equal("Fix the login form", svc.Published[^1].Task);

        local["/src/ombra"] = new(ProgressStatus.Waiting, "Fix the login form");
        await vm.PublishAsync();
        Assert.Equal(ProgressStatus.Waiting, svc.Published[^1].Status);

        // persisted: a new view model over the same root keeps the switches
        var again = new TeamProgressViewModel(new FakeContext(root, svc), () => [("/src/ombra", "ombra")], _ => default);
        Assert.True(again.Projects.Single().Share && again.Projects.Single().ShareTask);

        row.Share = false;
        Assert.False(row.ShareTask);
        Assert.Equal([Ombra], svc.Stopped);
        var count = svc.Published.Count;
        await vm.PublishAsync();
        Assert.Equal(count, svc.Published.Count);
    }

    [AvaloniaFact]
    public async Task An_admin_creates_a_team_project_for_a_local_project()
    {
        var (vm, svc, _, _) = Make("create");
        var row = vm.Projects.Single(p => p.Path == "/src/docs");
        Assert.True(row.CanCreate);
        await row.CreateCommand.ExecuteAsync(null);
        row = vm.Projects.Single(p => p.Path == "/src/docs");
        Assert.Equal("cc33", row.Selected?.Project?.ProjectId);
        Assert.Equal("docs", row.Selected?.Label);
        Assert.False(row.Share);
        Assert.Equal(3, svc.All.Count);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task Team_page_section_and_account_switches_render(string language)
    {
        var settingsPath = Path.Combine(TestProcesses.TempDir("team-progress-view-" + language), "settings.json");
        var previous = Loc.Current;
        Loc.Load(language, settingsPath);
        try
        {
            var (vm, _, _, _) = Make("view-" + language);
            var row = vm.Projects.Single(p => p.Path == "/src/ombra");
            row.Selected = row.Choices.Single(c => c.Project == Ombra);
            row.Share = true;
            await vm.PublishAsync();

            var page = new Ombra.App.Views.Team.TeamProgressSection { DataContext = vm };
            var settings = new Ombra.App.Views.Settings.ProgressSettingsSection { DataContext = vm };
            var w = new Window { Content = new StackPanel { Spacing = 16, Margin = new Avalonia.Thickness(24), Children = { page, settings } }, Width = 820, Height = 760 };
            w.Show();
            Settle(w);
            var texts = w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains(Loc.T("Project progress"), texts);
            Assert.Contains(Loc.T("Working"), texts);
            Assert.Contains(Loc.T("Waiting for input"), texts);
            Assert.Contains("Port the sidebar", texts);
            Assert.Contains(Loc.T("Share progress with the team"), texts);
            // On/off settings are switches; the task title one is off-limits while sharing is off.
            Assert.Empty(w.GetVisualDescendants().OfType<CheckBox>());
            Assert.Contains(Loc.T("Share progress"), texts);
            Assert.Contains(Loc.T("Include the current task title"), texts);
            var switches = w.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.DataContext == row).ToList();
            var share = switches.Single(t => t.Name == "ShareProgressSwitch");
            var task = switches.Single(t => t.Name == "ShareTaskSwitch");
            Assert.True(share.Classes.Contains("switch") && share.IsChecked == true);
            Assert.True(task.IsEffectivelyEnabled);
            row.Share = false;
            Settle(w);
            Assert.False(task.IsEffectivelyEnabled);
            row.Share = true;
            Settle(w);
            // The folder reads with ~ for the home folder; the full path is the tooltip.
            var path = w.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "ProgressProjectPath" && t.DataContext == row);
            Assert.Equal("/src/ombra", ToolTip.GetTip(path));
            Assert.Equal("/src/ombra", path.Text); // outside the home folder: as it is
            if (language == "ru")
            {
                Assert.NotEqual("Project progress", Loc.T("Project progress"));
                Assert.NotEqual("Working", Loc.T("Working"));
            }
            Shot(w, "team-progress-" + language);
        }
        finally { Loc.Load(previous, settingsPath); }
    }

    private static void Settle(Window w)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }
    }

    private static void Shot(Window w, string name)
    {
        Settle(w);
        Directory.CreateDirectory(ShotDir);
        using var frame = w.CaptureRenderedFrame();
        using var file = File.Create(Path.Combine(ShotDir, name + ".png"));
        frame?.Save(file, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
