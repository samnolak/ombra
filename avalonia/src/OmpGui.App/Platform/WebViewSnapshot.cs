using Avalonia.Platform;
using OmpGui.ClientCore;

namespace OmpGui.App.Platform;

/// <summary>
/// Screenshots of the preview's web view for the agent's browser, through each engine's own snapshot call: WKWebView
/// <c>takeSnapshotWithConfiguration:completionHandler:</c> on macOS, WebView2 <c>CapturePreview</c> on Windows,
/// WebKitGTK <c>webkit_web_view_get_snapshot</c> on Linux. They draw the page itself, not the screen, so no screen
/// recording permission is involved. Avalonia's NativeWebView has no capture API; its platform handle
/// (<c>NativeWebView.TryGetPlatformHandle()</c>) gives the native web view.
/// </summary>
/// <remarks>
/// Call on the UI thread (the engines want their own thread: the main thread on macOS and Windows, GLib's on Linux,
/// where the call is moved there). Every failure is an <see cref="AgentBrowserException"/> with a message for the agent;
/// nothing escapes into the native callbacks. Limits: the visible part of the page only (no full page), the page must be
/// shown (a hidden or zero-sized view has nothing to draw).
/// </remarks>
internal static class WebViewSnapshot
{
    /// <summary>How long an engine may take before the agent is told it did not answer.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<AgentScreenshot> CaptureAsync(IPlatformHandle? handle, CancellationToken ct)
    {
        Task<AgentScreenshot> capture;
        try
        {
            capture = Start(handle);
        }
        catch (Exception e) when (e is not AgentBrowserException)
        {
            throw Failed(e);
        }
        try
        {
            return await capture.WaitAsync(Timeout, ct);
        }
        catch (TimeoutException)
        {
            throw new AgentBrowserException("timeout", $"The preview did not finish the screenshot within {Timeout.TotalSeconds:0} s.");
        }
        catch (Exception e) when (e is not AgentBrowserException and not OperationCanceledException)
        {
            throw Failed(e);
        }
    }

    private static Task<AgentScreenshot> Start(IPlatformHandle? handle)
    {
        if (handle is null)
            throw new AgentBrowserException("no_page", "The preview's web view is not ready yet: wait for the page to load, then try again.");
        if (OperatingSystem.IsMacOS() && handle is IAppleWKWebViewPlatformHandle apple)
            return MacWebViewSnapshot.Capture(apple.WKWebView);
        if (OperatingSystem.IsWindows() && handle is IWindowsWebView2PlatformHandle webView2)
            return WebView2Snapshot.Capture(webView2.CoreWebView2);
        if (OperatingSystem.IsLinux() && handle is IGtkWebViewPlatformHandle gtk)
            return GtkWebViewSnapshot.CaptureAsync(gtk.WebKitWebView);
        throw new AgentBrowserException("not_supported",
            "Screenshots are not available with this web engine. Read the page instead: tab.observe(), tab.extract() or tab.evaluate().");
    }

    internal static AgentBrowserException Failed(Exception e) =>
        new("screenshot_failed", "The preview could not take the screenshot: " + e.Message);

    /// <summary>The engine's PNG as a screenshot; a clear error when it is not a PNG.</summary>
    internal static AgentScreenshot FromPng(byte[] png) =>
        AgentScreenshot.FromPng(png) ?? throw new AgentBrowserException("screenshot_failed", "The web engine returned an image that is not a PNG.");
}
