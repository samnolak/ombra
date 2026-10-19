using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.Tests;

internal static class TestProcesses
{
    /// <summary>Shared fake engine executable and the scenario for one logical session.</summary>
    public static OmpLaunchSpec Fake(string scenario)
    {
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory.Replace("Ombra.Tests", "Ombra.FakeOmp"), "Ombra.FakeOmp.dll"));
        if (!File.Exists(dll)) throw new FileNotFoundException("build tests/Ombra.FakeOmp first", dll);
        return new OmpLaunchSpec
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            Arguments = [dll],
            SessionArguments = [scenario],
        };
    }

    /// <summary>Fake omp that honours project folder and <c>--resume</c> like omp, keeping sessions under <paramref name="sessionDir"/>.</summary>
    public static Func<LaunchRequest, OmpLaunchSpec> FakeFactory(string scenario, string sessionDir) => req =>
    {
        var spec = Fake(scenario);
        return spec with
        {
            SessionArguments = [.. spec.SessionArguments,
                .. (req.ResumeSessionFile is { } resume ? new[] { "--resume", resume } : Array.Empty<string>()),
                .. (req.ApprovalMode is { } mode ? new[] { "--approval-mode", mode } : Array.Empty<string>())],
            WorkingDirectory = req.WorkingDirectory,
            ResumeOnCurrentModel = req.ResumeOnCurrentModel,
            Environment = new Dictionary<string, string?> { ["FAKE_SESSION_DIR"] = sessionDir },
        };
    };

    /// <summary>
    /// omp's CLI played by the fake: <paramref name="answers"/> maps joined arguments ("plugin list --json") to what
    /// is printed; <paramref name="log"/> receives each call.
    /// </summary>
    public static Func<IReadOnlyList<string>, string?, OmpLaunchSpec> FakeCli(string answers, string? log = null) => (cliArgs, dir) =>
    {
        var spec = Fake("cli");
        var env = new Dictionary<string, string?> { ["FAKE_OMP_CLI"] = answers };
        if (log is not null) env["FAKE_OMP_CLI_LOG"] = log;
        return new OmpLaunchSpec { FileName = spec.FileName, Arguments = [.. spec.Arguments, "cli", .. cliArgs], WorkingDirectory = dir, Environment = env };
    };

    /// <summary>The fake omp for <paramref name="scenario"/> answering builtin slash commands from <paramref name="commands"/> (prefix → text).</summary>
    public static OmpLaunchSpec FakeWithCommands(string scenario, string commands, string? log = null)
    {
        var spec = Fake(scenario);
        var env = new Dictionary<string, string?> { ["FAKE_OMP_COMMANDS"] = commands };
        if (log is not null) env["FAKE_OMP_COMMAND_LOG"] = log;
        return spec with { Environment = env };
    }

    /// <summary>A new folder in the run's temp folder, deleted with it (<see cref="TestEnvironment"/>).</summary>
    public static string TempDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Real omp settings for integration tests (a local JSON, see docs/PROJECT_STATE.md); null skips them.</summary>
    public static OmpRuntimeOptions? RealOmp()
    {
        var path = Environment.GetEnvironmentVariable("OMBRA_TEST_CONFIG");
        return path is null || !File.Exists(path) ? null : LoadIsolated(path, "OMBRA_TEST_CONFIG");
    }

    /// <summary>Settings for a real omp, refused (the test fails, nothing starts) when that omp would use the user's own
    /// home or agent folder — its sessions would show as chats in the user's Ombra — or the app the user's chats and
    /// teams folders (<see cref="Ombra.Verify.HarnessIsolation"/>).</summary>
    public static OmpRuntimeOptions LoadIsolated(string path, string variable)
    {
        var options = OmpRuntimeOptions.Load(path);
        var problems = Ombra.Verify.HarnessIsolation.Problems(options.ToLaunchSpec().Environment, Environment.GetEnvironmentVariable,
            ChatFolders.DefaultRoot, Environment.GetEnvironmentVariable("OMBRA_TEAMS_DIR") ?? "");
        if (problems.Count > 0)
            throw new InvalidOperationException($"{variable} ({path}) is not isolated from the user's folders: " + string.Join("; ", problems));
        return options;
    }

    public static async Task<T> Eventually<T>(Func<T> read, Func<T, bool> until, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var v = read();
            if (until(v)) return v;
            if (DateTime.UtcNow > deadline)
            {
                var tail = v is SessionSnapshot snap
                    ? "\nlast events:\n" + string.Join("\n", snap.DebugTail.TakeLast(25).Select(d => $"{d.At:HH:mm:ss.fff} {d.Type} {d.Summary}"))
                    : "";
                throw new TimeoutException($"timed out waiting for {what}; last value: {v}{tail}");
            }
            await Task.Delay(20);
        }
    }
}
