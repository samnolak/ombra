using System.Diagnostics;
using Avalonia;
using Ombra.App.Controls;
using Ombra.App.Platform.Mac;

namespace Ombra.App.Nightly;

internal static partial class NightlyAcceptance
{
    private sealed partial class Run
    {
        private const string LsRegister =
            "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";

        /// <summary>A site link Nightly hands to an app (src/zen/open-in-app), the scheme it hands it with and the app URL.</summary>
        private static readonly (string Scheme, string Site, string App)[] OpenInAppLinks =
        [
            ("zoommtg", "https://zoom.us/j/123456789?pwd=ombra", "zoommtg://zoom.us/join?action=join&confno=123456789&pwd=ombra"),
            ("msteams", "https://teams.microsoft.com/l/meetup-join/ombra-acceptance", "msteams:/l/meetup-join/ombra-acceptance"),
            ("discord", "https://discord.gg/ombraacceptance", "discord://-/invite/ombraacceptance"),
        ];

        /// <summary>
        /// Open in app on the Browser page: a link Ombra opens there (a chat link's "Open in Browser page",
        /// <see cref="ViewModels.MainViewModel.OpenLink(Uri, ViewModels.LinkTarget)"/>) and a link clicked in the page, both to a site whose
        /// app is installed, reach that app through LaunchServices with the app's URL, and the page stays: the tab
        /// opened for Ombra's link closes (no blank tab, no landing page), the clicked page keeps its colour. The app is
        /// a probe written here (an AppleScript applet claiming the scheme of the first known app this Mac has no handler
        /// for — Zoom's, else Teams', else Discord's) that writes the URLs it is opened with; it is unregistered and
        /// deleted afterwards, so no real app opens and no meeting is joined.
        /// </summary>
        private async Task OpenInAppChecks()
        {
            if (!await Until(() =>
                {
                    if (!vm.ShowBrowserPage) vm.OpenBrowserPageCommand.Execute(null);
                    return BrowserWindow() is { FramesShown: > 0, IsEffectivelyVisible: true } p && Matches(p);
                }, 60))
            {
                Check("open-in-app-browser-window", false, $"panes={Panes().Count}");
                return;
            }
            var pane = BrowserWindow()!;
            var link = OpenInAppLinks.FirstOrDefault(l => SchemeHandler(l.Scheme) is null);
            if (link.Scheme is null)
            {
                Check("open-in-app-probe-scheme", false, "every probe scheme has an app on this Mac");
                return;
            }
            var output = Path.Combine(Path.GetTempPath(), $"ombra-open-in-app-{Environment.ProcessId}.txt");
            // LaunchServices ignores apps under the temporary folder for URL schemes: the probe lives in the output
            // directory, what it writes in the temporary folder (an applet writing elsewhere may need the user's consent).
            var probe = Path.Combine(dir, $"OmbraOpenInAppProbe-{Environment.ProcessId}.app");
            File.Delete(output);
            try
            {
                var registered = MakeProbe(probe, link.Scheme, output)
                    && await Until(() => SchemeHandler(link.Scheme) is { } app && app.TrimEnd('/').EndsWith(Path.GetFileName(probe), StringComparison.Ordinal), 15);
                Check("open-in-app-probe", registered, $"{link.Scheme} -> {SchemeHandler(link.Scheme)}");
                if (!registered) return;

                // The page the tabs go back to: a link covering it, to the app's site.
                var page = Path.Combine(dir, "open-in-app.html");
                File.WriteAllText(page,
                    $"<!doctype html><title>open-in-app</title><body style=\"margin:0\"><a href=\"{link.Site}\" "
                    + "style=\"display:block;height:100vh;background:#d028d0\">app link</a></body>");
                (int R, int G, int B) magenta = (208, 40, 208), color = default;
                bool Magenta() => Near(color = ContentColor(pane, ContentMiddle), magenta);
                vm.OpenInBrowserWindow?.Invoke(new Uri(page));
                Check("open-in-app-page", await Until(Magenta, 30), $"colour={color}");

                // A link from Ombra (a chat link's "Open in Browser page"): the app gets it, the tab opened for it closes.
                var opened = vm.OpenInBrowserWindow is not null;
                vm.OpenLink(new Uri(link.Site), ViewModels.LinkTarget.BrowserPage);
                var got = await Until(() => Lines(output).Count >= 1, 20);
                await Task.Delay(1500);
                Activate();
                var stays = await Until(Magenta, 10);
                ScreenShot("open-in-app-ombra-link");
                Check("open-in-app-ombra-link", opened && got && Lines(output).FirstOrDefault() == link.App && stays,
                    $"opened={opened} app got [{string.Join(" | ", Lines(output))}] want {link.App}; page colour={color}");

                // A click on the page's link: the app gets it, the page stays.
                await Task.Delay(500);
                MouseClick(ClientToWindow(pane.TranslatePoint(new Point(pane.Bounds.Width * 0.7, pane.Bounds.Height * 0.5), window) ?? default),
                    right: false, clicks: 1);
                got = await Until(() => Lines(output).Count >= 2, 20);
                await Task.Delay(1500);
                Activate();
                stays = await Until(Magenta, 10);
                ScreenShot("open-in-app-click");
                Check("open-in-app-click", got && Lines(output).ElementAtOrDefault(1) == link.App && stays,
                    $"app got [{string.Join(" | ", Lines(output))}]; page colour={color}");
            }
            finally
            {
                Exec(LsRegister, "-u", probe);
                if (Directory.Exists(probe)) Directory.Delete(probe, recursive: true);
                File.Delete(output);
                vm.ShowChatsCommand.Execute(null);
                await Task.Delay(600);
            }
        }

        private static List<string> Lines(string file) =>
            File.Exists(file) ? File.ReadAllLines(file).Where(l => l.Length > 0).ToList() : [];

        /// <summary>The app LaunchServices opens <paramref name="scheme"/> links with, or null.</summary>
        private static string? SchemeHandler(string scheme)
        {
            var workspace = MacObjC.Send(MacObjC.Cls("NSWorkspace"), "sharedWorkspace");
            var app = MacObjC.Send(workspace, "URLForApplicationToOpenURL:", MacObjC.Url(new Uri(scheme + "://probe")));
            return app == 0 ? null : MacObjC.ToStr(MacObjC.Send(app, "path"));
        }

        /// <summary>An applet that appends each URL it is opened with to <paramref name="output"/> and quits, claiming
        /// <paramref name="scheme"/>; registered with LaunchServices.</summary>
        private bool MakeProbe(string app, string scheme, string output)
        {
            if (Directory.Exists(app)) Directory.Delete(app, recursive: true);
            var script =
                $"on open location u\n do shell script \"printf '%s\\\\n' \" & quoted form of u & \" >> \" & quoted form of \"{output}\"\nend open location";
            var plist = Path.Combine(app, "Contents", "Info.plist");
            var ok = Exec("/usr/bin/osacompile", "-o", app, "-e", script) == 0
                && Exec("/usr/bin/plutil", "-replace", "CFBundleIdentifier", "-string", $"app.ombra.acceptance.open-in-app-{Environment.ProcessId}", plist) == 0
                && Exec("/usr/bin/plutil", "-replace", "CFBundleURLTypes", "-json",
                    $"[{{\"CFBundleURLName\":\"Ombra acceptance\",\"CFBundleURLSchemes\":[\"{scheme}\"]}}]", plist) == 0
                // A run that never activates an app (Platform/TestFocus.cs): LaunchServices activates an agent app it
                // opens a URL with, never a background-only one.
                && Exec("/usr/bin/plutil", "-replace", Platform.TestFocus.NoActivate ? "LSBackgroundOnly" : "LSUIElement", "-bool", "YES", plist) == 0
                && Exec("/usr/bin/codesign", "--force", "--deep", "--sign", "-", app) == 0
                && Exec(LsRegister, "-f", app) == 0;
            Log($"open-in-app probe {app} scheme={scheme} ok={ok}");
            return ok;
        }

        private int Exec(string file, params string[] args)
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardError = true, RedirectStandardOutput = true })!;
            var err = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) Log($"{Path.GetFileName(file)} exit {p.ExitCode}: {err.Trim()}");
            return p.ExitCode;
        }
    }
}
