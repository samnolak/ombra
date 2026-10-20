using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Ombra.App.Controls;
using Ombra.App.Platform.Mac;
using Ombra.ClientCore;
using static Ombra.App.Platform.Mac.MacRuntime;

namespace Ombra.App.Nightly;

internal static partial class NightlyAcceptance
{
    private sealed partial class Run
    {
        /// <summary>How many times each way of opening a new tab is tried.</summary>
        private const int NewTabTries = 20;

        // Points of the Browser page's Nightly window in its default layout (a fresh profile): the sidebar's URL bar and
        // its "+ New Tab" row (a space with tabs).
        private static readonly Point SidebarUrlBar = new(115, 62);
        private static readonly Point SidebarNewTab = new(115, 169);

        /// <summary>An empty spot of the sidebar, under its tabs.</summary>
        private static Point SidebarEmpty(NightlyPane pane) => new(115, pane.Bounds.Height * 0.8);

        /// <summary>
        /// Every way of opening a new tab in the Browser page's Nightly window, <see cref="NewTabTries"/> times each, as a
        /// user does it (keys and clicks posted to the app's queue): each time the new tab's URL bar floats over the
        /// content with the keyboard (<see cref="NewTabBar"/>). The empty tab (the first Browser page, the last tab
        /// closed) is a new tab too: Esc, ⌘T, leaving the page for the chats and the app losing the focus all leave it
        /// with its bar. On a page: ⌘T with the focus in the page, in the sidebar's URL bar, in the sidebar and in Ombra's
        /// rail, the sidebar's "+ New Tab", a middle click on the sidebar, ⌘T after Chats ↔ Browser and after the app
        /// lost the focus, ⌘T in compact mode; and the owner's sequence: a bar open, another app takes the focus, back,
        /// then "+ New Tab", ⌘T or a middle click again (the bar stays, with the keyboard). A URL typed into the empty
        /// tab's bar opens. Returns to the chats.
        /// <para>Any profile: the space's tabs are closed first (<see cref="EmptyTabBaseline"/>); it must have no pinned
        /// tabs. Gecko's focus must be production's: no Marionette recommended prefs (<c>MOZ_MARIONETTE</c>, which Nightly
        /// inherits, applies them unless the runtime profile sets <c>remote.prefs.recommended</c> false), whose
        /// <c>focusmanager.testmode</c> keeps the window active when Ombra deactivates it (AppWindow::WindowDeactivated),
        /// so a bar outlives the app losing the focus (<c>newtab-production-focus</c>). <paramref name="stealFocus"/>:
        /// another app takes the focus right after every other open (<see cref="AnotherAppTakesFocus"/>; the try is made
        /// again), so each try after it opens a new tab in a window that Ombra deactivated and activated again.</para>
        /// </summary>
        private async Task NewTabChecks(int agentPid, bool stealFocus = false)
        {
            var marionette = Environment.GetEnvironmentVariable("MOZ_MARIONETTE") is not null;
            var prefs = Environment.GetEnvironmentVariable("OMBRA_NIGHTLY_PROFILE") is { Length: > 0 } profile ? Path.Combine(profile, "prefs.js") : null;
            var recommendedOff = prefs is not null && File.Exists(prefs)
                && File.ReadAllText(prefs).Contains("\"remote.prefs.recommended\", false", StringComparison.Ordinal);
            Check("newtab-production-focus", !marionette || recommendedOff,
                marionette ? $"MOZ_MARIONETTE set; remote.prefs.recommended false in the runtime profile: {recommendedOff}" : "no Marionette");
            _stealFocus = stealFocus;
            if (await OpenBrowserWindow("nt-a-first", agentPid) is not { } pane) return;
            await EnsureActive();
            var (first, firstDetail) = await NewTabBar(pane);
            Check("newtab-first-browser-page", first, firstDetail);
            await Task.Delay(300);
            ScreenShot("nt-b-empty-tab");
            var (baseline, closed) = await EmptyTabBaseline(pane);
            Check("newtab-empty-tab-baseline", baseline, $"{closed} tabs closed before the space was left with its empty tab");
            if (!baseline)
            {
                vm.ShowChatsCommand.Execute(null);
                return;
            }

            Func<Task> Keys(string characters, ushort keyCode, ulong flags) => () =>
            {
                KeyPress(characters, keyCode, flags);
                return Task.CompletedTask;
            };
            var esc = Keys("\u001b", 0x35, 0);
            var cmdT = Keys("t", 0x11, CommandKey);
            var cmdW = Keys("w", 0x0D, CommandKey);
            async Task ChatsAndBack()
            {
                vm.ShowChatsCommand.Execute(null);
                await Until(() => !pane.IsEffectivelyVisible, 5);
                await Task.Delay(200);
                vm.OpenBrowserPageCommand.Execute(null);
                await Until(() => pane.IsEffectivelyVisible && pane.IsKeyboardFocusWithin, 5);
            }

            // ── the empty tab ──
            await NewTabPath("empty-esc", pane, null, esc, null);
            await NewTabPath("empty-cmd-t", pane, null, cmdT, null);
            await NewTabPath("empty-chats-browser", pane, null, ChatsAndBack, null);
            await NewTabPath("empty-focus-loss", pane, null, LoseAppFocus, null);
            // A URL typed into the bar opens (the white fixture page), and ⌘W on that last tab leaves the empty tab again.
            var page = new Uri(new Uri(url), "cutover.html");
            // Retries replace the same try's result; a stolen focus must not count a typed URL twice.
            var typed = new bool[NewTabTries];
            await NewTabPath("last-tab-closed", pane, async i =>
            {
                foreach (var ch in page.AbsoluteUri) KeyPress(ch.ToString(), CharKeyCode(ch), 0);
                KeyPress("\r", 0x24, 0);
                typed[i] = await Until(async () => await FrameRegionLuma(pane, ContentMiddle) > 240, 15);
            }, cmdW, null);
            var typedCount = typed.Count(opened => opened);
            Check("newtab-typed-url-opens", typedCount == NewTabTries, $"{typedCount}/{NewTabTries} typed URLs showed {page}");

            // ── from a page ──
            vm.OpenInBrowserWindow?.Invoke(page);
            Check("newtab-page-open", await Until(async () => await FrameRegionLuma(pane, ContentMiddle) > 240, 30), page.AbsoluteUri);
            await Task.Delay(300);
            var content = new Point(pane.Bounds.Width * 0.7, pane.Bounds.Height * 0.5);
            // Esc closes the new tab's bar over the page: the page shows where the bar was.
            async Task Close()
            {
                KeyPress("\u001b", 0x35, 0);
                await Until(async () => await FrameRegionLuma(pane, UrlBarBand) > 240, 5);
            }
            await NewTabPath("page-cmd-t", pane, async _ => await ClickPane(pane, content), cmdT, Close);
            await NewTabPath("urlbar-cmd-t", pane, async _ => await ClickPane(pane, SidebarUrlBar), cmdT, Close);
            await NewTabPath("sidebar-cmd-t", pane, async _ => await ClickPane(pane, SidebarEmpty(pane)), cmdT, Close);
            await NewTabPath("ombra-rail-cmd-t", pane, async _ =>
            {
                Press(Named<Button>("RailBrowserButton"), "Browser (rail)");
                await Task.Delay(300);
                Log($"Ombra focus={window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "none"}");
            }, cmdT, Close);
            Func<Task> newTabClick = () => ClickPane(pane, SidebarNewTab);
            Func<Task> middleClick = () => MiddleClick(pane, SidebarEmpty(pane));
            await NewTabPath("sidebar-new-tab", pane, null, newTabClick, Close);
            await NewTabPath("sidebar-middle-click", pane, null, middleClick, Close);
            await NewTabPath("chats-browser-cmd-t", pane, _ => ChatsAndBack(), cmdT, Close);
            await NewTabPath("focus-loss-cmd-t", pane, _ => LoseAppFocus(), cmdT, Close);
            // The owner's sequence: a new tab's bar open, another app takes the focus, back in Ombra (Firefox keeps the
            // bar open through the window's deactivation), then "+ New Tab", ⌘T or a middle click again: the bar stays,
            // with the keyboard (Zen's new tab used to toggle it closed, leaving no input at all).
            Func<int, Task> OpenThenSwitchApps(Func<Task> opener) => async _ =>
            {
                await opener();
                var (opened, detail) = await NewTabBar(pane);
                if (!opened) _beforeFailed = $"no bar before the switch ({detail})";
                await LoseAppFocus();
            };
            await NewTabPath("switch-sidebar-new-tab", pane, OpenThenSwitchApps(newTabClick), newTabClick, Close);
            await NewTabPath("switch-cmd-t", pane, OpenThenSwitchApps(cmdT), cmdT, Close);
            await NewTabPath("switch-middle-click", pane, OpenThenSwitchApps(middleClick), middleClick, Close);
            // Compact mode (⌘S): the sidebar hides, the bar still floats over the content.
            await ClickPane(pane, content);
            KeyPress("s", 0x01, CommandKey);
            await Task.Delay(800);
            ScreenShot("nt-c-compact");
            await NewTabPath("compact-cmd-t", pane, async _ => await ClickPane(pane, content), cmdT, Close);
            await ClickPane(pane, content);
            KeyPress("s", 0x01, CommandKey);
            await Task.Delay(800);

            // The page closed: the empty tab again, with its bar.
            await ClickPane(pane, content);
            KeyPress("w", 0x0D, CommandKey);
            var (back, backDetail) = await NewTabBar(pane);
            Check("newtab-page-closed", back, backDetail);
            vm.ShowChatsCommand.Execute(null);
            await Until(() => !pane.IsEffectivelyVisible, 10);
        }

        /// <summary>The average luma of <paramref name="region"/> (fractions of the window) in the latest frame; -1
        /// without one.</summary>
        private async Task<double> FrameRegionLuma(NightlyPane pane, Rect region)
        {
            try
            {
                var shot = await pane.Agent.CaptureAsync(CancellationToken.None);
                using var bitmap = new Bitmap(new MemoryStream(shot.Png));
                var size = pane.FrameLayout.Shown.Size;
                return FrameLuma(bitmap, new Rect(size.Width * region.X, size.Height * region.Y, size.Width * region.Width, size.Height * region.Height),
                    shot.Width / size.Width);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return -1;
            }
        }

        private const ulong CommandKey = 1UL << 20;

        /// <summary>
        /// <paramref name="open"/> (after <paramref name="before"/>) <see cref="NewTabTries"/> times, each followed by
        /// <see cref="NewTabBar"/> and <paramref name="after"/>: one check with how many showed the bar, the first
        /// failures' details and a screen shot of each failure (and of the first success). A try during which another app
        /// took the focus from Ombra (other than <see cref="LoseAppFocus"/>'s own; <see cref="NewTabChecks"/>' stealFocus
        /// on every other try) says nothing about the new tab and is made again, up to 30 times in all (stealFocus' own
        /// besides). <paramref name="after"/> runs while Ombra has the focus (<see cref="Settle"/>).
        /// </summary>
        private async Task NewTabPath(string name, NightlyPane pane, Func<int, Task>? before, Func<Task> open, Func<Task>? after)
        {
            var failures = new List<string>();
            var interrupted = 0;
            var stolen = 0;
            void OnDeactivated(object? sender, EventArgs e) => _deactivated = true;
            window.Deactivated += OnDeactivated;
            try
            {
                for (var i = 0; i < NewTabTries;)
                {
                    await EnsureActive();
                    _deactivated = false;
                    _beforeFailed = null;
                    if (before is not null) await before(i);
                    await open();
                    if (_stealFocus && (++_opens & 1) == 1)
                    {
                        AnotherAppTakesFocus();
                        await Until(() => !window.IsActive, 3);
                        stolen++;
                        Log($"newtab {name} #{i}: another app takes the focus (stealFocus), active={window.IsActive}");
                    }
                    var (ok, detail) = await NewTabBar(pane);
                    if (_deactivated || !window.IsActive)
                    {
                        Log($"newtab {name} #{i} again: another app took the focus meanwhile ({detail})");
                        await Settle(after);
                        if (++interrupted < 30 + stolen) continue;
                        failures.Add($"#{i}: other apps kept taking the focus");
                        break;
                    }
                    if (_beforeFailed is { } why) (ok, detail) = (false, $"{why}; {detail}");
                    Log($"newtab {name} #{i} {(ok ? "ok" : "FAILED")} {detail}");
                    if (!ok)
                    {
                        failures.Add($"#{i}: {detail}");
                        ScreenShot($"nt-fail-{name}-{i}");
                    }
                    else if (i == 0) ScreenShot($"nt-{name}");
                    await Settle(after);
                    i++;
                }
            }
            finally
            {
                window.Deactivated -= OnDeactivated;
            }
            Check($"newtab-{name}", failures.Count == 0,
                $"{NewTabTries - failures.Count}/{NewTabTries} showed the bar, {interrupted} tries again after another app took the focus"
                + (_stealFocus ? $" ({stolen} on purpose)" : "")
                + (failures.Count > 0 ? ": " + string.Join("; ", failures.Take(3)) : ""));
        }

        private bool _deactivated;
        private bool _stealFocus;
        private int _opens;
        // What a path's before could not set up (OpenThenSwitchApps: no bar before the switch): the try fails.
        private string? _beforeFailed;

        /// <summary>
        /// A try's end, <paramref name="after"/> (Esc closing the bar over the page), while Ombra has the focus: keys
        /// posted while another app has it reach no window, and the next try would start with that try's bar still open
        /// (Firefox keeps it through the window's deactivation). Made again when another app took the focus meanwhile.
        /// </summary>
        private async Task Settle(Func<Task>? after)
        {
            if (after is null) return;
            for (var n = 0; n < 3; n++)
            {
                await EnsureActive();
                _deactivated = false;
                await after();
                if (!_deactivated && window.IsActive) return;
            }
        }

        /// <summary>
        /// The window left with the empty tab alone in its space, as on a fresh profile: ⌃Tab to any other tab of the space,
        /// Esc (the empty tab keeps its bar, a page's closes) and, on a page, ⌘W, until ⌃Tab finds no tab but the empty
        /// one. False when tabs stay (pinned ones: ⌘W only selects an unpinned tab). Also how many were closed.
        /// </summary>
        private async Task<(bool Ok, int Closed)> EmptyTabBaseline(NightlyPane pane)
        {
            for (var closed = 0; closed < 24; closed++)
            {
                await EnsureActive();
                pane.FocusPage();
                KeyPress("\t", 0x30, 1UL << 18);
                await Task.Delay(600);
                KeyPress("\u001b", 0x35, 0);
                var (empty, detail) = await NewTabBar(pane);
                if (empty) return (true, closed);
                Log($"newtab baseline: a tab of the space ({detail}): ⌘W");
                KeyPress("w", 0x0D, CommandKey);
                await Task.Delay(600);
            }
            return (false, 24);
        }

        /// <summary>
        /// Whether Nightly shows a new tab's URL bar with the keyboard. A composition started through the window's text
        /// input client (as an input method starts one) must land in an editor floating over the content: its caret, as
        /// Nightly reports it for the candidate window, lies in the content's upper middle, not in the sidebar's URL bar
        /// or nowhere (no editor has the keyboard); the frame must show the bar there, its band unlike the content right
        /// above the bar. The composition is cancelled each time. A bar on its way gets 3 s. Not while another app has the
        /// focus (no input method composes for Ombra then).
        /// </summary>
        private async Task<(bool Ok, string Detail)> NewTabBar(NightlyPane pane)
        {
            await Task.Delay(300);
            var end = DateTime.UtcNow.AddSeconds(3);
            while (true)
            {
                var focus = $"Ombra focus={window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "none"} active={window.IsActive}";
                if (!window.IsActive) return (false, $"another app has the focus, {focus}");
                var client = TextInputClient();
                string detail;
                if (client == 0) detail = $"no text input client, {focus}";
                else
                {
                    var reports = pane.TextRects;
                    SetMarkedText(client, "あ");
                    var reported = await Until(() => pane.TextRects > reports, 1);
                    var caret = pane.LastTextRect;
                    SetMarkedText(client, "");
                    await Task.Delay(150);
                    var size = pane.FrameLayout.Shown.Size;
                    var floating = reported && caret.Height > 0
                        && caret.Center.X > size.Width * 0.15 && caret.Center.X < size.Width * 0.85
                        && caret.Center.Y > size.Height * 0.15 && caret.Center.Y < size.Height * 0.65;
                    detail = reported ? $"caret {caret} in {size.Width:0}x{size.Height:0}" : $"no caret: no editor has the keyboard, {focus}";
                    if (floating)
                    {
                        var (seen, lumas) = await BarSeen(pane, caret);
                        if (seen || DateTime.UtcNow > end) return (seen, $"{detail} {lumas}");
                        detail += $" {lumas}";
                    }
                }
                if (DateTime.UtcNow > end) return (false, detail);
                await Task.Delay(250);
            }
        }

        /// <summary>The bar in the latest frame: the average luma right of the caret, at its height (the bar's empty
        /// part), against the content's just above the bar's top edge. Glass over the empty tab's backdrop or dark over a
        /// white page, the two differ by far more than the backdrop's gradient does.</summary>
        private async Task<(bool Seen, string Detail)> BarSeen(NightlyPane pane, Rect caret)
        {
            AgentScreenshot shot;
            try
            {
                shot = await pane.Agent.CaptureAsync(CancellationToken.None);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return (false, $"no frame: {e.Message}");
            }
            using var bitmap = new Bitmap(new MemoryStream(shot.Png));
            var k = shot.Width / pane.FrameLayout.Shown.Width;
            var inside = FrameLuma(bitmap, new Rect(caret.X + 120, caret.Y, 200, caret.Height), k);
            var above = FrameLuma(bitmap, new Rect(caret.X + 120, caret.Y - 45, 200, 10), k);
            return (inside >= 0 && above >= 0 && Math.Abs(inside - above) >= 12, $"bar luma {inside:0} above {above:0}");
        }

        /// <summary>The average luma (0–255) of <paramref name="points"/> (the window's points) in a frame of
        /// <paramref name="k"/> pixels per point; -1 outside it.</summary>
        private static unsafe double FrameLuma(Bitmap bitmap, Rect points, double k)
        {
            var size = bitmap.PixelSize;
            int x0 = (int)(points.X * k), y0 = (int)(points.Y * k), w = (int)(points.Width * k), h = (int)(points.Height * k);
            if (x0 < 0 || y0 < 0 || w <= 0 || h <= 0 || x0 + w > size.Width || y0 + h > size.Height) return -1;
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

        private async Task EnsureActive()
        {
            for (var i = 0; i < 3 && !window.IsActive; i++)
            {
                Activate();
                ActivateRunningApplication();
                await Until(() => window.IsActive, 2);
            }
        }

        /// <summary>Another app takes the focus (Ombra deactivates), then Ombra gets it back. Only a try where it did not
        /// come back (another app kept it) counts as interrupted.</summary>
        private async Task LoseAppFocus()
        {
            AnotherAppTakesFocus();
            await Until(() => !window.IsActive, 5);
            await Task.Delay(300);
            for (var i = 0; i < 3 && !window.IsActive; i++)
            {
                Activate();
                ActivateRunningApplication();
                await Until(() => window.IsActive, 2);
            }
            if (window.IsActive) _deactivated = false;
        }

        /// <summary>macOS 14 activates cooperatively: the running application's own request too.</summary>
        private static unsafe void ActivateRunningApplication() =>
            ((delegate* unmanaged<nint, nint, ulong, byte>)MacObjC.MsgSend)(
                MacObjC.Send(MacObjC.Cls("NSRunningApplication"), "currentApplication"), MacObjC.Sel("activateWithOptions:"), 3);

        /// <summary>Finder (always running) takes the focus, as when the user switches apps; in a run that never activates
        /// an app (<see cref="Platform.TestFocus"/>) the window loses Ombra's activation in-process instead.</summary>
        private unsafe void AnotherAppTakesFocus()
        {
            if (Platform.TestFocus.NoActivate)
            {
                Platform.TestFocus.Deactivate(window);
                return;
            }
            var apps = MacObjC.Send(MacObjC.Cls("NSRunningApplication"), "runningApplicationsWithBundleIdentifier:", MacObjC.Str("com.apple.finder"));
            if (MacObjC.SendLong(apps, "count") > 0)
                ((delegate* unmanaged<nint, nint, ulong, byte>)MacObjC.MsgSend)(MacObjC.Send(apps, "objectAtIndex:", 0), MacObjC.Sel("activateWithOptions:"), 3);
        }

        /// <summary>A left click at <paramref name="at"/> (the browser window's points).</summary>
        private async Task ClickPane(NightlyPane pane, Point at)
        {
            MouseClick(ClientToWindow(pane.TranslatePoint(at, window) ?? default), right: false, clicks: 1);
            await Task.Delay(300);
        }

        /// <summary>A middle click at <paramref name="at"/> (the browser window's points), as the mouse's: a CGEvent for
        /// the window (AppKit's own constructor gives other-button events button number 0).</summary>
        private async Task MiddleClick(NightlyPane pane, Point at)
        {
            var inWindow = ClientToWindow(pane.TranslatePoint(at, window) ?? default);
            var screen = ConvertToScreen(inWindow);
            var number = MacObjC.SendLong(NsWindow, "windowNumber");
            foreach (var type in new uint[] { 25, 26 }) // kCGEventOtherMouseDown, kCGEventOtherMouseUp
            {
                var cg = CGEventCreateMouseEvent(0, type, new CGPoint(screen.X, PrimaryScreenHeight() - screen.Y), 2 /* center */);
                try
                {
                    CGEventSetIntegerValueField(cg, 1 /* kCGMouseEventClickState */, 1);
                    CGEventSetIntegerValueField(cg, 91 /* window under the pointer */, number);
                    CGEventSetIntegerValueField(cg, 92 /* …that can handle this event */, number);
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
            Log($"middle click window ({inWindow.X:0.#},{inWindow.Y:0.#})");
            await Task.Delay(300);
        }

        private unsafe CGPoint ConvertToScreen(CGPoint inWindow) =>
            ((delegate* unmanaged<nint, nint, CGPoint, CGPoint>)MacObjC.MsgSend)(NsWindow, MacObjC.Sel("convertPointToScreen:"), inWindow);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern nint CGEventCreateMouseEvent(nint source, uint type, CGPoint location, uint button);
    }
}
