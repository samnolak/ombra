using System.Globalization;
using Avalonia;
using Ombra.App.Controls;
using Ombra.App.Platform.Mac;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Nightly;

internal static partial class NightlyAcceptance
{
    private sealed partial class Run
    {
        /// <summary>
        /// Drag and drop in the Browser page's Nightly window (its window is never on screen, so the pane's mouse runs its
        /// drags: desktop ZenEmbedHost.mm, "Drags"), on a page written next to the log (<c>dnd.html</c>) whose background
        /// says what happened: a hand's drag of its source box onto its target box drops "from-src" there and tells the
        /// source "move" (blue); a drag of the source out of the pane is carried on by Ombra's AppKit drag and ends there
        /// (the source told "none": grey; without a mouse button held AppKit ends it at once); Finder's way of dropping a
        /// file on the page (the system drag pasteboard, AppKit's destination calls on Ombra's view) gives the page that
        /// file (black). Before the fix each drag began an AppKit drag of the off-screen window that no drop target ever
        /// saw: no drop, no dragend, the page's session stuck.
        /// </summary>
        private async Task DragDropChecks(NightlyPane pane)
        {
            var maxWindows = 0;
            var samples = 0;
            using var monitor = Avalonia.Threading.DispatcherTimer.Run(() =>
            {
                maxWindows = Math.Max(maxWindows, NightlyWindowRects(pane.RuntimePid).Count);
                samples++;
                return true;
            }, TimeSpan.FromMilliseconds(40));
            var page = Path.Combine(dir, "dnd.html");
            var file = Path.Combine(dir, "dnd-file.txt");
            await File.WriteAllTextAsync(page, DragDropPage);
            await File.WriteAllTextAsync(file, "hello-dnd\n");
            vm.OpenInBrowserWindow?.Invoke(new Uri(page));
            double luma = 0;
            bool Body(double low, double high) => (luma = ContentLuma(pane)) >= low && luma <= high;
            var loaded = await Until(() => Body(240, 256), 20);
            Activate();
            await Until(() => window.IsActive, 5);
            await Task.Delay(800);
            CGPoint At(double x, double y) => ClientToWindow(pane.TranslatePoint(new Point(x, y), window) ?? default);
            double W = pane.Bounds.Width, H = pane.Bounds.Height;

            await Drag(At, W * 0.32, H * 0.2, W * 0.85, H * 0.2, 16, 0);
            Check("browser-window-drag-page", loaded && await Until(() => Body(60, 110), 5), $"body luma={luma:0} (blue: dropped, dragend move)");
            Shot("p-browser-window-drag-page");

            await Drag(At, W * 0.32, H * 0.2, W * 0.85, H * 0.2, 16, 0, async () =>
            {
                Check("browser-window-drag-focus-started", await Until(() => Body(240, 256), 5),
                    "dragstart reset the previous result before switching apps");
                AnotherAppTakesFocus();
                Check("browser-window-drag-deactivated", await Until(() => !window.IsActive, 5),
                    "Finder takes focus while the mouse button is held");
                await Task.Delay(300);
            });
            Activate();
            await Until(() => window.IsActive, 5);
            Check("browser-window-drag-focus-cancel", await Until(() => Body(160, 190), 5),
                $"body luma={luma:0} (cyan: mozUserCancelled, dropEffect none, no drop)");

            await Drag(At, W * 0.32, H * 0.2, W * 0.85, H * 0.2, 16, 0, async () =>
            {
                Check("browser-window-drag-escape-started", await Until(() => Body(240, 256), 5),
                    "dragstart reset the previous cancellation result");
                KeyPress("\x1b", 0x35, 0);
                await Task.Delay(300);
            });
            Check("browser-window-drag-escape-cancel", await Until(() => Body(160, 190), 5),
                $"body luma={luma:0} (cyan: mozUserCancelled, dropEffect none, no drop)");

            await Drag(At, W * 0.32, H * 0.2, W * 0.85, H * 0.2, 16, 0);
            Check("browser-window-drag-after-cancel", await Until(() => Body(60, 110), 5),
                $"body luma={luma:0} (blue: normal drop still works after cancellation)");

            await Drag(At, W * 0.32, H * 0.2, -30, H * 0.2, 16, 0);
            Check("browser-window-drag-out", await Until(() => Body(110, 150) || Body(160, 190), 5), $"body luma={luma:0} (grey/cyan: dragend none, the drag carried out of the pane)");

            await DropFile(At(W * 0.85, H * 0.85), file);
            Check("browser-window-drag-files", await Until(() => Body(0, 30), 5), $"body luma={luma:0} (black: the page got dnd-file.txt)");
            Shot("p-browser-window-drag-files");
            Check("browser-window-drag-no-native-windows", maxWindows == 0 && NightlyWindowRects(pane.RuntimePid).Count == 0,
                $"CGWindowList on-screen Nightly windows: max={maxWindows}, samples={samples}, after={NightlyWindowRects(pane.RuntimePid).Count}");
            KeyPress("w", 0x0D, 1UL << 20);
            await Task.Delay(500);
        }

        private const string DragDropPage = """
            <!doctype html><meta charset="utf-8"><title>DND</title>
            <style>body{margin:0;background:#fff}div{position:fixed;width:45vw;height:40vh;border:2px solid #444;box-sizing:border-box}</style>
            <div id="src" draggable="true" style="left:0;top:0;background:#fd8">source</div>
            <div id="dst" style="left:55vw;top:0;background:#eee">target</div>
            <div id="files" style="left:55vw;top:60vh;background:#eef">files</div>
            <script>
            let dropped = "";
            const paint = c => document.body.style.background = c;
            src.ondragstart = e => { dropped = ""; paint("#fff"); e.dataTransfer.setData("text/plain", "from-src"); e.dataTransfer.effectAllowed = "copyMove"; };
            src.ondragend = e => paint(e.dataTransfer.mozUserCancelled && e.dataTransfer.dropEffect == "none" && !dropped ? "#00ffff" : e.dataTransfer.dropEffect == "move" && dropped == "from-src" ? "#0000ff" : e.dataTransfer.dropEffect == "none" ? "#808080" : "#ff0");
            for (const z of [dst, files]) { z.ondragenter = z.ondragover = e => e.preventDefault(); }
            dst.ondrop = e => { e.preventDefault(); dropped = e.dataTransfer.getData("text/plain"); };
            files.ondrop = e => { e.preventDefault(); const f = e.dataTransfer.files[0]; f && f.text().then(t => paint(f.name == "dnd-file.txt" && t.startsWith("hello-dnd") ? "#000" : "#f0f")); };
            </script>
            """;

        /// <summary>
        /// A file dropped from another app (as Finder does): the file on the system's drag pasteboard, then AppKit's
        /// destination calls (entered, updated while Nightly answers, prepare, perform, conclude) on Ombra's view at
        /// <paramref name="at"/> (window coordinates) with an NSDraggingInfo of the drag there.
        /// </summary>
        private async Task DropFile(CGPoint at, string path)
        {
            var pasteboard = MacObjC.Send(MacObjC.Cls("NSPasteboard"), "pasteboardWithName:", MacObjC.Str("Apple CFPasteboard drag"));
            MacObjC.Send(pasteboard, "clearContents");
            var urls = MacObjC.Send(MacObjC.Cls("NSArray"), "arrayWithObject:", MacObjC.FileUrl(path));
            MacObjC.SendBool(pasteboard, "writeObjects:", urls);
            _dropAt = at;
            _dropPasteboard = pasteboard;
            _dropWindow = NsWindow;
            var info = MacObjC.Send(MacObjC.Send(DraggingInfoClass(), "alloc"), "init");
            var view = HitTest(at);
            Log($"drop file {path} on {MacObjC.ClassName(MacObjC.ClassOf(view))} at {at.X:0},{at.Y:0}");
            var operation = Destination(view, "draggingEntered:", info);
            // AppKit updates the destination while the pointer rests; Nightly's answers come in meanwhile.
            for (var i = 0; i < 8; i++)
            {
                await Task.Delay(100);
                operation = Destination(view, "draggingUpdated:", info);
            }
            Log($"drop file: operation {operation}");
            if (operation != 0 && (!MacObjC.RespondsTo(view, "prepareForDragOperation:") || MacObjC.SendBool(view, "prepareForDragOperation:", info))
                && MacObjC.SendBool(view, "performDragOperation:", info))
            {
                if (MacObjC.RespondsTo(view, "concludeDragOperation:")) MacObjC.SendVoid(view, "concludeDragOperation:", info);
            }
            else MacObjC.SendVoid(view, "draggingExited:", info);
            MacObjC.Release(info);
        }

        private unsafe nint HitTest(CGPoint at) =>
            ((delegate* unmanaged<nint, nint, CGPoint, nint>)MacObjC.MsgSend)(MacObjC.Send(NsWindow, "contentView"), MacObjC.Sel("hitTest:"), at);

        private static unsafe nuint Destination(nint view, string selector, nint info) =>
            ((delegate* unmanaged<nint, nint, nint, nuint>)MacObjC.MsgSend)(view, MacObjC.Sel(selector), info);

        // The drag DropFile's NSDraggingInfo stands for.
        private static CGPoint _dropAt;
        private static nint _dropPasteboard, _dropWindow;

        private static unsafe nint DraggingInfoClass() => MacObjC.DefineClass("OmbraAcceptanceDraggingInfo", "NSDraggingInfo",
            ("draggingLocation", (nint)(delegate* unmanaged[Cdecl]<nint, nint, CGPoint>)&InfoLocation, "{CGPoint=dd}@:"),
            ("draggedImageLocation", (nint)(delegate* unmanaged[Cdecl]<nint, nint, CGPoint>)&InfoLocation, "{CGPoint=dd}@:"),
            ("draggingSourceOperationMask", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nuint>)&InfoOperations, "Q@:"),
            ("draggingPasteboard", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&InfoPasteboard, "@@:"),
            ("draggingDestinationWindow", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&InfoWindow, "@@:"),
            ("draggingSource", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&InfoNone, "@@:"),
            ("draggedImage", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&InfoNone, "@@:"),
            ("draggingSequenceNumber", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nuint>)&InfoOne, "q@:"),
            ("numberOfValidItemsForDrop", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nuint>)&InfoOne, "q@:"));

        [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static CGPoint InfoLocation(nint self, nint cmd) => _dropAt;

        [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static nuint InfoOperations(nint self, nint cmd) => 1 | 2 | 4 | 16; // copy, link, generic, move: Finder's

        [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static nint InfoPasteboard(nint self, nint cmd) => _dropPasteboard;

        [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static nint InfoWindow(nint self, nint cmd) => _dropWindow;

        [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static nint InfoNone(nint self, nint cmd) => 0;

        [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static nuint InfoOne(nint self, nint cmd) => 1;

        /// <summary>
        /// The driver's mouse drags in the browser window (points from its pane's top-left, as <c>click</c>):
        /// <c>down X Y [MODS]</c>, <c>move X Y [MODS]</c> (with the button held), <c>up X Y [MODS]</c>, and
        /// <c>drag X1 Y1 X2 Y2 [STEPS] [MODS]</c>: a press, STEPS (default 16) moves about a frame apart, a rest over the
        /// target and the release there, as a hand drags.
        /// </summary>
        private async Task DragCommand(string line, NightlyPane pane)
        {
            var a = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double D(int i) => double.Parse(a[i], CultureInfo.InvariantCulture);
            ulong Mods(int i) => a.Length > i && !char.IsDigit(a[i][0]) ? Modifiers(a[i]) : 0;
            CGPoint At(double x, double y) => ClientToWindow(pane.TranslatePoint(new Point(x, y), window) ?? default);
            switch (a[0])
            {
                case "down":
                    Post(DragMouseEvent(1, At(D(1), D(2)), Mods(3)));
                    break;
                case "move":
                    Post(DragMouseEvent(6, At(D(1), D(2)), Mods(3)));
                    break;
                case "up":
                    Post(DragMouseEvent(2, At(D(1), D(2)), Mods(3)));
                    break;
                case "drag":
                    await Drag(At, D(1), D(2), D(3), D(4), a.Length > 5 && char.IsDigit(a[5][0]) ? int.Parse(a[5], CultureInfo.InvariantCulture) : 16,
                        a.Length > 5 && !char.IsDigit(a[5][0]) ? Modifiers(a[5]) : Mods(6));
                    break;
            }
        }

        private async Task Drag(Func<double, double, CGPoint> at, double x1, double y1, double x2, double y2, int steps, ulong flags,
            Func<Task>? beforeRelease = null)
        {
            Post(MouseEvent(5, at(x1, y1), 0));
            Post(DragMouseEvent(1, at(x1, y1), flags));
            await Task.Delay(80);
            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps;
                Post(DragMouseEvent(6, at(x1 + (x2 - x1) * t, y1 + (y2 - y1) * t), flags));
                await Task.Delay(16);
            }
            // A hand rests over the target before letting go; drop targets that open on hover (folders) get their time.
            for (var i = 0; i < 6; i++)
            {
                Post(DragMouseEvent(6, at(x2, y2), flags));
                await Task.Delay(50);
            }
            if (beforeRelease is not null) await beforeRelease();
            Post(DragMouseEvent(2, at(x2, y2), flags));
            await Task.Delay(100);
        }

        /// <summary>A left-button mouse event (down 1, up 2, dragged 6) with <paramref name="flags"/>, pressed while held.</summary>
        private unsafe nint DragMouseEvent(ulong type, CGPoint at, ulong flags) =>
            ((delegate* unmanaged<nint, nint, ulong, CGPoint, ulong, double, long, nint, long, long, float, nint>)MacObjC.MsgSend)(
                MacObjC.Cls("NSEvent"), MacObjC.Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
                type, at, flags, Uptime(), MacObjC.SendLong(NsWindow, "windowNumber"), 0, 0, type == 6 ? 0 : 1, type == 2 ? 0f : 1f);
    }
}
