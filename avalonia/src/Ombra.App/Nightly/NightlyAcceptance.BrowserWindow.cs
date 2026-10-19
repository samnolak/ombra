using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.App.Platform.Mac;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Nightly;

internal static partial class NightlyAcceptance
{
    /// <summary>With it set to a file, the run carries out that file's commands (one per line, read as they are appended)
    /// instead of the scripted checks: the Browser page's Nightly window driven step by step (<see cref="Run.Drive"/>).</summary>
    public const string DriveVariable = "OMBRA_NIGHTLY_ACCEPTANCE_DRIVE";

    private sealed partial class Run
    {
        private NightlyPane? BrowserWindow() => Panes().FirstOrDefault(p => p.IsBrowserWindow);

        /// <summary>
        /// The Browser page shows one Nightly browser window (the whole browser chrome) on the runtime the agent's pane
        /// uses, the chats sidebar and the built-in preview's tab strip hidden, and nothing else draws a page there: one
        /// visible pane, its frame the pane's size 1:1.
        /// </summary>
        private async Task<NightlyPane?> OpenBrowserWindow(string shot, int agentPid)
        {
            vm.OpenBrowserPageCommand.Execute(null);
            var shown = await Until(() => BrowserWindow() is { FramesShown: > 0, IsEffectivelyVisible: true } p && Matches(p), 60);
            var pane = BrowserWindow();
            var visible = Panes().Where(p => p.IsEffectivelyVisible).ToList();
            var sidebar = Named<Border>("Sidebar") is not null;
            var strip = window.GetVisualDescendants().OfType<DockPanel>().Any(d => d.Classes.Contains("preview-tab-strip") && d.IsEffectivelyVisible);
            var layout = pane?.FrameLayout;
            Check("browser-window", shown && visible.Count == 1 && visible[0] == pane && !sidebar && !strip
                && (agentPid == 0 || pane!.RuntimePid == agentPid),
                $"panes={Panes().Count} visible={visible.Count} frame={layout?.Width}x{layout?.Height} at {layout?.Shown} pane={pane?.Bounds.Size} "
                + $"sidebar={sidebar} strip={strip} pid={pane?.RuntimePid} agent pid={agentPid}");
            await Task.Delay(600);
            ScreenShot(shot);
            return pane;
        }

        /// <summary>
        /// The Browser page's Nightly window, driven as a user would: it is the page's sheet from the window's top (its
        /// corners the sheet's), a link opened for it is a tab of its own (the white fixture page in its content), a
        /// right click shows Nightly's context menu as a real window over the pane where the pointer is, which Esc and a
        /// click elsewhere close, ⌘T opens Nightly's new tab (its URL bar floating over the content) and ⌘W closes it
        /// again, while Ombra's own menu items (⌘W is one) leave the keys to it; switching Chats ↔ Browser and resizing
        /// never leaves a second copy of a page on screen (the agent's pane, open in the right panel, hides meanwhile).
        /// Returns to the chats.
        /// </summary>
        private async Task BrowserWindowChecks(int agentPid)
        {
            if (await OpenBrowserWindow("p-browser-window", agentPid) is not { } pane) return;
            var host = Named<Border>("BrowserWindowHost");
            var at = pane.TranslatePoint(default, window) ?? new Point(-1, -1);
            Check("browser-window-sheet", host is not null && at.Y == 0 && host.CornerRadius.TopRight > 0
                && host.CornerRadius == (CornerRadius)window.FindResource("GuiSheetRadius")! && host.ClipToBounds,
                $"pane at {at} size={pane.Bounds.Size} radius={host?.CornerRadius}");

            // A link for the window (the Browser page's OpenInBrowserWindow): a new tab with the white fixture page.
            var cutover = new Uri(new Uri(url), "cutover.html");
            vm.OpenInBrowserWindow?.Invoke(cutover);
            double content = 0;
            var white = await Until(() => (content = ContentLuma(pane)) > 240, 30);
            Check("browser-window-open", white, $"content luma={content:0}");
            Activate();
            await Until(() => window.IsActive, 5);

            // A context menu: a real Nightly window at the pointer, above Ombra's window; Esc and a click elsewhere close it.
            var centre = new Point(pane.Bounds.Width * 0.65, pane.Bounds.Height * 0.6);
            var screen = PaneScreenPoint(pane, centre);
            MouseClick(ClientToWindow(pane.TranslatePoint(centre, window) ?? default), right: true, clicks: 1);
            List<CGRect> menus = [];
            var shown = await Until(() => (menus = NightlyWindowRects(pane.RuntimePid)).Count > 0, 10);
            var near = menus.Any(r => Math.Abs(r.X - screen.X) <= 24 && Math.Abs(r.Y - screen.Y) <= 24 && r.Width > 80 && r.Height > 80);
            Check("browser-window-popup", shown && near && NightlyAbove(pane.RuntimePid),
                $"pointer {screen.X:0},{screen.Y:0} menus={string.Join(" ", menus.Select(r => $"{r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}"))}");
            await Task.Delay(300);
            ScreenShot("p-browser-window-menu");
            KeyPress("\u001b", 0x35, 0);
            Check("browser-window-popup-esc", await Until(() => NightlyWindowRects(pane.RuntimePid).Count == 0, 5), "");
            MouseClick(ClientToWindow(pane.TranslatePoint(centre, window) ?? default), right: true, clicks: 1);
            await Until(() => NightlyWindowRects(pane.RuntimePid).Count > 0, 10);
            MouseClick(ClientToWindow(pane.TranslatePoint(new Point(pane.Bounds.Width - 40, pane.Bounds.Height - 40), window) ?? default), right: false, clicks: 1);
            Check("browser-window-popup-outside", await Until(() => NightlyWindowRects(pane.RuntimePid).Count == 0, 5), "");

            // ⌘T: Nightly's new tab, its URL bar floating over the content (dark across the content's upper middle);
            // ⌘W closes that empty tab again, back to the white page.
            KeyPress("t", 0x11, 1UL << 20);
            var newTab = await Until(() => (content = ContentLuma(pane, UrlBarBand)) < 120, 10);
            Check("browser-window-cmd-t", newTab, $"url bar band luma={content:0}");
            await Task.Delay(300);
            ScreenShot("p-browser-window-cmd-t");
            KeyPress("w", 0x0D, 1UL << 20);
            var back = await Until(() => (content = ContentLuma(pane, UrlBarBand)) > 240, 10);
            Check("browser-window-cmd-w", back && Panes().Contains(pane) && window.IsVisible, $"url bar band luma={content:0}");

            // Chats ↔ Browser and a resize: one copy of the page, the window's frame the pane's.
            var single = true;
            var detail = "";
            foreach (var step in new[] { "chats", "browser", "resize", "chats", "browser" })
            {
                if (step == "chats") vm.ShowChatsCommand.Execute(null);
                else if (step == "browser") vm.OpenBrowserPageCommand.Execute(null);
                else window.Width -= 160;
                await Task.Delay(800);
                var visible = Panes().Where(p => p.IsEffectivelyVisible).ToList();
                var browsers = Panes().Count(p => p.IsBrowserWindow);
                var ok = browsers == 1 && (step == "chats" ? !pane.IsEffectivelyVisible : visible.Count == 1 && visible[0] == pane && await Until(() => Matches(pane), 10));
                detail += $"{step}: visible={visible.Count} browser windows={browsers} matches={Matches(pane)}; ";
                single &= ok;
            }
            window.Width += 160;
            Check("browser-window-single", single, detail);
            await Task.Delay(600);
            Shot("p-browser-window-after");
            vm.ShowChatsCommand.Execute(null);
            Check("browser-window-hidden", await Until(() => !pane.IsEffectivelyVisible && Named<Border>("Sidebar") is not null, 10)
                && BrowserWindow() == pane, $"panes={Panes().Count}");
        }

        /// <summary>The content's middle, right of the window's sidebar (fractions of the pane).</summary>
        private static readonly Rect ContentMiddle = new(0.55, 0.4, 0.3, 0.2);

        /// <summary>Where Nightly's floating URL bar of a new tab sits: across the content's upper middle.</summary>
        private static readonly Rect UrlBarBand = new(0.45, 0.33, 0.3, 0.03);

        /// <summary>The average luma (0–255) of <paramref name="region"/> (fractions of the pane; default
        /// <see cref="ContentMiddle"/>), from a shot of Ombra's window.</summary>
        private unsafe double ContentLuma(NightlyPane pane, Rect? region = null)
        {
            var r = region ?? ContentMiddle;
            var file = Shot("probe");
            var s = window.RenderScaling;
            var topLeft = pane.TranslatePoint(new Point(pane.Bounds.Width * r.X, pane.Bounds.Height * r.Y), window) ?? default;
            int x0 = (int)Math.Round(topLeft.X * s), y0 = (int)Math.Round(topLeft.Y * s), w = (int)(pane.Bounds.Width * r.Width * s), h = (int)(pane.Bounds.Height * r.Height * s);
            using var bitmap = new Avalonia.Media.Imaging.Bitmap(file);
            var size = bitmap.PixelSize;
            if (x0 < 0 || y0 < 0 || x0 + w > size.Width || y0 + h > size.Height) return -1;
            var stride = size.Width * 4;
            var pixels = new byte[stride * size.Height];
            fixed (byte* buffer = pixels) bitmap.CopyPixels(new PixelRect(size), (nint)buffer, pixels.Length, stride);
            long sum = 0;
            for (var y = y0; y < y0 + h; y++)
            for (var x = x0; x < x0 + w; x++)
            {
                var o = y * stride + x * 4;
                sum += (pixels[o] + pixels[o + 1] + pixels[o + 2]) / 3;
            }
            return (double)sum / (w * h);
        }

        /// <summary>A point of the pane on screen (points, top-left origin, as CGWindowList reports windows).</summary>
        private Point PaneScreenPoint(NightlyPane pane, Point p)
        {
            var inWindow = pane.TranslatePoint(p, window) ?? default;
            var bottomLeft = NightlyCocoa.ScreenBottomLeft(NsWindow, inWindow.X, inWindow.Y, 0, 0) ?? default;
            return new Point(bottomLeft.X, PrimaryScreenHeight() - bottomLeft.Y);
        }

        /// <summary>Nightly's windows on screen (its popups), top-left origin.</summary>
        private static List<CGRect> NightlyWindowRects(int pid)
        {
            var rects = new List<CGRect>();
            var list = CGWindowListCopyWindowInfo(1, 0);
            try
            {
                using var pidKey = new NSStr("kCGWindowOwnerPID");
                using var boundsKey = new NSStr("kCGWindowBounds");
                foreach (var info in Items(list))
                {
                    if (Int(CFDictionaryGetValue(info, pidKey.Handle)) != pid) continue;
                    CGRectMakeWithDictionaryRepresentation(CFDictionaryGetValue(info, boundsKey.Handle), out var b);
                    rects.Add(b);
                }
            }
            finally
            {
                NightlyMach.CFRelease(list);
            }
            return rects;
        }

        /// <summary>Nightly's first on-screen window comes before (above) Ombra's in the window list.</summary>
        private bool NightlyAbove(int pid)
        {
            var list = CGWindowListCopyWindowInfo(1, 0);
            try
            {
                using var pidKey = new NSStr("kCGWindowOwnerPID");
                using var numberKey = new NSStr("kCGWindowNumber");
                var ombra = MacObjC.SendLong(NsWindow, "windowNumber");
                foreach (var info in Items(list))
                {
                    if (Int(CFDictionaryGetValue(info, pidKey.Handle)) == pid) return true;
                    if (Int(CFDictionaryGetValue(info, numberKey.Handle)) == ombra) return false;
                }
                return false;
            }
            finally
            {
                NightlyMach.CFRelease(list);
            }
        }

        // ── the driver: OMBRA_NIGHTLY_ACCEPTANCE_DRIVE ──

        /// <summary>
        /// Commands, one per line: <c>browser</c> / <c>chats</c> (the rail), <c>preview [URL]</c> (the agent's pane in the
        /// right panel), <c>size W H</c>, <c>theme dark|light|system</c>,
        /// <c>click X Y</c>, <c>rclick X Y</c>, <c>dclick X Y</c>, <c>hover X Y</c> (points from the browser window pane's
        /// top-left), <c>key MODS CODE CHARS</c> (MODS: <c>-</c> or a <c>+</c>-joined list of cmd, shift, ctrl, alt; CODE the
        /// macOS key code in hex), <c>type TEXT</c>, <c>open URL</c> (a new tab of the window), <c>shot NAME</c> (the screen
        /// under the window, popups included), <c>wshot NAME</c> (the window alone), <c>state</c>, <c>windows</c> (Nightly's
        /// windows on screen), <c>wait MS</c>, <c>quit</c>. Each is logged; <c>DONE n</c> after each.
        /// </summary>
        private async Task Drive(string file)
        {
            window.Position = new PixelPoint(60, 60);
            window.Width = 1320;
            window.Height = 860;
            await Task.Delay(500);
            Activate();
            long offset = 0;
            var n = 0;
            var pending = "";
            while (true)
            {
                string text;
                try
                {
                    await using var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
                    stream.Seek(offset, SeekOrigin.Begin);
                    using var reader = new StreamReader(stream);
                    text = await reader.ReadToEndAsync();
                    offset = stream.Position;
                }
                catch (IOException)
                {
                    text = "";
                }
                pending += text;
                int newline;
                while ((newline = pending.IndexOf('\n')) >= 0)
                {
                    var line = pending[..newline].Trim();
                    pending = pending[(newline + 1)..];
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    Log($"DRIVE {line}");
                    if (line == "quit") return;
                    try
                    {
                        await Command(line);
                    }
                    catch (Exception e)
                    {
                        Log($"DRIVE error {e.Message}");
                    }
                    Log($"DONE {++n}");
                }
                await Task.Delay(100);
            }
        }

        private async Task Command(string line)
        {
            var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            double D(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
            var pane = BrowserWindow();
            switch (parts[0])
            {
                case "browser":
                    await OpenBrowserWindow("browser", Shown() is { IsBrowserWindow: false } a ? a.RuntimePid : 0);
                    break;
                case "chats":
                    vm.ShowChatsCommand.Execute(null);
                    await Task.Delay(400);
                    break;
                case "preview":
                    vm.IsPreviewOpen = true;
                    if (parts.Length > 1) vm.Preview.NavigateCommand.Execute(parts[1]);
                    await Task.Delay(400);
                    break;
                case "size":
                    window.Width = D(1);
                    window.Height = D(2);
                    await Task.Delay(600);
                    break;
                case "theme":
                    vm.SetThemeCommand.Execute(parts[1]);
                    await Task.Delay(600);
                    break;
                case "click" or "rclick" or "dclick" or "hover" when pane is not null:
                    var at = ClientToWindow(pane.TranslatePoint(new Point(D(1), D(2)), window) ?? default);
                    if (parts[0] == "hover") Hover(at);
                    else MouseClick(at, right: parts[0] == "rclick", clicks: parts[0] == "dclick" ? 2 : 1);
                    break;
                case "key":
                    KeyPress(parts.Length > 3 ? parts[3].Replace("\\t", "\t", StringComparison.Ordinal) : "", Convert.ToUInt16(parts[2], 16), Modifiers(parts[1]));
                    break;
                case "type":
                    foreach (var ch in line[5..]) KeyPress(ch.ToString(), CharKeyCode(ch), 0);
                    break;
                case "open" when vm.OpenInBrowserWindow is { } open:
                    open(new Uri(parts[1]));
                    break;
                case "shot":
                    ScreenShot(parts[1]);
                    break;
                case "wshot":
                    Shot(parts[1]);
                    break;
                case "state":
                    LogState();
                    break;
                case "windows":
                    LogNightlyWindows(pane?.RuntimePid ?? 0);
                    break;
                case "activate":
                    Activate();
                    await Task.Delay(300);
                    break;
                case "wait":
                    await Task.Delay((int)D(1));
                    break;
                default:
                    Log($"DRIVE unknown or not possible now: {line}");
                    break;
            }
        }

        private void LogState()
        {
            var visible = Panes().Where(p => p.IsEffectivelyVisible).ToList();
            Log($"state page={vm.ShowBrowserPage} sidebar={vm.ShowSidebar} useNightly={vm.Browser.UseNightly} active={window.IsActive} panes={Panes().Count} "
                + $"visible={visible.Count} focused={window.FocusManager?.GetFocusedElement()?.GetType().Name}");
            foreach (var p in Panes())
                Log($"  pane browser={p.IsBrowserWindow} visible={p.IsEffectivelyVisible} window={p.WindowId} frames={p.FramesShown} "
                    + $"layout={p.FrameLayout} bounds={p.Bounds} matches={Matches(p)} pid={p.RuntimePid}");
            Log($"  responder={Cls(MacObjC.Send(NsWindow, "firstResponder"))} views={string.Join(",", Views(MacObjC.Send(NsWindow, "contentView"), 0))}");
        }

        private static string Cls(nint o) => o == 0 ? "nil" : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(object_getClassName(o)) ?? "?";

        [DllImport("/usr/lib/libobjc.A.dylib")]
        private static extern nint object_getClassName(nint o);

        private static IEnumerable<string> Views(nint view, int depth)
        {
            yield return new string('>', depth) + Cls(view);
            var subs = MacObjC.Send(view, "subviews");
            for (long i = 0, n = MacObjC.SendLong(subs, "count"); i < n; i++)
                foreach (var s in Views(MacObjC.SendIndex(subs, "objectAtIndex:", (nuint)i), depth + 1)) yield return s;
        }

        private static ulong Modifiers(string mods)
        {
            ulong flags = 0;
            foreach (var m in mods.Split('+'))
                flags |= m switch { "cmd" => 1UL << 20, "shift" => 1UL << 17, "ctrl" => 1UL << 18, "alt" => 1UL << 19, _ => 0 };
            return flags;
        }

        private static ushort CharKeyCode(char c) => char.ToLowerInvariant(c) switch
        {
            'a' => 0x00, 's' => 0x01, 'd' => 0x02, 'f' => 0x03, 'h' => 0x04, 'g' => 0x05, 'z' => 0x06, 'x' => 0x07, 'c' => 0x08,
            'v' => 0x09, 'b' => 0x0B, 'q' => 0x0C, 'w' => 0x0D, 'e' => 0x0E, 'r' => 0x0F, 'y' => 0x10, 't' => 0x11, '1' => 0x12,
            '2' => 0x13, '3' => 0x14, '4' => 0x15, '6' => 0x16, '5' => 0x17, '9' => 0x19, '7' => 0x1A, '8' => 0x1C, '0' => 0x1D,
            'o' => 0x1F, 'u' => 0x20, 'i' => 0x22, 'p' => 0x23, 'l' => 0x25, 'j' => 0x26, 'k' => 0x28, 'n' => 0x2D, 'm' => 0x2E,
            '.' => 0x2F, ' ' => 0x31, '/' => 0x2C, ':' => 0x29, '-' => 0x1B, _ => 0x31,
        };

        private void MouseClick(CGPoint at, bool right, int clicks)
        {
            Post(MouseEvent(5, at, 0));
            for (var i = 1; i <= clicks; i++)
            {
                Post(MouseEvent(right ? 3UL : 1UL, at, i));
                Post(MouseEvent(right ? 4UL : 2UL, at, i));
            }
        }

        private unsafe void Hover(CGPoint at)
        {
            var view = ((delegate* unmanaged<nint, nint, CGPoint, nint>)MacObjC.MsgSend)(MacObjC.Send(NsWindow, "contentView"), MacObjC.Sel("hitTest:"), at);
            MacObjC.SendVoid(view, "mouseMoved:", MouseEvent(5, at, 0));
        }

        /// <summary>A key down and up with <paramref name="flags"/> (NSEventModifierFlags), posted to the app's queue.</summary>
        private unsafe void KeyPress(string characters, ushort keyCode, ulong flags)
        {
            var unmodified = characters.ToLowerInvariant();
            foreach (var type in new ulong[] { 10, 11 })
            {
                using var chars = new NSStr(characters);
                using var plain = new NSStr(unmodified);
                var ev = ((delegate* unmanaged<nint, nint, ulong, CGPoint, ulong, double, long, nint, nint, nint, byte, ushort, nint>)MacObjC.MsgSend)(
                    MacObjC.Cls("NSEvent"), MacObjC.Sel("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
                    type, default, flags, Uptime(), MacObjC.SendLong(NsWindow, "windowNumber"), 0, chars.Handle, plain.Handle, 0, keyCode);
                Post(ev);
            }
        }

        // ── Nightly's windows on screen (its popups): CGWindowListCopyWindowInfo ──

        /// <summary>Logs every on-screen window of <paramref name="pid"/> (bounds in screen points, top-left origin, window
        /// layer) next to Ombra's window and the browser pane; returns how many there are.</summary>
        private unsafe int LogNightlyWindows(int pid)
        {
            var frame = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(NsWindow, MacObjC.Sel("frame"));
            var primary = PrimaryScreenHeight();
            Log($"ombra window {frame.X},{primary - frame.Y - frame.Height} {frame.Width}x{frame.Height} number={MacObjC.SendLong(NsWindow, "windowNumber")}");
            if (BrowserWindow() is { } pane && pane.TranslatePoint(default, window) is { } topLeft
                && NightlyCocoa.ScreenBottomLeft(NsWindow, topLeft.X, topLeft.Y, pane.Bounds.Width, pane.Bounds.Height) is { } bottomLeft)
                Log($"browser pane {bottomLeft.X},{primary - bottomLeft.Y - pane.Bounds.Height} {pane.Bounds.Width}x{pane.Bounds.Height}");
            var list = CGWindowListCopyWindowInfo(1 /* on screen only */, 0);
            var count = 0;
            try
            {
                using var pidKey = new NSStr("kCGWindowOwnerPID");
                using var boundsKey = new NSStr("kCGWindowBounds");
                using var layerKey = new NSStr("kCGWindowLayer");
                using var numberKey = new NSStr("kCGWindowNumber");
                var order = 0;
                foreach (var info in Items(list))
                {
                    order++;
                    if (Int(CFDictionaryGetValue(info, pidKey.Handle)) != pid) continue;
                    CGRectMakeWithDictionaryRepresentation(CFDictionaryGetValue(info, boundsKey.Handle), out var b);
                    Log($"nightly window {Int(CFDictionaryGetValue(info, numberKey.Handle))} order={order} layer={Int(CFDictionaryGetValue(info, layerKey.Handle))} "
                        + $"{b.X},{b.Y} {b.Width}x{b.Height}");
                    count++;
                }
            }
            finally
            {
                NightlyMach.CFRelease(list);
            }
            Log($"nightly windows on screen: {count}");
            return count;
        }

        private static IEnumerable<nint> Items(nint array)
        {
            for (long i = 0, n = CFArrayGetCount(array); i < n; i++) yield return CFArrayGetValueAtIndex(array, i);
        }

        private static int Int(nint number)
        {
            if (number == 0) return -1;
            CFNumberGetValue(number, 9 /* kCFNumberIntType */, out var v);
            return v;
        }

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern bool CGRectMakeWithDictionaryRepresentation(nint dict, out CGRect rect);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern long CFArrayGetCount(nint array);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern nint CFArrayGetValueAtIndex(nint array, long index);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern nint CFDictionaryGetValue(nint dict, nint key);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern bool CFNumberGetValue(nint number, int type, out int value);
    }
}
