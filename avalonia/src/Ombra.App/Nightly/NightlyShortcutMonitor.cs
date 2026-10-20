using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ombra.App.Platform.Mac;

namespace Ombra.App.Nightly;

/// <summary>
/// Shortcuts for the Browser page's Nightly window. While its pane has the keyboard, a key down with ⌘ or Control is
/// that window's (⌘T, ⌘L, ⌘W, ⇧⌘T, ⌃Tab, ⌘C, ⌘F, its spaces' keys…). AppKit would offer it to Ombra's menu bar
/// (⌘W, ⌘C, ⌘F are items there) and to its keyboard navigation (⌃Tab) before any view, so a local <c>NSEvent</c>
/// monitor takes it first: the event goes to the pane and is consumed. Ombra keeps only the app's and the window's own:
/// ⌘Q, ⌘H, ⌥⌘H, ⌘M, ⌘`, ⌃⌘F (full screen) and ⌘Esc (back to the chats). The key up takes the pane's usual path.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class NightlyShortcutMonitor
{
    /// <summary>The pane with the keyboard: it says whether the event's window is its own and takes the key.</summary>
    internal interface ITarget
    {
        bool TakesShortcut(nint nsWindow);
        void Shortcut(in NightlyCocoa.KeyEvent key);
    }

    private const ulong KeyDownMask = 1UL << 10; // NSEventMaskKeyDown
    private const ulong Command = 1UL << 20, Control = 1UL << 18;
    private static readonly List<ITarget> Targets = [];
    private static nint _monitor;

    public static void Add(ITarget target)
    {
        if (!Targets.Contains(target)) Targets.Add(target);
        if (_monitor != 0) return;
        var block = MacBlocks.Create((nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&Invoke, Targets, "@16@?0@8");
        // The monitor keeps (copies) the block; this reference is the one Create made.
        _monitor = MacObjC.Retain(((delegate* unmanaged<nint, nint, ulong, nint, nint>)MacObjC.MsgSend)(
            MacObjC.Cls("NSEvent"), MacObjC.Sel("addLocalMonitorForEventsMatchingMask:handler:"), KeyDownMask, block));
        MacBlocks.Release(block);
    }

    public static void Remove(ITarget target)
    {
        Targets.Remove(target);
        if (Targets.Count > 0 || _monitor == 0) return;
        MacObjC.SendVoid(MacObjC.Cls("NSEvent"), "removeMonitor:", _monitor);
        MacObjC.Release(_monitor);
        _monitor = 0;
    }

    /// <summary>Whether Ombra keeps a key down with these modifiers (NSEventModifierFlags) and macOS key code.</summary>
    internal static bool IsOmbras(ulong modifiers, int keyCode) =>
        (modifiers & (Command | Control)) == 0
        || ((modifiers & Command) != 0 && keyCode is 0x0C /* Q */ or 0x04 /* H */ or 0x2E /* M */ or 0x32 /* ` */ or 0x35 /* Esc */)
        || ((modifiers & (Command | Control)) == (Command | Control) && keyCode == 0x03 /* F */);

    /// <summary>The monitor's handler: <c>NSEvent* (^)(NSEvent*)</c>; returning nil consumes the event.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Invoke(nint block, nint ev)
    {
        try
        {
            if (NightlyCocoa.KeyEventOf(ev) is not { Type: 10 } key || IsOmbras(key.Modifiers, key.KeyCode)) return ev;
            var window = MacObjC.Send(ev, "window");
            if (window == 0 || Targets.FirstOrDefault(t => t.TakesShortcut(window)) is not { } target) return ev;
            target.Shortcut(key);
            return 0;
        }
        catch (Exception e)
        {
            Services.ClientLog.Shared.Write("nightly", e);
            return ev;
        }
    }
}
