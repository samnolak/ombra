using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OmpGui.App.Services.Runtime;
using OmpGui.ClientCore;
using OmpGui.Rpc;

namespace OmpGui.Tests;

/// <summary>Installer for the pinned runtime pack, over an in-memory registry and scripted child processes.</summary>
public sealed class RuntimeInstallerTests
{
    internal const string Key = "test-x64";

    /// <summary>An npm-style tarball holding <c>package/bin/bun</c>.</summary>
    internal static byte[] Tarball(string exe = "bin/bun")
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz))
        {
            var readme = new PaxTarEntry(TarEntryType.RegularFile, "package/README.md") { DataStream = new MemoryStream("bun"u8.ToArray()) };
            tar.WriteEntry(readme);
            var bin = new PaxTarEntry(TarEntryType.RegularFile, "package/" + exe)
            {
                DataStream = new MemoryStream(Encoding.ASCII.GetBytes("#!/bin/sh\necho fake bun\n")),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            };
            tar.WriteEntry(bin);
        }
        return ms.ToArray();
    }

    internal static string Integrity(byte[] data) => "sha512-" + Convert.ToBase64String(SHA512.HashData(data));

    internal sealed class Registry(byte[] body, Func<CancellationToken, Task>? beforeBody = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (beforeBody is not null) await beforeBody(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
    }

    /// <summary>Scripted bun: <c>install</c> lays out omp's CLI entry, <c>--version</c> prints <paramref name="version"/>.</summary>
    internal sealed class FakeBun(int installExit = 0, string version = RuntimePack.OmpVersion)
    {
        public List<ProcessStartInfo> Calls { get; } = [];

        public Task<ProcessResult> Run(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add(psi);
            var args = psi.ArgumentList.ToList();
            if (args[0] == "install")
            {
                if (installExit != 0) return Task.FromResult(new ProcessResult(installExit, "resolving…\nerror: IntegrityCheckFailed for \"left-pad\"\n"));
                var cli = Path.Combine(psi.WorkingDirectory, RuntimePack.CliEntry);
                Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
                File.WriteAllText(cli, "// omp");
                Directory.CreateDirectory(psi.Environment["BUN_INSTALL_CACHE_DIR"]!);
                return Task.FromResult(new ProcessResult(0, "installed 412 packages"));
            }
            return Task.FromResult(new ProcessResult(0, version + "\n"));
        }
    }

    internal static RuntimeInstaller Installer(string root, byte[] tarball, FakeBun bun, string? integrity = null, Registry? registry = null) =>
        new(new HttpClient(registry ?? new Registry(tarball)), root, Key, "https://registry.test",
            new Dictionary<string, BunBuild> { [Key] = new(Key, integrity ?? Integrity(tarball), "bin/bun") })
        { Run = bun.Run };

    /// <summary>What an install left in the root, apart from its lock file (kept on purpose: see RuntimeInstaller).</summary>
    private static string[] Entries(string root) =>
        Directory.EnumerateFileSystemEntries(root).Where(p => Path.GetFileName(p) != ".install.lock").ToArray();

    private static string[] Leftovers(string root) =>
        Directory.EnumerateDirectories(root).Select(Path.GetFileName).Where(n => n != RuntimePack.Name).ToArray()!;

    [Fact]
    public async Task Installs_bun_and_omp_from_the_pinned_lockfile_and_reports_the_layout()
    {
        var root = TestProcesses.TempDir("rt");
        var tgz = Tarball();
        var bun = new FakeBun();
        var registry = new Registry(tgz);
        var installer = Installer(root, tgz, bun, registry: registry);
        Assert.Null(installer.FindInstalled());

        var stages = new List<string>();
        var rt = await installer.InstallAsync(new SyncProgress(p => stages.Add(p.Stage)), CancellationToken.None);

        Assert.Equal($"https://registry.test/@oven/bun-{Key}/-/bun-{Key}-{RuntimePack.BunVersion}.tgz", Assert.Single(registry.Requests));
        Assert.Equal(Path.Combine(root, RuntimePack.Name), rt.Directory);
        Assert.Equal("#!/bin/sh\necho fake bun\n", File.ReadAllText(rt.Bun));
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(rt.Bun).HasFlag(UnixFileMode.UserExecute));
        Assert.True(File.Exists(rt.Cli));
        Assert.Equal(rt, installer.FindInstalled());
        Assert.Empty(Leftovers(root));
        Assert.False(File.Exists(Path.Combine(rt.Directory, "bun.tgz")));
        Assert.False(Directory.Exists(Path.Combine(rt.Directory, "cache")));
        Assert.Contains("Installed", stages);

        // Pinned: frozen lockfile, no lifecycle scripts, a private cache, the pack's own files.
        var install = bun.Calls[0];
        Assert.Equal(["install", "--frozen-lockfile", "--ignore-scripts", "--no-progress"], install.ArgumentList.ToArray());
        Assert.StartsWith(root, install.Environment["BUN_INSTALL_CACHE_DIR"]);
        Assert.Equal(File.ReadAllText(RepoFile("runtime/packs/omp-18.8.0-bun-1.4.2/bun.lock")), File.ReadAllText(Path.Combine(rt.Directory, "omp", "bun.lock")));
        // omp is checked in the staging folder, before it replaces anything.
        var check = bun.Calls[1].ArgumentList.ToArray();
        Assert.Equal("--no-install", check[0]);
        Assert.EndsWith(RuntimePack.CliEntry.Replace('/', Path.DirectorySeparatorChar), check[1]);
        Assert.Contains(".staging-", check[1]);
        Assert.Equal("--version", check[2]);
        // Regression (package E2E): running omp wrote Bun's transpiler cache to the user's ~/.bun. Every Bun the
        // installer starts keeps it in the pack folder being built.
        Assert.All(bun.Calls, c => Assert.Equal(InstalledRuntime.TranspilerCacheIn(Path.GetDirectoryName(c.WorkingDirectory)!),
            c.Environment[InstalledRuntime.TranspilerCacheVariable]));
    }

    [Fact]
    public void The_installed_runtime_fills_in_only_an_empty_command()
    {
        var rt = new InstalledRuntime("/r", "/r/bun/bun", "/r/omp/cli.ts", Key);
        var filled = rt.ApplyTo(new OmpRuntimeOptions { Profile = "work" });
        Assert.Equal("/r/bun/bun", filled.Command);
        // The opened project's bunfig.toml and .env never reach Bun (security audit F-1).
        Assert.Equal(["--config=" + Path.Combine("/r", "omp", "bunfig.toml"), "--no-env-file", "--no-install", "/r/omp/cli.ts"], filled.PrefixArgs);
        Assert.Equal("work", filled.Profile);
        // Bun's transpiler cache stays in the pack, not the user's ~/.bun; the user's own settings are kept.
        Assert.Equal(Path.Combine("/r", "transpiler-cache"), filled.Environment[InstalledRuntime.TranspilerCacheVariable]);
        var withEnv = rt.ApplyTo(new OmpRuntimeOptions { Environment = new() { ["HOME"] = "/h", [InstalledRuntime.TranspilerCacheVariable] = "/mine" } });
        Assert.Equal("/h", withEnv.Environment["HOME"]);
        Assert.Equal("/mine", withEnv.Environment[InstalledRuntime.TranspilerCacheVariable]);
        var own = new OmpRuntimeOptions { Command = "/usr/local/bin/omp" };
        Assert.Same(own, rt.ApplyTo(own));
        var prefixOnly = new OmpRuntimeOptions { PrefixArgs = ["x.ts"] };
        Assert.Same(prefixOnly, rt.ApplyTo(prefixOnly));
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_integrity_installs_nothing()
    {
        var root = TestProcesses.TempDir("rt-bad");
        var bun = new FakeBun();
        var installer = Installer(root, Tarball(), bun, integrity: Integrity(Tarball("bin/other")));
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(null, CancellationToken.None));
        Assert.Contains("integrity", e.Message);
        Assert.Empty(bun.Calls); // nothing from the unverified tarball was run
        Assert.Empty(Entries(root));
    }

    [Fact]
    public async Task A_failed_bun_install_reports_its_output_and_keeps_the_previous_install()
    {
        var root = TestProcesses.TempDir("rt-fail");
        var tgz = Tarball();
        var before = await Installer(root, tgz, new FakeBun()).InstallAsync(null, CancellationToken.None);
        var e = await Assert.ThrowsAsync<IOException>(() => Installer(root, tgz, new FakeBun(installExit: 1)).InstallAsync(null, CancellationToken.None));
        Assert.Contains("IntegrityCheckFailed", e.Message);
        Assert.Equal(before, Installer(root, tgz, new FakeBun()).FindInstalled());
        Assert.Empty(Leftovers(root));
    }

    [Fact]
    public async Task Omp_reporting_another_version_is_not_installed()
    {
        var root = TestProcesses.TempDir("rt-ver");
        var installer = Installer(root, Tarball(), new FakeBun(version: "18.3.0"));
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(null, CancellationToken.None));
        Assert.Contains(RuntimePack.OmpVersion, e.Message);
        Assert.Null(installer.FindInstalled());
        Assert.Empty(Entries(root));
    }

    [Fact]
    public async Task Cancelling_the_download_leaves_nothing()
    {
        var root = TestProcesses.TempDir("rt-cancel");
        using var cts = new CancellationTokenSource();
        var tgz = Tarball();
        var registry = new Registry(tgz, async ct => { await cts.CancelAsync(); ct.ThrowIfCancellationRequested(); });
        var installer = Installer(root, tgz, new FakeBun(), registry: registry);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(null, cts.Token));
        Assert.Empty(Entries(root));
    }

    [Fact]
    public async Task Reinstalling_replaces_the_install_and_cleans_up_stale_attempts()
    {
        var root = TestProcesses.TempDir("rt-re");
        var tgz = Tarball();
        await Installer(root, tgz, new FakeBun()).InstallAsync(null, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(root, ".staging-deadbeef", "x"));
        File.WriteAllText(Path.Combine(root, ".staging-deadbeef", ".ompgui-runtime-work"), "");   // a leftover of ours
        Directory.CreateDirectory(Path.Combine(root, "user-folder"));                             // not the installer's: kept
        Directory.CreateDirectory(Path.Combine(root, ".old-config"));                             // same prefix, no marker: kept
        var marker = Path.Combine(root, RuntimePack.Name, "installed.json");
        var first = File.ReadAllText(marker);
        await Task.Delay(20);
        var rt = await Installer(root, tgz, new FakeBun()).InstallAsync(null, CancellationToken.None);
        Assert.NotEqual(first, File.ReadAllText(marker));
        Assert.Equal([".old-config", "user-folder"], Leftovers(root).Order());
        Assert.True(File.Exists(rt.Bun));
    }

    [Fact]
    public async Task A_second_install_at_the_same_time_is_refused_and_disturbs_nothing()
    {
        // Regression (review round 4): two installs deleted each other's staging and the swap's backup.
        var root = TestProcesses.TempDir("rt-lock");
        using (new FileStream(Path.Combine(root, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var bun = new FakeBun();
            var e = await Assert.ThrowsAsync<IOException>(() => Installer(root, Tarball(), bun).InstallAsync(null, CancellationToken.None));
            Assert.Contains("Another installation", e.Message);
            Assert.Empty(bun.Calls);
        }
        Assert.NotNull(await Installer(root, Tarball(), new FakeBun()).InstallAsync(null, CancellationToken.None)); // free again
    }

    [Fact]
    public void An_incomplete_or_foreign_folder_is_not_an_install()
    {
        var root = TestProcesses.TempDir("rt-inc");
        var installer = Installer(root, Tarball(), new FakeBun());
        Directory.CreateDirectory(Path.Combine(root, RuntimePack.Name, "bun"));
        Assert.Null(installer.FindInstalled());
        File.WriteAllText(Path.Combine(root, RuntimePack.Name, "installed.json"), "{\"pack\":\"omp-1.0.0\",\"platform\":\"test-x64\"}");
        Assert.Null(installer.FindInstalled());
        File.WriteAllText(Path.Combine(root, RuntimePack.Name, "installed.json"), "not json");
        Assert.Null(installer.FindInstalled());
    }

    [Fact]
    public async Task No_pinned_build_for_the_platform_is_reported_not_guessed()
    {
        var installer = new RuntimeInstaller(new HttpClient(new Registry([])), TestProcesses.TempDir("rt-none"), "linux-riscv64");
        Assert.False(installer.Supported);
        await Assert.ThrowsAsync<NotSupportedException>(() => installer.InstallAsync(null, CancellationToken.None));
        Assert.False(new RuntimeInstaller(new HttpClient(), "/x", null).Supported);
    }

    [Fact]
    public void Every_supported_platform_has_a_sha512_pin_and_the_pack_files_are_built_in()
    {
        Assert.Equal(9, RuntimePack.Bun.Count);
        Assert.All(RuntimePack.Bun.Values, b =>
        {
            Assert.StartsWith("sha512-", b.Integrity);
            Assert.Equal(64, Convert.FromBase64String(b.Integrity["sha512-".Length..]).Length);
            Assert.Equal(b.Key.StartsWith("windows", StringComparison.Ordinal) ? "bin/bun.exe" : "bin/bun", b.Exe);
        });
        foreach (var name in RuntimePack.PackFiles)
        {
            using var reader = new StreamReader(RuntimePack.OpenPackFile(name));
            Assert.Equal(File.ReadAllText(RepoFile("runtime/packs/omp-18.8.0-bun-1.4.2/" + name)), reader.ReadToEnd());
        }
        Assert.Contains("\"@oh-my-pi/pi-coding-agent\": \"18.8.0\"", File.ReadAllText(RepoFile("runtime/packs/omp-18.8.0-bun-1.4.2/package.json")));
    }

    [Fact]
    public void This_machine_maps_to_a_pinned_build()
    {
        var key = OmpGui.App.Platform.RuntimePlatform.Key(out var description);
        Assert.NotNull(key); // every CI runner (Linux x64, macOS arm64, Windows x64) has one
        Assert.True(RuntimePack.Bun.ContainsKey(key!), $"{key} ({description})");
    }

    [Fact]
    public void Start_failures_are_classified()
    {
        Assert.Equal(StartProblem.NotFound, SessionController.ClassifyStartFailure(new OmpStartException("could not start omp", null, "", launchFailed: true)));
        Assert.Equal(StartProblem.NoModel, SessionController.ClassifyStartFailure(new OmpStartException("exited 1", 1, "No models available. Use /login or set an API key environment variable.")));
        // omp 18.8.0's wording (seen with an empty HOME and no API key in the environment)
        Assert.Equal(StartProblem.NoModel, SessionController.ClassifyStartFailure(new OmpStartException("exited 1", 1,
            "No default model selected. Use /login, set an API key environment variable, or select a local model with /model or --model.")));
        Assert.Equal(StartProblem.Other, SessionController.ClassifyStartFailure(new OmpStartException("exited 3", 3, "fatal: something else")));
    }

    [Fact]
    public async Task Missing_omp_and_omp_without_a_model_are_told_apart()
    {
        await using (var missing = new SessionController(new OmpLaunchSpec { FileName = Path.Combine(TestProcesses.TempDir("none"), "omp-not-here") }))
        {
            await Assert.ThrowsAsync<OmpStartException>(() => missing.StartAsync());
            Assert.Equal(StartProblem.NotFound, missing.Snapshot().StartProblem);
        }
        await using (var noModel = new SessionController(TestProcesses.Fake("no-models")))
        {
            await Assert.ThrowsAsync<OmpStartException>(() => noModel.StartAsync());
            var snap = noModel.Snapshot();
            Assert.Equal(StartProblem.NoModel, snap.StartProblem);
            Assert.Contains("/login", snap.LastError);
        }
        await using var other = new SessionController(TestProcesses.Fake("crash-before-ready"));
        await Assert.ThrowsAsync<OmpStartException>(() => other.StartAsync());
        Assert.Equal(StartProblem.Other, other.Snapshot().StartProblem);
    }

    internal static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }

    /// <summary>Progress that reports on the calling thread (Progress&lt;T&gt; posts, which races the assertions).</summary>
    internal sealed class SyncProgress(Action<RuntimeInstallProgress> report) : IProgress<RuntimeInstallProgress>
    {
        public void Report(RuntimeInstallProgress value) => report(value);
    }
}
