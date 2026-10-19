#if OMBRA_FEATURE_SMOKE
using Avalonia.Controls;
using Avalonia.VisualTree;
using Ombra.App.ViewModels;
using Ombra.ClientCore;
using Ombra.ClientCore.Teams;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] ProgressSections() => [("progress", ProgressFlow)];

        private sealed class SmokeProgress : ITeamProgressService
        {
            public static readonly TeamProject Ombra = new("t1", "aa11aa11aa11aa11aa11aa11aa11aa11", "g1");
            public static readonly TeamProject Docs = new("t1", "bb22bb22bb22bb22bb22bb22bb22bb22", "g2");
            public List<ProgressEntry> Received { get; } = [];
            public List<(TeamProject Project, string Name, ProgressStatus Status, string? Task)> Published { get; } = [];
            public IReadOnlyList<ProgressEntry> Entries => Received;
            public event Action? Changed;
            public void Raise() => Changed?.Invoke();
            public IReadOnlyList<TeamProject> Projects(string teamId) => [Ombra, Docs];
            public Task PublishAsync(TeamProject project, string projectName, ProgressStatus status, string? task)
            {
                lock (Published) Published.Add((project, projectName, status, task));
                return Task.CompletedTask;
            }
            public Task StopAsync(TeamProject project) => Task.CompletedTask;
            public Task<TeamProject> CreateProjectAsync(string teamId) => Task.FromResult(Ombra);
            public Task MaintainAsync(string teamId, bool admin) => Task.CompletedTask;
        }

        private sealed class SmokeProgressTeam(string root, ITeamProgressService service) : ITeamProgressContext
        {
            public string? TeamId => "t1";
            public bool IsAdmin => false;
            public IReadOnlyList<TeamMember> Members { get; } =
            [
                new(SmokeHub.Alice, "owner", "Alice", [], true, ""),
                new(SmokeHub.Bob, "member", "Bob", [], false, ""),
                new(SmokeHub.Carol, "member", "Carol", [], false, ""),
            ];
            public string Root => root;
            public ITeamProgressService? Service => service;
            public event Action? Changed { add { } remove { } }
        }

        /// <summary>
        /// P3 project progress in the real window over an in-memory team: the Team page's section with live rows and the
        /// project filter; Settings › Account binds this project to a team project and opts in; the publisher then sends
        /// this window's real state (idle, then working while omp runs a prompt, with the task title only after its switch).
        /// </summary>
        private async Task ProgressFlow()
        {
            Activate();
            var svc = new SmokeProgress();
            var root = Path.Combine(Path.GetTempPath(), "ombra-smoke-progress-" + Guid.NewGuid().ToString("N")[..8]);
            vm.UseTeamProgress(new SmokeProgressTeam(root, svc));
            svc.Received.AddRange([
                new("t1", SmokeProgress.Ombra.ProjectId, "Ombra", SmokeHub.Bob, Guid.NewGuid(), ProgressStatus.Working, "Port the sidebar", DateTimeOffset.Now.AddMinutes(-1), false),
                new("t1", SmokeProgress.Ombra.ProjectId, "Ombra", SmokeHub.Carol, Guid.NewGuid(), ProgressStatus.Waiting, null, DateTimeOffset.Now.AddMinutes(-4), false),
                new("t1", SmokeProgress.Docs.ProjectId, "Docs", SmokeHub.Bob, Guid.NewGuid(), ProgressStatus.Idle, null, DateTimeOffset.Now.AddMinutes(-25), false),
            ]);
            svc.Raise();

            vm.OpenTeamPageCommand.Execute(null);
            await Task.Delay(800);
            var card = window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "ProgressCard" && b.IsEffectivelyVisible);
            Check("progress-card-visible", card is not null);
            card?.BringIntoView();
            await Task.Delay(500);
            Check("progress-rows", vm.TeamProgress.Rows.Count == 3 && vm.TeamProgress.Rows[0].Task == "Port the sidebar",
                string.Join(" | ", vm.TeamProgress.Rows.Select(r => $"{r.Member}:{r.Project}:{r.StatusText}:{r.Task}")));
            Shot("70-progress-team");

            var filter = window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.Name == "ProgressFilter");
            if (filter is not null) filter.SelectedItem = "Docs";
            await Task.Delay(400);
            Check("progress-filter", vm.TeamProgress.Rows.Count == 1 && vm.TeamProgress.Rows[0].Project == "Docs", $"rows={vm.TeamProgress.Rows.Count}");
            Shot("71-progress-filter");
            vm.TeamProgress.Filter = TeamProgressViewModel.AllProjects;

            // Settings › Account: nothing leaves until this project is bound and opted in
            await vm.OpenSettingsAtAsync("account");
            await Task.Delay(800);
            var here = vm.ProjectRoot ?? Environment.CurrentDirectory;
            vm.TeamProgress.RefreshProjects();
            var row = vm.TeamProgress.Projects.FirstOrDefault(p => SessionCatalog.SamePath(p.Path, here));
            Check("progress-local-project", row is not null, string.Join(", ", vm.TeamProgress.Projects.Select(p => p.Path)));
            if (row is null) return;
            await Task.Delay(1500);
            Check("progress-nothing-before-opt-in", svc.Published.Count == 0, $"published={svc.Published.Count}");
            row.Selected = row.Choices.First(c => c.Project == SmokeProgress.Ombra);
            row.Share = true;
            Check("progress-published-idle", await Until(() => Last(svc) is { Status: ProgressStatus.Idle, Task: null }, 5), $"{Last(svc)}");
            var settingsCard = window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "ProgressSettingsCard" && b.IsEffectivelyVisible);
            Check("progress-settings-visible", settingsCard is not null);
            settingsCard?.BringIntoView();
            await Task.Delay(500);
            Shot("72-progress-settings");
            // a real run: working while omp works (with the plan's current task once its switch is on), then idle again
            row.ShareTask = true;
            vm.CloseSettingsCommand.Execute(null);
            await Prompt("TODOCALL make a plan");
            await Send("SLOW tell a long story");
            Check("progress-published-working", await Until(() => Last(svc) is { Status: ProgressStatus.Working, Task: "Read README.md" }, 15),
                string.Join(" | ", svc.Published.ToList().TakeLast(3).Select(p => $"{p.Status}:{p.Task}"))
                + $" todos={vm.HasTodos} current={vm.TodoCurrent} statuses={string.Join(",", vm.TodoPhases.SelectMany(p => p.Tasks).Select(t => t.Status))}");
            Shot("73-progress-working");
            vm.AbortCommand.Execute(null); // the story is long: stop it, the project is idle again
            await Until(() => !vm.IsRunning && vm.Phase == SessionPhase.Ready, 30);
            Check("progress-published-idle-after", await Until(() => Last(svc) is { Status: ProgressStatus.Idle, Task: null }, 5), $"{Last(svc)}");
            row.Share = false;
            var count = svc.Published.Count;
            await Task.Delay(2500);
            Check("progress-opt-out-stops", svc.Published.Count == count, $"{count}→{svc.Published.Count}");
        }

        private static (TeamProject Project, string Name, ProgressStatus Status, string? Task)? Last(SmokeProgress s)
        {
            lock (s.Published) return s.Published.Count == 0 ? null : s.Published[^1];
        }
    }
}
#endif
