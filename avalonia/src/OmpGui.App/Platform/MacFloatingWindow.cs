using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Platform;

namespace OmpGui.App.Platform;

/// <summary>
/// Makes a window float like a desktop companion on macOS (the pet on the desktop): it shows on every Space and over
/// full-screen apps, stays where it is in Mission Control instead of being laid out as an app window, is skipped by
/// Cmd-` window cycling, and has no window shadow (the window is see-through: the shadow would outline its box).
/// Avalonia has no API for these, so they are set on the <c>NSWindow</c> through the Objective-C runtime; anything
/// unexpected (no handle, a handle that is not a view or a window) leaves the window as it is.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacFloatingWindow
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // NSWindowCollectionBehavior
    private const nuint CanJoinAllSpaces = 1 << 0;
    private const nuint Stationary = 1 << 4;
    private const nuint IgnoresCycle = 1 << 6;
    private const nuint FullScreenAuxiliary = 1 << 8;

    /// <summary>Call once the window is open (it has its native window then), on the UI thread.</summary>
    public static void Configure(IPlatformHandle? handle)
    {
        if (handle is not { Handle: not 0 }) return;
        var setBehavior = sel_registerName("setCollectionBehavior:");
        var window = handle.Handle;
        // Avalonia hands out the window's content view; its window is the NSWindow
        if (!RespondsTo(window, setBehavior))
        {
            var getWindow = sel_registerName("window");
            if (!RespondsTo(window, getWindow)) return;
            window = intptr_objc_msgSend(window, getWindow);
            if (window == 0 || !RespondsTo(window, setBehavior)) return;
        }
        void_objc_msgSend(window, setBehavior, CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary);
        void_objc_msgSend(window, sel_registerName("setHasShadow:"), (byte)0);
    }

    private static bool RespondsTo(nint receiver, nint selector) =>
        bool_objc_msgSend(receiver, sel_registerName("respondsToSelector:"), selector) != 0;

    // ── Objective-C runtime (objc_msgSend is called with the exact argument types of each method: arm64 needs that) ──

    [DllImport(ObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint intptr_objc_msgSend(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte bool_objc_msgSend(nint receiver, nint selector, nint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend(nint receiver, nint selector, nuint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void void_objc_msgSend(nint receiver, nint selector, byte a);
}
