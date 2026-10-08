using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform.Mac;

/// <summary>
/// A pop-up window a page opened: a raw WKWebView created with exactly the configuration WebKit passed to
/// <c>createWebViewWithConfiguration</c> (WebKit refuses any other; this keeps <c>window.opener</c>, the cookies and the
/// session). Avalonia's NativeWebView cannot take a configuration, so the app hosts <see cref="NativeHandle"/> itself
/// (an <c>NSView</c>, in a NativeControlHost) as a tab. The view has a navigation delegate of our own
/// (<c>OmpPopupNavigationDelegate</c>: finish and policy) plus everything <see cref="WebKitHooks.Attach"/> adds, so
/// sign-in, dialogs, downloads and pop-ups from pop-ups work as in the opener. Main thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class PopupWebViewHost : BrowserPopup
{
    private const long PolicyCancel = 0, PolicyAllow = 1;

    private static nint NavigationDelegateClass { get; } = MacObjC.DefineClass("OmpPopupNavigationDelegate", "WKNavigationDelegate",
        ("webView:didFinishNavigation:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidFinishNavigation, "v@:@@"),
        ("webView:decidePolicyForNavigationAction:decisionHandler:",
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&DecidePolicyForAction, "v@:@@@?"));

    private nint _webView;
    private nint _navigationDelegate;
    private IDisposable? _hooks;

    private PopupWebViewHost(nint webView, nint navigationDelegate)
    {
        _webView = webView;
        _navigationDelegate = navigationDelegate;
    }

    public override nint NativeHandle => _webView;
    public override string HandleDescriptor => "NSView";
    public override bool IsClosed => _webView == 0;

    public override Uri? Url => _webView == 0 ? null : MacObjC.ToUri(MacObjC.Send(_webView, "URL"));
    public override string Title => _webView == 0 ? "" : MacObjC.ToStr(MacObjC.Send(_webView, "title")) ?? "";
    public override bool CanGoBack => _webView != 0 && MacObjC.SendBool(_webView, "canGoBack");
    public override bool CanGoForward => _webView != 0 && MacObjC.SendBool(_webView, "canGoForward");

    /// <summary>
    /// The pop-up for <paramref name="configuration"/>, shown by the opener's app (<see cref="BrowserPageHooks.OpenPopup"/>);
    /// null when the app refuses it or the hooks cannot be attached (nothing is left behind).
    /// </summary>
    internal static PopupWebViewHost? Create(nint configuration, WebKitPage opener, Uri? requestedUrl)
    {
        var webView = ((delegate* unmanaged<nint, nint, MacRect, nint, nint>)MacObjC.MsgSend)(
            MacObjC.Send(MacObjC.Cls("WKWebView"), "alloc"), MacObjC.Sel("initWithFrame:configuration:"), default, configuration);
        if (webView == 0) return null;
        var navigationDelegate = MacObjC.Send(MacObjC.Send(NavigationDelegateClass, "alloc"), "init");
        MacObjC.SendVoid(webView, "setNavigationDelegate:", navigationDelegate); // weak: we keep the +1
        var popup = new PopupWebViewHost(webView, navigationDelegate) { RequestedUrl = requestedUrl };
        BrowserPageHooks? hooks = null;
        try
        {
            hooks = opener.Hooks.OpenPopup?.Invoke(popup);
        }
        catch (Exception)
        {
            hooks = null;
        }
        if (hooks is null)
        {
            popup.Close();
            return null;
        }
        popup._hooks = WebKitHooks.Attach(webView, hooks, out _);
        if (popup._hooks is null)
        {
            hooks.RaiseClosed();
            popup.Close();
            return null;
        }
        // The opener is hidden behind the pop-up's tab: its timers and messages keep running (OAuth result by postMessage)
        opener.KeepRunningWhenHidden();
        return popup;
    }

    public override void Navigate(Uri url)
    {
        if (_webView == 0) return;
        MacObjC.Guard(() => MacObjC.Send(_webView, "loadRequest:", MacObjC.Send(MacObjC.Cls("NSURLRequest"), "requestWithURL:", MacObjC.Url(url))));
    }

    public override void Reload()
    {
        if (_webView != 0) MacObjC.Send(_webView, "reload");
    }

    public override void GoBack()
    {
        if (_webView != 0) MacObjC.Send(_webView, "goBack");
    }

    public override void GoForward()
    {
        if (_webView != 0) MacObjC.Send(_webView, "goForward");
    }

    public override Task<string?> RunScriptAsync(string script)
    {
        if (_webView == 0) return Task.FromException<string?>(new InvalidOperationException("The pop-up is closed."));
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        MacObjC.Guard(() =>
        {
            var block = MacBlocks.Create((nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnScriptResult, tcs, "v24@?0@8@\"NSError\"16");
            try
            {
                MacObjC.SendVoid(_webView, "evaluateJavaScript:completionHandler:", MacObjC.Str(script), block);
            }
            finally
            {
                MacBlocks.Release(block);
            }
        });
        return tcs.Task;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnScriptResult(nint block, nint result, nint error) =>
        MacObjC.Guard(() =>
        {
            if (MacBlocks.TakeState<TaskCompletionSource<string?>>(block) is not { } tcs) return;
            if (error != 0)
            {
                var (domain, code, message) = MacObjC.Error(error);
                var info = MacObjC.Send(error, "userInfo");
                var js = info == 0 ? null : MacObjC.ToStr(MacObjC.Send(info, "objectForKey:", MacObjC.Str("WKJavaScriptExceptionMessage")));
                tcs.TrySetException(new InvalidOperationException(js ?? $"{message} ({domain} {code})"));
                return;
            }
            if (result == 0 || MacObjC.IsKindOf(result, "NSNull")) tcs.TrySetResult(null);
            else if (MacObjC.IsKindOf(result, "NSString")) tcs.TrySetResult(MacObjC.ToStr(result));
            else tcs.TrySetResult(Json(result));
        });

    /// <summary>A script's non-string result as JSON (numbers, booleans, arrays, objects), like Avalonia's InvokeScript.</summary>
    private static string? Json(nint value)
    {
        const nuint FragmentsAllowed = 4; // NSJSONWritingFragmentsAllowed
        var data = MacObjC.SendNUInt(MacObjC.Cls("NSJSONSerialization"), "dataWithJSONObject:options:error:", value, FragmentsAllowed, 0);
        if (data == 0) return MacObjC.ToStr(MacObjC.Send(value, "description"));
        var length = (int)MacObjC.SendNUInt(data, "length");
        var bytes = MacObjC.Send(data, "bytes");
        return bytes == 0 ? "" : System.Text.Encoding.UTF8.GetString((byte*)bytes, length);
    }

    /// <summary>Tears the pop-up down: pending questions get their default answers, the view leaves its host and is released.</summary>
    public override void Close()
    {
        var webView = _webView;
        if (webView == 0) return;
        _webView = 0;
        _hooks?.Dispose();
        _hooks = null;
        var navigationDelegate = _navigationDelegate;
        _navigationDelegate = 0;
        // Not inside a WebKit callback: the view may be the one calling us right now
        MacObjC.Post(() =>
        {
            MacObjC.SendVoid(webView, "stopLoading");
            MacObjC.SendVoid(webView, "setNavigationDelegate:", 0);
            MacObjC.SendVoid(webView, "removeFromSuperview");
            MacObjC.Release(webView);
            MacObjC.Release(navigationDelegate);
        });
    }

    // ── our navigation delegate (the extras come from WebKitHooks.Attach, like on Avalonia's) ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidFinishNavigation(nint self, nint cmd, nint webView, nint navigation) =>
        MacObjC.Guard(() =>
        {
            if (WebKitPage.Find(webView) is { } page) page.Hooks.RaiseFinished(page.Url);
        });

    /// <summary>The pane's rule for the page's own navigations: http, https, about, blob and data only (no file: or custom schemes).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DecidePolicyForAction(nint self, nint cmd, nint webView, nint action, nint handler)
    {
        var policy = PolicyCancel;
        MacObjC.Guard(() =>
        {
            var url = MacObjC.ToUri(MacObjC.Send(MacObjC.Send(action, "request"), "URL"));
            if (url is { Scheme: "http" or "https" or "about" or "blob" or "data" }) policy = PolicyAllow;
        });
        MacBlocks.InvokeLong(handler, policy);
    }
}

/// <summary>CGRect (the pop-up starts at zero size; its host sizes it).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MacRect(double x, double y, double width, double height)
{
    public double X = x;
    public double Y = y;
    public double Width = width;
    public double Height = height;
}
