using System.Text.Json.Nodes;
using OmpGui.WebViewHarness.Native;
using OmpGui.WebViewHarness.Server;
#if APP_HOOKS
using OmpGui.App.Platform.Mac;
using OmpGui.ClientCore.Browser;
#endif

namespace OmpGui.WebViewHarness.Scenarios;

/// <summary>R1 reproduced: with Avalonia's delegate shape and no hooks, a 401 Basic page just shows nginx's 401 body.</summary>
internal sealed class AuthBaselineScenario : IScenario
{
    public string Name => "auth-baseline";

    public string Description => "401 Basic with no auth hook (today's app): WebKit rejects the challenge, the 401 body loads";

    public async Task RunAsync(HarnessContext ctx)
    {
        var view = ctx.CreateWebView(HookOptions.None);
        var nav = await view.LoadAsync(ctx.Server.Url("/auth/basic"));
        ctx.Check("navigation finishes", nav["ok"]?.GetValue<bool>() == true, nav);
        ctx.Check("the 401 body is shown (no sign-in ever asked)", view.DocumentTitle == "401 Authorization Required", view.DocumentTitle);
        ctx.Check("no credentials were sent", ctx.Server.Requests.All(r => r["authorization"] is null),
            new JsonArray(ctx.Server.Requests.Select(r => (JsonNode)r.DeepClone()).ToArray()));
    }
}

/// <summary>
/// HTTP Basic through a hook on the Avalonia-shaped navigation delegate: the app's <c>WebKitHooks</c> (A1) when
/// linked, else the harness's reference IMP. Wrong → retry with FailedBefore; right → 200; cancel → 401 body;
/// pending challenge answered exactly once on cancel/detach.
/// </summary>
internal sealed class AuthBasicScenario : IScenario
{
    public string Name => "auth-basic";

    public string Description => "401 Basic realm=Studio: wrong then right credentials, session reuse, cancel, pending answered on detach";

    private sealed record Ask(string? Host, long Port, string? Realm, bool IsProxy, bool FailedBefore);

    private delegate Task<(string User, string Password)?> Script(Ask ask, CancellationToken ct);

    public async Task RunAsync(HarnessContext ctx)
    {
        ctx.Data["hooks"] = ctx.HooksMode;
        var url = ctx.Server.Url("/auth/basic");

        // 1. wrong password, then the right one
        var asks = new List<Ask>();
        var a = Attach(ctx, (ask, _) =>
        {
            asks.Add(ask);
            return Task.FromResult<(string, string)?>(asks.Count == 1 ? (TestServer.AuthUser, "wrong") : (TestServer.AuthUser, TestServer.AuthPassword));
        });
        var nav = await a.View.LoadAsync(url);
        ctx.Check("asked for credentials with host and realm",
            asks.Count > 0 && asks[0].Host == "127.0.0.1" && asks[0].Realm == TestServer.AuthRealm && !asks[0].IsProxy && !asks[0].FailedBefore,
            Json(asks));
        ctx.Check("wrong password → asked again with FailedBefore", asks.Count == 2 && asks[1].FailedBefore, Json(asks));
        ctx.Check("right password → the page loads (200)", nav["ok"]?.GetValue<bool>() == true && a.View.DocumentTitle == "signed in",
            new JsonObject { ["nav"] = nav.DeepClone(), ["title"] = a.View.DocumentTitle });

        // 2. the same web view again: the session credential is reused, nobody is asked
        nav = await a.View.LoadAsync(url);
        ctx.Check("credential kept for the session (no second sign-in)", asks.Count == 2 && a.View.DocumentTitle == "signed in",
            new JsonObject { ["asks"] = asks.Count, ["title"] = a.View.DocumentTitle });

        // 3. cancel (fresh data store: nothing cached) → the 401 body, like Safari
        var cancelAsks = 0;
        var b = Attach(ctx, (_, _) =>
        {
            cancelAsks++;
            return Task.FromResult<(string, string)?>(null);
        });
        nav = await b.View.LoadAsync(url);
        ctx.Check("cancel → the 401 body is shown", cancelAsks == 1 && b.View.DocumentTitle == "401 Authorization Required",
            new JsonObject { ["asks"] = cancelAsks, ["title"] = b.View.DocumentTitle, ["nav"] = nav.DeepClone() });

        // 4. challenge left pending, then the page goes away / the hooks detach → answered (Cancel) exactly once
        var pendingAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var c = Attach(ctx, async (_, ct) =>
        {
            pendingAsked.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            return null;
        });
        var pendingNav = c.View.LoadAsync(url);
        var asked = await Task.WhenAny(pendingAsked.Task, Task.Delay(10000)) == pendingAsked.Task;
        ctx.Check("pending: challenge reached the handler", asked);
        c.CancelPending();
        nav = await pendingNav;
        ctx.Check("pending challenge cancelled on navigate-away → 401 body, no WebKit exception",
            nav["ok"]?.GetValue<bool>() == true && c.View.DocumentTitle == "401 Authorization Required",
            new JsonObject { ["title"] = c.View.DocumentTitle, ["handlerSawCancellation"] = cancelled });

        // 5. hooks detached (web view torn down) while a challenge waits for the user → Cancel once, nobody asked again
        var teardownAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownAsks = 0;
        var teardownCancelled = false;
        var e = Attach(ctx, async (_, ct) =>
        {
            teardownAsks++;
            teardownAsked.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                teardownCancelled = true;
            }
            return null;
        });
        var teardownNav = e.View.LoadAsync(url);
        await Task.WhenAny(teardownAsked.Task, Task.Delay(10000));
        e.Detach();
        nav = await teardownNav;
        await Task.Delay(200);
        ctx.Check("detach while pending → handler's token cancelled, 401 body, asked once, no WebKit exception",
            teardownAsks == 1 && teardownCancelled && nav["ok"]?.GetValue<bool>() == true && e.View.DocumentTitle == "401 Authorization Required",
            new JsonObject { ["asks"] = teardownAsks, ["cancelled"] = teardownCancelled, ["title"] = e.View.DocumentTitle, ["nav"] = nav.DeepClone() });

        // 6. detached hooks: challenges no longer reach the handler (WebKit's default: reject → 401 body)
        var detachedAsks = 0;
        var d = Attach(ctx, (_, _) =>
        {
            detachedAsks++;
            return Task.FromResult<(string, string)?>((TestServer.AuthUser, TestServer.AuthPassword));
        });
        d.Detach();
        nav = await d.View.LoadAsync(url);
        ctx.Check("after detach: nobody asked, 401 body", detachedAsks == 0 && d.View.DocumentTitle == "401 Authorization Required",
            new JsonObject { ["asks"] = detachedAsks, ["title"] = d.View.DocumentTitle });

        ctx.Data["requests"] = new JsonArray(ctx.Server.Requests.Select(r => (JsonNode)r.DeepClone()).ToArray());
    }

    private static JsonArray Json(List<Ask> asks) => new(asks.Select(x => (JsonNode)new JsonObject
    {
        ["host"] = x.Host, ["port"] = x.Port, ["realm"] = x.Realm, ["isProxy"] = x.IsProxy, ["failedBefore"] = x.FailedBefore,
    }).ToArray());

    /// <summary>A web view with the auth hook of the selected mode, answering through <paramref name="script"/>.</summary>
    private static Attached Attach(HarnessContext ctx, Script script)
    {
#if APP_HOOKS
        if (ctx.HooksMode == "app")
        {
            var view = ctx.CreateWebView(HookOptions.None);
            var handle = WebKitHooks.Attach(view.Handle, new BrowserPageHooks { Auth = new ScriptedAuth(script) }, out var problem);
            if (handle is null) throw new InvalidOperationException("WebKitHooks.Attach failed: " + problem);
            return new Attached(view, () => WebKitHooks.CancelPending(view.Handle), handle.Dispose);
        }
#endif
        var harnessView = ctx.CreateWebView(new HookOptions { HarnessAuth = true });
        var cts = new CancellationTokenSource();
        var detached = false;
        harnessView.OnAuth = async challenge =>
        {
            if (detached) return null;
            var ask = new Ask(challenge.Host, challenge.Port, challenge.Realm, challenge.IsProxy, challenge.PreviousFailureCount > 0);
            return await script(ask, cts.Token);
        };
        return new Attached(harnessView,
            () =>
            {
                cts.Cancel();
                cts = new CancellationTokenSource();
            },
            () =>
            {
                detached = true;
                harnessView.OnAuth = null;
                cts.Cancel();
                harnessView.CompletePendingWithDefaults();
            });
    }

    private sealed record Attached(HarnessWebView View, Action CancelPending, Action Detach);

#if APP_HOOKS
    private sealed class ScriptedAuth(Script script) : IBrowserAuthHandler
    {
        public async Task<CredentialAnswer?> RequestCredentialsAsync(CredentialRequest request, CancellationToken ct)
        {
            var answer = await script(new Ask(request.Host, request.Port, request.Realm, request.IsProxy, request.FailedBefore), ct);
            return answer is { } x ? new CredentialAnswer(x.User, x.Password) : null;
        }
    }
#endif
}
