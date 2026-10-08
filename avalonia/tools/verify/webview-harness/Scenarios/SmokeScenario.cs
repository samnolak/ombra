using System.Text.Json.Nodes;
using OmpGui.WebViewHarness.Native;

namespace OmpGui.WebViewHarness.Scenarios;

/// <summary>The spike's first question: does an off-screen WKWebView load pages and run JS in a windowless process?</summary>
internal sealed class SmokeScenario : IScenario
{
    public string Name => "smoke";

    public string Description => "off-screen WKWebView: load, evaluateJavaScript round-trip, async JS, timers, page facts";

    public async Task RunAsync(HarnessContext ctx)
    {
        var view = ctx.CreateWebView(HookOptions.None);
        var nav = await view.LoadAsync(ctx.Server.Url("/"));
        ctx.Check("page loads (didFinishNavigation)", nav["ok"]?.GetValue<bool>() == true && view.DocumentTitle == "harness", nav);

        var sum = await view.EvalAsync("1 + 2");
        ctx.Check("evaluateJavaScript round-trip", sum?.GetValue<int>() == 3, sum);

        var text = await view.EvalAsync("document.getElementById('r').textContent");
        ctx.Check("DOM read", text?.GetValue<string>() == "ok", text);

        var awaited = await view.CallAsync("await new Promise(r => setTimeout(r, 50)); return 'waited';");
        ctx.Check("callAsyncJavaScript awaits a Promise", awaited?.GetValue<string>() == "waited", awaited);

        var timerMs = await view.CallAsync("const t0 = performance.now(); await new Promise(r => setTimeout(r, 100)); return Math.round(performance.now() - t0);");
        ctx.Check("timers are not suspended off screen (100 ms timer < 1 s)", timerMs?.GetValue<int>() is >= 90 and < 1000, timerMs);

        var raf = await view.CallAsync(
            "return await Promise.race([new Promise(r => requestAnimationFrame(() => r(true))), new Promise(r => setTimeout(() => r(false), 500))]);");
        var facts = await view.EvalAsync(
            "({ visibilityState: document.visibilityState, hidden: document.hidden, hasFocus: document.hasFocus(), " +
            "userAgent: navigator.userAgent, webdriver: navigator.webdriver, devicePixelRatio, innerWidth, innerHeight })");
        ctx.Data["requestAnimationFrameFires"] = raf?.DeepClone();
        ctx.Data["page"] = facts?.DeepClone();
        ctx.Data["webProcessId"] = view.WebProcessId;
        ctx.Check("viewport is the web view's frame (1024×768)",
            facts?["innerWidth"]?.GetValue<int>() == 1024 && facts?["innerHeight"]?.GetValue<int>() == 768, facts);
    }
}
