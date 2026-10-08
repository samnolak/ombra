using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace OmpGui.WebViewHarness.Native;

/// <summary>Which delegates the harness installs on a web view (beyond the Avalonia-shaped navigation delegate).</summary>
internal sealed record HookOptions
{
    /// <summary>Our reference <c>WKUIDelegate</c> (dialogs, popups, file input, close). Avalonia sets none.</summary>
    public bool UiDelegate { get; init; }

    /// <summary>
    /// Reference navigation selectors added to the navigation delegate's class with <c>class_addMethod</c> (failures,
    /// crash, downloads, preferences-variant policy) — the technique the app uses on Avalonia's delegate.
    /// </summary>
    public bool NavigationExtras { get; init; }

    /// <summary>Reference <c>didReceiveAuthenticationChallenge</c> (only when the app's hooks are not used).</summary>
    public bool HarnessAuth { get; init; }

    /// <summary>
    /// The app's own hooks (<c>Platform/Mac/WebKitHooks</c>) instead of the reference delegates, answered through the
    /// app's <c>DialogBroker</c> by the same scenario handlers (<c>AppHooksBridge</c>).
    /// </summary>
    public bool AppHooks { get; init; }

    public static HookOptions None { get; } = new();
    public static HookOptions Reference { get; } = new() { UiDelegate = true, NavigationExtras = true };
    public static HookOptions App { get; } = new() { AppHooks = true };
}

internal sealed record JsDialog(string Kind, string? Message, string? DefaultText, string? FrameHost, bool IsMainFrame);

internal sealed record OpenPanelRequest(bool AllowsMultiple, bool AllowsDirectories);

internal sealed record AuthChallenge(string? Method, string? Host, long Port, string? Realm, bool IsProxy, long PreviousFailureCount);

internal sealed record DownloadRecord(string Source)
{
    public string? SuggestedFilename { get; set; }
    public string? Destination { get; set; }
    public bool Finished { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// A WKWebView that is never on screen: optionally the content view of a borderless <c>NSWindow</c> that is never
/// ordered in (no window list entry on screen), non-persistent data store, <c>inactiveSchedulingPolicy = None</c>.
/// Its navigation delegate is an instance of <c>OmpHarnessNavDelegate</c>, which has exactly the two selectors
/// Avalonia 12.1's <c>ManagedWKNavigationDelegate</c> has, so app hooks that augment Avalonia's delegate can be
/// exercised on it unchanged.
/// </summary>
internal sealed class HarnessWebView : IDisposable
{
    private const ulong NSWindowStyleMaskBorderless = 0;
    private const ulong NSBackingStoreBuffered = 2;
    private const long WKInactiveSchedulingPolicyNone = 2;

    private static readonly Dictionary<nint, HarnessWebView> Registry = [];
    private static int _counter;

    private nint _navDelegate;
    private nint _uiDelegate;
    private TaskCompletionSource<JsonObject>? _navigation;
    /// <summary>A pop-up the app's hooks created: the app owns the view, this closes it.</summary>
    private Action? _adoptedClose;
#if APP_HOOKS
    private AppHooksBridge? _app;

    /// <summary>The app's hooks on this view (app mode), null otherwise.</summary>
    internal AppHooksBridge? App => _app;

    /// <summary>Connects an adopted pop-up to the hooks the app attached to it.</summary>
    internal void UseAppBridge(AppHooksBridge bridge) => _app = bridge;
#endif

    private HarnessWebView(string label, nint handle, nint window, HookOptions hooks, HarnessWebView? opener)
    {
        Label = label;
        Handle = handle;
        Window = window;
        Hooks = hooks;
        Opener = opener;
        Registry[handle] = this;
    }

    public string Label { get; }
    public nint Handle { get; private set; }
    public nint Window { get; private set; }
    public HookOptions Hooks { get; }
    public HarnessWebView? Opener { get; }
    public List<HarnessWebView> Popups { get; } = [];
    public List<DownloadRecord> Downloads { get; } = [];
    public List<PendingBlock> PendingHandlers { get; } = [];

    /// <summary>Answers a JS dialog: alert → any, confirm → bool, prompt → string or null. Null handler = defaults.</summary>
    public Func<JsDialog, Task<object?>>? OnJsDialog { get; set; }

    public Func<OpenPanelRequest, Task<IReadOnlyList<string>?>>? OnOpenPanel { get; set; }

    /// <summary>Destination for a download (null = cancel). Default: none (cancel).</summary>
    public Func<DownloadRecord, string?>? DownloadDestination { get; set; }

    /// <summary>App mode: the folder the user chose for downloads ("Save" on the card saves there).</summary>
    public string? DownloadFolder { get; set; }

    public Func<AuthChallenge, Task<(string User, string Password)?>>? OnAuth { get; set; }

    public bool AllowPopups { get; set; } = true;

    public int ContentProcessTerminations { get; set; }

    /// <summary>App mode: answers what the app's broker still asks with its default (replaces the reference bookkeeping).</summary>
    internal Func<int>? CancelPendingOverride { get; set; }

    public static HarnessWebView? Find(nint webView) => Registry.GetValueOrDefault(webView);

    /// <summary>A new top-level web view with a fresh configuration.</summary>
    public static HarnessWebView Create(HookOptions hooks, double width = 1024, double height = 768, bool inWindow = true)
    {
        var config = ObjC.New("WKWebViewConfiguration");
        try
        {
            ObjC.SendVoid(config, "setWebsiteDataStore:", ObjC.Send(ObjC.Cls("WKWebsiteDataStore"), "nonPersistentDataStore"));
            var prefs = ObjC.Send(config, "preferences");
            if (ObjC.RespondsTo(prefs, "setInactiveSchedulingPolicy:"))
                ObjC.SendVoidLong(prefs, "setInactiveSchedulingPolicy:", WKInactiveSchedulingPolicyNone);
            return CreateWith(config, hooks, null, width, height, inWindow);
        }
        finally
        {
            ObjC.Release(config);
        }
    }

    /// <summary>A popup built from exactly the configuration WebKit passed to <c>createWebViewWithConfiguration</c>.</summary>
    internal static HarnessWebView CreatePopup(nint configuration, HarnessWebView opener) =>
        CreateWith(configuration, opener.Hooks, opener, 800, 600, inWindow: true);

    /// <summary>A pop-up the app's hooks built (<c>PopupWebViewHost</c>): registered for the scenario, closed through <paramref name="close"/>.</summary>
    internal static HarnessWebView Adopt(nint handle, HarnessWebView opener, Action close)
    {
        var view = new HarnessWebView($"popup{Interlocked.Increment(ref _counter)}", handle, 0, opener.Hooks, opener) { _adoptedClose = close };
        HarnessLog.Add(view.Label, "created", new JsonObject { ["inWindow"] = false, ["hooks"] = "app (adopted pop-up)" });
        return view;
    }

    /// <summary>A view created with a configuration the scenario prepares (<paramref name="configure"/> gets the <c>WKWebViewConfiguration*</c>).</summary>
    public static HarnessWebView CreateConfigured(HookOptions hooks, Action<nint> configure)
    {
        var config = ObjC.New("WKWebViewConfiguration");
        try
        {
            ObjC.SendVoid(config, "setWebsiteDataStore:", ObjC.Send(ObjC.Cls("WKWebsiteDataStore"), "nonPersistentDataStore"));
            configure(config);
            return CreateWith(config, hooks, null, 1024, 768, inWindow: true);
        }
        finally
        {
            ObjC.Release(config);
        }
    }

    private static unsafe HarnessWebView CreateWith(nint config, HookOptions hooks, HarnessWebView? opener, double width, double height, bool inWindow)
    {
        var frame = new CGRect(0, 0, width, height);
        var wk = ((delegate* unmanaged<nint, nint, CGRect, nint, nint>)ObjC.MsgSend)(
            ObjC.Send(ObjC.Cls("WKWebView"), "alloc"), ObjC.Sel("initWithFrame:configuration:"), frame, config);
        if (wk == 0) throw new InvalidOperationException("WKWebView could not be created.");
        nint window = 0;
        if (inWindow)
        {
            window = ((delegate* unmanaged<nint, nint, CGRect, ulong, ulong, byte, nint>)ObjC.MsgSend)(
                ObjC.Send(ObjC.Cls("NSWindow"), "alloc"), ObjC.Sel("initWithContentRect:styleMask:backing:defer:"),
                new CGRect(-20000, -20000, width, height), NSWindowStyleMaskBorderless, NSBackingStoreBuffered, 0);
            ObjC.SendVoidBool(window, "setReleasedWhenClosed:", false);
            ObjC.SendVoid(window, "setContentView:", wk); // never ordered in: not on screen
        }
        var label = opener is null ? "main" : $"popup{Interlocked.Increment(ref _counter)}";
        var view = new HarnessWebView(label, wk, window, hooks, opener);
        view.InstallDelegates();
        HarnessLog.Add(label, "created", new JsonObject { ["inWindow"] = inWindow, ["hooks"] = hooks.ToString() });
        return view;
    }

    private void InstallDelegates()
    {
        _navDelegate = ObjC.Send(ObjC.Send(Delegates.NavigationDelegateClass, "alloc"), "init");
        ObjC.SendVoid(Handle, "setNavigationDelegate:", _navDelegate); // weak property: we keep the +1
#if APP_HOOKS
        if (Hooks.AppHooks)
        {
            _app = AppHooksBridge.Attach(this);
            return;
        }
#else
        if (Hooks.AppHooks) throw new InvalidOperationException("The app's hooks are not linked into this build.");
#endif
        if (Hooks.NavigationExtras) Delegates.AddNavigationExtras(Handle);
        if (Hooks.HarnessAuth) Delegates.AddHarnessAuth(Handle);
        if (Hooks.UiDelegate)
        {
            _uiDelegate = ObjC.Send(ObjC.Send(Delegates.UiDelegateClass, "alloc"), "init");
            ObjC.SendVoid(Handle, "setUIDelegate:", _uiDelegate);
        }
    }

    // ── navigation ──

    public Task<JsonObject> LoadAsync(string url, TimeSpan? timeout = null)
    {
        var wait = ExpectNavigation(timeout);
        var request = ObjC.Send(ObjC.Cls("NSURLRequest"), "requestWithURL:", ObjC.Url(url));
        ObjC.Send(Handle, "loadRequest:", request);
        return WithDocumentTitle(wait);
    }

    public Task<JsonObject> ReloadAsync(TimeSpan? timeout = null)
    {
        var wait = ExpectNavigation(timeout);
        ObjC.Send(Handle, "reload");
        return WithDocumentTitle(wait);
    }

    /// <summary>
    /// After a successful navigation, reads <c>document.title</c> into <see cref="DocumentTitle"/> and the result
    /// (<c>WKWebView.title</c> is updated asynchronously and can still be empty right after didFinishNavigation).
    /// </summary>
    private async Task<JsonObject> WithDocumentTitle(Task<JsonObject> navigation)
    {
        var result = await navigation;
        DocumentTitle = null;
        if (result["ok"]?.GetValue<bool>() == true)
        {
            DocumentTitle = await EvalRawAsync("document.title");
            result["title"] = DocumentTitle;
        }
        return result;
    }

    /// <summary><c>document.title</c> of the page the last <see cref="LoadAsync"/>/<see cref="ReloadAsync"/> finished on.</summary>
    public string? DocumentTitle { get; private set; }

    /// <summary>The next navigation's end: <c>{ok, url, title}</c> or <c>{ok:false, error}</c>; a timeout reports <c>{timeout:true}</c>.</summary>
    public Task<JsonObject> ExpectNavigation(TimeSpan? timeout = null)
    {
        _navigation?.TrySetResult(new JsonObject { ["ok"] = false, ["superseded"] = true });
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigation = tcs;
        var limit = timeout ?? TimeSpan.FromSeconds(15);
        _ = Task.Delay(limit).ContinueWith(_ => tcs.TrySetResult(new JsonObject { ["ok"] = false, ["timeout"] = true, ["afterMs"] = limit.TotalMilliseconds }),
            TaskScheduler.Default);
        return tcs.Task;
    }

    internal void NavigationFinished(JsonObject result)
    {
        _navigation?.TrySetResult(result);
        if (result["ok"]?.GetValue<bool>() == true) Finished?.Invoke();
    }

    /// <summary>A navigation finished loading (<c>webView:didFinishNavigation:</c>), whoever started it.</summary>
    public event Action? Finished;

    public string? Url => ObjC.ToStr(ObjC.Send(ObjC.Send(Handle, "URL"), "absoluteString"));

    /// <summary>The WebContent process (SPI <c>_webProcessIdentifier</c>, guarded); 0 if unknown.</summary>
    public int WebProcessId => ObjC.RespondsTo(Handle, "_webProcessIdentifier") ? ObjC.SendInt(Handle, "_webProcessIdentifier") : 0;

    // ── JavaScript ──

    /// <summary><c>evaluateJavaScript:completionHandler:</c> of <c>JSON.stringify(expression)</c>, parsed.</summary>
    public async Task<JsonNode?> EvalAsync(string expression)
    {
        var json = await EvalRawAsync($"JSON.stringify((() => ({expression}))())");
        return json is null ? null : JsonNode.Parse(json);
    }

    /// <summary>Runs a statement-bodied async function in the page world (<c>callAsyncJavaScript</c>), result JSON-parsed.</summary>
    public async Task<JsonNode?> CallAsync(string body)
    {
        var json = await CallRawAsync(body);
        return json is null ? null : JsonNode.Parse(json);
    }

    private unsafe Task<string?> CallRawAsync(string body)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = Blocks.Create((nint)(delegate* unmanaged<nint, nint, nint, void>)&OnScriptResult, tcs, "v24@?0@8@\"NSError\"16");
        try
        {
            var script = ObjC.Str($"return JSON.stringify(await (async () => {{ {body} }})());");
            var args = ObjC.Send(ObjC.Cls("NSDictionary"), "dictionary");
            var world = ObjC.Send(ObjC.Cls("WKContentWorld"), "pageWorld");
            ObjC.SendVoid(Handle, "callAsyncJavaScript:arguments:inFrame:inContentWorld:completionHandler:", script, args, 0, world, block);
        }
        finally
        {
            Blocks.Release(block);
        }
        return tcs.Task;
    }

    /// <summary>
    /// <c>evaluateJavaScript</c> runs as a user gesture (WebKit forces it), which grants the page user activation.
    /// When set, scripts go through the SPI <c>_evaluateJavaScriptWithoutUserGesture:completionHandler:</c> instead, so
    /// reading the page does not change what it observes (trusted-input measurements).
    /// </summary>
    public bool WithoutUserGesture { get; set; }

    public unsafe Task<string?> EvalRawAsync(string script)
    {
        const string NoGesture = "_evaluateJavaScriptWithoutUserGesture:completionHandler:";
        if (WithoutUserGesture && !ObjC.RespondsTo(Handle, NoGesture))
            throw new NotSupportedException("This WebKit has no " + NoGesture);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = Blocks.Create((nint)(delegate* unmanaged<nint, nint, nint, void>)&OnScriptResult, tcs, "v24@?0@8@\"NSError\"16");
        try
        {
            ObjC.SendVoid(Handle, WithoutUserGesture ? NoGesture : "evaluateJavaScript:completionHandler:", ObjC.Str(script), block);
        }
        finally
        {
            Blocks.Release(block);
        }
        return tcs.Task;
    }

    [UnmanagedCallersOnly]
    private static void OnScriptResult(nint block, nint result, nint error)
    {
        try
        {
            var tcs = Blocks.TakeState<TaskCompletionSource<string?>>(block);
            if (tcs is null) return;
            if (error != 0)
            {
                var (domain, code, message) = ObjC.Error(error);
                var info = ObjC.Send(error, "userInfo");
                var jsMessage = info == 0 ? null : ObjC.ToStr(ObjC.Send(info, "objectForKey:", ObjC.Str("WKJavaScriptExceptionMessage")));
                tcs.TrySetException(new JsException(domain, code, jsMessage ?? message));
                return;
            }
            if (result == 0 || ObjC.ClassName(result) is "NSNull") tcs.TrySetResult(null);
            else if (ObjC.SendBool(result, "isKindOfClass:", ObjC.Cls("NSString"))) tcs.TrySetResult(ObjC.ToStr(result));
            else tcs.TrySetResult(ObjC.Describe(result));
        }
        catch
        {
            // nothing may unwind into WebKit
        }
    }

    /// <summary>Polls <paramref name="expression"/> (JS, truthy = done) until it holds or the timeout passes.</summary>
    public async Task<bool> WaitForJsAsync(string expression, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await EvalAsync($"!!({expression})") is JsonValue v && v.GetValue<bool>()) return true;
            }
            catch (JsException)
            {
                // page between loads
            }
            await Task.Delay(50);
        }
        return false;
    }

    public static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(25);
        }
        return true;
    }

    /// <summary>Answers every handler still pending with its default (as the app must on dispose).</summary>
    public int CompletePendingWithDefaults()
    {
        if (CancelPendingOverride is { } cancel) return cancel();
        var n = 0;
        foreach (var p in PendingHandlers.ToList())
            if (p.Default()) n++;
        PendingHandlers.Clear();
        return n;
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        foreach (var popup in Popups.ToList()) popup.Dispose();
        CompletePendingWithDefaults();
#if APP_HOOKS
        _app?.Dispose();
        _app = null;
#endif
        if (_adoptedClose is { } close)
        {
            // The app's pop-up: it tears its view down itself
            Registry.Remove(Handle);
            Handle = 0;
            close();
            Opener?.Popups.Remove(this);
            HarnessLog.Add(Label, "disposed");
            return;
        }
        ObjC.SendVoid(Handle, "stopLoading");
        ObjC.SendVoid(Handle, "setUIDelegate:", 0);
        ObjC.SendVoid(Handle, "setNavigationDelegate:", 0);
        if (Window != 0)
        {
            ObjC.SendVoid(Window, "setContentView:", 0);
            ObjC.SendVoid(Window, "close");
            ObjC.Release(Window);
            Window = 0;
        }
        Registry.Remove(Handle);
        ObjC.Release(Handle);
        ObjC.Release(_uiDelegate);
        ObjC.Release(_navDelegate);
        Handle = 0;
        Opener?.Popups.Remove(this);
        HarnessLog.Add(Label, "disposed");
    }
}

internal sealed class JsException(string? domain, long code, string? message)
    : Exception($"{message} ({domain} {code})")
{
    public string? Domain { get; } = domain;
    public long Code { get; } = code;
}
