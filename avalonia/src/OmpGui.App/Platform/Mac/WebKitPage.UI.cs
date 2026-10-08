using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// Our <c>WKUIDelegate</c> (class <c>OmpWKUIDelegate</c>, one instance per web view). Without one WebKit answers alert
/// OK, confirm/prompt Cancel, refuses <c>window.open</c> and file inputs, and ignores <c>window.close</c>.
/// </summary>
internal sealed partial class WebKitPage
{
    private const long MediaCamera = 0, MediaMicrophone = 1; // WKMediaCaptureType; 2 = both
    private const long PermissionGrant = 1, PermissionDeny = 2; // WKPermissionDecision; 0 = WebKit's own prompt

    private static unsafe nint UiDelegateClass { get; } = MacObjC.DefineClass("OmpWKUIDelegate", "WKUIDelegate",
        ("webView:runJavaScriptAlertPanelWithMessage:initiatedByFrame:completionHandler:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&RunAlert, "v@:@@@@?"),
        ("webView:runJavaScriptConfirmPanelWithMessage:initiatedByFrame:completionHandler:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&RunConfirm, "v@:@@@@?"),
        ("webView:runJavaScriptTextInputPanelWithPrompt:defaultText:initiatedByFrame:completionHandler:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, void>)&RunPrompt, "v@:@@@@@?"),
        // WKUIDelegatePrivate (WebKit checks respondsToSelector:): "Leave page?" for beforeunload handlers
        ("_webView:runBeforeUnloadConfirmPanelWithMessage:initiatedByFrame:completionHandler:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&RunBeforeUnload, "v@:@@@@?"),
        ("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint>)&CreateWebView, "@@:@@@@"),
        ("webViewDidClose:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DidClose, "v@:@"),
        ("webView:runOpenPanelWithParameters:initiatedByFrame:completionHandler:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&RunOpenPanel, "v@:@@@@?"),
        ("webView:requestMediaCapturePermissionForOrigin:initiatedByFrame:type:decisionHandler:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, long, nint, void>)&RequestMediaCapturePermission, "v@:@@@q@?"));

    /// <summary>The UI delegate's IMP for a selector (the harness calls it directly with a synthetic origin).</summary>
    internal static nint UiImplementation(string selector) => MacObjC.ImplementationOf(UiDelegateClass, selector);

    /// <summary>"https://example.com:8443" of a <c>WKSecurityOrigin*</c> (default ports left out).</summary>
    internal static string? OriginText(nint origin)
    {
        if (origin == 0) return null;
        var protocol = MacObjC.ToStr(MacObjC.Send(origin, "protocol"));
        var host = MacObjC.ToStr(MacObjC.Send(origin, "host"));
        if (string.IsNullOrEmpty(protocol) || string.IsNullOrEmpty(host)) return null;
        var port = MacObjC.SendLong(origin, "port");
        var defaultPort = protocol == "http" ? 80 : protocol == "https" ? 443 : 0;
        return port <= 0 || port == defaultPort ? $"{protocol}://{host}" : $"{protocol}://{host}:{port}";
    }

    /// <summary>The origin of the frame that asks, as an address for the card's title; the page's address when unknown.</summary>
    private Uri? FrameUrl(nint frame)
    {
        var text = frame == 0 ? null : OriginText(MacObjC.Send(frame, "securityOrigin"));
        return text is not null && Uri.TryCreate(text, UriKind.Absolute, out var u) ? u : Url;
    }

    // ── alert / confirm / prompt / beforeunload: the page's scripts wait until the block is called ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunAlert(nint self, nint cmd, nint webView, nint message, nint frame, nint handler) =>
        Ask(webView, handler, MacBlocks.InvokeVoid, (p, broker, ct) =>
            broker.AlertAsync(new AlertRequest(MacObjC.ToStr(message) ?? "") { PageUrl = p.FrameUrl(frame), CausedByAgent = p.AgentActive() }, ct)
                .ContinueWith(static _ => true, TaskScheduler.Default),
            static (b, _) => MacBlocks.InvokeVoid(b));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunConfirm(nint self, nint cmd, nint webView, nint message, nint frame, nint handler) =>
        Ask(webView, handler, static b => MacBlocks.InvokeBool(b, false), (p, broker, ct) =>
            broker.ConfirmAsync(new ConfirmRequest(MacObjC.ToStr(message) ?? "") { PageUrl = p.FrameUrl(frame), CausedByAgent = p.AgentActive() }, ct),
            static (b, ok) => MacBlocks.InvokeBool(b, ok));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunPrompt(nint self, nint cmd, nint webView, nint prompt, nint defaultText, nint frame, nint handler) =>
        Ask(webView, handler, static b => MacBlocks.InvokeObject(b, 0), (p, broker, ct) =>
            broker.PromptAsync(new PromptRequest(MacObjC.ToStr(prompt) ?? "", MacObjC.ToStr(defaultText)) { PageUrl = p.FrameUrl(frame), CausedByAgent = p.AgentActive() }, ct),
            static (b, text) => MacBlocks.InvokeObject(b, text is null ? 0 : MacObjC.Str(text)));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunBeforeUnload(nint self, nint cmd, nint webView, nint message, nint frame, nint handler) =>
        Ask(webView, handler, static b => MacBlocks.InvokeBool(b, true), (p, broker, ct) =>
            broker.BeforeUnloadAsync(new BeforeUnloadRequest(MacObjC.ToStr(message)) { PageUrl = p.FrameUrl(frame), CausedByAgent = p.AgentActive() }, ct),
            static (b, leave) => MacBlocks.InvokeBool(b, leave));

    /// <summary>
    /// One question to the broker: the handler is copied and answered with <paramref name="answer"/> once the broker
    /// answers; WebKit's no-delegate default (<paramref name="answerDefault"/>) when there is no page or broker, on
    /// failure, and when the page goes first.
    /// </summary>
    private static void Ask<T>(nint webView, nint handler, Action<nint> answerDefault,
        Func<WebKitPage, DialogBroker, CancellationToken, Task<T>> question, Action<nint, T> answer)
    {
        MacPendingBlock? pending = null;
        try
        {
            if (Find(webView) is not { } page || page.Broker() is not { } broker)
            {
                answerDefault(handler);
                return;
            }
            pending = new MacPendingBlock(handler, answerDefault);
            page.Answer(question(page, broker, CancellationToken.None), pending, answer);
        }
        catch (Exception)
        {
            // Nothing may unwind into WebKit; the handler is answered once
            if (pending is null) Try(() => answerDefault(handler));
            else pending.Default();
        }
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // nothing more to do
        }
    }

    // ── pop-ups ──

    /// <summary>
    /// <c>window.open</c> / <c>target=_blank</c>: a new WKWebView built from exactly WebKit's configuration (so
    /// <c>window.opener</c> and the session are kept), shown by the app as a tab. WebKit loads the request into it
    /// itself. 0 refuses (the page's <c>window.open</c> returns null).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint CreateWebView(nint self, nint cmd, nint webView, nint configuration, nint action, nint features)
    {
        nint created = 0;
        MacObjC.Guard(() =>
        {
            if (Find(webView) is not { Hooks.OpenPopup: not null } opener) return;
            var url = MacObjC.ToUri(MacObjC.Send(MacObjC.Send(action, "request"), "URL"));
            if (PopupWebViewHost.Create(configuration, opener, url) is { } popup) created = popup.NativeHandle;
        });
        return created; // +0 for WebKit (not an alloc/new/copy method); the host keeps its own reference
    }

    /// <summary><c>window.close()</c>: the app closes the pop-up's tab, on the next turn (never inside WebKit's callback).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidClose(nint self, nint cmd, nint webView) =>
        With(webView, p => MacObjC.Post(p.Hooks.RaiseClosed));

    // ── file input ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunOpenPanel(nint self, nint cmd, nint webView, nint parameters, nint frame, nint handler) =>
        Ask(webView, handler, static b => MacBlocks.InvokeObject(b, 0), (p, broker, ct) =>
        {
            var accept = new List<string>();
            // The accept= list is SPI; used when this WebKit has it
            if (MacObjC.RespondsTo(parameters, "_acceptedMIMETypes")) accept.AddRange(MacObjC.Strings(MacObjC.Send(parameters, "_acceptedMIMETypes")));
            if (MacObjC.RespondsTo(parameters, "_acceptedFileExtensions"))
                accept.AddRange(MacObjC.Strings(MacObjC.Send(parameters, "_acceptedFileExtensions")).Select(e => e.StartsWith('.') ? e : "." + e));
            var request = new FileChooserRequest(MacObjC.SendBool(parameters, "allowsMultipleSelection"), accept,
                MacObjC.RespondsTo(parameters, "allowsDirectories") && MacObjC.SendBool(parameters, "allowsDirectories"))
            {
                PageUrl = p.FrameUrl(frame),
                CausedByAgent = p.AgentActive(),
            };
            return broker.ChooseFilesAsync(request, ct);
        }, static (b, files) =>
        {
            if (files is not { Count: > 0 })
            {
                MacBlocks.InvokeObject(b, 0);
                return;
            }
            var array = MacObjC.Send(MacObjC.Cls("NSMutableArray"), "array");
            foreach (var f in files) MacObjC.SendVoid(array, "addObject:", MacObjC.FileUrl(f));
            MacBlocks.InvokeObject(b, array);
        });

    // ── camera and microphone (macOS 12+) ──

    /// <summary>
    /// A site asks for the camera and/or microphone: "Always allow on this site" answers are remembered
    /// (<see cref="BrowserPageHooks.SiteSettings"/>); otherwise the permission card asks. WebKit itself refuses before
    /// asking us when the app has no usage description for the device (the app ships the microphone one only).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RequestMediaCapturePermission(nint self, nint cmd, nint webView, nint origin, nint frame, long type, nint handler)
    {
        var kind = type switch
        {
            MediaCamera => PermissionKind.Camera,
            MediaMicrophone => PermissionKind.Microphone,
            _ => PermissionKind.CameraAndMicrophone,
        };
        string? site = null;
        try
        {
            site = OriginText(origin);
        }
        catch (Exception)
        {
            // unknown origin: denied below
        }
        if (site is null)
        {
            MacBlocks.InvokeLong(handler, PermissionDeny);
            return;
        }
        if (Find(webView)?.Hooks.SiteSettings?.IsAlwaysAllowed(site, kind) == true)
        {
            MacBlocks.InvokeLong(handler, PermissionGrant);
            return;
        }
        Ask(webView, handler, static b => MacBlocks.InvokeLong(b, PermissionDeny), (p, broker, ct) =>
            broker.PermissionAsync(new PermissionRequest(kind, site) { PageUrl = p.FrameUrl(frame) ?? p.Url, CausedByAgent = p.AgentActive() }, ct)
                .ContinueWith(t =>
                {
                    var answer = t.Result;
                    if (answer == PermissionAnswer.AlwaysForSite) p.Hooks.SiteSettings?.AlwaysAllow(site, kind);
                    return answer;
                }, TaskScheduler.Default),
            static (b, answer) => MacBlocks.InvokeLong(b, answer == PermissionAnswer.Deny ? PermissionDeny : PermissionGrant));
    }
}
