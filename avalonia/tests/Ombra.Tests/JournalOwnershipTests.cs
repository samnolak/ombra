using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ombra.App;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class JournalOwnershipTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    internal sealed class Child : IAsyncDisposable
    {
        internal Process Process { get; }
        private readonly Task<string> _stderr;

        internal Child(ProcessStartInfo start)
        {
            start.UseShellExecute = false;
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            Process = System.Diagnostics.Process.Start(start)!;
            _stderr = Process.StandardError.ReadToEndAsync();
        }

        internal async Task<string> Line() => await Process.StandardOutput.ReadLineAsync().WaitAsync(Wait)
            ?? throw new IOException("Child closed stdout: " + await _stderr.WaitAsync(Wait));

        internal async Task<JsonObject> Ready()
        {
            var response = JsonNode.Parse(await Line())!.AsObject();
            Assert.Equal("ready", response["status"]!.GetValue<string>());
            return response;
        }

        internal async Task Save(string text)
        {
            await Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(text));
            await Process.StandardInput.FlushAsync();
            Assert.Equal("saved", await Line());
        }

        internal async Task Exit(int expected, bool closeInput = true)
        {
            if (closeInput) Process.StandardInput.Close();
            await Process.WaitForExitAsync().WaitAsync(Wait);
            Assert.True(Process.ExitCode == expected, $"exit {Process.ExitCode}, expected {expected}: {await _stderr}");
        }

        internal async Task Crash()
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync().WaitAsync(Wait);
        }

        public async ValueTask DisposeAsync()
        {
            await Crash();
            Process.Dispose();
        }
    }

    internal static Child Start(string mode, string config)
    {
        var fake = TestProcesses.Fake(mode);
        var start = new ProcessStartInfo(fake.FileName);
        foreach (var arg in fake.Arguments) start.ArgumentList.Add(arg);
        foreach (var arg in fake.SessionArguments) start.ArgumentList.Add(arg);
        start.ArgumentList.Add(config);
        return new Child(start);
    }

    private static Child App(string config, bool environment, params string[] args)
    {
        var build = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var dll = RuntimeInstallerTests.RepoFile(Path.Combine("avalonia", "src", "Ombra.App", "bin", build, "net10.0", "Ombra.dll"));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        start.ArgumentList.Add(dll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (environment) start.Environment[OmpRuntimeOptions.ConfigEnvVar] = config;
        else { start.ArgumentList.Add("--config"); start.ArgumentList.Add(config); }
        return new Child(start);
    }

    [Theory]
    [InlineData("draft-owner")]
    [InlineData("client-owner")]
    public async Task Two_real_processes_exclude_overwrite_allow_isolated_configs_and_recover_after_kill(string mode)
    {
        var root = TestProcesses.TempDir("journal-processes");
        var config = Path.Combine(root, "settings.json");
        var path = DraftStore.PathFor(config);
        File.WriteAllText(path + ".lock", "persistent lock state");
        var lockBytes = File.ReadAllBytes(path + ".lock");
        await using var first = Start(mode, config);
        Assert.Null((await first.Ready())["error"]);
        await first.Save("first committed edit");
        await first.Save("latest committed input\nwith another line");
        var committed = File.ReadAllBytes(path);

        await using (var second = Start(mode, config))
        {
            Assert.Equal("busy", await second.Line());
            await second.Exit(2);
        }
        Assert.Equal(committed, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp-*"));
        Assert.Equal("latest committed input\nwith another line", Assert.Single(DraftStore.ReadSnapshot(path).All).Composer.Text);

        await using (var independent = Start(mode, Path.Combine(root, "isolated.json")))
        {
            Assert.Null((await independent.Ready())["text"]);
            await independent.Save("independent input");
            await independent.Exit(0);
        }
        await first.Crash();
        Assert.True(File.Exists(path + ".lock"));
        Assert.Equal(lockBytes, File.ReadAllBytes(path + ".lock"));
        await using var restarted = Start(mode, config);
        Assert.Equal("latest committed input\nwith another line", (await restarted.Ready())["text"]!.GetValue<string>());
        await restarted.Save("edit after recovery");
        await restarted.Exit(0);
        Assert.Equal("edit after recovery", Assert.Single(DraftStore.ReadSnapshot(path).All).Composer.Text);
        Assert.Equal(lockBytes, File.ReadAllBytes(path + ".lock"));
    }

    [Fact]
    public async Task Simultaneous_first_loads_have_exactly_one_writer_before_any_journal_exists()
    {
        var config = Path.Combine(TestProcesses.TempDir("journal-race"), "settings.json");
        await using var a = Start("draft-owner", config);
        await using var b = Start("draft-owner", config);
        var results = await Task.WhenAll(a.Line(), b.Line());
        Assert.Single(results, r => r == "busy");
        var winner = results[0] == "busy" ? b : a;
        var loser = results[0] == "busy" ? a : b;
        Assert.False(File.Exists(DraftStore.PathFor(config)));
        await loser.Exit(2);
        await winner.Save("only the admitted process writes");
        await winner.Exit(0);
        Assert.Equal("only the admitted process writes", Assert.Single(DraftStore.ReadSnapshot(DraftStore.PathFor(config)).All).Composer.Text);
    }

    [Fact]
    public async Task Corrupt_journal_is_owned_until_failed_dispose_and_never_replaced()
    {
        var config = Path.Combine(TestProcesses.TempDir("corrupt-owned-journal"), "settings.json");
        var path = DraftStore.PathFor(config);
        const string damaged = "{original damaged private input";
        File.WriteAllText(path, damaged);
        var owner = new DraftStore(path);
        try
        {
            Assert.NotNull(owner.LastError);
            Assert.NotNull(DraftStore.ReadSnapshot(path).LastError);
            await using var loser = Start("draft-owner", config);
            Assert.Equal("busy", await loser.Line());
            await loser.Exit(2);
            owner.Update("new", d => d with { Composer = DraftContent.Empty with { Text = "do not replace damaged input" } }, Path.GetDirectoryName(path)!);
            Assert.Throws<IOException>(owner.Flush);
        }
        finally { Assert.Throws<IOException>(owner.Dispose); }
        Assert.Equal(damaged, File.ReadAllText(path));
        await using var next = Start("draft-owner", config);
        Assert.NotNull((await next.Ready())["error"]);
        await next.Exit(3);
        Assert.Equal(damaged, File.ReadAllText(path));
    }

    [Fact]
    public void Snapshot_is_read_only_and_failed_acquisition_does_not_change_journal_or_lock_state()
    {
        var root = TestProcesses.TempDir("journal-read-only");
        var missing = Path.Combine(root, "absent", "drafts.json");
        Assert.Empty(DraftStore.ReadSnapshot(missing).All);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
        var path = Path.Combine(root, "drafts.json");
        File.WriteAllText(path, "{damaged}");
        Assert.NotNull(DraftStore.ReadSnapshot(path).LastError);
        Assert.False(File.Exists(path + ".lock"));
        Directory.CreateDirectory(path + ".lock");
        var failure = Record.Exception(() => new DraftStore(path));
        Assert.NotNull(failure);
        Assert.IsNotType<DraftStoreInUseException>(failure);
        Assert.Equal("{damaged}", File.ReadAllText(path));
        Directory.Delete(path + ".lock");
        File.WriteAllText(path + ".lock", "not a PID or expiring lease");
        var owner = new DraftStore(path);
        Assert.Throws<DraftStoreInUseException>(() => new DraftStore(path));
        Assert.Throws<IOException>(owner.Dispose);
        Assert.Equal("not a PID or expiring lease", File.ReadAllText(path + ".lock"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Early_admission_refuses_app_before_startup_writes_but_version_is_side_effect_free(bool environment)
    {
        var root = TestProcesses.TempDir("client-admission");
        var config = Path.Combine(root, "not-created", "settings.json");
        await using var owner = Start("admission-owner", config);
        Assert.Equal("ready", await owner.Line());
        Assert.False(Directory.Exists(Path.GetDirectoryName(config)));
        await using (var second = App(config, environment, "--smoke-exit-after-ms", "1")) await second.Exit(1);
        Assert.False(Directory.Exists(Path.GetDirectoryName(config)));
        await using (var version = App(config, environment, "--version"))
        {
            Assert.StartsWith("Ombra ", await version.Line());
            await version.Exit(0);
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(config)));
        await owner.Exit(0);
    }

    [Theory]
    [InlineData("client-owner")]
    [InlineData("draft-owner")]
    public async Task Legacy_migration_refuses_live_source_writer_then_preserves_last_committed_input(string mode)
    {
        var root = TestProcesses.TempDir("live-legacy-migration");
        var old = Path.Combine(root, "OmpGui");
        Directory.CreateDirectory(old);
        var legacyConfig = Path.Combine(old, "omp-gui.local.json");
        File.WriteAllText(legacyConfig, "{\"theme\":\"dark\"}");
        var config = Path.Combine(root, "Ombra", "ombra.local.json");
        await using var owner = Start(mode, legacyConfig);
        await owner.Ready();
        await owner.Save("input before rejected migration");
        var committed = File.ReadAllBytes(DraftStore.PathFor(legacyConfig));
        var logs = new List<string>();
        var beforeMove = false;
        Assert.False(Ombra.App.Services.LegacyNames.MigrateRoots([root], logs.Add, () => beforeMove = true));
        Assert.False(beforeMove);
        Assert.Empty(logs);
        Assert.False(Directory.Exists(Path.GetDirectoryName(config)));
        Assert.Null(new DirectoryInfo(old).LinkTarget);
        Assert.Equal(committed, File.ReadAllBytes(DraftStore.PathFor(legacyConfig)));
        // A failed secondary acquisition did not leave the destination's admission owned.
        await using (var destination = Start("admission-owner", config))
        {
            Assert.Equal("ready", await destination.Line());
            await destination.Exit(0);
        }
        await owner.Save("latest input before exit");
        await owner.Exit(0);
        // Match Program: own the destination, then acquire migration guards on the same startup thread.
        using (var admission = ClientInstanceLease.TryAcquire(config))
        {
            Assert.NotNull(admission);
            Assert.True(Ombra.App.Services.LegacyNames.MigrateRoots([root], logs.Add, () =>
                Assert.Throws<DraftStoreInUseException>(() => new DraftStore(DraftStore.PathFor(legacyConfig)))));
        }
        Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(config));
        Assert.NotNull(new DirectoryInfo(old).LinkTarget);
        Assert.Equal("latest input before exit", Assert.Single(DraftStore.ReadSnapshot(DraftStore.PathFor(config)).All).Composer.Text);
        await using var restarted = Start("client-owner", config);
        Assert.Equal("latest input before exit", (await restarted.Ready())["text"]!.GetValue<string>());
        await restarted.Exit(0);
    }

    [Fact]
    public async Task Legacy_migration_also_refuses_an_already_owned_destination_without_creating_it()
    {
        var root = TestProcesses.TempDir("admitted-legacy-migration");
        var old = Path.Combine(root, "OmpGui");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "omp-gui.local.json"), "{\"theme\":\"dark\"}");
        var config = Path.Combine(root, "Ombra", "ombra.local.json");
        await using var owner = Start("admission-owner", config);
        Assert.Equal("ready", await owner.Line());
        Assert.False(Ombra.App.Services.LegacyNames.MoveDataFolder(root, _ => { }));
        Assert.False(Directory.Exists(Path.GetDirectoryName(config)));
        Assert.Null(new DirectoryInfo(old).LinkTarget);
        await owner.Exit(0);
        Assert.True(Ombra.App.Services.LegacyNames.MoveDataFolder(root, _ => { }));
        Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(config));
    }

    [Fact]
    public async Task Directory_and_configuration_symlinks_share_both_ownership_layers()
    {
        if (OperatingSystem.IsWindows()) return; // creating symbolic links needs an optional Windows privilege
        var root = TestProcesses.TempDir("journal-aliases");
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        var config = Path.Combine(real, "settings.json");
        File.WriteAllText(config, "{}");
        var directoryAlias = Path.Combine(root, "directory-link");
        Directory.CreateSymbolicLink(directoryAlias, real);
        var fileAlias = Path.Combine(root, "settings-link.json");
        File.CreateSymbolicLink(fileAlias, config);
        await using var owner = Start("client-owner", config);
        await owner.Ready();
        await owner.Save("saved through canonical configuration");
        foreach (var alias in new[] { Path.Combine(directoryAlias, "settings.json"), fileAlias })
        {
            Assert.Equal(DraftStore.PathFor(config), DraftStore.PathFor(alias));
            foreach (var mode in new[] { "draft-owner", "client-owner" })
            {
                await using var loser = Start(mode, alias);
                Assert.Equal("busy", await loser.Line());
                await loser.Exit(2);
            }
            await using var app = App(alias, environment: true, "--smoke-exit-after-ms", "1");
            await app.Exit(1);
        }
        await owner.Exit(0);
    }

    [AvaloniaFact]
    public async Task Application_owned_journal_remains_exclusive_after_view_model_shutdown()
    {
        var root = TestProcesses.TempDir("app-owned-journal");
        var settings = new ClientSettingsStore(Path.Combine(root, "settings.json"));
        using var journal = new DraftStore(DraftStore.PathFor(settings.Path));
        var session = new SessionController(TestProcesses.FakeFactory("normal", Path.Combine(root, "sessions")), new LaunchRequest(root));
        var vm = new MainViewModel(session, new AppArgs(), settings: settings, drafts: journal);
        vm.ComposerText = "input at exit";
        await vm.DisposeAsync();
        Assert.Equal("input at exit", Assert.Single(DraftStore.ReadSnapshot(journal.Path!).All).Composer.Text);
        Assert.Throws<DraftStoreInUseException>(() => new DraftStore(journal.Path));
    }

    [AvaloniaFact]
    public void Refusal_window_scrolls_long_content_keeps_close_visible_and_supports_keyboard_dismissal()
    {
        var window = Ombra.App.App.CreateStartupRefusalWindow(
            "Draft storage is unavailable", string.Concat(Enumerable.Repeat("A long translated storage error with details.\n", 200)),
            "/" + new string('p', 2048) + "/settings.json");
        window.Width = 320;
        window.Height = 180;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var close = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StartupRefusalClose");
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "StartupRefusalScroll");
            var at = close.TranslatePoint(default, window)!.Value;
            Assert.True(close.Bounds.Height > 0);
            Assert.True(at.Y >= 0 && at.Y + close.Bounds.Height <= window.ClientSize.Height);
            Assert.Same(close, window.FocusManager?.GetFocusedElement());
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.True(scroll.Viewport.Height > 0);
            scroll.ScrollToEnd();
            window.UpdateLayout();
            Assert.True(scroll.Offset.Y > 0);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    }
}
