using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OmpGui.App;
using OmpGui.App.Services;
using OmpGui.App.Services.Runtime;
using OmpGui.App.ViewModels;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>
/// Self-update: only a manifest signed with the release key is trusted; a verified package is unpacked next to the
/// app, must start and report its version, and is swapped in by the apply script once the app has quit (the script
/// really runs here, on Linux and macOS). A new runtime pack takes over from the one the previous version installed.
/// </summary>
public sealed class UpdateInstallTests
{
    private const string Feed = "https://updates.test/update.json";

    private sealed class Server(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static byte[] Manifest(string version, object? runtime = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 2,
            version,
            notes = "Signed.",
            runtime,
            assets = new Dictionary<string, object>
            {
                ["linux-x64"] = new { file = $"omp-gui-{version}-linux-x64.tar.gz", url = $"https://updates.test/omp-gui-{version}-linux-x64.tar.gz", sha256 = new string('a', 64), size = 10 },
            },
        });

    private static UpdateChecker Checker(Dictionary<string, byte[]> files, string? key = null) =>
        new(new HttpClient(new Server(files)), Feed, "0.1.0", "linux-x64", key ?? TestSigning.PublicKey);

    [Fact]
    public async Task A_manifest_signed_with_the_release_key_is_trusted()
    {
        var m = Manifest("0.2.0");
        var check = await Checker(new() { [Feed] = m, [Feed + ".sig"] = TestSigning.Sign(m) }).CheckAsync(CancellationToken.None);
        Assert.True(check.Available);
        Assert.False(check.BringsNewOmp);
    }

    [Fact]
    public async Task An_unsigned_tampered_or_foreign_signed_manifest_is_refused()
    {
        var m = Manifest("0.2.0");
        var unsigned = await Assert.ThrowsAsync<InvalidDataException>(() => Checker(new() { [Feed] = m }).CheckAsync(CancellationToken.None));
        Assert.Contains("not signed", unsigned.Message);

        var tampered = Manifest("0.3.0");
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => Checker(new() { [Feed] = tampered, [Feed + ".sig"] = TestSigning.Sign(m) }).CheckAsync(CancellationToken.None));
        Assert.Contains("not trusted", e.Message);

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherKey = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
        await Assert.ThrowsAsync<InvalidDataException>(() => Checker(new() { [Feed] = m, [Feed + ".sig"] = TestSigning.Sign(m) }, otherKey).CheckAsync(CancellationToken.None));

        await Assert.ThrowsAsync<InvalidDataException>(() => Checker(new() { [Feed] = m, [Feed + ".sig"] = "not base64 at all"u8.ToArray() }).CheckAsync(CancellationToken.None));
        // No key built in: nothing can be trusted, so nothing is offered.
        await Assert.ThrowsAsync<InvalidDataException>(() => new UpdateChecker(new HttpClient(new Server([])), Feed, "0.1.0", "linux-x64", null).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public void The_built_in_release_key_is_a_P256_public_key()
    {
        var key = UpdateChecker.BuiltInPublicKey();
        Assert.NotNull(key);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key!), out _);
        Assert.Equal(256, ecdsa.KeySize);
    }

    [Fact]
    public async Task A_version_that_brings_another_omp_says_so()
    {
        var m = Manifest("0.2.0", new { pack = "omp-19.0.0-bun-1.5.0", omp = "19.0.0", bun = "1.5.0" });
        var check = await Checker(new() { [Feed] = m, [Feed + ".sig"] = TestSigning.Sign(m) }).CheckAsync(CancellationToken.None);
        Assert.True(check.BringsNewOmp);
        Assert.Equal("19.0.0", check.Runtime!.Omp);
        var same = Manifest("0.2.0", new { pack = RuntimePack.Name, omp = RuntimePack.OmpVersion, bun = RuntimePack.BunVersion });
        Assert.False((await Checker(new() { [Feed] = same, [Feed + ".sig"] = TestSigning.Sign(same) }).CheckAsync(CancellationToken.None)).BringsNewOmp);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9/update.json", true)]
    [InlineData("http://localhost:9/update.json", true)]
    [InlineData("http://updates.test/update.json", false)]
    public async Task Plain_http_only_on_this_computer(string feed, bool allowed)
    {
        var checker = new UpdateChecker(new HttpClient(new Server([])), feed, "0.1.0", "linux-x64", TestSigning.PublicKey);
        if (allowed) Assert.Equal("No release is published yet.", (await checker.CheckAsync(CancellationToken.None)).Message);
        else await Assert.ThrowsAsync<InvalidDataException>(() => checker.CheckAsync(CancellationToken.None));
    }

    // ───────────── Installing ─────────────

    /// <summary>An "app" folder: <c>OmpGui</c> is a script that answers <c>--version</c> with <paramref name="version"/>.</summary>
    private static void WriteApp(string dir, string version, string marker)
    {
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "OmpGui");
        File.WriteAllText(exe, $"#!/bin/sh\necho \"OMP GUI {version} linux-x64\"\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), marker);
    }

    /// <summary>A package as tools/package/package.sh makes it: <c>omp-gui-&lt;v&gt;-linux-x64/…</c> in a tar.gz.</summary>
    private static string Package(string folder, string version, bool withExe = true, string? reports = null)
    {
        var src = Path.Combine(folder, "src-" + version);
        var top = Path.Combine(src, $"omp-gui-{version}-linux-x64");
        WriteApp(top, reports ?? version, "new " + version);
        if (!withExe) File.Delete(Path.Combine(top, "OmpGui"));
        var file = Path.Combine(folder, $"omp-gui-{version}-linux-x64.tar.gz");
        using (var fs = File.Create(file))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            TarFile.CreateFromDirectory(src, gz, includeBaseDirectory: false);
        Directory.Delete(src, true);
        return file;
    }

    private static (UpdateInstaller Installer, string Root, string State) Installed(string name)
    {
        var parent = TestProcesses.TempDir(name);
        var root = Path.Combine(parent, "omp-gui-0.1.0-linux-x64");
        WriteApp(root, "0.1.0", "old");
        var state = Path.Combine(parent, "state");
        // The script runs to completion here instead of in the background.
        return (new UpdateInstaller(root, "OmpGui", state) { Launch = psi => { using var p = Process.Start(psi)!; p.WaitForExit(); } }, root, state);
    }

    /// <summary>A process id that has already exited: the script does not wait.</summary>
    private static int ExitedPid()
    {
        using var p = Process.Start(new ProcessStartInfo("/bin/sh", "-c true"))!;
        p.WaitForExit();
        return p.Id;
    }

    [Fact]
    public async Task A_verified_package_replaces_the_app_after_it_quits_and_keeps_the_previous_version()
    {
        if (OperatingSystem.IsWindows()) return; // the PowerShell script is not run by the tests (not verified on Windows)
        var (installer, root, state) = Installed("upd-apply");
        Assert.True(installer.CanWrite());
        var package = Package(Path.GetDirectoryName(root)!, "0.2.0");

        var staged = await installer.StageAsync(package, "0.2.0", CancellationToken.None);
        Assert.False(File.Exists(package)); // the package is not kept once unpacked
        Assert.Equal("old", File.ReadAllText(Path.Combine(root, "marker.txt"))); // nothing replaced yet

        var script = installer.Apply(staged, "0.1.0", ExitedPid(), relaunch: false);
        Assert.EndsWith("apply-update.sh", script);
        Assert.Equal("new 0.2.0", File.ReadAllText(Path.Combine(root, "marker.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(installer.PreviousDirectory, "marker.txt")));
        Assert.False(Directory.Exists(staged.Staging));
        var result = UpdateInstaller.TakeLastResult(state);
        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.Equal(("0.1.0", "0.2.0"), (result.From, result.To));
        Assert.Null(UpdateInstaller.TakeLastResult(state)); // read once
        Assert.Contains("updated to 0.2.0", File.ReadAllText(Path.Combine(state, "apply.log")));

        // The next update replaces the kept previous version, never anything else.
        var next = await installer.StageAsync(Package(Path.GetDirectoryName(root)!, "0.3.0"), "0.3.0", CancellationToken.None);
        installer.Apply(next, "0.2.0", ExitedPid(), relaunch: false);
        Assert.Equal("new 0.3.0", File.ReadAllText(Path.Combine(root, "marker.txt")));
        Assert.Equal("new 0.2.0", File.ReadAllText(Path.Combine(installer.PreviousDirectory, "marker.txt")));
    }

    [Fact]
    public async Task A_package_without_the_app_or_of_another_version_changes_nothing()
    {
        if (OperatingSystem.IsWindows()) return;
        var (installer, root, _) = Installed("upd-bad-pkg");
        var parent = Path.GetDirectoryName(root)!;
        var before = Directory.GetFileSystemEntries(parent).Order().ToList();

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.StageAsync(Package(parent, "0.2.0", withExe: false), "0.2.0", CancellationToken.None));
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => installer.StageAsync(Package(parent, "0.2.0", reports: "0.1.9"), "0.2.0", CancellationToken.None));
        Assert.Contains("did not start", e.Message);
        File.WriteAllText(Path.Combine(parent, "x.tar.gz"), "not a tarball");
        await Assert.ThrowsAnyAsync<Exception>(() => installer.StageAsync(Path.Combine(parent, "x.tar.gz"), "0.2.0", CancellationToken.None));

        Assert.Equal(before, Directory.GetFileSystemEntries(parent).Order().ToList()); // no staging folder left
        Assert.Equal("old", File.ReadAllText(Path.Combine(root, "marker.txt")));
    }

    [Fact]
    public async Task A_failed_swap_puts_the_current_version_back()
    {
        if (OperatingSystem.IsWindows()) return;
        var (plain, root, state) = Installed("upd-putback");
        var staged = await plain.StageAsync(Package(Path.GetDirectoryName(root)!, "0.2.0"), "0.2.0", CancellationToken.None);
        // The new version vanishes after the app handed it over and before the script moves it in.
        var installer = new UpdateInstaller(root, "OmpGui", state)
        {
            Launch = psi =>
            {
                Directory.Delete(staged.NewRoot, recursive: true);
                using var p = Process.Start(psi)!;
                p.WaitForExit();
            },
        };
        installer.Apply(staged, "0.1.0", ExitedPid(), relaunch: false);
        Assert.Equal("old", File.ReadAllText(Path.Combine(root, "marker.txt")));
        Assert.False(Directory.Exists(installer.PreviousDirectory));
        var result = UpdateInstaller.TakeLastResult(state);
        Assert.False(result!.Ok);
        Assert.Contains("put back", result.Error);
    }

    [Fact]
    public void Script_values_are_quoted_for_the_shell()
    {
        var v = new UpdateInstaller.ScriptValues(42, "/a b/it's here", "/n", "/s", "/p", "/r", "/l", "OmpGui", "0.1.0", "0.2.0", true, ["--config", "/x y/c.json"], false);
        var sh = UpdateInstaller.ShellScript(v);
        Assert.Contains("'/a b/it'\\''s here'", sh);
        Assert.Contains("'--config' '/x y/c.json'", sh);
        Assert.Contains("kill -0 42", sh);
        var ps = UpdateInstaller.PowerShellScript(v);
        Assert.Contains("'/a b/it''s here'", ps);
        Assert.Contains("Wait-Process -Id 42", ps);
    }

    [Fact]
    public void A_development_build_does_not_replace_itself()
    {
        // Tests run under the dotnet host (testhost), not the packaged OmpGui executable.
        Assert.Null(UpdateInstaller.ForThisApp(out var why));
        Assert.Contains("development build", why);
    }

    // ───────────── omp moves with the client ─────────────

    private static void FakePack(string root, string name, string omp, string platform = "linux-x64", DateTimeOffset? at = null)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "bun"));
        File.WriteAllText(Path.Combine(dir, "bun", "bun"), "");
        var cli = Path.Combine(dir, "omp", RuntimePack.CliEntry.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        File.WriteAllText(cli, "");
        File.WriteAllText(Path.Combine(dir, "installed.json"), JsonSerializer.Serialize(new { pack = name, omp, bun = "1.4.0", platform, installedAt = at ?? DateTimeOffset.UtcNow }));
    }

    [Fact]
    public void The_pack_an_earlier_version_installed_is_found_and_removed_only_once_the_new_one_is_in()
    {
        var root = TestProcesses.TempDir("rt-prev");
        var installer = new RuntimeInstaller(new HttpClient(), root, "linux-x64");
        Assert.Null(installer.FindPrevious());
        FakePack(root, "omp-18.0.0-bun-1.3.0", "18.0.0", at: DateTimeOffset.UtcNow.AddDays(-30));
        FakePack(root, "omp-18.1.0-bun-1.4.0", "18.1.0", at: DateTimeOffset.UtcNow.AddDays(-2));
        Directory.CreateDirectory(Path.Combine(root, "someone-elses-folder"));
        Directory.CreateDirectory(Path.Combine(root, "omp-17.0.0-bun-1.2.0")); // no installed.json: not ours to touch

        Assert.Null(installer.FindInstalled());
        var prev = installer.FindPrevious();
        Assert.Equal(("omp-18.1.0-bun-1.4.0", "18.1.0"), (prev!.Pack, prev.Omp));
        Assert.Equal(0, installer.RemovePrevious()); // this version's pack is not installed yet: keep the one in use

        FakePack(root, RuntimePack.Name, RuntimePack.OmpVersion);
        Assert.NotNull(installer.FindInstalled());
        Assert.Equal(2, installer.RemovePrevious());
        Assert.Null(installer.FindPrevious());
        Assert.True(Directory.Exists(Path.Combine(root, "someone-elses-folder")));
        Assert.True(Directory.Exists(Path.Combine(root, "omp-17.0.0-bun-1.2.0")));
    }

    // ───────────── The window ─────────────

    private static async Task Until(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI never reached: " + what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
    }

    [AvaloniaFact]
    public async Task The_automatic_check_offers_an_update_in_the_sidebar_and_can_be_turned_off()
    {
        var m = Manifest("0.2.0", new { pack = "omp-19.0.0-bun-1.5.0", omp = "19.0.0", bun = "1.5.0" });
        var config = Path.Combine(TestProcesses.TempDir("upd-auto"), "omp-gui.local.json");
        var store = new ClientSettingsStore(config);
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(), null, store)
        {
            Updates = Checker(new() { [Feed] = m, [Feed + ".sig"] = TestSigning.Sign(m) }),
            UpdateCheckDelay = TimeSpan.FromMilliseconds(50),
        };
        vm.OnWindowOpened();
        await Until(() => vm.UpdateAvailable, "automatic check offered the update");
        Assert.True(vm.ShowUpdatePill);
        Assert.Equal("Update available: 0.2.0", vm.UpdatePill);
        Assert.Contains("moves omp to 19.0.0", vm.UpdateOmpText);

        vm.OpenUpdatesCommand.Execute(null);
        await Until(() => vm.IsSettingsOpen, "settings open");
        Assert.Equal("updates", vm.SettingsCategory);
        vm.CheckForUpdatesAutomatically = false;
        Assert.False(store.Load().CheckForUpdates);
        await vm.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task After_an_update_the_sidebar_says_how_it_went()
    {
        var parent = TestProcesses.TempDir("upd-outcome");
        var root = Path.Combine(parent, "app");
        WriteApp(root, "0.2.0", "new");
        var state = Path.Combine(parent, "state");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "last-update.json"), """{"ok":true,"from":"0.1.0","to":"0.2.0","error":null,"previous":"/x/.app.previous"}""");
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs())
        {
            Updates = new UpdateChecker(new HttpClient(new Server([])), Feed, "0.2.0", "linux-x64", TestSigning.PublicKey),
            UpdateInstaller = new UpdateInstaller(root, "OmpGui", state),
            UpdateCheckDelay = TimeSpan.FromHours(1),
        };
        vm.OnWindowOpened();
        await Until(() => vm.ShowUpdatePill, "outcome shown");
        Assert.Equal("Updated to 0.2.0", vm.UpdatePill);
        Assert.Contains("Updated from 0.1.0 to 0.2.0", vm.UpdateText);
        Assert.Equal("Download and install", vm.UpdateActionLabel);
        vm.OpenUpdatesCommand.Execute(null);
        await Until(() => !vm.ShowUpdatePill, "outcome dismissed once seen");
        await vm.DisposeAsync();
    }
}
