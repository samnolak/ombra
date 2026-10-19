using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.ClientCore.Browser;

namespace Ombra.App.Nightly;

internal static partial class NightlyAcceptance
{
    /// <summary>
    /// The cutover to Nightly in the real window: the first-run consent strip (answered with its button), the rail's
    /// Browser page on the same runtime, the page's questions as Ombra's cards over the Nightly pane (alert, confirm,
    /// prompt, HTTP sign-in, a site permission, a download into the downloads strip, the file picker), and the
    /// fallback bar when Nightly keeps crashing, with Try again bringing it back. The fixture is <c>cutover.html</c>
    /// next to the main one, served with <c>/auth</c> (Basic, user <c>ombra</c>, password <c>k1</c>) and
    /// <c>/file.txt</c> (an attachment); buttons at (20, 20 + 50·i) 160×30 in the order alert, confirm, prompt,
    /// notifications permission, download, file, sign-in; <c>window.r</c> holds the answers.
    /// </summary>
    private sealed partial class Run
    {
        private T? Named<T>(string name, Control? within = null) where T : Control =>
            (within ?? (Visual)window).GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);

        /// <summary>A click in the middle of <paramref name="control"/>, posted as the hardware's.</summary>
        private bool Press(Control? control, string what)
        {
            if (control is null)
            {
                Log($"press {what}: not shown");
                return false;
            }
            var at = ClientToWindow(control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window) ?? default);
            Post(MouseEvent(5, at, 0));
            Post(MouseEvent(1, at, 1));
            Post(MouseEvent(2, at, 1));
            Log($"press {what} '{(control as ContentControl)?.Content}' window ({at.X:0.#},{at.Y:0.#})");
            return true;
        }

        // ── first run: the consent strip ──

        private async Task Consent()
        {
            if (!vm.Preview.NeedsNightlyConsent)
            {
                Log($"consent: engine already chosen ({vm.AgentBrowserEngine})");
                return;
            }
            var strip = await Until(() => Named<Border>("NightlyConsentBar") is not null, 10);
            Check("consent-strip", strip && !vm.Preview.UseNightly && Panes().Count == 0, $"engine={vm.AgentBrowserEngine} panes={Panes().Count}");
            await Task.Delay(400);
            Shot("0-consent");
            Activate();
            await Until(() => window.IsActive, 5);
            Press(Named<Button>("NightlyAcceptButton"), "Use Nightly");
            var accepted = await Until(() => !vm.Preview.NeedsNightlyConsent && vm.Preview.UseNightly && vm.AgentBrowserEngine == "nightly", 10);
            Check("consent-accepted", accepted && Named<Border>("NightlyConsentBar") is null,
                $"engine={vm.AgentBrowserEngine} useNightly={vm.Preview.UseNightly}");
        }

        // ── the rail's Browser, the page's questions, the fallback ──

        private async Task Cutover()
        {
            await BrowserPage();
            var pane = Shown();
            if (pane is null) return;
            await Questions(pane);
            await Fallback();
        }

        private async Task BrowserPage()
        {
            // The rail's Browser with Nightly: one whole Nightly browser window on the agent's runtime (BrowserWindowChecks).
            await BrowserWindowChecks(Shown()?.RuntimePid ?? 0);
            Check("browser-page-closed", await Until(() => Shown() is { FramesShown: > 0, IsBrowserWindow: false }, 10),
                $"panes={Panes().Count}");
        }

        private BrowserDialog? Asked => vm.Preview.ActiveTab?.Dialogs.Current;

        private async Task<JsonObject?> Answers(NightlyPane pane)
        {
            try
            {
                return await pane.Agent.RunScriptAsync("JSON.stringify(window.r || {})") is { } text ? JsonNode.Parse(text) as JsonObject : null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return null;
            }
        }

        private async Task<string?> Answer(NightlyPane pane, string key)
        {
            string? value = null;
            await Until(async () => (value = (await Answers(pane))?[key]?.ToJsonString()) is not null, 10);
            return value;
        }

        /// <summary>Clicks a fixture button, waits for its card over the pane and shoots it.</summary>
        private async Task<BrowserDialog?> Ask(NightlyPane pane, int index, BrowserDialogKind kind, string shot)
        {
            Activate();
            Click(pane, 100, 35 + 50 * index);
            var card = await Until(() => Asked is { Kind: var k } && k == kind && Named<Border>("BrowserDialogCard") is not null, 20);
            var dialog = Asked;
            // The Nightly pane is composed by Avalonia: its live page stays under the card (no picture, no hidden pane).
            Check($"card-{kind.ToString().ToLowerInvariant()}", card && pane.IsEffectivelyVisible && Named<Image>("BrowserDialogSnapshot") is null,
                $"asked={dialog?.Kind} message='{dialog?.Message}' agent={dialog?.CausedByAgent} pane shown={pane.IsEffectivelyVisible}");
            await Task.Delay(400);
            Shot(shot);
            return card ? dialog : null;
        }

        private async Task Questions(NightlyPane pane)
        {
            var cutoverUrl = new Uri(new Uri(url), "cutover.html").AbsoluteUri;
            var downloads = Path.Combine(dir, "downloads");
            Directory.CreateDirectory(downloads);
            vm.Preview.SiteSettings.DownloadFolder = downloads;
            vm.Preview.NavigateCommand.Execute(cutoverUrl);
            var loaded = await Until(() => Title == "K1 cutover" && Matches(pane), 30);
            Check("cutover-page", loaded, $"title='{Title}'");
            if (!loaded) return;
            await Task.Delay(300);

            if (await Ask(pane, 0, BrowserDialogKind.Alert, "q-alert") is not null)
            {
                Press(Named<Button>("BrowserDialogPrimary"), "alert OK");
                Check("alert-answered", await Answer(pane, "a") == "true" && Asked is null, "");
            }
            if (await Ask(pane, 1, BrowserDialogKind.Confirm, "r-confirm") is not null)
            {
                Press(Named<Button>("BrowserDialogPrimary"), "confirm OK");
                Check("confirm-answered", await Answer(pane, "c") == "true", "");
            }
            if (await Ask(pane, 2, BrowserDialogKind.Prompt, "s-prompt") is not null)
            {
                if (Named<TextBox>("BrowserDialogPrompt") is { } box) box.Text = "Ombra";
                Press(Named<Button>("BrowserDialogPrimary"), "prompt OK");
                Check("prompt-answered", await Answer(pane, "p") == "\"Ombra\"", "");
            }
            if (await Ask(pane, 3, BrowserDialogKind.Permission, "t-permission") is { } permission)
            {
                var allow = Named<Button>("BrowserDialogPrimary") ?? Named<Button>("BrowserDialogPrimaryStacked");
                Press(allow, "permission allow");
                // Allowed reaches Gecko: the page's Notification.requestPermission() resolves "granted".
                var g = await Answer(pane, "g");
                Check("permission-answered", g == "\"granted\"", $"{(permission.Request as PermissionRequest)?.Permission} page got {g}");
            }
            if (await Ask(pane, 4, BrowserDialogKind.Download, "u-download") is not null)
            {
                Press(Named<Button>("BrowserDialogPrimary") ?? Named<Button>("BrowserDialogPrimaryStacked"), "download save");
                var saved = Path.Combine(downloads, "file.txt");
                var done = await Until(() => vm.Preview.Downloads.Items.Any(d => d.State == BrowserDownloadState.Finished) && File.Exists(saved), 30);
                var strip = await Until(() => Named<Border>("DownloadsStrip") is not null, 5);
                Check("download-strip", done && strip && File.ReadAllText(saved) == "hello from the cutover fixture\n",
                    $"items={string.Join(",", vm.Preview.Downloads.Items.Select(d => $"{d.FileName}:{d.State}"))} file={File.Exists(saved)}");
                await Task.Delay(400);
                Shot("v-download-strip");
            }
            if (await Ask(pane, 5, BrowserDialogKind.FileChooser, "w-file-picker") is { } chooser)
            {
                // "Choose…" opens the system's open panel, which this shell cannot drive: the card gets the file the panel would return.
                var pick = Path.Combine(dir, "picked.txt");
                File.WriteAllText(pick, "picked");
                chooser.Complete(new List<string> { pick });
                Check("file-picked", await Answer(pane, "f") == "\"picked.txt\"", "");
            }
            if (await Ask(pane, 6, BrowserDialogKind.Credentials, "x-sign-in") is not null)
            {
                if (Named<TextBox>("BrowserDialogUser") is { } user) user.Text = "ombra";
                if (Named<TextBox>("BrowserDialogPassword") is { } password) password.Text = "k1";
                Press(Named<Button>("BrowserDialogPrimary"), "sign in");
                Check("signed-in", await Until(() => Title == "K1 signed in", 20), $"title='{Title}'");
                await Task.Delay(300);
                Shot("y-signed-in");
            }
            vm.Preview.NavigateCommand.Execute(url);
            await Until(() => Fixture.Length > 0 && Matches(pane), 30);
        }

        private async Task Fallback()
        {
            // Nightly keeps crashing (killed as soon as it is up): the supervisor gives up and the preview takes over, saying why.
            var killed = new HashSet<int>();
            var fell = await Until(() =>
            {
                if (vm.Preview.ShowNightlyFallback) return true;
                foreach (var p in Panes().Select(p => p.RuntimePid).Where(p => p > 0 && killed.Add(p)))
                {
                    NightlyMach.Signal(p, 9);
                    Log($"killed nightly pid {p}");
                }
                return false;
            }, 90);
            var bar = await Until(() => Named<Border>("NightlyFallbackBar") is not null, 5);
            Check("fallback-bar", fell && bar && !vm.Preview.UseNightly && Panes().Count == 0,
                $"kills={killed.Count} reason='{vm.Preview.NightlyFallbackReason}' panes={Panes().Count}");
            await Task.Delay(1500); // the built-in preview loads the page
            Shot("z-fallback");
            Press(Named<Button>("NightlyRetryButton"), "Try again");
            var back = await Until(() => vm.Preview.UseNightly && Shown() is { FramesShown: > 0, RuntimePid: > 0 } p && !killed.Contains(p.RuntimePid)
                && Fixture.Length > 0, 60);
            Check("fallback-retry", back && Named<Border>("NightlyFallbackBar") is null, $"pid={Shown()?.RuntimePid} title='{Title}'");
        }
    }
}
