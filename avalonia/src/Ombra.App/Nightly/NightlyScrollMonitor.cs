using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ombra.App.Platform.Mac;

namespace Ombra.App.Nightly;

/// <summary>
/// Scroll events for Nightly panes with what Avalonia's wheel event leaves out: the gesture and momentum phases and the
/// precise (pixel) deltas of trackpads and Magic Mice, which Gecko's APZ needs for pan gestures, momentum and
/// overscroll. A local <c>NSEvent</c> monitor sees each scroll event before Avalonia; one over a pane (or continuing a
/// gesture that began over it) goes to that pane and is consumed, so the panels around it do not scroll too.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class NightlyScrollMonitor
{
    /// <summary>A pane that takes scroll events: it says whether a window point is on it and takes the event there.</summary>
    internal interface ITarget
    {
        /// <summary>Whether the point (in the NSWindow's coordinates, bottom-left origin) is on this pane, unobscured.</summary>
        bool Hits(nint nsWindow, double x, double y);
        void Scroll(nint nsWindow, double x, double y, in WheelInput input);
    }

    private const ulong ScrollWheelMask = 1UL << 22; // NSEventMaskScrollWheel
    private static readonly List<ITarget> Targets = [];
    private static nint _monitor;
    private static ITarget? _captured;

    public static void Add(ITarget target)
    {
        if (!Targets.Contains(target)) Targets.Add(target);
        if (_monitor != 0) return;
        var block = MacBlocks.Create((nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&Invoke, Targets, "@16@?0@8");
        // The monitor keeps (copies) the block; this reference is the one Create made.
        _monitor = MacObjC.Retain(((delegate* unmanaged<nint, nint, ulong, nint, nint>)MacObjC.MsgSend)(
            MacObjC.Cls("NSEvent"), MacObjC.Sel("addLocalMonitorForEventsMatchingMask:handler:"), ScrollWheelMask, block));
        MacBlocks.Release(block);
    }

    public static void Remove(ITarget target)
    {
        Targets.Remove(target);
        if (_captured == target) _captured = null;
        if (Targets.Count > 0 || _monitor == 0) return;
        MacObjC.SendVoid(MacObjC.Cls("NSEvent"), "removeMonitor:", _monitor);
        MacObjC.Release(_monitor);
        _monitor = 0;
    }

    /// <summary>The monitor's handler: <c>NSEvent* (^)(NSEvent*)</c>; returning nil consumes the event.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Invoke(nint block, nint ev)
    {
        try
        {
            return Route(ev) ? 0 : ev;
        }
        catch (Exception e)
        {
            Services.ClientLog.Shared.Write("nightly", e);
            return ev;
        }
    }

    private static bool Route(nint ev)
    {
        var window = MacObjC.Send(ev, "window");
        if (window == 0) return false;
        var location = ((delegate* unmanaged<nint, nint, MacRuntime.CGPoint>)MacObjC.MsgSend)(ev, MacObjC.Sel("locationInWindow"));
        var input = new WheelInput(
            (NightlyProtocol.Phase)MacObjC.SendNUInt(ev, "phase"),
            (NightlyProtocol.Phase)MacObjC.SendNUInt(ev, "momentumPhase"),
            MacObjC.SendBool(ev, "hasPreciseScrollingDeltas"),
            Double(ev, "scrollingDeltaX"), Double(ev, "scrollingDeltaY"),
            Double(ev, "deltaX"), Double(ev, "deltaY"),
            MacObjC.SendNUInt(ev, "modifierFlags"));
        // A gesture belongs to the pane it began on (its momentum too), even when the pointer leaves it.
        ITarget? target;
        if (input.Phase is NightlyProtocol.Phase.Began or NightlyProtocol.Phase.MayBegin)
            target = _captured = Targets.FirstOrDefault(t => t.Hits(window, location.X, location.Y));
        else if (input.Phase != NightlyProtocol.Phase.None || input.MomentumPhase != NightlyProtocol.Phase.None)
            target = _captured;
        else
            target = Targets.FirstOrDefault(t => t.Hits(window, location.X, location.Y));
        if (input.MomentumPhase is NightlyProtocol.Phase.Ended or NightlyProtocol.Phase.Cancelled) _captured = null;
        if (target is null) return false;
        target.Scroll(window, location.X, location.Y, input);
        return true;
    }

    private static double Double(nint obj, string sel) =>
        ((delegate* unmanaged<nint, nint, double>)MacObjC.MsgSend)(obj, MacObjC.Sel(sel));
}
