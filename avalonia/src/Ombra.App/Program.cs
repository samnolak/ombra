using System.Diagnostics;
using Avalonia;
using Ombra.ClientCore;

namespace Ombra.App;

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
    /// <summary><c>--install-engine &lt;root&gt; [--engine-platform key]</c>: package time — install the pinned engine pack
    /// into root for the package to carry (see <c>Services/Runtime/EngineBundleCli</c>).</summary>
    public string? InstallEngine { get; init; }
    public string? EnginePlatform { get; init; }

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
                "--install-engine" => a with { InstallEngine = Next() },
                "--engine-platform" => a with { EnginePlatform = Next() },
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
    internal static string SettingsPath { get; private set; } = "";
    internal static DraftStore? Drafts { get; private set; }
    internal static bool AlreadyRunning { get; private set; }
    internal static Exception? StartupError { get; private set; }
    internal static bool StartupRefused => AlreadyRunning || StartupError is not null;

    [STAThread]
    public static int Main(string[] args)
    {
        // The updater must not initialize UI, acquire a client lease, migrate settings or join the parent's job.
        // Dispatch before permissive AppArgs parsing so malformed/mixed helper commands fail closed.
        if (args.Contains("--apply-update", StringComparer.Ordinal))
            return Services.UpdateApplyHelper.RunAsync(args).GetAwaiter().GetResult();
        Args = AppArgs.Parse(args);
        if (Args.PrintVersion)
        {
            Console.WriteLine($"Ombra {Services.UpdateChecker.ThisVersion()} {Services.UpdateChecker.ThisRid()}");
            return 0;
        }
        // Headless tools do not own or mutate the client journal, and staged --version above changes nothing.
        if (Args.Update || Args.SelfTest || Args.InstallEngine is not null)
        {
            // Headless runtime probes/installers also launch children. The update apply helper deliberately
            // escapes this Windows job through StartIndependent; --version above never joins or creates a job.
            if (OperatingSystem.IsWindows()) OwnedProcesses.MarkThisProcess();
            if (Args.InstallEngine is not null) return Services.Runtime.EngineBundleCli.RunAsync(Args).GetAwaiter().GetResult();
            Services.ClientLog.Shared.Write("path", LoginShellPath.Initialize().Diagnostic);
            return Args.Update ? Services.UpdateCli.RunAsync(Args).GetAwaiter().GetResult()
                : Services.SelfTest.RunAsync(Args).GetAwaiter().GetResult();
        }
        ClientInstanceLease? instance = null;
        void ReleaseOwnership()
        {
            try { Drafts?.Dispose(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Draft save failed: " + e.Message); }
            finally { Drafts = null; instance?.Dispose(); instance = null; }
        }
        try
        {
            try
            {
                SettingsPath = ClientInstanceLease.SettingsPath(Args.ConfigPath);
                instance = ClientInstanceLease.TryAcquire(SettingsPath);
                AlreadyRunning = instance is null;
                if (!AlreadyRunning)
                {
                    // Windows migration can start cmd.exe to create a junction. Establish kernel ownership before
                    // even that first helper; a refused second instance never joins a job or starts migration.
                    if (OperatingSystem.IsWindows()) OwnedProcesses.MarkThisProcess();
                    // Admission precedes all writable startup work. An explicit configuration is isolated from the
                    // installed app's legacy migration, whether supplied by --config or OMBRA_CONFIG.
                    if (Args.ConfigPath is null && Environment.GetEnvironmentVariable(OmpRuntimeOptions.ConfigEnvVar) is null)
                        AlreadyRunning = !Services.LegacyNames.MigrateData(m => Services.ClientLog.Shared.Write("rename", m));
                    if (!AlreadyRunning) Drafts = new DraftStore(DraftStore.PathFor(SettingsPath));
                }
            }
            catch (DraftStoreInUseException) { AlreadyRunning = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or WaitHandleCannotBeOpenedException or System.Security.SecurityException)
            {
                StartupError = e;
            }
            if (StartupRefused)
            {
                ReleaseOwnership(); // a refusal window must not itself prevent a later legitimate launch
                Console.Error.WriteLine(AlreadyRunning
                    ? $"Ombra is already using this configuration: {SettingsPath}"
                    : $"Ombra could not acquire exclusive access to its drafts: {StartupError!.Message}");
                if (Args.IsUnattended) return 1;
            }
            else
            {
                // A losing launch never registers process cleanup, opens a runtime, or changes user data.
                Services.ClientLog.Shared.HookProcess();
                if (Services.LegacyNames.RenameMacBundleAndRelaunch(args, m => Services.ClientLog.Shared.Write("rename", m), ReleaseOwnership)) return 0;
                Services.ClientLog.Shared.Write("path", LoginShellPath.Initialize().Diagnostic);
            }
            // A run a harness drives (feature smoke, NightlyAcceptance, package smoke, benchmark) never takes the owner's
            // focus (Platform/TestFocus.cs: OMBRA_TEST_NO_ACTIVATE).
            Platform.TestFocus.Configure(Args.IsUnattended
                || Environment.GetEnvironmentVariable("OMBRA_FEATURE_SMOKE") is { Length: > 0 }
                || Environment.GetEnvironmentVariable("OMBRA_NIGHTLY_ACCEPTANCE") is { Length: > 0 });
            // A run nobody watches waits a bounded time, rather than hanging on a sleeping display.
            TimeSpan? displayLimit = Args.IsUnattended ? Platform.DisplayWait.UnattendedLimit : null;
            if (OperatingSystem.IsMacOS())
                Platform.DisplayWait.UntilActive(Platform.DisplayWait.MacHasActiveDisplay, Thread.Sleep,
                    m => { if (StartupRefused) Console.Error.WriteLine(m); else Services.ClientLog.Shared.Write("display", m); }, displayLimit);
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (InvalidOperationException e) when (OperatingSystem.IsMacOS() && Platform.DisplayWait.IsRenderTimerFailure(e))
        {
            // The display slept between the check above and Avalonia's setup, which cannot run twice in one process:
            // wait for it again and start over in a new process. A few times at most, so a render timer that fails for
            // another reason ends the app with its error in client.log instead of starting it forever.
            if (StartupRefused) { Console.Error.WriteLine(e); return 1; }
            Services.ClientLog.Shared.Write("display", e);
            var attempt = int.TryParse(Environment.GetEnvironmentVariable(RelaunchVariable), out var n) ? n : 0;
            if (attempt >= MaxRelaunches || Args.IsUnattended || Environment.ProcessPath is not { } self) return 1;
            Platform.DisplayWait.UntilActive(Platform.DisplayWait.MacHasActiveDisplay, Thread.Sleep, m => Services.ClientLog.Shared.Write("display", m));
            var start = new ProcessStartInfo(self) { UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment[RelaunchVariable] = (attempt + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            ReleaseOwnership(); // the replacement must acquire before it starts any writable startup work
            Process.Start(start)?.Dispose();
            return 0;
        }
        finally { ReleaseOwnership(); }
    }

    /// <summary>How many times in a row the app started itself again after its render timer failed (see Main).</summary>
    private const string RelaunchVariable = "OMBRA_DISPLAY_RELAUNCH";
    private const int MaxRelaunches = 3;

    public static AppBuilder BuildAvaloniaApp() => Platform.TestFocus.Apply(
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // macOS: Avalonia 12.1's Metal path can present a frame rendered for the old size after a resize, and Core
            // Animation stretches it (everything squeezed until the next repaint; AvaloniaUI/Avalonia#22215, unmerged).
            // Native popups resizing (the permission menu's confirmation) hit the same path and can freeze
            // (AvaloniaUI/Avalonia#22297). OpenGL first, popups drawn inside the window. Both are still open and 12.1.4's
            // metal.mm is 12.1.3's (checked 2026-10-21), and Metal missed vsyncs in the renderer runs
            // (docs/browser-research/k1-gecko-embedding.md). OpenGL's price: Avalonia.Native's target (rendertarget.mm,
            // getNextSurfaceInSafeContext) takes a new window-sized IOSurface for every frame the render thread draws,
            // where Metal reuses its CAMetalLayer's drawables, and the compositor copies its whole-window layer into it;
            // only the dirty part is redrawn, but every frame costs the whole window (about 50 MB at 2560×1320 pt, 2×).
            // What animates in the window keeps its frames few (Controls/Pulse, Controls/OniView.FrameInterval).
            .With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
                OverlayPopups = true,
            })
            .WithInterFont()
            .LogToTrace());
}
