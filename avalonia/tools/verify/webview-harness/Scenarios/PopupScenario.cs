using System.Text.Json.Nodes;
using OmpGui.WebViewHarness.Native;

namespace OmpGui.WebViewHarness.Scenarios;

/// <summary>OAuth-style popup: window.open → a WKWebView built from WebKit's configuration → opener postMessage → window.close.</summary>
internal sealed class PopupScenario : IScenario
{
    public string Name => "popup";

    public string Description => "window.open + window.opener.postMessage + window.close through createWebViewWithConfiguration/webViewDidClose";

    public async Task RunAsync(HarnessContext ctx)
    {
        // Today's app: no UI delegate → window.open returns null
        var baseline = ctx.CreateWebView(HookOptions.None);
        await baseline.LoadAsync(ctx.Server.Url("/popup/opener"));
        await baseline.EvalAsync("openPopup()");
        var baseEvents = await baseline.EvalAsync("window.__events");
        ctx.Check("baseline (no UI delegate): window.open returns null", baseEvents?[0]?["returnedWindow"]?.GetValue<bool>() == false, baseEvents);

        var view = ctx.CreateWebView(HookOptions.Reference);
        await view.LoadAsync(ctx.Server.Url("/popup/opener"));
        await view.EvalAsync("openPopup()");
        var closed = await view.WaitForJsAsync("window.__closed && window.__messages.length > 0", TimeSpan.FromSeconds(10));
        var messages = await view.EvalAsync("window.__messages");
        var events = await view.EvalAsync("window.__events");
        var log = HarnessLog.Snapshot();
        ctx.Check("createWebViewWithConfiguration returned a popup",
            log.Any(e => (string?)e["event"] == "createWebView" && e["created"]?.GetValue<bool>() == true), events);
        ctx.Check("popup loaded its page", log.Any(e => (string?)e["event"] == "didFinishNavigation" && ((string?)e["view"])?.StartsWith("popup") == true
            && ((string?)e["url"])?.EndsWith("/popup/child") == true));
        var first = messages?[0];
        ctx.Check("window.opener survived: the opener got the code by postMessage",
            first?["data"]?["code"]?.GetValue<string>() == "oauth-code-123" && first?["data"]?["hasOpener"]?.GetValue<bool>() == true
            && first?["origin"]?.GetValue<string>() == ctx.Server.BaseUrl.TrimEnd('/'), messages);
        ctx.Check("window.name passed to the popup", first?["data"]?["name"]?.GetValue<string>() == "oauth", first);
        ctx.Check("window.close → webViewDidClose, opener sees popup.closed", closed && log.Any(e => (string?)e["event"] == "webViewDidClose"), events);
        var gone = await HarnessWebView.WaitForAsync(() => view.Popups.Count == 0, TimeSpan.FromSeconds(3));
        ctx.Check("popup web view torn down", gone, view.Popups.Count);
    }
}
