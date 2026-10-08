using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace OmpGui.WebViewHarness.Native;

/// <summary>
/// The harness's Objective-C delegate classes. <c>OmpHarnessNavDelegate</c> mirrors Avalonia 12.1's
/// <c>ManagedWKNavigationDelegate</c> (only <c>didFinishNavigation</c> and the 3-argument
/// <c>decidePolicyForNavigationAction</c>). Everything else is the reference implementation of the hooks in
/// <c>browser-native.md</c> §3: a UI delegate of our own, selectors added to the navigation delegate's class with
/// <c>class_addMethod</c> followed by re-assigning the delegate, and a download delegate. IMPs find their web view by
/// the <c>webView</c> argument. Every block WebKit hands us is answered exactly once.
/// </summary>
internal static unsafe partial class Delegates
{
    private const long PolicyCancel = 0, PolicyAllow = 1, PolicyDownload = 2;
    private const long DispositionUseCredential = 0, DispositionDefault = 1, DispositionCancel = 2;
    private const ulong NSURLCredentialPersistenceForSession = 1;

    /// <summary>The main thread's context (set by Program); deferred work from inside callbacks goes here.</summary>
    public static SynchronizationContext Main { get; set; } = null!;

    private static readonly Dictionary<nint, (HarnessWebView View, DownloadRecord Record)> DownloadsByHandle = [];

    private static nint _downloadDelegate;

    // ── classes ──

    public static nint NavigationDelegateClass { get; } = ObjC.DefineClass("OmpHarnessNavDelegate", "WKNavigationDelegate",
        ("webView:didFinishNavigation:", (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&DidFinishNavigation, "v@:@@"),
        ("webView:decidePolicyForNavigationAction:decisionHandler:",
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DecidePolicyForAction, "v@:@@@"));

    public static nint UiDelegateClass { get; } = ObjC.DefineClass("OmpHarnessUIDelegate", "WKUIDelegate",
        ("webView:runJavaScriptAlertPanelWithMessage:initiatedByFrame:completionHandler:",
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)&RunAlert, "v@:@@@@"),
        ("webView:runJavaScriptConfirmPanelWithMessage:initiatedByFrame:completionHandler:",
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)&RunConfirm, "v@:@@@@"),
        ("webView:runJavaScriptTextInputPanelWithPrompt:defaultText:initiatedByFrame:completionHandler:",
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, void>)&RunPrompt, "v@:@@@@@"),
        ("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:",
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)&CreateWebView, "@@:@@@@"),
        ("webViewDidClose:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&DidClose, "v@:@"),
        ("webView:runOpenPanelWithParameters:initiatedByFrame:completionHandler:",
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)&RunOpenPanel, "v@:@@@@"));

    private static nint DownloadDelegateClass { get; } = ObjC.DefineClass("OmpHarnessDownloadDelegate", "WKDownloadDelegate",
        ("download:decideDestinationUsingResponse:suggestedFilename:completionHandler:",
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)&DecideDestination, "v@:@@@@"),
        ("downloadDidFinish:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&DownloadDidFinish, "v@:@"),
        ("download:didFailWithError:resumeData:",
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DownloadDidFail, "v@:@@@"));

    /// <summary>Adds the reference navigation selectors to the class of the web view's navigation delegate.</summary>
    public static JsonObject AddNavigationExtras(nint webView)
    {
        var cls = ObjC.ClassOf(ObjC.Send(webView, "navigationDelegate"));
        var added = new JsonObject
        {
            ["didStartProvisional"] = ObjC.AddMethodIfMissing(cls, "webView:didStartProvisionalNavigation:",
                (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&DidStartProvisional, "v@:@@"),
            ["didFailProvisional"] = ObjC.AddMethodIfMissing(cls, "webView:didFailProvisionalNavigation:withError:",
                (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidFailProvisional, "v@:@@@"),
            ["didFail"] = ObjC.AddMethodIfMissing(cls, "webView:didFailNavigation:withError:",
                (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidFail, "v@:@@@"),
            ["processTerminated"] = ObjC.AddMethodIfMissing(cls, "webViewWebContentProcessDidTerminate:",
                (nint)(delegate* unmanaged<nint, nint, nint, void>)&ProcessDidTerminate, "v@:@"),
            ["responsePolicy"] = ObjC.AddMethodIfMissing(cls, "webView:decidePolicyForNavigationResponse:decisionHandler:",
                (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DecidePolicyForResponse, "v@:@@@"),
            ["actionPolicyWithPreferences"] = ObjC.AddMethodIfMissing(cls, "webView:decidePolicyForNavigationAction:preferences:decisionHandler:",
                (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)&DecidePolicyForActionWithPreferences, "v@:@@@@"),
            ["actionBecameDownload"] = ObjC.AddMethodIfMissing(cls, "webView:navigationAction:didBecomeDownload:",
                (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidBecomeDownload, "v@:@@@"),
            ["responseBecameDownload"] = ObjC.AddMethodIfMissing(cls, "webView:navigationResponse:didBecomeDownload:",
                (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidBecomeDownload, "v@:@@@"),
        };
        // WebKit caches respondsToSelector: in setNavigationDelegate:, so re-assign the same object
        ObjC.SendVoid(webView, "setNavigationDelegate:", ObjC.Send(webView, "navigationDelegate"));
        HarnessLog.Add(HarnessWebView.Find(webView)?.Label ?? "?", "navigationExtrasAdded", added);
        return added;
    }

    public static bool AddHarnessAuth(nint webView)
    {
        var cls = ObjC.ClassOf(ObjC.Send(webView, "navigationDelegate"));
        var added = ObjC.AddMethodIfMissing(cls, "webView:didReceiveAuthenticationChallenge:completionHandler:",
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidReceiveChallenge, "v@:@@@");
        ObjC.SendVoid(webView, "setNavigationDelegate:", ObjC.Send(webView, "navigationDelegate"));
        HarnessLog.Add(HarnessWebView.Find(webView)?.Label ?? "?", "harnessAuthAdded", new JsonObject { ["added"] = added });
        return added;
    }

    // ── helpers ──

    private static string Label(nint webView) => HarnessWebView.Find(webView)?.Label ?? "?";

    private static string? UrlOf(nint request) => ObjC.ToStr(ObjC.Send(ObjC.Send(request, "URL"), "absoluteString"));

    private static (string? Host, bool IsMain) FrameInfo(nint frame) =>
        frame == 0 ? (null, true) : (ObjC.ToStr(ObjC.Send(ObjC.Send(frame, "securityOrigin"), "host")), ObjC.SendBool(frame, "isMainFrame"));

    private static void Guard(string where, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            HarnessLog.Add("?", "imp-exception", new JsonObject { ["where"] = where, ["error"] = e.ToString() });
        }
    }

    private static void InvokeVoid(nint b) => ((delegate* unmanaged<nint, void>)Blocks.InvokePointer(b))(b);
    private static void InvokeBool(nint b, bool v) => ((delegate* unmanaged<nint, byte, void>)Blocks.InvokePointer(b))(b, v ? (byte)1 : (byte)0);
    private static void InvokeObject(nint b, nint o) => ((delegate* unmanaged<nint, nint, void>)Blocks.InvokePointer(b))(b, o);
    private static void InvokeLong(nint b, long v) => ((delegate* unmanaged<nint, long, void>)Blocks.InvokePointer(b))(b, v);
    private static void InvokeLongObject(nint b, long v, nint o) => ((delegate* unmanaged<nint, long, nint, void>)Blocks.InvokePointer(b))(b, v, o);

    // ── navigation delegate (Avalonia shape) ──

    [UnmanagedCallersOnly]
    private static void DidFinishNavigation(nint self, nint cmd, nint webView, nint navigation) => Guard(nameof(DidFinishNavigation), () =>
    {
        var view = HarnessWebView.Find(webView);
        var result = new JsonObject { ["ok"] = true, ["url"] = view?.Url };
        HarnessLog.Add(Label(webView), "didFinishNavigation", (JsonObject)result.DeepClone());
        view?.NavigationFinished(result);
    });

    [UnmanagedCallersOnly]
    private static void DecidePolicyForAction(nint self, nint cmd, nint webView, nint action, nint handler)
    {
        try
        {
            var target = ObjC.Send(action, "targetFrame");
            HarnessLog.Add(Label(webView), "decidePolicyForNavigationAction", new JsonObject
            {
                ["url"] = UrlOf(ObjC.Send(action, "request")),
                ["navigationType"] = ObjC.SendLong(action, "navigationType"),
                ["targetFrameNil"] = target == 0,
            });
        }
        catch (Exception e)
        {
            HarnessLog.Add("?", "imp-exception", new JsonObject { ["where"] = nameof(DecidePolicyForAction), ["error"] = e.Message });
        }
        InvokeLong(handler, PolicyAllow); // what Avalonia answers for ordinary navigations
    }

    // ── navigation extras (class_addMethod) ──

    [UnmanagedCallersOnly]
    private static void DidStartProvisional(nint self, nint cmd, nint webView, nint navigation) =>
        Guard(nameof(DidStartProvisional), () => HarnessLog.Add(Label(webView), "didStartProvisionalNavigation",
            new JsonObject { ["url"] = HarnessWebView.Find(webView)?.Url }));

    [UnmanagedCallersOnly]
    private static void DidFailProvisional(nint self, nint cmd, nint webView, nint navigation, nint error) =>
        Guard(nameof(DidFailProvisional), () => Failed(webView, "didFailProvisionalNavigation", error));

    [UnmanagedCallersOnly]
    private static void DidFail(nint self, nint cmd, nint webView, nint navigation, nint error) =>
        Guard(nameof(DidFail), () => Failed(webView, "didFailNavigation", error));

    private static void Failed(nint webView, string ev, nint error)
    {
        var (domain, code, message) = ObjC.Error(error);
        var data = new JsonObject { ["ok"] = false, ["domain"] = domain, ["code"] = code, ["error"] = message };
        HarnessLog.Add(Label(webView), ev, (JsonObject)data.DeepClone());
        HarnessWebView.Find(webView)?.NavigationFinished(data);
    }

    [UnmanagedCallersOnly]
    private static void ProcessDidTerminate(nint self, nint cmd, nint webView) => Guard(nameof(ProcessDidTerminate), () =>
    {
        var view = HarnessWebView.Find(webView);
        if (view is null) return;
        view.ContentProcessTerminations++;
        var answered = view.CompletePendingWithDefaults();
        HarnessLog.Add(view.Label, "webContentProcessDidTerminate", new JsonObject { ["pendingAnsweredWithDefault"] = answered });
        view.NavigationFinished(new JsonObject { ["ok"] = false, ["crashed"] = true });
    });

    [UnmanagedCallersOnly]
    private static void DecidePolicyForResponse(nint self, nint cmd, nint webView, nint navigationResponse, nint handler)
    {
        var policy = PolicyAllow;
        try
        {
            var response = ObjC.Send(navigationResponse, "response");
            var canShow = ObjC.SendBool(navigationResponse, "canShowMIMEType");
            string? disposition = null;
            if (ObjC.SendBool(response, "isKindOfClass:", ObjC.Cls("NSHTTPURLResponse")))
                disposition = ObjC.ToStr(ObjC.Send(response, "valueForHTTPHeaderField:", ObjC.Str("Content-Disposition")));
            var attachment = disposition?.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase) == true;
            if (!canShow || attachment) policy = PolicyDownload;
            HarnessLog.Add(Label(webView), "decidePolicyForNavigationResponse", new JsonObject
            {
                ["url"] = ObjC.ToStr(ObjC.Send(ObjC.Send(response, "URL"), "absoluteString")),
                ["mime"] = ObjC.ToStr(ObjC.Send(response, "MIMEType")),
                ["canShowMIMEType"] = canShow,
                ["contentDisposition"] = disposition,
                ["policy"] = policy,
            });
        }
        catch (Exception e)
        {
            HarnessLog.Add("?", "imp-exception", new JsonObject { ["where"] = nameof(DecidePolicyForResponse), ["error"] = e.Message });
        }
        InvokeLong(handler, policy);
    }

    /// <summary>State of the forwarded 4-argument policy call: WebKit's handler (copied) and its preferences (retained).</summary>
    private sealed class ForwardedPolicy(nint webView, nint handler, nint preferences, bool shouldDownload)
    {
        public readonly nint WebView = webView;
        public readonly PendingBlock Handler = new(handler, b => InvokeLongObject(b, PolicyCancel, 0));
        public readonly nint Preferences = ObjC.Retain(preferences);
        public readonly bool ShouldDownload = shouldDownload;
    }

    /// <summary>
    /// WebKit prefers this variant over the 3-argument one. Forward to the 3-argument IMP (Avalonia's in the app) with
    /// our own block, then turn Allow into Download when the action is an <c>&lt;a download&gt;</c>.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void DecidePolicyForActionWithPreferences(nint self, nint cmd, nint webView, nint action, nint preferences, nint handler)
    {
        ForwardedPolicy? state = null;
        try
        {
            var shouldDownload = ObjC.RespondsTo(action, "shouldPerformDownload") && ObjC.SendBool(action, "shouldPerformDownload");
            state = new ForwardedPolicy(webView, handler, preferences, shouldDownload);
            var block = Blocks.Create((nint)(delegate* unmanaged<nint, long, void>)&OnForwardedPolicy, state, "v16@?0q8");
            try
            {
                ObjC.SendVoid(self, "webView:decidePolicyForNavigationAction:decisionHandler:", webView, action, block);
            }
            finally
            {
                Blocks.Release(block);
            }
        }
        catch (Exception e)
        {
            HarnessLog.Add("?", "imp-exception", new JsonObject { ["where"] = nameof(DecidePolicyForActionWithPreferences), ["error"] = e.Message });
            if (state is null) InvokeLongObject(handler, PolicyAllow, preferences);
            else state.Handler.Complete(b => InvokeLongObject(b, PolicyAllow, state.Preferences));
        }
    }

    [UnmanagedCallersOnly]
    private static void OnForwardedPolicy(nint block, long policy) => Guard(nameof(OnForwardedPolicy), () =>
    {
        var state = Blocks.TakeState<ForwardedPolicy>(block);
        if (state is null) return;
        var final = policy == PolicyAllow && state.ShouldDownload ? PolicyDownload : policy;
        HarnessLog.Add(Label(state.WebView), "actionPolicyForwarded", new JsonObject
        {
            ["avaloniaPolicy"] = policy, ["shouldPerformDownload"] = state.ShouldDownload, ["policy"] = final,
        });
        state.Handler.Complete(b => InvokeLongObject(b, final, state.Preferences));
        ObjC.Release(state.Preferences);
    });

    [UnmanagedCallersOnly]
    private static void DidBecomeDownload(nint self, nint cmd, nint webView, nint actionOrResponse, nint download) => Guard(nameof(DidBecomeDownload), () =>
    {
        var view = HarnessWebView.Find(webView);
        if (view is null) return;
        if (_downloadDelegate == 0) _downloadDelegate = ObjC.Send(ObjC.Send(DownloadDelegateClass, "alloc"), "init");
        var record = new DownloadRecord(ObjC.ClassName(actionOrResponse) ?? "?");
        view.Downloads.Add(record);
        DownloadsByHandle[ObjC.Retain(download)] = (view, record); // delegate is weak; keep the download alive
        ObjC.SendVoid(download, "setDelegate:", _downloadDelegate);
        HarnessLog.Add(view.Label, "didBecomeDownload", new JsonObject { ["from"] = record.Source });
    });

    // ── download delegate ──

    [UnmanagedCallersOnly]
    private static void DecideDestination(nint self, nint cmd, nint download, nint response, nint suggestedFilename, nint handler)
    {
        nint url = 0;
        try
        {
            if (DownloadsByHandle.TryGetValue(download, out var entry))
            {
                entry.Record.SuggestedFilename = ObjC.ToStr(suggestedFilename);
                var path = entry.View.DownloadDestination?.Invoke(entry.Record);
                entry.Record.Destination = path;
                if (path is not null) url = ObjC.FileUrl(path);
                HarnessLog.Add(entry.View.Label, "decideDestination", new JsonObject
                {
                    ["suggestedFilename"] = entry.Record.SuggestedFilename, ["destination"] = path,
                });
            }
        }
        catch (Exception e)
        {
            HarnessLog.Add("?", "imp-exception", new JsonObject { ["where"] = nameof(DecideDestination), ["error"] = e.Message });
            url = 0;
        }
        InvokeObject(handler, url); // nil cancels
    }

    [UnmanagedCallersOnly]
    private static void DownloadDidFinish(nint self, nint cmd, nint download) => Guard(nameof(DownloadDidFinish), () =>
    {
        if (!DownloadsByHandle.Remove(download, out var entry)) return;
        entry.Record.Finished = true;
        HarnessLog.Add(entry.View.Label, "downloadDidFinish", new JsonObject { ["destination"] = entry.Record.Destination });
        ObjC.Release(download);
    });

    [UnmanagedCallersOnly]
    private static void DownloadDidFail(nint self, nint cmd, nint download, nint error, nint resumeData) => Guard(nameof(DownloadDidFail), () =>
    {
        if (!DownloadsByHandle.Remove(download, out var entry)) return;
        var (domain, code, message) = ObjC.Error(error);
        entry.Record.Error = $"{message} ({domain} {code})";
        HarnessLog.Add(entry.View.Label, "downloadDidFail", new JsonObject { ["error"] = entry.Record.Error });
        ObjC.Release(download);
    });

    // ── authentication (reference) ──

    [UnmanagedCallersOnly]
    private static void DidReceiveChallenge(nint self, nint cmd, nint webView, nint challenge, nint handler)
    {
        var view = HarnessWebView.Find(webView);
        try
        {
            var space = ObjC.Send(challenge, "protectionSpace");
            var method = ObjC.ToStr(ObjC.Send(space, "authenticationMethod"));
            var info = new AuthChallenge(method, ObjC.ToStr(ObjC.Send(space, "host")), ObjC.SendLong(space, "port"),
                ObjC.ToStr(ObjC.Send(space, "realm")), ObjC.SendBool(space, "isProxy"), ObjC.SendLong(challenge, "previousFailureCount"));
            HarnessLog.Add(Label(webView), "didReceiveAuthenticationChallenge", new JsonObject
            {
                ["method"] = info.Method, ["host"] = info.Host, ["port"] = info.Port, ["realm"] = info.Realm,
                ["isProxy"] = info.IsProxy, ["previousFailureCount"] = info.PreviousFailureCount,
            });
            var userAuth = method is "NSURLAuthenticationMethodHTTPBasic" or "NSURLAuthenticationMethodHTTPDigest"
                or "NSURLAuthenticationMethodNTLM" or "NSURLAuthenticationMethodNegotiate" or "NSURLAuthenticationMethodDefault";
            if (view?.OnAuth is null || !userAuth)
            {
                InvokeLongObject(handler, DispositionDefault, 0);
                return;
            }
            var pending = new PendingBlock(handler, b => InvokeLongObject(b, DispositionCancel, 0));
            view.PendingHandlers.Add(pending);
            _ = AnswerAuthAsync(view, info, pending);
        }
        catch (Exception e)
        {
            HarnessLog.Add("?", "imp-exception", new JsonObject { ["where"] = nameof(DidReceiveChallenge), ["error"] = e.Message });
            InvokeLongObject(handler, DispositionCancel, 0);
        }
    }

    private static void UseCredential(nint block, string user, string password)
    {
        var cred = ((delegate* unmanaged<nint, nint, nint, nint, ulong, nint>)ObjC.MsgSend)(ObjC.Cls("NSURLCredential"),
            ObjC.Sel("credentialWithUser:password:persistence:"), ObjC.Str(user), ObjC.Str(password), NSURLCredentialPersistenceForSession);
        InvokeLongObject(block, DispositionUseCredential, cred);
    }

    // ── UI delegate ──

    [UnmanagedCallersOnly]
    private static void RunAlert(nint self, nint cmd, nint webView, nint message, nint frame, nint handler) =>
        JsPanel(webView, "alert", message, 0, frame, handler, InvokeVoid, (b, _) => InvokeVoid(b));

    [UnmanagedCallersOnly]
    private static void RunConfirm(nint self, nint cmd, nint webView, nint message, nint frame, nint handler) =>
        JsPanel(webView, "confirm", message, 0, frame, handler, b => InvokeBool(b, false), (b, v) => InvokeBool(b, v is true));

    [UnmanagedCallersOnly]
    private static void RunPrompt(nint self, nint cmd, nint webView, nint prompt, nint defaultText, nint frame, nint handler) =>
        JsPanel(webView, "prompt", prompt, defaultText, frame, handler, b => InvokeObject(b, 0),
            (b, v) => InvokeObject(b, v is string s ? ObjC.Str(s) : 0));

    private static void JsPanel(nint webView, string kind, nint message, nint defaultText, nint frame, nint handler,
        Action<nint> answerDefault, Action<nint, object?> answer)
    {
        try
        {
            var view = HarnessWebView.Find(webView);
            var (host, isMain) = FrameInfo(frame);
            var dialog = new JsDialog(kind, ObjC.ToStr(message), ObjC.ToStr(defaultText), host, isMain);
            HarnessLog.Add(Label(webView), kind, new JsonObject
            {
                ["message"] = dialog.Message, ["defaultText"] = dialog.DefaultText, ["frameHost"] = host, ["isMainFrame"] = isMain,
            });
            var pending = new PendingBlock(handler, answerDefault);
            if (view?.OnJsDialog is null)
            {
                pending.Default();
                return;
            }
            view.PendingHandlers.Add(pending);
            _ = AnswerPanelAsync(view, dialog, pending, answer);
        }
        catch (Exception e)
        {
            HarnessLog.Add("?", "imp-exception", new JsonObject { ["where"] = kind, ["error"] = e.Message });
            answerDefault(handler);
        }
    }

    [UnmanagedCallersOnly]
    private static nint CreateWebView(nint self, nint cmd, nint webView, nint configuration, nint action, nint features)
    {
        try
        {
            var opener = HarnessWebView.Find(webView);
            var url = UrlOf(ObjC.Send(action, "request"));
            if (opener is null || !opener.AllowPopups)
            {
                HarnessLog.Add(Label(webView), "createWebView", new JsonObject { ["url"] = url, ["created"] = false });
                return 0;
            }
            var popup = HarnessWebView.CreatePopup(configuration, opener);
            opener.Popups.Add(popup);
            HarnessLog.Add(opener.Label, "createWebView", new JsonObject { ["url"] = url, ["created"] = true, ["popup"] = popup.Label });
            return popup.Handle; // +0 for WebKit (not an alloc/new/copy method); we keep our own reference
        }
        catch (Exception e)
        {
            HarnessLog.Add("?", "imp-exception", new JsonObject { ["where"] = nameof(CreateWebView), ["error"] = e.ToString() });
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static void DidClose(nint self, nint cmd, nint webView) => Guard(nameof(DidClose), () =>
    {
        var view = HarnessWebView.Find(webView);
        HarnessLog.Add(Label(webView), "webViewDidClose");
        // not inside WebKit's own callback: tear down on the next loop turn
        if (view is not null) Main.Post(_ => view.Dispose(), null);
    });

    [UnmanagedCallersOnly]
    private static void RunOpenPanel(nint self, nint cmd, nint webView, nint parameters, nint frame, nint handler)
    {
        try
        {
            var view = HarnessWebView.Find(webView);
            var request = new OpenPanelRequest(ObjC.SendBool(parameters, "allowsMultipleSelection"), ObjC.SendBool(parameters, "allowsDirectories"));
            HarnessLog.Add(Label(webView), "runOpenPanel", new JsonObject
            {
                ["allowsMultipleSelection"] = request.AllowsMultiple, ["allowsDirectories"] = request.AllowsDirectories,
            });
            var pending = new PendingBlock(handler, b => InvokeObject(b, 0));
            if (view?.OnOpenPanel is null)
            {
                pending.Default();
                return;
            }
            view.PendingHandlers.Add(pending);
            _ = AnswerOpenPanelAsync(view, request, pending);
        }
        catch (Exception e)
        {
            HarnessLog.Add("?", "imp-exception", new JsonObject { ["where"] = nameof(RunOpenPanel), ["error"] = e.Message });
            InvokeObject(handler, 0);
        }
    }
}
