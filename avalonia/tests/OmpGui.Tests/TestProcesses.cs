using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.Tests;

internal static class TestProcesses
{
    /// <summary>Launch spec for the scripted fake omp (built next to this test project).</summary>
    public static OmpLaunchSpec Fake(string scenario)
    {
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory.Replace("OmpGui.Tests", "OmpGui.FakeOmp"), "OmpGui.FakeOmp.dll"));
        if (!File.Exists(dll)) throw new FileNotFoundException("build tests/OmpGui.FakeOmp first", dll);
        return new OmpLaunchSpec
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            Arguments = [dll, scenario],
        };
    }

    /// <summary>Fake omp that honours project folder and <c>--resume</c> like omp, keeping sessions under <paramref name="sessionDir"/>.</summary>
    public static Func<LaunchRequest, OmpLaunchSpec> FakeFactory(string scenario, string sessionDir) => req =>
    {
        var spec = Fake(scenario);
        return new OmpLaunchSpec
        {
            FileName = spec.FileName,
            Arguments = req.ResumeSessionFile is { } r ? [.. spec.Arguments, "--resume", r] : spec.Arguments,
            WorkingDirectory = req.WorkingDirectory,
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
        return new OmpLaunchSpec { FileName = spec.FileName, Arguments = [.. spec.Arguments, .. cliArgs], WorkingDirectory = dir, Environment = env };
    };

    /// <summary>The fake omp for <paramref name="scenario"/> answering builtin slash commands from <paramref name="commands"/> (prefix → text).</summary>
    public static OmpLaunchSpec FakeWithCommands(string scenario, string commands, string? log = null)
    {
        var spec = Fake(scenario);
        var env = new Dictionary<string, string?> { ["FAKE_OMP_COMMANDS"] = commands };
        if (log is not null) env["FAKE_OMP_COMMAND_LOG"] = log;
        return spec with { Environment = env };
    }

    public static string TempDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ompgui-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Real omp settings for integration tests (a local JSON, see docs/PROJECT_STATE.md); null skips them.</summary>
    public static OmpRuntimeOptions? RealOmp()
    {
        var path = Environment.GetEnvironmentVariable("OMPGUI_TEST_CONFIG");
        return path is null || !File.Exists(path) ? null : OmpRuntimeOptions.Load(path);
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
