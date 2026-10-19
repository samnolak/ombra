using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ombra.App.Platform.Mac;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Nightly;

/// <summary>
/// Ombra's AppKit drag for a drag that began in a Nightly pane and left it (desktop <c>ZenEmbedHost.mm</c>, "Drags"): the
/// embedded window is never on screen, so only Ombra, which has the real mouse, can run a drag over other windows and
/// apps. Its items are those of the named pasteboard Nightly put the drag's data on; each type is read from there only
/// when a destination asks for it (Nightly's view provides it then), a promised file at the place the destination named
/// (Finder: where it was dropped).
/// </summary>
[SupportedOSPlatform("macos")]
internal static class NightlyDrag
{
    // NSDragOperation
    public const ulong None = 0, Copy = 1, Link = 2, Generic = 4, Move = 16;
    private const ulong Offered = Copy | Link | Generic | Move;

    private const string PromisedFileUrl = "com.apple.pasteboard.promised-file-url";

    // Ours (NSPasteboardItem) → Nightly's (NSPasteboardItem, retained), for the provider; replaced at the next drag.
    private static readonly Dictionary<nint, nint> Items = [];
    private static string _pasteboard = "";
    private static Action<ulong, CGPoint, bool>? _ended;
    private static nint _source, _provider;

    /// <summary>
    /// Begins the drag from <paramref name="view"/> with <paramref name="mouseEvent"/> (the mouse-dragged event that left
    /// the pane): Nightly's pasteboard <paramref name="pasteboard"/> as its data, <paramref name="png"/> drawn at
    /// <paramref name="imageFrame"/> (the view's coordinates). <paramref name="ended"/> gets the operation, the screen point
    /// and whether the user cancelled it (Escape). False when there is nothing to drag or AppKit refused.
    /// </summary>
    public static unsafe bool Begin(nint view, nint mouseEvent, string pasteboard, byte[] png, CGRect imageFrame, Action<ulong, CGPoint, bool> ended)
    {
        var pool = MacObjC.PoolPush();
        try
        {
            Forget();
            EnsureClasses();
            var theirs = MacObjC.Send(MacObjC.Send(MacObjC.Cls("NSPasteboard"), "pasteboardWithName:", MacObjC.Str(pasteboard)), "pasteboardItems");
            var count = theirs == 0 ? 0 : MacObjC.SendLong(theirs, "count");
            if (count == 0 || view == 0 || mouseEvent == 0) return false;
            var image = ImageFrom(png);
            var items = MacObjC.Send(MacObjC.Cls("NSMutableArray"), "array");
            for (long i = 0; i < count; i++)
            {
                var their = MacObjC.SendIndex(theirs, "objectAtIndex:", (nuint)i);
                // +1 from alloc: Items owns it until the next drag (a destination may read it after the drop).
                var ours = MacObjC.Send(MacObjC.Send(MacObjC.Cls("NSPasteboardItem"), "alloc"), "init");
                if (!SendBool2(ours, "setDataProvider:forTypes:", _provider, MacObjC.Send(their, "types")))
                {
                    MacObjC.Release(ours);
                    continue;
                }
                Items[ours] = MacObjC.Retain(their);
                var item = MacObjC.Send(MacObjC.Send(MacObjC.Send(MacObjC.Cls("NSDraggingItem"), "alloc"), "initWithPasteboardWriter:", ours), "autorelease");
                ((delegate* unmanaged<nint, nint, CGRect, nint, void>)MacObjC.MsgSend)(item, MacObjC.Sel("setDraggingFrame:contents:"), imageFrame, i == 0 ? image : 0);
                MacObjC.SendVoid(items, "addObject:", item);
            }
            if (MacObjC.SendLong(items, "count") == 0) return false;
            _pasteboard = pasteboard;
            _ended = ended;
            var session = MacObjC.Send(view, "beginDraggingSessionWithItems:event:source:", items, mouseEvent, _source);
            if (session == 0)
            {
                _ended = null;
                return false;
            }
            return true;
        }
        finally
        {
            MacObjC.PoolPop(pool);
        }
    }

    /// <summary>The Avalonia drag effects as an NSDragOperation mask, and back.</summary>
    public static ulong Operations(Avalonia.Input.DragDropEffects effects) =>
        (effects.HasFlag(Avalonia.Input.DragDropEffects.Copy) ? Copy | Generic : 0)
        | (effects.HasFlag(Avalonia.Input.DragDropEffects.Move) ? Move : 0)
        | (effects.HasFlag(Avalonia.Input.DragDropEffects.Link) ? Link : 0);

    /// <summary>The effect a drop with <paramref name="operation"/> (NSDragOperation) has, AppKit's order: copy, link,
    /// generic (a copy), move.</summary>
    public static Avalonia.Input.DragDropEffects Effect(ulong operation) =>
        (operation & Copy) != 0 ? Avalonia.Input.DragDropEffects.Copy
        : (operation & Link) != 0 ? Avalonia.Input.DragDropEffects.Link
        : (operation & Generic) != 0 ? Avalonia.Input.DragDropEffects.Copy
        : (operation & Move) != 0 ? Avalonia.Input.DragDropEffects.Move
        : Avalonia.Input.DragDropEffects.None;

    private static unsafe nint ImageFrom(byte[] png)
    {
        if (png.Length == 0) return 0;
        nint data;
        fixed (byte* bytes = png)
            data = ((delegate* unmanaged<nint, nint, byte*, nuint, nint>)MacObjC.MsgSend)(
                MacObjC.Cls("NSData"), MacObjC.Sel("dataWithBytes:length:"), bytes, (nuint)png.Length);
        var image = MacObjC.Send(MacObjC.Send(MacObjC.Cls("NSImage"), "alloc"), "initWithData:", data);
        return image == 0 ? 0 : MacObjC.Send(image, "autorelease");
    }

    private static unsafe bool SendBool2(nint receiver, string selector, nint a, nint b) =>
        ((delegate* unmanaged<nint, nint, nint, nint, byte>)MacObjC.MsgSend)(receiver, MacObjC.Sel(selector), a, b) != 0;

    private static void Forget()
    {
        foreach (var (ours, theirs) in Items)
        {
            MacObjC.Release(theirs);
            MacObjC.Release(ours);
        }
        Items.Clear();
    }

    private static unsafe void EnsureClasses()
    {
        if (_source != 0) return;
        var source = MacObjC.DefineClass("OmbraNightlyDragSource", "NSDraggingSource",
            ("draggingSession:sourceOperationMaskForDraggingContext:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, long, nuint>)&SourceOperations, "Q@:@q"),
            ("draggingSession:endedAtPoint:operation:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, CGPoint, nuint, void>)&Ended, "v@:@{CGPoint=dd}Q"));
        var provider = MacObjC.DefineClass("OmbraNightlyDragData", "NSPasteboardItemDataProvider",
            ("pasteboard:item:provideDataForType:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&Provide, "v@:@@@"),
            ("pasteboardFinishedWithDataProvider:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Finished, "v@:@"));
        _source = MacObjC.Send(MacObjC.Send(source, "alloc"), "init");
        _provider = MacObjC.Send(MacObjC.Send(provider, "alloc"), "init");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static nuint SourceOperations(nint self, nint cmd, nint session, long context) => (nuint)Offered;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void Ended(nint self, nint cmd, nint session, CGPoint point, nuint operation) =>
        MacObjC.Guard(() =>
        {
            var current = MacObjC.Send(MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication"), "currentEvent");
            var cancelled = current != 0 && MacObjC.SendNUInt(current, "type") == 10 /* keyDown */
                && MacObjC.SendNUInt(current, "keyCode") == 0x35 /* Escape */;
            var ended = _ended;
            _ended = null;
            ended?.Invoke(operation, point, cancelled);
        });

    /// <summary>A destination asks for <paramref name="type"/>: read it from Nightly's item (its view provides it now); a
    /// promised file goes where the destination said (its paste location, handed to Nightly's pasteboard).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void Provide(nint self, nint cmd, nint pasteboard, nint item, nint type) =>
        MacObjC.Guard(() =>
        {
            if (!Items.TryGetValue(item, out var theirs)) return;
            if (MacObjC.ToStr(type) == PromisedFileUrl) CopyPasteLocation(pasteboard, _pasteboard);
            var data = MacObjC.Send(theirs, "dataForType:", type);
            if (data != 0) SendBool2(item, "setData:forType:", data, type);
        });

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void Finished(nint self, nint cmd, nint pasteboard)
    {
    }

    private static void CopyPasteLocation(nint from, string to)
    {
        var name = MacObjC.Send(from, "name");
        if (name == 0 || PasteboardCreate(name, out var source) != 0) return;
        try
        {
            PasteboardSynchronize(source);
            if (PasteboardCopyPasteLocation(source, out var location) != 0 || location == 0) return;
            try
            {
                if (PasteboardCreate(MacObjC.Str(to), out var target) != 0) return;
                PasteboardSetPasteLocation(target, location);
                NightlyMach.CFRelease(target);
            }
            finally
            {
                NightlyMach.CFRelease(location);
            }
        }
        finally
        {
            NightlyMach.CFRelease(source);
        }
    }

    private const string HIServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    [DllImport(HIServices)]
    private static extern int PasteboardCreate(nint name, out nint pasteboard);

    [DllImport(HIServices)]
    private static extern uint PasteboardSynchronize(nint pasteboard);

    [DllImport(HIServices)]
    private static extern int PasteboardCopyPasteLocation(nint pasteboard, out nint location);

    [DllImport(HIServices)]
    private static extern int PasteboardSetPasteLocation(nint pasteboard, nint location);
}
