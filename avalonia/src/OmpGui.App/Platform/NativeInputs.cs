using Avalonia.Platform;
using OmpGui.ClientCore.Browser;

namespace OmpGui.App.Platform;

/// <summary>
/// User-equivalent input and the app's isolated script world for one native web view (BROWSER_PLAN B4), from its
/// platform handle: WKWebView on macOS (<see cref="Mac.NativeInput"/>, <see cref="Mac.MacIsolatedScripts"/>). WebView2
/// (CDP <c>Input.*</c>, <c>Page.createIsolatedWorld</c>) and WebKitGTK (<c>gtk_widget_event</c>, script worlds) are not
/// built yet: there both report why, and the agent's browser falls back to script events it marks as such.
/// </summary>
internal static class NativeInputs
{
    private const string NotOnWebView2 = "Native input is not built for WebView2 yet (CDP Input.*): the agent's clicks and typing are script events.";
    private const string NotOnGtk = "Native input is not built for WebKitGTK yet: the agent's clicks and typing are script events.";
    private const string NoWorldOnWebView2 = "The isolated script world is not built for WebView2 yet (Page.createIsolatedWorld).";
    private const string NoWorldOnGtk = "The isolated script world is not built for WebKitGTK yet (script worlds).";

    /// <summary>Input and isolated world for <paramref name="handle"/> (never null: an engine without them says why). UI thread.</summary>
    public static (INativeInput Input, IIsolatedScripts Isolated) For(IPlatformHandle? handle)
    {
        try
        {
            if (OperatingSystem.IsMacOS() && handle is IAppleWKWebViewPlatformHandle apple && apple.WKWebView != 0)
                return (new Mac.NativeInput(apple.WKWebView), new Mac.MacIsolatedScripts(apple.WKWebView));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return (new UnsupportedNativeInput("Native input could not start: " + e.Message), new UnsupportedIsolatedScripts(e.Message));
        }
        if (handle is IWindowsWebView2PlatformHandle) return (new UnsupportedNativeInput(NotOnWebView2), new UnsupportedIsolatedScripts(NoWorldOnWebView2));
        if (handle is IGtkWebViewPlatformHandle) return (new UnsupportedNativeInput(NotOnGtk), new UnsupportedIsolatedScripts(NoWorldOnGtk));
        const string None = "This web engine offers no native input.";
        return (new UnsupportedNativeInput(None), new UnsupportedIsolatedScripts(None));
    }
}
