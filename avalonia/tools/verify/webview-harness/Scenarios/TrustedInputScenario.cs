using System.Text.Json.Nodes;
using OmpGui.WebViewHarness.Native;

namespace OmpGui.WebViewHarness.Scenarios;

/// <summary>
/// The trusted-input self-test page. Compares what the page observes for script-dispatched events (today's agent
/// input) with in-process NSEvents sent to the WKWebView's own responder methods and <c>insertText:</c> (the B4
/// approach: no CGEventPost, no accessibility permission), with the web view's window never on screen.
/// </summary>
internal sealed class TrustedInputScenario : IScenario
{
    private const ulong NSEventTypeLeftMouseDown = 1, NSEventTypeLeftMouseUp = 2, NSEventTypeKeyDown = 10, NSEventTypeKeyUp = 11;
    private const ushort KeyCodeA = 0;

    public string Name => "trusted-input";

    public string Description => "self-test page: isTrusted / userActivation for synthetic JS events vs native NSEvent click, insertText, keyDown";

    public async Task RunAsync(HarnessContext ctx)
    {
        var view = ctx.CreateWebView(HookOptions.Reference);
        // WKWebView's macOS default lets any script open windows (javaScriptCanOpenWindowsAutomatically = YES), so a
        // popup proves nothing. Block them as Safari does; -configuration returns a copy, but it shares the live
        // WKPreferences object, so this applies to the running view.
        var prefs = ObjC.Send(ObjC.Send(view.Handle, "configuration"), "preferences");
        ObjC.SendVoidBool(prefs, "setJavaScriptCanOpenWindowsAutomatically:", false);

        // 1. today's agent input: a script-dispatched click, run with evaluateJavaScript (what NativeWebView.InvokeScript
        //    calls). WebKit runs evaluateJavaScript as a user gesture, so the page gets activation, but isTrusted is false.
        await view.LoadAsync(ctx.Server.Url("/input"));
        await view.EvalAsync("document.getElementById('b').dispatchEvent(new MouseEvent('click', { bubbles: true })), true");
        var synthetic = await view.EvalAsync("window.__log");
        var synthClick = Find(synthetic, "click", "b");
        ctx.Check("synthetic click (evaluateJavaScript): isTrusted false", synthClick?["isTrusted"]?.GetValue<bool>() == false, synthClick);
        ctx.Data["syntheticClickViaEvaluateJavaScript"] = synthClick?.DeepClone();
        ctx.Data["probe"] = (await view.EvalAsync("window.__probe"))?.DeepClone();

        // From here on, the harness reads the page without a user gesture so it cannot grant activation itself.
        view.WithoutUserGesture = true;

        // 2. the same synthetic click with no gesture around it: neither trusted nor activating
        await view.LoadAsync(ctx.Server.Url("/input"));
        await view.EvalAsync("document.getElementById('b').dispatchEvent(new MouseEvent('click', { bubbles: true })), true");
        var plain = Find(await view.EvalAsync("window.__log"), "click", "b");
        ctx.Check("synthetic click (no gesture): isTrusted false, no activation",
            plain?["isTrusted"]?.GetValue<bool>() == false && plain?["userActivation"]?["isActive"]?.GetValue<bool>() == false, plain);
        ctx.Check("popup blocking via configuration.preferences applies: window.open without activation returns null",
            plain?["windowOpenReturned"]?.GetValue<bool>() == false, plain);

        // 3. native click on a fresh document (no activation yet)
        await view.LoadAsync(ctx.Server.Url("/input"));
        var before = await view.EvalAsync("navigator.userActivation ? navigator.userActivation.hasBeenActive : null");
        ctx.Check("fresh document has no user activation before the native click", before?.GetValue<bool>() == false, before);
        await NativeClick(view, "b");
        await view.WaitForJsAsync("window.__log.some(e => e.type === 'click')", TimeSpan.FromSeconds(3));
        var native = await view.EvalAsync("window.__log");
        var down = Find(native, "mousedown", "b");
        var click = Find(native, "click", "b");
        ctx.Data["nativeClickEvents"] = native?.DeepClone();
        ctx.Check("native click: mousedown and click are trusted",
            down?["isTrusted"]?.GetValue<bool>() == true && click?["isTrusted"]?.GetValue<bool>() == true, native);
        ctx.Check("native click: user activation is active in the click handler",
            click?["userActivation"]?["isActive"]?.GetValue<bool>() == true, click);
        ctx.Check("native click: window.open inside the click handler succeeds",
            click?["windowOpenReturned"]?.GetValue<bool>() == true, click);

        // 4. text: click into the field, then insertText: (bypasses the keyboard layout / input method)
        await view.EvalAsync("window.__log = [], true");
        await NativeClick(view, "t");
        var focused = await view.WaitForJsAsync("document.activeElement && document.activeElement.id === 't'", TimeSpan.FromSeconds(2));
        ctx.Check("native click focuses the text field", focused);
        const string text = "Привет 日本";
        InsertText(view, text);
        await view.WaitForJsAsync($"document.getElementById('t').value === {System.Text.Json.JsonSerializer.Serialize(text)}", TimeSpan.FromSeconds(2));
        var value = await view.EvalAsync("document.getElementById('t').value");
        var typing = await view.EvalAsync("window.__log.filter(e => e.type === 'beforeinput' || e.type === 'input')");
        ctx.Check("insertText: Unicode text arrives", value?.GetValue<string>() == text, value);
        ctx.Check("insertText: beforeinput/input are trusted",
            typing is JsonArray arr && arr.Count > 0 && arr.All(e => e?["isTrusted"]?.GetValue<bool>() == true), typing);

        // 5. a key on a fresh document (field focused by script, no activation yet): keyDown/keyUp to the web view
        await view.LoadAsync(ctx.Server.Url("/input"));
        await view.EvalAsync("document.getElementById('t').focus(), window.__log = [], true");
        var keyBefore = await view.EvalAsync("navigator.userActivation.hasBeenActive");
        ctx.Check("fresh document has no user activation before the key", keyBefore?.GetValue<bool>() == false, keyBefore);
        Key(view, NSEventTypeKeyDown, "a", KeyCodeA);
        Key(view, NSEventTypeKeyUp, "a", KeyCodeA);
        await view.WaitForJsAsync("window.__log.some(e => e.type === 'keydown')", TimeSpan.FromSeconds(2));
        var keys = await view.EvalAsync("window.__log");
        var keydown = Find(keys, "keydown", null);
        ctx.Data["keyEvents"] = keys?.DeepClone();
        ctx.Check("native keyDown: trusted keydown with activation",
            keydown?["isTrusted"]?.GetValue<bool>() == true && keydown?["userActivation"]?["isActive"]?.GetValue<bool>() == true, keydown);
        var after = await view.EvalAsync("document.getElementById('t').value");
        ctx.Data["valueAfterKey"] = after?.DeepClone();
    }

    private static JsonNode? Find(JsonNode? log, string type, string? target) =>
        (log as JsonArray)?.FirstOrDefault(e => (string?)e?["type"] == type && (target is null || (string?)e?["target"] == target));

    /// <summary>Element centre (CSS px) → window coordinates of the web view's never-shown window.</summary>
    private static async Task<CGPoint> WindowPoint(HarnessWebView view, string id)
    {
        var r = await view.EvalAsync($"(() => {{ const r = document.getElementById('{id}').getBoundingClientRect(); return {{ x: r.left + r.width / 2, y: r.top + r.height / 2 }}; }})()");
        var x = r!["x"]!.GetValue<double>();
        var y = r["y"]!.GetValue<double>();
        var bounds = ObjC.SendRect(view.Handle, "bounds");
        var local = ObjC.SendBool(view.Handle, "isFlipped") ? new CGPoint(x, y) : new CGPoint(x, bounds.Size.Height - y);
        return ObjC.SendPointToView(view.Handle, local, 0);
    }

    private static double Uptime() => ObjC.SendDouble(ObjC.Send(ObjC.Cls("NSProcessInfo"), "processInfo"), "systemUptime");

    private static unsafe nint MouseEvent(HarnessWebView view, ulong type, CGPoint p, float pressure) =>
        ((delegate* unmanaged<nint, nint, ulong, CGPoint, ulong, double, long, nint, long, long, float, nint>)ObjC.MsgSend)(
            ObjC.Cls("NSEvent"), ObjC.Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
            type, p, 0, Uptime(), ObjC.SendLong(view.Window, "windowNumber"), 0, 0, 1, pressure);

    private static async Task NativeClick(HarnessWebView view, string id)
    {
        var p = await WindowPoint(view, id);
        ObjC.SendVoid(view.Handle, "mouseDown:", MouseEvent(view, NSEventTypeLeftMouseDown, p, 1));
        await Task.Delay(40);
        ObjC.SendVoid(view.Handle, "mouseUp:", MouseEvent(view, NSEventTypeLeftMouseUp, p, 0));
        await Task.Delay(100);
    }

    private static unsafe void InsertText(HarnessWebView view, string text)
    {
        ObjC.SendBool(view.Window, "makeFirstResponder:", view.Handle);
        ((delegate* unmanaged<nint, nint, nint, NSRange, void>)ObjC.MsgSend)(
            view.Handle, ObjC.Sel("insertText:replacementRange:"), ObjC.Str(text), new NSRange(unchecked((nuint)long.MaxValue), 0));
    }

    private static unsafe void Key(HarnessWebView view, ulong type, string chars, ushort keyCode)
    {
        ObjC.SendBool(view.Window, "makeFirstResponder:", view.Handle);
        var ev = ((delegate* unmanaged<nint, nint, ulong, CGPoint, ulong, double, long, nint, nint, nint, byte, ushort, nint>)ObjC.MsgSend)(
            ObjC.Cls("NSEvent"), ObjC.Sel("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
            type, new CGPoint(0, 0), 0, Uptime(), ObjC.SendLong(view.Window, "windowNumber"), 0, ObjC.Str(chars), ObjC.Str(chars), 0, keyCode);
        ObjC.SendVoid(view.Handle, type == NSEventTypeKeyDown ? "keyDown:" : "keyUp:", ev);
    }
}
