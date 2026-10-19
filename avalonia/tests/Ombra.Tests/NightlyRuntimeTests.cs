using System.Buffers.Binary;
using System.Diagnostics;
using Ombra.App.Localization;
using Ombra.App.Nightly;

namespace Ombra.Tests;

/// <summary>
/// The embedded Nightly runtime (K1 option A): the wire layouts against ZenEmbedHost.mm's structs, the process
/// supervisor's start / crash restart / give up / stop (with stand-in processes), and, where a Nightly build with the
/// embed patches is named by <c>OMBRA_TEST_NIGHTLY_APP</c> (and a profile root by <c>OMBRA_TEST_NIGHTLY_PROFILE_ROOT</c>),
/// the real runtime: handshake, a window per pane, frames, a page's address and title, a crash and its restart, and no
/// process left after the last pane closes.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")] // runtime tests skip elsewhere; the analyzer does not read Assert.SkipUnless
public sealed partial class NightlyRuntimeTests
{
    // ── protocol ──

    [Fact]
    public void MessagesHaveTheLayoutOfTheNativeStructs()
    {
        var mouse = NightlyProtocol.MouseMessage(7, NightlyProtocol.MouseKind.Down, 1, 2, 10.5, 20.25, 1UL << 20);
        Assert.Equal(64, mouse.Length);
        Assert.Equal(64u, BinaryPrimitives.ReadUInt32LittleEndian(mouse.AsSpan(4)));
        Assert.Equal(NightlyProtocol.Mouse, BinaryPrimitives.ReadInt32LittleEndian(mouse.AsSpan(20)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(mouse.AsSpan(24)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(mouse.AsSpan(28)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(mouse.AsSpan(32)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(mouse.AsSpan(36)));
        Assert.Equal(10.5, BinaryPrimitives.ReadDoubleLittleEndian(mouse.AsSpan(40)));
        Assert.Equal(20.25, BinaryPrimitives.ReadDoubleLittleEndian(mouse.AsSpan(48)));
        Assert.Equal(1UL << 20, BinaryPrimitives.ReadUInt64LittleEndian(mouse.AsSpan(56)));

        var key = NightlyProtocol.KeyMessage(3, true, 0x00, "a", "A", 1UL << 17, isRepeat: true);
        Assert.Equal(120, key.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(key.AsSpan(36)));
        Assert.Equal('a', (char)BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(48)));
        Assert.Equal('A', (char)BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(80)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(key.AsSpan(112)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(key.AsSpan(116)));

        var wheel = NightlyProtocol.WheelMessage(3, 1, 2, new WheelInput(NightlyProtocol.Phase.Changed, NightlyProtocol.Phase.None, true, -4, 8, -0.4, 0.8, 0));
        Assert.Equal(96, wheel.Length);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(wheel.AsSpan(28)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(wheel.AsSpan(36)));
        Assert.Equal(8, BinaryPrimitives.ReadDoubleLittleEndian(wheel.AsSpan(64)));
        Assert.Equal(0.8, BinaryPrimitives.ReadDoubleLittleEndian(wheel.AsSpan(80)));

        var resize = NightlyProtocol.ResizeMessage(3, 800, 600, (100, 200));
        Assert.Equal(64, resize.Length);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(resize.AsSpan(28)));
        Assert.Equal(200, BinaryPrimitives.ReadDoubleLittleEndian(resize.AsSpan(56)));

        Assert.Equal(160, NightlyProtocol.TextMessage(3, "ё").Length);
        var command = NightlyProtocol.CommandMessage(3, NightlyProtocol.CommandOp.Navigate, "https://example.com/")!;
        Assert.Equal(0, command.Length % 4);
        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(32)));
        Assert.Equal("https://example.com/", System.Text.Encoding.UTF8.GetString(command, 40, 20));
        Assert.Null(NightlyProtocol.CommandMessage(3, NightlyProtocol.CommandOp.Navigate, new string('a', NightlyProtocol.MaxText + 1)));
    }

    /// <summary>Protocol 4's device command: op 7 of Command with the device as JSON (zen-embed.js reads these keys), empty to end it.</summary>
    [Fact]
    public void DeviceMessageCarriesTheDeviceAsJsonInACommand()
    {
        var device = new NightlyProtocol.Device(Mobile: true, PixelRatio: 2.625, UserAgent: "UA \"x\"", Touch: true, Landscape: false);
        var m = NightlyProtocol.DeviceMessage(5, device)!;
        Assert.Equal(NightlyProtocol.Command, BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(20)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(24)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(28)));
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(32));
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(m, 40, length))!;
        Assert.True(json["mobile"]!.GetValue<bool>());
        Assert.Equal(2.625, json["dpr"]!.GetValue<double>());
        Assert.Equal("UA \"x\"", json["userAgent"]!.GetValue<string>());
        Assert.True(json["touch"]!.GetValue<bool>());
        Assert.False(json["landscape"]!.GetValue<bool>());
        Assert.Equal(4u, NightlyProtocol.Version);

        var off = NightlyProtocol.DeviceMessage(5, null)!;
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(off.AsSpan(28)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(off.AsSpan(32)));
        Assert.Contains("\"dpr\":0,", NightlyProtocol.DeviceJson(device with { PixelRatio = -1 })); // the display's
    }

    [Fact]
    public void ThemeMessageIsACommandForNoWindow()
    {
        foreach (var (dark, text) in new[] { (true, "dark"), (false, "light") })
        {
            var m = NightlyProtocol.ThemeMessage(dark);
            Assert.Equal(NightlyProtocol.Command, BinaryPrimitives.ReadInt32LittleEndian(m.AsSpan(20)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(24)));
            Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(28)));
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(32));
            Assert.Equal(text, System.Text.Encoding.UTF8.GetString(m, 40, length));
        }
    }

    /// <summary>user.js: no "Update Complete!" toast on the runtime's first start, and the appearance's current state.</summary>
    [Fact]
    public void UserPrefsCarryTheToastAndTheTheme()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var profile = Directory.CreateTempSubdirectory("ombra-userjs-").FullName;
        try
        {
            using var runtime = new NightlyRuntime(new NightlyRuntimeOptions("/nonexistent/nightly", profile, null, null, null, false), a => a());
            runtime.WriteUserPrefs();
            var prefs = File.ReadAllText(Path.Combine(profile, "user.js"));
            Assert.Contains("user_pref(\"zen.updates.show-update-notification\", false);", prefs);
            Assert.Contains("user_pref(\"ui.systemUsesDarkTheme\", 0);", prefs);

            runtime.DarkTheme = true;
            runtime.WriteUserPrefs();
            prefs = File.ReadAllText(Path.Combine(profile, "user.js"));
            Assert.Contains("user_pref(\"ui.systemUsesDarkTheme\", 1);", prefs);
        }
        finally
        {
            Directory.Delete(profile, true);
        }
    }

    [Fact]
    public void ReadsNightlysMessages()
    {
        var location = new byte[40 + 12];
        BinaryPrimitives.WriteInt32LittleEndian(location.AsSpan(20), NightlyProtocol.Location);
        BinaryPrimitives.WriteUInt32LittleEndian(location.AsSpan(24), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(location.AsSpan(28), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(location.AsSpan(32), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(location.AsSpan(36), 4);
        "about:ab".Select((c, i) => location[40 + i] = (byte)c).ToList();
        "Tést"u8.ToArray().AsSpan(0, 4).CopyTo(location.AsSpan(48));
        var info = NightlyProtocol.ReadLocation(location);
        Assert.NotNull(info);
        Assert.Equal(5u, info.Value.WindowId);
        Assert.Equal(NightlyProtocol.LocationFlags.Loading | NightlyProtocol.LocationFlags.CanGoBack, info.Value.Flags);
        Assert.Equal("about:ab", info.Value.Url);
        // A length past the message is refused, not read.
        BinaryPrimitives.WriteUInt32LittleEndian(location.AsSpan(36), 400);
        Assert.Null(NightlyProtocol.ReadLocation(location));

        var window = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(window.AsSpan(24), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(window.AsSpan(28), 1);
        BinaryPrimitives.WriteDoubleLittleEndian(window.AsSpan(32), 1200);
        BinaryPrimitives.WriteDoubleLittleEndian(window.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(window.AsSpan(56), 9);
        Assert.Equal(new NightlyProtocol.WindowInfo(2, true, 1200, 0, 2, 9), NightlyProtocol.ReadWindow(window));
        BinaryPrimitives.WriteUInt32LittleEndian(window.AsSpan(60), 4);
        Assert.Equal(4u, NightlyProtocol.ReadWindow(window).Opener);
    }

    // ── supervisor (stand-in processes) ──

    private sealed class Serial
    {
        private readonly object _gate = new();
        public void Post(Action a) => ThreadPool.QueueUserWorkItem(_ => { lock (_gate) a(); });
        public T Run<T>(Func<T> f) { lock (_gate) return f(); }
        public void Run(Action a) { lock (_gate) a(); }
    }

    /// <summary>A shell that starts a child (as Nightly starts its content and GPU processes) and waits.</summary>
    private static Process StandIn() =>
        Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "sleep 600 & wait"]) { UseShellExecute = false })!;

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    private static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task StopEndsTheProcessAndItsChildren()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "libproc child lookup is macOS");
        var serial = new Serial();
        Process? started = null;
        using var supervisor = new NightlySupervisor(() => started = StandIn(), serial.Post, TimeSpan.FromSeconds(30));
        serial.Run(supervisor.Start);
        Assert.Equal(NightlyState.Starting, serial.Run(() => supervisor.State));
        await Until(() => NightlyMach.Children(started!.Id).Count == 1, "child started");
        var child = NightlyMach.Children(started!.Id)[0];
        serial.Run(supervisor.MarkReady);
        Assert.Equal(NightlyState.Running, serial.Run(() => supervisor.State));

        serial.Run(supervisor.Stop);
        await Until(() => !Alive(started.Id) && !Alive(child), "process tree ended");
        await Task.Delay(300);
        Assert.Equal(NightlyState.Stopped, serial.Run(() => supervisor.State));
        Assert.Equal(1, supervisor.Starts); // no restart after a stop on purpose
    }

    [Fact]
    public async Task ACrashRestartsItAndTheStateSaysSo()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "libproc child lookup is macOS");
        var serial = new Serial();
        var states = new List<NightlyState>();
        using var supervisor = new NightlySupervisor(StandIn, serial.Post, TimeSpan.FromSeconds(30), _ => TimeSpan.FromMilliseconds(100));
        supervisor.StateChanged += () => states.Add(supervisor.State);
        serial.Run(supervisor.Start);
        serial.Run(supervisor.MarkReady);
        var first = serial.Run(() => supervisor.Pid);
        // The supervisor records the child while the parent runs; a crashed parent's child is found only that way.
        await Until(() => serial.Run(() => supervisor.KnownDescendants.Count == 1), "child recorded");
        var child = serial.Run(() => supervisor.KnownDescendants.ToList());

        Process.GetProcessById(first).Kill(); // a crash
        await Until(() => serial.Run(() => supervisor.Starts == 2 && supervisor.State == NightlyState.Starting), "restarted");
        Assert.Contains(NightlyState.Restarting, states);
        Assert.Contains(Loc.F("Nightly exited unexpectedly (code {0}).", "\0").Split('\0')[0], serial.Run(() => supervisor.Detail ?? ""));
        Assert.NotEqual(first, serial.Run(() => supervisor.Pid));
        foreach (var c in child) await Until(() => !Alive(c), "the crashed process's child ended");
        serial.Run(supervisor.MarkReady);
        Assert.Equal(NightlyState.Running, serial.Run(() => supervisor.State));
        Assert.Null(serial.Run(() => supervisor.Detail));
    }

    [Fact]
    public async Task GivesUpAfterRepeatedFailuresUntilRetried()
    {
        var serial = new Serial();
        using var supervisor = new NightlySupervisor(StandIn, serial.Post, TimeSpan.FromMilliseconds(200), _ => TimeSpan.FromMilliseconds(20));
        serial.Run(supervisor.Start);
        // Never ready: each start times out and is replaced, until the supervisor gives up.
        await Until(() => serial.Run(() => supervisor.State == NightlyState.Failed), "gave up", 30);
        Assert.Equal(NightlySupervisor.MaxAttempts, supervisor.Starts);
        Assert.Contains(Loc.F("Nightly did not connect within {0} seconds.", "\0").Split('\0')[0], supervisor.Detail);
        Assert.Equal(0, supervisor.Pid);

        serial.Run(supervisor.Retry);
        Assert.Equal(NightlyState.Starting, serial.Run(() => supervisor.State));
        Assert.Equal(NightlySupervisor.MaxAttempts + 1, supervisor.Starts);
        serial.Run(supervisor.Stop);
    }

    [Fact]
    public void AStartThatCannotWorkFailsAtOnce()
    {
        var serial = new Serial();
        using var supervisor = new NightlySupervisor(() => throw new FileNotFoundException("no Nightly here"), serial.Post, TimeSpan.FromSeconds(5));
        serial.Run(supervisor.Start);
        Assert.Equal(NightlyState.Failed, supervisor.State);
        Assert.Equal("no Nightly here", supervisor.Detail);
        Assert.Equal(0, supervisor.Starts);
    }

    // ── the real runtime ──

    private sealed class RecordingView(NightlyRuntime? runtime = null) : INightlyView
    {
        public NightlyRuntime? Runtime { get; set; } = runtime;
        public List<NightlyState> States { get; } = [];
        public uint WindowId;
        public int Attached, Surfaces, Frames;
        public NightlyProtocol.LocationInfo? Location;
        public NightlyProtocol.WindowInfo? Window;
        public void OnRuntimeState(NightlyState state, string? detail) => States.Add(state);
        public void OnAttached(uint windowId) { WindowId = windowId; Attached++; }
        public void OnDetached() => WindowId = 0;
        public void OnWindow(NightlyProtocol.WindowInfo window) => Window = window;
        public void OnSurface(NightlyProtocol.SurfaceInfo surface, nint ioSurface) { Surfaces++; NightlyMach.CFRelease(ioSurface); }
        public void OnFrame(NightlyProtocol.FrameInfo frame) { Frames++; Runtime!.ReleaseFrame(frame.WindowId, frame.Seq); }
        public void OnLocation(NightlyProtocol.LocationInfo location) => Location = location;
        public void OnPopup(uint popupId, NightlyPopup popup) { }
        public void OnPopupClosed(uint popupId) { }
        public void OnCursor(int cursor) { }
        public void OnTextRect(NightlyProtocol.TextRectInfo rect) { }
        public void OnPopupWindow(uint windowId) => Runtime!.CloseWindow(windowId);
        public void OnWindowClosed() { }
        public List<(uint Id, string Json)> Dialogs { get; } = [];
        public List<uint> ClosedDialogs { get; } = [];
        public void OnDialog(uint dialogId, string json) => Dialogs.Add((dialogId, json));
        public void OnDialogClosed(uint dialogId) => ClosedDialogs.Add(dialogId);
    }

    /// <summary>Processes running the Nightly build's executables (the app and its helper apps).</summary>
    private static List<string> NightlyProcesses(string app)
    {
        var ps = Process.Start(new ProcessStartInfo("/bin/ps", ["-axo", "pid=,comm="]) { RedirectStandardOutput = true })!;
        var lines = ps.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        ps.WaitForExit();
        var real = NightlyMach.RealPath(app);
        return [.. lines.Where(l => l.Contains(real, StringComparison.Ordinal) || l.Contains(app, StringComparison.Ordinal))];
    }

    [Fact]
    public async Task TheRealRuntimeShowsAPaneSurvivesACrashAndLeavesNothingBehind()
    {
        var app = Environment.GetEnvironmentVariable("OMBRA_TEST_NIGHTLY_APP");
        var root = Environment.GetEnvironmentVariable("OMBRA_TEST_NIGHTLY_PROFILE_ROOT");
        Assert.SkipUnless(OperatingSystem.IsMacOS() && app is { Length: > 0 } && root is { Length: > 0 },
            "set OMBRA_TEST_NIGHTLY_APP (an embed-capable Nightly.app) and OMBRA_TEST_NIGHTLY_PROFILE_ROOT");
        Assert.Empty(NightlyProcesses(app!)); // the check below would be meaningless otherwise
        var profile = Path.Combine(root!, "runtime-test-" + Guid.NewGuid().ToString("N")[..8]);
        var serial = new Serial();
        var options = new NightlyRuntimeOptions(NightlyMach.RealPath(Path.Combine(app!, "Contents", "MacOS", "nightly")), profile, null, null, null, AllowUnsignedPeer: true);
        var runtime = new NightlyRuntime(options, serial.Post);
        var view = new RecordingView(runtime);
        try
        {
            var token = serial.Run(() => runtime.Register(view));
            await Until(() => serial.Run(() => runtime.State == NightlyState.Running), "handshake", 90);
            await Until(() => serial.Run(() => view.WindowId != 0 && view.Frames > 0), "window and frames", 60);
            Assert.Equal(token, view.Window!.Value.Pane);
            Assert.True(view.Surfaces > 0);

            // Navigation through the window's chrome script; the page's address and title come back.
            serial.Run(() => runtime.Send(NightlyProtocol.CommandMessage(view.WindowId, NightlyProtocol.CommandOp.Navigate,
                "data:text/html,<title>K1 pane</title><p>hello</p>")!));
            await Until(() => serial.Run(() => view.Location is { Title: "K1 pane" } l && !l.Flags.HasFlag(NightlyProtocol.LocationFlags.Loading)), "location", 30);
            Assert.StartsWith("data:text/html", view.Location!.Value.Url);

            // A crash: the runtime starts again by itself and the pane gets a new window.
            var first = serial.Run(() => runtime.Pid);
            Process.GetProcessById(first).Kill();
            await Until(() => serial.Run(() => view.States.Contains(NightlyState.Restarting)), "restarting", 30);
            var framesBefore = serial.Run(() => view.Frames);
            await Until(() => serial.Run(() => runtime.State == NightlyState.Running && runtime.Pid != first && view.WindowId != 0 && view.Frames > framesBefore),
                "running again", 90);
            Assert.Equal(2, view.Attached);

            serial.Run(() => runtime.Unregister(token));
            Assert.Equal(NightlyState.Stopped, serial.Run(() => runtime.State));
            await Until(() => NightlyProcesses(app!).Count == 0, "no Nightly process left", 15);
        }
        finally
        {
            serial.Run(runtime.Dispose);
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
        }
    }
}
