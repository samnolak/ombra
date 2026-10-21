using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Ombra.App.Nightly;
using Ombra.ClientCore;
using Ombra.ClientCore.Browser;

namespace Ombra.Tests;

/// <summary>
/// The agent's browser tools on a Nightly pane (<see cref="NightlyAgent"/>): the Script / ScriptResult layouts against
/// ZenEmbedHost.mm's structs, the screenshot PNG, and, with a real embed-capable Nightly (<c>OMBRA_TEST_NIGHTLY_APP</c>),
/// omp's cmux requests (<see cref="AgentBrowserBridge"/>) end to end against a real pane: open, wait, eval, snapshot,
/// click / type / fill / hover / press as native input, scroll and screenshot. In the runtime tests' class, so the real
/// runtime tests never run side by side.
/// </summary>
public sealed partial class NightlyRuntimeTests
{
    [Fact]
    public void ScriptMessagesHaveTheLayoutOfTheNativeStructs()
    {
        var m = NightlyProtocol.ScriptMessage(7, 9, NightlyProtocol.ScriptKind.Isolated, unchecked((nint)0x1234_5678_9ABC), 10, 4);
        Assert.Equal(64, m.Length);
        Assert.Equal(0x80000013u, BinaryPrimitives.ReadUInt32LittleEndian(m)); // COPY_SEND | COMPLEX
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(4)));
        Assert.Equal(NightlyProtocol.Script, BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(20)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(24))); // descriptor count
        Assert.Equal(0x1234_5678_9ABCUL, BinaryPrimitives.ReadUInt64LittleEndian(m.AsSpan(28))); // OOL address
        Assert.Equal(0, m[36]); // not deallocated: the sender's memory
        Assert.Equal(1, m[37]); // MACH_MSG_VIRTUAL_COPY
        Assert.Equal(1, m[39]); // MACH_MSG_OOL_DESCRIPTOR
        Assert.Equal(14u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(40)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(44)));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(48)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(52)));
        Assert.Equal(10u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(56)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(60)));

        var flags = NightlyProtocol.FlagsMessage(3, 56, 1UL << 17);
        Assert.Equal(120, flags.Length);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(flags.AsSpan(28))); // flagsChanged
        Assert.Equal(56, BinaryPrimitives.ReadInt32LittleEndian(flags.AsSpan(32)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(flags.AsSpan(36)));

        var result = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20), NightlyProtocol.ScriptResult);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(44), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(48), 77);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(52), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(56), 6);
        var read = NightlyProtocol.ReadScriptResult(result, "Ошибка"u8.ToArray()[..6]);
        Assert.Equal(new NightlyProtocol.ScriptResultInfo(5, 77, NightlyProtocol.ScriptStatus.Error, "Оши"), read);
        // A length past the out-of-line bytes is refused, not read.
        Assert.Null(NightlyProtocol.ReadScriptResult(result, null));
    }

    [Fact]
    public void TheScreenshotIsAPngOfTheFramesPixels()
    {
        const int w = 5, h = 3;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 37);
        var png = NightlySnapshot.Png(w, h, rgb);
        var shot = AgentScreenshot.FromPng(png);
        Assert.NotNull(shot);
        Assert.Equal((w, h), (shot!.Width, shot.Height));
        Assert.Equal(rgb, DecodeRgb(png, out var dw, out var dh));
        Assert.Equal((w, h), (dw, dh));
    }

    /// <summary>The RGB rows of an 8-bit truecolour PNG with filters None or Sub (what <see cref="NightlySnapshot"/> writes); chunk CRCs checked.</summary>
    private static byte[] DecodeRgb(byte[] png, out int width, out int height)
    {
        width = height = 0;
        using var idat = new MemoryStream();
        for (var o = 8; o < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(o));
            var type = Encoding.ASCII.GetString(png, o + 4, 4);
            var data = png.AsSpan(o + 8, length);
            Assert.Equal(Crc32(png.AsSpan(o + 4, length + 4)), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(o + 8 + length)));
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);
                Assert.Equal(2, data[9]);
            }
            else if (type == "IDAT") idat.Write(data);
            o += 12 + length;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var bytes = raw.ToArray();
        var row = width * 3;
        var rgb = new byte[row * height];
        for (var y = 0; y < height; y++)
        {
            var filter = bytes[y * (row + 1)];
            Assert.InRange(filter, 0, 1);
            for (var i = 0; i < row; i++)
            {
                var v = bytes[y * (row + 1) + 1 + i];
                rgb[y * row + i] = (byte)(filter == 1 && i >= 3 ? v + rgb[y * row + i - 3] : v);
            }
        }
        return rgb;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    // ── the real runtime, driven by omp's cmux requests ──

    /// <summary>A pane without a window around it: the Nightly window's frames, address and the agent's target.</summary>
    private sealed class AgentPane(NightlyRuntime runtime) : INightlyView, NightlyAgent.ITarget
    {
        public NightlyFrames Frames { get; } = new();
        public volatile uint Window;
        public volatile int FrameCount;
        public double Scale = 2;
        public NightlyProtocol.LocationInfo? Location;
        public NightlyRuntime? Runtime => runtime;
        public uint WindowId => Window;
        public long LastUserInput => 0;
        public Task<IDisposable> AgentActsAsync() => Task.FromResult<IDisposable>(new NoScope());
        public Task ShownFrameAsync(TimeSpan timeout, CancellationToken ct) => Task.CompletedTask;
        private sealed class NoScope : IDisposable { public void Dispose() { } }
        public void OnRuntimeState(NightlyState state, string? detail) { }
        public void OnAttached(uint windowId)
        {
            Window = windowId;
            runtime.Send(NightlyProtocol.ResizeMessage(windowId, 800, 600, null));
        }
        public void OnDetached()
        {
            Window = 0;
            Frames.Clear();
        }
        public void OnWindow(NightlyProtocol.WindowInfo window)
        {
            if (window.Scale > 0) Scale = window.Scale;
        }
        public void OnSurface(NightlyProtocol.SurfaceInfo surface, nint ioSurface)
        {
            Frames.AddSurface(surface, ioSurface);
            NightlyMach.CFRelease(ioSurface);
        }
        public void OnFrame(NightlyProtocol.FrameInfo frame)
        {
            Frames.SetLatest(frame);
            FrameCount++;
            runtime.ReleaseFrame(frame.WindowId, frame.Seq);
        }
        public void OnLocation(NightlyProtocol.LocationInfo location) => Location = location;
        // Popups, pointer shapes, the composition's place and the windows the page opens (NightlyPopupTests).
        public readonly List<(uint Id, NightlyPopup Popup)> Popups = [];
        public readonly List<uint> ClosedPopups = [];
        public readonly List<uint> PopupWindows = [];
        public int Cursor = -1;
        public NightlyProtocol.TextRectInfo? TextRect;
        public bool WindowClosed;
        public void OnPopup(uint popupId, NightlyPopup popup) => Popups.Add((popupId, popup));
        public void OnPopupClosed(uint popupId) => ClosedPopups.Add(popupId);
        public void OnCursor(int cursor) => Cursor = cursor;
        public void OnTextRect(NightlyProtocol.TextRectInfo rect) => TextRect = rect;
        public void OnPopupWindow(uint windowId) => PopupWindows.Add(windowId);
        public void OnWindowClosed() => WindowClosed = true;
        public void OnDialog(uint dialogId, string json) => _ = runtime.ReplyDialogAsync(WindowId, dialogId, "{}");
        public void OnDialogClosed(uint dialogId) { }
    }

    /// <summary>omp's tab on the pane, as the app's PreviewAgentPage gives it to the bridge (scripts, input, world, capture).</summary>
    private sealed class AgentTab(AgentPane pane, NightlyRuntime runtime, Serial serial) : IAgentBrowserPage
    {
        public NightlyAgent Agent { get; } = new(pane, pane.Frames);
        public bool IsClosed => false;
        public DialogBroker? Dialogs => null;
        public INativeInput? Input => Agent;
        public IIsolatedScripts? Isolated => Agent;
        public Task ShowAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<Uri> NavigateAsync(string address, CancellationToken ct)
        {
            // Loading until Nightly reports the new address loaded (as the app's tab does from its navigation start)
            serial.Run(() =>
            {
                _expected = new Uri(address).AbsoluteUri;
                runtime.Send(NightlyProtocol.CommandMessage(pane.Window, NightlyProtocol.CommandOp.Navigate, address)!);
            });
            return Task.FromResult(new Uri(address));
        }

        private string? _expected;

        public Task<AgentPageState> GetStateAsync(CancellationToken ct) => Task.FromResult(serial.Run(() =>
            new AgentPageState(pane.Location is { } l && Uri.TryCreate(l.Url, UriKind.Absolute, out var u) ? u : null,
                pane.Location is not { } at || at.Flags.HasFlag(NightlyProtocol.LocationFlags.Loading) || (_expected is not null && at.Url != _expected),
                false, null)));

        public async Task<string> RunScriptAsync(string script, CancellationToken ct) => await Agent.RunPageScriptAsync(script, ct) ?? "";
        public Task<AgentScreenshot> CaptureAsync(CancellationToken ct) => Agent.CaptureAsync(ct);
        public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class OneTab(IAgentBrowserPage page) : IAgentBrowserHost
    {
        public Task<IAgentBrowserPage> OpenTabAsync(string surfaceId, CancellationToken ct) => Task.FromResult(page);
    }

    /// <summary>
    /// The page omp drives: a field, a button and a hover target that record whether their events were trusted, a red
    /// block for the screenshot, a page global the isolated world must not see, content taller than the window, and a
    /// CSP that forbids eval (the agent's scripts are not the page's).
    /// </summary>
    private const string AgentFixture = """
        <!doctype html><html><head><meta charset="utf-8"><title>K1 tools</title>
        <style>
          body { margin: 0; font: 16px sans-serif; }
          #name { position: absolute; left: 20px; top: 20px; width: 240px; height: 32px; }
          #go { position: absolute; left: 20px; top: 80px; width: 120px; height: 40px; }
          #hover { position: absolute; left: 300px; top: 80px; width: 100px; height: 40px; background: #00f; }
          #red { position: absolute; left: 0; top: 300px; width: 200px; height: 100px; background: #f00; }
          #tall { position: absolute; left: 0; top: 0; width: 1px; height: 4000px; }
        </style></head><body>
        <input id="name" aria-label="Name">
        <button id="go">Go</button>
        <div id="hover" role="note" aria-label="Hover me"></div>
        <div id="red"></div><div id="tall"></div>
        <script nonce="k1">
          window.pageSecret = 42;
          window.clicks = 0; window.clickTrusted = null; window.hovered = null; window.keys = [];
          document.getElementById("go").addEventListener("click", e => { clicks++; clickTrusted = e.isTrusted; });
          document.getElementById("hover").addEventListener("mouseover", e => { hovered = e.isTrusted; });
          document.getElementById("name").addEventListener("keydown", e => keys.push(e.key + (e.isTrusted ? "" : "!")));
        </script></body></html>
        """;

    private static (HttpListener Server, string Url) ServeFixture(string html)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var url = $"http://127.0.0.1:{port}/";
        var server = new HttpListener();
        server.Prefixes.Add(url);
        server.Start();
        var body = Encoding.UTF8.GetBytes(html);
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext context;
                try { context = await server.GetContextAsync(); }
                catch (Exception) { return; }
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Headers["Content-Security-Policy"] = "script-src 'nonce-k1'";
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });
        return (server, url);
    }

    private static async Task<JsonObject> Call(AgentBrowserBridge bridge, string method, JsonObject p)
    {
        var line = new JsonObject { ["id"] = "1", ["method"] = method, ["params"] = p }.ToJsonString();
        var answer = (JsonObject)JsonNode.Parse(await bridge.HandleLineAsync(line, CancellationToken.None))!;
        Assert.True(answer["ok"]?.GetValue<bool>() == true, $"{method}: {answer.ToJsonString()}");
        return answer["result"]!.AsObject();
    }

    [Fact]
    public async Task OmpsBrowserToolsDriveARealNightlyPane()
    {
        var app = Environment.GetEnvironmentVariable("OMBRA_TEST_NIGHTLY_APP");
        var root = Environment.GetEnvironmentVariable("OMBRA_TEST_NIGHTLY_PROFILE_ROOT");
        Assert.SkipUnless(OperatingSystem.IsMacOS() && app is { Length: > 0 } && root is { Length: > 0 },
            "set OMBRA_TEST_NIGHTLY_APP (an embed-capable Nightly.app) and OMBRA_TEST_NIGHTLY_PROFILE_ROOT");
        Assert.Empty(NightlyProcesses(app!));
        var profile = Path.Combine(root!, "agent-test-" + Guid.NewGuid().ToString("N")[..8]);
        var serial = new Serial();
        var options = new NightlyRuntimeOptions(NightlyMach.RealPath(Path.Combine(app!, "Contents", "MacOS", "nightly")), profile, null, null, null, AllowUnsignedPeer: true);
        var runtime = new NightlyRuntime(options, serial.Post);
        var pane = new AgentPane(runtime);
        var (server, url) = ServeFixture(AgentFixture);
        try
        {
            var token = serial.Run(() => runtime.Register(pane));
            await Until(() => serial.Run(() => runtime.State == NightlyState.Running && pane.Window != 0 && pane.FrameCount > 0), "a window with frames", 90);

            var tab = new AgentTab(pane, runtime, serial);
            var bridge = new AgentBrowserBridge(new OneTab(tab));
            var surface = (await Call(bridge, "browser.open_split", new JsonObject { ["url"] = url, ["focus"] = false }))["surface_id"]!.GetValue<string>();
            async Task<JsonObject> Do(string method, JsonObject p)
            {
                p["surface_id"] = surface;
                return await Call(bridge, method, p);
            }
            var readable = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            async Task<string> Eval(string script) => (await Do("browser.eval", new JsonObject { ["script"] = script }))["value"]!.ToJsonString(readable);

            await Do("browser.wait", new JsonObject { ["load_state"] = "complete", ["timeout_ms"] = 30000 });
            Assert.Equal("\"K1 tools\"", await Eval("document.title"));
            // The page's world (its globals, its CSP not applying to the agent), and Ombra's isolated world apart from it
            Assert.Equal("42", await Eval("window.pageSecret"));
            Assert.Equal("\"undefined\"", await tab.Agent.CallAsync("() => typeof window.pageSecret", "[]", CancellationToken.None));
            Assert.Equal("\"K1 tools|42\"", await tab.Agent.CallAsync("(a, b) => document.title + '|' + (a + b)", "[40, 2]", CancellationToken.None));
            var thrown = await Assert.ThrowsAsync<IsolatedScriptException>(() => tab.Agent.CallAsync("() => { throw new TypeError('nope'); }", "[]", CancellationToken.None));
            Assert.StartsWith("TypeError: nope", thrown.Message);

            var snapshot = (await Do("browser.snapshot", new JsonObject { ["interactive"] = true })).ToJsonString();
            Assert.Contains("Go", snapshot);
            Assert.Contains("Name", snapshot);

            var click = await Do("browser.click", new JsonObject { ["selector"] = "#go" });
            Assert.Equal("native", click["input"]?.GetValue<string>());
            Assert.Equal("1", await Eval("clicks"));
            Assert.Equal("true", await Eval("clickTrusted"));

            var hover = await Do("browser.hover", new JsonObject { ["selector"] = "#hover" });
            Assert.Equal("native", hover["input"]?.GetValue<string>());
            Assert.Equal("true", await Eval("hovered"));

            var type = await Do("browser.type", new JsonObject { ["selector"] = "#name", ["text"] = "Привет ok" });
            Assert.Equal("native", type["input"]?.GetValue<string>());
            Assert.Equal("\"Привет ok\"", await Eval("document.getElementById('name').value"));
            await Do("browser.fill", new JsonObject { ["selector"] = "#name", ["text"] = "new" });
            Assert.Equal("\"new\"", await Eval("document.getElementById('name').value"));
            await Do("browser.press", new JsonObject { ["key"] = "Enter" });
            Assert.Contains("Enter", await Eval("keys.join(',')"));
            Assert.DoesNotContain("!", await Eval("keys.join(',')")); // every key event trusted

            await Do("browser.scroll", new JsonObject { ["dy"] = 600 });
            Assert.True(int.Parse(await Eval("Math.round(scrollY)")) > 0, "the page scrolled");
            await Eval("scrollTo(0, 0), 0");
            await Task.Delay(500); // the frame after the scroll

            var shot = await Do("browser.screenshot", new JsonObject());
            var png = Convert.FromBase64String(shot["png_base64"]!.GetValue<string>());
            var rgb = DecodeRgb(png, out var width, out var height);
            var scale = pane.Scale;
            Assert.Equal((int)(800 * scale), width);
            Assert.Equal((int)(600 * scale), height);
            int Pixel(double x, double y, int c) => rgb[((int)(y * scale) * width + (int)(x * scale)) * 3 + c];
            Assert.Equal((255, 0, 0), (Pixel(100, 350, 0), Pixel(100, 350, 1), Pixel(100, 350, 2))); // the red block
            Assert.Equal((0, 0, 255), (Pixel(350, 100, 0), Pixel(350, 100, 1), Pixel(350, 100, 2))); // the hover target
            Assert.Equal(url, shot["url"]!.GetValue<string>());

            serial.Run(() => runtime.Unregister(token));
            await Until(() => NightlyProcesses(app!).Count == 0, "no Nightly process left", 15);
        }
        finally
        {
            server.Stop();
            serial.Run(runtime.Dispose);
            pane.Frames.Dispose();
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
        }
    }
}
