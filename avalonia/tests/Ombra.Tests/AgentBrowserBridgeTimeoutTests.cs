using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Ombra.ClientCore;
using Ombra.ClientCore.Browser;

namespace Ombra.Tests;

/// <summary>
/// The agent's browser when a page stops answering (a content process hung, as on avito.ru): every script and input
/// batch has a deadline inside omp's 30-second socket timer, the request answers a typed error, screenshots and the
/// address never wait on the page, and the connection stays usable for the next request. Also the Augur v1 intent
/// hints the bridge sends the tab's engine before it opens a page, goes to one or clicks a link. Deadlines here are a
/// few hundred ms (<see cref="AgentBrowserBridge.ScriptDeadline"/> is injectable), not the real 10 s.
/// </summary>
public sealed class AgentBrowserBridgeTimeoutTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);

    /// <summary>A tab whose page answers through <see cref="Run"/>; records the scripts and the tokens they got.</summary>
    private sealed class Page : IAgentBrowserPage
    {
        public List<string> Scripts { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public List<string> Navigations { get; } = [];
        public AgentPageState State { get; set; } = new(new Uri("http://localhost:3000/"), false, false, null);
        public Func<string, CancellationToken, Task<string>> Run { get; set; } = (_, _) => Task.FromResult("{\"ok\":2}");
        public Func<AgentScreenshot> Capture { get; set; } = () => throw new AgentBrowserException("not_supported", "No capture here.");
        public string? Refuse { get; set; }
        public INativeInput? Input { get; init; }
        public IIsolatedScripts? Isolated { get; init; }
        public IAgentSpeculation? Speculation { get; init; }
        public DialogBroker? Dialogs => null;
        public bool IsClosed => false;

        public Task ShowAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<Uri> NavigateAsync(string address, CancellationToken ct)
        {
            if (address == Refuse) throw new AgentBrowserException("not_found", "The user closed this tab in the Ombra preview: open a new one.");
            Navigations.Add(address);
            State = State with { Url = new Uri(address) };
            return Task.FromResult(new Uri(address));
        }

        public Task<AgentPageState> GetStateAsync(CancellationToken ct) => Task.FromResult(State);

        public Task<string> RunScriptAsync(string script, CancellationToken ct)
        {
            Scripts.Add(script);
            Tokens.Add(ct);
            return Run(script, ct);
        }

        public Task<AgentScreenshot> CaptureAsync(CancellationToken ct) => Task.FromResult(Capture());
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>The pane: hands out <paramref name="pages"/> in order and logs each open after the hints it got.</summary>
    private sealed class Host(List<string> log, params Page[] pages) : IAgentBrowserHost
    {
        private int _next;
        public IAgentSpeculation? Speculation { get; init; }

        public Task<IAgentBrowserPage> OpenTabAsync(string surfaceId, CancellationToken ct)
        {
            log.Add("open");
            return Task.FromResult<IAgentBrowserPage>(pages[_next++]);
        }
    }

    /// <summary>An engine taking intent hints: what it was told, in order.</summary>
    private sealed class Hints(List<string> log, Func<int>? inputSteps = null) : IAgentSpeculation
    {
        public List<SpeculationHint> Sent { get; } = [];
        public List<string> Cancelled { get; } = [];
        /// <summary>How many input steps had reached the page when each hint went out.</summary>
        public List<int> StepsBefore { get; } = [];

        public void Speculate(SpeculationHint hint)
        {
            Sent.Add(hint);
            StepsBefore.Add(inputSteps?.Invoke() ?? 0);
            log.Add("hint " + hint.Source);
        }

        public void CancelSpeculation(string id)
        {
            Cancelled.Add(id);
            log.Add("cancel");
        }
    }

    /// <summary>Records steps; <see cref="Hang"/> makes a batch wait for its token (a page that never answers the batch's round trip).</summary>
    private sealed class RecordedInput : INativeInput
    {
        public List<NativeInputStep> Steps { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Func<IReadOnlyList<NativeInputStep>, bool>? Hang { get; set; }
        public Func<IReadOnlyList<NativeInputStep>, Exception?>? Fail { get; set; }
        public string? Unsupported => null;
        public bool CanHover => true;

        public async Task SendAsync(IReadOnlyList<NativeInputStep> steps, CancellationToken ct)
        {
            Tokens.Add(ct);
            if (Fail?.Invoke(steps) is { } e) throw e;
            if (Hang?.Invoke(steps) == true) await Task.Delay(Timeout.Infinite, ct);
            Steps.AddRange(steps);
        }
    }

    /// <summary>The app's isolated world: answers the library by name (the target given, a still page, clicks that land).</summary>
    private sealed class World(JsonObject target) : IIsolatedScripts
    {
        public string? Unsupported => null;
        public Func<string, Task<string?>>? Override { get; set; }

        public Task<string?> CallAsync(string functionSource, string argsJson, CancellationToken ct)
        {
            var name = JsonNode.Parse(argsJson)![0]!.GetValue<string>();
            if (Override is { } o) return o(name);
            JsonNode? answer = name switch
            {
                "target" => target.DeepClone(),
                "quiet" => new JsonObject { ["mutation"] = 900, ["network"] = 900, ["ready"] = "complete" },
                "arm" => true,
                "armed" => new JsonObject { ["reached"] = true, ["trusted"] = true },
                _ => null,
            };
            return Task.FromResult(answer?.ToJsonString());
        }
    }

    private static JsonObject Target(string tag = "button", string? href = null) => new()
    {
        ["tag"] = tag, ["visible"] = true, ["inView"] = true, ["disabled"] = false, ["editable"] = false, ["focused"] = false,
        ["checkable"] = false, ["checked"] = false, ["menu"] = false, ["x"] = 120, ["y"] = 45, ["covered"] = null, ["href"] = href,
    };

    private static AgentBrowserBridge Bridge(IAgentBrowserHost host) => new(host)
    {
        ScriptDeadline = Short,
        InputDeadline = Short,
        Pause = static (_, _) => Task.CompletedTask,
        TargetLimit = TimeSpan.FromMilliseconds(200),
    };

    private static async Task<JsonObject> Call(AgentBrowserBridge bridge, string method, JsonObject? p = null)
    {
        var line = new JsonObject { ["id"] = "1", ["method"] = method, ["params"] = p ?? new JsonObject() }.ToJsonString();
        return (JsonObject)JsonNode.Parse(await bridge.HandleLineAsync(line, CancellationToken.None))!;
    }

    private static async Task<string> Open(AgentBrowserBridge bridge, string url = "http://localhost:3000/")
    {
        var open = await Call(bridge, "browser.open_split", new JsonObject { ["url"] = url });
        Assert.True(open["ok"]!.GetValue<bool>(), open.ToJsonString());
        return open["result"]!["surface_id"]!.GetValue<string>();
    }

    private static string Code(JsonObject answer) => answer["error"]?["code"]?.GetValue<string>() ?? "ok";

    private static string Message(JsonObject answer) => answer["error"]?["message"]?.GetValue<string>() ?? "";

    /// <summary>A script that never answers and ignores its token (an engine that cannot stop it).</summary>
    private static Task<string> Never(string _, CancellationToken __) => new TaskCompletionSource<string>().Task;

    [Fact]
    public async Task A_page_that_never_answers_is_reported_unresponsive_at_the_deadline_and_the_script_is_cancelled()
    {
        var page = new Page();
        var bridge = Bridge(new Host([], page));
        var surface = await Open(bridge);
        page.Run = Never;

        var clock = Stopwatch.StartNew();
        var eval = await Call(bridge, "browser.eval", new JsonObject { ["surface_id"] = surface, ["script"] = "document.title" });
        var took = clock.Elapsed;

        Assert.Equal("page_unresponsive", Code(eval));
        Assert.Contains("did not answer within", Message(eval));
        Assert.InRange(took, Short - TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));
        // The engine was told to stop waiting (the Nightly runtime forgets the request: a late answer is dropped)
        Assert.True(page.Tokens.Single().IsCancellationRequested);
        Assert.Single(page.Scripts); // no fallback script went into the hung page after the deadline
    }

    [Fact]
    public async Task An_explicit_timeout_ms_is_honoured_up_to_its_bound_and_answers_script_timeout()
    {
        var page = new Page();
        var bridge = Bridge(new Host([], page));
        var surface = await Open(bridge);
        page.Run = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };

        var clock = Stopwatch.StartNew();
        var eval = await Call(bridge, "browser.eval", new JsonObject { ["surface_id"] = surface, ["script"] = "1", ["timeout_ms"] = 800 });
        var took = clock.Elapsed;

        Assert.Equal("script_timeout", Code(eval));
        Assert.Contains("600 ms", Message(eval)); // answered a quarter early, ahead of omp's own timer for it
        Assert.InRange(took, TimeSpan.FromMilliseconds(550), TimeSpan.FromSeconds(5)); // longer than the default deadline
        Assert.Equal(AgentBrowserBridge.WaitBudget(AgentBrowserBridge.ScriptTimeoutLimit),
            AgentBrowserBridge.ScriptTimeout(new JsonObject { ["timeout_ms"] = 3_600_000 }));
        Assert.Null(AgentBrowserBridge.ScriptTimeout(new JsonObject()));
    }

    [Fact]
    public async Task A_screenshot_and_the_address_of_a_hung_page_come_at_once_without_a_page_script()
    {
        var page = new Page { Capture = () => AgentScreenshot.FromPng(Png())! };
        var bridge = Bridge(new Host([], page));
        var surface = await Open(bridge, "http://localhost:3000/frozen");
        page.Run = Never;

        var clock = Stopwatch.StartNew();
        var shot = await Call(bridge, "browser.screenshot", new JsonObject { ["surface_id"] = surface });
        var url = await Call(bridge, "browser.url.get", new JsonObject { ["surface_id"] = surface });

        Assert.True(clock.Elapsed < Short, $"took {clock.ElapsedMilliseconds} ms");
        Assert.Equal("ok", Code(shot));
        Assert.Equal("http://localhost:3000/frozen", shot["result"]!["url"]!.GetValue<string>());
        Assert.Equal("http://localhost:3000/frozen", url["result"]!["url"]!.GetValue<string>());
        Assert.Empty(page.Scripts);
    }

    [Fact]
    public async Task Omps_title_width_and_geometry_reads_of_a_hung_page_answer_from_the_preview_at_once()
    {
        var page = new Page { State = new(new Uri("http://localhost:3000/"), false, false, null, "Frozen shop") };
        var bridge = Bridge(new Host([], page));
        bridge.ProbeDeadline = TimeSpan.FromMilliseconds(100);
        bridge.ScriptDeadline = TimeSpan.FromSeconds(30); // only the probes' own deadline can end these
        var surface = await Open(bridge);
        page.Run = Never;

        var clock = Stopwatch.StartNew();
        var title = await Call(bridge, "browser.eval", new JsonObject { ["surface_id"] = surface, ["script"] = "document.title" });
        var width = await Call(bridge, "browser.eval", new JsonObject { ["surface_id"] = surface, ["script"] = "window.innerWidth" });
        // omp 18.12.2's GEOMETRY_SCRIPT (cmux rpc.ts), sent beside every observe's snapshot
        const string geometry = "(() => ({ innerWidth: window.innerWidth, innerHeight: window.innerHeight, dpr: window.devicePixelRatio||1, scrollX: window.scrollX, scrollY: window.scrollY, scrollWidth: document.documentElement.scrollWidth, scrollHeight: document.documentElement.scrollHeight }))()";
        var shape = await Call(bridge, "browser.eval", new JsonObject { ["surface_id"] = surface, ["script"] = geometry });

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.ElapsedMilliseconds} ms");
        Assert.Equal("Frozen shop", title["result"]!["value"]!.GetValue<string>());
        Assert.Equal("page_unresponsive", Code(width)); // omp's screenshot goes on without the width
        Assert.Equal("page_unresponsive", Code(shape)); // the preview knows neither the scroll nor the pixel ratio: fails fast

        // Any other script keeps the full deadline
        page.Scripts.Clear();
        var other = Call(bridge, "browser.eval", new JsonObject { ["surface_id"] = surface, ["script"] = "window.innerWidth + 1" });
        await Task.Delay(400);
        Assert.False(other.IsCompleted);
        Assert.False(page.Tokens[^1].IsCancellationRequested);

        // A page that answers in time gives its own title
        page.Run = (_, _) => Task.FromResult("{\"ok\":\"Live title\"}");
        Assert.Equal("Live title", (await Call(bridge, "browser.eval", new JsonObject { ["surface_id"] = surface, ["script"] = "document.title" }))["result"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_evaluations_fallback_scripts_share_one_deadline()
    {
        // Each script answers just inside the deadline on its own: the expression form did not run (a syntax error),
        // the check says nothing ran, the statements form hangs. Three per-script timers would take nearly three deadlines.
        var page = new Page();
        var bridge = Bridge(new Host([], page));
        bridge.ScriptDeadline = TimeSpan.FromMilliseconds(600);
        var surface = await Open(bridge);
        page.Run = async (_, ct) =>
        {
            var n = page.Scripts.Count;
            await Task.Delay(n < 3 ? 400 : Timeout.Infinite, ct);
            return n == 1 ? throw new IsolatedScriptException("SyntaxError: Unexpected token") : "false";
        };

        async Task<(JsonObject Answer, TimeSpan Took)> Eval(JsonObject p)
        {
            page.Scripts.Clear();
            p["surface_id"] = surface;
            p["script"] = "let x = 1; x";
            var clock = Stopwatch.StartNew();
            var answer = await Call(bridge, "browser.eval", p);
            return (answer, clock.Elapsed);
        }

        var (unresponsive, took) = await Eval(new JsonObject());
        Assert.Equal("page_unresponsive", Code(unresponsive));
        Assert.InRange(took, TimeSpan.FromMilliseconds(550), TimeSpan.FromMilliseconds(1100)); // one budget, not 400 + 400 + 600
        Assert.InRange(page.Scripts.Count, 2, 3);

        // An asked timeout_ms is one budget for the whole evaluation too (800 ms: answered at 600)
        var (timedOut, tookAsked) = await Eval(new JsonObject { ["timeout_ms"] = 800 });
        Assert.Equal("script_timeout", Code(timedOut));
        Assert.InRange(tookAsked, TimeSpan.FromMilliseconds(550), TimeSpan.FromMilliseconds(1100));
    }

    [Fact]
    public async Task After_a_timeout_the_same_connection_serves_the_next_request_on_a_healthy_tab()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a named pipe on Windows");
        var hung = new Page();
        var healthy = new Page();
        using var bridge = AgentBrowserBridge.TryStart();
        Assert.NotNull(bridge);
        bridge.Host = new Host([], hung, healthy);
        bridge.ScriptDeadline = Short;

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(bridge.Endpoint));
        await using var stream = new NetworkStream(socket);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        async Task<JsonObject> Send(string method, JsonObject p)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(new JsonObject { ["id"] = "1", ["method"] = method, ["params"] = p }.ToJsonString() + "\n"));
            return (JsonObject)JsonNode.Parse(await reader.ReadLineAsync() ?? "{}")!;
        }
        await stream.WriteAsync(Encoding.UTF8.GetBytes("auth " + bridge.Password + "\n"));
        Assert.Equal("OK", await reader.ReadLineAsync());
        var a = (await Send("browser.open_split", new JsonObject { ["url"] = "http://localhost:3000/a" }))["result"]!["surface_id"]!.GetValue<string>();
        var b = (await Send("browser.open_split", new JsonObject { ["url"] = "http://localhost:3000/b" }))["result"]!["surface_id"]!.GetValue<string>();
        hung.Run = Never;

        Assert.Equal("page_unresponsive", Code(await Send("browser.eval", new JsonObject { ["surface_id"] = a, ["script"] = "1" })));
        // The connection is still open and its slot free: omp's queued request goes through on it
        var next = await Send("browser.eval", new JsonObject { ["surface_id"] = b, ["script"] = "1" });
        Assert.Equal("ok", Code(next));
        Assert.Equal(2, next["result"]!["value"]!.GetValue<int>());
        // and the hung tab itself answers again once its page does
        hung.Run = (_, _) => Task.FromResult("{\"ok\":3}");
        Assert.Equal(3, (await Send("browser.eval", new JsonObject { ["surface_id"] = a, ["script"] = "1" }))["result"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public async Task Engine_failures_keep_their_codes_and_never_become_a_null_value()
    {
        var page = new Page();
        var bridge = Bridge(new Host([], page));
        var surface = await Open(bridge);
        async Task<JsonObject> Eval() => await Call(bridge, "browser.eval", new JsonObject { ["surface_id"] = surface, ["script"] = "1" });

        page.Run = (_, _) => throw new AgentBrowserException("window_closed", "The page's Nightly window closed.");
        Assert.Equal("window_closed", Code(await Eval()));
        page.Run = (_, _) => Task.FromException<string>(new AgentBrowserException("runtime_disconnected", "Nightly stopped."));
        Assert.Equal("runtime_disconnected", Code(await Eval()));
        page.Run = (_, _) => Task.FromException<string>(new IOException("the web view's pipe broke"));
        Assert.Equal("internal_error", Code(await Eval()));

        // The engine's word that the script did not run is the one failure that means "no value": the statements form runs
        page.Scripts.Clear();
        var calls = 0;
        page.Run = (_, _) => (++calls) switch
        {
            1 => Task.FromException<string>(new IsolatedScriptException("SyntaxError: Unexpected token")),
            2 => Task.FromResult("false"),
            _ => Task.FromResult("{\"ok\":5}"),
        };
        var statements = await Eval();
        Assert.Equal(5, statements["result"]!["value"]!.GetValue<int>());
        Assert.Equal(3, page.Scripts.Count);

        // The isolated world's failures keep their codes too
        var world = new World(Target()) { Override = _ => Task.FromException<string?>(new AgentBrowserException("window_closed", "gone")) };
        var tab = new Page { Input = new RecordedInput(), Isolated = world };
        var other = Bridge(new Host([], tab));
        Assert.Equal("window_closed", Code(await Call(other, "browser.click", new JsonObject { ["surface_id"] = await Open(other), ["selector"] = "#go" })));
    }

    [Fact]
    public async Task A_slow_but_answering_action_answers_within_the_request_budget()
    {
        // Every library call answers in 150 ms, inside the 250 ms script deadline; a click takes six of them
        var still = new World(Target());
        var slow = new World(Target())
        {
            Override = async name =>
            {
                await Task.Delay(150);
                return await still.CallAsync("", new JsonArray(name, new JsonArray()).ToJsonString(), CancellationToken.None);
            },
        };
        var input = new RecordedInput();
        var page = new Page { Input = input, Isolated = slow };
        var bridge = Bridge(new Host([], page));
        bridge.RequestBudget = TimeSpan.FromMilliseconds(500);
        var surface = await Open(bridge);

        var clock = Stopwatch.StartNew();
        var click = await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go" });
        var took = clock.Elapsed;

        Assert.Equal("page_unresponsive", Code(click));
        Assert.InRange(took, TimeSpan.FromMilliseconds(450), TimeSpan.FromMilliseconds(1100)); // the request's budget, not 6 × 150 ms

        // The same page with time enough: every step answers, the click goes through
        bridge.RequestBudget = TimeSpan.FromSeconds(5);
        Assert.Equal("ok", Code(await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go" })));
        Assert.Contains(input.Steps, s => s is NativeMouseStep { Action: NativeMouseAction.Down });
    }

    [Fact]
    public async Task An_input_batch_the_page_never_answers_ends_at_its_deadline()
    {
        var input = new RecordedInput { Hang = steps => steps.Any(s => s is NativeMouseStep { Action: NativeMouseAction.Down }) };
        var page = new Page { Input = input, Isolated = new World(Target()) };
        var bridge = Bridge(new Host([], page));
        var surface = await Open(bridge);

        var click = await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go" });

        Assert.Equal("page_unresponsive", Code(click));
        Assert.True(input.Tokens[^1].IsCancellationRequested); // the batch was told to stop: its serial slot is free
    }

    // ── Augur v1 intent hints ──

    [Fact]
    public async Task Opening_and_going_to_a_page_hint_first_and_a_refused_navigation_cancels_its_hint()
    {
        var log = new List<string>();
        var engine = new Hints(log);
        var page = new Page { Speculation = engine, Refuse = "https://refused.example/" };
        var bridge = Bridge(new Host(log, page) { Speculation = engine });

        var surface = await Open(bridge, "https://example.com/start");
        Assert.Equal(["hint agent_open", "open"], log);
        Assert.Equal(SpeculationHint.AgentOpen, engine.Sent[0].Source);
        Assert.Equal("https://example.com/start", engine.Sent[0].Url.AbsoluteUri);

        Assert.Equal("ok", Code(await Call(bridge, "browser.navigate", new JsonObject { ["surface_id"] = surface, ["url"] = "https://example.com/next" })));
        Assert.Equal(SpeculationHint.AgentGoto, engine.Sent[1].Source);
        Assert.Empty(engine.Cancelled);

        var refused = await Call(bridge, "browser.navigate", new JsonObject { ["surface_id"] = surface, ["url"] = "https://refused.example/" });
        Assert.Equal("not_found", Code(refused));
        Assert.Equal([engine.Sent[2].Id], engine.Cancelled);

        // Nothing for what is not an http(s) page
        Assert.Equal("ok", Code(await Call(bridge, "browser.navigate", new JsonObject { ["surface_id"] = surface, ["url"] = "about:blank" })));
        Assert.Equal(3, engine.Sent.Count);
    }

    [Fact]
    public async Task A_click_on_a_link_hints_its_address_before_the_pointer_moves()
    {
        var log = new List<string>();
        var input = new RecordedInput();
        var engine = new Hints(log, () => input.Steps.Count);
        var page = new Page { Input = input, Isolated = new World(Target("a", "https://example.com/item/7")), Speculation = engine };
        var bridge = Bridge(new Host(log, page));
        var surface = await Open(bridge);

        var click = await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "@e3" });

        Assert.Equal("ok", Code(click));
        var hint = Assert.Single(engine.Sent);
        Assert.Equal(SpeculationHint.AgentClick, hint.Source);
        Assert.Equal("https://example.com/item/7", hint.Url.AbsoluteUri);
        Assert.Equal(0, engine.StepsBefore.Single()); // before any input reached the page
        Assert.InRange(hint.LeadMs, 50, 120); // the first move has no path: what is left is the pause before the press
        Assert.Empty(engine.Cancelled);
    }

    [Fact]
    public async Task A_click_on_something_that_is_not_a_link_hints_nothing()
    {
        var engine = new Hints([]);
        var page = new Page { Input = new RecordedInput(), Isolated = new World(Target("button")), Speculation = engine };
        var bridge = Bridge(new Host([], page));
        var surface = await Open(bridge);

        Assert.Equal("ok", Code(await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#save" })));
        Assert.Empty(engine.Sent);
        Assert.Empty(engine.Cancelled);
    }

    [Fact]
    public async Task A_click_on_a_link_that_fails_or_misses_cancels_its_hint()
    {
        var input = new RecordedInput { Fail = steps => steps.Any(s => s is NativeMouseStep { Action: NativeMouseAction.Down }) ? new TimeoutException("The user is using this page.") : null };
        var engine = new Hints([]);
        var world = new World(Target("a", "https://example.com/item/7"));
        var page = new Page { Input = input, Isolated = world, Speculation = engine };
        var bridge = Bridge(new Host([], page));
        var surface = await Open(bridge);

        Assert.Equal("busy", Code(await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "@e3" })));
        Assert.Equal([Assert.Single(engine.Sent).Id], engine.Cancelled);

        // The click landed on something else (a cover appeared): no navigation to that link follows
        input.Fail = null;
        var armed = new World(Target("a", "https://example.com/item/8"));
        world.Override = name => name == "armed"
            ? Task.FromResult<string?>("{\"reached\":false,\"trusted\":true}")
            : armed.CallAsync("", new JsonArray(name, new JsonArray()).ToJsonString(), CancellationToken.None);
        var missed = await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "@e3" });
        Assert.Equal("ok", Code(missed));
        Assert.Equal(2, engine.Sent.Count);
        Assert.Equal(engine.Sent[1].Id, engine.Cancelled[^1]);
    }

    /// <summary>A PNG's signature and IHDR header (3×2): all the bridge reads of it.</summary>
    private static byte[] Png()
    {
        var png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 3);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 2);
        return png;
    }
}
