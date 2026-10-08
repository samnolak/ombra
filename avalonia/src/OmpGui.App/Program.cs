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

    /// <summary>A run a script drives and nobody watches (package smoke, benchmark): it must end, never wait without limit.</summary>
    public bool IsUnattended => SmokeExitAfterMs > 0 || ExitAfterBench || BenchLatency > 0 || BenchOut is not null;

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
        // The window app has no visible console: its errors go to client.log (the command-line modes above print them)
        Services.ClientLog.Shared.HookProcess();
        // A run nobody watches (package smoke, benchmark) waits a bounded time, then fails with the error instead of hanging
        TimeSpan? displayLimit = Args.IsUnattended ? Platform.DisplayWait.UnattendedLimit : null;
        if (OperatingSystem.IsMacOS())
            Platform.DisplayWait.UntilActive(Platform.DisplayWait.MacHasActiveDisplay, Thread.Sleep, m => Services.ClientLog.Shared.Write("display", m), displayLimit);
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (InvalidOperationException e) when (OperatingSystem.IsMacOS() && Platform.DisplayWait.IsRenderTimerFailure(e))
        {
            // The display slept between the check above and Avalonia's setup, which cannot run twice in one process:
            // wait for it again and start over in a new process. A few times at most, so a render timer that fails for
            // another reason ends the app with its error in client.log instead of starting it forever.
            Services.ClientLog.Shared.Write("display", e);
            var attempt = int.TryParse(Environment.GetEnvironmentVariable(RelaunchVariable), out var n) ? n : 0;
            if (attempt >= MaxRelaunches || Args.IsUnattended || Environment.ProcessPath is not { } self) return 1;
            Platform.DisplayWait.UntilActive(Platform.DisplayWait.MacHasActiveDisplay, Thread.Sleep, m => Services.ClientLog.Shared.Write("display", m));
            var start = new ProcessStartInfo(self) { UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment[RelaunchVariable] = (attempt + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Process.Start(start)?.Dispose();
            return 0;
        }
    }

    /// <summary>How many times in a row the app started itself again after its render timer failed (see Main).</summary>
    private const string RelaunchVariable = "OMPGUI_DISPLAY_RELAUNCH";
    private const int MaxRelaunches = 3;

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // macOS: Avalonia 12.1's Metal path can present a frame rendered for the old size after a resize, and Core
            // Animation stretches it (everything squeezed until the next repaint; AvaloniaUI/Avalonia#22215, unmerged).
            // Native popups resizing (the permission menu's confirmation) hit the same path and can freeze
            // (AvaloniaUI/Avalonia#22297). OpenGL first, popups drawn inside the window.
            .With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
                OverlayPopups = true,
            })
            .WithInterFont()
            .LogToTrace();
}
