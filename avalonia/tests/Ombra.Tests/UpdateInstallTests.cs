using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ombra.App;
using Ombra.App.Localization;
using Ombra.App.Services;
using Ombra.App.Services.Runtime;
using Ombra.App.ViewModels;
using Ombra.ClientCore;

namespace Ombra.Tests;

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
                ["linux-x64"] = new { file = $"ombra-{version}-linux-x64.tar.gz", url = $"https://updates.test/ombra-{version}-linux-x64.tar.gz", sha256 = new string('a', 64), size = 10 },
            },
        });

    private static UpdateChecker Checker(Dictionary<string, byte[]> files, string? key = null) =>
        new(new HttpClient(new Server(files)), Feed, "0.1.0", "linux-x64", key ?? TestSigning.PublicKey);

    [Fact]
    public void Apply_propagates_independent_launcher_failure_without_retrying_as_an_owned_child()
    {
        var request = HelperFixture("update-independent-launch");
        var launches = 0;
        var failure = new System.ComponentModel.Win32Exception(5, "job breakaway denied");
        var installer = new UpdateInstaller(request.Target, request.Executable, request.State);
        var staged = new StagedUpdate(request.To, request.Staging, request.NewRoot);
        Assert.Same(failure, Assert.Throws<System.ComponentModel.Win32Exception>(() =>
            UpdateApplyHelper.Prepare(installer, staged, request.From, Environment.ProcessId, true, [], _ =>
            {
                launches++;
                throw failure;
            })));
        Assert.Equal(1, launches);
        Assert.True(Directory.Exists(staged.NewRoot));
        Assert.Equal("old", File.ReadAllText(Path.Combine(request.Target, "marker.txt")));
        Assert.Single(Directory.GetDirectories(request.State)); // only the fixture helper, not the failed launch's copy
    }

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
        if (allowed) Assert.Equal(Loc.T("No release is published yet."), (await checker.CheckAsync(CancellationToken.None)).Message);
        else await Assert.ThrowsAsync<InvalidDataException>(() => checker.CheckAsync(CancellationToken.None));
    }

    // ───────────── Installing ─────────────

    /// <summary>An "app" folder: <c>Ombra</c> is a script that answers <c>--version</c> with <paramref name="version"/>.</summary>
    private static void WriteApp(string dir, string version, string marker)
    {
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "Ombra");
        File.WriteAllText(exe, $"#!/bin/sh\necho \"Ombra {version} linux-x64\"\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
        File.WriteAllText(Path.Combine(dir, "marker.txt"), marker);
    }

    /// <summary>A package as tools/package/package.sh makes it: <c>ombra-&lt;v&gt;-linux-x64/…</c> in a tar.gz.</summary>
    private static string Package(string folder, string version, bool withExe = true, string? reports = null)
    {
        var src = Path.Combine(folder, "src-" + version);
        var top = Path.Combine(src, $"ombra-{version}-linux-x64");
        WriteApp(top, reports ?? version, "new " + version);
        if (!withExe) File.Delete(Path.Combine(top, "Ombra"));
        var file = Path.Combine(folder, $"ombra-{version}-linux-x64.tar.gz");
        using (var fs = File.Create(file))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            TarFile.CreateFromDirectory(src, gz, includeBaseDirectory: false);
        Directory.Delete(src, true);
        return file;
    }

    private static (UpdateInstaller Installer, string Root, string State) Installed(string name)
    {
        var parent = TestProcesses.TempDir(name);
        var root = Path.Combine(parent, "ombra-0.1.0-linux-x64");
        WriteApp(root, "0.1.0", "old");
        var state = Path.Combine(parent, "state");
        // The script runs to completion here instead of in the background.
        return (new UpdateInstaller(root, "Ombra", state) { Launch = psi => { using var p = Process.Start(psi)!; p.WaitForExit(); } }, root, state);
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
        if (OperatingSystem.IsWindows()) return; // Unix package/script fixture; app-owned helper behavior is covered separately below.
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

    private static string Sh(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? "" : $"exit {p.ExitCode}: {output.Result}{error}";
    }

    [Fact]
    public async Task A_signed_bundle_zipped_with_ditto_keeps_a_valid_signature_when_unpacked()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS code signing");
            return;
        }
        var dir = TestProcesses.TempDir("upd-codesign");
        var app = Path.Combine(dir, "src", "Ombra.app");
        var macos = Path.Combine(app, "Contents", "MacOS");
        Directory.CreateDirectory(macos);
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
            <key>CFBundleExecutable</key><string>Ombra</string>
            <key>CFBundleIdentifier</key><string>dev.ombra.signature-test</string>
            <key>CFBundlePackageType</key><string>APPL</string>
            </dict></plist>
            """);
        File.Copy("/usr/bin/true", Path.Combine(macos, "Ombra"));
        // A non-Mach-O file next to the executable: codesign keeps its signature in extended attributes.
        File.WriteAllText(Path.Combine(macos, "System.Security.Principal.dll"), "not a Mach-O file");
        Assert.Equal("", Sh("/usr/bin/codesign", "--force", "--deep", "--sign", "-", app));
        Assert.Equal("", Sh("/usr/bin/codesign", "--verify", "--deep", "--strict", app));
        var zip = Path.Combine(dir, "Ombra-macos.zip");
        Assert.Equal("", Sh("/usr/bin/ditto", "-c", "-k", "--keepParent", app, zip));

        var unpacked = Path.Combine(dir, "unpacked");
        Directory.CreateDirectory(unpacked);
        await UpdateInstaller.UnpackAsync(zip, unpacked, CancellationToken.None);
        await UpdateInstaller.VerifyCodeSignatureAsync(Path.Combine(unpacked, "Ombra.app"), CancellationToken.None);

        // What the updater did before: ZipFile drops the attributes, and the signature check refuses the result.
        var plain = Path.Combine(dir, "plain");
        ZipFile.ExtractToDirectory(zip, plain);
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.VerifyCodeSignatureAsync(Path.Combine(plain, "Ombra.app"), CancellationToken.None));
        Assert.Contains("code signature", e.Message);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task A_failed_swap_puts_the_current_version_back()
    {
        if (OperatingSystem.IsWindows()) return;
        var (plain, root, state) = Installed("upd-putback");
        var staged = await plain.StageAsync(Package(Path.GetDirectoryName(root)!, "0.2.0"), "0.2.0", CancellationToken.None);
        // The new version vanishes after the app handed it over and before the script moves it in.
        var installer = new UpdateInstaller(root, "Ombra", state)
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
        var v = new UpdateInstaller.ScriptValues(42, "/a b/it's here", "/n", "/s", "/p", "/r", "/l", "Ombra", "0.1.0", "0.2.0", true, ["--config", "/x y/c.json"], false);
        var sh = UpdateInstaller.ShellScript(v);
        Assert.Contains("'/a b/it'\\''s here'", sh);
        Assert.Contains("'--config' '/x y/c.json'", sh);
        Assert.Contains("kill -0 42", sh);
    }

    [Fact]
    public void A_development_build_does_not_replace_itself()
    {
        // Tests run under the dotnet host (testhost), not the packaged Ombra executable.
        Assert.Null(UpdateInstaller.ForThisApp(out var why));
        Assert.Contains("development build", why);
    }

    private static UpdateApplyHelper.Request HelperFixture(string name)
    {
        var root = TestProcesses.TempDir(name);
        // Darwin's /var and /tmp aliases are symlinks; production admission intentionally rejects aliases.
        if (OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal)) root = "/private" + root;
        if (OperatingSystem.IsMacOS() && root.StartsWith("/tmp/", StringComparison.Ordinal)) root = "/private" + root;
        var target = Path.Combine(root, "installed");
        var staging = Path.Combine(root, ".installed.update-2");
        var newRoot = Path.Combine(staging, "package", "new");
        var state = Path.Combine(root, "state");
        var helper = Path.Combine(state, "apply-helper-fixture");
        WriteApp(target, "1", "old");
        WriteApp(newRoot, "2", "new");
        Directory.CreateDirectory(helper);
        File.WriteAllText(Path.Combine(staging, ".ombra-update-staging"), "fixture");
        File.WriteAllText(Path.Combine(staging, UpdateApplyHelper.ReadyMarker), JsonSerializer.Serialize(new StagedUpdate("2", staging, newRoot)));
        var token = new string('a', 64);
        File.WriteAllText(Path.Combine(helper, ".ombra-update-helper"), token);
        return new(token, helper, int.MaxValue, null, target, newRoot, staging, Path.Combine(root, ".installed.previous"),
            state, "Ombra", "1", "2", false, []);
    }

    private sealed class RetryClock
    {
        internal long Ticks;
        internal int Delays;
        internal Task Delay(TimeSpan delay) { Ticks += delay.Ticks; Delays++; return Task.CompletedTask; }
        internal UpdateApplyHelper.Operations Operations(Action<string, string>? move = null, Func<int, long?, Task<bool>>? wait = null) => new()
        {
            Timestamp = () => Ticks, Frequency = TimeSpan.TicksPerSecond, Delay = Delay,
            Move = move ?? Directory.Move, WaitForParent = wait ?? ((_, _) => Task.FromResult(true)),
        };
    }

    [Fact]
    public async Task Helper_retries_transient_locks_then_commits_and_keeps_old_version()
    {
        var r = HelperFixture("helper-retry");
        var attempts = 0;
        var clock = new RetryClock();
        var operations = clock.Operations((source, target) =>
        {
            if (++attempts <= 3) throw new IOException("sharing violation", unchecked((int)0x80070020));
            Directory.Move(source, target);
        });
        Assert.Equal(0, await UpdateApplyHelper.ApplyAsync(r, operations));
        Assert.Equal(3, clock.Delays);
        Assert.Equal("new", File.ReadAllText(Path.Combine(r.Target, "marker.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(r.Previous, "marker.txt")));
        Assert.False(Directory.Exists(r.Staging));
        var result = UpdateInstaller.TakeLastResult(r.State);
        Assert.True(result!.Ok);
        Assert.Equal(("1", "2", r.Previous), (result.From, result.To, result.Previous));
        Assert.Null(UpdateInstaller.TakeLastResult(r.State));
    }

    [Fact]
    public async Task Helper_exhausts_sixty_second_lock_window_without_changing_current_version()
    {
        var r = HelperFixture("helper-lock-timeout");
        var clock = new RetryClock();
        Assert.Equal(1, await UpdateApplyHelper.ApplyAsync(r, clock.Operations((_, _) =>
            throw new IOException("sharing violation", unchecked((int)0x80070020)))));
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, clock.Ticks);
        Assert.Equal("old", File.ReadAllText(Path.Combine(r.Target, "marker.txt")));
        Assert.False(Directory.Exists(r.Previous));
        Assert.False(UpdateInstaller.TakeLastResult(r.State)!.Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Helper_reports_whether_rollback_really_restored_the_previous_version(bool failRollback)
    {
        var r = HelperFixture("helper-rollback");
        var clock = new RetryClock();
        var rollbackAttempts = 0;
        Assert.Equal(1, await UpdateApplyHelper.ApplyAsync(r, clock.Operations((source, target) =>
        {
            if (source == r.NewRoot) throw new IOException("new app disappeared");
            if (source == r.Previous && (++rollbackAttempts <= 2 || failRollback))
                throw new IOException("rollback sharing violation", unchecked((int)0x80070020));
            Directory.Move(source, target);
        })));
        var result = UpdateInstaller.TakeLastResult(r.State)!;
        Assert.False(result.Ok);
        Assert.Contains(failRollback ? "rollback failed" : "was put back", result.Error);
        Assert.Equal("old", File.ReadAllText(Path.Combine(failRollback ? r.Previous : r.Target, "marker.txt")));
        Assert.Equal(failRollback, Directory.Exists(r.Previous));
        Assert.True(Directory.Exists(r.NewRoot));
        Assert.Equal(failRollback ? TimeSpan.FromSeconds(60).Ticks : TimeSpan.FromMilliseconds(500).Ticks, clock.Ticks);
    }

    [Fact]
    public async Task Helper_waits_before_deleting_previous_or_moving_either_app()
    {
        var r = HelperFixture("helper-parent");
        WriteApp(r.Previous, "0", "earlier");
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new RetryClock();
        var apply = UpdateApplyHelper.ApplyAsync(r, clock.Operations(wait: (_, _) => gate.Task));
        Assert.False(apply.IsCompleted);
        Assert.Equal("earlier", File.ReadAllText(Path.Combine(r.Previous, "marker.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(r.Target, "marker.txt")));
        Assert.True(Directory.Exists(r.NewRoot));
        gate.SetResult(false);
        Assert.Equal(1, await apply);
        Assert.Equal("earlier", File.ReadAllText(Path.Combine(r.Previous, "marker.txt")));
        Assert.Contains("nothing was changed", UpdateInstaller.TakeLastResult(r.State)!.Error);
    }

    [Fact]
    public async Task Helper_revalidates_prepared_payload_after_wait_before_deleting_backup()
    {
        var r = HelperFixture("helper-revalidation");
        WriteApp(r.Previous, "0", "earlier");
        var clock = new RetryClock();
        Assert.Equal(1, await UpdateApplyHelper.ApplyAsync(r, clock.Operations(wait: (_, _) =>
        {
            File.Delete(Path.Combine(r.Staging, UpdateApplyHelper.ReadyMarker));
            return Task.FromResult(true);
        })));
        Assert.Equal("earlier", File.ReadAllText(Path.Combine(r.Previous, "marker.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(r.Target, "marker.txt")));
        Assert.False(UpdateInstaller.TakeLastResult(r.State)!.Ok);
    }

    [Theory]
    [InlineData("state-inside-target")]
    [InlineData("unrelated-previous")]
    [InlineData("outside-new-root")]
    [InlineData("unverified-stage")]
    [InlineData("wrong-version")]
    [InlineData("executable-traversal")]
    [InlineData("unowned-previous")]
    public async Task Helper_refuses_unsafe_admission_without_touching_installed_data(string kind)
    {
        var original = HelperFixture("helper-admission");
        var r = kind switch
        {
            "state-inside-target" => original with { State = Path.Combine(original.Target, "state") },
            "unrelated-previous" => original with { Previous = original.Target },
            "outside-new-root" => original with { NewRoot = original.Target },
            "wrong-version" => original with { To = "3" },
            "executable-traversal" => original with { Executable = "../Ombra" },
            _ => original,
        };
        if (kind == "unverified-stage") File.Delete(Path.Combine(r.Staging, UpdateApplyHelper.ReadyMarker));
        if (kind == "unowned-previous")
        {
            Directory.CreateDirectory(r.Previous);
            File.WriteAllText(Path.Combine(r.Previous, "unrelated"), "keep");
        }
        var clock = new RetryClock();
        if (kind is "unverified-stage" or "wrong-version" or "unowned-previous")
        {
            Assert.Equal(1, await UpdateApplyHelper.ApplyAsync(r, clock.Operations()));
            Assert.False(UpdateInstaller.TakeLastResult(original.State)!.Ok);
        }
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateApplyHelper.ApplyAsync(r, clock.Operations()));
        Assert.Equal("old", File.ReadAllText(Path.Combine(original.Target, "marker.txt")));
        Assert.True(Directory.Exists(original.NewRoot));
        Assert.Null(UpdateInstaller.TakeLastResult(original.State));
        if (kind == "unowned-previous") Assert.Equal("keep", File.ReadAllText(Path.Combine(r.Previous, "unrelated")));
    }

    [Fact]
    public async Task Helper_refuses_symlink_cleanup_targets()
    {
        if (OperatingSystem.IsWindows()) return; // creating symlinks requires privileges on Windows
        var r = HelperFixture("helper-symlink");
        var unrelated = Path.Combine(Path.GetDirectoryName(r.Target)!, "unrelated");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(r.Staging, "linked"), unrelated);
        Assert.Equal(1, await UpdateApplyHelper.ApplyAsync(r, new RetryClock().Operations()));
        Assert.False(UpdateInstaller.TakeLastResult(r.State)!.Ok);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated, "keep")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(r.Target, "marker.txt")));
    }

    [Fact]
    public async Task Helper_relaunches_with_exact_arguments_without_shell_interpretation()
    {
        if (OperatingSystem.IsWindows()) return; // actual Unix executable fixture, not Windows-native proof
        var r = HelperFixture("helper-relaunch") with { Relaunch = true, Args = ["--config", "a b/\"quoted\";$HOME.json", ""] };
        File.WriteAllText(Path.Combine(r.NewRoot, r.Executable), "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$0.args\"\n");
        Assert.Equal(0, await UpdateApplyHelper.ApplyAsync(r, new UpdateApplyHelper.Operations
        {
            WaitForParent = (_, _) => Task.FromResult(true),
            Launch = start =>
            {
                using var child = OwnedProcesses.StartIndependent(start)!;
                Assert.True(child.WaitForExit(10_000));
                Assert.Equal(0, child.ExitCode);
            },
        }));
        Assert.Equal(r.Args, File.ReadAllLines(Path.Combine(r.Target, r.Executable + ".args")));
        Assert.True(UpdateInstaller.TakeLastResult(r.State)!.Ok);
    }

    [Fact]
    public async Task Helper_reports_relaunch_failure_without_claiming_a_committed_install_was_rolled_back()
    {
        var r = HelperFixture("helper-relaunch-failure") with { Relaunch = true };
        var launches = 0;
        Assert.Equal(1, await UpdateApplyHelper.ApplyAsync(r, new UpdateApplyHelper.Operations
        {
            WaitForParent = (_, _) => Task.FromResult(true),
            Launch = _ => { launches++; throw new System.ComponentModel.Win32Exception(5, "job breakaway denied"); },
        }));
        Assert.Equal(1, launches);
        Assert.Equal("new", File.ReadAllText(Path.Combine(r.Target, "marker.txt")));
        var result = UpdateInstaller.TakeLastResult(r.State)!;
        Assert.True(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Helper_command_rejects_mixed_options_and_foreign_request_origin()
    {
        Assert.Equal(1, await UpdateApplyHelper.RunAsync(["--version", "--apply-update", "missing", "token"]));
        var r = HelperFixture("helper-origin");
        var request = Path.Combine(r.Helper, "request.json");
        File.WriteAllText(request, JsonSerializer.Serialize(r));
        Assert.Equal(1, await UpdateApplyHelper.RunAsync(["--apply-update", request, "wrong token"]));
        Assert.Equal(1, await UpdateApplyHelper.RunAsync(["--apply-update", request, r.Token])); // testhost is not the copied helper
        Assert.True(File.Exists(request));
        Assert.Equal("old", File.ReadAllText(Path.Combine(r.Target, "marker.txt")));
        Assert.Null(UpdateInstaller.TakeLastResult(r.State));
    }

    [Fact]
    public void Helper_cleanup_requires_completion_and_defers_while_the_helper_owns_its_lease()
    {
        var r = HelperFixture("helper-cleanup");
        var unrelated = Path.Combine(r.State, "apply-helper-unrelated");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep"), "keep");
        UpdateApplyHelper.CleanupCompleted(r.State);
        Assert.True(Directory.Exists(r.Helper)); // not yet completed
        File.WriteAllText(Path.Combine(r.Helper, ".completed"), r.Token);
        using (new FileStream(Path.Combine(r.Helper, ".running"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            UpdateApplyHelper.CleanupCompleted(r.State);
            Assert.True(Directory.Exists(r.Helper));
        }
        UpdateApplyHelper.CleanupCompleted(r.State);
        Assert.False(Directory.Exists(r.Helper));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated, "keep")));
    }

    // ───────────── omp moves with the client ─────────────

    private static void FakePack(string root, string name, string omp, string platform = "linux-x64", DateTimeOffset? at = null)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "bun"));
        File.WriteAllText(Path.Combine(dir, "bun", "bun"), "");
        var cli = Path.Combine(dir, "omp", (name == RuntimePack.Name ? RuntimePack.CliEntry : RuntimePack.HistoricalCliEntry).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        File.WriteAllText(cli, "");
        if (name == RuntimePack.Name)
            File.WriteAllText(Path.Combine(dir, "omp", RuntimePack.HostEntry.Replace('/', Path.DirectorySeparatorChar)), "");
        File.WriteAllText(Path.Combine(dir, "installed.json"), JsonSerializer.Serialize(new
        {
            pack = name, omp, bun = "1.4.0", platform, installedAt = at ?? DateTimeOffset.UtcNow,
            packManifestSha256 = name == RuntimePack.Name ? RuntimePack.ManifestSha256 : null,
            sourceArtifact = name == RuntimePack.Name ? new { identity = RuntimePack.SourceIdentity, sha256 = RuntimePack.SourceSha256 } : null,
        }));
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
        var config = Path.Combine(TestProcesses.TempDir("upd-auto"), "ombra.local.json");
        var store = new ClientSettingsStore(config);
        var vm = new MainViewModel(new SessionController(TestProcesses.Fake("normal")), new AppArgs(), null, store)
        {
            Updates = Checker(new() { [Feed] = m, [Feed + ".sig"] = TestSigning.Sign(m) }),
            UpdateCheckDelay = TimeSpan.FromMilliseconds(50),
        };
        vm.OnWindowOpened();
        await Until(() => vm.UpdateAvailable, "automatic check offered the update");
        Assert.True(vm.ShowUpdatePill);
        Assert.Equal(Loc.F("Update available: {0}", "0.2.0"), vm.UpdatePill);
        Assert.Contains(Loc.F("This version moves omp to {0} (Bun {1}). After the restart it installs by itself (about 550 MB, verified like the first install); until then omp {2} keeps working.", "19.0.0", "\0", "\0").Split('\0')[0], vm.UpdateOmpText);

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
            UpdateInstaller = new UpdateInstaller(root, "Ombra", state),
            UpdateCheckDelay = TimeSpan.FromHours(1),
        };
        vm.OnWindowOpened();
        await Until(() => vm.ShowUpdatePill, "outcome shown");
        Assert.Equal(Loc.F("Updated to {0}", "0.2.0"), vm.UpdatePill);
        Assert.Contains(Loc.F("Updated from {0} to {1}. The version before is kept in {2} until the next update.", "0.1.0", "0.2.0", "\0").Split('\0')[0], vm.UpdateText);
        Assert.Equal(Loc.T("Download and install"), vm.UpdateActionLabel);
        vm.OpenUpdatesCommand.Execute(null);
        await Until(() => !vm.ShowUpdatePill, "outcome dismissed once seen");
        await vm.DisposeAsync();
    }
}
