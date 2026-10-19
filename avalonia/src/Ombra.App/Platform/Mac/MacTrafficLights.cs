using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Platform.Mac;

/// <summary>
/// Keeps the window's traffic lights in the middle of a title band taller than the system's title bar (the Codex app's:
/// centred in its 44 pt band), the way Electron's <c>trafficLightPosition</c> does: the buttons' container is made as tall
/// as the band, and the buttons are centred in it from the given left edge on, at AppKit's own spacing.
/// <para>AppKit puts them back whenever it tiles its title bar — a new window title, a resize, a change of appearance
/// (the theme): the container to its own 28 pt and the buttons to its top left, 8 pt above the band's middle
/// (<c>-[NSThemeFrame _updateButtonPositions]</c> through <c>-[_NSThemeWidget setFrameOrigin:ignoreRentry:]</c>, which
/// goes straight to NSView's setter: no override of <c>setFrameOrigin:</c> sees it, and placing them again from the
/// frame-change notification is overwritten as AppKit's setter returns). So the container and the three buttons are
/// pinned where AppKit sets their frames: each becomes an instance of a subclass of its own class (as key-value observing
/// does) whose frame setters pass the band's place on to AppKit's own while the window is held, and what was asked for
/// otherwise (full screen, where the lights live in a title bar of their own).</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacTrafficLights
{
    /// <summary>A window's band and left edge, and the buttons' spacing as AppKit lays them out.</summary>
    private sealed class Held
    {
        public double Band, Left, Spacing;
        public nint Window;
    }

    /// <summary>The pinned views: the container (index -1) and the close, minimise and zoom buttons (0–2).</summary>
    private static readonly Dictionary<nint, (Held Held, int Index)> Pinned = [];
    private static readonly Dictionary<nint, Held> Windows = [];

    /// <summary>Each pinned subclass and AppKit's class it stands in for.</summary>
    private static readonly Dictionary<nint, nint> Originals = [];

    private const string Prefix = "OmbraPinned_";
    private const string ThemeWidgetSetter = "setFrameOrigin:ignoreRentry:";

    /// <summary>Puts the window's lights in the middle of the band and keeps them there until <see cref="Release"/>.</summary>
    /// <param name="window">The NSWindow (or a view in it).</param>
    /// <param name="band">The band's height, in points.</param>
    /// <param name="left">The close button's left edge, in points.</param>
    public static void Hold(nint window, double band, double left)
    {
        window = WindowOf(window);
        if (window == 0) return;
        var close = MacObjC.SendIndex(window, "standardWindowButton:", 0);
        var mini = MacObjC.SendIndex(window, "standardWindowButton:", 1);
        var zoom = MacObjC.SendIndex(window, "standardWindowButton:", 2);
        if (close == 0 || mini == 0 || zoom == 0) return;
        var container = MacObjC.Send(MacObjC.Send(close, "superview"), "superview");
        if (container == 0) return;
        if (!Windows.TryGetValue(window, out var held))
        {
            // AppKit's spacing, read before the buttons are pinned (none yet: the title bar is not tiled, hold later)
            var spacing = Frame(mini).X - Frame(close).X;
            if (spacing <= 0) return;
            Windows[window] = held = new Held { Window = window, Spacing = spacing };
        }
        (held.Band, held.Left) = (band, left);
        // AppKit makes a new container after full screen: each view is pinned once
        var views = new[] { container, close, mini, zoom };
        for (var i = 0; i < views.Length; i++)
            if (!Pinned.ContainsKey(views[i]) && Pin(views[i])) Pinned[views[i]] = (held, i - 1);
        // AppKit's own setters, now pinned, put each where it belongs
        foreach (var view in views) SetFrame(view, Frame(view));
    }

    /// <summary>Leaves the window's lights to AppKit again (full screen hides them and gives them a title bar of its own).</summary>
    public static void Release(nint window)
    {
        window = WindowOf(window);
        if (!Windows.Remove(window)) return;
        foreach (var view in Pinned.Where(p => p.Value.Held.Window == window).Select(p => p.Key).ToList()) Pinned.Remove(view);
    }

    private static nint WindowOf(nint handle)
    {
        if (handle == 0) return 0;
        if (MacObjC.RespondsTo(handle, "standardWindowButton:")) return handle;
        if (!MacObjC.RespondsTo(handle, "window")) return 0;
        var window = MacObjC.Send(handle, "window");
        return MacObjC.RespondsTo(window, "standardWindowButton:") ? window : 0;
    }

    /// <summary>The frame AppKit asks for, or the band's place for it while its window is held.</summary>
    private static CGRect Placed(nint view, CGRect asked)
    {
        if (!Pinned.TryGetValue(view, out var pin) || !Windows.ContainsKey(pin.Held.Window)) return asked;
        var held = pin.Held;
        // The container hangs from the window's top edge (AppKit's frames start at the bottom left); the buttons are
        // centred in it at AppKit's spacing
        return pin.Index < 0
            ? new CGRect(asked.X, Frame(held.Window).Height - held.Band, asked.Width, held.Band)
            : new CGRect(held.Left + pin.Index * held.Spacing, Math.Round((held.Band - asked.Height) / 2), asked.Width, asked.Height);
    }

    /// <summary>Makes <paramref name="view"/> an instance of a subclass of its class with the pinned setters; false when it
    /// cannot be (key-value observing's subclass already stands in for its class).</summary>
    private static bool Pin(nint view)
    {
        var cls = MacObjC.ClassOf(view);
        var name = MacObjC.ClassName(cls) ?? "";
        if (name.StartsWith(Prefix, StringComparison.Ordinal)) return true;
        if (name.StartsWith("NSKVONotifying_", StringComparison.Ordinal)) return false;
        List<(string, nint, string)> setters =
        [
            ("setFrame:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, CGRect, void>)&SetFramePinned, "v@:{CGRect={CGPoint=dd}{CGSize=dd}}"),
            ("setFrameOrigin:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, CGPoint, void>)&SetFrameOriginPinned, "v@:{CGPoint=dd}"),
            ("setFrameSize:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, CGSize, void>)&SetFrameSizePinned, "v@:{CGSize=dd}"),
        ];
        // The window buttons' own setter, the one AppKit tiles them with
        if (MacObjC.ImplementationOf(cls, ThemeWidgetSetter) != 0)
            setters.Add((ThemeWidgetSetter, (nint)(delegate* unmanaged[Cdecl]<nint, nint, CGPoint, byte, void>)&SetThemeWidgetOriginPinned, "v@:{CGPoint=dd}B"));
        var pinned = MacObjC.DefineClass(cls, Prefix + name, null, [.. setters]);
        Originals[pinned] = cls;
        MacObjC.SetClass(view, pinned);
        return true;
    }

    /// <summary>AppKit's own setter for the view (its class's, under the pinned subclass and any subclass made later).</summary>
    private static nint Own(nint view, string selector)
    {
        for (var cls = MacObjC.ClassOf(view); cls != 0; cls = MacObjC.Send(cls, "superclass"))
            if (Originals.TryGetValue(cls, out var original)) return MacObjC.ImplementationOf(original, selector);
        return MacObjC.ImplementationOf(MacObjC.Cls("NSView"), selector);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetFramePinned(nint self, nint cmd, CGRect frame) =>
        ((delegate* unmanaged<nint, nint, CGRect, void>)Own(self, "setFrame:"))(self, cmd, Placed(self, frame));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetFrameOriginPinned(nint self, nint cmd, CGPoint origin) =>
        ((delegate* unmanaged<nint, nint, CGPoint, void>)Own(self, "setFrameOrigin:"))(self, cmd, PlacedOrigin(self, origin));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetThemeWidgetOriginPinned(nint self, nint cmd, CGPoint origin, byte ignoreReentry) =>
        ((delegate* unmanaged<nint, nint, CGPoint, byte, void>)Own(self, ThemeWidgetSetter))(self, cmd, PlacedOrigin(self, origin), ignoreReentry);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetFrameSizePinned(nint self, nint cmd, CGSize size)
    {
        var now = Frame(self);
        var placed = Placed(self, new CGRect(now.X, now.Y, size.Width, size.Height));
        ((delegate* unmanaged<nint, nint, CGSize, void>)Own(self, "setFrameSize:"))(self, cmd, new CGSize(placed.Width, placed.Height));
    }

    private static CGPoint PlacedOrigin(nint view, CGPoint origin)
    {
        var now = Frame(view);
        var placed = Placed(view, new CGRect(origin.X, origin.Y, now.Width, now.Height));
        return new CGPoint(placed.X, placed.Y);
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
