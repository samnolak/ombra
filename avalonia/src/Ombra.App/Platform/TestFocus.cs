using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Ombra.App.Nightly;
using Ombra.App.Platform.Mac;

namespace Ombra.App.Platform;

/// <summary>
/// <c>OMBRA_TEST_NO_ACTIVATE</c>: GUI checks run on the owner's Mac while he works in another app, so a check never takes
/// his focus. With it the app's activation policy is always <c>NSApplicationActivationPolicyProhibited</c> (no Dock icon,
/// no menu bar, and macOS never activates it: not at launch when the app that started it is the front one, not on a click,
/// not when the front app quits — its windows still show and take in-process events); every activation call the process
/// makes does nothing (Avalonia's at launch and in <c>Window.Show</c>/<c>Activate</c>, <c>-[NSApp unhide:]</c>, the
/// drivers', another app's too); the Dock never bounces; the main window is shown without becoming key, behind the other
/// apps' windows (<see cref="SendBehind"/>), on the display left of the main one (<see cref="LeftDisplay"/>); the Nightly
/// runtime it starts cannot raise itself (<c>mozilla.widget.raise-on-setfocus</c>); a check starts no <c>screencapture</c>
/// without Screen Recording (<see cref="ScreenCaptureAllowed"/>). <see cref="Activate"/> gives the window Avalonia's
/// activation (focus, menus) in-process. Should the app still become active, stderr says so with the mouse state.
/// <para>On by default for every run a harness drives: the feature smoke (<c>OMBRA_FEATURE_SMOKE</c>), NightlyAcceptance
/// (<c>OMBRA_NIGHTLY_ACCEPTANCE</c>), <c>--smoke-exit-after-ms</c> and benchmark runs, the performance gate, and what
/// Ombra.Tests starts; set <c>OMBRA_TEST_NO_ACTIVATE=0</c> only for a check of app activation itself. The user's app
/// never turns it on alone.</para>
/// </summary>
public static unsafe class TestFocus
{
    public const string Variable = "OMBRA_TEST_NO_ACTIVATE";

    /// <summary>The process never activates itself (see the class summary).</summary>
    public static bool NoActivate { get; private set; }

    /// <summary>
    /// Decides once, before Avalonia starts: <c>OMBRA_TEST_NO_ACTIVATE=1</c>, or a run a harness drives
    /// (<paramref name="harness"/>) unless it is <c>0</c>. When on, sets the variable for what the app starts and
    /// installs the guards.
    /// </summary>
    public static void Configure(bool harness)
    {
        var value = Environment.GetEnvironmentVariable(Variable);
        NoActivate = value == "1" || (harness && value != "0");
        if (!NoActivate) return;
        Environment.SetEnvironmentVariable(Variable, "1");
        if (OperatingSystem.IsMacOS()) InstallGuards();
    }

    /// <summary>
    /// A harness run asks Avalonia for no Dock icon (Accessory), so it always sets the policy, whatever the process
    /// started with; the guard makes it Prohibited.
    /// </summary>
    public static AppBuilder Apply(AppBuilder builder) =>
        NoActivate && OperatingSystem.IsMacOS() ? builder.With(new MacOSPlatformOptions { ShowInDock = false }) : builder;

    /// <summary>
    /// The window takes the keyboard, as after the user's click on it: normally the app is activated and the window made
    /// key; with <see cref="NoActivate"/> the app stays inactive (AppKit makes no window key in an inactive app) and the
    /// window becomes Avalonia's active window in-process (<c>-becomeKeyWindow</c>: focus, its menu as the main menu),
    /// left where it is in the window order (<see cref="SendBehind"/>).
    /// </summary>
    public static void Activate(Window window)
    {
        if (!OperatingSystem.IsMacOS())
        {
            window.Activate();
            return;
        }
        var ns = NightlyCocoa.WindowOf(window.TryGetPlatformHandle()?.Handle ?? 0);
        if (!NoActivate)
        {
            ((delegate* unmanaged<nint, nint, byte, void>)MacObjC.MsgSend)(
                MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication"), MacObjC.Sel("activateIgnoringOtherApps:"), 1);
            if (ns != 0) MacObjC.SendVoid(ns, "makeKeyAndOrderFront:", 0);
            window.Activate();
            return;
        }
        if (ns == 0 || window.IsActive) return;
        MacObjC.SendVoid(ns, "becomeKeyWindow");
        // Avalonia brings a window that became key to the front (-windowDidBecomeKey:, now and on the next main-queue
        // turn): back behind the user's windows after both.
        SendBehind(window);
        MacObjC.Post(() => SendBehind(window));
    }

    /// <summary>
    /// With <see cref="NoActivate"/>: the window goes behind the other apps' windows (<c>-orderBack:</c>), so it never
    /// covers what the user works in and his clicks and keys never land in it. Posted events, the window's own shots and
    /// Nightly's frames do not need it in front.
    /// </summary>
    public static void SendBehind(Window window)
    {
        if (!NoActivate || !OperatingSystem.IsMacOS()) return;
        var ns = NightlyCocoa.WindowOf(window.TryGetPlatformHandle()?.Handle ?? 0);
        if (ns != 0) MacObjC.SendVoid(ns, "orderBack:", 0);
    }

    /// <summary>With <see cref="NoActivate"/>: the window loses Avalonia's activation as when another app takes the focus
    /// (<c>-resignKeyWindow</c>), without any app switch.</summary>
    public static void Deactivate(Window window)
    {
        if (!OperatingSystem.IsMacOS() || !window.IsActive) return;
        var ns = NightlyCocoa.WindowOf(window.TryGetPlatformHandle()?.Handle ?? 0);
        if (ns != 0) MacObjC.SendVoid(ns, "resignKeyWindow");
    }

    /// <summary>A check's window position: with <see cref="NoActivate"/> 40 pt into the work area of the leftmost display
    /// left of the main one (the owner works on the main display), else <paramref name="fallback"/>.</summary>
    public static PixelPoint Position(Window window, PixelPoint fallback) => LeftDisplay(window) ?? fallback;

    /// <summary>With <see cref="NoActivate"/>: 40 pt into the leftmost display left of the main one; null without one.</summary>
    public static PixelPoint? LeftDisplay(Window window)
    {
        if (!NoActivate || window.Screens?.All is not { Count: > 1 } screens) return null;
        var left = screens.Where(s => s.Bounds.X < 0).OrderBy(s => s.Bounds.X).FirstOrDefault();
        if (left is null) return null;
        var inset = (int)Math.Round(40 * left.Scaling);
        return new PixelPoint(left.WorkingArea.X + inset, left.WorkingArea.Y + inset);
    }

    /// <summary>
    /// Whether a check may start <c>screencapture</c> (another process, which needs Screen Recording): always outside
    /// a harness run; in one only when the permission is there (<c>CGPreflightScreenCaptureAccess</c>, which never asks),
    /// as macOS otherwise asks for it in a window that takes the focus. A check then captures its own window in-process.
    /// </summary>
    public static bool ScreenCaptureAllowed => !NoActivate || (OperatingSystem.IsMacOS() && CGPreflightScreenCaptureAccess());

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CGPreflightScreenCaptureAccess();

    // ── the guards: AppKit's activation calls of this process do nothing ──

    private static nint sSetActivationPolicy;

    [SupportedOSPlatform("macos")]
    private static void InstallGuards()
    {
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        var app = MacObjC.Cls("NSApplication");
        // Whatever policy Avalonia asks for (Regular, or Accessory without a Dock icon): Prohibited, which macOS never
        // activates. A child of the front app is otherwise activated at launch by the system, no call of its own needed.
        sSetActivationPolicy = Replace(app, "setActivationPolicy:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&ProhibitedPolicy);
        Replace(app, "activateIgnoringOtherApps:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte, void>)&IgnoreActivate);
        Replace(app, "activate", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&IgnoreCooperativeActivate);
        Replace(app, "unhide:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&UnhideWithoutActivation);
        Replace(app, "requestUserAttention:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nuint, nint>)&IgnoreAttention);
        var running = MacObjC.Cls("NSRunningApplication");
        Replace(running, "activateWithOptions:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nuint, byte>)&RefuseActivation);
        Replace(running, "activateFromApplication:options:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nuint, byte>)&RefuseActivationFrom);
        // Should the app still become active (a path not guarded here): said on stderr with the mouse state, for whoever
        // reads the run's log.
        var pool = MacObjC.PoolPush();
        try
        {
            var observer = MacObjC.Send(MacObjC.DefineClass("OmbraTestFocusObserver", null,
                ("becameActive:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&BecameActive, "v@:@")), "new");
            ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)MacObjC.MsgSend)(
                MacObjC.Send(MacObjC.Cls("NSNotificationCenter"), "defaultCenter"), MacObjC.Sel("addObserver:selector:name:object:"),
                observer, MacObjC.Sel("becameActive:"), MacObjC.Str("NSApplicationDidBecomeActiveNotification"), 0);
        }
        finally { MacObjC.PoolPop(pool); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [SupportedOSPlatform("macos")]
    private static void BecameActive(nint self, nint cmd, nint notification)
    {
        // The mouse over the window with a button held: the user clicked it.
        var app = MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication");
        var current = MacObjC.Send(app, "currentEvent");
        var buttons = MacObjC.SendNUInt(MacObjC.Cls("NSEvent"), "pressedMouseButtons");
        var mouse = ((delegate* unmanaged<nint, nint, MacRuntime.CGPoint>)MacObjC.MsgSend)(MacObjC.Cls("NSEvent"), MacObjC.Sel("mouseLocation"));
        Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} TestFocus: the app became active; mouse buttons={buttons} at "
            + $"({mouse.X:0},{mouse.Y:0}); event: {(current == 0 ? "none" : MacObjC.ToStr(MacObjC.Send(current, "description")))}");
    }

    private static void Blocked(string call) => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} TestFocus: {call} refused");

    /// <summary>The implementation <paramref name="selector"/> had (0: the class has none, nothing replaced).</summary>
    [SupportedOSPlatform("macos")]
    private static nint Replace(nint cls, string selector, nint imp) =>
        // -activate and -activateFromApplication:options: are macOS 14's: absent before, nothing to guard.
        cls != 0 && MacRuntime.InstanceMethod(cls, selector) is var method && method != 0 ? MacRuntime.SetImplementation(method, imp) : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static byte ProhibitedPolicy(nint self, nint cmd, nint policy) =>
        ((delegate* unmanaged<nint, nint, nint, byte>)sSetActivationPolicy)(self, cmd, 2 /* NSApplicationActivationPolicyProhibited */);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void IgnoreActivate(nint self, nint cmd, byte ignoringOtherApps) => Blocked("-[NSApplication activateIgnoringOtherApps:]");

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void IgnoreCooperativeActivate(nint self, nint cmd) => Blocked("-[NSApplication activate]");

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    [SupportedOSPlatform("macos")]
    private static void UnhideWithoutActivation(nint self, nint cmd, nint sender) =>
        ((delegate* unmanaged<nint, nint, void>)MacObjC.MsgSend)(self, MacObjC.Sel("unhideWithoutActivation"));

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static nint IgnoreAttention(nint self, nint cmd, nuint type)
    {
        Blocked("-[NSApplication requestUserAttention:]");
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static byte RefuseActivation(nint self, nint cmd, nuint options)
    {
        Blocked("-[NSRunningApplication activateWithOptions:]");
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static byte RefuseActivationFrom(nint self, nint cmd, nint application, nuint options)
    {
        Blocked("-[NSRunningApplication activateFromApplication:options:]");
        return 0;
    }
}
