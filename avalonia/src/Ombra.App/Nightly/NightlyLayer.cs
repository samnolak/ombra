using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Ombra.App.Platform.Mac;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Nightly;

/// <summary>
/// Where a pane's Nightly frames are drawn: a Core Animation layer in the pane's window, right under Avalonia's view,
/// whose contents is the IOSurface Nightly presented. The window server composites it as it composites Nightly's own
/// windows: no copy and no Avalonia frame per page frame. (Avalonia's OpenGL import copied every frame on the CPU,
/// ~3 ms for a 2520×1700 px page on an M5, and each frame redrew Ombra's window, ~4 ms more: 90 % of a core at 160 fps,
/// a page frame shown 14–21 ms after Nightly presented it, and Nightly held back while Ombra caught up.)
/// Avalonia shows the layer through a hole the pane clears in what Avalonia draws, so whatever Avalonia draws over the
/// pane (cards, popups, the status) stays above the page, and what it draws around the hole (rounded corners, the
/// window around a sheet) covers the layer's edges.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class NightlyLayer
{
    private readonly nint _avnView, _parent, _view, _layer;
    private nint _contents;
    private CGRect _frame;
    private double _scale;
    private bool _hidden = true;

    private NightlyLayer(nint avnView, nint parent, nint view, nint layer)
    {
        _avnView = avnView;
        _parent = parent;
        _view = view;
        _layer = layer;
    }

    /// <summary>A layer under the Avalonia view of a top level's platform handle (its NSView, or its NSWindow whose content
    /// holds the view), hidden; null when there is no such view in a window yet.</summary>
    public static NightlyLayer? Under(nint handle)
    {
        var avnView = AvaloniaView(handle);
        var parent = avnView == 0 ? 0 : MacObjC.Send(avnView, "superview");
        if (parent == 0) return null;
        var layer = MacObjC.Retain(MacObjC.Send(MacObjC.Cls("CALayer"), "layer"));
        using (var resize = new NSStr("resize")) MacObjC.SendVoid(layer, "setContentsGravity:", resize.Handle);
        // No implicit animations: a new frame, a move or a resize shows at once.
        var none = MacObjC.Send(MacObjC.Cls("NSNull"), "null");
        var keys = new[] { "contents", "bounds", "position", "frame", "hidden", "contentsScale", "onOrderIn", "onOrderOut" };
        var dict = MacObjC.Send(MacObjC.Cls("NSMutableDictionary"), "dictionary");
        foreach (var key in keys)
        {
            using var k = new NSStr(key);
            MacObjC.SendVoid(dict, "setObject:forKey:", none, k.Handle);
        }
        MacObjC.SendVoid(layer, "setActions:", dict);
        SetBool(layer, "setHidden:", true);
        var view = ((delegate* unmanaged<nint, nint, CGRect, nint>)MacObjC.MsgSend)(
            MacObjC.Send(MacObjC.Cls("NSView"), "alloc"), MacObjC.Sel("initWithFrame:"), default);
        // Layer hosting (the layer set before wantsLayer): AppKit leaves the layer's contents alone.
        MacObjC.SendVoid(view, "setLayer:", layer);
        SetBool(view, "setWantsLayer:", true);
        SetBool(view, "setHidden:", true);
        ((delegate* unmanaged<nint, nint, nint, long, nint, void>)MacObjC.MsgSend)(
            parent, MacObjC.Sel("addSubview:positioned:relativeTo:"), view, -1 /* NSWindowBelow */, avnView);
        return new NightlyLayer(avnView, parent, view, layer);
    }

    /// <summary>The view Avalonia draws the top level in: the handle when it is a view, else the window's descendant
    /// <c>AvnView</c>.</summary>
    private static nint AvaloniaView(nint handle)
    {
        if (handle == 0) return 0;
        if (MacObjC.IsKindOf(handle, "NSView")) return handle;
        return MacObjC.IsKindOf(handle, "NSWindow") ? Find(MacObjC.Send(handle, "contentView")) : 0;

        static nint Find(nint view)
        {
            if (view == 0) return 0;
            if (MacObjC.ClassName(MacObjC.ClassOf(view)) == "AvnView") return view;
            var subviews = MacObjC.Send(view, "subviews");
            for (long i = 0, n = MacObjC.SendLong(subviews, "count"); i < n; i++)
                if (Find(MacObjC.SendIndex(subviews, "objectAtIndex:", (nuint)i)) is var found and not 0) return found;
            return 0;
        }
    }

    /// <summary>The layer's view is still under the Avalonia view of <paramref name="handle"/> in its window.</summary>
    public bool IsUnder(nint handle) => AvaloniaView(handle) == _avnView && MacObjC.Send(_view, "superview") == _parent;

    /// <summary>The layer is on screen (placed, not hidden).</summary>
    public bool IsShown => !_hidden;

    /// <summary>Shows <paramref name="surface"/> (an IOSurfaceRef; Core Animation keeps it as long as it shows it).</summary>
    public void Show(nint surface)
    {
        if (surface == _contents) return;
        _contents = surface;
        MacObjC.SendVoid(_layer, "setContents:", surface);
    }

    /// <summary>
    /// Places the layer at <paramref name="rect"/> of Avalonia's view (DIP = points, top-left origin), its contents drawn
    /// at <paramref name="scale"/> pixels per point; hidden when <paramref name="hidden"/> or the rect is empty.
    /// </summary>
    public void Place(Rect rect, double scale, bool hidden)
    {
        hidden |= rect.Width <= 0 || rect.Height <= 0;
        if (!hidden)
        {
            var inParent = ((delegate* unmanaged<nint, nint, CGRect, nint, CGRect>)MacObjC.MsgSend)(
                _avnView, MacObjC.Sel("convertRect:toView:"), new CGRect(rect.X, rect.Y, rect.Width, rect.Height), _parent);
            if (!inParent.Equals(_frame))
            {
                _frame = inParent;
                ((delegate* unmanaged<nint, nint, CGRect, void>)MacObjC.MsgSend)(_view, MacObjC.Sel("setFrame:"), inParent);
            }
            if (scale != _scale)
            {
                _scale = scale;
                ((delegate* unmanaged<nint, nint, double, void>)MacObjC.MsgSend)(_layer, MacObjC.Sel("setContentsScale:"), scale);
            }
        }
        if (hidden == _hidden) return;
        _hidden = hidden;
        SetBool(_view, "setHidden:", hidden);
        SetBool(_layer, "setHidden:", hidden);
    }

    /// <summary>Takes the layer out of the window for good.</summary>
    public void Remove()
    {
        MacObjC.SendVoid(_layer, "setContents:", 0);
        MacObjC.SendVoid(_view, "removeFromSuperview");
        MacObjC.Release(_view);
        MacObjC.Release(_layer);
        _contents = 0;
    }

    private static void SetBool(nint obj, string selector, bool value) =>
        ((delegate* unmanaged<nint, nint, byte, void>)MacObjC.MsgSend)(obj, MacObjC.Sel(selector), value ? (byte)1 : (byte)0);
}
