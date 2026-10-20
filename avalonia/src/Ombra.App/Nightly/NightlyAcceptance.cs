using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App.Controls;
using Ombra.App.Platform.Mac;
using Ombra.App.ViewModels;
using static Ombra.App.Platform.Mac.MacRuntime;
using System.Text.Json.Nodes;
using Ombra.ClientCore;

namespace Ombra.App.Nightly;

/// <summary>
/// Development check of the Nightly pane in the app's own window, run only when <c>OMBRA_NIGHTLY_ACCEPTANCE</c> names
/// an output directory and <c>OMBRA_NIGHTLY_ACCEPTANCE_URL</c> a fixture page (the agent browser engine set to Nightly
/// in the runtime options). It opens the page in the browser pane, takes screenshots of the window at two sizes
/// (<c>screencapture -l</c>) and checks the page's device-pixel checkerboard came through 1:1, then types into the
/// page's field, clicks its link, answers its <c>&lt;select&gt;</c> dropdown and its link's context menu ("Open link in
/// new tab": a new Ombra tab) from the keyboard, opens a pop-up (<c>window.open</c>: a tab, which goes when its page
/// closes the window), composes Japanese and Chinese text through the window's <c>NSTextInputClient</c> (where the
/// candidate window goes included), checks the page's pointer shapes, scrolls it, closes a second pane, closes the last
/// one (engine switched to the built-in preview: the runtime must stop and leave no process) and quits with a pane
/// open. Results go to <c>acceptance.log</c> in the output directory, popups shot with the screen under the window
/// (<c>screencapture -R</c>); the process check after quitting is the caller's.
/// <para>Input is posted to the app's own event queue as <c>NSEvent</c>s (<c>-[NSApp postEvent:atStart:]</c>), so it
/// takes the path of hardware input inside the app: <c>sendEvent:</c>, the scroll monitor, Avalonia's view and the
/// pane's handlers. No window server, no Accessibility permission. Composition is the input method's calls on the
/// window's first responder (<c>setMarkedText:selectedRange:replacementRange:</c>, <c>insertText:replacementRange:</c>,
/// <c>firstRectForCharacterRange:actualRange:</c>).</para>
/// <para>The fixture's contract (CSS px, page top-left): a text field <c>#field</c> at (20, 20) 240×32, a link
/// <c>#link</c> to <c>#linked</c> at (20, 70) 200×30 counting its clicks, a canvas at (20, 120) 200×100 painted as a
/// one-device-pixel black and white checkerboard, a <c>&lt;select&gt;</c> of Alpha, Beta, Gamma at (300, 20) 160×32, a
/// button at (300, 70) 160×30 that opens the page with <c>?popup=1</c>, a field <c>#ime</c> at (300, 120) 240×32,
/// <c>cursor: pointer</c> at (300, 170) 100×40 and <c>cursor: grab</c> at (420, 170) 100×40, content far taller than
/// the pane, and the title <c>K1 f=&lt;field&gt; c=&lt;clicks&gt; y=&lt;scrollY&gt; … s=&lt;select&gt; p=&lt;popup&gt;
/// … ime=&lt;#ime&gt; comp=&lt;composition, end:&lt;commit&gt;&gt; cu=… op=&lt;has an opener: 1&gt;</c>.</para>
/// <para>The agent's browser tools are driven as omp drives them: a cmux bridge of its own (<see cref="AgentBrowserBridge"/>,
/// the app's <see cref="PreviewAgentHost"/>) over its Unix socket with its password, then <c>browser.open_split</c>,
/// <c>wait</c>, <c>eval</c>, <c>snapshot</c>, <c>click</c>, <c>type</c>, <c>screenshot</c>, <c>devices</c>,
/// <c>set_device</c> and <c>surface.close</c> on a Nightly tab of the real window; the screenshot is saved next to the
/// window shots. Device mode is checked on <c>device.html</c> next to the fixture (<see cref="DeviceMode"/>).</para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class NightlyAcceptance
{
    public const string DirectoryVariable = "OMBRA_NIGHTLY_ACCEPTANCE";
    public const string UrlVariable = "OMBRA_NIGHTLY_ACCEPTANCE_URL";

    public static void StartIfRequested(Window window, MainViewModel vm)
    {
        if (Environment.GetEnvironmentVariable(DirectoryVariable) is not { Length: > 0 } dir
            || Environment.GetEnvironmentVariable(UrlVariable) is not { Length: > 0 } url)
            return;
        Directory.CreateDirectory(dir);
        window.Opened += (_, _) => Dispatcher.UIThread.Post(() => _ = new Run(window, vm, dir, url).RunAsync());
    }

    private sealed partial class Run(Window window, MainViewModel vm, string dir, string url)
    {
        private readonly StreamWriter _log = new(Path.Combine(dir, "acceptance.log")) { AutoFlush = true };
        private int _failures;

        private void Log(string line) => _log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");

        private void Check(string name, bool pass, string detail)
        {
            Log($"{(pass ? "PASS" : "FAIL")} {name} {detail}");
            if (!pass) _failures++;
        }

        private static async Task<bool> Until(Func<bool> condition, double seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > end) return false;
                await Task.Delay(50);
            }
            return true;
        }

        private static async Task<bool> Until(Func<Task<bool>> condition, double seconds)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (!await condition())
            {
                if (DateTime.UtcNow > end) return false;
                await Task.Delay(200);
            }
            return true;
        }

        private nint NsWindow => NightlyCocoa.WindowOf(window.TryGetPlatformHandle()?.Handle ?? 0);

        private List<NightlyPane> Panes() => window.GetVisualDescendants().OfType<NightlyPane>().ToList();

        private NightlyPane? Shown() => Panes().FirstOrDefault(p => p.IsEffectivelyVisible);

        private string Title => vm.Preview.Title;

        private string Fixture => Title is { } t && t.StartsWith("K1 ", StringComparison.Ordinal) ? t : "";

        public async Task RunAsync()
        {
            try
            {
                await Script();
            }
            catch (Exception e)
            {
                Check("script", false, e.ToString());
            }
            Log($"RESULT {(_failures == 0 ? "PASS" : $"FAIL ({_failures})")}");
            Log("QUIT");
            _log.Dispose();
            window.Close(); // the app's own quit (the tray's Quit does this)
        }

        private async Task Script()
        {
            Log($"engine={vm.AgentBrowserEngine} url={url}");
            if (Environment.GetEnvironmentVariable(DriveVariable) is { Length: > 0 } drive)
            {
                await Drive(drive);
                return;
            }
            window.Position = Platform.TestFocus.Position(window, new PixelPoint(60, 60));
            window.Width = 1320;
            window.Height = 860;
            vm.IsPreviewOpen = true;
            await Task.Delay(500);
            await Consent();
            vm.Preview.NavigateCommand.Execute(url);
            var loaded = await Until(() => Shown() is { FramesShown: > 0 } && Fixture.Length > 0, 60);
            Check("page-loaded", loaded, $"title='{Title}' url={vm.Preview.CurrentUrl} pid={Shown()?.RuntimePid}");
            if (!loaded) return;
            var pid = Shown()!.RuntimePid;

            await Size("a-1320x860", 1320, 860);
            await Size("b-940x640", 940, 640);
            window.Width = 1320;
            window.Height = 860;
            await Until(() => Matches(Shown()), 10);
            await Task.Delay(500);

            // ── input ──
            Activate();
            var active = await Until(() => window.IsActive, 5);
            Log($"window active={active} key={MacObjC.SendBool(NsWindow, "isKeyWindow")}");
            var pane = Shown()!;
            string? lastUrl = null;
            pane.LocationChanged += l =>
            {
                lastUrl = l.Url;
                Log($"location flags={l.Flags} url={l.Url} title='{l.Title}'");
            };
            // The pane sends each key to Nightly as its native event (key code and characters), so the page types the
            // event's characters: with a hardware key, the keyboard layout's. Avalonia saw each key go down.
            var keys = 0;
            EventHandler<Avalonia.Input.KeyEventArgs> countKey = (_, _) => keys++;
            window.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, countKey, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
            Click(pane, 140, 36);
            await Task.Delay(300);
            foreach (var (ch, code) in new[] { ("h", 0x04), ("e", 0x0E), ("l", 0x25), ("l", 0x25), ("o", 0x1F) })
                Key(ch, (ushort)code);
            await Until(() => keys >= 5, 5);
            window.RemoveHandler(Avalonia.Input.InputElement.KeyDownEvent, countKey);
            Check("typing", keys == 5 && await Until(() => Fixture.Contains("f=hello ", StringComparison.Ordinal), 10),
                $"{keys} keys down in Avalonia, title='{Title}'");

            Click(pane, 80, 85);
            Check("link-click", await Until(() => Fixture.Contains(" c=1 ", StringComparison.Ordinal) && lastUrl?.EndsWith("#linked", StringComparison.Ordinal) == true, 10),
                $"title='{Title}' location={lastUrl}");
            await Task.Delay(300);
            Shot("c-typed-clicked");

            await PopupsAndInput(pane);

            ScrollGesture(pane, 400, 400, steps: 12, pixelsPerStep: 40);
            Check("scroll", await Until(() => ScrollY() > 200, 10), $"title='{Title}'");
            await Task.Delay(600);
            Log($"after scroll title='{Title}'");
            Shot("d-scrolled");

            await AgentTools();

            await DeviceMode(pane);

            // ── a second pane, closed ──
            vm.Preview.NewTabCommand.Execute(null);
            var second = vm.Preview.ActiveTab;
            vm.Preview.NavigateCommand.Execute(url);
            var twoPanes = await Until(() => Panes().Count == 2 && Shown() is { FramesShown: > 0 } p && p != pane && Fixture.Length > 0, 30);
            Check("second-pane", twoPanes, $"panes={Panes().Count} pid={Shown()?.RuntimePid}");
            vm.Preview.CloseTabCommand.Execute(second);
            var oneLeft = await Until(() => Panes().Count == 1, 5);
            var framesBefore = pane.FramesShown;
            vm.Preview.SelectTabCommand.Execute(vm.Preview.Tabs[0]);
            var stillRendering = await Until(() => pane.FramesShown > framesBefore || Matches(pane), 5);
            Check("second-pane-closed", oneLeft && pane.RuntimePid == pid && NightlyMach.IsAlive(pid),
                $"panes={Panes().Count} runtime pid={pid} alive={NightlyMach.IsAlive(pid)} first pane ok={stillRendering}");

            // ── the last pane closed: the runtime stops, no process stays ──
            var appPath = NightlyMach.RealPath(Environment.GetEnvironmentVariable(NightlyRuntimeOptions.AppVariable) ?? NightlyRuntimePackage.Find().App ?? "");
            Log($"nightly processes before last close: {string.Join(",", NightlyProcesses(appPath))}");
            vm.SetAgentBrowserEngineCommand.Execute("preview");
            var gone = await Until(() => Panes().Count == 0 && !NightlyMach.IsAlive(pid) && NightlyProcesses(appPath).Count == 0, 15);
            Check("last-pane-closed", gone, $"panes={Panes().Count} pid {pid} alive={NightlyMach.IsAlive(pid)} left={string.Join(",", NightlyProcesses(appPath))}");

            // ── reopened, then the app quits with the pane open ──
            vm.SetAgentBrowserEngineCommand.Execute("nightly");
            // A fresh load (field empty, no clicks): the title is the new runtime's, not the one shown before the switch.
            var reopened = await Until(() => Shown() is { FramesShown: > 0, RuntimePid: > 0 } p && p.RuntimePid != pid
                && Fixture.Contains("f= c=0 ", StringComparison.Ordinal), 60);
            Check("reopened", reopened, $"pid={Shown()?.RuntimePid} title='{Title}'");
            if (reopened) await Cutover();
            Log($"QUIT_PID {Shown()?.RuntimePid}");
            Log($"nightly processes before quit: {string.Join(",", NightlyProcesses(appPath))}");
        }

        // ── the agent's browser tools (omp's cmux requests over the bridge socket) ──

        private async Task AgentTools()
        {
            using var bridge = AgentBrowserBridge.TryStart();
            if (bridge is null)
            {
                Check("agent-bridge", false, "no socket");
                return;
            }
            bridge.Host = new PreviewAgentHost(vm);
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(bridge.Endpoint));
            await using var stream = new System.Net.Sockets.NetworkStream(socket);
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { NewLine = "\n", AutoFlush = true };
            await writer.WriteLineAsync("auth " + bridge.Password);
            Check("agent-auth", await reader.ReadLineAsync() == "OK", bridge.Endpoint);
            var n = 0;
            async Task<JsonObject> Call(string method, JsonObject p)
            {
                await writer.WriteLineAsync(new JsonObject { ["id"] = ++n, ["method"] = method, ["params"] = p }.ToJsonString());
                var line = await reader.ReadLineAsync() ?? "{}";
                Log($"agent {method} → {(line.Length > 300 ? line[..300] + "…" : line)}");
                return JsonNode.Parse(line)!.AsObject();
            }
            static bool Ok(JsonObject a) => a["ok"]?.GetValue<bool>() == true;
            static JsonNode? Result(JsonObject a, string name) => a["result"]?[name];

            var tabsBefore = vm.Preview.Tabs.Count;
            var open = await Call("browser.open_split", new JsonObject { ["url"] = url });
            var surface = Result(open, "surface_id")?.GetValue<string>() ?? "";
            Check("agent-open", Ok(open) && vm.Preview.Tabs.Count == tabsBefore + 1, $"tabs {tabsBefore}→{vm.Preview.Tabs.Count}");
            async Task<JsonObject> Do(string method, JsonObject p)
            {
                p["surface_id"] = surface;
                return await Call(method, p);
            }
            var wait = await Do("browser.wait", new JsonObject { ["load_state"] = "complete", ["timeout_ms"] = 30000 });
            var pane = Shown();
            Check("agent-wait", Ok(wait) && pane is { FramesShown: > 0 }, $"pane frames={pane?.FramesShown} pid={pane?.RuntimePid}");
            var title = await Do("browser.eval", new JsonObject { ["script"] = "document.title" });
            Check("agent-eval", Ok(title) && Result(title, "value")?.GetValue<string>()?.StartsWith("K1 f= c=0", StringComparison.Ordinal) == true, title.ToJsonString());
            var snapshot = await Do("browser.snapshot", new JsonObject { ["interactive"] = true });
            Check("agent-snapshot", Ok(snapshot) && snapshot.ToJsonString().Contains("textbox", StringComparison.Ordinal), "");
            var click = await Do("browser.click", new JsonObject { ["selector"] = "#link" });
            Check("agent-click", Ok(click) && Result(click, "input")?.GetValue<string>() == "native"
                && await Until(() => Fixture.Contains(" c=1 ", StringComparison.Ordinal), 5), $"title='{Title}'");
            var type = await Do("browser.type", new JsonObject { ["selector"] = "#field", ["text"] = "агент ok" });
            Check("agent-type", Ok(type) && Result(type, "input")?.GetValue<string>() == "native"
                && await Until(() => Fixture.Contains("f=агент ok ", StringComparison.Ordinal), 5), $"title='{Title}'");
            await Task.Delay(300);
            var shot = await Do("browser.screenshot", new JsonObject());
            var png = Result(shot, "png_base64")?.GetValue<string>() is { } b64 ? Convert.FromBase64String(b64) : [];
            var file = Path.Combine(dir, "e-agent-screenshot.png");
            await File.WriteAllBytesAsync(file, png);
            var (_, fw, fh) = pane?.FrameLayout ?? default;
            Check("agent-screenshot", Ok(shot) && Result(shot, "width")?.GetValue<int>() == fw && Result(shot, "height")?.GetValue<int>() == fh,
                $"{file} {Result(shot, "width")}x{Result(shot, "height")} (frame {fw}x{fh}) {png.Length} bytes");
            Shot("f-agent-window");

            // The agent's device mode (omp's tab.emulate({ device }) → browser.set_device): the pane's, as the device bar's
            var presets = await Do("browser.devices", new JsonObject());
            Check("agent-devices", Ok(presets) && presets.ToJsonString().Contains("\"Pixel 8\"", StringComparison.Ordinal), "");
            var device = await Do("browser.set_device", new JsonObject { ["preset"] = "pixel 8" });
            Check("agent-set-device", Ok(device) && Result(device, "preset")?.GetValue<string>() == "Pixel 8"
                && Result(device, "width")?.GetValue<int>() == 412 && Result(device, "dpr")?.GetValue<double>() == 2.625
                && Result(device, "touch")?.GetValue<bool>() == true && Result(device, "platform")?.GetValue<string>() == "android"
                && Result(device, "emulated")?.GetValue<bool>() == true && vm.Preview.DevicePreset?.Name == "Pixel 8", device.ToJsonString());
            string? seen = null;
            var emulated = await Until(async () =>
            {
                // The fixture has no meta viewport, so a phone lays it out 980 px wide; the screen is the device's
                var probe = await Do("browser.eval", new JsonObject { ["script"] = "screen.width + ' ' + devicePixelRatio + ' ' + matchMedia('(pointer: coarse)').matches" });
                seen = Result(probe, "value")?.ToJsonString();
                return seen == "\"412 2.625 true\"";
            }, 20);
            Check("agent-device-page", emulated, $"page: {seen}");
            Shot("f2-agent-device");
            var cleared = await Do("browser.set_device", new JsonObject { ["clear"] = true });
            Check("agent-clear-device", Ok(cleared) && Result(cleared, "width") is null && Result(cleared, "dpr")?.GetValue<double>() == 0
                && !vm.Preview.HasDevice && vm.Preview.EmbedDevice is null, cleared.ToJsonString());
            var close = await Call("surface.close", new JsonObject { ["surface_id"] = surface });
            Check("agent-close", Ok(close), "");
            await Task.Delay(500);
            // The pane keeps a tab the user is looking at; the rest of the check wants only the first one.
            foreach (var extra in vm.Preview.Tabs.Skip(1).ToList()) vm.Preview.CloseTabCommand.Execute(extra);
            vm.Preview.SelectTabCommand.Execute(vm.Preview.Tabs[0]);
            await Task.Delay(300);
        }

        // ── device mode: every preset of the device bar (and one turned) as the page sees it, and a tap ──

        /// <summary>What the device fixture's page reports (<c>window.k3</c> counts the taps on <c>#tap</c>).</summary>
        private sealed record DeviceProbe(double W, double H, double Dpr, string Ua, bool Coarse, int TouchPoints, string Orientation,
            bool TouchApi, int TouchStarts, int TouchEnds, int Clicks, string LastClick);

        private static async Task<DeviceProbe?> Probe(NightlyPane pane)
        {
            const string script = "JSON.stringify({ w: innerWidth, h: innerHeight, dpr: devicePixelRatio, ua: navigator.userAgent, "
                + "coarse: matchMedia('(pointer: coarse)').matches, mtp: navigator.maxTouchPoints, ori: screen.orientation.type, "
                + "api: 'ontouchstart' in window, k3: window.k3 || null })";
            try
            {
                if (await pane.Agent.RunScriptAsync(script) is not { } text || JsonNode.Parse(text) is not JsonObject o || o["k3"] is not JsonObject k)
                    return null;
                return new DeviceProbe(o["w"]!.GetValue<double>(), o["h"]!.GetValue<double>(), o["dpr"]!.GetValue<double>(),
                    o["ua"]!.GetValue<string>(), o["coarse"]!.GetValue<bool>(), o["mtp"]!.GetValue<int>(), o["ori"]!.GetValue<string>(),
                    o["api"]!.GetValue<bool>(), k["ts"]!.GetValue<int>(), k["te"]!.GetValue<int>(), k["clicks"]!.GetValue<int>(),
                    k["last"]!.GetValue<string>());
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return null; // between documents (a reload for the new user agent or touch)
            }
        }

        /// <summary>
        /// The fixture <c>device.html</c> next to the main one (meta viewport <c>width=device-width</c>, <c>#tap</c> at
        /// (20, 20) 200×80 counting touchstart, touchend and clicks with the click's pointer type in <c>window.k3</c>):
        /// for each preset the page's size (the preset's exactly, also where the panel is smaller: the pane then shows
        /// the frame scaled down to fit, aspect kept), devicePixelRatio, user agent, <c>(pointer: coarse)</c>,
        /// maxTouchPoints, the TouchEvent API and orientation, then a click on <c>#tap</c> where the scaled frame shows it,
        /// which is a finger's tap (touchstart, touchend, a click of pointer type touch) on a phone or tablet and the
        /// mouse's on a desktop. Leaving device mode gives the page back as it was.
        /// </summary>
        private async Task DeviceMode(NightlyPane pane)
        {
            var deviceUrl = new Uri(new Uri(url), "device.html").AbsoluteUri;
            vm.Preview.NavigateCommand.Execute(deviceUrl);
            DeviceProbe? seen = null;
            var loaded = await Until(async () => (seen = await Probe(pane)) is not null, 30);
            Check("device-page", loaded, deviceUrl);
            if (!loaded) return;
            var own = seen!;
            Log($"device off: {own}");
            var host = (Control)pane.Parent!;
            var runs = PreviewViewModel.DevicePresets.Select(p => (Preset: p, Landscape: false)).Append((PreviewViewModel.DevicePresets[1], true));
            foreach (var (preset, landscape) in runs)
            {
                var id = new string(preset.Name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray()) + (landscape ? "-landscape" : "");
                vm.Preview.ChooseDeviceCommand.Execute(preset);
                if (landscape) vm.Preview.RotateDeviceCommand.Execute(null);
                var mobile = preset.Platform != DevicePlatform.Desktop;
                var width = landscape ? preset.Height : preset.Width;
                var height = landscape ? preset.Width : preset.Height;
                bool Expected(DeviceProbe p) =>
                    Math.Abs(p.W - width) <= 1 && Math.Abs(p.H - height) <= 1
                    && Math.Abs(p.Dpr - preset.PixelRatio) < 0.001
                    && p.Ua == (mobile ? PreviewViewModel.UserAgentFor(preset.Platform) : own.Ua)
                    && p.Coarse == (mobile || own.Coarse) && p.TouchApi == (mobile || own.TouchApi)
                    && p.TouchPoints == (mobile ? 5 : own.TouchPoints)
                    && (!mobile || p.Orientation == (landscape ? "landscape-primary" : "portrait-primary"));
                var ok = await Until(async () => (seen = await Probe(pane)) is { } p && Expected(p) && ShowsViewport(pane, host, width, height), 30);
                Check($"device-{id}", ok, $"{seen} pane={pane.Bounds.Size} host={host.Bounds.Size} frame={pane.FrameLayout} scale={PageScale(pane):0.###}");
                if (!ok) continue;
                await Task.Delay(300);
                var before = seen!;
                Click(pane, 120, 60);
                var tapped = await Until(async () => (seen = await Probe(pane)) is { } p && p.Clicks > before.Clicks, 5);
                var input = mobile
                    ? seen!.TouchStarts > before.TouchStarts && seen.TouchEnds > before.TouchEnds && seen.LastClick == "touch"
                    : seen!.TouchStarts == before.TouchStarts && seen.LastClick == "mouse";
                Check($"device-{id}-tap", tapped && input, $"before {before} after {seen}");
                // Just right of #tap (it ends at 220): no click on it. Without the scale a scaled frame took this for #tap.
                var hit = seen!;
                Click(pane, 260, 60);
                await Task.Delay(800);
                Check($"device-{id}-miss", (seen = await Probe(pane)) is { } after && after.Clicks == hit.Clicks, $"before {hit} after {seen}");
                Shot($"m-device-{id}");
            }
            // A size typed in, taller than any screen here: the off-screen window still takes it (AppKit does not
            // constrain it to a screen), so the page gets it too.
            (vm.Preview.CustomWidth, vm.Preview.CustomHeight) = ("1000", "4000");
            vm.Preview.ApplyCustomSizeCommand.Execute(null);
            var tall = await Until(async () => (seen = await Probe(pane)) is { } p && Math.Abs(p.W - 1000) <= 1 && Math.Abs(p.H - 4000) <= 1
                && ShowsViewport(pane, host, 1000, 4000), 30);
            Check("device-custom-1000x4000", tall, $"{seen} pane={pane.Bounds.Size} frame={pane.FrameLayout} scale={PageScale(pane):0.###} screens={ScreenHeights()}");
            if (tall)
            {
                var before = seen!;
                Click(pane, 120, 60);
                var tapped = await Until(async () => (seen = await Probe(pane)) is { } p && p.Clicks > before.Clicks, 5);
                Check("device-custom-1000x4000-tap", tapped, $"before {before} after {seen}");
            }
            Shot("m-device-custom-1000x4000");
            vm.Preview.ClearDeviceCommand.Execute(null);
            var back = await Until(async () => (seen = await Probe(pane)) is { } p && Math.Abs(p.W - host.Bounds.Width) <= 1
                && Math.Abs(p.Dpr - own.Dpr) < 0.001 && p.Ua == own.Ua && p.Coarse == own.Coarse && p.TouchPoints == own.TouchPoints
                && p.TouchApi == own.TouchApi && Matches(pane), 30);
            Check("device-cleared", back, $"{seen} host={host.Bounds.Size}");
            Shot("n-device-cleared");
            vm.Preview.NavigateCommand.Execute(url);
            await Until(() => Fixture.Length > 0 && Matches(pane), 30);
            await Task.Delay(300);
        }

        /// <summary>
        /// The pane shows the page at <paramref name="width"/>×<paramref name="height"/> CSS px: the frame is that size at
        /// the backing scale; the pane is that size where the panel has room (1:1) and otherwise the largest that fits
        /// with the same aspect, the frame filling it.
        /// </summary>
        private bool ShowsViewport(NightlyPane pane, Control host, double width, double height)
        {
            if (pane.FramesShown == 0) return false;
            var (shown, w, h) = pane.FrameLayout;
            var s = window.RenderScaling;
            var room = host.Bounds.Size.Deflate(pane.Margin);
            var fit = Math.Min(1, Math.Min(room.Width / width, room.Height / height));
            return Math.Abs(w - width * s) <= 1 && Math.Abs(h - height * s) <= 1
                && Math.Abs(pane.Bounds.Width - width * fit) <= 1 / s + 0.01 && Math.Abs(pane.Bounds.Height - height * fit) <= 1 / s + 0.01
                && Math.Abs(shown.Width - pane.Bounds.Width) <= 1 / s + 0.01 && Math.Abs(shown.Height - pane.Bounds.Height) <= 1 / s + 0.01
                && shown.X >= 0 && shown.Y >= 0 && shown.X <= 1 / s && shown.Y <= 1 / s;
        }

        /// <summary>Pane DIP per CSS px of the page: 1 unless a device larger than the panel is scaled down.</summary>
        private double PageScale(NightlyPane pane)
        {
            var (shown, w, _) = pane.FrameLayout;
            return w > 0 && shown.Width > 0 ? shown.Width * window.RenderScaling / w : 1;
        }

        // ── the page's popups and windows, composition, pointer shape ──

        private async Task PopupsAndInput(NightlyPane pane)
        {
            var popups = pane.Popups;
            var first = vm.Preview.ActiveTab;
            string Rows() => string.Join(",", popups.Rows.Select(r => Avalonia.Automation.AutomationProperties.GetName(r)));

            // <select>: Ombra's dropdown under the field, the page's choice on it; the keyboard picks Gamma.
            Click(pane, 380, 36);
            var dropdown = await Until(() => popups.Current is { Kind: NightlyPopup.PopupKind.Select }, 10);
            Check("select-popup", dropdown && Rows() == "Alpha,Beta,Gamma" && popups.Highlight == 0, $"rows={Rows()} highlight={popups.Highlight}");
            await Task.Delay(500);
            ScreenShot("g-select");
            Key("\uF701", 125); // ↓
            Key("\uF701", 125);
            await Until(() => popups.Highlight == 2, 5);
            Key("\r", 36); // Return
            Check("select-choice", await Until(() => Fixture.Contains(" s=gamma ", StringComparison.Ordinal) && popups.Current is null, 10),
                $"title='{Title}'");

            // The link's context menu at the pointer; its first row opens the link in a new Ombra tab.
            var tabs = vm.Preview.Tabs.Count;
            RightClick(pane, 80, 85);
            var menu = await Until(() => popups.Current is { Kind: NightlyPopup.PopupKind.Menu }, 10);
            var commands = string.Join(",", popups.Current?.Items.Select(i => i.Command) ?? []);
            Check("context-menu", menu && commands.StartsWith("open-link,copy-link", StringComparison.Ordinal)
                && Rows().StartsWith(Localization.Loc.T("Open link in new tab"), StringComparison.Ordinal), $"commands={commands} rows={Rows()}");
            await Task.Delay(500);
            ScreenShot("h-context-menu");
            Key("\uF701", 125);
            Key("\r", 36);
            var linkTab = await Until(() => vm.Preview.Tabs.Count == tabs + 1 && vm.Preview.ActiveTab is { IsPopup: true } t && t.Opener == first
                && Shown() is { FramesShown: > 0 } p && p != pane && vm.Preview.CurrentUrl?.ToString().EndsWith("#linked", StringComparison.Ordinal) == true, 20);
            Check("context-menu-new-tab", linkTab, $"tabs {tabs}→{vm.Preview.Tabs.Count} url={vm.Preview.CurrentUrl} panes={Panes().Count}");
            await Task.Delay(500);
            ScreenShot("i-link-tab");
            await BackToFirst(first, pane);

            // window.open from a click: the pop-up is a tab next to its opener, its page has the opener; then the page
            // closes its window and the tab goes.
            Click(pane, 380, 85);
            var opened = await Until(() => vm.Preview.Tabs.Count == tabs + 1 && vm.Preview.ActiveTab is { IsPopup: true } t && t.Opener == first
                && Shown() is { FramesShown: > 0 } p && p != pane && Fixture.Contains(" p=1 ", StringComparison.Ordinal) && Fixture.EndsWith(" op=1", StringComparison.Ordinal), 20);
            Check("window-open-tab", opened, $"tabs {tabs}→{vm.Preview.Tabs.Count} title='{Title}'");
            await Task.Delay(500);
            ScreenShot("j-window-open-tab");
            if (opened)
            {
                var popupTab = vm.Preview.ActiveTab;
                try { await Shown()!.Agent.RunScriptAsync("window.close()"); }
                catch (Exception e) { Log($"window.close script: {e.Message}"); } // its document may go before the answer
                Check("window-close-tab", await Until(() => !vm.Preview.Tabs.Contains(popupTab) && Panes().Count == 1, 10),
                    $"tabs={vm.Preview.Tabs.Count} panes={Panes().Count}");
            }
            await BackToFirst(first, pane);

            // Composition through the window's text input client, as an input method drives it: Japanese marked text
            // (the candidate window asks where it is), its commit; then Chinese pinyin and its commit.
            Click(pane, 420, 136);
            await Task.Delay(300);
            var client = TextInputClient();
            Log($"text input client {MacObjC.ClassName(MacObjC.ClassOf(client))}");
            SetMarkedText(client, "にほん");
            Check("ime-marked", await Until(() => Fixture.Contains(" ime=にほん comp=にほん ", StringComparison.Ordinal), 10), $"title='{Title}'");
            var field = ScreenRect(pane, 300, 120, 240, 32);
            var candidate = default(CGRect);
            var inField = await Until(() =>
            {
                candidate = FirstRect(client);
                return candidate.Height > 0 && candidate.X >= field.X - 2 && candidate.X <= field.X + field.Width
                    && candidate.Y >= field.Y - 2 && candidate.Y + candidate.Height <= field.Y + field.Height + 2;
            }, 10);
            Check("ime-candidate-rect", inField, $"firstRectForCharacterRange={candidate} field on screen={field}");
            await Task.Delay(300);
            ScreenShot("k-ime-marked");
            InsertText(client, "日本");
            Check("ime-commit", await Until(() => Fixture.Contains(" ime=日本 comp=end:日本 ", StringComparison.Ordinal), 10), $"title='{Title}'");
            SetMarkedText(client, "zhong wen");
            Check("ime-pinyin", await Until(() => Fixture.Contains(" ime=日本zhong wen comp=zhong wen ", StringComparison.Ordinal), 10), $"title='{Title}'");
            InsertText(client, "中文");
            Check("ime-pinyin-commit", await Until(() => Fixture.Contains(" ime=日本中文 comp=end:中文 ", StringComparison.Ordinal), 10), $"title='{Title}'");
            await Task.Delay(300);
            ScreenShot("l-ime-committed");
            await CompositionEndedByNightly(pane, 1);

            // The pointer shape the page wants: the hand over cursor: pointer, the open hand over grab.
            var moves = new List<string>();
            EventHandler<Avalonia.Input.PointerEventArgs> recordMove = (_, e) => moves.Add($"{e.GetPosition(pane):0.#}");
            window.AddHandler(Avalonia.Input.InputElement.PointerMovedEvent, recordMove, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
            Hover(pane, 350, 190);
            var hand = await Until(() => pane.PageCursor == Avalonia.Input.StandardCursorType.Hand, 5);
            await Task.Delay(200);
            var nsHand = CurrentCursorIs("pointingHandCursor");
            Check("cursor-pointer", hand && nsHand, $"pane={pane.PageCursor} [NSCursor currentCursor] is pointingHandCursor={nsHand} moves={string.Join(" ", moves)}");
            Hover(pane, 440, 190); // the grab box's left part: the pane is 480 px wide, its overlay scroll bar at the edge
            var grab = await Until(() => pane.PageCursor == Avalonia.Input.StandardCursorType.DragMove, 5);
            await Task.Delay(200);
            Check("cursor-grab", grab && !CurrentCursorIs("pointingHandCursor"), $"pane={pane.PageCursor} moves={string.Join(" ", moves)}");
            Hover(pane, 150, 400);
            window.RemoveHandler(Avalonia.Input.InputElement.PointerMovedEvent, recordMove);
        }

        /// <summary>The first tab shown again (a pop-up tab closed), its pane on screen.</summary>
        private async Task BackToFirst(PreviewTab first, NightlyPane pane)
        {
            foreach (var extra in vm.Preview.Tabs.Where(t => t != first).ToList()) vm.Preview.CloseTabCommand.Execute(extra);
            vm.Preview.SelectTabCommand.Execute(first);
            await Until(() => Shown() == pane && Panes().Count == 1, 10);
            await Task.Delay(300);
        }

        // ── sizes and screenshots ──

        private async Task Size(string name, double width, double height)
        {
            window.Width = width;
            window.Height = height;
            var matched = await Until(() => Matches(Shown()), 15);
            await Task.Delay(700); // the frame after the last resize is on screen
            matched &= Matches(Shown());
            var pane = Shown()!;
            var (shown, w, h) = pane.FrameLayout;
            var s = window.RenderScaling;
            Check($"{name}-frame-1to1", matched, $"pane={pane.Bounds.Width}x{pane.Bounds.Height} dip scale={s} frame={w}x{h} px shown={shown}");
            var file = Shot(name);
            Check($"{name}-checkerboard", Checkerboard(file, pane, out var detail), detail);
        }

        /// <summary>The last frame fills the pane at one surface pixel per device pixel, from its top-left corner.</summary>
        private bool Matches(NightlyPane? pane)
        {
            if (pane is null || pane.FramesShown == 0) return false;
            var (shown, w, h) = pane.FrameLayout;
            var s = window.RenderScaling;
            return Math.Abs(w - pane.Bounds.Width * s) <= 1 && Math.Abs(h - pane.Bounds.Height * s) <= 1
                && shown.X == 0 && shown.Y == 0 && Math.Abs(shown.Width * s - w) < 0.01 && Math.Abs(shown.Height * s - h) < 0.01;
        }

        private string Shot(string name)
        {
            var file = Path.Combine(dir, name + ".png");
            var number = MacObjC.SendLong(NsWindow, "windowNumber");
            // screencapture is another process, which needs screen recording permission; the app's own window it does not.
            // Without the permission a harness run never starts it: macOS would ask in a window that takes the focus.
            if (!Platform.TestFocus.ScreenCaptureAllowed)
            {
                Log($"SHOT {file} window={number} in-process={OwnWindowImage(number, file)} (no Screen Recording)");
                return file;
            }
            using var p = Process.Start(new ProcessStartInfo("/usr/sbin/screencapture", ["-x", "-o", "-l", number.ToString(), file]))!;
            p.WaitForExit();
            var own = p.ExitCode != 0 && OwnWindowImage(number, file);
            Log($"SHOT {file} window={number} exit={p.ExitCode}{(p.ExitCode != 0 ? $" in-process={own}" : "")}");
            return file;
        }

        /// <summary>The app's own window as the window server composes it (Nightly's layer and web views included), as PNG.</summary>
        private static unsafe bool OwnWindowImage(long windowNumber, string file)
        {
            var image = CGWindowListCreateImage(new CGRect(double.PositiveInfinity, double.PositiveInfinity, 0, 0),
                8 /* kCGWindowListOptionIncludingWindow */, (uint)windowNumber, 1 | 8 /* ignore framing, best resolution */);
            if (image == 0) return false;
            var rep = MacObjC.Send(MacObjC.Send(MacObjC.Cls("NSBitmapImageRep"), "alloc"), "initWithCGImage:", image);
            try
            {
                var png = ((delegate* unmanaged<nint, nint, nuint, nint, nint>)MacObjC.MsgSend)(rep, MacObjC.Sel("representationUsingType:properties:"), 4 /* PNG */, 0);
                using var path = new NSStr(file);
                return png != 0 && ((delegate* unmanaged<nint, nint, nint, byte, byte>)MacObjC.MsgSend)(png, MacObjC.Sel("writeToFile:atomically:"), path.Handle, 1) != 0;
            }
            finally
            {
                if (rep != 0) MacObjC.SendVoid(rep, "release");
                NightlyMach.CFRelease(image);
            }
        }

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern nint CGWindowListCreateImage(CGRect screenBounds, uint listOption, uint windowId, uint imageOption);

        /// <summary>The window's area of the screen with the app's popups over it (their windows are not the window's).</summary>
        private unsafe string ScreenShot(string name)
        {
            var file = Path.Combine(dir, name + ".png");
            var frame = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(NsWindow, MacObjC.Sel("frame"));
            var rect = FormattableString.Invariant($"{frame.X},{PrimaryScreenHeight() - frame.Y - frame.Height},{frame.Width},{frame.Height}");
            if (!Platform.TestFocus.ScreenCaptureAllowed)
            {
                // No Screen Recording (see Shot): the window alone, without the popups over it.
                var number = MacObjC.SendLong(NsWindow, "windowNumber");
                Log($"SHOT {file} screen rect={rect} in-process window={OwnWindowImage(number, file)} (no Screen Recording)");
                return file;
            }
            using var p = Process.Start(new ProcessStartInfo("/usr/sbin/screencapture", ["-x", "-R", rect, file]))!;
            p.WaitForExit();
            Log($"SHOT {file} screen rect={rect} exit={p.ExitCode}");
            return file;
        }

        /// <summary>Every screen's height in points, for the log.</summary>
        private static unsafe string ScreenHeights()
        {
            var screens = MacObjC.Send(MacObjC.Cls("NSScreen"), "screens");
            var heights = new List<double>();
            for (long i = 0, n = MacObjC.SendLong(screens, "count"); i < n; i++)
            {
                var screen = ((delegate* unmanaged<nint, nint, nint, nint>)MacObjC.MsgSend)(screens, MacObjC.Sel("objectAtIndex:"), (nint)i);
                heights.Add(((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(screen, MacObjC.Sel("frame")).Height);
            }
            return string.Join(",", heights);
        }

        private static unsafe double PrimaryScreenHeight()
        {
            var screens = MacObjC.Send(MacObjC.Cls("NSScreen"), "screens");
            return ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(MacObjC.Send(screens, "firstObject"), MacObjC.Sel("frame")).Height;
        }

        /// <summary>
        /// The fixture's checkerboard in the screenshot: every device pixel black or white and unlike its neighbours
        /// (any scaling or offset of the frame blends them into grey).
        /// </summary>
        private unsafe bool Checkerboard(string file, NightlyPane pane, out string detail)
        {
            var s = window.RenderScaling;
            var (shown, _, _) = pane.FrameLayout;
            var topLeft = pane.TranslatePoint(new Point(shown.X + 20, shown.Y + 120), window) ?? default;
            // The screenshot is the window's frame (no shadow) at the backing scale; the canvas from the client area.
            var frame = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(NsWindow, MacObjC.Sel("frame"));
            var onScreen = NightlyCocoa.ScreenBottomLeft(NsWindow, topLeft.X, topLeft.Y, 200, 100) ?? default;
            int x0 = (int)Math.Round((onScreen.X - frame.X) * s), y0 = (int)Math.Round((frame.Y + frame.Height - (onScreen.Y + 100)) * s);
            int cw = (int)Math.Round(200 * s), ch = (int)Math.Round(100 * s);
            using var bitmap = new Bitmap(file);
            var size = bitmap.PixelSize;
            if (x0 < 0 || y0 < 0 || x0 + cw > size.Width || y0 + ch > size.Height)
            {
                detail = $"canvas {x0},{y0} {cw}x{ch} outside the {size.Width}x{size.Height} shot";
                return false;
            }
            var stride = size.Width * 4;
            var pixels = new byte[stride * size.Height];
            fixed (byte* buffer = pixels) bitmap.CopyPixels(new PixelRect(size), (nint)buffer, pixels.Length, stride);
            int Luma(int x, int y) { var o = y * stride + x * 4; return (pixels[o] + pixels[o + 1] + pixels[o + 2]) / 3; }
            int bad = 0, total = 0, mixed = 0;
            for (var y = y0 + 1; y < y0 + ch - 1; y++)
            for (var x = x0 + 1; x < x0 + cw - 1; x++)
            {
                total++;
                var v = Luma(x, y);
                if (v is > 24 and < 231) { mixed++; bad++; continue; }
                var dark = v < 128;
                if (dark == Luma(x + 1, y) < 128 || dark == Luma(x, y + 1) < 128) bad++;
            }
            detail = $"canvas at {x0},{y0} {cw}x{ch} px in {Path.GetFileName(file)} {size.Width}x{size.Height}: {bad}/{total} pixels off ({mixed} grey)";
            return bad == 0;
        }

        // ── input: NSEvents posted to the app's own queue ──

        private static nint App => MacObjC.Send(MacObjC.Cls("NSApplication"), "sharedApplication");

        private static double Uptime() => SendDouble(MacObjC.Send(MacObjC.Cls("NSProcessInfo"), "processInfo"), "systemUptime");

        /// <summary>The window takes the keyboard without activating the app (<see cref="Platform.TestFocus.Activate"/>).</summary>
        private void Activate() => Platform.TestFocus.Activate(window);

        private unsafe void Post(nint ev) => ((delegate* unmanaged<nint, nint, nint, byte, void>)MacObjC.MsgSend)(App, MacObjC.Sel("postEvent:atStart:"), ev, 0);

        /// <summary>A point of the page (CSS px from the pane's frame, scaled as shown) in the window's coordinates (bottom-left origin).</summary>
        private CGPoint WindowPoint(NightlyPane pane, double x, double y)
        {
            var (shown, _, _) = pane.FrameLayout;
            var k = PageScale(pane);
            return ClientToWindow(pane.TranslatePoint(new Point(shown.X + x * k, shown.Y + y * k), window) ?? default);
        }

        /// <summary>A point of the window's client area (DIP, top-left origin) in the window's coordinates (bottom-left origin).</summary>
        private unsafe CGPoint ClientToWindow(Point client)
        {
            var content = MacObjC.Send(NsWindow, "contentView");
            var bounds = ((delegate* unmanaged<nint, nint, CGRect>)MacObjC.MsgSend)(content, MacObjC.Sel("bounds"));
            var inView = new CGPoint(client.X, MacObjC.SendBool(content, "isFlipped") ? client.Y : bounds.Height - client.Y);
            return ((delegate* unmanaged<nint, nint, CGPoint, nint, CGPoint>)MacObjC.MsgSend)(content, MacObjC.Sel("convertPoint:toView:"), inView, 0);
        }

        private unsafe nint MouseEvent(ulong type, CGPoint at, long clicks) =>
            ((delegate* unmanaged<nint, nint, ulong, CGPoint, ulong, double, long, nint, long, long, float, nint>)MacObjC.MsgSend)(
                MacObjC.Cls("NSEvent"), MacObjC.Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
                type, at, 0, Uptime(), MacObjC.SendLong(NsWindow, "windowNumber"), 0, 0, clicks, type is 1 or 3 ? 1f : 0f);

        private void Click(NightlyPane pane, double x, double y)
        {
            var at = WindowPoint(pane, x, y);
            Post(MouseEvent(5, at, 0)); // NSEventTypeMouseMoved
            Post(MouseEvent(1, at, 1)); // LeftMouseDown
            Post(MouseEvent(2, at, 1)); // LeftMouseUp
            Log($"click page ({x},{y}) window ({at.X:0.#},{at.Y:0.#})");
        }

        private void RightClick(NightlyPane pane, double x, double y)
        {
            var at = WindowPoint(pane, x, y);
            Post(MouseEvent(5, at, 0)); // NSEventTypeMouseMoved
            Post(MouseEvent(3, at, 1)); // RightMouseDown
            Post(MouseEvent(4, at, 1)); // RightMouseUp
            Log($"right click page ({x},{y}) window ({at.X:0.#},{at.Y:0.#})");
        }

        /// <summary>
        /// The pointer moved over the page: <c>mouseMoved:</c> to the window's view under it (Avalonia's), which is
        /// where AppKit's tracking area sends a hardware move; a queued mouse-moved event did not reach it.
        /// </summary>
        private unsafe void Hover(NightlyPane pane, double x, double y)
        {
            var at = WindowPoint(pane, x, y);
            var view = ((delegate* unmanaged<nint, nint, CGPoint, nint>)MacObjC.MsgSend)(MacObjC.Send(NsWindow, "contentView"), MacObjC.Sel("hitTest:"), at);
            MacObjC.SendVoid(view, "mouseMoved:", MouseEvent(5, at, 0));
            Log($"hover page ({x},{y}) window ({at.X:0.#},{at.Y:0.#}) to {MacObjC.ClassName(MacObjC.ClassOf(view))}");
        }

        /// <summary>A rectangle of the page (CSS px from the pane's frame, scaled as shown) on the screen (bottom-left origin).</summary>
        private CGRect ScreenRect(NightlyPane pane, double x, double y, double width, double height)
        {
            var (shown, _, _) = pane.FrameLayout;
            var k = PageScale(pane);
            var client = pane.TranslatePoint(new Point(shown.X + x * k, shown.Y + y * k), window) ?? default;
            var (sx, sy) = NightlyCocoa.ScreenBottomLeft(NsWindow, client.X, client.Y, width * k, height * k) ?? default;
            return new CGRect(sx, sy, width * k, height * k);
        }

        private static bool CurrentCursorIs(string cursor) =>
            MacObjC.Send(MacObjC.Cls("NSCursor"), "currentCursor") == MacObjC.Send(MacObjC.Cls("NSCursor"), cursor);

        // ── composition: NSTextInputClient calls on the window's first responder, as an input method makes them ──

        private static readonly NSRange NotFound = new(nuint.MaxValue >> 1, 0); // NSNotFound (NSIntegerMax)

        private nint TextInputClient()
        {
            var responder = MacObjC.Send(NsWindow, "firstResponder");
            return MacObjC.RespondsTo(responder, "setMarkedText:selectedRange:replacementRange:") ? responder : 0;
        }

        private unsafe void SetMarkedText(nint client, string text)
        {
            using var s = new NSStr(text);
            ((delegate* unmanaged<nint, nint, nint, NSRange, NSRange, void>)MacObjC.MsgSend)(
                client, MacObjC.Sel("setMarkedText:selectedRange:replacementRange:"), s.Handle, new NSRange((nuint)text.Length, 0), NotFound);
            Log($"setMarkedText '{text}'");
        }

        private unsafe void InsertText(nint client, string text)
        {
            using var s = new NSStr(text);
            ((delegate* unmanaged<nint, nint, nint, NSRange, void>)MacObjC.MsgSend)(client, MacObjC.Sel("insertText:replacementRange:"), s.Handle, NotFound);
            Log($"insertText '{text}'");
        }

        /// <summary>Where the input method puts its candidate window (screen, bottom-left origin).</summary>
        private static unsafe CGRect FirstRect(nint client) =>
            ((delegate* unmanaged<nint, nint, NSRange, nint, CGRect>)MacObjC.MsgSend)(client, MacObjC.Sel("firstRectForCharacterRange:actualRange:"), new NSRange(0, 0), 0);

        private unsafe void Key(string characters, ushort keyCode)
        {
            foreach (var type in new ulong[] { 10, 11 }) // KeyDown, KeyUp
            {
                using var chars = new NSStr(characters);
                var ev = ((delegate* unmanaged<nint, nint, ulong, CGPoint, ulong, double, long, nint, nint, nint, byte, ushort, nint>)MacObjC.MsgSend)(
                    MacObjC.Cls("NSEvent"), MacObjC.Sel("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:"),
                    type, default, 0, Uptime(), MacObjC.SendLong(NsWindow, "windowNumber"), 0, chars.Handle, chars.Handle, 0, keyCode);
                Post(ev);
            }
        }

        /// <summary>A trackpad pan over the page: began, changed (precise pixel deltas, content moving up), ended.</summary>
        private unsafe void ScrollGesture(NightlyPane pane, double x, double y, int steps, int pixelsPerStep)
        {
            var at = WindowPoint(pane, x, y);
            var screen = ((delegate* unmanaged<nint, nint, CGPoint, CGPoint>)MacObjC.MsgSend)(NsWindow, MacObjC.Sel("convertPointToScreen:"), at);
            var primaryHeight = PrimaryScreenHeight();
            var number = MacObjC.SendLong(NsWindow, "windowNumber");
            void Wheel(long phase, int delta)
            {
                var cg = CGEventCreateScrollWheelEvent2(0, 0 /* pixels */, 1, delta, 0, 0);
                try
                {
                    CGEventSetLocation(cg, new CGPoint(screen.X, primaryHeight - screen.Y));
                    CGEventSetIntegerValueField(cg, 99 /* kCGScrollWheelEventScrollPhase */, phase);
                    CGEventSetIntegerValueField(cg, 91 /* window under the pointer */, number);
                    CGEventSetIntegerValueField(cg, 92 /* …that can handle this event */, number);
                    var ev = MacObjC.Send(MacObjC.Cls("NSEvent"), "eventWithCGEvent:", cg);
                    // Bound to the window (AppKit leaves a CGEvent's scroll windowless; a queued one stays so): straight to
                    // -[NSApp sendEvent:], where local monitors see it as they see hardware scrolls.
                    if (MacObjC.RespondsTo(ev, "_eventRelativeToWindow:") && MacObjC.Send(ev, "_eventRelativeToWindow:", NsWindow) is var relative and not 0)
                        ev = relative;
                    MacObjC.SendVoid(App, "sendEvent:", ev);
                }
                finally
                {
                    NightlyMach.CFRelease(cg);
                }
            }
            Wheel(1, 0); // kCGScrollPhaseBegan
            for (var i = 0; i < steps; i++) Wheel(2, -pixelsPerStep); // Changed; negative: the content moves up
            Wheel(4, 0); // Ended
            Log($"scroll gesture page ({x},{y}) {steps}×{pixelsPerStep}px");
        }

        private int ScrollY()
        {
            var i = Fixture.IndexOf(" y=", StringComparison.Ordinal);
            if (i < 0) return 0;
            var rest = Fixture[(i + 3)..];
            var end = rest.IndexOf(' ');
            return int.TryParse(end < 0 ? rest : rest[..end], out var y) ? y : 0;
        }

        /// <summary>Processes run from the Nightly bundle (any user's tools aside, Ombra's runtime and its children).</summary>
        private static List<int> NightlyProcesses(string appPath)
        {
            var prefix = appPath.TrimEnd('/') + "/";
            var found = new List<int>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                    if (NightlyMach.ExecutableOf(p.Id) is { } exe && exe.StartsWith(prefix, StringComparison.Ordinal)) found.Add(p.Id);
            }
            return found;
        }

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern nint CGEventCreateScrollWheelEvent2(nint source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern void CGEventSetLocation(nint ev, CGPoint location);

        [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        private static extern void CGEventSetIntegerValueField(nint ev, int field, long value);
    }
}
