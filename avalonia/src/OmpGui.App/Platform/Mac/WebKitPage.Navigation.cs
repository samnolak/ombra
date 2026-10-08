using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// Navigation-delegate selectors Avalonia 12.1's delegate lacks, added to its runtime class with <c>class_addMethod</c>
/// (then the delegate is assigned again: WebKit caches which selectors a delegate answers when it is set). Pop-ups get
/// the same IMPs on their own delegate class (<see cref="PopupWebViewHost"/>).
/// </summary>
internal sealed partial class WebKitPage
{
    private const long PolicyCancel = 0, PolicyAllow = 1, PolicyDownload = 2;

    /// <summary>Selector, IMP, type encoding of each navigation extra.</summary>
    private static unsafe (string Selector, nint Imp, string Types)[] NavigationExtras =>
    [
        ("webView:didStartProvisionalNavigation:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidStartProvisional, "v@:@@"),
        ("webView:didCommitNavigation:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidCommit, "v@:@@"),
        ("webView:didFailProvisionalNavigation:withError:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidFailProvisional, "v@:@@@"),
        ("webView:didFailNavigation:withError:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DidFail, "v@:@@@"),
        ("webViewWebContentProcessDidTerminate:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ProcessDidTerminate, "v@:@"),
        ("webView:decidePolicyForNavigationResponse:decisionHandler:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DecidePolicyForResponse, "v@:@@@?"),
        ("webView:decidePolicyForNavigationAction:preferences:decisionHandler:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, void>)&DecidePolicyForActionWithPreferences, "v@:@@@@?"),
        ("webView:navigationAction:didBecomeDownload:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&WebKitDownloads.DidBecomeDownload, "v@:@@@"),
        ("webView:navigationResponse:didBecomeDownload:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&WebKitDownloads.DidBecomeDownload, "v@:@@@"),
    ];

    /// <summary>
    /// Adds the extras to the class of <paramref name="webView"/>'s navigation delegate and assigns the delegate again.
    /// A selector the class already implements itself (a newer Avalonia) is left alone and reported.
    /// </summary>
    private static bool InstallNavigationExtras(nint webView, List<string> problems)
    {
        var navigationDelegate = MacObjC.Send(webView, "navigationDelegate");
        if (navigationDelegate == 0) return false;
        var cls = MacObjC.ClassOf(navigationDelegate);
        foreach (var (selector, imp, types) in NavigationExtras)
            if (!MacObjC.AddMethod(cls, selector, imp, types)) problems.Add($"{MacObjC.ClassName(cls)} already implements {selector}");
        MacObjC.SendVoid(webView, "setNavigationDelegate:", navigationDelegate);
        return true;
    }

    private static void With(nint webView, Action<WebKitPage> action)
    {
        if (Find(webView) is { } page) MacObjC.Guard(() => action(page));
    }

    // ── loading events ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidStartProvisional(nint self, nint cmd, nint webView, nint navigation) =>
        With(webView, p => p.Hooks.RaiseStarted(p.Url));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidCommit(nint self, nint cmd, nint webView, nint navigation) =>
        With(webView, p => p.Hooks.RaiseCommitted(p.Url));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidFailProvisional(nint self, nint cmd, nint webView, nint navigation, nint error) =>
        With(webView, p => p.Failed(error, provisional: true));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidFail(nint self, nint cmd, nint webView, nint navigation, nint error) =>
        With(webView, p => p.Failed(error, provisional: false));

    private void Failed(nint error, bool provisional)
    {
        var (domain, code, message) = MacObjC.Error(error);
        if (BrowserLoadFailure.IsIgnored(domain, code)) return;
        var info = error == 0 ? 0 : MacObjC.Send(error, "userInfo");
        var failing = info == 0 ? null : MacObjC.ToStr(MacObjC.Send(info, "objectForKey:", MacObjC.Str("NSErrorFailingURLStringKey")));
        var url = failing is not null && Uri.TryCreate(failing, UriKind.Absolute, out var u) ? u : Url;
        Hooks.RaiseFailed(new BrowserLoadFailure(url, domain, code, message, provisional));
    }

    /// <summary>
    /// The web content process ended. Implementing this selector stops WebKit's silent reload: the page stays blank,
    /// the app shows its crash page (Reload). Handlers still waiting are answered with their defaults; the broker's
    /// questions are cancelled (<see cref="BrowserPageHooks.RaiseTerminated"/>).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ProcessDidTerminate(nint self, nint cmd, nint webView) =>
        With(webView, p =>
        {
            p.AnswerPendingWithDefaults();
            p.Hooks.RaiseTerminated();
        });

    // ── policies ──

    /// <summary>
    /// Shows what WebKit can show; downloads the rest and every <c>Content-Disposition: attachment</c> (the rule
    /// WebKitGTK applies). Reports the response first (status, type, headers).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DecidePolicyForResponse(nint self, nint cmd, nint webView, nint navigationResponse, nint handler)
    {
        var policy = PolicyAllow;
        MacObjC.Guard(() =>
        {
            var response = MacObjC.Send(navigationResponse, "response");
            var canShow = MacObjC.SendBool(navigationResponse, "canShowMIMEType");
            var isMain = MacObjC.SendBool(navigationResponse, "isForMainFrame");
            var status = 0;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (MacObjC.IsKindOf(response, "NSHTTPURLResponse"))
            {
                status = (int)MacObjC.SendLong(response, "statusCode");
                var fields = MacObjC.Send(response, "allHeaderFields");
                foreach (var key in MacObjC.Strings(MacObjC.Send(fields, "allKeys")))
                    if (MacObjC.ToStr(MacObjC.Send(fields, "objectForKey:", MacObjC.Str(key))) is { } value) headers[key] = value;
            }
            var attachment = headers.TryGetValue("Content-Disposition", out var cd) && cd.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
            if (!canShow || attachment) policy = PolicyDownload;
            if (Find(webView) is { } page)
                page.Hooks.RaiseResponse(new BrowserResponse(MacObjC.ToUri(MacObjC.Send(response, "URL")), status,
                    MacObjC.ToStr(MacObjC.Send(response, "MIMEType")), headers, isMain, policy == PolicyDownload));
        });
        MacBlocks.InvokeLong(handler, policy);
    }

    /// <summary>State of a forwarded 4-argument policy call: WebKit's handler (copied) and its preferences (retained).</summary>
    private sealed class ForwardedPolicy(nint handler, nint preferences)
    {
        public readonly MacPendingBlock Handler = new(handler, b => MacBlocks.InvokeLongObject(b, PolicyCancel, 0));
        public readonly nint Preferences = MacObjC.Retain(preferences);
    }

    /// <summary>
    /// WebKit prefers this variant over the delegate's 3-argument one. An <c>&lt;a download&gt;</c> (http, https, blob,
    /// data) becomes a download at once: it never navigates the page, so the delegate (Avalonia's, which would report a
    /// navigation and show the file's address) is not asked. Everything else goes to the delegate's 3-argument method
    /// with our own block, so Avalonia's NavigationStarted / NewWindowRequested still fire and its answer counts.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void DecidePolicyForActionWithPreferences(nint self, nint cmd, nint webView, nint action, nint preferences, nint handler)
    {
        var shouldDownload = false;
        try
        {
            shouldDownload = MacObjC.RespondsTo(action, "shouldPerformDownload") && MacObjC.SendBool(action, "shouldPerformDownload")
                && MacObjC.ToUri(MacObjC.Send(MacObjC.Send(action, "request"), "URL")) is { Scheme: "http" or "https" or "blob" or "data" };
        }
        catch (Exception)
        {
            // treated as an ordinary navigation
        }
        if (shouldDownload)
        {
            MacBlocks.InvokeLongObject(handler, PolicyDownload, preferences);
            return;
        }
        ForwardedPolicy? state = null;
        try
        {
            state = new ForwardedPolicy(handler, preferences);
            var block = MacBlocks.Create((nint)(delegate* unmanaged[Cdecl]<nint, long, void>)&OnForwardedPolicy, state, "v16@?0q8");
            try
            {
                MacObjC.SendVoid(self, "webView:decidePolicyForNavigationAction:decisionHandler:", webView, action, block);
            }
            finally
            {
                MacBlocks.Release(block);
            }
        }
        catch (Exception)
        {
            if (state is null) MacBlocks.InvokeLongObject(handler, PolicyAllow, preferences);
            else
            {
                var prefs = state.Preferences;
                state.Handler.Complete(b => MacBlocks.InvokeLongObject(b, PolicyAllow, prefs));
                MacObjC.Release(prefs);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnForwardedPolicy(nint block, long policy)
    {
        if (MacBlocks.TakeState<ForwardedPolicy>(block) is not { } state) return;
        var prefs = state.Preferences;
        state.Handler.Complete(b =>
        {
            MacBlocks.InvokeLongObject(b, policy, prefs);
            MacObjC.Release(prefs);
        });
    }
}
