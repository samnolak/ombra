using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// The agent's browser: omp's browser tool (its cmux backend) driving the preview panel through
/// <see cref="AgentBrowserBridge"/>. The page is a fake that answers scripts the way the engines do.
/// </summary>
public sealed class AgentBrowserTests
{
    /// <summary>A page that records what it was asked and answers through <see cref="Answer"/>.</summary>
    private sealed class FakePage : IAgentBrowserPage
    {
        public List<string> Scripts { get; } = [];
        public List<string> Navigations { get; } = [];
        public bool Shown { get; private set; }
        public bool HasPage { get; set; } = true;
        public AgentPageState State { get; set; } = new(new Uri("http://localhost:3000/"), false, false, null);
        public Func<string, string> Answer { get; set; } = _ => "{\"ok\":true}";

        public Task ShowAsync(CancellationToken ct)
        {
            Shown = true;
            return Task.CompletedTask;
        }

        public Task<Uri> NavigateAsync(string address, CancellationToken ct)
        {
            if (address.StartsWith("file:", StringComparison.Ordinal))
                throw new AgentBrowserException("invalid_params", "Only http and https pages open in the preview (not \"file:\").");
            Navigations.Add(address);
            HasPage = true;
            return Task.FromResult(new Uri(address));
        }

        public Task<AgentPageState> GetStateAsync(CancellationToken ct) => Task.FromResult(State);

        public Task<string> RunScriptAsync(string script, CancellationToken ct)
        {
            if (!HasPage) throw new AgentBrowserException("no_page", "No page is open in the preview yet.");
            Scripts.Add(script);
            return Task.FromResult(Answer(script));
        }
    }

    private static async Task<JsonObject> Call(AgentBrowserBridge bridge, string method, JsonObject? p = null)
    {
        var line = new JsonObject { ["id"] = "1", ["method"] = method, ["params"] = p ?? new JsonObject() }.ToJsonString();
        return (JsonObject)JsonNode.Parse(await bridge.HandleLineAsync(line, CancellationToken.None))!;
    }

    private static async Task<(AgentBrowserBridge bridge, FakePage page, string surface)> Opened(string url = "http://localhost:3000/")
    {
        var page = new FakePage();
        var bridge = new AgentBrowserBridge(page);
        var open = await Call(bridge, "browser.open_split", new JsonObject { ["url"] = url, ["focus"] = false });
        Assert.True(open["ok"]!.GetValue<bool>(), open.ToJsonString());
        return (bridge, page, open["result"]!["surface_id"]!.GetValue<string>());
    }

    [Fact]
    public void Omp_is_pointed_at_the_bridge_unless_the_settings_say_otherwise()
    {
        using var bridge = AgentBrowserBridge.TryStart();
        Assert.NotNull(bridge);
        var spec = bridge.AddTo(new OmpLaunchSpec { FileName = "omp", Environment = new Dictionary<string, string?> { ["LANG"] = "C.UTF-8" } });
        Assert.Equal(bridge.Endpoint, spec.Environment[AgentBrowserBridge.SocketVariable]);
        Assert.Equal(bridge.Password, spec.Environment[AgentBrowserBridge.PasswordVariable]);
        Assert.Null(spec.Environment["CMUX_SURFACE_ID"]); // a cmux terminal's surface never leaks in
        Assert.Equal("C.UTF-8", spec.Environment["LANG"]);

        var own = new OmpLaunchSpec { FileName = "omp", Environment = new Dictionary<string, string?> { [AgentBrowserBridge.SocketVariable] = "/tmp/mine.sock" } };
        Assert.Same(own, bridge.AddTo(own));
    }

    [Fact]
    public async Task Opening_a_page_navigates_the_preview_and_names_a_tab()
    {
        var (bridge, page, surface) = await Opened();
        Assert.Equal(["http://localhost:3000/"], page.Navigations);
        Assert.False(string.IsNullOrEmpty(surface));

        // about:blank only shows the panel
        var blank = await Call(bridge, "browser.open_split", new JsonObject { ["url"] = "about:blank" });
        Assert.True(blank["ok"]!.GetValue<bool>());
        Assert.True(page.Shown);
        Assert.Single(page.Navigations);
    }

    [Fact]
    public async Task A_newer_tab_takes_the_preview_over()
    {
        var (bridge, _, first) = await Opened();
        var second = (await Call(bridge, "browser.open_split", new JsonObject { ["url"] = "http://localhost:3000/b" }))["result"]!["surface_id"]!.GetValue<string>();
        var stale = await Call(bridge, "browser.url.get", new JsonObject { ["surface_id"] = first });
        Assert.False(stale["ok"]!.GetValue<bool>());
        Assert.Equal("not_found", stale["error"]!["code"]!.GetValue<string>());
        Assert.Contains("one page at a time", stale["error"]!["message"]!.GetValue<string>());
        Assert.True((await Call(bridge, "browser.url.get", new JsonObject { ["surface_id"] = second }))["ok"]!.GetValue<bool>());

        // Closing the tab leaves the page in the preview; the tab is gone
        Assert.True((await Call(bridge, "surface.close", new JsonObject { ["surface_id"] = second }))["ok"]!.GetValue<bool>());
        Assert.False((await Call(bridge, "browser.url.get", new JsonObject { ["surface_id"] = second }))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Eval_returns_the_value_and_reports_page_errors()
    {
        var (bridge, page, surface) = await Opened();
        page.Answer = _ => "{\"ok\":{\"title\":\"Hello\",\"n\":2}}";
        var r = await Call(bridge, "browser.eval", new JsonObject { ["surface_id"] = surface, ["script"] = "({ title: document.title, n: 2 })" });
        Assert.Equal("Hello", r["result"]!["value"]!["title"]!.GetValue<string>());
        Assert.Equal(surface, r["result"]!["surface_id"]!.GetValue<string>());
        Assert.Contains("({ title: document.title, n: 2 })", page.Scripts[^1]);

        // Engines that return a string result as JSON text
        page.Answer = _ => "\"{\\\"ok\\\":\\\"x\\\"}\"";
        Assert.Equal("x", (await Call(bridge, "browser.eval", new JsonObject { ["script"] = "'x'" }))["result"]!["value"]!.GetValue<string>());

        page.Answer = _ => "{\"err\":\"boom\",\"name\":\"TypeError\",\"stack\":\"at f\"}";
        var failed = await Call(bridge, "browser.eval", new JsonObject { ["script"] = "f()" });
        Assert.Equal("js_error", failed["error"]!["code"]!.GetValue<string>());
        Assert.Equal("TypeError: boom\nat f", failed["error"]!["message"]!.GetValue<string>());

        page.Answer = _ => "{\"err\":\"not_found: no element matches \\\"#x\\\"\"}";
        var missing = await Call(bridge, "browser.click", new JsonObject { ["selector"] = "#x" });
        Assert.Equal("not_found", missing["error"]!["code"]!.GetValue<string>());
        Assert.Equal("no element matches \"#x\"", missing["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Statements_run_through_eval_when_they_are_not_an_expression()
    {
        var (bridge, page, _) = await Opened();
        // The expression form does not parse (nothing comes back and the run mark is unset); the eval form answers
        page.Answer = s => s.StartsWith("String(globalThis.__ompBridgeRun", StringComparison.Ordinal) ? "false"
            : s.Contains("(0, eval)(", StringComparison.Ordinal) ? "{\"ok\":3}" : "";
        var r = await Call(bridge, "browser.eval", new JsonObject { ["script"] = "const a = 1; a + 2" });
        Assert.Equal(3, r["result"]!["value"]!.GetValue<int>());

        // A script that ran but whose page went away (a click that navigates) has no value; it is not run again
        page.Scripts.Clear();
        page.Answer = s => s.StartsWith("String(globalThis.__ompBridgeRun", StringComparison.Ordinal) ? "true" : "";
        var gone = await Call(bridge, "browser.click", new JsonObject { ["selector"] = "a" });
        Assert.True(gone["ok"]!.GetValue<bool>());
        Assert.Equal(2, page.Scripts.Count);
    }

    [Fact]
    public async Task Waiting_for_a_load_follows_the_preview_and_says_when_it_failed()
    {
        var (bridge, page, _) = await Opened();
        page.Answer = s => s == "document.readyState" ? "\"complete\"" : "{\"ok\":true}";
        page.State = page.State with { IsLoading = true };
        var timedOut = await Call(bridge, "browser.wait", new JsonObject { ["load_state"] = "complete", ["timeout_ms"] = 250 });
        Assert.Equal("timeout", timedOut["error"]!["code"]!.GetValue<string>());

        page.State = page.State with { IsLoading = false };
        Assert.True((await Call(bridge, "browser.wait", new JsonObject { ["load_state"] = "complete", ["timeout_ms"] = 1000 }))["ok"]!.GetValue<bool>());

        page.State = page.State with { LoadFailed = true };
        var failed = await Call(bridge, "browser.wait", new JsonObject { ["load_state"] = "complete", ["timeout_ms"] = 1000 });
        Assert.Equal("navigation_failed", failed["error"]!["code"]!.GetValue<string>());
        Assert.Contains("http://localhost:3000/", failed["error"]!["message"]!.GetValue<string>());

        page.State = page.State with { Unavailable = "The embedded browser needs WebKitGTK on this system." };
        Assert.Equal("unavailable", (await Call(bridge, "browser.wait", new JsonObject { ["timeout_ms"] = 1000 }))["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task What_the_preview_cannot_do_is_said_plainly()
    {
        var (bridge, _, _) = await Opened();
        var shot = await Call(bridge, "browser.screenshot");
        Assert.Equal("not_supported", shot["error"]!["code"]!.GetValue<string>());
        Assert.Contains("tab.observe()", shot["error"]!["message"]!.GetValue<string>());

        var file = await Call(bridge, "browser.navigate", new JsonObject { ["url"] = "file:///tmp/index.html" });
        Assert.Equal("invalid_params", file["error"]!["code"]!.GetValue<string>());

        var none = new AgentBrowserBridge(new FakePage());
        Assert.Equal("not_found", (await Call(none, "browser.eval", new JsonObject { ["script"] = "1" }))["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Actions_and_snapshots_call_the_page_library()
    {
        var (bridge, page, _) = await Opened();
        page.Answer = _ => "{\"ok\":{\"snapshot\":\"- button \\\"Save\\\" [ref=e1]\",\"refs\":{\"e1\":{\"role\":\"button\",\"name\":\"Save\"}},\"url\":\"http://localhost:3000/\"}}";
        var snap = await Call(bridge, "browser.snapshot", new JsonObject { ["interactive"] = true, ["max_depth"] = 12 });
        Assert.Equal("Save", snap["result"]!["refs"]!["e1"]!["name"]!.GetValue<string>());
        Assert.Contains(").snapshot(true, 12, false)", page.Scripts[^1]);

        page.Answer = _ => "{\"ok\":true}";
        Assert.True((await Call(bridge, "browser.fill", new JsonObject { ["selector"] = "@e2", ["text"] = "hi \"you\"" }))["ok"]!.GetValue<bool>());
        Assert.Contains(").act(\"fill\", \"@e2\", {\"text\":", page.Scripts[^1]);
        Assert.True((await Call(bridge, "browser.press", new JsonObject { ["key"] = "Enter" }))["ok"]!.GetValue<bool>());
        Assert.Contains(").press(\"Enter\")", page.Scripts[^1]);
    }

    [Fact]
    public async Task Omp_connects_with_the_password_over_the_socket()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a named pipe on Windows");
        using var bridge = AgentBrowserBridge.TryStart();
        Assert.NotNull(bridge);
        bridge.Page = new FakePage();

        async Task<string[]> Talk(params string[] lines)
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(bridge.Endpoint));
            await using var stream = new NetworkStream(socket);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var answers = new List<string>();
            foreach (var l in lines)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(l + "\n"));
                answers.Add(await reader.ReadLineAsync() ?? "");
            }
            return [.. answers];
        }

        var denied = await Talk("auth nope");
        Assert.Equal("ERROR: Access denied", denied[0]);

        var ok = await Talk("auth " + bridge.Password, "{\"id\":\"7\",\"method\":\"browser.open_split\",\"params\":{\"url\":\"http://localhost:5173/\"}}");
        Assert.Equal("OK", ok[0]);
        var answer = JsonNode.Parse(ok[1])!;
        Assert.Equal("7", answer["id"]!.GetValue<string>());
        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.Equal("http://localhost:5173/", answer["result"]!["url"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public void The_preview_opens_what_the_agent_asks_and_refuses_the_rest()
    {
        var vm = new PreviewViewModel();
        Assert.Null(vm.NavigateForAgent("localhost:5173"));
        Assert.Equal("http://localhost:5173/", vm.CurrentUrl!.AbsoluteUri);
        Assert.True(vm.IsLoading);
        vm.OnNavigationCompleted(vm.CurrentUrl, success: false, canGoBack: false, canGoForward: false);
        Assert.True(vm.LoadFailed);
        vm.OnNavigationStarted(new Uri("http://localhost:5173/"));
        Assert.False(vm.LoadFailed);

        Assert.Contains("Only http and https", vm.NavigateForAgent("file:///tmp/index.html"));
        Assert.Null(vm.RunScript); // no web view in a headless window: the bridge says "no page"

        vm.NoteAgentActivity();
        Assert.True(vm.IsAgentBrowsing);
    }
}
