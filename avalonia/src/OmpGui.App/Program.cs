using System.Diagnostics;
using Avalonia;

namespace OmpGui.App;

/// <summary>Command line: [--config path] plus test hooks used by the verification scripts (see docs/PROJECT_STATE.md).</summary>
public sealed record AppArgs
{
    public string? ConfigPath { get; init; }
    public string? AutoPrompt { get; init; }
    public int BenchLatency { get; init; }
    public string? BenchOut { get; init; }
    public string? TimelinePath { get; init; }
    public string? StartupLogPath { get; init; }
    public bool ExitAfterBench { get; init; }
    /// <summary><c>--self-test report.json</c>: check the build without a window (see <c>Services/SelfTest</c>).</summary>
    public string? SelfTestReport { get; init; }
    public bool SelfTest { get; init; }
    /// <summary><c>--install-runtime</c> (with --self-test): install the pinned runtime when it is missing.</summary>
    public bool InstallRuntime { get; init; }
    /// <summary><c>--smoke-exit-after-ms N</c>: package smoke — once omp is up (or failed), wait N ms and close the window.</summary>
    public int SmokeExitAfterMs { get; init; }
    /// <summary><c>--version</c>: print the version and platform, no window.</summary>
    public bool PrintVersion { get; init; }
    /// <summary><c>--update [--yes [--restart]] [--feed url]</c>: check for an update without a window; with --yes install it (see <c>Services/UpdateCli</c>).</summary>
    public bool Update { get; init; }
    public bool Yes { get; init; }
    public bool Restart { get; init; }
    public string? UpdateFeed { get; init; }

    public static AppArgs Parse(string[] args)
    {
        var a = new AppArgs();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            a = args[i] switch
            {
                "--config" => a with { ConfigPath = Next() },
                "--auto-prompt" => a with { AutoPrompt = Next() },
                "--bench-latency" => a with { BenchLatency = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture) },
                "--bench-out" => a with { BenchOut = Next() },
                "--exit-after-bench" => a with { ExitAfterBench = true },
                "--timeline" => a with { TimelinePath = Next() },
                "--startup-log" => a with { StartupLogPath = Next() },
                "--self-test" => a with { SelfTest = true, SelfTestReport = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? Next() : null },
                "--install-runtime" => a with { InstallRuntime = true },
                "--version" => a with { PrintVersion = true },
                "--update" => a with { Update = true },
                "--yes" => a with { Yes = true },
                "--restart" => a with { Restart = true },
                "--feed" => a with { UpdateFeed = Next() },
                "--smoke-exit-after-ms" => a with { SmokeExitAfterMs = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture) },
                _ => a,
            };
        }
        return a;
    }
}

internal static class Program
{
    public static readonly long StartTimestamp = Stopwatch.GetTimestamp();
    public static AppArgs Args { get; private set; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        Args = AppArgs.Parse(args);
        if (Args.PrintVersion)
        {
            Console.WriteLine($"OMP GUI {Services.UpdateChecker.ThisVersion()} {Services.UpdateChecker.ThisRid()}");
            return 0;
        }
        if (Args.Update) return Services.UpdateCli.RunAsync(Args).GetAwaiter().GetResult();
        if (Args.SelfTest) return Services.SelfTest.RunAsync(Args).GetAwaiter().GetResult();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
