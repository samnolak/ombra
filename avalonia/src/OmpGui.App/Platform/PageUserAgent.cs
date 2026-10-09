using Avalonia.Controls;
using Avalonia.Platform;

namespace OmpGui.App.Platform;

/// <summary>
/// The user agent a preview page sees, for the device chosen in the preview's toolbar (a phone's, a tablet's): set on
/// the native web view through its platform handle. macOS: <c>-[WKWebView setCustomUserAgent:]</c> (nil restores the
/// engine's own, Safari's identity included). Other engines: not yet; there the device sets the size only.
/// </summary>
internal static class PageUserAgent
{
    /// <summary>Whether this platform's engine takes another user agent (the device menu says so when it does not).</summary>
    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>Sets <paramref name="userAgent"/> (null: the engine's own). True when it changed (the page loaded with the old one). Main thread.</summary>
    public static bool Set(IPlatformHandle? handle, string? userAgent)
    {
        if (!OperatingSystem.IsMacOS() || handle is not IAppleWKWebViewPlatformHandle { WKWebView: not 0 } apple) return false;
        var view = apple.WKWebView;
        var now = Mac.MacObjC.ToStr(Mac.MacObjC.Send(view, "customUserAgent"));
        if ((string.IsNullOrEmpty(now) ? null : now) == userAgent) return false;
        var pool = Mac.MacObjC.Send(Mac.MacObjC.Send(Mac.MacObjC.Cls("NSAutoreleasePool"), "alloc"), "init");
        try { Mac.MacObjC.SendVoid(view, "setCustomUserAgent:", userAgent is null ? 0 : Mac.MacObjC.Str(userAgent)); }
        finally { Mac.MacObjC.SendVoid(pool, "drain"); }
        return true;
    }
}
