using Avalonia.Platform;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform;

/// <summary>
/// The engine hooks the preview installs on each native web view when its adapter is created
/// (<c>NativeWebView.AdapterCreated</c>), through the platform handle, for the tab's <see cref="BrowserPageHooks"/>.
/// WKWebView (<see cref="Mac.WebKitHooks"/>): sign-in, alert/confirm/prompt, pop-ups, file inputs, downloads,
/// camera/microphone, loading failures and crashes. WebView2 (<see cref="Windows.WebView2Hooks"/>) and WebKitGTK
/// (<see cref="Linux.WebKitGtkHooks"/>): sign-in only so far. Disposing the result (on <c>AdapterDestroyed</c>) removes
/// them and answers what is still pending with the engine's default.
/// </summary>
internal static class BrowserHooks
{
    /// <summary>The hooks for <paramref name="handle"/>; null where the engine is not supported or refused them (the page keeps the engine's default).</summary>
    public static async Task<IDisposable?> AttachAsync(IPlatformHandle? handle, BrowserPageHooks page)
    {
        try
        {
            if (OperatingSystem.IsMacOS() && handle is IAppleWKWebViewPlatformHandle apple)
                return Mac.WebKitHooks.Attach(apple.WKWebView, page, out _);
            if (OperatingSystem.IsWindows() && handle is IWindowsWebView2PlatformHandle webView2)
                return Windows.WebView2Hooks.Attach(webView2.CoreWebView2, page, out _);
            if (OperatingSystem.IsLinux() && handle is IGtkWebViewPlatformHandle gtk)
                return await Linux.WebKitGtkHooks.AttachAsync(gtk.WebKitWebView, page);
        }
        catch (Exception)
        {
            // A missing symbol or an engine that refuses: the page works as before, without the cards
        }
        return null;
    }

    /// <summary>Removes hooks made by <see cref="AttachAsync"/> (also when it has not finished yet).</summary>
    public static async Task DetachAsync(Task<IDisposable?> attach)
    {
        try
        {
            (await attach)?.Dispose();
        }
        catch (Exception)
        {
            // The engine is already gone
        }
    }

    /// <summary>
    /// The text WebKit appends to its user agent on macOS (<c>applicationNameForUserAgent</c>): Safari's
    /// "Version/… Safari/605.1.15", so the pane's user agent is Safari's. Null elsewhere (WebView2 and WebKitGTK keep
    /// their own, already a real browser's).
    /// </summary>
    public static string? ApplicationNameForUserAgent => OperatingSystem.IsMacOS() ? Mac.SafariIdentity.ApplicationNameForUserAgent : null;
}
