using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ombra.App.Platform.Mac;

namespace Ombra.App.Nightly;

/// <summary>
/// Geometry between an Avalonia window's client area (DIP = points, top-left origin) and AppKit's window and screen
/// coordinates (bottom-left origin), for the pane's position on screen and the scroll monitor's hit test.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class NightlyCocoa
{
    /// <summary>The NSWindow of a top level's platform handle (Avalonia gives the window or its view).</summary>
    public static nint WindowOf(nint handle) =>
        MacObjC.RespondsTo(handle, "contentView") ? handle : MacObjC.RespondsTo(handle, "window") ? MacObjC.Send(handle, "window") : 0;

    /// <summary>
    /// The bottom-left corner on screen (Cocoa coordinates) of a rectangle of the window's client area, given by its
    /// top-left corner and size; null when the window has no content view.
    /// </summary>
    public static (double X, double Y)? ScreenBottomLeft(nint nsWindow, double x, double top, double width, double height)
    {
        var content = nsWindow == 0 ? 0 : MacObjC.Send(nsWindow, "contentView");
        if (content == 0) return null;
        var y = MacObjC.SendBool(content, "isFlipped") ? top + height : Bounds(content).Height - top - height;
        var inWindow = ((delegate* unmanaged<nint, nint, MacRuntime.CGRect, nint, MacRuntime.CGRect>)MacObjC.MsgSend)(
            content, MacObjC.Sel("convertRect:toView:"), new MacRuntime.CGRect(x, y, width, height), 0);
        var onScreen = ((delegate* unmanaged<nint, nint, MacRuntime.CGRect, MacRuntime.CGRect>)MacObjC.MsgSend)(
            nsWindow, MacObjC.Sel("convertRectToScreen:"), inWindow);
        return (onScreen.X, onScreen.Y);
    }

    /// <summary>An event's <c>locationInWindow</c> as a client-area point (top-left origin); null without a content view.</summary>
    public static (double X, double Y)? ClientPoint(nint nsWindow, double x, double y)
    {
        var content = nsWindow == 0 ? 0 : MacObjC.Send(nsWindow, "contentView");
        if (content == 0) return null;
        var inView = ((delegate* unmanaged<nint, nint, MacRuntime.CGPoint, nint, MacRuntime.CGPoint>)MacObjC.MsgSend)(
            content, MacObjC.Sel("convertPoint:fromView:"), new MacRuntime.CGPoint(x, y), 0);
        return (inView.X, MacObjC.SendBool(content, "isFlipped") ? inView.Y : Bounds(content).Height - inView.Y);
    }

    /// <summary>A key <c>NSEvent</c> as AppKit delivered it (<see cref="CurrentKeyEvent"/>).</summary>
    /// <param name="Identity">The event object and its timestamp: the same native event for every callback it causes.</param>
    /// <param name="Type">NSEventType: 10 key down, 11 key up, 12 flags changed.</param>
    public readonly record struct KeyEvent((nint Event, double Timestamp) Identity, ulong Type, int KeyCode, string Characters,
        string Unmodified, ulong Modifiers, bool IsRepeat);

    /// <summary>
    /// The key event AppKit is handling now (<c>-[NSApp currentEvent]</c>), when it is one: Avalonia's key and text
    /// callbacks run inside <c>keyDown:</c>, and so do the input method's answers (text, composition, commands).
    /// </summary>
    public static KeyEvent? CurrentKeyEvent()
    {
        var app = MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication");
        return app == 0 ? null : KeyEventOf(MacObjC.Send(app, "currentEvent"));
    }

    /// <summary><paramref name="ev"/> (an <c>NSEvent</c>) when it is a key event.</summary>
    public static KeyEvent? KeyEventOf(nint ev)
    {
        if (ev == 0) return null;
        var type = (ulong)MacObjC.SendNUInt(ev, "type");
        if (type is not (10 or 11 or 12)) return null;
        var timestamp = ((delegate* unmanaged<nint, nint, double>)MacObjC.MsgSend)(ev, MacObjC.Sel("timestamp"));
        var keyCode = (int)((delegate* unmanaged<nint, nint, ushort>)MacObjC.MsgSend)(ev, MacObjC.Sel("keyCode"));
        var modifiers = (ulong)MacObjC.SendNUInt(ev, "modifierFlags");
        if (type == 12) return new KeyEvent((ev, timestamp), type, keyCode, "", "", modifiers, false);
        return new KeyEvent((ev, timestamp), type, keyCode, MacObjC.ToStr(MacObjC.Send(ev, "characters")) ?? "",
            MacObjC.ToStr(MacObjC.Send(ev, "charactersIgnoringModifiers")) ?? "", modifiers, MacObjC.SendBool(ev, "isARepeat"));
    }

    private static MacRuntime.CGRect Bounds(nint view) =>
        ((delegate* unmanaged<nint, nint, MacRuntime.CGRect>)MacObjC.MsgSend)(view, MacObjC.Sel("bounds"));
}
