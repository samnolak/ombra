using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmpGui.WebViewHarness.Native;
using OmpGui.WebViewHarness.Scenarios;
using OmpGui.WebViewHarness.Server;

[assembly: SupportedOSPlatform("macos")]

namespace OmpGui.WebViewHarness;

/// <summary>
/// Windowless native WKWebView harness (BROWSER_PLAN B0): one scenario per process, its result as one JSON object on
/// stdout; exit code 0 = all checks passed. <c>tools/verify/webview-harness/run.sh &lt;scenario|all&gt;</c>.
/// </summary>
internal static class Program
{
    private static readonly IScenario[] All =
    [
        new SmokeScenario(),
        new AuthBaselineScenario(),
        new AuthBasicScenario(),
        new DialogsBaselineScenario(),
        new DialogsScenario(),
        new PopupScenario(),
        new DownloadScenario(),
        new FileInputScenario(),
        new TrustedInputScenario(),
#if APP_INPUT
        new NativeInputScenario(),
        new AgentNativeScenario(),
#endif
        new CrashScenario(),
        new LoadFailureScenario(),
        new UserAgentScenario(),
#if APP_HOOKS
        new PermissionScenario(),
        new BeforeUnloadScenario(),
        new TernScenario(),
#endif
    ];

#if APP_HOOKS
    private static readonly bool AppHooksLinked = true;
#else
    private static readonly bool AppHooksLinked = false;
#endif

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("The WKWebView harness runs on macOS only.");
            return 2;
        }
        if (args.Length == 0 || args[0] is "--list" or "-h" or "--help")
        {
            // `--list --hooks harness` leaves out the scenarios that test only the app's own hooks (run.sh all uses it)
            var listHooks = args.SkipWhile(a => a != "--hooks").Skip(1).FirstOrDefault() ?? (AppHooksLinked ? "app" : "harness");
            foreach (var s in All.Where(s => listHooks == "app" || s is not IAppHooksOnly)) Console.WriteLine($"{s.Name,-18} {s.Description}");
            return args.Length == 0 ? 2 : 0;
        }
        var scenario = All.FirstOrDefault(s => s.Name == args[0]);
        if (scenario is null)
        {
            Console.Error.WriteLine($"Unknown scenario '{args[0]}'. --list shows them.");
            return 2;
        }
        var hooks = args.SkipWhile(a => a != "--hooks").Skip(1).FirstOrDefault() ?? (AppHooksLinked ? "app" : "harness");
        if (hooks == "app" && !AppHooksLinked)
        {
            Console.Error.WriteLine("--hooks app: the app's Platform/Mac hooks are not linked into this build.");
            return 2;
        }
        var timeout = TimeSpan.FromSeconds(int.TryParse(System.Environment.GetEnvironmentVariable("HARNESS_TIMEOUT_SEC"), out var t) ? t : 90);

        var loop = new MainLoop();
        SynchronizationContext.SetSynchronizationContext(loop.Context);
        Delegates.Main = loop.Context;
        using var server = new TestServer();
        var ctx = new HarnessContext(loop, server, hooks);
        var started = DateTime.UtcNow;
        string? failure = null;
        var task = Run(scenario, ctx);
        if (!loop.RunUntil(task, timeout)) failure = $"timed out after {timeout.TotalSeconds} s";
        else if (task.Exception is { } e) failure = e.InnerException?.ToString() ?? e.ToString();

        var environment = DescribeEnvironment(loop);
        var result = new JsonObject
        {
            ["scenario"] = scenario.Name,
            ["ok"] = failure is null && ctx.Checks.Count > 0 && ctx.Ok && Isolated(environment),
            ["hooks"] = hooks,
            ["appHooksLinked"] = AppHooksLinked,
            ["durationMs"] = (long)(DateTime.UtcNow - started).TotalMilliseconds,
            ["failure"] = failure,
            ["environment"] = environment,
            ["checks"] = ctx.Checks.DeepClone(),
            ["data"] = ctx.Data.DeepClone(),
            ["events"] = HarnessLog.ToJson(),
        };
        Console.Out.WriteLine(result.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // a local report, not HTML
        }));
        Console.Out.Flush();
        var ok = result["ok"]!.GetValue<bool>();
        var passed = ctx.Checks.Count(c => c?["ok"]?.GetValue<bool>() == true);
        Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")} {scenario.Name} ({passed}/{ctx.Checks.Count} checks, hooks={hooks}, {result["durationMs"]} ms)"
            + (failure is null ? "" : " — " + failure.Split('\n')[0]));
        if (failure is null) ctx.Dispose();
        // WebKit's helper processes go with us; skip finalizers racing AppKit teardown
        System.Environment.Exit(ok ? 0 : 1);
        return ok ? 0 : 1;
    }

    private static async Task Run(IScenario scenario, HarnessContext ctx)
    {
        await Task.Yield(); // start inside the loop
        await scenario.RunAsync(ctx);
    }

    /// <summary>What proves the run stayed off screen: activation policy, active state, our windows' visibility.</summary>
    private static JsonObject DescribeEnvironment(MainLoop loop)
    {
        var windows = ObjC.Send(loop.App, "windows");
        var count = windows == 0 ? 0 : (int)ObjC.SendLong(windows, "count");
        var list = new JsonArray();
        var anyVisible = false;
        for (var i = 0; i < count; i++)
        {
            var w = ObjC.Send(windows, "objectAtIndex:", i);
            var visible = ObjC.SendBool(w, "isVisible");
            anyVisible |= visible;
            list.Add(new JsonObject { ["isVisible"] = visible, ["isOnActiveSpace"] = ObjC.SendBool(w, "isOnActiveSpace") });
        }
        // this app's windows that the window server has on screen (no screen-recording permission needed for our own)
        var onScreen = ObjC.Send(ObjC.Cls("NSWindow"), "windowNumbersWithOptions:", 0);
        return new JsonObject
        {
            ["pid"] = System.Environment.ProcessId,
            ["macOS"] = System.Environment.OSVersion.VersionString,
            ["activationPolicy"] = loop.ActivationPolicy,
            ["activationPolicyProhibited"] = loop.ActivationPolicy == 2,
            ["appIsActive"] = loop.IsActive,
            ["windows"] = list,
            ["anyWindowVisible"] = anyVisible,
            ["onScreenWindowCount"] = onScreen == 0 ? 0 : ObjC.SendLong(onScreen, "count"),
        };
    }

    private static bool Isolated(JsonObject env) =>
        env["activationPolicyProhibited"]!.GetValue<bool>() && !env["appIsActive"]!.GetValue<bool>()
        && !env["anyWindowVisible"]!.GetValue<bool>() && env["onScreenWindowCount"]!.GetValue<long>() == 0;
}
