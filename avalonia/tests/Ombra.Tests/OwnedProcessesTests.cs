using Ombra.ClientCore;
using static Ombra.ClientCore.OwnedProcesses;

namespace Ombra.Tests;

/// <summary>What the app ends when it quits and when it starts: its own processes and their descendants, what a gone app
/// left behind, and never another running Ombra's.</summary>
public sealed class OwnedProcessesTests
{
    private const string Rt = "/Users/me/Library/Application Support/Ombra/runtimes";
    private const string Bun = Rt + "/omp-18.8.0-bun-1.4.2/bun/bun";
    private const int Self = 100;

    private static OwnedProcesses With(params Info[] all) => new(Rt, () => all);

    private static Info App(int pid, int? owner = null) => new(pid, 1, "/Applications/Ombra.app/Contents/MacOS/Ombra HOME=/Users/me", owner);

    [Fact]
    public void At_quit_everything_marked_by_this_app_ends_with_its_descendants_however_they_were_reparented()
    {
        var o = With(
            App(Self),
            new(200, Self, Bun + " cli.ts --mode rpc-ui", Self),
            new(300, 1, Bun + " cli.ts __omp_worker_daemon_broker", Self),   // the broker, detached
            new(301, 300, "ng serve app --port 4207", Self),
            new(302, 301, "node_modules/@esbuild/bin/esbuild --service", Self),
            new(400, 1, "npm run dev", Self),                                  // a job whose broker is gone
            new(500, 1, "/usr/local/bin/omp", null),                           // the user's own omp
            new(600, 1, "code .", null));                                      // an editor opened without the mark
        Assert.Equal([200, 300, 301, 302, 400], o.Select(Moment.Quit, Self));
    }

    [Fact]
    public void At_start_only_what_a_gone_app_left_ends_never_this_apps_own_or_another_running_apps()
    {
        var o = With(
            App(Self),
            new(200, Self, Bun + " cli.ts --mode rpc-ui", Self),               // started a moment ago
            App(700),                                                          // another Ombra, running
            new(701, 1, Bun + " cli.ts __omp_worker_daemon_broker", 700),
            new(800, 1, Bun + " cli.ts __omp_worker_daemon_broker", 42),       // its app (42) is gone
            new(801, 800, "npm start", 42));
        Assert.Equal([800, 801], o.Select(Moment.Start, Self));
        // At quit, this app's own as well (a gone app's leftovers go whenever they are found)
        Assert.Equal([200, 800, 801], o.Select(Moment.Quit, Self));
    }

    [Fact]
    public void An_earlier_versions_unmarked_runtime_processes_end_at_start_unless_another_app_could_own_them()
    {
        Info[] leftovers =
        [
            new(64151, 1, Bun + " cli.ts __omp_worker_daemon_broker LANG=C.UTF-8", null),
            new(12205, 64151, "ng serve merchant-dashboard --port 4207", null),
            new(12230, 12205, "/Users/me/p/node_modules/@esbuild/darwin-arm64/bin/esbuild --service=0.25.4", null),
            new(900, 1, "/opt/homebrew/bin/bun dev", null),                    // bun of the user's, not the app's runtime
        ];
        Assert.Equal([12205, 12230, 64151], With([App(Self), .. leftovers]).Select(Moment.Start, Self));
        Assert.Empty(With([App(Self), .. leftovers]).Select(Moment.Quit, Self));
        // Another Ombra (an older one, which does not mark) may be running them
        Assert.Empty(With([App(Self), App(700), .. leftovers]).Select(Moment.Start, Self));
    }

    [Fact]
    public void The_app_itself_and_its_ancestors_are_never_chosen_even_when_marked()
    {
        var o = With(
            new(50, 1, "/bin/zsh -l", 42),                                     // the shell the app was started from
            new(Self, 50, "/Applications/Ombra.app/Contents/MacOS/Ombra OMBRA_OWNER=42", 42),
            new(200, Self, Bun + " cli.ts", Self));
        Assert.Equal([200], o.Select(Moment.Quit, Self));
        Assert.Empty(o.Select(Moment.Start, Self));
    }
}
