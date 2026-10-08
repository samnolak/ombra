using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OmpGui.WebViewHarness.Native;
#if APP_HOOKS
using OmpGui.App.Platform.Mac;
using OmpGui.ClientCore.Browser;
#endif

namespace OmpGui.WebViewHarness.Scenarios;

/// <summary>
/// Navigation failures: an unreachable server is reported with the engine's error (the app's error page), a navigation
/// the next one replaced is not (NSURLErrorCancelled), and responses carry their status and type.
/// </summary>
internal sealed class LoadFailureScenario : IScenario
{
    public string Name => "load-failure";

    public string Description => "failed provisional navigation reported with its NSError, cancelled navigation not reported, response status";

    public async Task RunAsync(HarnessContext ctx)
    {
        var view = ctx.CreateWebView(HookOptions.Reference);
        var closedPort = ClosedPort();
        var nav = await view.LoadAsync($"http://127.0.0.1:{closedPort}/");
        ctx.Check("unreachable server → failed provisional navigation with NSURLErrorDomain -1004",
            nav["ok"]?.GetValue<bool>() == false && (string?)nav["domain"] == "NSURLErrorDomain" && nav["code"]?.GetValue<long>() == -1004,
            nav);

        // A navigation replaced by the next one (NSURLErrorCancelled) is not a failure for the user
        var replaced = view.ExpectNavigation(TimeSpan.FromSeconds(8));
        ObjC.Send(view.Handle, "loadRequest:", ObjC.Send(ObjC.Cls("NSURLRequest"), "requestWithURL:", ObjC.Url(ctx.Server.Url("/slow"))));
        await Task.Delay(300);
        var next = await view.LoadAsync(ctx.Server.Url("/"));
        _ = replaced;
        var cancelledReported = HarnessLog.Snapshot().Any(e => (string?)e["event"] is "didFailProvisionalNavigation" or "didFailNavigation" && e["code"]?.GetValue<long>() == -999);
        // (the reference delegate reports every failure, -999 included, so its first finish can be the replaced one)
        var loaded = await view.WaitForJsAsync("document.title === 'harness'", TimeSpan.FromSeconds(5));
        ctx.Check("the next page loads", loaded, next);
        if (ctx.HooksMode == "app")
        {
            ctx.Check("app: the replaced navigation (-999) is not reported as a failure",
                !cancelledReported && next["ok"]?.GetValue<bool>() == true, next);
#if APP_HOOKS
            var responses = HarnessLog.Snapshot().Where(e => (string?)e["event"] == "decidePolicyForNavigationResponse").ToList();
            await view.LoadAsync(ctx.Server.Url("/missing-page"));
            var missing = HarnessLog.Snapshot().LastOrDefault(e => (string?)e["event"] == "decidePolicyForNavigationResponse");
            ctx.Check("app: responses are reported with status, type and main frame (404 page)",
                missing?["status"]?.GetValue<int>() == 404 && (string?)missing["mime"] == "text/plain" && missing["mainFrame"]?.GetValue<bool>() == true
                && (string?)missing["url"] == ctx.Server.Url("/missing-page"), missing);
            ctx.Data["responsesBefore404"] = responses.Count;
#endif
        }
    }

    private static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

/// <summary>
/// The user agent the app sets (Safari's, with its "Version/… Safari/605.1.15" token), in the page and on the wire, and
/// in a pop-up built from the opener's configuration.
/// </summary>
internal sealed class UserAgentScenario : IScenario
{
    public string Name => "user-agent";

    public string Description => "applicationNameForUserAgent = Safari's token: navigator.userAgent and the HTTP header match Safari's format; pop-ups inherit it";

    private static readonly Regex SafariFormat = new(
        @"^Mozilla/5\.0 \(Macintosh; Intel Mac OS X 10_15_7\) AppleWebKit/605\.1\.15 \(KHTML, like Gecko\) Version/(?<v>\d+(\.\d+)*) Safari/605\.1\.15$");

    public async Task RunAsync(HarnessContext ctx)
    {
#if APP_HOOKS
        var (safariVersion, safariBuild, webKitBuild) = SafariIdentity.Inputs();
        var appName = SafariIdentity.ApplicationNameForUserAgent;
        ctx.Data["safari"] = new JsonObject { ["version"] = safariVersion, ["build"] = safariBuild, ["loadedWebKitBuild"] = webKitBuild, ["applicationName"] = appName };

        var baseline = ctx.CreateWebView(HookOptions.None);
        await baseline.LoadAsync(ctx.Server.Url("/"));
        var plain = (await baseline.EvalAsync("navigator.userAgent"))?.GetValue<string>();
        ctx.Check("baseline (no application name): WKWebView's user agent lacks Safari's Version/ token", plain is not null && !plain.Contains("Version/"), plain);

        var view = ctx.CreateConfiguredWebView(HookOptions.Reference, config => ObjC.SendVoid(config, "setApplicationNameForUserAgent:", ObjC.Str(appName)));
        await view.LoadAsync(ctx.Server.Url("/"));
        var ua = (await view.EvalAsync("navigator.userAgent"))?.GetValue<string>();
        var match = ua is null ? null : SafariFormat.Match(ua);
        ctx.Check("navigator.userAgent has Safari's exact format", match?.Success == true, ua);
        var expectedVersion = safariVersion is not null && safariBuild is not null && safariBuild == webKitBuild ? safariVersion : null;
        ctx.Check("version: Safari's own when the loaded WebKit is Safari's, else this macOS's Safari version",
            match?.Success == true && appName == SafariUserAgent.ApplicationName(safariVersion, safariBuild, webKitBuild, Environment.OSVersion.Version)
            && (expectedVersion is null || match.Groups["v"].Value == expectedVersion),
            new JsonObject { ["version"] = match?.Groups["v"].Value, ["expected"] = expectedVersion ?? "(OS fallback)" });
        var header = ctx.Server.Requests.LastOrDefault(r => (string?)r["path"] == "/")?["userAgent"]?.GetValue<string>();
        ctx.Check("the HTTP User-Agent header is the same", header == ua, header);

        // A pop-up is built from the opener's configuration: same user agent
        await view.EvalAsync("window.open('/', 'ua-check'), true");
        var opened = await HarnessWebView.WaitForAsync(() => view.Popups.Count > 0, TimeSpan.FromSeconds(5));
        string? popupUa = null;
        if (opened)
        {
            var popup = view.Popups[0];
            await popup.WaitForJsAsync("document.readyState === 'complete' && location.pathname === '/'", TimeSpan.FromSeconds(5));
            popupUa = (await popup.EvalAsync("navigator.userAgent"))?.GetValue<string>();
        }
        ctx.Check("a pop-up has the same user agent", opened && popupUa == ua, popupUa);
#else
        ctx.Check("the app's SafariIdentity is linked", false);
        await Task.CompletedTask;
#endif
    }
}

#if APP_HOOKS
/// <summary>
/// Camera and microphone requests through the app's UI delegate: the permission card, "Always allow on this site"
/// remembered, Deny, and the handler answered once when the page goes. The IMP is called directly with a synthetic
/// <c>WKSecurityOrigin</c>: WebKit refuses a real <c>getUserMedia</c> before asking any delegate when the process has
/// no usage description (this harness has none), and a real grant would ask macOS for the device (a TCC prompt).
/// </summary>
internal sealed class PermissionScenario : IAppHooksOnly
{
    private const long Grant = 1, Deny = 2;

    public string Name => "permission";

    public string Description => "requestMediaCapturePermission: card, Allow once, Always for site remembered, Deny, pending answered on detach";

    private static readonly Dictionary<nint, List<long>> Decisions = [];

    private static readonly unsafe nint FakeOriginClass = ObjC.DefineClass("OmpHarnessFakeSecurityOrigin", null,
        ("protocol", (nint)(delegate* unmanaged<nint, nint, nint>)&Protocol, "@@:"),
        ("host", (nint)(delegate* unmanaged<nint, nint, nint>)&Host, "@@:"),
        ("port", (nint)(delegate* unmanaged<nint, nint, long>)&Port, "q@:"));

    private static nint _protocol, _host;
    private static long _port;

    [UnmanagedCallersOnly]
    private static nint Protocol(nint self, nint cmd) => _protocol;

    [UnmanagedCallersOnly]
    private static nint Host(nint self, nint cmd) => _host;

    [UnmanagedCallersOnly]
    private static long Port(nint self, nint cmd) => _port;

    [UnmanagedCallersOnly]
    private static void OnDecision(nint block, long decision)
    {
        lock (Decisions)
        {
            if (Decisions.TryGetValue(block, out var list)) list.Add(decision);
        }
    }

    public async Task RunAsync(HarnessContext ctx)
    {
        if (!ctx.Check("app hooks (this scenario tests the app's UI delegate)", ctx.HooksMode == "app")) return;
        var view = ctx.CreateWebView(HookOptions.Reference);
        await view.LoadAsync(ctx.Server.Url("/"));
        var app = view.App!;
        _protocol = ObjC.Retain(ObjC.Str("http"));
        _host = ObjC.Retain(ObjC.Str("127.0.0.1"));
        _port = ctx.Server.Port;
        if (FakeOriginClass == 0) throw new InvalidOperationException("The synthetic origin class is not registered.");
        var origin = ObjC.New("OmpHarnessFakeSecurityOrigin");
        var site = $"http://127.0.0.1:{ctx.Server.Port}";

        var answers = new Queue<PermissionAnswer?>();
        app.Intercept = dialog =>
        {
            if (dialog.Request is not PermissionRequest) return false;
            if (answers.TryDequeue(out var a) && a is { } answer) dialog.Complete(answer);
            return true; // null: left open
        };

        answers.Enqueue(PermissionAnswer.AllowOnce);
        var first = await Request(view, origin, type: 1);
        var asked = app.Asked.OfType<PermissionRequest>().ToList();
        ctx.Check("microphone: the card asks with the site and kind; Allow once → granted, answered once",
            first.Count == 1 && first[0] == Grant && asked.Count == 1 && asked[0].Permission == PermissionKind.Microphone && asked[0].Origin == site,
            Report(first, asked));

        answers.Enqueue(PermissionAnswer.AlwaysForSite);
        var second = await Request(view, origin, type: 1);
        asked = app.Asked.OfType<PermissionRequest>().ToList();
        ctx.Check("Allow once is not remembered: asked again; Always allow on this site → granted",
            second.Count == 1 && second[0] == Grant && asked.Count == 2 && app.Hooks.SiteSettings!.IsAlwaysAllowed(site, PermissionKind.Microphone),
            Report(second, asked));

        var third = await Request(view, origin, type: 1);
        ctx.Check("remembered: granted without a card", third.Count == 1 && third[0] == Grant && app.Asked.OfType<PermissionRequest>().Count() == 2,
            Report(third, app.Asked.OfType<PermissionRequest>().ToList()));

        answers.Enqueue(PermissionAnswer.Deny);
        var camera = await Request(view, origin, type: 0);
        asked = app.Asked.OfType<PermissionRequest>().ToList();
        ctx.Check("camera is its own question; Don't allow → denied",
            camera.Count == 1 && camera[0] == Deny && asked.Count == 3 && asked[2].Permission == PermissionKind.Camera, Report(camera, asked));

        // Left open, then the page goes (hooks detached): denied, exactly once
        answers.Enqueue(null);
        var block = MakeBlock();
        Invoke(view, origin, 2, block);
        await HarnessWebView.WaitForAsync(() => app.Broker.Pending.Count == 1, TimeSpan.FromSeconds(3));
        var openCard = app.Broker.Pending.FirstOrDefault()?.Request as PermissionRequest;
        view.Dispose();
        await Task.Delay(300);
        var detached = Taken(block);
        ctx.Check("camera and microphone left open, then the page closes → denied exactly once",
            openCard?.Permission == PermissionKind.CameraAndMicrophone && detached.Count == 1 && detached[0] == Deny, Report(detached, []));
        ObjC.Release(origin);
    }

    private static JsonObject Report(List<long> decisions, List<PermissionRequest> asked) => new()
    {
        ["decisions"] = new JsonArray([.. decisions.Select(d => (JsonNode)d)]),
        ["asked"] = new JsonArray([.. asked.Select(a => (JsonNode)$"{a.Permission} {a.Origin}")]),
    };

    private static async Task<List<long>> Request(HarnessWebView view, nint origin, long type)
    {
        var block = MakeBlock();
        Invoke(view, origin, type, block);
        await HarnessWebView.WaitForAsync(() => Peek(block) > 0, TimeSpan.FromSeconds(3));
        await Task.Delay(100); // a second answer would come now
        return Taken(block);
    }

    private static unsafe nint MakeBlock()
    {
        var block = Blocks.Create((nint)(delegate* unmanaged<nint, long, void>)&OnDecision, new object(), "v16@?0q8");
        lock (Decisions) Decisions[block] = [];
        return block;
    }

    private static int Peek(nint block)
    {
        lock (Decisions) return Decisions.TryGetValue(block, out var l) ? l.Count : 0;
    }

    private static List<long> Taken(nint block)
    {
        lock (Decisions)
        {
            Decisions.Remove(block, out var list);
            return list ?? [];
        }
    }

    /// <summary><c>-[OmpWKUIDelegate webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler:]</c>.</summary>
    private static unsafe void Invoke(HarnessWebView view, nint origin, long type, nint block)
    {
        const string Selector = "webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler:";
        var imp = WebKitPage.UiImplementation(Selector);
        if (imp == 0) throw new InvalidOperationException("The app's UI delegate has no " + Selector);
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, long, nint, void>)imp)(0, ObjC.Sel(Selector), view.Handle, origin, 0, type, block);
        Blocks.Release(block); // the delegate copied it if it keeps it
    }
}

/// <summary>A page's beforeunload handler asks through the app's UI delegate (SPI): Stay keeps the page, Leave navigates.</summary>
internal sealed class BeforeUnloadScenario : IAppHooksOnly
{
    public string Name => "beforeunload";

    public string Description => "\"Leave page?\" through _webView:runBeforeUnloadConfirmPanel…: Stay keeps the page, Leave navigates";

    public async Task RunAsync(HarnessContext ctx)
    {
        if (!ctx.Check("app hooks (this scenario tests the app's UI delegate)", ctx.HooksMode == "app")) return;
        var view = ctx.CreateWebView(HookOptions.Reference);
        var app = view.App!;
        var answers = new Queue<bool>([false, true]);
        app.Intercept = dialog =>
        {
            if (dialog.Kind != BrowserDialogKind.BeforeUnload) return false;
            dialog.Complete(answers.Dequeue());
            return true;
        };
        await view.LoadAsync(ctx.Server.Url("/beforeunload"));
        await view.EvalAsync("document.getElementById('r').click(), true"); // evaluateJavaScript is a user gesture: the page may ask now

        var stay = view.ExpectNavigation(TimeSpan.FromSeconds(3));
        await view.EvalAsync($"location.href = {System.Text.Json.JsonSerializer.Serialize(ctx.Server.Url("/"))}, true");
        await stay;
        await Task.Delay(300);
        var asked = app.Asked.OfType<BeforeUnloadRequest>().Count();
        ctx.Check("asked through the broker; Stay → still on the page", asked == 1 && view.Url?.EndsWith("/beforeunload") == true,
            new JsonObject { ["asked"] = asked, ["url"] = view.Url });

        var leave = view.ExpectNavigation(TimeSpan.FromSeconds(5));
        await view.EvalAsync($"location.href = {System.Text.Json.JsonSerializer.Serialize(ctx.Server.Url("/"))}, true");
        var nav = await leave;
        ctx.Check("asked again; Leave → the next page loads", app.Asked.OfType<BeforeUnloadRequest>().Count() == 2 && nav["ok"]?.GetValue<bool>() == true
            && view.Url == ctx.Server.Url("/"), new JsonObject { ["nav"] = nav.DeepClone(), ["url"] = view.Url });
    }
}
#endif
