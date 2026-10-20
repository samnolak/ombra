#if OMBRA_FEATURE_SMOKE
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Ombra.App.Controls;
using Avalonia.VisualTree;
using Ombra.App.Platform.Mac;
using Ombra.App.Views;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Smoke;

internal static partial class FeatureSmoke
{
    private sealed partial class Run
    {
        private (string, Func<Task>)[] TitleBarSections() => [("title-bar", TitleBar)];

        /// <summary>
        /// The traffic lights and the band's row share one vertical centre (the Codex app's chrome). In the real window:
        /// the lights in the middle of the band (AppKit's frames), every icon and text in the band on their middle and clear
        /// of them (Avalonia's layout), and in a <c>screencapture -l</c> shot the lights' circles against the band's panel,
        /// search, ×, + and back icons, ±1 px — wide and narrow, light and dark, sidebar shown and hidden, after AppKit
        /// lays its title bar out again (a theme change, a new window title, a resize, a full-screen round trip), on the
        /// conversation and the Settings, Team and Browser pages.
        /// </summary>
        private async Task TitleBar()
        {
            window.Position = Platform.TestFocus.Position(window, new PixelPoint(40, 40));
            window.Width = 1360;
            window.Height = 880;
            SetSidebar(true);
            Activate();
            foreach (var theme in new[] { "dark", "light" })
            {
                vm.SettingsTheme = theme;
                await TitleBarCase($"wide-{theme}");
                SetSidebar(false);
                await TitleBarCase($"wide-no-sidebar-{theme}");
                SetSidebar(true);
            }
            // A new window title (the attention dot): AppKit lays the title bar out again
            vm.NeedsAttention = true;
            await TitleBarCase("title-changed-light");
            vm.NeedsAttention = false;
            vm.SettingsTheme = "dark";
            vm.OpenSettingsFromRailCommand.Execute(null);
            await TitleBarCase("settings-dark");
            vm.CloseSettingsCommand.Execute(null);
            // The Team and Browser pages have no chats sidebar (nor ⌘B): one case each, the page's sheet after the rail
            vm.OpenTeamPageCommand.Execute(null);
            Check("title-bar-team-no-sidebar", !vm.ShowSidebar && !vm.ToggleSidebarCommand.CanExecute(null));
            await TitleBarCase("team-dark");
            vm.OpenBrowserPageCommand.Execute(null);
            Check("title-bar-browser-no-sidebar", !vm.ShowSidebar && !vm.ToggleSidebarCommand.CanExecute(null));
            await TitleBarCase("browser-dark");
            vm.SettingsTheme = "light";
            await TitleBarCase("browser-light");
            vm.ShowChatsCommand.Execute(null);

            window.Width = 700;
            await TitleBarCase("narrow-light");
            vm.SettingsTheme = "dark";
            await TitleBarCase("narrow-dark");
            vm.OpenSettingsFromRailCommand.Execute(null);
            await TitleBarCase("narrow-settings-dark");
            vm.CloseSettingsCommand.Execute(null);
            window.Width = 1360;
            SetSidebar(true);
            await TitleBarCase("wide-again-dark");
            // Full screen hides the lights in a title bar of its own; back out of it they are in the band again
            window.WindowState = WindowState.FullScreen;
            await Task.Delay(2500);
            var styleMask = MacObjC.SendNUInt(NsWindow, "styleMask");
            Check("title-bar-full-screen", (styleMask & (1 << 14)) != 0 && !((MainWindow)window).TrafficLightsShown,
                $"styleMask=0x{styleMask:x} lights shown={((MainWindow)window).TrafficLightsShown}");
            window.WindowState = WindowState.Normal;
            await Task.Delay(2500);
            await TitleBarCase("after-full-screen-dark");
        }

        private void SetSidebar(bool shown)
        {
            if (vm.ShowSidebar != shown) vm.ToggleSidebarCommand.Execute(null);
        }

        private async Task TitleBarCase(string name)
        {
            await Task.Delay(900);
            Activate();
            await Task.Delay(300);
            var w = NsWindow;
            var close = MacObjC.SendIndex(w, "standardWindowButton:", 0);
            var zoom = MacObjC.SendIndex(w, "standardWindowButton:", 2);
            var frame = NsFrame(w);
            var closeRect = InWindow(close, frame.Height);
            var lights = closeRect.Union(InWindow(zoom, frame.Height));
            var lightsMid = lights.Y + lights.Height / 2;
            var band = MainWindow.TitleBarHeight;
            Log($"title-bar {name}: window {frame.Width:0}x{frame.Height:0} lights {lights} " +
                $"titlebar-container {NsFrame(MacObjC.Send(MacObjC.Send(close, "superview"), "superview"))} button {NsFrame(close)}");
            Check($"title-bar-lights-{name}", Math.Abs(lightsMid - band / 2) <= 0.5 && Math.Abs(lights.X - MainWindow.TrafficLightsLeft) <= 0.5,
                $"lights {lights.X:0.#}–{lights.Right:0.#} × {lights.Y:0.#}–{lights.Bottom:0.#}, mid {lightsMid:0.##} (band {band})");

            // Every icon and text in the band: its middle on the lights' middle, and none under them (Avalonia's layout, points)
            var items = BandItems(band).ToList();
            var off = items.Select(c => (c, r: WindowRect(c))).Where(x => Math.Abs(x.r.Y + x.r.Height / 2 - lightsMid) > 0.5)
                .Select(x => $"{Describe(x.c)} mid={x.r.Y + x.r.Height / 2:0.##} ({x.r.Y:0.#}–{x.r.Bottom:0.#})").ToList();
            Check($"title-bar-row-{name}", off.Count == 0, $"lights mid={lightsMid:0.##} items={items.Count}: "
                + string.Join(", ", items.Select(Describe)) + (off.Count > 0 ? " | off: " + string.Join("; ", off) : ""));
            var under = items.Where(c => WindowRect(c) is var r && r.X < lights.Right + 4 && r.Right > lights.X).Select(Describe).ToList();
            Check($"title-bar-clear-{name}", under.Count == 0, under.Count > 0 ? "under the lights: " + string.Join(", ", under) : "");

            // The shot: the lights' circles on the band's middle, and the ink of every icon in the band drawn symmetric
            // top to bottom (the panel toggles, search, ×, +, back) on the lights' middle, in device pixels
            Shot($"90-title-bar-{name}");
            var file = Path.Combine(dir, $"90-title-bar-{name}.png");
            if (!File.Exists(file)) { Check($"title-bar-pixels-{name}", false, "no shot"); return; }
            var scale = SendDouble(w, "backingScaleFactor");
            using var shot = new Bitmap(file);
            var px = Pixels(shot);
            var lightRows = InkRows(px, lights, scale, Background(px, lights.Right + 30, 2, scale));
            var bandMid = band * scale / 2;
            var symmetric = new[] { "IconPanelLeft", "IconPanelRight", "IconSearch", "IconClose", "IconPlus", "IconArrowLeft" }
                .Select(k => window.FindResource(k)).Where(r => r is not null).ToList();
            var icons = items.OfType<Button>().Select(b => (b, i: b.GetVisualDescendants().OfType<Icon>().FirstOrDefault(i => symmetric.Contains(i.Data))))
                .Where(x => x.i is not null).ToList();
            var inks = icons.Select(x => (x.b, rows: InkRows(px, WindowRect(x.i!), scale, Background(px, WindowRect(x.i!).X - 4, WindowRect(x.i!).Y - 4, scale)))).ToList();
            var ok = lightRows is { } l && Math.Abs(l.Mid - bandMid) <= 1 && inks.All(x => x.rows is { } r && Math.Abs(r.Mid - l.Mid) <= 1);
            Check($"title-bar-pixels-{name}", ok,
                $"band mid {bandMid:0.#}px; lights rows {lightRows?.Top}–{lightRows?.Bottom} mid {lightRows?.Mid:0.#}px; icons: "
                + string.Join(", ", inks.Select(x => $"{Describe(x.b)} {x.rows?.Top}–{x.rows?.Bottom} mid {x.rows?.Mid:0.#}px")) + $" (scale {scale})");
        }

        /// <summary>The icons and texts shown in the band (a text or an icon inside a button counts as the button): visible,
        /// not clipped away, not under a page or a sheet over them.</summary>
        private IEnumerable<Control> BandItems(double band) =>
            window.GetVisualDescendants().OfType<Control>()
                .Where(c => c is Icon or TextBlock or TextBox && c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0)
                .Select(c => c.GetVisualAncestors().OfType<Button>().FirstOrDefault() is { } b && b.Bounds.Height <= band ? (Control)b : c)
                .Distinct()
                .Where(c => WindowRect(c) is var r && r.Y >= -0.5 && r.Bottom <= band + 0.5 && r.Right > 0 && r.X < window.Bounds.Width
                    && IsShownAt(c, r) && IsOnTop(c, r));

        /// <summary>Not clipped away by a scroller or a clipping parent.</summary>
        private bool IsShownAt(Control c, Rect r)
        {
            foreach (var a in c.GetVisualAncestors().OfType<Control>())
                if (a.ClipToBounds && a.TranslatePoint(default, window) is { } p && !new Rect(p, a.Bounds.Size).Intersects(r)) return false;
            return true;
        }

        /// <summary>What the pointer reaches at its middle is it, in it, or one it is in — not a page over it (the pet's
        /// layer, over the whole window, lets the pointer through).</summary>
        private bool IsOnTop(Control c, Rect r) =>
            window.InputHitTest(r.Center) is Visual top
            && (top == c || top.GetVisualAncestors().Contains(c) || c.GetVisualAncestors().Contains(top));

        private Rect WindowRect(Control c) => c.TranslatePoint(default, window) is { } p ? new Rect(p, c.Bounds.Size) : default;

        private static string Describe(Control c) =>
            $"{c.GetType().Name}#{c.Name ?? (c as TextBlock)?.Text ?? Avalonia.Automation.AutomationProperties.GetName(c)}";

        private static unsafe CGRect NsFrame(nint view) =>
            view == 0 ? default : ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(view, MacObjC.Sel("frame"));

        /// <summary>A view's rectangle in the window, top-left origin, in points.</summary>
        private static unsafe Rect InWindow(nint view, double windowHeight)
        {
            var bounds = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(view, MacObjC.Sel("bounds"));
            var r = ((delegate* unmanaged<nint, nint, CGRect, nint, CGRect>)MacObjC.MsgSend)(view, MacObjC.Sel("convertRect:toView:"), bounds, 0);
            return new Rect(r.X, windowHeight - r.Y - r.Height, r.Width, r.Height);
        }

        private sealed record Shot32(int Width, int Height, byte[] Bgra);

        private static Shot32 Pixels(Bitmap bitmap)
        {
            var size = bitmap.PixelSize;
            var bytes = new byte[size.Width * size.Height * 4];
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), bytes.Length, size.Width * 4); }
            finally { handle.Free(); }
            if (bitmap.Format is { } f && f == PixelFormat.Rgba8888)
                for (var i = 0; i < bytes.Length; i += 4) (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
            return new Shot32(size.Width, size.Height, bytes);
        }

        private static (byte B, byte G, byte R) Background(Shot32 px, double x, double y, double scale)
        {
            var i = (Math.Clamp((int)(y * scale), 0, px.Height - 1) * px.Width + Math.Clamp((int)(x * scale), 0, px.Width - 1)) * 4;
            return (px.Bgra[i], px.Bgra[i + 1], px.Bgra[i + 2]);
        }

        /// <summary>The first and last pixel rows in <paramref name="r"/> (points) with ink on <paramref name="bg"/>.</summary>
        private static (int Top, int Bottom, double Mid)? InkRows(Shot32 px, Rect r, double scale, (byte B, byte G, byte R) bg)
        {
            int x0 = Math.Max(0, (int)Math.Floor(r.X * scale)), x1 = Math.Min(px.Width, (int)Math.Ceiling(r.Right * scale));
            int y0 = Math.Max(0, (int)Math.Floor(r.Y * scale)), y1 = Math.Min(px.Height, (int)Math.Ceiling(r.Bottom * scale));
            int top = -1, bottom = -1;
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var i = (y * px.Width + x) * 4;
                    if (Math.Abs(px.Bgra[i] - bg.B) + Math.Abs(px.Bgra[i + 1] - bg.G) + Math.Abs(px.Bgra[i + 2] - bg.R) < 60) continue;
                    if (top < 0) top = y;
                    bottom = y;
                    break;
                }
            return top < 0 ? null : (top, bottom, (top + bottom + 1) / 2.0);
        }
    }
}
#endif
