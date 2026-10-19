using Avalonia;
using Ombra.App.Controls;
using Ombra.App.Platform.Mac;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Nightly;

internal static partial class NightlyAcceptance
{
    private sealed partial class Run
    {
        // The page NewWindowChecks opens (a file of the run's directory, so no server is needed): its whole viewport is
        // two buttons in its colour, the upper one opens a sized popup as a sign-in does, the lower one a link. A message
        // from the popup turns the page cyan.
        private const string WindowsPage = """
            <!doctype html><meta charset="utf-8"><title>W</title>
            <style>html,body{margin:0;height:100%;background:#20a020}a,button{display:block;width:100%;height:50vh;margin:0;border:0;background:inherit}</style>
            <button id="popup" onclick="window.open('windows-popup.html','signin','width=460,height=560')"></button>
            <a id="link" href="windows-b.html"></a>
            <script>addEventListener('message', e => { if (e.data == 'token') document.body.style.background = '#20a0a0'; });</script>
            """;

        // The popup: magenta; a click anywhere answers its opener and closes it, as a sign-in popup does when done.
        private const string WindowsPopupPage = """
            <!doctype html><meta charset="utf-8"><title>Sign in</title>
            <style>html,body{margin:0;height:100%;background:#a020a0}button{width:100%;height:100%;border:0;background:inherit}</style>
            <button onclick="window.opener && window.opener.postMessage('token', '*'); window.close()"></button>
            """;

        private const string WindowsLinkPage = """
            <!doctype html><meta charset="utf-8"><title>B</title><style>html,body{margin:0;height:100%;background:#2040c0}</style>
            """;

        private static readonly (int R, int G, int B) Green = (0x20, 0xa0, 0x20), Cyan = (0x20, 0xa0, 0xa0),
            Magenta = (0xa0, 0x20, 0xa0), Blue = (0x20, 0x40, 0xc0);

        /// <summary>Where the colour of the windows page is read: the upper button's middle (fractions of the pane).</summary>
        private static readonly Rect WindowsProbe = new(0.6, 0.25, 0.2, 0.04);

        /// <summary>
        /// Nothing the Browser page's window does opens a Nightly window outside Ombra (none on screen at any step): a
        /// sized <c>window.open</c> (a sign-in popup) is a tab of the window that answers its opener and is closed by its
        /// page, a shift-click (a link for a new window) is a tab in front, ⌘N is a new tab, ⌘⇧O (the Library's
        /// bookmarks) is a tab, and a link Ombra opens (a chat's, a card's) shows the Browser page with it in a tab
        /// instead of going to the system's browser.
        /// </summary>
        private async Task NewWindowChecks(NightlyPane pane)
        {
            File.WriteAllText(Path.Combine(dir, "windows.html"), WindowsPage);
            File.WriteAllText(Path.Combine(dir, "windows-popup.html"), WindowsPopupPage);
            File.WriteAllText(Path.Combine(dir, "windows-b.html"), WindowsLinkPage);
            var page = new Uri(Path.Combine(dir, "windows.html"));
            var pid = pane.RuntimePid;
            (int R, int G, int B) color = default;
            async Task<bool> Shows((int R, int G, int B) want, double seconds) =>
                await Until(() => Near(color = ContentColor(pane, WindowsProbe), want), seconds);
            bool NoWindows() => NightlyWindowRects(pid).Count == 0;
            // "Something other than the page": a colour was read (the probe is inside the shot) and it isn't cyan. The
            // page must have shown cyan just before, so the detector is known to see it.
            bool Left() => (color = ContentColor(pane, WindowsProbe)).R >= 0 && !Near(color, Cyan);
            CGPoint At(double fx, double fy) => ClientToWindow(pane.TranslatePoint(new Point(pane.Bounds.Width * fx, pane.Bounds.Height * fy), window) ?? default);

            vm.OpenInBrowserWindow?.Invoke(page);
            Check("windows-page", await Shows(Green, 30), $"colour={color}");
            Activate();
            await Until(() => window.IsActive, 5);

            // A sign-in popup: a tab in front (magenta), no window; its click answers the opener and closes the tab.
            MouseClick(At(0.6, 0.25), right: false, clicks: 1);
            var tab = await Shows(Magenta, 10);
            await Task.Delay(1500);
            Check("windows-popup-tab", tab && NoWindows(), $"colour={color} nightly windows={NightlyWindowRects(pid).Count}");
            Shot("p-windows-popup-tab");
            MouseClick(At(0.6, 0.5), right: false, clicks: 1);
            Check("windows-popup-opener", await Shows(Cyan, 10) && NoWindows(), $"colour={color}");

            // A link for a new window (shift-click): a tab in front, no window.
            ClickWith(At(0.6, 0.75), 1UL << 17);
            var shifted = await Shows(Blue, 10);
            await Task.Delay(1500);
            Check("windows-shift-click", shifted && NoWindows(), $"colour={color} nightly windows={NightlyWindowRects(pid).Count}");
            KeyPress("w", 0x0D, 1UL << 20);
            await Shows(Cyan, 10);

            // ⌘N: a new tab of the window (Zen's URL bar over the content, as ⌘T), no window; ⌘W closes it again.
            KeyPress("n", 0x2D, 1UL << 20);
            double band = 0;
            var newTab = await Until(() => (band = ContentLuma(pane, UrlBarBand)) < 120, 10);
            await Task.Delay(1500);
            Check("windows-cmd-n", newTab && NoWindows(), $"url bar band luma={band:0} nightly windows={NightlyWindowRects(pid).Count}");
            KeyPress("w", 0x0D, 1UL << 20);
            var cyanBeforeLibrary = await Shows(Cyan, 10);

            // ⌘⇧O, the Library's bookmarks: the organizer as a tab (no longer the page's colour), no Library window.
            KeyPress("o", 0x1F, (1UL << 20) | (1UL << 17));
            var library = cyanBeforeLibrary && await Until(Left, 10);
            await Task.Delay(1500);
            Check("windows-library", library && NoWindows(), $"cyan before={cyanBeforeLibrary} colour={color} nightly windows={NightlyWindowRects(pid).Count}");
            Shot("p-windows-library");
            KeyPress("w", 0x0D, 1UL << 20);
            var cyanBeforeLink = await Shows(Cyan, 10);

            // A link Ombra opens (a chat's link, a card's Open): from the Chats page, the Browser page shows it in a tab.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var fixture) || fixture.Scheme is not ("http" or "https")) return;
            vm.ShowChatsCommand.Execute(null);
            await Until(() => !pane.IsEffectivelyVisible, 5);
            var link = new Uri(fixture, "?from=ombra");
            vm.OpenExternal(link.AbsoluteUri);
            var shown = cyanBeforeLink && await Until(() => vm.ShowBrowserPage && pane.IsEffectivelyVisible, 10)
                && await Until(Left, 15);
            await Task.Delay(1000);
            Check("windows-ombra-link", shown && NoWindows(), $"cyan before={cyanBeforeLink} page={vm.ShowBrowserPage} colour={color} nightly windows={NightlyWindowRects(pid).Count}");
            Shot("p-windows-ombra-link");
            KeyPress("w", 0x0D, 1UL << 20);
            await Task.Delay(500);
        }

        // The window's shot is colour-matched to the display: a page's #20a020 comes back as about (77, 156, 54). The four
        // colours differ by 120 or more in some channel, so 64 tells them apart.
        private static bool Near((int R, int G, int B) a, (int R, int G, int B) b) =>
            Math.Abs(a.R - b.R) <= 64 && Math.Abs(a.G - b.G) <= 64 && Math.Abs(a.B - b.B) <= 64;

        /// <summary>The average colour of <paramref name="region"/> (fractions of the pane) in a shot of Ombra's window.</summary>
        private unsafe (int R, int G, int B) ContentColor(NightlyPane pane, Rect region)
        {
            var file = Shot("probe");
            var s = window.RenderScaling;
            var topLeft = pane.TranslatePoint(new Point(pane.Bounds.Width * region.X, pane.Bounds.Height * region.Y), window) ?? default;
            int x0 = (int)Math.Round(topLeft.X * s), y0 = (int)Math.Round(topLeft.Y * s);
            int w = (int)(pane.Bounds.Width * region.Width * s), h = (int)(pane.Bounds.Height * region.Height * s);
            using var bitmap = new Avalonia.Media.Imaging.Bitmap(file);
            var size = bitmap.PixelSize;
            if (x0 < 0 || y0 < 0 || x0 + w > size.Width || y0 + h > size.Height || w * h == 0) return (-1, -1, -1);
            var stride = size.Width * 4;
            var pixels = new byte[stride * size.Height];
            fixed (byte* buffer = pixels) bitmap.CopyPixels(new PixelRect(size), (nint)buffer, pixels.Length, stride);
            var bgra = bitmap.Format == Avalonia.Platform.PixelFormat.Bgra8888;
            long r = 0, g = 0, b = 0;
            for (var y = y0; y < y0 + h; y++)
            for (var x = x0; x < x0 + w; x++)
            {
                var o = y * stride + x * 4;
                r += pixels[bgra ? o + 2 : o];
                g += pixels[o + 1];
                b += pixels[bgra ? o : o + 2];
            }
            var n = (long)w * h;
            return ((int)(r / n), (int)(g / n), (int)(b / n));
        }

        /// <summary>A left click with modifier <paramref name="flags"/> (NSEventModifierFlags), posted as the hardware's.</summary>
        private unsafe void ClickWith(CGPoint at, ulong flags)
        {
            Post(MouseEvent(5, at, 0));
            foreach (var type in new ulong[] { 1, 2 })
                Post(((delegate* unmanaged<nint, nint, ulong, CGPoint, ulong, double, long, nint, long, long, float, nint>)MacObjC.MsgSend)(
                    MacObjC.Cls("NSEvent"), MacObjC.Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
                    type, at, flags, Uptime(), MacObjC.SendLong(NsWindow, "windowNumber"), 0, 0, 1, type == 1 ? 1f : 0f));
        }
    }
}
