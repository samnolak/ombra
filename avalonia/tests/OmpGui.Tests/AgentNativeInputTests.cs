using System.Text.Json.Nodes;
using OmpGui.ClientCore;
using OmpGui.ClientCore.Browser;

namespace OmpGui.Tests;

/// <summary>
/// The agent browser's element actions as input the engine treats like the user's (BROWSER_PLAN B4): the page library
/// runs in the app's isolated world and says where to act; the actions go out as native input steps with pauses;
/// where that is not possible the library dispatches script events and the answer says so. The engine side
/// (WKWebView) is proven in the windowless harness (<c>run.sh native-input</c>, <c>run.sh agent-native</c>).
/// </summary>
public sealed class AgentNativeInputTests
{
    /// <summary>Records every step; <see cref="Fail"/> can make a send throw (the user is busy, a step the engine refuses).</summary>
    private sealed class RecordingInput : INativeInput
    {
        public List<NativeInputStep> Steps { get; } = [];
        public string? Unsupported { get; init; }
        public bool CanHover { get; init; } = true;
        public Func<IReadOnlyList<NativeInputStep>, Exception?>? Fail { get; set; }

        public Task SendAsync(IReadOnlyList<NativeInputStep> steps, CancellationToken ct)
        {
            if (Fail?.Invoke(steps) is { } e) return Task.FromException(e);
            Steps.AddRange(steps);
            return Task.CompletedTask;
        }
    }

    /// <summary>The app's isolated world: answers the library's calls by name.</summary>
    private sealed class FakeWorld : IIsolatedScripts
    {
        public List<(string Name, JsonArray Args)> Calls { get; } = [];
        public List<string> Sources { get; } = [];
        public string? Unsupported => null;
        public Func<string, JsonArray, JsonNode?> Answer { get; set; } = (_, _) => null;

        public Task<string?> CallAsync(string functionSource, string argsJson, CancellationToken ct)
        {
            Sources.Add(functionSource);
            var call = JsonNode.Parse(argsJson)!.AsArray();
            var name = call[0]!.GetValue<string>();
            var args = call[1]!.AsArray();
            Calls.Add((name, args));
            try
            {
                return Task.FromResult(Answer(name, args)?.ToJsonString());
            }
            catch (Exception e)
            {
                return Task.FromException<string?>(e);
            }
        }

        public IEnumerable<string> Names => Calls.Select(c => c.Name);
    }

    private sealed class Page(INativeInput? input, IIsolatedScripts? world) : IAgentBrowserPage
    {
        public List<string> PageScripts { get; } = [];
        public int Actions { get; private set; }
        public INativeInput? Input => input;
        public IIsolatedScripts? Isolated => world;
        public DialogBroker? Dialogs => null;
        public bool IsClosed => false;

        public IDisposable? BeginAction()
        {
            Actions++;
            return null;
        }

        public Task ShowAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<Uri> NavigateAsync(string address, CancellationToken ct) => Task.FromResult(new Uri(address));
        public Task<AgentPageState> GetStateAsync(CancellationToken ct) => Task.FromResult(new AgentPageState(new Uri("http://localhost:3000/"), false, false, null));

        public Task<string> RunScriptAsync(string script, CancellationToken ct)
        {
            PageScripts.Add(script);
            return Task.FromResult("{\"ok\":true}");
        }

        public Task<AgentScreenshot> CaptureAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Host(IAgentBrowserPage page) : IAgentBrowserHost
    {
        public Task<IAgentBrowserPage> OpenTabAsync(string surfaceId, CancellationToken ct) => Task.FromResult(page);
    }

    /// <summary>A button at (120, 45): visible, uncovered, not moving.</summary>
    private static JsonObject Button(Action<JsonObject>? change = null)
    {
        var t = new JsonObject
        {
            ["tag"] = "button", ["visible"] = true, ["inView"] = true, ["disabled"] = false, ["editable"] = false, ["focused"] = false,
            ["checkable"] = false, ["checked"] = false, ["menu"] = false, ["x"] = 120, ["y"] = 45, ["covered"] = null,
        };
        change?.Invoke(t);
        return t;
    }

    private static readonly JsonObject Still = new() { ["mutation"] = 900, ["network"] = 900, ["ready"] = "complete" };

    /// <summary>A world where the target is <paramref name="target"/>, the page is still and clicks reach the element.</summary>
    private static FakeWorld World(JsonObject target) => new()
    {
        Answer = (name, _) => name switch
        {
            "target" => target.DeepClone(),
            "quiet" => Still.DeepClone(),
            "arm" => true,
            "armed" => new JsonObject { ["reached"] = true, ["trusted"] = true },
            "exists" => true,
            _ => null,
        },
    };

    private static async Task<(AgentBrowserBridge Bridge, string Surface)> Open(IAgentBrowserPage page)
    {
        var bridge = new AgentBrowserBridge(new Host(page)) { Pause = static (_, _) => Task.CompletedTask, TargetLimit = TimeSpan.FromMilliseconds(200) };
        var open = await Call(bridge, "browser.open_split", new JsonObject { ["url"] = "http://localhost:3000/" });
        return (bridge, open["result"]!["surface_id"]!.GetValue<string>());
    }

    private static async Task<JsonObject> Call(AgentBrowserBridge bridge, string method, JsonObject p)
    {
        var line = new JsonObject { ["id"] = "1", ["method"] = method, ["params"] = p }.ToJsonString();
        return (JsonObject)JsonNode.Parse(await bridge.HandleLineAsync(line, CancellationToken.None))!;
    }

    private static string Code(JsonObject answer) => answer["error"]?["code"]?.GetValue<string>() ?? "ok";

    private static IEnumerable<NativeMouseStep> Presses(RecordingInput input) =>
        input.Steps.OfType<NativeMouseStep>().Where(m => m.Action != NativeMouseAction.Move);

    [Fact]
    public async Task A_click_goes_to_the_page_as_native_input_where_the_isolated_library_found_the_element()
    {
        var input = new RecordingInput();
        var world = World(Button());
        var page = new Page(input, world);
        var (bridge, surface) = await Open(page);
        var before = page.PageScripts.Count;

        var answer = await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go" });

        Assert.Equal("native", answer["result"]!["input"]!.GetValue<string>());
        Assert.Null(answer["result"]!["note"]);
        // the pointer travels there, then one press and release at the element
        Assert.IsType<NativeMouseStep>(input.Steps[0]);
        Assert.Equal(NativeMouseAction.Move, ((NativeMouseStep)input.Steps[0]).Action);
        Assert.Equal(
            [new NativeMouseStep(NativeMouseAction.Down, 120, 45, NativeMouseButton.Left, 1, NativeModifiers.None),
             new NativeMouseStep(NativeMouseAction.Up, 120, 45, NativeMouseButton.Left, 1, NativeModifiers.None)],
            Presses(input));
        // the library ran in the isolated world only: nothing of it in the page's own world
        Assert.Equal(before, page.PageScripts.Count);
        Assert.All(world.Sources, s => Assert.StartsWith("(name, args) => ", s));
        Assert.Equal(["target", "target", "arm", "quiet", "armed"], world.Names);
        Assert.Equal(1, page.Actions); // what the page asks meanwhile goes to omp first
    }

    [Fact]
    public async Task A_double_click_presses_twice_with_the_click_count()
    {
        var input = new RecordingInput();
        var (bridge, surface) = await Open(new Page(input, World(Button())));
        await Call(bridge, "browser.dblclick", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go" });
        Assert.Equal([1, 1, 2, 2], Presses(input).Select(p => p.Clicks));
        Assert.Equal([NativeMouseAction.Down, NativeMouseAction.Up, NativeMouseAction.Down, NativeMouseAction.Up], Presses(input).Select(p => p.Action));
    }

    [Fact]
    public async Task Typing_clicks_into_the_field_then_commits_each_character_and_presses_Enter_for_line_breaks()
    {
        var input = new RecordingInput();
        var (bridge, surface) = await Open(new Page(input, World(Button(t => { t["tag"] = "input"; t["editable"] = true; }))));

        var answer = await Call(bridge, "browser.type", new JsonObject { ["surface_id"] = surface, ["selector"] = "#name", ["text"] = "Ян\nok" });

        Assert.Equal("native", answer["result"]!["input"]!.GetValue<string>());
        Assert.Equal(2, Presses(input).Count()); // the click that focuses it
        Assert.Equal(["Я", "н", "o", "k"], input.Steps.OfType<NativeTextStep>().Select(t => t.Text));
        Assert.Equal([true, false], input.Steps.OfType<NativeKeyStep>().Select(k => k.Down));
        Assert.All(input.Steps.OfType<NativeKeyStep>(), k => Assert.Equal("Enter", k.Key));
        // text before the Enter, text after it
        var order = input.Steps.Where(s => s is NativeTextStep or NativeKeyStep).Select(s => s is NativeTextStep t ? t.Text : "⏎").ToList();
        Assert.Equal(["Я", "н", "⏎", "⏎", "o", "k"], order);
    }

    [Fact]
    public async Task A_focused_field_is_typed_into_without_a_click_and_fill_replaces_its_content_at_once()
    {
        var input = new RecordingInput();
        var (bridge, surface) = await Open(new Page(input, World(Button(t => { t["tag"] = "textarea"; t["editable"] = true; t["focused"] = true; }))));

        await Call(bridge, "browser.fill", new JsonObject { ["surface_id"] = surface, ["selector"] = "#bio", ["text"] = "новый текст" });

        Assert.Empty(Presses(input));
        var shortcut = OperatingSystem.IsMacOS() ? NativeModifiers.Meta : NativeModifiers.Control;
        var keys = input.Steps.OfType<NativeKeyStep>().Where(k => k.Down).Select(k => (k.Key, k.Modifiers)).ToList();
        Assert.Equal([(shortcut == NativeModifiers.Meta ? "Meta" : "Control", shortcut), ("a", shortcut), ("Backspace", NativeModifiers.None)], keys);
        Assert.Equal(["новый текст"], input.Steps.OfType<NativeTextStep>().Select(t => t.Text));
    }

    [Fact]
    public async Task Selects_and_menu_fields_are_set_by_script_events_and_the_answer_says_so()
    {
        var input = new RecordingInput();
        var world = World(Button(t => { t["tag"] = "select"; t["menu"] = true; }));
        var (bridge, surface) = await Open(new Page(input, world));

        var answer = await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#country" });

        Assert.Empty(input.Steps);
        Assert.Equal("synthetic", answer["result"]!["input"]!.GetValue<string>());
        Assert.Contains("<select>", answer["result"]!["reason"]!.GetValue<string>());
        var act = world.Calls.Single(c => c.Name == "act");
        Assert.Equal(["click", "#country"], act.Args.Take(2).Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public async Task Without_native_input_the_actions_are_script_events_and_the_answer_says_why()
    {
        var world = World(Button());
        var (bridge, surface) = await Open(new Page(null, world));
        var none = await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go" });
        Assert.Equal("synthetic", none["result"]!["input"]!.GetValue<string>());
        Assert.Equal("This preview has no native input.", none["result"]!["reason"]!.GetValue<string>());
        Assert.Contains(world.Calls, c => c.Name == "act");

        var engine = new RecordingInput { Unsupported = "Native input is not built for WebView2 yet." };
        (bridge, surface) = await Open(new Page(engine, World(Button())));
        var press = await Call(bridge, "browser.press", new JsonObject { ["surface_id"] = surface, ["key"] = "Enter" });
        Assert.Equal("Native input is not built for WebView2 yet.", press["result"]!["reason"]!.GetValue<string>());
        Assert.Empty(engine.Steps);
    }

    [Fact]
    public async Task Without_an_isolated_world_the_library_runs_in_the_page_and_input_is_still_native()
    {
        var input = new RecordingInput();
        var page = new Page(input, null);
        var (bridge, surface) = await Open(page);
        // The page world answers every script with {"ok":true}: a target that is not an object means the page changed
        var answer = await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go" });
        Assert.Equal("not_found", Code(answer));
        Assert.Contains(page.PageScripts, s => s.Contains(".target(", StringComparison.Ordinal));
        Assert.Empty(input.Steps);
    }

    [Fact]
    public async Task A_covered_hidden_or_disabled_element_is_reported_with_what_to_do()
    {
        async Task<JsonObject> ClickOn(JsonObject target)
        {
            var (bridge, surface) = await Open(new Page(new RecordingInput(), World(target)));
            return await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go" });
        }
        var covered = await ClickOn(Button(t => t["covered"] = "div.cookie-banner"));
        Assert.Equal("not_actionable", Code(covered));
        Assert.Contains("covered by div.cookie-banner", covered["error"]!["message"]!.GetValue<string>());
        Assert.Contains("is not visible", (await ClickOn(Button(t => t["visible"] = false)))["error"]!["message"]!.GetValue<string>());
        Assert.Contains("is disabled", (await ClickOn(Button(t => t["disabled"] = true)))["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_element_that_is_still_moving_is_clicked_where_it_comes_to_rest()
    {
        var input = new RecordingInput();
        var y = 0;
        var world = World(Button());
        var still = world.Answer;
        world.Answer = (name, args) => name == "target" ? Button(t => t["y"] = Math.Min(45, y += 15)) : still(name, args);
        var (bridge, surface) = await Open(new Page(input, world));
        await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go" });
        Assert.All(Presses(input), p => Assert.Equal(45, p.Y));
    }

    [Fact]
    public async Task Hover_that_does_not_reach_the_page_goes_by_script_events_and_says_why()
    {
        var input = new RecordingInput();
        var world = World(Button());
        var still = world.Answer;
        world.Answer = (name, args) => name == "armed" ? new JsonObject { ["reached"] = false, ["trusted"] = false } : still(name, args);
        var (bridge, surface) = await Open(new Page(input, world));

        var answer = await Call(bridge, "browser.hover", new JsonObject { ["surface_id"] = surface, ["selector"] = "#menu" });

        Assert.All(input.Steps, s => Assert.Equal(NativeMouseAction.Move, Assert.IsType<NativeMouseStep>(s).Action));
        Assert.Equal("synthetic", answer["result"]!["input"]!.GetValue<string>());
        Assert.Contains("active one", answer["result"]!["reason"]!.GetValue<string>());
        Assert.Contains(world.Calls, c => c.Name == "act" && c.Args[0]!.GetValue<string>() == "hover");

        var noHover = new RecordingInput { CanHover = false };
        (bridge, surface) = await Open(new Page(noHover, World(Button())));
        var plain = await Call(bridge, "browser.hover", new JsonObject { ["surface_id"] = surface, ["selector"] = "#menu" });
        Assert.Equal("synthetic", plain["result"]!["input"]!.GetValue<string>());
        Assert.Empty(noHover.Steps);
    }

    [Fact]
    public async Task Check_clicks_only_when_the_state_differs_and_reports_it()
    {
        var input = new RecordingInput();
        var isChecked = false;
        var world = World(Button());
        var still = world.Answer;
        world.Answer = (name, args) => name == "target" ? Button(t => { t["tag"] = "input"; t["checkable"] = true; t["checked"] = isChecked; }) : still(name, args);
        input.Fail = steps =>
        {
            if (steps.Any(s => s is NativeMouseStep { Action: NativeMouseAction.Up })) isChecked = !isChecked; // the page toggles on click
            return null;
        };
        var (bridge, surface) = await Open(new Page(input, world));

        var first = await Call(bridge, "browser.check", new JsonObject { ["surface_id"] = surface, ["selector"] = "#agree" });
        var second = await Call(bridge, "browser.check", new JsonObject { ["surface_id"] = surface, ["selector"] = "#agree" });

        Assert.True(first["result"]!["checked"]!.GetValue<bool>());
        Assert.True(second["result"]!["checked"]!.GetValue<bool>());
        Assert.Single(Presses(input), p => p.Action == NativeMouseAction.Down);
        var notCheckable = await Call(bridge, "browser.uncheck", new JsonObject { ["surface_id"] = surface, ["selector"] = "#agree" });
        Assert.Equal("ok", Code(notCheckable));

        world.Answer = (name, args) => name == "target" ? Button() : still(name, args);
        Assert.Equal("invalid_target", Code(await Call(bridge, "browser.check", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go" })));
    }

    [Fact]
    public async Task Errors_from_the_isolated_library_keep_their_codes_and_a_busy_user_is_reported()
    {
        var input = new RecordingInput();
        var world = new FakeWorld
        {
            Answer = (_, _) => throw new IsolatedScriptException("Error: not_found: no element matches \"#gone\"\nresolve@\nact@"),
        };
        var (bridge, surface) = await Open(new Page(input, world));
        var missing = await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#gone" });
        Assert.Equal("not_found", Code(missing));
        Assert.Equal("no element matches \"#gone\"", missing["error"]!["message"]!.GetValue<string>());

        var busy = new RecordingInput { Fail = _ => new TimeoutException("The user is using this page in the preview: try again when they stop.") };
        (bridge, surface) = await Open(new Page(busy, World(Button())));
        var answer = await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go" });
        Assert.Equal("busy", Code(answer));
    }

    [Fact]
    public async Task After_an_action_the_answer_waits_until_the_page_is_still()
    {
        var quiet = 0;
        var world = World(Button());
        var still = world.Answer;
        world.Answer = (name, args) => name == "quiet"
            ? new JsonObject { ["mutation"] = ++quiet < 3 ? 20 : 400, ["network"] = 900, ["ready"] = "complete" }
            : still(name, args);
        var (bridge, surface) = await Open(new Page(new RecordingInput(), world));
        await Call(bridge, "browser.click", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go" });
        Assert.Equal(3, quiet);
    }

    [Fact]
    public async Task Snapshot_wait_and_scroll_use_the_isolated_world()
    {
        var input = new RecordingInput();
        var world = World(Button());
        var still = world.Answer;
        world.Answer = (name, args) => name switch
        {
            "snapshot" => new JsonObject { ["snapshot"] = "- button \"Go\" [ref=e1]", ["refs"] = new JsonObject() },
            "viewport" => new JsonObject { ["x"] = 400, ["y"] = 300 },
            _ => still(name, args),
        };
        var page = new Page(input, world);
        var (bridge, surface) = await Open(page);
        var before = page.PageScripts.Count;

        var snap = await Call(bridge, "browser.snapshot", new JsonObject { ["surface_id"] = surface });
        Assert.Equal("- button \"Go\" [ref=e1]", snap["result"]!["snapshot"]!.GetValue<string>());
        Assert.Equal("ok", Code(await Call(bridge, "browser.wait", new JsonObject { ["surface_id"] = surface, ["selector"] = "#go", ["timeout_ms"] = 2000 })));
        var scroll = await Call(bridge, "browser.scroll", new JsonObject { ["surface_id"] = surface, ["dy"] = 250 });
        Assert.Equal("native", scroll["result"]!["input"]!.GetValue<string>());
        var wheel = input.Steps.OfType<NativeWheelStep>().ToList();
        Assert.Equal(250, wheel.Sum(w => w.DeltaY));
        Assert.All(wheel, w => Assert.Equal((400, 300), (w.X, w.Y)));
        Assert.Equal(before, page.PageScripts.Count);
        Assert.Contains("exists", world.Names);
    }

    // ── the pacing helpers on their own ──

    [Fact]
    public void Key_combinations_are_read_as_Playwright_and_omp_write_them()
    {
        Assert.Equal(("Enter", NativeModifiers.None), NativeInputActions.ParseCombo("Enter", NativeModifiers.Meta));
        Assert.Equal(("a", NativeModifiers.Control | NativeModifiers.Shift), NativeInputActions.ParseCombo("Ctrl+Shift+a", NativeModifiers.Meta));
        Assert.Equal(("z", NativeModifiers.Meta), NativeInputActions.ParseCombo("ControlOrMeta+z", NativeModifiers.Meta));
        Assert.Equal(("z", NativeModifiers.Control), NativeInputActions.ParseCombo("ControlOrMeta+z", NativeModifiers.Control));
        Assert.Equal(("+", NativeModifiers.Shift), NativeInputActions.ParseCombo("Shift++", NativeModifiers.Meta));
        Assert.Equal((" ", NativeModifiers.None), NativeInputActions.ParseCombo("Space", NativeModifiers.Meta));
        Assert.Equal(("Escape", NativeModifiers.Alt), NativeInputActions.ParseCombo("Option+Esc", NativeModifiers.Meta));
        Assert.Throws<ArgumentException>(() => NativeInputActions.ParseCombo("Hyper+a", NativeModifiers.Meta));
    }

    [Fact]
    public async Task Pressing_a_combination_holds_the_modifiers_around_the_key()
    {
        var input = new RecordingInput();
        var actions = new NativeInputActions(input, static (_, _) => Task.CompletedTask);
        await actions.PressAsync("Shift+Tab", CancellationToken.None);
        Assert.Equal(
            [new NativeKeyStep(true, "Shift", null, NativeModifiers.Shift), new NativeKeyStep(true, "Tab", null, NativeModifiers.Shift),
             new NativeKeyStep(false, "Tab", null, NativeModifiers.Shift), new NativeKeyStep(false, "Shift", null, NativeModifiers.None)],
            input.Steps);
        input.Steps.Clear();
        await actions.PressAsync("Shift+a", CancellationToken.None);
        Assert.Equal("A", ((NativeKeyStep)input.Steps[1]).Key); // as the keyboard gives it
    }

    [Fact]
    public async Task Wheel_drag_and_long_text_go_in_natural_pieces()
    {
        var input = new RecordingInput();
        var actions = new NativeInputActions(input, static (_, _) => Task.CompletedTask);

        await actions.ScrollAsync(100, 100, 7, 1003, CancellationToken.None);
        var wheel = input.Steps.OfType<NativeWheelStep>().ToList();
        Assert.Equal((7, 1003), (wheel.Sum(w => w.DeltaX), wheel.Sum(w => w.DeltaY)));
        Assert.All(wheel, w => Assert.InRange(Math.Abs(w.DeltaY), 0, 120));

        input.Steps.Clear();
        await actions.DragAsync(10, 10, 110, 10, CancellationToken.None);
        var mouse = input.Steps.OfType<NativeMouseStep>().ToList();
        var down = mouse.FindIndex(m => m.Action == NativeMouseAction.Down);
        var up = mouse.FindIndex(m => m.Action == NativeMouseAction.Up);
        Assert.Equal(10, up - down - 1); // ten moves with the button held
        Assert.Equal((110, 10), (mouse[up].X, mouse[up].Y));

        input.Steps.Clear();
        await actions.TypeAsync(new string('x', 640), CancellationToken.None);
        var text = input.Steps.OfType<NativeTextStep>().ToList();
        Assert.InRange(text.Count, 60, 64);
        Assert.Equal(640, text.Sum(t => t.Text.Length));
    }

    [Fact]
    public async Task Without_hover_the_pointer_does_not_travel_and_hovering_says_why()
    {
        var input = new RecordingInput { CanHover = false };
        var actions = new NativeInputActions(input, static (_, _) => Task.CompletedTask);
        await actions.ClickAsync(50, 60, CancellationToken.None);
        Assert.All(input.Steps, s => Assert.NotEqual(NativeMouseAction.Move, ((NativeMouseStep)s).Action));
        await Assert.ThrowsAsync<NotSupportedException>(() => actions.HoverAsync(50, 60, CancellationToken.None));
    }
}
