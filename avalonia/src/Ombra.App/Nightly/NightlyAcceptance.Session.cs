using Avalonia;
using Ombra.App.Controls;
using Ombra.ClientCore;

namespace Ombra.App.Nightly;

internal static partial class NightlyAcceptance
{
    private sealed partial class Run
    {
        /// <summary>
        /// The Browser page's session outlives its Nightly, as a standalone Nightly's does. Two unpinned tabs (a red and a
        /// blue page written next to the log) are opened for the window; Nightly is then asked to quit as at Ombra's quit
        /// (Quit: its session written; its window closing makes the page a new pane on a new Nightly) and the page's
        /// window has both tabs back (⌃Tab through the tabs, the content's colour); then the same after a crash (SIGKILL,
        /// the session's last periodic save; the same pane on the restarted Nightly). The runtime is not one of the
        /// processes the app's reaper ends at quit (<see cref="OwnedProcesses"/>: its SIGTERM would end Nightly before its
        /// session is written). Shows the Browser page and returns to the chats.
        /// </summary>
        private async Task SessionChecks()
        {
            if (!await Until(() =>
                {
                    if (!vm.ShowBrowserPage) vm.OpenBrowserPageCommand.Execute(null);
                    return BrowserWindow() is { FramesShown: > 0, IsEffectivelyVisible: true } p && Matches(p);
                }, 60))
            {
                Check("session-browser-window", false, $"panes={Panes().Count}");
                return;
            }
            var pane = BrowserWindow()!;
            var owner = OwnedProcesses.ListProcesses().FirstOrDefault(p => p.Pid == pane.RuntimePid);
            Check("session-runtime-not-reaped", owner is { Owner: null }, $"pid={pane.RuntimePid} owner={owner?.Owner}");

            var red = Page("session-red", "#d02828");
            var blue = Page("session-blue", "#2838d0");
            vm.OpenInBrowserWindow?.Invoke(red);
            await Until(() => Hue(pane) == "red", 20);
            vm.OpenInBrowserWindow?.Invoke(blue);
            var opened = await Until(() => Hue(pane) == "blue", 20);
            Check("session-tabs-opened", opened, $"content {Hue(pane)}");

            // Quit as Ombra's quit asks it (its session written); the page then starts it again.
            var first = pane.RuntimePid;
            var asked = NightlyHost.Get().Runtime?.Send(NightlyProtocol.QuitMessage()) == true;
            var (after, seen) = await RestartedTabs(first, "q-session-after-quit");
            Check("session-restore-quit", asked && seen.SetEquals(["red", "blue"]), $"asked={asked} pid {first}→{after?.RuntimePid} tabs seen: {string.Join(",", seen)}");

            // A crash: what the last periodic save (every 15 s) holds comes back.
            if (after is not null)
            {
                await Task.Delay(TimeSpan.FromSeconds(16));
                var second = after.RuntimePid;
                NightlyMach.Signal(second, 9);
                Log($"killed nightly pid {second}");
                (after, seen) = await RestartedTabs(second, "q-session-after-crash");
                Check("session-restore-crash", seen.SetEquals(["red", "blue"]), $"pid {second}→{after?.RuntimePid} tabs seen: {string.Join(",", seen)}");
            }
            vm.ShowChatsCommand.Execute(null);
            await Task.Delay(600);
        }

        private Uri Page(string name, string colour)
        {
            var path = Path.Combine(dir, name + ".html");
            File.WriteAllText(path, $"<!doctype html><title>{name}</title><body style=\"margin:0;height:100vh;background:{colour}\"></body>");
            return new Uri(path);
        }

        /// <summary>"red" or "blue" for the session pages in the content's middle, else "other".</summary>
        private string Hue(NightlyPane pane)
        {
            var (r, _, b) = ContentColor(pane, ContentMiddle);
            return r > b + 80 ? "red" : b > r + 80 ? "blue" : "other";
        }

        /// <summary>Waits for the page's window on a Nightly other than <paramref name="before"/>, then goes through its
        /// tabs with ⌃Tab (a click on the content first: a start shows Zen's empty tab with its URL bar); the pane and the
        /// session pages seen.</summary>
        private async Task<(NightlyPane? Pane, HashSet<string> Seen)> RestartedTabs(int before, string shot)
        {
            var seen = new HashSet<string>();
            if (!await Until(() => BrowserWindow() is { RuntimePid: > 0, FramesShown: > 2, IsEffectivelyVisible: true } p && p.RuntimePid != before && Matches(p), 90))
                return (null, seen);
            var pane = BrowserWindow()!;
            await Task.Delay(1500);
            Activate();
            await Until(() => window.IsActive, 5);
            // A click on the content outside the start's floating URL bar closes it and makes the window key.
            MouseClickPane(pane, new Point(pane.Bounds.Width * 0.7, pane.Bounds.Height * 0.85));
            await Task.Delay(500);
            for (var i = 0; i < 16 && seen.Count < 2; i++)
            {
                KeyPress("\t", 0x30, 1UL << 18);
                await Task.Delay(900); // a restored tab loads when selected
                if (Hue(pane) is var hue and not "other") seen.Add(hue);
            }
            ScreenShot(shot);
            return (pane, seen);
        }
    }
}
