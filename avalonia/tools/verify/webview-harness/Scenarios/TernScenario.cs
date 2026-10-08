#if APP_HOOKS
using System.Diagnostics;
using System.Text.Json.Nodes;
using OmpGui.App.Platform.Mac;
using OmpGui.ClientCore.Browser;
using OmpGui.Rpc;
using OmpGui.WebViewHarness.Native;

namespace OmpGui.WebViewHarness.Scenarios;

/// <summary>
/// BROWSER_PLAN B3 conformance on real WKWebViews: the app's <see cref="TernHost"/> serves omp's Tern protocol, each
/// <c>open</c> gets an off-screen web view with the app's own hooks (<see cref="WebKitHooks"/>), input
/// (<see cref="NativeInput"/>) and Tern engine (<see cref="TernWebKitPage"/>), and omp's own Tern client from the pinned
/// omp (<c>tools/verify/tern-conformance/conformance.ts webkit</c>, run with that omp's bun) drives every op on
/// <c>Server/Pages/tern.html</c>. One check per conformance step.
/// </summary>
internal sealed class TernScenario : IAppHooksOnly
{
    public string Name => "tern";

    public string Description => "omp's own Tern client drives real web views through the app's Tern host (every op)";

    public async Task RunAsync(HarnessContext ctx)
    {
        if (FindOmpClient() is not { } omp)
        {
            ctx.Check("an OMP GUI runtime pack with omp's Tern client is installed (or OMPGUI_TERN_OMP_PACK names one)", false);
            return;
        }
        using var host = TernHost.TryStart() ?? throw new InvalidOperationException("The Tern host could not listen.");
        var browser = new HarnessTern(ctx);
        host.Browser = browser;
        var spec = host.AddTo(new OmpLaunchSpec { FileName = omp.Bun });
        var start = new ProcessStartInfo(omp.Bun) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "--config=" + omp.Config, "--no-env-file", "--no-install", Script(), "webkit", ctx.Server.BaseUrl }) start.ArgumentList.Add(a);
        start.Environment["OMP_SRC"] = omp.Src;
        start.Environment[TernHost.SocketVariable] = spec.Environment[TernHost.SocketVariable];
        start.Environment[TernHost.PaneVariable] = spec.Environment[TernHost.PaneVariable];
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var done = false;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (line.Length == 0 || JsonNode.Parse(line) is not JsonObject o) continue;
            if (o["done"] is not null)
            {
                done = true;
                ctx.Data["summary"] = o.DeepClone();
                continue;
            }
            ctx.Check(o["check"]?.GetValue<string>() ?? "?", o["ok"]?.GetValue<bool>() == true,
                new JsonObject { ["ms"] = o["ms"]?.DeepClone(), ["detail"] = o["detail"]?.DeepClone() });
        }
        await process.WaitForExitAsync();
        var stderr = await errors;
        if (stderr.Length > 0) ctx.Data["stderr"] = stderr.Length > 4000 ? stderr[^4000..] : stderr;
        ctx.Check("the conformance run ended normally", done && process.ExitCode == 0, new JsonObject { ["exitCode"] = process.ExitCode });
        ctx.Check("every tab omp opened was closed again", browser.Pages.All(p => p.IsClosed), browser.Pages.Count);
    }

    /// <summary>tools/verify/tern-conformance/conformance.ts, next to this harness's sources (the build output lives elsewhere).</summary>
    private static string Script([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var script = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "tern-conformance", "conformance.ts"));
        return File.Exists(script) ? script : throw new FileNotFoundException("The Tern conformance script is missing.", script);
    }

    /// <summary>bun, omp's bunfig and its pi-coding-agent/src from an installed OMP GUI runtime pack.</summary>
    private static (string Bun, string Config, string Src)? FindOmpClient()
    {
        var packs = new List<string>();
        if (Environment.GetEnvironmentVariable("OMPGUI_TERN_OMP_PACK") is { Length: > 0 } pinned) packs.Add(pinned);
        var support = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        if (Directory.Exists(support))
            foreach (var app in Directory.EnumerateDirectories(support, "OmpGui*"))
                if (Directory.Exists(Path.Combine(app, "runtimes")))
                    packs.AddRange(Directory.EnumerateDirectories(Path.Combine(app, "runtimes")));
        foreach (var pack in packs)
        {
            var src = Path.Combine(pack, "omp", "node_modules", "@oh-my-pi", "pi-coding-agent", "src");
            if (File.Exists(Path.Combine(pack, "bun", "bun")) && File.Exists(Path.Combine(src, "tools", "browser", "tern", "wire.ts")))
                return (Path.Combine(pack, "bun", "bun"), Path.Combine(pack, "omp", "bunfig.toml"), src);
        }
        return null;
    }

    /// <summary>The Tern daemon side: each open is an off-screen web view with the app's hooks, input and engine.</summary>
    private sealed class HarnessTern(HarnessContext ctx) : ITernBrowser
    {
        public List<Page> Pages { get; } = [];

        public Task<ITernPage> OpenAsync(TernOpenRequest request, TernBlock block, CancellationToken ct) =>
            MacRuntime.OnMainAsync<ITernPage>(() =>
            {
                var view = ctx.CreateWebView(HookOptions.None, request.Width, request.Height);
                var hooks = new BrowserPageHooks();
                var broker = new DialogBroker();
                hooks.Broker = () => broker;
                var attached = WebKitHooks.Attach(view.Handle, hooks, out var problem)
                    ?? throw new TernException(TernException.Failed, "The app's WebKit hooks refused the web view: " + problem);
                var page = new Page(view, block, hooks, broker, new NativeInput(view.Handle), attached);
                hooks.AgentActive = () => page.IsAgentActing;
                view.Finished += () => hooks.RaiseFinished(view.Url is { } u ? new Uri(u) : null);
                lock (Pages) Pages.Add(page);
                return page;
            });

        // The harness never touches the user's clipboard
        public Task WriteClipboardAsync(string text, CancellationToken ct) =>
            Task.FromException(new TernException(TernException.Unsupported, "The harness has no clipboard."));
    }

    private sealed class Page(HarnessWebView view, TernBlock block, BrowserPageHooks hooks, DialogBroker broker, NativeInput input, IDisposable attached)
        : TernWebKitPage(view.Handle, block, hooks, broker, input)
    {
        public override Task CloseAsync(CancellationToken ct) => MacRuntime.OnMainAsync(() =>
        {
            Detach();
            Dialogs?.Dispose();
            input.Dispose();
            attached.Dispose();
        });
    }
}
#endif
