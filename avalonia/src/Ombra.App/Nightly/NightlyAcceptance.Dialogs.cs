using Avalonia;
using Ombra.App.Controls;
using Ombra.App.Platform.Mac;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Nightly;

internal static partial class NightlyAcceptance
{
    private sealed partial class Run
    {
        // The page DialogChecks opens: a key asks one of the page's questions (a key press is the user's activation), its
        // colour says how it ended (the Browser window reports no titles).
        private const string DialogsPage = """
            <!doctype html><meta charset="utf-8"><title>dialogs</title>
            <style>html,body{margin:0;height:100%;background:#2080c0}</style>
            <input id="file" type="file" style="position:absolute;left:-9999px">
            <script>
              const file = document.getElementById('file');
              const state = c => document.body.style.background = c;
              file.addEventListener('change', () => state('#20a0a0'));
              file.addEventListener('cancel', () => state('#a020a0'));
              addEventListener('keydown', e => {
                if (e.key == 'a') { alert('dialogs alert'); state('#20a020'); }
                if (e.key == 'n') Notification.requestPermission().then(r => state(r == 'denied' ? '#c02020' : '#c0c020'));
                if (e.key == 'u') file.click();
              });
            </script>
            """;

        private static readonly (int R, int G, int B) Ready = (0x20, 0x80, 0xc0), AlertClosed = (0x20, 0xa0, 0x20),
            Denied = (0xc0, 0x20, 0x20), FileCancelled = (0xa0, 0x20, 0xa0);

        /// <summary>Where the dialogs page's colour is read (fractions of the pane: right of the sidebar, mid height).</summary>
        private static readonly Rect DialogsProbe = new(0.7, 0.6, 0.1, 0.05);

        /// <summary>
        /// The Browser page's window keeps Nightly's own dialogs, shown where the user sees them, and the keyboard comes
        /// back to the page after each: <c>alert()</c> is Nightly's tab-modal dialog inside the window (Return closes it);
        /// a permission doorhanger is a Nightly window above Ombra's that draws its own background (its middle opaque, not
        /// the transparent panel a missing NSPopover left) and Esc answers it (Block); Save Page As and a page's file input
        /// are the system's save and open panels as sheets on Ombra's window (NightlyFilePanel), never a sheet on the
        /// Nightly window that is not on screen: the save panel writes the page where it was told, the open panel's
        /// Cancel reaches the page.
        /// </summary>
        private async Task DialogChecks(NightlyPane pane)
        {
            File.WriteAllText(Path.Combine(dir, "dialogs.html"), DialogsPage);
            var pid = pane.RuntimePid;
            (int R, int G, int B) color = default;
            async Task<bool> Shows((int R, int G, int B) want, double seconds) =>
                await Until(() => Near(color = ContentColor(pane, DialogsProbe), want), seconds);
            vm.OpenInBrowserWindow?.Invoke(new Uri(Path.Combine(dir, "dialogs.html")));
            var loaded = await Shows(Ready, 20);
            Check("dialogs-page", loaded, $"color={color}");
            if (!loaded) return;
            Activate();
            pane.FocusPage();
            // A click in the page: its keyboard focus, as the user gives it.
            MouseClick(ClientToWindow(pane.TranslatePoint(new Point(pane.Bounds.Width * 0.75, pane.Bounds.Height * 0.62), window) ?? default),
                right: false, clicks: 1);
            await Task.Delay(500);

            // alert(): inside the window (no Nightly window on screen), Return answers it, the page goes on.
            KeyPress("a", 0x00, 0);
            await Task.Delay(1200);
            var inside = NightlyWindowRects(pid).Count == 0;
            Shot("dlg-alert");
            KeyPress("\r", 0x24, 0);
            Check("dialogs-alert", inside && await Shows(AlertClosed, 10) && pane.IsKeyboardFocusWithin,
                $"color={color} nightly windows={NightlyWindowRects(pid).Count} focus={pane.IsKeyboardFocusWithin}");

            // The notifications doorhanger: above Ombra, with a background; Esc blocks.
            KeyPress("n", 0x2D, 0);
            List<CGRect> popups = [];
            var shown = await Until(() => (popups = NightlyWindowRects(pid)).Count > 0, 10) && NightlyAbove(pid);
            await Task.Delay(600);
            var opaque = PopupOpaque(pid, out var alphaDetail);
            ScreenShot("dlg-doorhanger");
            Check("dialogs-doorhanger", shown && opaque,
                $"popups={string.Join(" ", popups.Select(r => $"{r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}"))} {alphaDetail}");
            KeyPress("\u001b", 0x35, 0);
            Check("dialogs-doorhanger-block", await Shows(Denied, 10) && NightlyWindowRects(pid).Count == 0 && pane.IsKeyboardFocusWithin,
                $"color={color} focus={pane.IsKeyboardFocusWithin}");

                // Save Page As (⌘S): the save panel as a sheet on Ombra's window; it saves where it is told.
                var saved = Path.Combine(dir, "dialogs-saved.html");
                File.Delete(saved);
                KeyPress("s", 0x01, 1UL << 20);
                nint sheet = 0;
                var asked = await Until(() => (sheet = MacObjC.Send(NsWindow, "attachedSheet")) != 0, 15);
                var sheetClass = sheet != 0 ? MacObjC.ClassName(MacObjC.ClassOf(sheet)) : "";
                var save = asked && MacObjC.IsKindOf(sheet, "NSSavePanel") && !MacObjC.IsKindOf(sheet, "NSOpenPanel")
                    && NightlyWindowRects(pid).Count == 0;
                await Task.Delay(500);
                Shot("dlg-save-as");
                if (asked)
                {
                    MacObjC.SendVoid(sheet, "setDirectoryURL:", MacObjC.FileUrl(dir));
                    MacObjC.SendVoid(sheet, "setNameFieldStringValue:", MacObjC.Str("dialogs-saved.html"));
                    MacObjC.SendVoid(sheet, "ok:", 0);
                }
                Check("dialogs-save-as", save && await Until(() => File.Exists(saved) && new FileInfo(saved).Length > 0, 20)
                    && await Until(() => MacObjC.Send(NsWindow, "attachedSheet") == 0 && pane.IsKeyboardFocusWithin, 5),
                    $"sheet={sheetClass} saved={File.Exists(saved)} focus={pane.IsKeyboardFocusWithin}");

                // A page's <input type=file>: the open panel as a sheet on Ombra's window; Cancel reaches the page.
                KeyPress("u", 0x20, 0);
                var open = await Until(() => (sheet = MacObjC.Send(NsWindow, "attachedSheet")) != 0, 15)
                    && MacObjC.IsKindOf(sheet, "NSOpenPanel") && NightlyWindowRects(pid).Count == 0;
                await Task.Delay(500);
                Shot("dlg-upload");
                if (sheet != 0) MacObjC.SendVoid(sheet, "cancel:", 0);
                Check("dialogs-upload", open && await Shows(FileCancelled, 10)
                    && await Until(() => MacObjC.Send(NsWindow, "attachedSheet") == 0 && pane.IsKeyboardFocusWithin, 5),
                    $"color={color} focus={pane.IsKeyboardFocusWithin}");
        }

        /// <summary>Nightly's windows on screen by number.</summary>
        private static List<long> NightlyWindowNumbers(int pid)
        {
            var numbers = new List<long>();
            var list = CGWindowListCopyWindowInfo(1, 0);
            try
            {
                using var pidKey = new NSStr("kCGWindowOwnerPID");
                using var numberKey = new NSStr("kCGWindowNumber");
                foreach (var info in Items(list))
                    if (Int(CFDictionaryGetValue(info, pidKey.Handle)) == pid) numbers.Add(Int(CFDictionaryGetValue(info, numberKey.Handle)));
            }
            finally
            {
                NightlyMach.CFRelease(list);
            }
            return numbers;
        }

        /// <summary>The middle of Nightly's popup window on screen is opaque (its own shot: works while the screen is locked).</summary>
        private bool PopupOpaque(int pid, out string detail)
        {
            detail = "";
            var number = NightlyWindowNumbers(pid).FirstOrDefault();
            if (number == 0) return false;
            var file = Path.Combine(dir, "dlg-doorhanger-window.png");
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/sbin/screencapture",
                       ["-x", "-o", "-l", number.ToString(System.Globalization.CultureInfo.InvariantCulture), file]))!)
                p.WaitForExit();
            if (!File.Exists(file)) return false;
            using var bitmap = new Avalonia.Media.Imaging.Bitmap(file);
            var w = bitmap.PixelSize.Width;
            var h = bitmap.PixelSize.Height;
            var pixels = new byte[w * h * 4];
            unsafe
            {
                fixed (byte* b = pixels)
                    bitmap.CopyPixels(new PixelRect(0, 0, w, h), (nint)b, pixels.Length, w * 4);
            }
            // Alpha along the middle row, away from the rounded corners.
            var row = h / 2;
            var samples = Enumerable.Range(1, 9).Select(i => pixels[(row * w + w * i / 10) * 4 + 3]).ToList();
            detail = $"window={number} alpha={string.Join(",", samples)}";
            return samples.All(a => a > 200);
        }
    }
}
