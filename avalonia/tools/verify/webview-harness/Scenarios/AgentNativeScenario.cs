#if APP_INPUT
using System.Text.Json.Nodes;
using OmpGui.App.Platform.Mac;
using OmpGui.ClientCore;
using OmpGui.ClientCore.Browser;
using OmpGui.WebViewHarness.Native;

namespace OmpGui.WebViewHarness.Scenarios;

/// <summary>
/// The agent's browser end to end on a real WKWebView: ClientCore's <see cref="AgentBrowserBridge"/> answering omp's
/// cmux requests (as omp 18.8 sends them) for a page backed by the app's <c>NativeInput</c> and <c>MacIsolatedScripts</c>
/// (what <c>PreviewAgentPage</c> gives it in the app). Element actions must reach the page as trusted input, the page
/// library must live in the isolated world, and a page without native input must be told its events are synthetic.
/// </summary>
internal sealed class AgentNativeScenario : IScenario
{
    public string Name => "agent-native";

    public string Description => "the agent browser bridge on a real WKWebView: click/type/fill/check/press/scroll as native input, library in the isolated world, honest synthetic fallback";

    public async Task RunAsync(HarnessContext ctx)
    {
        var view = ctx.CreateWebView(HookOptions.Reference);
        ObjC.SendVoidBool(ObjC.Send(ObjC.Send(view.Handle, "configuration"), "preferences"), "setJavaScriptCanOpenWindowsAutomatically:", false);
        var composer = NativeInputScenario.AddComposer(view);
        view.WithoutUserGesture = true; // the harness's own reads give the page nothing
        using var input = new NativeInput(view.Handle);
        using var isolated = new MacIsolatedScripts(view.Handle);
        var page = new Page(view, input, isolated);
        var bridge = new AgentBrowserBridge(new Host(page));

        var open = await Call(bridge, "browser.open_split", new JsonObject { ["url"] = ctx.Server.Url("/native-input") });
        ctx.Check("browser.open_split opens the page", open["ok"]?.GetValue<bool>() == true, open);
        var surface = open["result"]?["surface_id"]?.GetValue<string>() ?? "";
        JsonObject Args(JsonObject a)
        {
            a["surface_id"] = surface;
            return a;
        }

        // click: the page's patched getBoundingClientRect (-999) cannot mislead the isolated library
        var before = await view.EvalAsync("navigator.userActivation.hasBeenActive");
        var click = await Call(bridge, "browser.click", Args(new() { ["selector"] = "#b" }));
        await view.WaitForJsAsync("window.__log.some(e => e.type === 'click')", TimeSpan.FromSeconds(3));
        var clicked = NativeInputScenario.Find(await view.EvalAsync("window.__log"), "click", "b");
        ctx.Check("browser.click: answered as native input; the page got a trusted click with activation and could open a pop-up",
            (string?)click["result"]?["input"] == "native" && before?.GetValue<bool>() == false
            && clicked?["isTrusted"]?.GetValue<bool>() == true && clicked?["userActivation"]?["isActive"]?.GetValue<bool>() == true
            && clicked?["windowOpenReturned"]?.GetValue<bool>() == true,
            new JsonObject { ["answer"] = click.DeepClone(), ["activatedBefore"] = before?.DeepClone(), ["click"] = clicked?.DeepClone() });

        // type and fill
        const string Text = "Привет 日本 ok";
        var type = await Call(bridge, "browser.type", Args(new() { ["selector"] = "#t", ["text"] = Text }));
        var value = await view.EvalAsync("t.value");
        var inputs = await view.EvalAsync("window.__log.filter(e => e.type === 'input' && e.target === 't')");
        ctx.Check("browser.type: clicks into the field, then the text arrives as trusted input events",
            (string?)type["result"]?["input"] == "native" && value?.GetValue<string>() == Text
            && inputs is JsonArray ia && ia.Count > 0 && ia.All(e => e?["isTrusted"]?.GetValue<bool>() == true),
            new JsonObject { ["answer"] = type.DeepClone(), ["value"] = value?.DeepClone() });
        var fill = await Call(bridge, "browser.fill", Args(new() { ["selector"] = "#t", ["text"] = "replaced" }));
        value = await view.EvalAsync("t.value");
        ctx.Check("browser.fill: replaces the field's content", (string?)fill["result"]?["input"] == "native" && value?.GetValue<string>() == "replaced",
            new JsonObject { ["answer"] = fill.DeepClone(), ["value"] = value?.DeepClone() });

        // press: Enter in a textarea makes a line, with a trusted keydown
        await Call(bridge, "browser.fill", Args(new() { ["selector"] = "#ta", ["text"] = "a" }));
        var press = await Call(bridge, "browser.press", Args(new() { ["key"] = "Enter" }));
        await Call(bridge, "browser.type", Args(new() { ["selector"] = "#ta", ["text"] = "b" }));
        value = await view.EvalAsync("ta.value");
        var enter = await view.EvalAsync("window.__log.filter(e => e.type === 'keydown' && e.key === 'Enter')");
        ctx.Check("browser.press Enter: a trusted keydown, the textarea gets a new line",
            (string?)press["result"]?["input"] == "native" && value?.GetValue<string>() == "a\nb"
            && enter is JsonArray ea && ea.Count > 0 && ea.All(e => e?["isTrusted"]?.GetValue<bool>() == true),
            new JsonObject { ["answer"] = press.DeepClone(), ["value"] = value?.DeepClone() });

        // check: clicked only when needed, state reported
        var check = await Call(bridge, "browser.check", Args(new() { ["selector"] = "#cb" }));
        var again = await Call(bridge, "browser.check", Args(new() { ["selector"] = "#cb" }));
        var cbClicks = await view.EvalAsync("window.__log.filter(e => e.type === 'click' && e.target === 'cb')");
        ctx.Check("browser.check: one trusted click, checked; checking again changes nothing",
            check["result"]?["checked"]?.GetValue<bool>() == true && again["result"]?["checked"]?.GetValue<bool>() == true
            && (await view.EvalAsync("cb.checked"))?.GetValue<bool>() == true
            && cbClicks is JsonArray cc && cc.Count == 1 && cc[0]?["isTrusted"]?.GetValue<bool>() == true,
            new JsonObject { ["first"] = check.DeepClone(), ["second"] = again.DeepClone(), ["clicks"] = cbClicks?.DeepClone() });

        // a <select> is set by script (its native menu would open over the app), and the answer says so
        var select = await Call(bridge, "browser.fill", Args(new() { ["selector"] = "#sel", ["text"] = "two" }));
        ctx.Check("browser.fill on a <select>: set by script, answered as synthetic with the reason",
            (await view.EvalAsync("sel.value"))?.GetValue<string>() == "two" && (string?)select["result"]?["input"] == "synthetic"
            && ((string?)select["result"]?["reason"])?.Contains("<select>", StringComparison.Ordinal) == true, select);

        // scroll: the wheel over the element
        var scroll = await Call(bridge, "browser.scroll", Args(new() { ["selector"] = "#scroller", ["dy"] = 300 }));
        await view.WaitForJsAsync("scroller.scrollTop > 0", TimeSpan.FromSeconds(2));
        var wheel = await view.EvalAsync("window.__log.filter(e => e.type === 'wheel')");
        ctx.Check("browser.scroll: trusted wheel events over the element, and it scrolls",
            (string?)scroll["result"]?["input"] == "native" && (await view.EvalAsync("scroller.scrollTop"))?.GetValue<double>() > 0
            && wheel is JsonArray wa && wa.Count > 0 && wa.All(e => e?["isTrusted"]?.GetValue<bool>() == true), scroll);

        // the snapshot and refs come from the isolated world; a ref acts natively too
        var snap = await Call(bridge, "browser.snapshot", Args(new() { ["interactive"] = true }));
        var text = (string?)snap["result"]?["snapshot"] ?? "";
        var dblRef = System.Text.RegularExpressions.Regex.Match(text, "checkbox \"Agree\".*\\[ref=(e\\d+)\\]");
        ctx.Check("browser.snapshot works from the isolated world (refs included)",
            text.Contains("button \"Click me\"", StringComparison.Ordinal) && dblRef.Success, new JsonObject { ["snapshot"] = text });
        if (dblRef.Success)
        {
            var uncheck = await Call(bridge, "browser.uncheck", Args(new() { ["selector"] = "@" + dblRef.Groups[1].Value }));
            ctx.Check("an element ref from the snapshot is acted on natively (uncheck)",
                (string?)uncheck["result"]?["input"] == "native" && (await view.EvalAsync("cb.checked"))?.GetValue<bool>() == false, uncheck);
        }

        // what the page can see of us: nothing
        var probe = await view.EvalAsync("({ webdriver: navigator.webdriver, ours: Object.getOwnPropertyNames(window).filter(k => /^_*omp/i.test(k) || /^_[0-9a-f]{16}$/.test(k)) })");
        ctx.Check("the page sees no library global (it lives in the isolated world) and navigator.webdriver is false",
            probe?["webdriver"]?.GetValue<bool>() == false && probe?["ours"] is JsonArray { Count: 0 }, probe);
        ctx.Check("the composer kept the keyboard through every agent action", ObjC.Send(view.Window, "firstResponder") == composer);

        // a preview without native input: script events, and omp is told so
        var plain = new Page(view, null, isolated);
        var fallback = new AgentBrowserBridge(new Host(plain));
        var reopen = await Call(fallback, "browser.open_split", new JsonObject { ["url"] = ctx.Server.Url("/native-input") });
        var second = reopen["result"]?["surface_id"]?.DeepClone();
        await Call(fallback, "browser.snapshot", new JsonObject { ["surface_id"] = second?.DeepClone() });
        var activated = await view.EvalAsync("navigator.userActivation.hasBeenActive");
        ctx.Check("the bridge's own scripts (snapshot, in the isolated world) give the page no user activation", activated?.GetValue<bool>() == false, activated);
        var fake = await Call(fallback, "browser.click", new JsonObject { ["surface_id"] = second?.DeepClone(), ["selector"] = "#b" });
        var synthetic = NativeInputScenario.Find(await view.EvalAsync("window.__log"), "click", "b");
        ctx.Check("without native input: the click is a script event (isTrusted false, no activation: no pop-up) and the answer says synthetic with the reason",
            (string?)fake["result"]?["input"] == "synthetic" && fake["result"]?["reason"] is not null && synthetic?["isTrusted"]?.GetValue<bool>() == false
            && synthetic?["windowOpenReturned"]?.GetValue<bool>() == false,
            new JsonObject { ["answer"] = fake.DeepClone(), ["click"] = synthetic?.DeepClone() });
    }

    private static async Task<JsonObject> Call(AgentBrowserBridge bridge, string method, JsonObject p)
    {
        var line = new JsonObject { ["id"] = "1", ["method"] = method, ["params"] = p }.ToJsonString();
        var answer = (JsonObject)JsonNode.Parse(await bridge.HandleLineAsync(line, CancellationToken.None))!;
        HarnessLog.Add("bridge", method, answer.DeepClone().AsObject());
        return answer;
    }

    private sealed class Host(IAgentBrowserPage page) : IAgentBrowserHost
    {
        public Task<IAgentBrowserPage> OpenTabAsync(string surfaceId, CancellationToken ct) => Task.FromResult(page);
    }

    /// <summary>The page as the app's <c>PreviewAgentPage</c> offers it, on the harness's WKWebView (WebKit calls on the main thread).</summary>
    private sealed class Page(HarnessWebView view, INativeInput? input, IIsolatedScripts isolated) : IAgentBrowserPage
    {
        public INativeInput? Input => input;

        public IIsolatedScripts? Isolated => isolated;

        public DialogBroker? Dialogs => null;

        public bool IsClosed => false;

        public Task ShowAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<Uri> NavigateAsync(string address, CancellationToken ct)
        {
            await (await MacRuntime.OnMainAsync(() => view.LoadAsync(address)));
            return new Uri(address);
        }

        public async Task<AgentPageState> GetStateAsync(CancellationToken ct)
        {
            var url = await MacRuntime.OnMainAsync(() => view.Url);
            return new AgentPageState(url is null ? null : new Uri(url), false, false, null);
        }

        public async Task<string> RunScriptAsync(string script, CancellationToken ct) =>
            await (await MacRuntime.OnMainAsync(() => view.EvalRawAsync(script))) ?? "";

        public Task<AgentScreenshot> CaptureAsync(CancellationToken ct) =>
            Task.FromException<AgentScreenshot>(new AgentBrowserException("not_supported", "No capture in this scenario."));

        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
#endif
