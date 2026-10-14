using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Platform.Mac;

/// <summary>
/// Puts the window's traffic lights in the middle of a title band taller than the system's title bar (the ChatGPT app's:
/// centred in its 44 pt band), the way Electron's <c>trafficLightPosition</c> does: the buttons' container is made as tall
/// as the band, and the buttons are centred in it from <paramref name="left"/> on, at their own spacing. AppKit lays the
/// title bar out again on a resize or a full-screen change, so the window calls this after each.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacTrafficLights
{
    /// <param name="window">The NSWindow (or a view in it).</param>
    /// <param name="band">The band's height, in points.</param>
    /// <param name="left">The close button's left edge, in points.</param>
    public static void Place(nint window, double band, double left)
    {
        if (window == 0) return;
        if (!MacObjC.RespondsTo(window, "standardWindowButton:"))
        {
            if (!MacObjC.RespondsTo(window, "window")) return;
            window = MacObjC.Send(window, "window");
            if (!MacObjC.RespondsTo(window, "standardWindowButton:")) return;
        }
        var close = MacObjC.SendIndex(window, "standardWindowButton:", 0);
        var mini = MacObjC.SendIndex(window, "standardWindowButton:", 1);
        var zoom = MacObjC.SendIndex(window, "standardWindowButton:", 2);
        if (close == 0 || mini == 0 || zoom == 0) return;
        var container = MacObjC.Send(MacObjC.Send(close, "superview"), "superview");
        if (container == 0) return;

        var buttonHeight = Frame(close).Height;
        var spacing = Frame(mini).X - Frame(close).X;
        var c = Frame(container);
        var w = Frame(window);
        // The container hangs from the window's top edge (AppKit's frames start at the bottom left)
        SetFrame(container, new CGRect(c.X, w.Height - band, c.Width, band));
        var i = 0;
        foreach (var button in new[] { close, mini, zoom })
        {
            var f = Frame(button);
            SetFrame(button, new CGRect(left + i++ * spacing, Math.Round((band - buttonHeight) / 2), f.Width, f.Height));
        }
    }

    /// <summary>A view's or a window's frame (structs come back in registers on arm64, through memory on x86-64).</summary>
    private static CGRect Frame(nint obj)
    {
        CGRect frame;
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            ((delegate* unmanaged<CGRect*, nint, nint, void>)Stret)(&frame, obj, MacObjC.Sel("frame"));
        else frame = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(obj, MacObjC.Sel("frame"));
        return frame;
    }

    private static void SetFrame(nint view, CGRect frame) =>
        ((delegate* unmanaged<nint, nint, CGRect, void>)MacObjC.MsgSend)(view, MacObjC.Sel("setFrame:"), frame);

    private static readonly nint Stret = RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? NativeLibrary.GetExport(NativeLibrary.Load(MacObjC.LibObjC), "objc_msgSend_stret")
        : 0;
}
