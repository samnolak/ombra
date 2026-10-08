#if APP_INPUT
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmpGui.App.Platform.Mac;
using OmpGui.ClientCore.Browser;
using OmpGui.WebViewHarness.Native;

namespace OmpGui.WebViewHarness.Scenarios;

/// <summary>
/// The trusted-input self-test (BROWSER_PLAN B4) with the app's own code: <c>Platform/Mac/NativeInput.cs</c> driven by
/// ClientCore's <see cref="NativeInputActions"/> (the pacing the agent browser uses) and <c>IsolatedWorld.cs</c>. The web
/// view's window is never on screen; a text view beside the web view plays the composer, which must keep the keyboard.
/// </summary>
internal sealed class NativeInputScenario : IScenario
{
    public string Name => "native-input";

    public string Description => "app's NativeInput + IsolatedWorld: trusted clicks, activation, popup, text (Cyrillic/Japanese), keys, shortcuts, wheel, drag, hover; isolated world; composer focus; user wins";

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task RunAsync(HarnessContext ctx)
    {
        var view = ctx.CreateWebView(HookOptions.Reference);
        // Popups depend on activation only when scripts may not open windows by themselves (Safari's setting)
        ObjC.SendVoidBool(ObjC.Send(ObjC.Send(view.Handle, "configuration"), "preferences"), "setJavaScriptCanOpenWindowsAutomatically:", false);
        var composer = AddComposer(view);
        ctx.Check("the composer (a text view beside the web view) has the keyboard before any agent input",
            ObjC.Send(view.Window, "firstResponder") == composer);

        using var input = new NativeInput(view.Handle);
        using var isolated = new MacIsolatedScripts(view.Handle);
        var actions = new NativeInputActions(input);
        ctx.Data["canHover"] = input.CanHover;
        ctx.Check("native input and the isolated world are available on this WebKit", input.Unsupported is null && isolated.Unsupported is null,
            new JsonObject { ["input"] = input.Unsupported, ["isolated"] = isolated.Unsupported });

        view.WithoutUserGesture = true; // reading the page must not give it activation
        await view.LoadAsync(ctx.Server.Url("/native-input"));
        await IsolatedWorldChecks(ctx, view, isolated);

        // Element centres from the isolated world (the page's patched getBoundingClientRect cannot mislead it)
        var at = new Dictionary<string, (double X, double Y)>();
        foreach (var id in new[] { "b", "hover", "t", "ta", "ce", "scroller", "slider", "dbl" })
            at[id] = await Center(isolated, id);
        ctx.Data["centres"] = new JsonObject(at.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonArray(kv.Value.X, kv.Value.Y))));

        // ── click: trusted, activation, a popup from the click handler; on a fresh document ──
        await view.LoadAsync(ctx.Server.Url("/native-input"));
        ctx.Check("fresh document has no user activation before the click",
            (await view.EvalAsync("navigator.userActivation.hasBeenActive"))?.GetValue<bool>() == false);
        await actions.ClickAsync(at["b"].X, at["b"].Y, CancellationToken.None);
        await view.WaitForJsAsync("window.__log.some(e => e.type === 'click')", TimeSpan.FromSeconds(3));
        var log = await view.EvalAsync("window.__log");
        var click = Find(log, "click", "b");
        ctx.Data["clickEvents"] = log?.DeepClone();
        ctx.Check("click: pointerdown, mousedown, mouseup and click are trusted",
            new[] { "pointerdown", "mousedown", "mouseup", "click" }.All(t => Find(log, t, "b")?["isTrusted"]?.GetValue<bool>() == true), log);
        ctx.Check("click: user activation is active in the click handler", click?["userActivation"]?["isActive"]?.GetValue<bool>() == true, click);
        ctx.Check("click: window.open inside the click handler succeeds", click?["windowOpenReturned"]?.GetValue<bool>() == true, click);
        ctx.Check("click: it lands where the isolated world measured (±2 px)",
            click is not null && Math.Abs(click["clientX"]!.GetValue<double>() - at["b"].X) <= 2 && Math.Abs(click["clientY"]!.GetValue<double>() - at["b"].Y) <= 2, click);

        Responder(ctx, view, "after click");
        // ── text: insertText, any script, no key events ──
        await Clear(view);
        await actions.ClickAsync(at["t"].X, at["t"].Y, CancellationToken.None);
        ctx.Check("a click focuses the text field", await view.WaitForJsAsync("document.activeElement.id === 't'", TimeSpan.FromSeconds(2)));
        Responder(ctx, view, "after clicking the text field");
        await Clear(view);
        const string Text = "Привет, 日本語 ok";
        await actions.TypeAsync(Text, CancellationToken.None);
        await view.WaitForJsAsync($"t.value === {JsonSerializer.Serialize(Text, Json)}", TimeSpan.FromSeconds(3));
        var value = await view.EvalAsync("t.value");
        var typing = await view.EvalAsync("window.__log");
        ctx.Check("type: Cyrillic and Japanese text arrives as typed", value?.GetValue<string>() == Text, value);
        ctx.Check("type: beforeinput/input are trusted insertText events",
            typing is JsonArray ta && ta.Count > 0 && ta.Where(e => (string?)e?["type"] is "beforeinput" or "input")
                .All(e => e?["isTrusted"]?.GetValue<bool>() == true && (string?)e?["inputType"] == "insertText"), typing);
        ctx.Check("type: text goes past the keyboard layout (no key events for it)",
            typing is JsonArray tk && !tk.Any(e => (string?)e?["type"] == "keydown"), typing);

        Responder(ctx, view, "after typing");
        // ── select all (⌘A, a key equivalent) and replace ──
        await actions.ReplaceAsync("replaced", CancellationToken.None);
        await view.WaitForJsAsync("t.value === 'replaced'", TimeSpan.FromSeconds(2));
        value = await view.EvalAsync("t.value");
        ctx.Check("fill: ⌘A, Backspace, then the text replaces the field's content", value?.GetValue<string>() == "replaced", value);

        Responder(ctx, view, "after fill");
        // ── Enter and Backspace in a textarea ──
        await actions.ClickAsync(at["ta"].X, at["ta"].Y, CancellationToken.None);
        await Clear(view);
        await actions.TypeAsync("line1\nline2", CancellationToken.None);
        await actions.PressAsync("Backspace", CancellationToken.None);
        await view.WaitForJsAsync("ta.value === 'line1\\nline'", TimeSpan.FromSeconds(2));
        value = await view.EvalAsync("ta.value");
        log = await view.EvalAsync("window.__log.filter(e => e.type === 'keydown')");
        ctx.Check("keys: Enter makes a new line and Backspace deletes, with trusted keydowns",
            value?.GetValue<string>() == "line1\nline" && Find(log, "keydown", null) is { } kd && kd["isTrusted"]?.GetValue<bool>() == true
            && (log as JsonArray)!.Any(e => (string?)e?["key"] == "Enter") && (log as JsonArray)!.Any(e => (string?)e?["key"] == "Backspace"),
            new JsonObject { ["value"] = value?.DeepClone(), ["keydowns"] = log?.DeepClone() });

        Responder(ctx, view, "after keys");
        // ── contenteditable ──
        await actions.ClickAsync(at["ce"].X, at["ce"].Y, CancellationToken.None);
        await Clear(view);
        const string Rich = "Редактор ✓ 編集";
        await actions.TypeAsync(Rich, CancellationToken.None);
        await view.WaitForJsAsync($"ce.textContent === {JsonSerializer.Serialize(Rich, Json)}", TimeSpan.FromSeconds(3));
        value = await view.EvalAsync("ce.textContent");
        typing = await view.EvalAsync("window.__log.filter(e => e.type === 'input')");
        ctx.Check("contenteditable: the text arrives with trusted input events",
            value?.GetValue<string>() == Rich && typing is JsonArray ci && ci.Count > 0 && ci.All(e => e?["isTrusted"]?.GetValue<bool>() == true),
            new JsonObject { ["value"] = value?.DeepClone(), ["input"] = typing?.DeepClone() });

        Responder(ctx, view, "after contenteditable");
        // ── shortcuts: one the page handles, one it does not (that one must not reach the app) ──
        var suppressedBefore = NativeInput.SuppressedKeyEvents;
        await Clear(view);
        await actions.PressAsync("Meta+k", CancellationToken.None);
        await view.WaitForJsAsync("window.__paletteOpened === true", TimeSpan.FromSeconds(2));
        log = await view.EvalAsync("window.__log.filter(e => e.type === 'keydown' && e.key === 'k')");
        ctx.Check("shortcut the page handles (⌘K): trusted keydown with metaKey, the page's handler ran",
            (await view.EvalAsync("window.__paletteOpened === true"))?.GetValue<bool>() == true
            && Find(log, "keydown", null) is { } mk && mk["isTrusted"]?.GetValue<bool>() == true && mk["metaKey"]?.GetValue<bool>() == true, log);
        ctx.Check("a key the page handled is not re-sent to the app", NativeInput.SuppressedKeyEvents == suppressedBefore,
            new JsonObject { ["suppressed"] = NativeInput.SuppressedKeyEvents - suppressedBefore });
        await actions.PressAsync("Meta+j", CancellationToken.None);
        await HarnessWebView.WaitForAsync(() => NativeInput.SuppressedKeyEvents > suppressedBefore, TimeSpan.FromSeconds(2));
        ctx.Check("a shortcut the page leaves alone (⌘J), re-sent by WebKit to the app, never reaches the app's menus",
            NativeInput.SuppressedKeyEvents > suppressedBefore, new JsonObject { ["suppressed"] = NativeInput.SuppressedKeyEvents - suppressedBefore });

        Responder(ctx, view, "after shortcuts");
        // ── wheel ──
        await Clear(view);
        await actions.ScrollAsync(at["scroller"].X, at["scroller"].Y, 0, 300, CancellationToken.None);
        await view.WaitForJsAsync("scroller.scrollTop > 0", TimeSpan.FromSeconds(3));
        log = await view.EvalAsync("window.__log.filter(e => e.type === 'wheel')");
        var scrollTop = await view.EvalAsync("scroller.scrollTop");
        ctx.Check("wheel: trusted wheel events (deltaY > 0) on the element under the pointer, and it scrolls",
            log is JsonArray wl && wl.Count > 0 && wl.All(e => e?["isTrusted"]?.GetValue<bool>() == true && e?["deltaY"]?.GetValue<double>() > 0)
            && wl.All(e => (string?)e?["target"] is "scroller" or "DIV") && scrollTop?.GetValue<double>() > 0,
            new JsonObject { ["wheel"] = log?.DeepClone(), ["scrollTop"] = scrollTop?.DeepClone() });

        // ── drag: moves carry the held button (e.buttons === 1) ──
        var s = at["slider"];
        await actions.DragAsync(s.X - 120, s.Y, s.X + 120, s.Y, CancellationToken.None);
        var drag = await view.EvalAsync("window.__drag");
        ctx.Check("drag: trusted pointermoves report the held primary button (buttons === 1)",
            drag is JsonArray da && da.Any(e => e?["buttons"]?.GetValue<long>() == 1 && e?["trusted"]?.GetValue<bool>() == true), drag);

        // ── double click ──
        await Clear(view);
        await actions.ClickAsync(at["dbl"].X, at["dbl"].Y, CancellationToken.None, count: 2);
        await view.WaitForJsAsync("window.__log.some(e => e.type === 'dblclick')", TimeSpan.FromSeconds(2));
        var dbl = Find(await view.EvalAsync("window.__log"), "dblclick", "dbl");
        ctx.Check("double click: a trusted dblclick", dbl?["isTrusted"]?.GetValue<bool>() == true, dbl);

        // ── a key on a fresh document gives activation ──
        await view.LoadAsync(ctx.Server.Url("/native-input"));
        await view.EvalAsync("t.focus(), window.__log = [], true");
        await actions.PressAsync("x", CancellationToken.None);
        await view.WaitForJsAsync("window.__log.some(e => e.type === 'keydown')", TimeSpan.FromSeconds(2));
        var key = Find(await view.EvalAsync("window.__log"), "keydown", null);
        ctx.Check("key on a fresh document: trusted keydown with user activation",
            key?["isTrusted"]?.GetValue<bool>() == true && key?["userActivation"]?["isActive"]?.GetValue<bool>() == true, key);

        Responder(ctx, view, "after wheel, drag, double click, key");
        // ── the composer kept the keyboard through all of it ──
        ctx.Check("the composer still has the keyboard after clicks, typing and shortcuts, and got none of the text",
            ObjC.Send(view.Window, "firstResponder") == composer && ObjC.ToStr(ObjC.Send(composer, "string")) == "",
            new JsonObject { ["composerText"] = ObjC.ToStr(ObjC.Send(composer, "string")) });
        var probe = await view.EvalAsync("({ webdriver: navigator.webdriver, ompGlobals: Object.getOwnPropertyNames(window).filter(k => /^_*omp/i.test(k)) })");
        ctx.Check("navigator.webdriver stays false and the page sees no globals of ours",
            probe?["webdriver"]?.GetValue<bool>() == false && probe?["ompGlobals"] is JsonArray { Count: 0 }, probe);

        await Hover(ctx, view, input, at["hover"]);

        await UserWins(ctx, view, input, at["b"]);
        await HiddenPane(ctx);
    }

    // ── isolated world ──

    private static async Task IsolatedWorldChecks(HarnessContext ctx, HarnessWebView view, MacIsolatedScripts isolated)
    {
        var ct = CancellationToken.None;
        var sum = await isolated.CallAsync("async (a, b) => { await new Promise(r => setTimeout(r, 30)); return { sum: a + b.n, s: b.s }; }",
            """[2, {"n": 3, "s": "Привет"}]""", ct);
        ctx.Check("isolated world: arguments arrive as JSON and a Promise is awaited", sum == """{"sum":5,"s":"Привет"}""", sum);
        var undefinedValue = await isolated.CallAsync("() => undefined", "[]", ct);
        var nullValue = await isolated.CallAsync("() => null", "[]", ct);
        ctx.Check("isolated world: undefined comes back as nothing, null as null", undefinedValue is null && nullValue == "null",
            new JsonObject { ["undefined"] = undefinedValue, ["null"] = nullValue });
        string? thrown = null, rejected = null;
        try { await isolated.CallAsync("() => { throw new Error('boom'); }", "[]", ct); }
        catch (IsolatedScriptException e) { thrown = e.Message; }
        try { await isolated.CallAsync("async () => { await null; throw new TypeError('late'); }", "[]", ct); }
        catch (IsolatedScriptException e) { rejected = e.Message; }
        ctx.Check("isolated world: an exception and a rejection come back as the error's text",
            thrown?.StartsWith("Error: boom", StringComparison.Ordinal) == true && rejected?.StartsWith("TypeError: late", StringComparison.Ordinal) == true,
            new JsonObject { ["thrown"] = thrown, ["rejected"] = rejected });

        await isolated.CallAsync("() => { globalThis.__ompGuiProbe = 42; window.ompHelper = { secret: 1 }; return true; }", "[]", ct);
        var again = await isolated.CallAsync("() => globalThis.__ompGuiProbe", "[]", ct);
        var page = await view.EvalAsync("({ probe: typeof __ompGuiProbe, helper: 'ompHelper' in window, names: Object.getOwnPropertyNames(window).filter(k => /^_*omp/i.test(k)) })");
        ctx.Check("the page cannot see the isolated world's globals (they persist there)",
            again == "42" && (string?)page?["probe"] == "undefined" && page?["helper"]?.GetValue<bool>() == false && page?["names"] is JsonArray { Count: 0 }, page);

        var ours = await isolated.CallAsync("() => ({ secret: typeof __pageSecret, left: document.getElementById('b').getBoundingClientRect().left })", "[]", ct);
        var theirs = await view.EvalAsync("document.getElementById('b').getBoundingClientRect().left");
        ctx.Check("the isolated world sees neither the page's globals nor its patched getBoundingClientRect",
            ours == """{"secret":"undefined","left":40}""" && theirs?.GetValue<double>() == -999,
            new JsonObject { ["isolated"] = ours, ["page"] = theirs?.DeepClone() });

        var pageWorld = await IsolatedWorld.CallAsync(view.Handle, null, "() => window.__pageSecret", "[]", 0, ct, userGesture: false);
        ctx.Check("a call in the page world (world null) sees the page", pageWorld == "\"page-only\"", pageWorld);

        var messages = new List<(string Body, bool HasFrame)>();
        using (IsolatedWorld.AddMessageHandler(view.Handle, IsolatedWorld.AppWorldName, "ompProbe", (body, frame) => messages.Add((body, frame != 0))))
        {
            await isolated.CallAsync("() => { webkit.messageHandlers.ompProbe.postMessage({ a: 1, s: 'ж' }); webkit.messageHandlers.ompProbe.postMessage('raw'); return true; }", "[]", ct);
            await HarnessWebView.WaitForAsync(() => messages.Count >= 2, TimeSpan.FromSeconds(2));
            var reachable = await view.EvalAsync("!!(window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.ompProbe)");
            ctx.Check("message handler in the isolated world: our scripts post to it (JSON or a raw string, with the frame); the page cannot reach it",
                messages.Count == 2 && messages[0].HasFrame && JsonNode.DeepEquals(JsonNode.Parse(messages[0].Body), JsonNode.Parse("""{"a":1,"s":"ж"}"""))
                && messages[1] == ("raw", true) && reachable?.GetValue<bool>() == false,
                new JsonObject { ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode?)m.Body)]), ["pageReaches"] = reachable?.DeepClone() });
        }
        var activated = await view.EvalAsync("navigator.userActivation.hasBeenActive");
        if (ObjC.RespondsTo(view.Handle, "_callAsyncJavaScript:arguments:inFrame:inContentWorld:withUserGesture:completionHandler:")
            || ObjC.RespondsTo(view.Handle, "_evaluateJavaScript:asAsyncFunction:withSourceURL:withArguments:forceUserGesture:inFrame:inWorld:completionHandler:"))
            ctx.Check("the app's helper scripts give the page no user activation (only real input does)", activated?.GetValue<bool>() == false, activated);
        else ctx.Data["activationAfterIsolatedCalls"] = activated?.DeepClone();
    }

    // ── hover: WebKit gives the page pointer moves only while its window is the active one (as Safari does) ──

    private static async Task Hover(HarnessContext ctx, HarnessWebView view, NativeInput input, (double X, double Y) target)
    {
        if (!input.CanHover)
        {
            ctx.Data["hover"] = "skipped: this WebKit has no _simulateMouseMove:";
            return;
        }
        // In a window that is not the key window, moves only update scrollbars: nothing reaches the page
        await view.LoadAsync(ctx.Server.Url("/native-input"));
        await new NativeInputActions(input).HoverAsync(target.X, target.Y, CancellationToken.None);
        await Task.Delay(300);
        ctx.Data["hoverInInactiveWindow"] = (await view.EvalAsync("window.__log"))?.DeepClone();

        // The harness window can never be key (it is never on screen), so it plays the app's active window: a subclass
        // answering isKeyWindow = YES and the "became key" notification WebKit listens for. Nothing is shown.
        SimulateActiveWindow(view);
        await view.LoadAsync(ctx.Server.Url("/native-input"));
        await new NativeInputActions(input).HoverAsync(target.X, target.Y, CancellationToken.None);
        await view.WaitForJsAsync("window.__log.some(e => e.type === 'mousemove' && e.target === 'hover')", TimeSpan.FromSeconds(3));
        var log = await view.EvalAsync("window.__log.filter(e => e.target === 'hover')");
        ctx.Check("hover (app window active): trusted mouseover, mouseenter and mousemove on the element, no button",
            new[] { "mouseover", "mouseenter", "mousemove" }.All(t => Find(log, t, "hover") is { } e && e["isTrusted"]?.GetValue<bool>() == true && e["buttons"]?.GetValue<long>() == 0),
            log);
        SimulateActiveWindow(view, active: false);
    }

    private static nint _activeWindow;
    private static unsafe delegate* unmanaged<nint, nint, byte> _isKeyWindow;

    [UnmanagedCallersOnly]
    private static unsafe byte IsKeyWindow(nint self, nint cmd) => self == _activeWindow ? (byte)1 : _isKeyWindow(self, cmd);

    /// <summary>
    /// Makes the harness window answer isKeyWindow = YES (or stop, with <paramref name="active"/> false) and tells WebKit
    /// with the notification it listens for. The method is added to the window's runtime class, which is left as it is
    /// (KVO's class: replacing it would break the observers).
    /// </summary>
    private static unsafe void SimulateActiveWindow(HarnessWebView view, bool active = true)
    {
        var cls = ObjC.ClassOf(view.Window);
        var ours = (nint)(delegate* unmanaged<nint, nint, byte>)&IsKeyWindow;
        if (_isKeyWindow == null)
        {
            var method = class_getInstanceMethod(cls, ObjC.Sel("isKeyWindow"));
            _isKeyWindow = (delegate* unmanaged<nint, nint, byte>)method_getImplementation(method);
            if (class_addMethod(cls, ObjC.Sel("isKeyWindow"), ours, "B@:") == 0) method_setImplementation(method, ours);
        }
        _activeWindow = active ? view.Window : 0;
        var center = ObjC.Send(ObjC.Cls("NSNotificationCenter"), "defaultCenter");
        ObjC.SendVoid(center, "postNotificationName:object:", ObjC.Str(active ? "NSWindowDidBecomeKeyNotification" : "NSWindowDidResignKeyNotification"), view.Window);
    }

    [DllImport(ObjC.LibObjC)]
    private static extern nint class_getInstanceMethod(nint cls, nint sel);

    [DllImport(ObjC.LibObjC)]
    private static extern nint method_getImplementation(nint method);

    [DllImport(ObjC.LibObjC)]
    private static extern nint method_setImplementation(nint method, nint imp);

    [DllImport(ObjC.LibObjC)]
    private static extern byte class_addMethod(nint cls, nint sel, nint imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    // ── the user wins ──

    private static async Task UserWins(HarnessContext ctx, HarnessWebView view, NativeInput input, (double X, double Y) target)
    {
        var quiet = NativeInput.UserQuiet;
        NativeInput.UserQuiet = TimeSpan.FromMilliseconds(600);
        try
        {
            // The user's own click on the page, through the app's event dispatch as AppKit delivers it
            var p = WindowPoint(view, target.X, target.Y);
            foreach (var type in new ulong[] { 1, 2 })
                ObjC.SendVoid(ObjC.Send(ObjC.Cls("NSApplication"), "sharedApplication"), "sendEvent:", UserMouseEvent(view, type, p));
            var noted = input.LastUserInput != 0;
            var clock = Stopwatch.StartNew();
            await input.SendAsync([new NativeMouseStep(NativeMouseAction.Move, target.X, target.Y, NativeMouseButton.Left, 0, NativeModifiers.None)], CancellationToken.None);
            var waited = clock.ElapsedMilliseconds;
            ctx.Check("user input wins: after the user's own click on the page the agent's next step waits until they pause",
                noted && waited >= 450, new JsonObject { ["noted"] = noted, ["waitedMs"] = waited });
        }
        finally
        {
            NativeInput.UserQuiet = quiet;
        }
    }

    // ── the pane hidden: the web view is in no window ──

    private static async Task HiddenPane(HarnessContext ctx)
    {
        using var hidden = HarnessWebView.Create(HookOptions.Reference, inWindow: false);
        ObjC.SendVoidBool(ObjC.Send(ObjC.Send(hidden.Handle, "configuration"), "preferences"), "setJavaScriptCanOpenWindowsAutomatically:", false);
        using var input = new NativeInput(hidden.Handle);
        using var isolated = new MacIsolatedScripts(hidden.Handle);
        await hidden.LoadAsync(ctx.Server.Url("/native-input"));
        hidden.WithoutUserGesture = true;
        var b = await Center(isolated, "b");
        var t = await Center(isolated, "t");
        await hidden.LoadAsync(ctx.Server.Url("/native-input"));
        var actions = new NativeInputActions(input);
        await actions.ClickAsync(b.X, b.Y, CancellationToken.None);
        await hidden.WaitForJsAsync("window.__log.some(e => e.type === 'click')", TimeSpan.FromSeconds(3));
        var click = Find(await hidden.EvalAsync("window.__log"), "click", "b");
        await actions.ClickAsync(t.X, t.Y, CancellationToken.None);
        await actions.TypeAsync("скрыт", CancellationToken.None);
        await hidden.WaitForJsAsync("t.value === 'скрыт'", TimeSpan.FromSeconds(2));
        var value = await hidden.EvalAsync("t.value");
        ctx.Data["hiddenPane"] = new JsonObject { ["click"] = click?.DeepClone(), ["value"] = value?.DeepClone() };
        ctx.Check("pane hidden (web view in no window): a click is trusted, activates and opens a popup; typing arrives",
            click?["isTrusted"]?.GetValue<bool>() == true && click?["userActivation"]?["isActive"]?.GetValue<bool>() == true
            && click?["windowOpenReturned"]?.GetValue<bool>() == true && value?.GetValue<string>() == "скрыт",
            new JsonObject { ["click"] = click?.DeepClone(), ["value"] = value?.DeepClone() });
    }

    // ── helpers ──

    /// <summary>A text view beside the web view in its window, made first responder: the app's composer.</summary>
    internal static unsafe nint AddComposer(HarnessWebView view)
    {
        var frame = ObjC.SendRect(view.Handle, "frame");
        var container = ObjC.Send(ObjC.Send(ObjC.Cls("NSView"), "alloc"), "init");
        ObjC.Retain(view.Handle);
        ObjC.SendVoid(view.Window, "setContentView:", container);
        ObjC.SendVoid(container, "addSubview:", view.Handle);
        ObjC.Release(view.Handle);
        var composer = ((delegate* unmanaged<nint, nint, CGRect, nint>)ObjC.MsgSend)(
            ObjC.Send(ObjC.Cls("NSTextView"), "alloc"), ObjC.Sel("initWithFrame:"), new CGRect(0, frame.Size.Height + 10, 300, 40));
        ObjC.SendVoid(container, "addSubview:", composer);
        ObjC.Release(composer);
        ObjC.Release(container);
        ObjC.SendBool(view.Window, "makeFirstResponder:", composer);
        return composer;
    }

    private static async Task<(double X, double Y)> Center(MacIsolatedScripts isolated, string id)
    {
        var json = await isolated.CallAsync("(id) => { const r = document.getElementById(id).getBoundingClientRect(); return [r.left + r.width / 2, r.top + r.height / 2]; }",
            JsonSerializer.Serialize(new[] { id }), CancellationToken.None);
        var a = JsonNode.Parse(json!)!.AsArray();
        return (a[0]!.GetValue<double>(), a[1]!.GetValue<double>());
    }

    /// <summary>A mouse event as AppKit makes it for the user's click (no mark of ours).</summary>
    private static unsafe nint UserMouseEvent(HarnessWebView view, ulong type, CGPoint p) =>
        ((delegate* unmanaged<nint, nint, ulong, CGPoint, ulong, double, long, nint, long, long, float, nint>)ObjC.MsgSend)(
            ObjC.Cls("NSEvent"), ObjC.Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
            type, p, 0, 0, ObjC.SendLong(view.Window, "windowNumber"), 0, 1, 1, type == 1 ? 1 : 0);

    private static CGPoint WindowPoint(HarnessWebView view, double x, double y)
    {
        var bounds = ObjC.SendRect(view.Handle, "bounds");
        var local = ObjC.SendBool(view.Handle, "isFlipped") ? new CGPoint(x, y) : new CGPoint(x, bounds.Size.Height - y);
        return ObjC.SendPointToView(view.Handle, local, 0);
    }

    private static void Responder(HarnessContext ctx, HarnessWebView view, string when)
    {
        if (ctx.Data["firstResponder"] is not JsonObject o) ctx.Data["firstResponder"] = o = [];
        o[when] = ObjC.ClassName(ObjC.Send(view.Window, "firstResponder"));
    }

    private static Task Clear(HarnessWebView view) => view.EvalAsync("window.__log = [], true");

    internal static JsonNode? Find(JsonNode? log, string type, string? target) =>
        (log as JsonArray)?.FirstOrDefault(e => (string?)e?["type"] == type && (target is null || (string?)e?["target"] == target));
}
#endif
