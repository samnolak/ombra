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
        /// Zen's spaces in the Browser page's Nightly window, driven as a user would, from the only space: a two-finger
        /// swipe to the left over the sidebar past the last space opens "Create a Space" (the content dark: the new
        /// space's empty tab); its name is typed with Shift only (⇧W ⇧O ⇧R ⇧K) and Enter creates it, then Esc (a form
        /// still open, as when the Shift keys were lost and the name stayed empty, is cancelled). Back in the first space
        /// (⌃1) a link opens the white fixture page there; ⌃2 / ⌃1, ⌥⌘→ / ⌥⌘← and swipes to the left and right then go
        /// to the dark space and back to the white one. Swipes need the browser window's pan gestures to be eligible for
        /// swipes (ZenEmbedHost's Wheel); the name, NightlyPane's ⇧ path.
        /// </summary>
        private async Task SpacesChecks(NightlyPane pane)
        {
            double luma = 0;
            bool White() => (luma = ContentLuma(pane)) > 240;
            bool Dark() => (luma = ContentLuma(pane)) is >= 0 and < 120;
            const ulong ctrl = 1UL << 18, cmdAlt = (1UL << 20) | (1UL << 19);
            Activate();
            await Until(() => window.IsActive, 5);
            var sidebar = new Point(120, pane.Bounds.Height * 0.45);
            MouseClickPane(pane, new Point(pane.Bounds.Width * 0.7, pane.Bounds.Height * 0.6));
            await Task.Delay(300);

            Swipe(pane, sidebar, -1);
            var form = await Until(Dark, 10);
            Check("browser-window-space-swipe-create", form, $"content luma={luma:0}");
            await Task.Delay(500);
            Shot("p-browser-window-space-create");
            MouseClickPane(pane, new Point(150, 222)); // the form's name field
            await Task.Delay(300);
            foreach (var (ch, code) in new[] { ("W", (ushort)0x0D), ("O", (ushort)0x1F), ("R", (ushort)0x0F), ("K", (ushort)0x28) })
                KeyPress(ch, code, 1UL << 17);
            await Task.Delay(200);
            KeyPress("\r", 0x24, 0);
            await Task.Delay(1200);
            KeyPress("\u001b", 0x35, 0);
            await Task.Delay(500);

            KeyPress("1", 0x12, ctrl);
            await Task.Delay(1000);
            vm.OpenInBrowserWindow?.Invoke(new Uri(new Uri(url), "cutover.html"));
            var first = await Until(White, 20);
            Check("browser-window-space-first", first, $"content luma={luma:0}");

            var detail = "";
            var switched = true;
            foreach (var (name, chars, code, flags, white) in new[]
            {
                ("ctrl+2", "2", (ushort)0x13, ctrl, false), ("ctrl+1", "1", (ushort)0x12, ctrl, true),
                ("cmd+alt+right", "\uf703", (ushort)0x7C, cmdAlt, false), ("cmd+alt+left", "\uf702", (ushort)0x7B, cmdAlt, true),
            })
            {
                KeyPress(chars, code, flags);
                var ok = await Until(white ? White : Dark, 5);
                detail += $"{name}: luma={luma:0} {(ok ? "ok" : "FAIL")}; ";
                switched &= ok;
                if (!white) Shot("p-browser-window-space-second");
            }
            Check("browser-window-space-keys", switched, detail);
            // A swipe while the last switch still animates is dropped by Zen: the luma changes before the switch ends.
            await Task.Delay(1000);

            Swipe(pane, sidebar, -1);
            var next = await Until(Dark, 5);
            detail = $"left: luma={luma:0}; ";
            await Task.Delay(1000);
            Swipe(pane, sidebar, 1);
            var back = await Until(White, 5);
            Check("browser-window-space-swipe", next && back, detail + $"right: luma={luma:0}");
        }

        private void MouseClickPane(NightlyPane pane, Point at) =>
            MouseClick(ClientToWindow(pane.TranslatePoint(at, window) ?? default), right: false, clicks: 1);

        /// <summary>A horizontal two-finger swipe at a pane point: began, 24 changes of 30 px, ended. <paramref name="direction"/>
        /// −1: the fingers move left (the next space), 1: right.</summary>
        private unsafe void Swipe(NightlyPane pane, Point at, int direction)
        {
            var point = ClientToWindow(pane.TranslatePoint(at, window) ?? default);
            var screen = ((delegate* unmanaged<nint, nint, CGPoint, CGPoint>)MacObjC.MsgSend)(NsWindow, MacObjC.Sel("convertPointToScreen:"), point);
            var number = MacObjC.SendLong(NsWindow, "windowNumber");
            void Wheel(long phase, int delta)
            {
                var cg = CGEventCreateScrollWheelEvent2(0, 0 /* pixels */, 2, 0, delta, 0);
                try
                {
                    CGEventSetLocation(cg, new CGPoint(screen.X, PrimaryScreenHeight() - screen.Y));
                    CGEventSetIntegerValueField(cg, 99 /* kCGScrollWheelEventScrollPhase */, phase);
                    CGEventSetIntegerValueField(cg, 91, number);
                    CGEventSetIntegerValueField(cg, 92, number);
                    var ev = MacObjC.Send(MacObjC.Cls("NSEvent"), "eventWithCGEvent:", cg);
                    if (MacObjC.RespondsTo(ev, "_eventRelativeToWindow:") && MacObjC.Send(ev, "_eventRelativeToWindow:", NsWindow) is var relative and not 0)
                        ev = relative;
                    MacObjC.SendVoid(App, "sendEvent:", ev);
                }
                finally
                {
                    NightlyMach.CFRelease(cg);
                }
            }
            // As a trackpad's: the gesture begins moving (Gecko's swipe tracker decides on the began event's direction).
            Wheel(1, 30 * direction);
            for (var i = 0; i < 24; i++) Wheel(2, 30 * direction);
            Wheel(4, 0);
            Log($"swipe {(direction < 0 ? "left" : "right")} at {at}");
        }
    }
}
