using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;
using OmpGui.ClientCore.Browser;
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
        /// <summary>Runs a script its own way (a page that hangs, a script that opens a dialog); null: <see cref="Answer"/>.</summary>
        public Func<string, CancellationToken, Task<string>>? Run { get; set; }
        public Func<AgentScreenshot> Capture { get; set; } = () => throw new AgentBrowserException("not_supported", "No capture here.");
        public int Captures { get; private set; }
        public bool ClosedByOmp { get; private set; }
        public bool IsClosed { get; set; }
        public DialogBroker? Dialogs { get; set; }

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
            State = State with { Url = new Uri(address) };
            return Task.FromResult(new Uri(address));
        }

        public Task<AgentPageState> GetStateAsync(CancellationToken ct) => Task.FromResult(State);

        public Task<string> RunScriptAsync(string script, CancellationToken ct)
        {
            if (!HasPage) throw new AgentBrowserException("no_page", "No page is open in the preview yet.");
            Scripts.Add(script);
            return Run is { } run ? run(script, ct) : Task.FromResult(Answer(script));
        }

        public Task<AgentScreenshot> CaptureAsync(CancellationToken ct)
        {
            if (!HasPage) throw new AgentBrowserException("no_page", "No page is open in the preview yet.");
            Captures++;
            return Task.FromResult(Capture());
        }

        public Task CloseAsync(CancellationToken ct)
        {
            ClosedByOmp = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>The pane: a <see cref="FakePage"/> per tab omp opens.</summary>
    private sealed class FakeHost : IAgentBrowserHost
    {
        public List<(string Surface, FakePage Page)> Tabs { get; } = [];
        public Func<FakePage> NewPage { get; set; } = () => new FakePage { HasPage = false };

        public Task<IAgentBrowserPage> OpenTabAsync(string surfaceId, CancellationToken ct)
        {
            var page = NewPage();
            Tabs.Add((surfaceId, page));
            return Task.FromResult<IAgentBrowserPage>(page);
        }
    }

    private static async Task<JsonObject> Call(AgentBrowserBridge bridge, string method, JsonObject? p = null)
    {
        var line = new JsonObject { ["id"] = "1", ["method"] = method, ["params"] = p ?? new JsonObject() }.ToJsonString();
        return (JsonObject)JsonNode.Parse(await bridge.HandleLineAsync(line, CancellationToken.None))!;
    }

    private static string Code(JsonObject answer) => answer["error"]?["code"]?.GetValue<string>() ?? "ok";

    private static string Text(JsonObject answer) => answer["error"]?["message"]?.GetValue<string>() ?? "";

    private static async Task<(AgentBrowserBridge bridge, FakePage page, string surface)> Opened(string url = "http://localhost:3000/")
    {
        var host = new FakeHost();
        var bridge = new AgentBrowserBridge(host);
        var open = await Call(bridge, "browser.open_split", new JsonObject { ["url"] = url, ["focus"] = false });
        Assert.True(open["ok"]!.GetValue<bool>(), open.ToJsonString());
        var surface = open["result"]!["surface_id"]!.GetValue<string>();
        return (bridge, host.Tabs.Single(t => t.Surface == surface).Page, surface);
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
    public async Task Opening_a_page_opens_a_tab_of_its_own_and_navigates_it()
    {
        var host = new FakeHost();
        var bridge = new AgentBrowserBridge(host);
        var open = await Call(bridge, "browser.open_split", new JsonObject { ["url"] = "http://localhost:3000/", ["focus"] = false });
        var surface = open["result"]!["surface_id"]!.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(surface));
        var (named, first) = Assert.Single(host.Tabs);
        Assert.Equal(surface, named);
        Assert.Equal(["http://localhost:3000/"], first.Navigations);

        // about:blank opens another tab and only shows it
        var blank = await Call(bridge, "browser.open_split", new JsonObject { ["url"] = "about:blank" });
        Assert.True(blank["ok"]!.GetValue<bool>());
        Assert.Equal(2, host.Tabs.Count);
        Assert.True(host.Tabs[1].Page.Shown);
        Assert.Empty(host.Tabs[1].Page.Navigations);
        Assert.Single(first.Navigations);

        // A tab the preview refuses to open is closed again, not left behind
        var file = await Call(bridge, "browser.open_split", new JsonObject { ["url"] = "file:///tmp/index.html" });
        Assert.Equal("invalid_params", Code(file));
        Assert.True(host.Tabs[2].Page.ClosedByOmp);
    }

    [Fact]
    public async Task Every_tab_stays_valid_beside_the_others_until_it_is_closed()
    {
        // R6: two omp processes (or two tabs of one) each keep their own page; neither evicts the other
        var host = new FakeHost();
        var bridge = new AgentBrowserBridge(host);
        async Task<string> Open(string url) =>
            (await Call(bridge, "browser.open_split", new JsonObject { ["url"] = url }))["result"]!["surface_id"]!.GetValue<string>();
        var a = await Open("http://localhost:3000/a");
        var b = await Open("http://localhost:4000/b");
        Assert.NotEqual(a, b);
        foreach (var (_, page) in host.Tabs) page.Answer = s => s == "location.href" ? "\"" + page.State.Url!.AbsoluteUri + "\"" : "{\"ok\":true}";

        async Task<string> Url(string surface) =>
            (await Call(bridge, "browser.url.get", new JsonObject { ["surface_id"] = surface }))["result"]!["url"]!.GetValue<string>();
        Assert.Equal("http://localhost:3000/a", await Url(a));
        Assert.Equal("http://localhost:4000/b", await Url(b));

        // Each request goes to its own tab's page
        await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = a, ["selector"] = "#save" });
        Assert.Contains(host.Tabs[0].Page.Scripts, s => s.Contains("\"#save\"", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Tabs[1].Page.Scripts, s => s.Contains("\"#save\"", StringComparison.Ordinal));
        await Call(bridge, "browser.navigate", new JsonObject { ["surface_id"] = a, ["url"] = "http://localhost:3000/next" });
        Assert.Equal(["http://localhost:3000/a", "http://localhost:3000/next"], host.Tabs[0].Page.Navigations);
        Assert.Equal(["http://localhost:4000/b"], host.Tabs[1].Page.Navigations);

        // omp closes b: the pane is told, b is gone, a still answers
        Assert.True((await Call(bridge, "surface.close", new JsonObject { ["surface_id"] = b }))["ok"]!.GetValue<bool>());
        Assert.True(host.Tabs[1].Page.ClosedByOmp);
        var gone = await Call(bridge, "browser.url.get", new JsonObject { ["surface_id"] = b });
        Assert.Equal("not_found", Code(gone));
        Assert.Contains("closed", Text(gone));
        Assert.Equal("http://localhost:3000/next", await Url(a));

        // The user closes a in the pane: omp is told plainly, and a request naming no tab finds none
        host.Tabs[0].Page.IsClosed = true;
        var closed = await Call(bridge, "browser.eval", new JsonObject { ["surface_id"] = a, ["script"] = "1" });
        Assert.Equal("not_found", Code(closed));
        Assert.Contains("user closed this tab", Text(closed));
        Assert.Equal("not_found", Code(await Call(bridge, "browser.url.get")));
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
        page.Answer = s => s.StartsWith("String(globalThis[", StringComparison.Ordinal) ? "false"
            : s.Contains("(0, eval)(", StringComparison.Ordinal) ? "{\"ok\":3}" : "";
        var r = await Call(bridge, "browser.eval", new JsonObject { ["script"] = "const a = 1; a + 2" });
        Assert.Equal(3, r["result"]!["value"]!.GetValue<int>());

        // A script that ran but whose page went away (a click that navigates) has no value; it is not run again
        page.Scripts.Clear();
        page.Answer = s => s.StartsWith("String(globalThis[", StringComparison.Ordinal) ? "true" : "";
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

    /// <summary>A PNG's signature and IHDR header with this size: all the bridge reads of it.</summary>
    private static byte[] Png(int width, int height)
    {
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        return png;
    }

    [Fact]
    public async Task Screenshots_come_back_as_base64_png_the_way_cmux_answers()
    {
        var (bridge, page, surface) = await Opened();
        var png = Png(3, 2);
        page.Capture = () => AgentScreenshot.FromPng(png)!;
        page.Answer = s => s == "location.href" ? "\"http://localhost:3000/\"" : "{\"ok\":true}";
        var shot = await Call(bridge, "browser.screenshot", new JsonObject { ["surface_id"] = surface });
        Assert.True(shot["ok"]!.GetValue<bool>(), shot.ToJsonString());
        var result = shot["result"]!;
        Assert.Equal(png, Convert.FromBase64String(result["png_base64"]!.GetValue<string>()));
        Assert.Equal(3, result["width"]!.GetValue<int>());
        Assert.Equal(2, result["height"]!.GetValue<int>());
        Assert.Equal("http://localhost:3000/", result["url"]!.GetValue<string>());
        Assert.Equal(surface, result["surface_id"]!.GetValue<string>());
        Assert.Equal(1, page.Captures);
    }

    [Fact]
    public async Task A_failed_screenshot_is_reported_with_its_code()
    {
        var (bridge, page, _) = await Opened();
        page.Capture = () => throw new AgentBrowserException("screenshot_failed", "WebKit could not take the screenshot.");
        var failed = await Call(bridge, "browser.screenshot");
        Assert.Equal("screenshot_failed", failed["error"]!["code"]!.GetValue<string>());
        Assert.Contains("WebKit", failed["error"]!["message"]!.GetValue<string>());

        page.Capture = () => new AgentScreenshot([], 0, 0);
        Assert.Equal("screenshot_failed", (await Call(bridge, "browser.screenshot"))["error"]!["code"]!.GetValue<string>());

        // No tab open: refused before the page is asked
        var none = new FakeHost();
        Assert.Equal("not_found", (await Call(new AgentBrowserBridge(none), "browser.screenshot"))["error"]!["code"]!.GetValue<string>());
        Assert.Empty(none.Tabs);
    }

    [Fact]
    public void Png_size_is_read_from_the_header_and_other_bytes_are_refused()
    {
        var shot = AgentScreenshot.FromPng(Png(1280, 800));
        Assert.Equal((1280, 800), (shot!.Width, shot.Height));
        Assert.Null(AgentScreenshot.FromPng([1, 2, 3]));
        Assert.Null(AgentScreenshot.FromPng(Png(0, 10)));
        var jpeg = Png(3, 2);
        jpeg[1] = 0xD8;
        Assert.Null(AgentScreenshot.FromPng(jpeg));
    }

    [Fact]
    public async Task The_native_capture_says_plainly_when_it_cannot_run()
    {
        // No web view yet (no platform handle): the agent is told to wait, nothing native is touched
        var none = await Assert.ThrowsAsync<AgentBrowserException>(() => OmpGui.App.Platform.WebViewSnapshot.CaptureAsync(null, CancellationToken.None));
        Assert.Equal("no_page", none.Code);
        // A handle of no known web engine: not supported, with what to do instead
        var other = await Assert.ThrowsAsync<AgentBrowserException>(() =>
            OmpGui.App.Platform.WebViewSnapshot.CaptureAsync(new Avalonia.Platform.PlatformHandle(1, "HWND"), CancellationToken.None));
        Assert.Equal("not_supported", other.Code);
        Assert.Contains("tab.observe()", other.Message);
    }

    [Fact]
    public async Task What_the_preview_cannot_do_is_said_plainly()
    {
        var (bridge, _, _) = await Opened();
        var shot = await Call(bridge, "browser.screenshot");
        Assert.Equal("not_supported", shot["error"]!["code"]!.GetValue<string>());
        var file = await Call(bridge, "browser.navigate", new JsonObject { ["url"] = "file:///tmp/index.html" });
        Assert.Equal("invalid_params", file["error"]!["code"]!.GetValue<string>());

        var none = new AgentBrowserBridge(new FakeHost());
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
        bridge.Host = new FakeHost();

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
        Assert.Null(vm.ActiveTab.RunScript); // no web view in a headless window: the bridge says "no page"

        vm.NoteAgentActivity();
        Assert.True(vm.IsAgentBrowsing);
    }

    [Fact]
    public async Task Waiting_answers_about_a_second_before_omp_gives_up_on_the_socket()
    {
        // R7: omp destroys the socket at timeout_ms itself; the clean timeout answer must come first
        Assert.Equal(TimeSpan.FromSeconds(29), AgentBrowserBridge.WaitBudget(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), AgentBrowserBridge.WaitBudget(TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.Zero, AgentBrowserBridge.WaitBudget(TimeSpan.Zero));

        var (bridge, page, _) = await Opened();
        page.State = page.State with { IsLoading = true };
        var clock = Stopwatch.StartNew();
        var loading = await Call(bridge, "browser.wait", new JsonObject { ["load_state"] = "load", ["timeout_ms"] = 4500 });
        var took = clock.ElapsedMilliseconds;
        Assert.Equal("timeout", Code(loading));
        Assert.Contains("3500 ms", Text(loading));
        Assert.InRange(took, 3400, 4200);

        // A page that never answers a script (hung) ends at the same deadline instead of holding the socket
        page.State = page.State with { IsLoading = false };
        page.Run = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };
        clock.Restart();
        var hung = await Call(bridge, "browser.wait", new JsonObject { ["selector"] = "#ready", ["timeout_ms"] = 1200 });
        took = clock.ElapsedMilliseconds;
        Assert.Equal("timeout", Code(hung));
        Assert.Contains("selector #ready", Text(hung));
        Assert.InRange(took, 850, 1150);
    }

    [Fact]
    public async Task An_open_dialog_is_reported_instead_of_stalling_later_requests()
    {
        var (bridge, page, _) = await Opened();
        var broker = new DialogBroker();
        page.Dialogs = broker;
        var confirm = broker.ConfirmAsync(new ConfirmRequest("Delete project?") { CausedByAgent = true }, CancellationToken.None);
        page.Scripts.Clear();

        var clock = Stopwatch.StartNew();
        var eval = await Call(bridge, "browser.eval", new JsonObject { ["script"] = "document.title" });
        Assert.Equal("dialog_open", Code(eval));
        Assert.Contains("a confirm dialog: \"Delete project?\"", Text(eval));
        Assert.Contains("browser.press", Text(eval));
        Assert.Equal("dialog_open", Code(await Call(bridge, "browser.snapshot")));
        Assert.Equal("dialog_open", Code(await Call(bridge, "browser.click", new JsonObject { ["selector"] = "#ok" })));
        Assert.Equal("dialog_open", Code(await Call(bridge, "browser.wait", new JsonObject { ["selector"] = "#done", ["timeout_ms"] = 30_000 })));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "nothing waited for the blocked page");
        Assert.Empty(page.Scripts); // nothing was sent into the blocked page
        // The address is still known (from the preview, not from the page)
        Assert.Equal("http://localhost:3000/", (await Call(bridge, "browser.url.get"))["result"]!["url"]!.GetValue<string>());

        // Only Enter and Escape answer it
        Assert.Equal("dialog_open", Code(await Call(bridge, "browser.press", new JsonObject { ["key"] = "a" })));
        Assert.Equal("ok", Code(await Call(bridge, "browser.press", new JsonObject { ["key"] = "Escape" })));
        Assert.False(await confirm);
        Assert.Empty(page.Scripts);
        Assert.Equal("ok", Code(await Call(bridge, "browser.eval", new JsonObject { ["script"] = "1" })));

        var prompt = broker.PromptAsync(new PromptRequest("Your name?", "Ann") { CausedByAgent = true }, CancellationToken.None);
        Assert.Contains("a prompt: \"Your name?\"", Text(await Call(bridge, "browser.eval", new JsonObject { ["script"] = "1" })));
        Assert.Equal("ok", Code(await Call(bridge, "browser.press", new JsonObject { ["key"] = "Enter" })));
        Assert.Equal("Ann", await prompt);
    }

    [Fact]
    public async Task A_script_that_opens_a_dialog_answers_dialog_open_at_once()
    {
        var (bridge, page, _) = await Opened();
        var broker = new DialogBroker();
        page.Dialogs = broker;
        Task<bool>? confirm = null;
        page.Run = (_, _) =>
        {
            // The click handler calls confirm(): the script itself waits until it is answered
            confirm = broker.ConfirmAsync(new ConfirmRequest("Delete project?") { CausedByAgent = true }, CancellationToken.None);
            return confirm.ContinueWith(_ => "{\"ok\":true}", TaskScheduler.Default);
        };
        var clock = Stopwatch.StartNew();
        var click = await Call(bridge, "browser.click", new JsonObject { ["selector"] = "#delete" });
        Assert.Equal("dialog_open", Code(click));
        Assert.Contains("a confirm dialog: \"Delete project?\"", Text(click));
        Assert.Contains("unanswered after 10 s, it goes to the user", Text(click));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));

        page.Run = null;
        Assert.Equal("ok", Code(await Call(bridge, "browser.press", new JsonObject { ["key"] = "Enter" })));
        Assert.True(await confirm!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(broker.CurrentModal);
    }

    [Fact]
    public async Task Alerts_omp_caused_are_accepted_and_its_unanswered_questions_go_to_the_user()
    {
        var broker = new DialogBroker();
        var host = new FakeHost { NewPage = () => new FakePage { HasPage = false, Dialogs = broker } };
        var bridge = new AgentBrowserBridge(host) { HandOffAfter = TimeSpan.FromMilliseconds(300) };
        await Call(bridge, "browser.open_split", new JsonObject { ["url"] = "http://localhost:3000/" });
        var page = host.Tabs[0].Page;

        // A click whose handler calls alert(): accepted (OK) as omp would, and the click goes on
        Task? alert = null;
        page.Run = (script, _) =>
        {
            if (!script.Contains("\"#save\"", StringComparison.Ordinal)) return Task.FromResult("{\"ok\":true}");
            alert = broker.AlertAsync(new AlertRequest("Saved") { CausedByAgent = true }, CancellationToken.None);
            return alert.ContinueWith(_ => "{\"ok\":true}", TaskScheduler.Default);
        };
        Assert.Equal("ok", Code(await Call(bridge, "browser.click", new JsonObject { ["selector"] = "#save" })));
        Assert.True(alert!.IsCompleted);

        // An alert the user caused is theirs: omp is told, nothing answers it for them
        var users = broker.AlertAsync(new AlertRequest("Hello"), CancellationToken.None);
        var told = await Call(bridge, "browser.eval", new JsonObject { ["script"] = "1" });
        Assert.Equal("dialog_open", Code(told));
        Assert.Contains("the user is asked in the OMP GUI preview", Text(told));
        Assert.False(users.IsCompleted);
        broker.Current!.Complete(null);

        // A confirm omp caused and left unanswered goes to the user's card, still open
        var confirm = broker.ConfirmAsync(new ConfirmRequest("Delete project?") { CausedByAgent = true }, CancellationToken.None);
        var dialog = broker.CurrentModal!;
        Assert.True(dialog.RoutedToAgent);
        Assert.Null(broker.Current);
        Assert.Contains("unanswered after 0.3 s", Text(await Call(bridge, "browser.eval", new JsonObject { ["script"] = "1" })));
        var clock = Stopwatch.StartNew();
        while (dialog.RoutedToAgent && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Assert.False(dialog.RoutedToAgent);
        Assert.Same(dialog, broker.Current);
        Assert.False(confirm.IsCompleted);
        dialog.Complete(false);

        // A file chooser, download, permission or pop-up omp's click caused: cmux can't answer those, the user can (at once)
        var file = broker.ChooseFilesAsync(new FileChooserRequest(false, []) { CausedByAgent = true }, CancellationToken.None);
        var download = broker.DownloadAsync(new DownloadRequest("report.csv", null, null) { CausedByAgent = true }, CancellationToken.None);
        Assert.Empty(broker.AgentPending);
        Assert.Equal(BrowserDialogKind.FileChooser, broker.Current!.Kind);
        Assert.Equal(1, broker.QueuedBehindCurrent);
        Assert.False(file.IsCompleted);
        Assert.False(download.IsCompleted);
    }

    [Fact]
    public void The_helper_scripts_live_under_a_random_sealed_name()
    {
        // R8: nothing of ours under a fixed page-world name the page could define first
        var (one, two) = (AgentBrowserScripts.CreateRandom(), AgentBrowserScripts.CreateRandom());
        Assert.NotEqual(one.Name, two.Name);
        Assert.NotEqual(one.RunMark, two.RunMark);
        foreach (var script in new[] { one.Library, one.Wrap("1", 1, viaEval: false), one.RanCheck(1) })
            Assert.DoesNotContain("__omp", script);
        Assert.Contains(AgentBrowserScripts.Literal(one.Name), one.Library);
    }

    /// <summary>A JavaScript engine for running the page library outside a browser: bun (on PATH or omp's runtime pack).</summary>
    private static string? FindBun()
    {
        var exe = OperatingSystem.IsWindows() ? "bun.exe" : "bun";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.Combine(home, "Library", "Application Support"), Path.Combine(home, ".local", "share") })
        {
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            foreach (var app in Directory.EnumerateDirectories(root, "OmpGui*"))
            {
                var runtimes = Path.Combine(app, "runtimes");
                if (!Directory.Exists(runtimes)) continue;
                foreach (var pack in Directory.EnumerateDirectories(runtimes))
                    if (File.Exists(Path.Combine(pack, "bun", exe))) return Path.Combine(pack, "bun", exe);
            }
        }
        return null;
    }

    private static async Task<string> RunJs(string bun, string code)
    {
        var file = Path.Combine(TestProcesses.TempDir("agent-js"), "page.js");
        await File.WriteAllTextAsync(file, code);
        var start = new ProcessStartInfo(bun) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(file);
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(p.ExitCode == 0, await errors);
        return (await output).Trim();
    }

    [Fact]
    public async Task A_page_cannot_swap_or_forge_the_helper_library()
    {
        // The library runs in a real JavaScript engine (JavaScriptCore in bun, WebKit's engine) with a page that tries
        if (FindBun() is not { } bun)
        {
            Assert.Skip("no bun found (PATH or an OMP GUI runtime pack)");
            return;
        }
        var s = AgentBrowserScripts.CreateRandom();
        var name = AgentBrowserScripts.Literal(s.Name);

        // A page that planted the old fixed name gets nothing from it; ours is reused, hidden, frozen and kept
        var reused = JsonNode.Parse(await RunJs(bun, $$"""
            globalThis.__ompAgent = { v: 1, snapshot: () => ({ snapshot: "forged" }) };
            const a = {{s.Library}};
            const b = {{s.Library}};
            const d = Object.getOwnPropertyDescriptor(globalThis, {{name}});
            const r = { same: a === b, notPlanted: a !== globalThis.__ompAgent && typeof a.act === "function",
              hidden: !Object.keys(globalThis).includes({{name}}) && !d.enumerable && !d.configurable && !d.writable, frozen: Object.isFrozen(a) };
            try { globalThis[{{name}}] = { forged: true }; } catch {}
            try { a.snapshot = () => "forged"; } catch {}
            r.kept = globalThis[{{name}}] === a && {{s.Library}} === a && String(a.snapshot).indexOf("forged") < 0;
            console.log(JSON.stringify(r));
            """))!;
        foreach (var check in new[] { "same", "notPlanted", "hidden", "frozen", "kept" })
            Assert.True(reused[check]!.GetValue<bool>(), check + ": " + reused.ToJsonString());

        // A look-alike defined under our name (learned somehow) without our seal is replaced
        Assert.Equal("replaced", await RunJs(bun, $$"""
            const fake = Object.freeze({ v: 2, seal: "guessed", snapshot: () => "forged" });
            Object.defineProperty(globalThis, {{name}}, { value: fake, configurable: true });
            const a = {{s.Library}};
            console.log(a !== fake && a.seal !== "guessed" && typeof a.act === "function" ? "replaced" : "used the fake");
            """));

        // One that cannot be replaced is refused with a code, never used
        Assert.StartsWith("blocked:", await RunJs(bun, $$"""
            Object.defineProperty(globalThis, {{name}}, { value: { v: 2, snapshot: () => "forged" }, configurable: false });
            try { const a = {{s.Library}}; console.log("used " + typeof a); } catch (e) { console.log(String(e.message)); }
            """));
    }

    [AvaloniaFact]
    public void Omps_tabs_show_in_the_preview_and_the_users_pick_holds()
    {
        var vm = new PreviewViewModel();
        var background = new List<(PreviewTab Tab, Uri Url)>();
        vm.TabNavigationRequested += (t, u) => background.Add((t, u));
        var dropped = new List<PreviewTab>();
        vm.TabClosed += dropped.Add;
        vm.NavigateCommand.Execute("localhost:5173");
        Assert.False(vm.ShowTabStrip);

        var a = vm.OpenAgentTab("A");
        Assert.True(vm.ShowTabStrip);
        Assert.Same(a, vm.ActiveTab);
        Assert.True(a.IsActive);
        Assert.False(vm.Tabs[0].IsActive);
        Assert.Null(vm.CurrentUrl);
        Assert.Null(vm.NavigateTabForAgent(a, "localhost:3000"));
        Assert.Equal("http://localhost:3000/", vm.CurrentUrl!.AbsoluteUri);
        Assert.Equal("localhost:3000", a.Header);

        // The user goes back to their own tab: its page comes back, and omp keeps working in the background
        vm.SelectTabCommand.Execute(vm.Tabs[0]);
        Assert.Equal("http://localhost:5173/", vm.CurrentUrl!.AbsoluteUri);
        Assert.Equal("http://localhost:5173", vm.Address);
        Assert.False(vm.FollowsAgent);
        Assert.Null(vm.NavigateTabForAgent(a, "localhost:3000/next"));
        Assert.Same(vm.Tabs[0], vm.ActiveTab);
        Assert.Equal((a, new Uri("http://localhost:3000/next")), Assert.Single(background));
        vm.OnNavigationCompleted(a, new Uri("http://localhost:3000/next"), true, canGoBack: true, canGoForward: false);
        Assert.False(a.IsLoading);
        Assert.Equal("http://localhost:5173/", vm.CurrentUrl!.AbsoluteUri);
        var b = vm.OpenAgentTab("B");
        Assert.Same(vm.Tabs[0], vm.ActiveTab);

        // Picking omp's latest tab follows omp again
        vm.SelectTabCommand.Execute(b);
        Assert.True(vm.FollowsAgent);
        vm.ShowForAgent(a);
        Assert.Same(a, vm.ActiveTab);
        Assert.Equal("http://localhost:3000/next", vm.Address);
        Assert.True(vm.BackCommand.CanExecute(null));

        // omp closes the tab the user looks at: it stays for them; one in the background goes
        vm.CloseTabForAgent(a);
        Assert.Contains(a, vm.Tabs);
        Assert.False(a.IsClosed);
        vm.CloseTabForAgent(b);
        Assert.DoesNotContain(b, vm.Tabs);
        Assert.True(b.IsClosed);
        Assert.Equal([b], dropped);

        // The user closes it: their own tab shows again (and stays: it cannot be closed)
        vm.CloseTabCommand.Execute(a);
        Assert.True(a.IsClosed);
        Assert.Same(vm.Tabs[0], vm.ActiveTab);
        Assert.Equal("http://localhost:5173/", vm.CurrentUrl!.AbsoluteUri);
        Assert.False(vm.ShowTabStrip);
        vm.CloseTabCommand.Execute(vm.Tabs[0]);
        Assert.Single(vm.Tabs);
    }

    [AvaloniaFact]
    public async Task The_preview_gives_each_page_omp_opens_a_tab_of_its_own()
    {
        var main = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs());
        var bridge = new AgentBrowserBridge(new PreviewAgentHost(main));
        async Task<string> Open(string url) =>
            (await Call(bridge, "browser.open_split", new JsonObject { ["url"] = url }))["result"]!["surface_id"]!.GetValue<string>();
        async Task<JsonObject> UrlOf(string surface) => await Call(bridge, "browser.url.get", new JsonObject { ["surface_id"] = surface });

        var a = await Open("http://localhost:3000/a");
        var b = await Open("localhost:4000");
        var preview = main.Preview;
        Assert.True(main.IsPreviewOpen);
        Assert.Equal(["", a, b], preview.Tabs.Select(t => t.Id));
        Assert.Equal(b, preview.ActiveTab.Id);
        Assert.True(preview.ShowTabStrip);
        // Headless: no web view, so scripts say "no page" and each tab answers with its own address
        Assert.Equal("http://localhost:3000/a", (await UrlOf(a))["result"]!["url"]!.GetValue<string>());
        Assert.Equal("http://localhost:4000/", (await UrlOf(b))["result"]!["url"]!.GetValue<string>());
        Assert.Equal("no_page", Code(await Call(bridge, "browser.eval", new JsonObject { ["surface_id"] = a, ["script"] = "1" })));

        // The user closes a in the pane: omp hears it; omp closing b (the tab on screen) leaves it for the user
        preview.CloseTabCommand.Execute(preview.Tabs.Single(t => t.Id == a));
        Assert.Contains("user closed this tab", Text(await UrlOf(a)));
        Assert.Equal("ok", Code(await Call(bridge, "surface.close", new JsonObject { ["surface_id"] = b })));
        Assert.Equal(b, preview.ActiveTab.Id);
        Assert.Equal("not_found", Code(await UrlOf(b)));
        await main.DisposeAsync();
    }
}
