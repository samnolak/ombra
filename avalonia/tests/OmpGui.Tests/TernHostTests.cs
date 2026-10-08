using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using OmpGui.ClientCore.Browser;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// The app as omp's Tern daemon (BROWSER_PLAN B3, <see cref="TernHost"/>): framing, greeting, pipelined requests,
/// per-tab state (events, dialogs, presets) over a scripted page, and conformance with omp's own Tern client from the
/// pinned omp (tools/verify/tern-conformance/conformance.ts, run with bun; skipped when no runtime pack is installed).
/// </summary>
public sealed class TernHostTests
{
    // ── A scripted page: what the host asks of a tab, recorded ──

    internal sealed class FakeBrowser : ITernBrowser
    {
        public List<FakePage> Pages { get; } = [];
        public string? Clipboard { get; private set; }
        /// <summary>Opens for this owner answer late (omp gives up first).</summary>
        public int SlowOwner { get; init; } = 99;

        public async Task<ITernPage> OpenAsync(TernOpenRequest request, TernBlock block, CancellationToken ct)
        {
            if (request.Owner == SlowOwner) await Task.Delay(800, ct);
            var page = new FakePage(block, request);
            lock (Pages) Pages.Add(page);
            return page;
        }

        public Task WriteClipboardAsync(string text, CancellationToken ct)
        {
            Clipboard = text;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakePage(ITernEventSink sink, TernOpenRequest open) : ITernPage
    {
        private readonly List<string> _history = [];
        private int _index = -1;
        private int _acting;

        public TernOpenRequest Open { get; } = open;
        public DialogBroker Broker { get; } = new();
        public List<NativeInputStep> Steps { get; } = [];
        public List<TernCaptureRequest> Captures { get; } = [];
        public List<TernCookie> Cookies { get; } = [];
        public IReadOnlyList<TernScript> Scripts { get; private set; } = [];
        public List<string?> Agents { get; } = [];
        public List<string?> Appearances { get; } = [];
        public List<Uri> Navigations { get; } = [];
        public (int W, int H) Viewport { get; private set; } = (open.Width, open.Height);
        public bool? ConfirmAnswer { get; private set; }
        public bool Closed { get; private set; }
        public int Copies { get; private set; }

        public bool IsClosed => false;
        public DialogBroker? Dialogs => Broker;

        public IDisposable BeginAgentAction()
        {
            Interlocked.Increment(ref _acting);
            return new Scope(() => Interlocked.Decrement(ref _acting));
        }

        public Task NavigateAsync(Uri url, CancellationToken ct)
        {
            Navigations.Add(url);
            if (_index < _history.Count - 1) _history.RemoveRange(_index + 1, _history.Count - _index - 1);
            _history.Add(url.AbsoluteUri);
            _index++;
            Load();
            return Task.CompletedTask;
        }

        public Task HistoryAsync(TernHistory go, CancellationToken ct)
        {
            _index = go switch
            {
                TernHistory.Back => Math.Max(0, _index - 1),
                TernHistory.Forward => Math.Min(_history.Count - 1, _index + 1),
                _ => _index,
            };
            Load();
            return Task.CompletedTask;
        }

        private string Url => _index < 0 ? "about:blank" : _history[_index];
        private string Title => _index < 0 ? "" : "Title of " + new Uri(Url).AbsolutePath;

        /// <summary>What a page does as it loads: its address, the new document, its title, the end of the load.</summary>
        private void Load()
        {
            sink.Post("url", new JsonObject { ["url"] = Url });
            sink.Post("committed", new JsonObject { ["url"] = Url });
            sink.Post("title", new JsonObject { ["title"] = Title });
            sink.Post("loaded");
        }

        public Task<TernPageState> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(new TernPageState(Url, Title, Viewport.W, Viewport.H, false, _index > 0, _index < _history.Count - 1));

        public async Task<string?> EvaluateAsync(string function, string argsJson, TernWorld world, string? frame, CancellationToken ct)
        {
            if (function.Contains("throw new Error('boom')")) throw new TernException(TernException.Js, "Error: boom\n    at <anonymous>:1:20");
            if (function.Contains("return never")) await Task.Delay(Timeout.Infinite, ct);
            if (function.Contains("return undefined")) return null;
            if (function.Contains("requestAnimationFrame")) return "true";
            return new JsonObject
            {
                ["function"] = function,
                ["args"] = JsonNode.Parse(argsJson),
                ["world"] = world == TernWorld.Isolated ? "isolated" : "page",
                ["frame"] = frame,
            }.ToJsonString();
        }

        public Task InputAsync(IReadOnlyList<NativeInputStep> steps, CancellationToken ct)
        {
            lock (Steps) Steps.AddRange(steps);
            // The page's "Delete" button at (13, 13) asks for confirmation when clicked
            if (steps.OfType<NativeMouseStep>().Any(s => s is { Action: NativeMouseAction.Up, X: 13, Y: 13 }))
                _ = Broker.ConfirmAsync(new ConfirmRequest("Delete it?") { CausedByAgent = Volatile.Read(ref _acting) > 0 }, CancellationToken.None)
                    .ContinueWith(t => ConfirmAnswer = t.Result, TaskScheduler.Default);
            return Task.CompletedTask;
        }

        public Task<byte[]> CaptureAsync(TernCaptureRequest request, CancellationToken ct)
        {
            lock (Captures) Captures.Add(request);
            return Task.FromResult(request.Jpeg ? new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 } : OnePixelPng);
        }

        public Task<byte[]> PdfAsync(CancellationToken ct) => Task.FromResult("%PDF-1.4\n%scripted\n"u8.ToArray());

        public Task SetViewportAsync(int width, int height, CancellationToken ct)
        {
            Viewport = (width, height);
            return Task.CompletedTask;
        }

        public Task SetScriptsAsync(IReadOnlyList<TernScript> scripts, CancellationToken ct)
        {
            Scripts = scripts;
            return Task.CompletedTask;
        }

        public Task SetUserAgentAsync(string? userAgent, CancellationToken ct)
        {
            Agents.Add(userAgent);
            return Task.CompletedTask;
        }

        public Task SetAppearanceAsync(string? scheme, CancellationToken ct)
        {
            Appearances.Add(scheme);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TernCookie>> GetCookiesAsync(CancellationToken ct)
        {
            lock (Cookies) return Task.FromResult<IReadOnlyList<TernCookie>>([.. Cookies]);
        }

        public Task SetCookieAsync(TernCookie cookie, CancellationToken ct)
        {
            lock (Cookies) Cookies.Add(cookie);
            return Task.CompletedTask;
        }

        public Task DeleteCookieAsync(string name, string domain, string path, CancellationToken ct)
        {
            lock (Cookies) Cookies.RemoveAll(c => c.Name == name && c.Domain == domain && c.Path == path);
            return Task.CompletedTask;
        }

        public Task CopyAsync(CancellationToken ct)
        {
            Copies++;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken ct)
        {
            Closed = true;
            Broker.Dispose();
            return Task.CompletedTask;
        }

        private sealed class Scope(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    // ── A minimal Tern client: frames on the socket ──

    private sealed class Wire : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly NetworkStream _stream;
        private int _next;

        public Wire(string path)
        {
            _socket.Connect(new UnixDomainSocketEndPoint(path));
            _stream = new NetworkStream(_socket, ownsSocket: false);
        }

        public async Task SendRawAsync(byte[] bytes) => await _stream.WriteAsync(bytes);

        public Task SendAsync(JsonObject frame) => SendRawAsync(Frame(Encoding.UTF8.GetBytes(frame.ToJsonString())));

        public static byte[] Frame(byte[] json)
        {
            var frame = new byte[4 + json.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)json.Length);
            json.CopyTo(frame, 4);
            return frame;
        }

        /// <summary>The next frame, null at the end of the stream (the host hung up).</summary>
        public async Task<JsonObject?> ReadAsync()
        {
            var header = new byte[4];
            if (await _stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false).AsTask().WaitAsync(TimeSpan.FromSeconds(10)) < 4) return null;
            var body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
            await _stream.ReadExactlyAsync(body).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            return (JsonObject)JsonNode.Parse(body)!;
        }

        public async Task<JsonObject> HelloAsync()
        {
            await SendAsync(new JsonObject { ["hello"] = new JsonObject() });
            return (await ReadAsync())!;
        }

        /// <summary>Sends one browser op and returns its id.</summary>
        public async Task<int> RequestAsync(JsonObject op)
        {
            var id = Interlocked.Increment(ref _next);
            await SendAsync(new JsonObject { ["id"] = id, ["browser"] = op });
            return id;
        }

        /// <summary>One op's answer (<c>{ok}</c> or <c>{error}</c>), others read on the way are dropped.</summary>
        public async Task<JsonObject> CallAsync(JsonObject op)
        {
            var id = await RequestAsync(op);
            while (await ReadAsync() is { } frame)
                if (frame["id"]!.GetValue<int>() == id) return (JsonObject)frame["browser"]!;
            throw new EndOfStreamException("the host hung up");
        }

        public void Dispose()
        {
            _stream.Dispose();
            _socket.Dispose();
        }
    }

    private static (TernHost Host, FakeBrowser Browser) Start()
    {
        var host = TernHost.TryStart();
        Assert.NotNull(host);
        var browser = new FakeBrowser();
        host.Browser = browser;
        return (host, browser);
    }

    private static string Kind(JsonObject answer) => answer["error"]?["kind"]?.GetValue<string>() ?? "ok";

    [Fact]
    public async Task Omp_is_greeted_and_every_launch_gets_the_socket_and_a_pane_of_its_own()
    {
        if (OperatingSystem.IsWindows()) return; // named pipe there; the framing is the same code
        var (host, _) = Start();
        using (host)
        {
            // Each omp the app starts: socket, its own pane id, Tern on; the user's own socket and flag stay theirs
            var a = host.AddTo(new OmpLaunchSpec { FileName = "omp" });
            var b = host.AddTo(new OmpLaunchSpec { FileName = "omp", Environment = new Dictionary<string, string?> { [TernHost.TernFlagVariable] = "0" } });
            Assert.Equal(host.Endpoint, a.Environment[TernHost.SocketVariable]);
            Assert.Equal("1", a.Environment[TernHost.TernFlagVariable]);
            Assert.Equal("0", b.Environment[TernHost.TernFlagVariable]);
            Assert.NotEqual(a.Environment[TernHost.PaneVariable], b.Environment[TernHost.PaneVariable]);
            Assert.Matches("^[0-9]+$", a.Environment[TernHost.PaneVariable]!);
            var own = new OmpLaunchSpec { FileName = "omp", Environment = new Dictionary<string, string?> { [TernHost.SocketVariable] = "/tmp/their.sock" } };
            Assert.Same(own, host.AddTo(own));
            // The socket's folder is this user's only
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(host.Endpoint)!));

            using var wire = new Wire(host.Endpoint);
            var welcome = await wire.HelloAsync();
            Assert.Equal("""{"welcome":{"ops":[]}}""", welcome.ToJsonString());
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(host.Endpoint)), "the socket folder is removed when the app quits");
    }

    [Fact]
    public async Task Requests_run_side_by_side_and_each_answer_carries_its_id_and_channel()
    {
        if (OperatingSystem.IsWindows()) return;
        var (host, browser) = Start();
        using var _ = host;
        using var wire = new Wire(host.Endpoint);
        await wire.HelloAsync();
        var open = await wire.CallAsync(new JsonObject { ["op"] = "open", ["owner"] = 7, ["url"] = "about:blank", ["width"] = 1365, ["height"] = 768 });
        var block = open["ok"]!["block"]!.GetValue<int>();
        Assert.Equal(7, browser.Pages.Single().Open.Owner);

        // A script that never ends does not hold up the requests after it (omp's own timer gives up on it)
        var slow = await wire.RequestAsync(new JsonObject { ["op"] = "eval", ["block"] = block, ["function"] = "function () { return never; }" });
        var fast = await wire.RequestAsync(new JsonObject { ["op"] = "state", ["block"] = block });
        var answer = await wire.ReadAsync();
        Assert.Equal(fast, answer!["id"]!.GetValue<int>());
        Assert.Equal("about:blank", answer["browser"]!["ok"]!["url"]!.GetValue<string>());
        Assert.NotEqual(slow, fast);

        // Other channels: fork is refused (omp then forks in place), unknown ones too, on their own key
        await wire.SendAsync(new JsonObject { ["id"] = 900, ["fork"] = new JsonObject { ["block"] = 7 } });
        var fork = await wire.ReadAsync();
        Assert.Equal("unsupported", fork!["fork"]!["error"]!["kind"]!.GetValue<string>());
        await wire.SendAsync(new JsonObject { ["id"] = 901, ["pane"] = new JsonObject() });
        Assert.Equal("unsupported", (await wire.ReadAsync())!["pane"]!["error"]!["kind"]!.GetValue<string>());

        // Errors omp maps: unknown block, unknown op, bad arguments
        Assert.Equal("not_found", Kind(await wire.CallAsync(new JsonObject { ["op"] = "state", ["block"] = 4242 })));
        Assert.Equal("unsupported", Kind(await wire.CallAsync(new JsonObject { ["op"] = "teleport", ["block"] = block })));
        Assert.Equal("invalid", Kind(await wire.CallAsync(new JsonObject { ["op"] = "goto", ["block"] = block, ["url"] = "file:///etc/hosts" })));
        Assert.Equal("invalid", Kind(await wire.CallAsync(new JsonObject { ["op"] = "viewport", ["block"] = block, ["width"] = 0, ["height"] = 10 })));
        Assert.Equal("unsupported", Kind(await wire.CallAsync(new JsonObject
        {
            ["op"] = "input", ["block"] = block,
            ["events"] = new JsonArray(new JsonObject { ["type"] = "mouse", ["action"] = "down", ["x"] = 1, ["y"] = 1, ["button"] = "back" }),
        })));
    }

    [Fact]
    public async Task A_frame_omps_reader_would_refuse_ends_the_connection_and_its_tabs()
    {
        if (OperatingSystem.IsWindows()) return;
        var (host, browser) = Start();
        using var _ = host;

        // No greeting: not omp, hung up at once
        using (var stranger = new Wire(host.Endpoint))
        {
            await stranger.SendAsync(new JsonObject { ["id"] = 1, ["browser"] = new JsonObject { ["op"] = "open" } });
            Assert.Null(await stranger.ReadAsync());
        }

        using var wire = new Wire(host.Endpoint);
        await wire.HelloAsync();
        await wire.CallAsync(new JsonObject { ["op"] = "open", ["owner"] = 1, ["width"] = 800, ["height"] = 600 });
        // Over omp's 256 MiB limit: the connection goes, and the tab omp opened on it closes
        await wire.SendRawAsync([0x01, 0x00, 0x00, 0x20]);
        Assert.Null(await wire.ReadAsync());
        await TestProcesses.Eventually(() => browser.Pages.Single().Closed, c => c, TimeSpan.FromSeconds(5), "tab closed with its connection");

        using var other = new Wire(host.Endpoint);
        await other.HelloAsync();
        await other.SendRawAsync(Wire.Frame("[1,2]"u8.ToArray())); // JSON, not an object
        Assert.Null(await other.ReadAsync());
    }

    [Fact]
    public async Task Events_come_in_order_and_omp_hears_what_it_missed()
    {
        var block = new TernBlock(1, 1);
        block.Attach(new FakePage(block, new TernOpenRequest(1, 800, 600)));
        for (var i = 0; i < TernBlock.EventLimit + 10; i++) block.Post("title", new JsonObject { ["title"] = "t" + i });
        var all = await block.DispatchAsync("events", new JsonObject { ["after"] = 0 }, CancellationToken.None);
        var events = all["events"]!.AsArray();
        Assert.Equal(TernBlock.EventLimit, events.Count);
        Assert.Equal(10, all["dropped"]!.GetValue<long>());
        Assert.Equal(11, events[0]!["seq"]!.GetValue<long>());
        var seqs = events.Select(e => e!["seq"]!.GetValue<long>()).ToList();
        Assert.Equal(seqs.Order(), seqs);
        var last = seqs[^1];
        var none = await block.DispatchAsync("events", new JsonObject { ["after"] = last }, CancellationToken.None);
        Assert.Empty(none["events"]!.AsArray());
        Assert.Null(none["dropped"]);
    }

    [Fact]
    public async Task Dialogs_follow_omps_policy_and_a_held_one_reaches_the_user_if_omp_leaves_it()
    {
        var block = new TernBlock(1, 1) { HandOffAfter = TimeSpan.FromMilliseconds(200) };
        var page = new FakePage(block, new TernOpenRequest(1, 800, 600));
        block.Attach(page);
        var broker = page.Broker;
        async Task<JsonObject> Op(string op, JsonObject? p = null) => await block.DispatchAsync(op, p ?? [], CancellationToken.None);
        List<JsonObject> Dialogs() => [.. block.DispatchAsync("events", new JsonObject { ["after"] = 0 }, CancellationToken.None).Result["events"]!.AsArray()
            .Select(e => e!.AsObject()).Where(e => e["type"]!.GetValue<string>() == "dialog")];

        // An alert omp caused: accepted at once and reported as handled
        await broker.AlertAsync(new AlertRequest("Saved") { CausedByAgent = true }, CancellationToken.None);
        Assert.Equal("accepted", Dialogs()[^1]["handled"]!.GetValue<string>());

        // default: a confirm is held for omp (handled null), scripts answer at once instead of waiting, `dialog` answers it
        var confirm = broker.ConfirmAsync(new ConfirmRequest("Delete it?") { CausedByAgent = true }, CancellationToken.None);
        Assert.Null(Dialogs()[^1]["handled"]);
        var blocked = await Assert.ThrowsAsync<TernException>(() => Op("eval", new JsonObject { ["function"] = "function () { return 1; }" }));
        Assert.Equal(TernException.Failed, blocked.Kind);
        Assert.Contains("confirm dialog (\"Delete it?\")", blocked.Message);
        await Op("dialog", new JsonObject { ["accept"] = true });
        Assert.True(await confirm);
        Assert.Equal(TernException.Failed, (await Assert.ThrowsAsync<TernException>(() => Op("dialog", new JsonObject { ["accept"] = true }))).Kind);

        // A prompt answered with text, then with its default
        var prompt = broker.PromptAsync(new PromptRequest("Name?", "Ada") { CausedByAgent = true }, CancellationToken.None);
        Assert.Equal("Ada", Dialogs()[^1]["default"]!.GetValue<string>());
        await Op("dialog", new JsonObject { ["accept"] = true, ["text"] = "Grace" });
        Assert.Equal("Grace", await prompt);

        // A script that opens a dialog while it runs: its eval answers with the dialog, not after it
        var run = Op("eval", new JsonObject { ["function"] = "function () { return never; }" });
        var during = broker.ConfirmAsync(new ConfirmRequest("Leave?") { CausedByAgent = true }, CancellationToken.None);
        Assert.Contains("Leave?", (await Assert.ThrowsAsync<TernException>(() => run)).Message);
        await Op("dialog", new JsonObject { ["accept"] = false });
        Assert.False(await during);

        // accept / dismiss policies answer without holding
        await Op("dialogs", new JsonObject { ["policy"] = "dismiss" });
        Assert.False(await broker.ConfirmAsync(new ConfirmRequest("Sure?") { CausedByAgent = true }, CancellationToken.None));
        await Op("dialogs", new JsonObject { ["policy"] = "accept" });
        Assert.Equal("Ada", await broker.PromptAsync(new PromptRequest("Name?", "Ada") { CausedByAgent = true }, CancellationToken.None));
        Assert.Equal("accepted", Dialogs()[^1]["handled"]!.GetValue<string>());

        // The user's own dialogs are never answered for them, nor reported as omp's
        await Op("dialogs", new JsonObject { ["policy"] = "default" });
        var users = broker.ConfirmAsync(new ConfirmRequest("Mine?"), CancellationToken.None);
        Assert.Same(broker.Current, broker.Pending.Single());
        Assert.NotEqual("Mine?", Dialogs()[^1]["message"]!.GetValue<string>());
        broker.Current!.Complete(true);
        Assert.True(await users);

        // Held and left by omp: the user's card after HandOffAfter, reported as handled by the user
        var left = broker.ConfirmAsync(new ConfirmRequest("Still there?") { CausedByAgent = true }, CancellationToken.None);
        await TestProcesses.Eventually(() => broker.Current, c => c is not null, TimeSpan.FromSeconds(5), "handed to the user");
        Assert.Equal("user", Dialogs()[^1]["handled"]!.GetValue<string>());
        broker.Current!.Complete(false);
        Assert.False(await left);
    }

    [Fact]
    public async Task Files_credentials_and_hosts_omp_set_are_used_and_the_rest_goes_to_the_user()
    {
        var block = new TernBlock(1, 1);
        var page = new FakePage(block, new TernOpenRequest(1, 800, 600));
        block.Attach(page);
        var broker = page.Broker;
        var file = Path.Combine(TestProcesses.TempDir("tern-files"), "report.csv");

        // The preset answers the next chooser omp caused (once), and omp hears "chooser"
        await block.DispatchAsync("files", new JsonObject { ["paths"] = new JsonArray(file) }, CancellationToken.None);
        Assert.Equal(new[] { file }, await broker.ChooseFilesAsync(new FileChooserRequest(false, []) { CausedByAgent = true }, CancellationToken.None));
        var events = (await block.DispatchAsync("events", new JsonObject { ["after"] = 0 }, CancellationToken.None))["events"]!.AsArray();
        Assert.Contains(events, e => e!["type"]!.GetValue<string>() == "chooser");
        // Without a preset the user picks
        var second = broker.ChooseFilesAsync(new FileChooserRequest(false, []) { CausedByAgent = true }, CancellationToken.None);
        Assert.NotNull(broker.Current);
        broker.Current!.Complete(null);
        Assert.Null(await second);
        // Permissions omp's action asked for: the user's
        var permission = broker.PermissionAsync(new PermissionRequest(PermissionKind.Camera, "https://example.com") { CausedByAgent = true }, CancellationToken.None);
        Assert.Equal(BrowserDialogKind.Permission, broker.Current!.Kind);
        broker.Current.Complete(PermissionAnswer.Deny);
        await permission;

        // Credentials omp set answer a first challenge; a refused one goes to the user
        await block.DispatchAsync("credentials", new JsonObject { ["username"] = "agent", ["password"] = "pw" }, CancellationToken.None);
        Assert.Equal(new CredentialAnswer("agent", "pw"), await broker.RequestCredentialsAsync(new CredentialRequest("example.com", 443, "Studio", false, false), CancellationToken.None));
        var refused = broker.RequestCredentialsAsync(new CredentialRequest("example.com", 443, "Studio", false, true), CancellationToken.None);
        Assert.Equal(BrowserDialogKind.Credentials, broker.Current!.Kind);
        broker.Current.Cancel();
        Assert.Null(await refused);
        await block.DispatchAsync("credentials", [], CancellationToken.None);
        var nobody = broker.RequestCredentialsAsync(new CredentialRequest("example.com", 443, "Studio", false, false), CancellationToken.None);
        Assert.NotNull(broker.Current);
        broker.Current!.Cancel();
        await nobody;

        // Allowed hosts: others are blocked (event, no navigation); sub-domains match *.
        await block.DispatchAsync("allow", new JsonObject { ["hosts"] = new JsonArray("*.example.com", "localhost") }, CancellationToken.None);
        await block.DispatchAsync("goto", new JsonObject { ["url"] = "https://app.example.com/a" }, CancellationToken.None);
        await block.DispatchAsync("goto", new JsonObject { ["url"] = "http://localhost:5173/" }, CancellationToken.None);
        await block.DispatchAsync("goto", new JsonObject { ["url"] = "https://example.org/" }, CancellationToken.None);
        Assert.Equal(new[] { "https://app.example.com/a", "http://localhost:5173/" }, page.Navigations.Select(u => u.AbsoluteUri));
        events = (await block.DispatchAsync("events", new JsonObject { ["after"] = 0 }, CancellationToken.None))["events"]!.AsArray();
        Assert.Equal("https://example.org/", events.Last(e => e!["type"]!.GetValue<string>() == "blocked")!["url"]!.GetValue<string>());

        // Downloads: the folder omp chose (made when missing); relative ones refused
        var dir = Path.Combine(TestProcesses.TempDir("tern-downloads"), "sub");
        await block.DispatchAsync("downloads", new JsonObject { ["dir"] = dir }, CancellationToken.None);
        Assert.Equal(dir, block.DownloadDirectory);
        Assert.True(Directory.Exists(dir));
        Assert.Equal(TernException.Invalid, (await Assert.ThrowsAsync<TernException>(() =>
            block.DispatchAsync("downloads", new JsonObject { ["dir"] = "rel" }, CancellationToken.None))).Kind);
    }

    // ── omp's own client ──

    /// <summary>bun, omp's bunfig and its pi-coding-agent/src from an installed OMP GUI runtime pack (the pinned omp).</summary>
    internal static (string Bun, string Config, string Src)? FindOmpClient()
    {
        var exe = OperatingSystem.IsWindows() ? "bun.exe" : "bun";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var packs = new List<string>();
        if (Environment.GetEnvironmentVariable("OMPGUI_TERN_OMP_PACK") is { Length: > 0 } pinned) packs.Add(pinned);
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.Combine(home, "Library", "Application Support"), Path.Combine(home, ".local", "share") })
        {
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            foreach (var app in Directory.EnumerateDirectories(root, "OmpGui*"))
                if (Directory.Exists(Path.Combine(app, "runtimes")))
                    packs.AddRange(Directory.EnumerateDirectories(Path.Combine(app, "runtimes")).OrderByDescending(p => Path.GetFileName(p) == OmpGui.App.Services.Runtime.RuntimePack.Name));
        }
        foreach (var pack in packs)
        {
            var src = Path.Combine(pack, "omp", "node_modules", "@oh-my-pi", "pi-coding-agent", "src");
            if (File.Exists(Path.Combine(pack, "bun", exe)) && File.Exists(Path.Combine(src, "tools", "browser", "tern", "wire.ts")))
                return (Path.Combine(pack, "bun", exe), Path.Combine(pack, "omp", "bunfig.toml"), src);
        }
        return null;
    }

    /// <summary>The repository's tools/verify/tern-conformance/conformance.ts (found from this file's own path: the build output lives elsewhere).</summary>
    internal static string ConformanceScript([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var script = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "tools", "verify", "tern-conformance", "conformance.ts"));
        return File.Exists(script) ? script : throw new FileNotFoundException("The Tern conformance script is missing.", script);
    }

    [Fact]
    public async Task Omps_own_Tern_client_drives_every_op_over_the_wire()
    {
        if (OperatingSystem.IsWindows() || FindOmpClient() is not { } omp)
        {
            Assert.Skip("no OMP GUI runtime pack with omp's Tern client (or Windows: the script speaks to a Unix socket)");
            return;
        }
        var (host, browser) = Start();
        using var _ = host;
        var spec = host.AddTo(new OmpLaunchSpec { FileName = omp.Bun });
        var start = new ProcessStartInfo(omp.Bun) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "--config=" + omp.Config, "--no-env-file", "--no-install", ConformanceScript(), "wire", "http://127.0.0.1:9" }) start.ArgumentList.Add(a);
        start.Environment["OMP_SRC"] = omp.Src;
        start.Environment[TernHost.SocketVariable] = spec.Environment[TernHost.SocketVariable];
        start.Environment[TernHost.PaneVariable] = spec.Environment[TernHost.PaneVariable];
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        var lines = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        var failed = lines.Where(l => l["ok"]?.GetValue<bool>() == false).Select(l => l.ToJsonString()).ToList();
        Assert.True(failed.Count == 0 && p.ExitCode == 0, string.Join("\n", failed) + "\n" + await errors);
        Assert.Equal(true, lines[^1]["done"]?.GetValue<bool>());
        Assert.True(lines[^1]["passed"]!.GetValue<int>() >= 18, lines[^1].ToJsonString());

        var main = browser.Pages.First(p => p.Open.Owner != browser.SlowOwner);
        Assert.Equal((1000, 700), (main.Open.Width, main.Open.Height));
        // omp's document-start scripts: its capture first, the user's init script, its kit last in the isolated world
        Assert.Contains(main.Scripts, s => s.Source == "window.__conformance = 1;" && s.World == TernWorld.Page && s.AllFrames);
        Assert.Equal(TernWorld.Isolated, main.Scripts[^1].World);
        // The click opened a confirm omp held and accepted
        Assert.True(main.ConfirmAnswer);
        // Typing: printable keys as key presses, the rest as text; a wheel step
        Assert.Contains(main.Steps, s => s is NativeKeyStep { Down: true, Key: "h" });
        Assert.Contains(main.Steps, s => s is NativeTextStep { Text: "é" });
        Assert.Contains(main.Steps, s => s is NativeWheelStep { DeltaY: 120 });
        Assert.Contains(main.Steps, s => s is NativeMouseStep { Action: NativeMouseAction.Down, X: 13, Y: 13, Clicks: 1 });
        Assert.Contains(main.Captures, c => c is { Jpeg: true, Quality: 50, Rect: null, FullPage: false, Scale: 1 });
        Assert.Contains(main.Captures, c => c is { Jpeg: false, FullPage: true });
        Assert.Equal(new string?[] { "ConformanceAgent/1.0", null }, main.Agents);
        Assert.Equal(new string?[] { "dark", null }, main.Appearances);
        Assert.Equal("from omp", browser.Clipboard);
        Assert.True(main.Closed);
        // The open omp gave up on was answered late, and omp closed that tab
        Assert.True(browser.Pages.Single(p => p.Open.Owner == browser.SlowOwner).Closed);
    }
}
