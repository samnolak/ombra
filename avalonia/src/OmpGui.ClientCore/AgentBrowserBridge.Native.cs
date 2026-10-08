using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmpGui.ClientCore.Browser;

namespace OmpGui.ClientCore;

/// <summary>
/// The element actions with input the engine treats like the user's (BROWSER_PLAN B4): the page library, in the app's
/// isolated world when the page has one, finds where to act (visible, enabled, not covered, not moving) and reports
/// whether the event got there; <see cref="NativeInputActions"/> sends it through the page's <see cref="INativeInput"/>;
/// then the bridge waits for the page to settle (no DOM change and no finished load for a moment), not a fixed time.
/// Where there is no native input, or for controls whose native handling would open the engine's own menus over the
/// app (<c>&lt;select&gt;</c>, date and colour fields), the library dispatches script events instead and the answer says
/// <c>"input": "synthetic"</c> with the reason, so omp knows activation-gated things (pop-ups, file pickers) may not follow.
/// </summary>
public sealed partial class AgentBrowserBridge
{
    /// <summary>How long the page must be still (DOM and network) after an action before the answer.</summary>
    private static readonly TimeSpan SettleQuiet = TimeSpan.FromMilliseconds(300);

    /// <summary>The most an action waits for the page to settle.</summary>
    internal TimeSpan SettleLimit { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long the bridge looks for a target that is visible, uncovered and not moving.</summary>
    internal TimeSpan TargetLimit { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The least time between two of omp's actions on a page.</summary>
    private static readonly TimeSpan BetweenActions = TimeSpan.FromMilliseconds(150);

    /// <summary>The pauses of native input and settling (tests run without them).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Pause { get; set; } = Task.Delay;

    private readonly ConditionalWeakTable<INativeInput, NativeInputActions> _actuators = new();
    private readonly ConditionalWeakTable<IAgentBrowserPage, StrongBox<long>> _lastAction = new();

    private NativeInputActions Actuator(INativeInput input) => _actuators.GetValue(input, i => new NativeInputActions(i, Pause));

    // ── the page library: isolated world first ──

    /// <summary>The library's <paramref name="name"/>(<paramref name="args"/>): in the app's isolated world when the page has one, else in the page.</summary>
    internal async Task<JsonNode?> LibraryAsync(IAgentBrowserPage page, string name, CancellationToken ct, params JsonNode?[] args)
    {
        if (page.Isolated is not { Unsupported: null } world)
            return await EvaluateAsync(page, _scripts.Call(name, args), ct).ConfigureAwait(false);
        var call = new JsonArray(JsonValue.Create(name), new JsonArray([.. args.Select(a => a?.DeepClone())])).ToJsonString(WriteOptions);
        string? text;
        try
        {
            text = await UnlessDialogAsync(page, c => world.CallAsync(_scripts.IsolatedCall, call, c), ct).ConfigureAwait(false);
        }
        catch (IsolatedScriptException e) when (e.Domain is not null)
        {
            return null; // the engine did not run it: the page went away (a click that navigates) or its process ended
        }
        catch (IsolatedScriptException e)
        {
            throw LibraryError(e.Message);
        }
        if (text is null) return null;
        try
        {
            return JsonNode.Parse(text, NodeOptions, DocumentOptions);
        }
        catch (JsonException)
        {
            throw new AgentBrowserException("js_error", "The page answered: " + Clip(text));
        }
    }

    /// <summary>A library exception from the isolated world ("Error: not_found: …" and its stack) as the bridge reports it.</summary>
    internal static AgentBrowserException LibraryError(string text)
    {
        var line = text.Split('\n', 2)[0];
        var message = line.StartsWith("Error: ", StringComparison.Ordinal) ? line["Error: ".Length..] : line;
        return CodedError().Match(message) is { Success: true } m
            ? new AgentBrowserException(m.Groups["code"].Value, m.Groups["message"].Value)
            : new AgentBrowserException("js_error", text);
    }

    // ── element actions ──

    /// <summary>A failure that is not omp's: the action goes the script-event way, with this reason.</summary>
    private sealed class ScriptEventsInstead(string reason) : Exception(reason);

    private static JsonObject Native() => new() { ["input"] = "native" };

    private static JsonObject Synthetic(string reason) => new() { ["input"] = "synthetic", ["reason"] = reason };

    /// <summary>Why this page's actions are script events (null: native input works).</summary>
    private static string? NoNativeInput(IAgentBrowserPage page) => page.Input switch
    {
        null => "This preview has no native input.",
        { Unsupported: { } why } => why,
        _ => null,
    };

    /// <summary>click, dblclick, hover, check, uncheck, type, fill on <paramref name="selector"/>.</summary>
    private async Task<JsonObject> ActAsync(IAgentBrowserPage page, string action, string selector, JsonObject args, CancellationToken ct)
    {
        var why = NoNativeInput(page);
        if (why is null)
        {
            try
            {
                return await NativeActAsync(page, page.Input!, action, selector, args, ct).ConfigureAwait(false);
            }
            catch (ScriptEventsInstead e)
            {
                why = e.Message;
            }
            catch (NotSupportedException e)
            {
                why = e.Message; // nothing of that step reached the page
            }
        }
        await LibraryAsync(page, "act", ct, action, selector, args).ConfigureAwait(false);
        return Synthetic(why ?? "");
    }

    private async Task<JsonObject> NativeActAsync(IAgentBrowserPage page, INativeInput input, string action, string selector, JsonObject args,
        CancellationToken ct)
    {
        var actuator = Actuator(input);
        using var acting = page.BeginAction();
        await PaceAsync(page, ct).ConfigureAwait(false);
        try
        {
            var result = Native();
            switch (action)
            {
                case "hover":
                {
                    if (!input.CanHover) throw new ScriptEventsInstead("This web view cannot move the pointer over the page.");
                    var target = await TargetAsync(page, selector, action, enabled: false, ct).ConfigureAwait(false);
                    var watched = await ArmAsync(page, selector, "mouseover", ct).ConfigureAwait(false);
                    await actuator.HoverAsync(target.X, target.Y, ct).ConfigureAwait(false);
                    if (watched && await ArmedAsync(page, ct).ConfigureAwait(false) is { Reached: false })
                        throw new ScriptEventsInstead("The pointer reaches the page only while the OMP GUI window is the active one.");
                    break;
                }
                case "click" or "dblclick":
                {
                    var target = await TargetAsync(page, selector, action, enabled: true, ct).ConfigureAwait(false);
                    if (target.Menu)
                        throw new ScriptEventsInstead($"A <{target.Tag}> opens the web view's own menu over the app, so it is set by script events.");
                    var watched = await ArmAsync(page, selector, "click", ct).ConfigureAwait(false);
                    await actuator.ClickAsync(target.X, target.Y, ct, action == "dblclick" ? 2 : 1).ConfigureAwait(false);
                    await SettleAsync(page, ct).ConfigureAwait(false);
                    if (watched && await ArmedAsync(page, ct).ConfigureAwait(false) is { Reached: false })
                        result["note"] = "The click landed on something else than the element (it moved or was covered meanwhile): observe the page again.";
                    break;
                }
                case "check" or "uncheck":
                {
                    var target = await TargetAsync(page, selector, action, enabled: true, ct).ConfigureAwait(false);
                    if (!target.Checkable)
                        throw new AgentBrowserException("invalid_target", JsonSerializer.Serialize(selector) + " is not a checkbox, radio button or switch");
                    var want = action == "check";
                    if (target.Checked != want)
                    {
                        await actuator.ClickAsync(target.X, target.Y, ct).ConfigureAwait(false);
                        await SettleAsync(page, ct).ConfigureAwait(false);
                        var after = await LibraryAsync(page, "target", ct, selector, "focus").ConfigureAwait(false);
                        result["checked"] = after?["checked"]?.GetValueKind() == JsonValueKind.True;
                    }
                    else result["checked"] = want;
                    break;
                }
                case "type" or "fill":
                {
                    var target = await TargetAsync(page, selector, action, enabled: true, ct).ConfigureAwait(false);
                    if (action == "fill" && target.Tag == "select")
                        throw new ScriptEventsInstead("A <select> opens the web view's own menu over the app, so it is set by script events.");
                    if (!target.Editable)
                        throw new AgentBrowserException("invalid_target",
                            JsonSerializer.Serialize(selector) + (action == "fill" ? " is not a text field or a select" : " is not a text field"));
                    // A person clicks into the field first; text then arrives where the caret is
                    if (!target.Focused) await actuator.ClickAsync(target.X, target.Y, ct).ConfigureAwait(false);
                    var text = args["text"]?.GetValue<string>() ?? "";
                    if (action == "fill") await actuator.ReplaceAsync(text, ct).ConfigureAwait(false);
                    else await actuator.TypeAsync(text, ct).ConfigureAwait(false);
                    await SettleAsync(page, ct).ConfigureAwait(false);
                    break;
                }
                default:
                    throw new AgentBrowserException("invalid_params", "unknown action " + JsonSerializer.Serialize(action));
            }
            return result;
        }
        catch (TimeoutException e)
        {
            throw new AgentBrowserException("busy", e.Message);
        }
        finally
        {
            Done(page);
        }
    }

    /// <summary>browser.press: a key or a combination, to whatever the page has focused.</summary>
    private async Task<JsonObject> PressAsync(IAgentBrowserPage page, string key, CancellationToken ct)
    {
        var why = NoNativeInput(page);
        if (why is null)
        {
            using var acting = page.BeginAction();
            await PaceAsync(page, ct).ConfigureAwait(false);
            try
            {
                await Actuator(page.Input!).PressAsync(key, ct).ConfigureAwait(false);
                await SettleAsync(page, ct).ConfigureAwait(false);
                return Native();
            }
            catch (ArgumentException e)
            {
                throw new AgentBrowserException("invalid_params", e.Message);
            }
            catch (TimeoutException e)
            {
                throw new AgentBrowserException("busy", e.Message);
            }
            catch (NotSupportedException e)
            {
                why = e.Message;
            }
            finally
            {
                Done(page);
            }
        }
        await LibraryAsync(page, "press", ct, key).ConfigureAwait(false);
        return Synthetic(why ?? "");
    }

    /// <summary>browser.scroll: the wheel over the element (or the middle of the page).</summary>
    private async Task<JsonObject> ScrollAsync(IAgentBrowserPage page, string? selector, double dx, double dy, CancellationToken ct)
    {
        var why = NoNativeInput(page);
        if (why is null)
        {
            using var acting = page.BeginAction();
            await PaceAsync(page, ct).ConfigureAwait(false);
            try
            {
                double x, y;
                if (selector is not null)
                {
                    var target = await TargetAsync(page, selector, "scroll", enabled: false, ct).ConfigureAwait(false);
                    (x, y) = (target.X, target.Y);
                }
                else
                {
                    var middle = await LibraryAsync(page, "viewport", ct).ConfigureAwait(false);
                    (x, y) = (middle?["x"]?.GetValue<double>() ?? 0, middle?["y"]?.GetValue<double>() ?? 0);
                }
                await Actuator(page.Input!).ScrollAsync(x, y, dx, dy, ct).ConfigureAwait(false);
                await SettleAsync(page, ct).ConfigureAwait(false);
                return Native();
            }
            catch (TimeoutException e)
            {
                throw new AgentBrowserException("busy", e.Message);
            }
            catch (NotSupportedException e)
            {
                why = e.Message;
            }
            finally
            {
                Done(page);
            }
        }
        var script = selector is not null
            ? LibraryAsync(page, "act", ct, "scroll", selector, new JsonObject { ["dx"] = dx, ["dy"] = dy })
            : LibraryAsync(page, "scroll", ct, dx, dy);
        await script.ConfigureAwait(false);
        return Synthetic(why ?? "");
    }

    // ── where to act ──

    /// <summary>What the library says about an action's target (<c>A.target</c>).</summary>
    private sealed record Target(double X, double Y, string Tag, bool Visible, bool Disabled, bool Editable, bool Focused, bool Checkable,
        bool Checked, bool Menu, bool InView, string? Covered)
    {
        public static Target From(JsonObject o) => new(
            o["x"]?.GetValue<double>() ?? 0, o["y"]?.GetValue<double>() ?? 0, o["tag"]?.GetValue<string>() ?? "",
            Flag(o, "visible"), Flag(o, "disabled"), Flag(o, "editable"), Flag(o, "focused"), Flag(o, "checkable"), Flag(o, "checked"),
            Flag(o, "menu"), Flag(o, "inView"), o["covered"]?.GetValueKind() == JsonValueKind.String ? o["covered"]!.GetValue<string>() : null);

        private static bool Flag(JsonObject o, string name) => o[name]?.GetValueKind() == JsonValueKind.True;
    }

    /// <summary>
    /// The point to act on: the element scrolled into view, visible, uncovered (and enabled when
    /// <paramref name="enabled"/>), at the same place twice in a row (not animating). Waits up to
    /// <see cref="TargetLimit"/>, then says what is in the way.
    /// </summary>
    private async Task<Target> TargetAsync(IAgentBrowserPage page, string selector, string action, bool enabled, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        Target? previous = null;
        while (true)
        {
            var node = await LibraryAsync(page, "target", ct, selector, action).ConfigureAwait(false) as JsonObject
                ?? throw new AgentBrowserException("not_found", "The page changed while looking for " + JsonSerializer.Serialize(selector) + ": observe it again.");
            var t = Target.From(node);
            var ready = t.Visible && t.InView && t.Covered is null && !(enabled && t.Disabled);
            if (ready && previous is { } p && Math.Abs(p.X - t.X) <= 1 && Math.Abs(p.Y - t.Y) <= 1) return t;
            if (clock.Elapsed > TargetLimit)
            {
                var name = JsonSerializer.Serialize(selector);
                if (ready) return t; // still moving: act where it is now
                throw new AgentBrowserException("not_actionable",
                    !t.Visible ? $"{name} is not visible: wait for it, or observe the page again."
                    : enabled && t.Disabled ? $"{name} is disabled: complete what the page asks for first."
                    : t.Covered is { } cover ? $"{name} is covered by {cover}: close or scroll away what covers it, then try again."
                    : $"{name} cannot be scrolled into view.");
            }
            previous = ready ? t : null;
            await Pause(TimeSpan.FromMilliseconds(ready ? 50 : 120), ct).ConfigureAwait(false);
        }
    }

    private async Task<bool> ArmAsync(IAgentBrowserPage page, string selector, string type, CancellationToken ct) =>
        (await LibraryAsync(page, "arm", ct, selector, type).ConfigureAwait(false))?.GetValueKind() == JsonValueKind.True;

    private sealed record Armed(bool Reached, bool Trusted);

    private async Task<Armed?> ArmedAsync(IAgentBrowserPage page, CancellationToken ct)
    {
        try
        {
            return await LibraryAsync(page, "armed", ct).ConfigureAwait(false) is JsonObject o
                ? new Armed(o["reached"]?.GetValueKind() == JsonValueKind.True, o["trusted"]?.GetValueKind() == JsonValueKind.True)
                : null;
        }
        catch (AgentBrowserException e) when (e.Code is "js_error" or "no_page")
        {
            return null; // a new document: the click navigated
        }
    }

    // ── pacing and settling ──

    /// <summary>At least <see cref="BetweenActions"/> after omp's previous action on the page.</summary>
    private async Task PaceAsync(IAgentBrowserPage page, CancellationToken ct)
    {
        var last = _lastAction.GetValue(page, static _ => new StrongBox<long>(0));
        var since = Stopwatch.GetElapsedTime(Interlocked.Read(ref last.Value));
        if (last.Value != 0 && since < BetweenActions) await Pause(BetweenActions - since, ct).ConfigureAwait(false);
    }

    private void Done(IAgentBrowserPage page) =>
        Interlocked.Exchange(ref _lastAction.GetValue(page, static _ => new StrongBox<long>(0)).Value, Stopwatch.GetTimestamp());

    /// <summary>
    /// Waits until the page has been still for <see cref="SettleQuiet"/> (no DOM change, no finished load) or
    /// <see cref="SettleLimit"/> passed. A page that is between documents (the action navigated) waits too. A dialog
    /// the action opened ends the wait with <c>dialog_open</c>.
    /// </summary>
    private async Task SettleAsync(IAgentBrowserPage page, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < SettleLimit)
        {
            await Pause(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            JsonNode? quiet;
            try
            {
                quiet = await LibraryAsync(page, "quiet", ct).ConfigureAwait(false);
            }
            catch (AgentBrowserException e) when (e.Code is "js_error" or "no_page")
            {
                quiet = null;
            }
            if (quiet is JsonObject q && q["ready"]?.GetValue<string>() != "loading"
                && (q["mutation"]?.GetValue<double>() ?? 0) >= SettleQuiet.TotalMilliseconds
                && (q["network"]?.GetValue<double>() ?? 0) >= SettleQuiet.TotalMilliseconds)
                return;
        }
    }
}
