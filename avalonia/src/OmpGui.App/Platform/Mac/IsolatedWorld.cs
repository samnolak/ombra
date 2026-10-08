using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// Scripts in a <c>WKContentWorld</c> of their own (macOS 11+): they share the page's DOM but not its globals, so the
/// page cannot see, call or replace them, and their built-ins are WebKit's own, whatever the page patched. Avalonia's
/// <c>InvokeScript</c> runs in the page world only, so this calls <c>callAsyncJavaScript:arguments:inFrame:inContentWorld:</c>
/// and <c>-[WKUserContentController addScriptMessageHandler:contentWorld:name:]</c> on the raw <c>WKWebView</c>.
/// </summary>
/// <remarks>
/// Any thread: the WebKit calls are made on the main thread. A null world name means the page's own world.
/// No Avalonia types: the windowless harness compiles this folder.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static class IsolatedWorld
{
    /// <summary>The world the app's helper scripts (the agent browser's library) run in.</summary>
    public const string AppWorldName = "omp-gui";

    /// <summary>
    /// The function body <c>callAsyncJavaScript</c> runs: the caller's function with the arguments parsed from one
    /// string argument (no source splicing of values), its Promise awaited. The answer is tagged text: "v" + JSON of the
    /// value, "e" + the exception (<c>String(e)</c>, then the stack), or <c>undefined</c> for undefined / function / symbol.
    /// </summary>
    private const string BodyHead = "const __ompFn = (\n";

    private const string BodyTail = """

);
let __ompValue;
try {
  __ompValue = await __ompFn(...JSON.parse(__ompArgs));
} catch (e) {
  let text;
  try { text = String(e); } catch (_) { text = "Error"; }
  const stack = e !== null && typeof e === "object" && typeof e.stack === "string" ? e.stack : "";
  return "e" + text + (stack && stack !== text ? "\n" + stack : "");
}
if (__ompValue === undefined || typeof __ompValue === "function" || typeof __ompValue === "symbol") return undefined;
let __ompJson;
try { __ompJson = JSON.stringify(__ompValue); }
catch (e) { return "eTypeError: the result cannot be sent back as JSON: " + (e && e.message); }
return __ompJson === undefined ? undefined : "v" + __ompJson;
""";

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, nint> Worlds = new(StringComparer.Ordinal);

    /// <summary>The <c>WKContentWorld</c> named <paramref name="name"/> (null: the page world); 0 before macOS 11. Main thread.</summary>
    public static nint World(string? name)
    {
        var cls = MacObjC.Cls("WKContentWorld");
        if (cls == 0) return 0;
        if (name is null) return MacObjC.Send(cls, "pageWorld");
        lock (Gate)
        {
            if (Worlds.TryGetValue(name, out var world)) return world;
            using var s = new MacRuntime.NSStr(name);
            world = MacObjC.Retain(MacObjC.Send(cls, "worldWithName:", s.Handle)); // kept for the process
            Worlds[name] = world;
            return world;
        }
    }

    /// <summary>Why <paramref name="wkWebView"/> cannot run isolated scripts; null when it can.</summary>
    public static string? Unsupported(nint wkWebView)
    {
        if (wkWebView == 0) return "No web view.";
        if (MacObjC.Cls("WKContentWorld") == 0) return "This macOS has no WKContentWorld (macOS 11 or later is needed).";
        return MacObjC.RespondsTo(wkWebView, "callAsyncJavaScript:arguments:inFrame:inContentWorld:completionHandler:")
            ? null
            : "This WebKit cannot run scripts in a content world.";
    }

    /// <summary>
    /// Calls <paramref name="functionSource"/> with the arguments of the JSON array <paramref name="argsJson"/> in world
    /// <paramref name="worldName"/> (null: the page world) of <paramref name="frameInfo"/> (a <c>WKFrameInfo</c>; 0: the
    /// main frame), awaiting a returned Promise: JSON text of the value, or null for undefined / function / symbol.
    /// Script exceptions, rejections and engine errors (frame gone, web process ended) are
    /// <see cref="IsolatedScriptException"/>s; cancelling <paramref name="ct"/> stops the wait (the script may still run).
    /// </summary>
    /// <param name="userGesture">
    /// WebKit runs app scripts as a user gesture (the page gets user activation from them). False uses the SPI
    /// <c>_callAsyncJavaScript:…withUserGesture:</c> when this WebKit has it, so only real input activates the page.
    /// </param>
    public static async Task<string?> CallAsync(nint wkWebView, string? worldName, string functionSource, string argsJson, nint frameInfo, CancellationToken ct,
        bool userGesture = true)
    {
        if (Unsupported(wkWebView) is { } why) throw new NotSupportedException(why);
        ct.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = BodyHead + functionSource + BodyTail;
        await MacRuntime.OnMainAsync(() => Start(wkWebView, worldName, body, argsJson, frameInfo, userGesture, tcs)).ConfigureAwait(false);
        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        var tagged = await tcs.Task.ConfigureAwait(false);
        if (tagged is null) return null;
        if (tagged.Length > 0 && tagged[0] == 'v') return tagged[1..];
        if (tagged.Length > 0 && tagged[0] == 'e') throw new IsolatedScriptException(tagged[1..]);
        throw new IsolatedScriptException("The page answered something that is not a script result.");
    }

    private const string WithoutGesture = "_callAsyncJavaScript:arguments:inFrame:inContentWorld:withUserGesture:completionHandler:";

    /// <summary>What both <c>callAsyncJavaScript</c> forms call inside WebKit (older WebKits have only this one).</summary>
    private const string Evaluate = "_evaluateJavaScript:asAsyncFunction:withSourceURL:withArguments:forceUserGesture:inFrame:inWorld:completionHandler:";

    private static unsafe void Start(nint webView, string? worldName, string body, string argsJson, nint frameInfo, bool userGesture,
        TaskCompletionSource<string?> tcs)
    {
        var world = World(worldName);
        var block = MacRuntime.ResultBlock((result, error) =>
        {
            if (error != 0)
            {
                var (domain, code, message) = MacRuntime.ScriptError(error);
                tcs.TrySetException(new IsolatedScriptException(message ?? "The script failed.", domain, code));
            }
            else if (result == 0 || MacObjC.IsKindOf(result, "NSNull")) tcs.TrySetResult(null);
            else if (MacObjC.IsKindOf(result, "NSString")) tcs.TrySetResult(MacObjC.ToStr(result));
            else tcs.TrySetResult("e" + "The page answered something that is not a script result.");
        });
        var pool = MacObjC.PoolPush();
        try
        {
            using var source = new MacRuntime.NSStr(body);
            using var key = new MacRuntime.NSStr("__ompArgs");
            using var value = new MacRuntime.NSStr(argsJson);
            var args = MacObjC.Send(MacObjC.Cls("NSDictionary"), "dictionaryWithObject:forKey:", value.Handle, key.Handle); // autoreleased
            if (!userGesture && MacObjC.RespondsTo(webView, WithoutGesture))
                ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, byte, nint, void>)MacObjC.MsgSend)(
                    webView, MacObjC.Sel(WithoutGesture), source.Handle, args, frameInfo, world, 0, block);
            else if (!userGesture && MacObjC.RespondsTo(webView, Evaluate))
                ((delegate* unmanaged<nint, nint, nint, byte, nint, nint, byte, nint, nint, nint, void>)MacObjC.MsgSend)(
                    webView, MacObjC.Sel(Evaluate), source.Handle, 1, 0, args, 0, frameInfo, world, block);
            else
                ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, void>)MacObjC.MsgSend)(
                    webView, MacObjC.Sel("callAsyncJavaScript:arguments:inFrame:inContentWorld:completionHandler:"),
                    source.Handle, args, frameInfo, world, block);
        }
        finally
        {
            MacBlocks.Release(block);
            MacObjC.PoolPop(pool);
        }
    }

    // ── script message handlers in a world: only scripts of that world can post to them ──

    private static readonly Dictionary<nint, Action<string, nint>> Handlers = [];
    private static nint _handlerClass;

    /// <summary>
    /// Adds a script message handler <paramref name="handlerName"/> in world <paramref name="worldName"/> (null: the page
    /// world) of <paramref name="wkWebView"/>'s user content controller: scripts of that world post with
    /// <c>webkit.messageHandlers.&lt;name&gt;.postMessage(body)</c>. <paramref name="onMessage"/> gets the body (a string as
    /// it is, anything else as JSON) and the sender's <c>WKFrameInfo</c> (valid during the call only), on the main
    /// thread. Disposing removes the handler. Main thread.
    /// </summary>
    public static IDisposable AddMessageHandler(nint wkWebView, string? worldName, string handlerName, Action<string, nint> onMessage)
    {
        if (wkWebView == 0) throw new ArgumentException("No web view.", nameof(wkWebView));
        var world = World(worldName);
        if (world == 0) throw new NotSupportedException("This macOS has no WKContentWorld (macOS 11 or later is needed).");
        // -configuration returns a copy, but it shares the live user content controller
        var controller = MacObjC.Retain(MacObjC.Send(MacObjC.Send(wkWebView, "configuration"), "userContentController"));
        if (controller == 0) throw new InvalidOperationException("The web view has no user content controller.");
        var handler = MacObjC.Send(MacObjC.Send(HandlerClass(), "alloc"), "init");
        lock (Gate) Handlers[handler] = onMessage;
        using var name = new MacRuntime.NSStr(handlerName);
        MacObjC.SendVoid(controller, "addScriptMessageHandler:contentWorld:name:", handler, world, name.Handle);
        MacObjC.Release(handler); // the controller keeps it
        return new Registration(controller, handler, world, handlerName);
    }

    private sealed class Registration(nint controller, nint handler, nint world, string name) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Gate) Handlers.Remove(handler);
            MacObjC.OnMainThread(() =>
            {
                using var s = new MacRuntime.NSStr(name);
                MacObjC.SendVoid(controller, "removeScriptMessageHandlerForName:contentWorld:", s.Handle, world);
                MacObjC.Release(controller);
            });
        }
    }

    private static unsafe nint HandlerClass()
    {
        lock (Gate)
        {
            if (_handlerClass != 0) return _handlerClass;
            _handlerClass = MacObjC.DefineClass("OmpGuiScriptMessageHandler", "WKScriptMessageHandler",
                ("userContentController:didReceiveScriptMessage:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&OnMessage, "v@:@@"));
            return _handlerClass;
        }
    }

    private const ulong JsonWritingFragmentsAllowed = 4;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnMessage(nint self, nint cmd, nint controller, nint message)
    {
        try
        {
            Action<string, nint>? onMessage;
            lock (Gate) Handlers.TryGetValue(self, out onMessage);
            if (onMessage is null || message == 0) return;
            var body = MacObjC.Send(message, "body");
            string text;
            if (body == 0 || MacObjC.IsKindOf(body, "NSNull")) text = "null";
            else if (MacObjC.IsKindOf(body, "NSString")) text = MacObjC.ToStr(body) ?? "";
            else
            {
                var data = ((delegate* unmanaged<nint, nint, nint, ulong, nint, nint>)MacObjC.MsgSend)(
                    MacObjC.Cls("NSJSONSerialization"), MacObjC.Sel("dataWithJSONObject:options:error:"), body, JsonWritingFragmentsAllowed, 0);
                text = data == 0
                    ? MacObjC.ToStr(MacObjC.Send(body, "description")) ?? ""
                    : Marshal.PtrToStringUTF8(MacObjC.Send(data, "bytes"), (int)MacObjC.SendLong(data, "length"));
            }
            onMessage(text, MacObjC.Send(message, "frameInfo"));
        }
        catch
        {
            // Nothing may unwind into WebKit
        }
    }
}

/// <summary>
/// The app's helper scripts in their own world of one <c>WKWebView</c> (<see cref="IsolatedWorld.AppWorldName"/>), run
/// without a user gesture where WebKit allows it: looking at the page never gives it user activation, only real input does.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacIsolatedScripts : IIsolatedScripts, IDisposable
{
    private nint _webView;

    /// <param name="wkWebView">The <c>WKWebView*</c>; retained until <see cref="Dispose"/>.</param>
    public MacIsolatedScripts(nint wkWebView)
    {
        Unsupported = IsolatedWorld.Unsupported(wkWebView);
        _webView = MacObjC.Retain(wkWebView);
    }

    public string? Unsupported { get; }

    public Task<string?> CallAsync(string functionSource, string argsJson, CancellationToken ct)
    {
        var view = _webView;
        if (view == 0) return Task.FromException<string?>(new NotSupportedException("The page is gone."));
        if (Unsupported is { } why) return Task.FromException<string?>(new NotSupportedException(why));
        return IsolatedWorld.CallAsync(view, IsolatedWorld.AppWorldName, functionSource, argsJson, 0, ct, userGesture: false);
    }

    public void Dispose()
    {
        var view = Interlocked.Exchange(ref _webView, 0);
        if (view != 0) MacObjC.OnMainThread(() => MacObjC.Release(view));
    }
}
