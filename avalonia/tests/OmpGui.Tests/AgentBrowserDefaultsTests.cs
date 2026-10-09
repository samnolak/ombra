using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using OmpGui.App;
using OmpGui.App.ViewModels;
using OmpGui.App.Views;
using OmpGui.ClientCore;
using OmpGui.ClientCore.Browser;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>
/// The built-in browser as the agent's default (BROWSER_PLAN A2): every omp the app starts gets the bridge's socket,
/// omp's force flags, the app's settings overlay in PI_CONFIG_FILES and the prompt-note extension; the setting off
/// adds none of them. The fake omp records how it was started (FAKE_OMP_LAUNCH_LOG).
/// </summary>
public sealed class AgentBrowserDefaultsTests
{
    private static OmpRuntimeOptions FakeOptions(string log, bool? enabled = null, Dictionary<string, string?>? env = null)
    {
        var fake = TestProcesses.Fake("normal");
        var environment = env ?? [];
        environment["FAKE_OMP_LAUNCH_LOG"] = log;
        environment["FAKE_SESSION_DIR"] = Path.GetDirectoryName(log);
        return new OmpRuntimeOptions { Command = fake.FileName, PrefixArgs = [.. fake.Arguments], Environment = environment, AgentUsesGuiBrowser = enabled };
    }

    private static List<JsonObject> Starts(string log) =>
        File.Exists(log) ? File.ReadAllLines(log).Select(l => (JsonObject)JsonNode.Parse(l)!).ToList() : [];

    private static string? Env(JsonObject start, string key) => start["env"]![key]?.GetValue<string>();

    private static List<string> Args(JsonObject start) => start["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();

    [Fact]
    public async Task Every_chat_omp_is_sent_to_the_built_in_browser_and_keeps_the_users_overlays()
    {
        using var bridge = AgentBrowserBridge.TryStart();
        Assert.NotNull(bridge);
        var dir = TestProcesses.TempDir("browser-defaults");
        var log = Path.Combine(dir, "launches.jsonl");
        var userOverlay = Path.Combine(dir, "my-overlay.yml");
        File.WriteAllText(userOverlay, "browser:\n  headless: false\n");
        var defaults = new AgentBrowserDefaults(bridge, Path.Combine(dir, "agent-browser"), inherited: _ => null);
        var options = FakeOptions(log, env: new() { [AgentBrowserDefaults.ConfigFilesVariable] = userOverlay });

        await using var first = new SessionController(r => defaults.ForSession(options, r), new LaunchRequest(dir, ApprovalMode: "write"));
        await first.StartAsync();
        await using var second = first.Sibling(new LaunchRequest(dir, ApprovalMode: "write")); // another chat
        await second.StartAsync();
        await first.SetApprovalModeAsync("always-ask"); // a restart of the same chat

        var starts = Starts(log);
        Assert.Equal(3, starts.Count);
        foreach (var s in starts)
        {
            Assert.Equal(bridge.Endpoint, Env(s, AgentBrowserBridge.SocketVariable));
            Assert.Equal(bridge.Password, Env(s, AgentBrowserBridge.PasswordVariable));
            Assert.Equal("1", Env(s, AgentBrowserBridge.CmuxFlagVariable));
            Assert.Equal("0", Env(s, AgentBrowserDefaults.RelayFlagVariable));
            foreach (var k in new[] { "CMUX_WORKSPACE_ID", "CMUX_SURFACE_ID", "CMUX_RELAY_ID", "CMUX_RELAY_TOKEN" })
                Assert.Null(Env(s, k)); // a real cmux terminal's ids never reach omp
            Assert.Equal(userOverlay + Path.PathSeparator + defaults.OverlayPath, Env(s, AgentBrowserDefaults.ConfigFilesVariable));
            var args = Args(s);
            var at = args.IndexOf(AgentBrowserDefaults.ExtensionFlag);
            Assert.True(at >= 0 && args[at + 1] == defaults.ExtensionPath, string.Join(" ", args));
            Assert.DoesNotContain("--append-system-prompt", args);
        }
        Assert.Equal("browser:\n  headless: false\n", File.ReadAllText(userOverlay)); // the user's own file untouched
        Assert.True(File.Exists(defaults.OverlayPath));
        Assert.True(File.Exists(defaults.ExtensionPath));
    }

    [Fact]
    public void The_terminals_omp_gets_the_same_and_keeps_an_inherited_overlay()
    {
        using var bridge = AgentBrowserBridge.TryStart();
        Assert.NotNull(bridge);
        var dir = TestProcesses.TempDir("browser-defaults-tui");
        var defaults = new AgentBrowserDefaults(bridge, Path.Combine(dir, "agent-browser"),
            inherited: k => k == AgentBrowserDefaults.ConfigFilesVariable ? "/home/me/team.yml" : null);
        var options = new OmpRuntimeOptions { Command = "omp" };
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs()) { OmpTuiLaunch = (d, _) => Task.FromResult(defaults.ForTui(options, d)) };
        vm.OpenOmpTuiCommand.Execute(null);
        var tui = Assert.Single(vm.Terminals);
        Assert.Equal(bridge.Endpoint, tui.Environment[AgentBrowserBridge.SocketVariable]);
        Assert.Equal("1", tui.Environment[AgentBrowserBridge.CmuxFlagVariable]);
        Assert.Equal("0", tui.Environment[AgentBrowserDefaults.RelayFlagVariable]);
        // The terminal can only add variables: the inherited value has to be in ours
        Assert.Equal("/home/me/team.yml" + Path.PathSeparator + defaults.OverlayPath, tui.Environment[AgentBrowserDefaults.ConfigFilesVariable]);
        Assert.Equal("", tui.Environment["CMUX_SURFACE_ID"]); // a cmux terminal's surface reads as unset
        Assert.Equal([AgentBrowserDefaults.ExtensionFlag, defaults.ExtensionPath], tui.Args.TakeLast(2));
    }

    [Fact]
    public async Task With_the_setting_off_omp_starts_as_its_own_configuration_says()
    {
        using var bridge = AgentBrowserBridge.TryStart();
        Assert.NotNull(bridge);
        var dir = TestProcesses.TempDir("browser-defaults-off");
        var log = Path.Combine(dir, "launches.jsonl");
        var defaults = new AgentBrowserDefaults(bridge, Path.Combine(dir, "agent-browser"), inherited: _ => null);
        var options = FakeOptions(log, enabled: false);

        await using (var s = new SessionController(r => defaults.ForSession(options, r), new LaunchRequest(dir, ApprovalMode: "write")))
            await s.StartAsync();
        var start = Assert.Single(Starts(log));
        // Nothing added: omp sees only what it inherits from the app (here the test run, itself maybe started by the app)
        foreach (var k in new[] { AgentBrowserBridge.SocketVariable, AgentBrowserBridge.PasswordVariable, AgentBrowserBridge.CmuxFlagVariable,
                     AgentBrowserDefaults.RelayFlagVariable, AgentBrowserDefaults.ConfigFilesVariable })
        {
            Assert.Equal(Environment.GetEnvironmentVariable(k), Env(start, k));
            Assert.NotEqual(bridge.Endpoint, Env(start, k));
        }
        Assert.DoesNotContain(AgentBrowserDefaults.ExtensionFlag, Args(start));

        var tui = defaults.ForTui(options, dir);
        var plain = options.ToTuiLaunchSpec(dir);
        Assert.Equal(plain.Arguments, tui.Arguments);
        Assert.Equal(plain.Environment, tui.Environment);
        Assert.False(Directory.Exists(defaults.Directory)); // nothing written either
    }

    [Fact]
    public void The_users_own_choices_win_and_unwritable_files_still_start_omp()
    {
        using var bridge = AgentBrowserBridge.TryStart();
        Assert.NotNull(bridge);
        var dir = TestProcesses.TempDir("browser-defaults-own");
        var defaults = new AgentBrowserDefaults(bridge, Path.Combine(dir, "agent-browser"), inherited: _ => null);

        // Their own cmux socket in the settings' environment: left exactly as it is
        var own = new OmpRuntimeOptions { Command = "omp", Environment = new() { [AgentBrowserBridge.SocketVariable] = "/tmp/mine.sock" } };
        Assert.Equal(own.ToLaunchSpec().Arguments, defaults.ForSession(own, new LaunchRequest()).Arguments);
        Assert.False(defaults.ForSession(own, new LaunchRequest()).Environment.ContainsKey(AgentBrowserDefaults.ConfigFilesVariable));

        // A flag they set themselves stays; a removed PI_CONFIG_FILES (null) means only ours
        var flags = new OmpRuntimeOptions { Command = "omp", Environment = new() { [AgentBrowserDefaults.RelayFlagVariable] = "1", [AgentBrowserDefaults.ConfigFilesVariable] = null } };
        var spec = defaults.ForSession(flags, new LaunchRequest());
        Assert.Equal("1", spec.Environment[AgentBrowserDefaults.RelayFlagVariable]);
        Assert.Equal(defaults.OverlayPath, spec.Environment[AgentBrowserDefaults.ConfigFilesVariable]);
        // Started from a process that already had our overlay (an omp started inside the app's own terminal): once
        var inner = new AgentBrowserDefaults(bridge, defaults.Directory, inherited: _ => defaults.OverlayPath).ForSession(new OmpRuntimeOptions { Command = "omp" }, new LaunchRequest());
        Assert.Equal(defaults.OverlayPath, inner.Environment[AgentBrowserDefaults.ConfigFilesVariable]);

        // The folder cannot be made (a file is in the way): socket and flags still, no overlay omp would fail to find
        var blocked = Path.Combine(dir, "blocked");
        File.WriteAllText(blocked, "");
        var noFiles = new AgentBrowserDefaults(bridge, blocked, inherited: _ => null).ForSession(new OmpRuntimeOptions { Command = "omp" }, new LaunchRequest());
        Assert.Equal(bridge.Endpoint, noFiles.Environment[AgentBrowserBridge.SocketVariable]);
        Assert.False(noFiles.Environment.ContainsKey(AgentBrowserDefaults.ConfigFilesVariable));
        Assert.DoesNotContain(AgentBrowserDefaults.ExtensionFlag, noFiles.Arguments);
    }

    [Fact]
    public void The_overlay_and_the_note_say_what_the_plan_asks_and_nothing_else()
    {
        using var bridge = AgentBrowserBridge.TryStart();
        Assert.NotNull(bridge);
        var dir = TestProcesses.TempDir("browser-defaults-files");
        var defaults = new AgentBrowserDefaults(bridge, Path.Combine(dir, "agent-browser"), inherited: _ => null);
        defaults.ForSession(new OmpRuntimeOptions { Command = "omp" }, new LaunchRequest());

        var yaml = File.ReadAllLines(defaults.OverlayPath).Where(l => !l.StartsWith('#')).ToArray();
        Assert.Equal(["browser:", "  enabled: true", "  cdpUrl: \"\"", "  cmux: true", "  idleCloseSec: 0"], yaml);
        var js = File.ReadAllText(defaults.ExtensionPath);
        Assert.Contains("pi.on(\"before_agent_start\"", js);
        Assert.Contains("export default function", js);
        var note = defaults.PromptNote();
        Assert.Contains(AgentBrowserDefaults.NoteMarker, note);
        Assert.Contains("watches live", note);
        Assert.Contains("`browser.open`", note);
        Assert.Contains("localhost", note);
        Assert.Contains("Never use `bash open`", note);
        Assert.Contains("`persist: true`", note);
        Assert.Contains("`tab.ariaSnapshot()`", note);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(defaults.Directory));
    }

    [Fact]
    public void With_Tern_every_omp_gets_its_own_pane_and_cmux_stays_as_the_fallback()
    {
        using var bridge = AgentBrowserBridge.TryStart();
        using var tern = TernHost.TryStart();
        Assert.NotNull(bridge);
        Assert.NotNull(tern);
        var dir = TestProcesses.TempDir("browser-defaults-tern");
        var defaults = new AgentBrowserDefaults(bridge, Path.Combine(dir, "agent-browser"), tern, inherited: _ => null);
        Assert.Equal(AgentBrowserBackend.Tern, defaults.Backend);

        var options = new OmpRuntimeOptions { Command = "omp" };
        var chat = defaults.ForSession(options, new LaunchRequest());
        var tui = defaults.ForTui(options, dir);
        foreach (var spec in new[] { chat, tui })
        {
            Assert.Equal(tern.Endpoint, spec.Environment[TernHost.SocketVariable]);
            Assert.Equal("1", spec.Environment[TernHost.TernFlagVariable]);
            Assert.Equal(bridge.Endpoint, spec.Environment[AgentBrowserBridge.SocketVariable]); // a Tern-less omp still finds the pane
            Assert.Equal(defaults.OverlayPath, spec.Environment[AgentBrowserDefaults.ConfigFilesVariable]);
        }
        Assert.NotEqual(chat.Environment[TernHost.PaneVariable], tui.Environment[TernHost.PaneVariable]);
        var yaml = File.ReadAllLines(defaults.OverlayPath).Where(l => !l.StartsWith('#')).ToArray();
        Assert.Equal(["browser:", "  enabled: true", "  cdpUrl: \"\"", "  cmux: true", "  tern: true", "  idleCloseSec: 0"], yaml);
        var note = defaults.PromptNote();
        Assert.Contains("trusted native input", note);
        Assert.DoesNotContain("cmux surface", note);

        // Their own Tern socket: left exactly as it is; cmux only: an inherited Tern pane can't win over the app's
        var own = new OmpRuntimeOptions { Command = "omp", Environment = new() { [TernHost.SocketVariable] = "/tmp/their-tern.sock" } };
        Assert.Equal("/tmp/their-tern.sock", defaults.ForSession(own, new LaunchRequest()).Environment[TernHost.SocketVariable]);
        Assert.False(defaults.ForSession(own, new LaunchRequest()).Environment.ContainsKey(TernHost.PaneVariable));
        var cmuxOnly = new AgentBrowserDefaults(bridge, Path.Combine(dir, "agent-browser-cmux"), inherited: _ => null).ForSession(options, new LaunchRequest());
        Assert.Equal("0", cmuxOnly.Environment[TernHost.TernFlagVariable]);
        Assert.False(cmuxOnly.Environment.ContainsKey(TernHost.SocketVariable));

        // The app's own switch between the two
        Assert.Equal(AgentBrowserBackend.Tern, AgentBrowserDefaults.ChooseBackend(k => k == AgentBrowserDefaults.BackendVariable ? " Tern " : null));
        Assert.Equal(AgentBrowserBackend.Cmux, AgentBrowserDefaults.ChooseBackend(k => k == AgentBrowserDefaults.BackendVariable ? "cmux" : null));
        Assert.Equal(AgentBrowserDefaults.DefaultBackend, AgentBrowserDefaults.ChooseBackend(_ => null));
    }

    [AvaloniaFact]
    public async Task The_switch_in_general_settings_is_on_by_default_and_saved_off()
    {
        var store = new ClientSettingsStore(Path.Combine(TestProcesses.TempDir("browser-switch"), "omp-gui.local.json"));
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(), null, store);
        var w = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        w.Show();
        vm.OpenSettingsCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!vm.IsSettingsOpen && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal("general", vm.SettingsCategory);
        var box = w.FindControl<ToggleButton>("AgentBrowserBox")!;
        Assert.True(box.IsEffectivelyVisible);
        Assert.True(box.IsChecked);
        Assert.Null(store.Load().AgentUsesGuiBrowser); // default on without writing anything
        box.IsChecked = false;
        Assert.False(vm.AgentUsesGuiBrowser);
        Assert.False(store.Load().AgentUsesGuiBrowser);
        await vm.DisposeAsync();
        w.Close();
    }
}
