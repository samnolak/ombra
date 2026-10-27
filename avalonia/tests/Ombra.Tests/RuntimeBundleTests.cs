using System.Text.Json.Nodes;
using Ombra.App.Services.Runtime;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>
/// The engine pack built into the app package (tools/package/package.sh): used in place when it matches this client's
/// pin and this machine's platform, never downloaded again; the network installer only for a repair or a mismatch.
/// </summary>
public sealed class RuntimeBundleTests
{
    private const string Key = RuntimeInstallerTests.Key;
    private const string OtherKey = "test-arm64";

    private static Dictionary<string, BunBuild> Builds(byte[] tgz) => new()
    {
        [Key] = new(Key, RuntimeInstallerTests.Integrity(tgz), "bin/bun"),
        [OtherKey] = new(OtherKey, RuntimeInstallerTests.Integrity(tgz), "bin/bun"),
    };

    /// <summary>Fails every request: the first start of a package must not reach the network.</summary>
    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            throw new HttpRequestException("network blocked in this test");
        }
    }

    /// <summary>A pack the way package.sh puts it into the app: installed by the client's own installer at package time,
    /// then moved into the app's engine folder.</summary>
    private static async Task<string> BundledRoot(byte[] tgz, string platform = Key)
    {
        var build = TestProcesses.TempDir("rt-bundle-build");
        var installer = new RuntimeInstaller(new HttpClient(new RuntimeInstallerTests.Registry(tgz)), build, platform, "https://registry.test", Builds(tgz))
        {
            Run = new RuntimeInstallerTests.FakeBun().Run,
        };
        await installer.InstallAsync(null, CancellationToken.None);
        var bundled = TestProcesses.TempDir("rt-bundle-app"); // stands in for Ombra.app/Contents/Resources/engine
        Directory.Move(Path.Combine(build, RuntimePack.Name), Path.Combine(bundled, RuntimePack.Name));
        return bundled;
    }

    private static RuntimeInstaller Installer(string root, byte[] tgz, string? bundled, HttpMessageHandler network, RuntimeInstallerTests.FakeBun? bun = null) =>
        new(new HttpClient(network), root, Key, "https://registry.test", Builds(tgz))
        {
            Run = (bun ?? new RuntimeInstallerTests.FakeBun()).Run,
            BundledRoot = bundled,
        };

    [Fact]
    public async Task A_bundled_pack_for_this_pin_and_platform_runs_in_place_without_a_download()
    {
        var tgz = RuntimeInstallerTests.Tarball();
        var bundled = await BundledRoot(tgz);
        var root = TestProcesses.TempDir("rt-bundle-user");
        var network = new NoNetwork();
        var bun = new RuntimeInstallerTests.FakeBun();
        var installer = Installer(root, tgz, bundled, network, bun);

        var rt = installer.FindInstalled();

        Assert.NotNull(rt);
        Assert.True(rt.Bundled);
        Assert.Equal(Path.Combine(bundled, RuntimePack.Name), rt.Directory);
        Assert.StartsWith(bundled, rt.Bun);
        // Read-only in place: Bun's transpiler cache goes to the user's runtimes folder, never into the app.
        Assert.Equal(Path.Combine(root, ".transpiler-cache", RuntimePack.Name), rt.TranspilerCache);
        var options = installer.ApplyDefaultTo(new OmpRuntimeOptions());
        Assert.Equal(rt.Bun, options.Command);
        Assert.Equal(rt.Host, options.PrefixArgs[^1]);
        Assert.Equal(rt.TranspilerCache, options.Environment[InstalledRuntime.TranspilerCacheVariable]);
        Assert.False(Directory.Exists(installer.TargetDirectory));
        Assert.Equal(0, network.Requests);
        Assert.Empty(bun.Calls);
    }

    [Fact]
    public async Task A_bundled_pack_for_another_platform_falls_back_to_the_network_installer()
    {
        var tgz = RuntimeInstallerTests.Tarball();
        var bundled = await BundledRoot(tgz, OtherKey); // e.g. the osx-x64 package's x64 pack on an arm64 Mac
        var root = TestProcesses.TempDir("rt-bundle-mismatch");
        var registry = new RuntimeInstallerTests.Registry(tgz);
        var installer = Installer(root, tgz, bundled, registry);

        Assert.Null(installer.FindBundled());
        Assert.Null(installer.FindInstalled());
        Assert.Equal(Path.Combine(installer.TargetDirectory, "install-required"), installer.ApplyDefaultTo(new OmpRuntimeOptions()).Command);

        var installed = await installer.InstallAsync(null, CancellationToken.None);

        Assert.Single(registry.Requests);
        var rt = installer.FindInstalled();
        Assert.Equal(installed, rt);
        Assert.False(rt!.Bundled);
        Assert.Equal(InstalledRuntime.TranspilerCacheIn(rt.Directory), rt.TranspilerCache);
        // No valid bundled pack was present: this install does not claim to override one.
        Assert.False(MarkerOf(rt.Directory)["overridesBundled"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("packManifestSha256")]
    [InlineData("pack")]
    [InlineData("sourceArtifact")]
    [InlineData("host")]
    [InlineData("harness")]
    public async Task A_bundled_pack_of_another_pin_or_incomplete_is_not_used(string damage)
    {
        var tgz = RuntimeInstallerTests.Tarball();
        var bundled = await BundledRoot(tgz);
        var dir = Path.Combine(bundled, RuntimePack.Name);
        var installer = Installer(TestProcesses.TempDir("rt-bundle-damaged"), tgz, bundled, new NoNetwork());
        Assert.NotNull(installer.FindBundled());

        var marker = MarkerOf(dir);
        switch (damage)
        {
            case "packManifestSha256": marker["packManifestSha256"] = new string('0', 64); break;
            case "pack": marker["pack"] = "ombra-engine-0.0.0-bun-0.0.0-v0"; break;
            case "sourceArtifact": marker["sourceArtifact"]!["sha256"] = new string('0', 64); break;
        }
        if (damage == "host") File.Delete(Path.Combine(dir, "omp", RuntimePack.HostEntry));
        else if (damage == "harness") Directory.Delete(Path.Combine(dir, "omp", "harness"), recursive: true);
        else File.WriteAllText(Path.Combine(dir, "installed.json"), marker.ToJsonString());

        Assert.Null(installer.FindBundled());
        Assert.Null(installer.FindInstalled());
    }

    [Fact]
    public async Task An_installed_pack_points_engine_and_terminal_launches_at_its_harness_and_is_reinstalled_without_it()
    {
        var tgz = RuntimeInstallerTests.Tarball();
        var root = TestProcesses.TempDir("rt-harness");
        var installer = Installer(root, tgz, null, new RuntimeInstallerTests.Registry(tgz));
        var installed = await installer.InstallAsync(null, CancellationToken.None);
        var rt = installer.FindInstalled()!;
        Assert.Equal(Path.Combine(installed.Directory, "omp", "harness"), rt.Harness);
        Assert.Equal(File.ReadAllBytes(RuntimeInstallerTests.RepoFile("harness/profile/agent/AGENTS.md")),
            File.ReadAllBytes(Path.Combine(rt.Harness, "agent", "AGENTS.md")));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(rt.Harness, "*", SearchOption.AllDirectories),
            p => p.Contains("__pycache__") || p.EndsWith(".pyc") || p.EndsWith(".seed") || p.EndsWith(".DS_Store"));

        // The chat engine, the terminal omp and CLI management all get the pack's harness
        var options = installer.ApplyDefaultTo(new OmpRuntimeOptions());
        Assert.Equal(rt.Harness, options.ToLaunchSpec().Environment[InstalledRuntime.HarnessVariable]);
        Assert.Equal(rt.Harness, options.ToTuiLaunchSpec(root).Environment[InstalledRuntime.HarnessVariable]);
        Assert.Equal(rt.Harness, options.ToCliLaunchSpec(["--version"]).Environment[InstalledRuntime.HarnessVariable]);
        // A harness the user set in the runtime environment (a developer's harness/profile) wins
        var dev = installer.ApplyDefaultTo(new OmpRuntimeOptions { Environment = new() { [InstalledRuntime.HarnessVariable] = "/repo/harness/profile" } });
        Assert.Equal("/repo/harness/profile", dev.ToLaunchSpec().Environment[InstalledRuntime.HarnessVariable]);
        Assert.Equal("/repo/harness/profile", dev.ToTuiLaunchSpec(root).Environment[InstalledRuntime.HarnessVariable]);

        // Without its harness the pack is not run; the install path replaces it
        Directory.Delete(rt.Harness, recursive: true);
        Assert.Null(installer.FindInstalled());
        Assert.Equal(Path.Combine(installer.TargetDirectory, "install-required"), installer.ApplyDefaultTo(new OmpRuntimeOptions()).Command);
        var repaired = await installer.InstallAsync(null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(repaired.Harness, "agent", "AGENTS.md")));
        Assert.Equal(repaired, installer.FindInstalled());
    }

    [Fact]
    public async Task The_bundled_pack_wins_over_an_earlier_download_and_a_repair_wins_over_the_bundled_pack()
    {
        var tgz = RuntimeInstallerTests.Tarball();
        var bundled = await BundledRoot(tgz);
        var root = TestProcesses.TempDir("rt-bundle-precedence");
        // Downloaded by a build without a bundled pack (an earlier version, or one run from source)
        var legacy = await Installer(root, tgz, null, new RuntimeInstallerTests.Registry(tgz)).InstallAsync(null, CancellationToken.None);
        Assert.False(MarkerOf(legacy.Directory)["overridesBundled"]!.GetValue<bool>());

        var withoutBundle = Installer(root, tgz, TestProcesses.TempDir("rt-bundle-none"), new NoNetwork());
        Assert.Equal(legacy, withoutBundle.FindInstalled());

        var installer = Installer(root, tgz, bundled, new RuntimeInstallerTests.Registry(tgz));
        Assert.True(installer.FindInstalled()!.Bundled);

        // Repair engine: the network installer, whose pack then runs instead of the bundled one
        var repaired = await installer.InstallAsync(null, CancellationToken.None);
        Assert.True(MarkerOf(repaired.Directory)["overridesBundled"]!.GetValue<bool>());
        var rt = installer.FindInstalled()!;
        Assert.False(rt.Bundled);
        Assert.Equal(installer.TargetDirectory, rt.Directory);
        Assert.True(Directory.Exists(Path.Combine(bundled, RuntimePack.Name)), "the app's own pack is never changed");
    }

    private static JsonObject MarkerOf(string pack) => JsonNode.Parse(File.ReadAllText(Path.Combine(pack, "installed.json")))!.AsObject();

    private static string Put(string modules, string relative, string text = "x")
    {
        var path = Path.Combine(modules, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Prune_removes_only_what_the_platform_never_loads()
    {
        var omp = TestProcesses.TempDir("rt-prune");
        var nm = Path.Combine(omp, "node_modules");
        foreach (var d in new[] { "darwin/arm64", "darwin/x64", "linux/x64", "linux/arm64", "win32/x64", "win32/arm64" })
            Put(nm, $"onnxruntime-node/bin/napi-v6/{d}/onnxruntime_binding.node");
        var linked = Put(nm, "onnxruntime-node/bin/napi-v6/darwin/arm64/libonnxruntime.1.dylib", "ort 1.30.0");
        var copy = Put(nm, "onnxruntime-node/bin/napi-v6/darwin/arm64/libonnxruntime.1.30.0.dylib", "ort 1.30.0");
        var binding = Put(nm, "onnxruntime-node/dist/binding.js");
        foreach (var p in RuntimePrune.Packages) Put(nm, p + "/index.js");
        var transformers = Put(nm, "@huggingface/transformers/dist/transformers.node.mjs");
        var map = Put(nm, "@huggingface/transformers/dist/transformers.node.mjs.map");
        var types = Put(nm, "@types/node/index.d.ts");
        var ownTypes = Put(nm, "@oh-my-pi/pi-coding-agent/src/tools/browser/declarations.d.ts");
        var source = Put(nm, "@oh-my-pi/omp-stats/src/client/App.tsx");
        var dashboard = Put(nm, "@oh-my-pi/omp-stats/dist/client/index.html");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(1)); // a source newer than the prebuilt dashboard

        var removed = RuntimePrune.Prune(omp, "darwin-aarch64", CancellationToken.None);

        Assert.True(removed > 0);
        Assert.True(File.Exists(Path.Combine(nm, "onnxruntime-node", "bin", "napi-v6", "darwin", "arm64", "onnxruntime_binding.node")));
        Assert.True(File.Exists(linked));
        Assert.False(File.Exists(copy));
        Assert.Equal(["arm64"], Directory.EnumerateDirectories(Path.Combine(nm, "onnxruntime-node", "bin", "napi-v6", "darwin")).Select(Path.GetFileName));
        Assert.Equal(["darwin"], Directory.EnumerateDirectories(Path.Combine(nm, "onnxruntime-node", "bin", "napi-v6")).Select(Path.GetFileName));
        Assert.True(File.Exists(binding));
        Assert.All(RuntimePrune.Packages, p => Assert.False(Directory.Exists(Path.Combine(nm, p))));
        Assert.True(File.Exists(transformers));
        Assert.False(File.Exists(map));
        Assert.False(File.Exists(types));
        Assert.True(File.Exists(ownTypes)); // imported as text by the engine's tool preludes
        Assert.True(File.Exists(source));
        Assert.True(File.GetLastWriteTimeUtc(dashboard) > File.GetLastWriteTimeUtc(source), "the prebuilt dashboard must be the newer");
    }

    [Fact]
    public void Prune_keeps_a_native_library_that_differs_from_the_linked_one()
    {
        var omp = TestProcesses.TempDir("rt-prune-distinct");
        var nm = Path.Combine(omp, "node_modules");
        Put(nm, "onnxruntime-node/bin/napi-v6/darwin/arm64/libonnxruntime.1.dylib", "ort 1.30.0");
        var other = Put(nm, "onnxruntime-node/bin/napi-v6/darwin/arm64/libonnxruntime.1.30.0.dylib", "ort 1.30.1");

        RuntimePrune.Prune(omp, "darwin-aarch64", CancellationToken.None);

        Assert.True(File.Exists(other));
    }

    [Theory]
    [InlineData("darwin-aarch64", "darwin", "arm64")]
    [InlineData("darwin-x64-baseline", "darwin", "x64")]
    [InlineData("linux-aarch64", "linux", "arm64")]
    [InlineData("linux-x64", "linux", "x64")]
    [InlineData("windows-aarch64", "win32", "arm64")]
    [InlineData("windows-x64", "win32", "x64")]
    public void Prune_keeps_the_binding_node_loads_for_the_platform(string platform, string os, string arch)
    {
        Assert.Equal((os, arch), RuntimePrune.NodeTarget(platform));
        var omp = TestProcesses.TempDir("rt-prune-platform");
        var nm = Path.Combine(omp, "node_modules");
        foreach (var target in new[] { "darwin/arm64", "darwin/x64", "linux/x64", "linux/arm64", "win32/x64", "win32/arm64" })
            Put(nm, $"onnxruntime-node/bin/napi-v6/{target}/onnxruntime_binding.node");

        RuntimePrune.Prune(omp, platform, CancellationToken.None);

        var bindingRoot = Path.Combine(nm, "onnxruntime-node", "bin", "napi-v6");
        Assert.Equal([os], Directory.EnumerateDirectories(bindingRoot).Select(Path.GetFileName));
        Assert.Equal([arch], Directory.EnumerateDirectories(Path.Combine(bindingRoot, os)).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(bindingRoot, os, arch, "onnxruntime_binding.node")));
    }
}
