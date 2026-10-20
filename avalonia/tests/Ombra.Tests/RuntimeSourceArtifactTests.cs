using System.Security.Cryptography;
using System.Text.Json;
using Ombra.App.Services.Runtime;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class RuntimeSourceArtifactTests
{
    private static string Stage()
    {
        var staging = Path.Combine(TestProcesses.TempDir("engine-source"), ".staging-test");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, ".ombra-runtime-work"), "test installer staging");
        var upstream = Path.Combine(staging, "omp", RuntimePack.HistoricalCliEntry);
        Directory.CreateDirectory(Path.GetDirectoryName(upstream)!);
        File.WriteAllText(upstream, "unmodified dependency source");
        return staging;
    }

    private static Dictionary<string, byte[]> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);

    private static void AssertUnchanged(string directory, Dictionary<string, byte[]> before)
    {
        Assert.Equal(before.Keys.Order(), Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Complete_canonical_engine_matches_manifest_and_installs_beside_untouched_dependencies()
    {
        var staging = Stage();
        using var manifest = RuntimeSourceArtifact.ReadManifest(RuntimePack.OpenPackFile);
        Assert.Equal(RuntimePack.OmpIntegrity, manifest.RootElement.GetProperty("omp").GetProperty("integrity").GetString());
        Assert.Equal(RuntimePack.Name, manifest.RootElement.GetProperty("name").GetString());
        Assert.Equal(RuntimePack.OmpVersion, manifest.RootElement.GetProperty("omp").GetProperty("version").GetString());
        Assert.Equal(RuntimePack.BunVersion, manifest.RootElement.GetProperty("bun").GetProperty("version").GetString());
        Assert.Equal(RuntimePack.HostEntry, manifest.RootElement.GetProperty("engine").GetProperty("entry").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("engine").GetProperty("protocolVersion").GetInt32());
        var artifact = await RuntimeSourceArtifact.ApplyAsync(staging, CancellationToken.None);
        Assert.Equal(RuntimePack.SourceIdentity, artifact.GetProperty("identity").GetString());
        Assert.Equal(RuntimePack.SourceSha256, artifact.GetProperty("sha256").GetString());
        Assert.Equal("complete-owned-source", artifact.GetProperty("kind").GetString());
        var canonical = Path.GetDirectoryName(RuntimeInstallerTests.RepoFile("runtime/engine/host.ts"))!;
        var paths = artifact.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!).ToArray();
        Assert.Equal(Directory.EnumerateFiles(canonical, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(canonical, p).Replace('\\', '/'))
            .Where(p => p.Split('/')[0] is not (".temp" or "patches" or "patcher.py" or "upstream.json" or "README.md"
                or "check-upstream.py" or "upstream-fixtures.patch" or "upstream-expected-failures.json"))
            .Order(), paths.Order());
        Assert.Contains("host.ts", paths);
        Assert.Contains("session-factory.ts", paths);
        Assert.Contains("coding-agent/LICENSE", paths);
        Assert.Contains("coding-agent/THIRD-PARTY-NOTICES.txt", paths);
        Assert.True(paths.Length > 1700);
        Assert.DoesNotContain(paths, p => p.StartsWith("patches/", StringComparison.Ordinal)
            || p is "patcher.py" or "upstream.json" or "README.md"
                or "check-upstream.py" or "upstream-fixtures.patch" or "upstream-expected-failures.json"
            || p.StartsWith(".temp/", StringComparison.Ordinal));
        foreach (var file in artifact.GetProperty("files").EnumerateArray())
        {
            var relative = file.GetProperty("path").GetString()!;
            var installed = File.ReadAllBytes(Path.Combine(staging, "omp", "engine", relative));
            Assert.Equal(File.ReadAllBytes(Path.Combine(canonical, relative)), installed);
            Assert.Equal(file.GetProperty("size").GetInt64(), installed.LongLength);
            Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(installed)).ToLowerInvariant());
        }
        Assert.Equal("unmodified dependency source", File.ReadAllText(Path.Combine(staging, "omp", RuntimePack.HistoricalCliEntry)));
    }

    [Theory]
    [InlineData("pack.json")]
    [InlineData("engine/host.ts")]
    [InlineData("engine/coding-agent/src/modes/rpc/rpc-messages-backward.ts")]
    public async Task Corrupt_embedded_manifest_or_source_fails_before_creating_the_engine(string resource)
    {
        var staging = Stage();
        var before = Snapshot(staging);
        Stream Open(string name)
        {
            var source = RuntimePack.OpenPackFile(name);
            if (name != resource) return source;
            using (source)
            using (var buffer = new MemoryStream())
            {
                source.CopyTo(buffer);
                var bytes = buffer.ToArray();
                bytes[0] ^= 1;
                return new MemoryStream(bytes);
            }
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeSourceArtifact.ApplyAsync(staging, CancellationToken.None, Open));
        Assert.Contains("SHA256 integrity", error.Message);
        AssertUnchanged(staging, before);
        Assert.False(Directory.Exists(Path.Combine(staging, "omp", "engine")));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Truncated_or_oversized_source_fails_before_writing_files(int difference)
    {
        var staging = Stage();
        var before = Snapshot(staging);
        Stream Open(string name)
        {
            var source = RuntimePack.OpenPackFile(name);
            if (name != "engine/host.ts") return source;
            using (source)
            using (var buffer = new MemoryStream())
            {
                source.CopyTo(buffer);
                var bytes = buffer.ToArray();
                Array.Resize(ref bytes, bytes.Length + difference);
                return new MemoryStream(bytes);
            }
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeSourceArtifact.ApplyAsync(staging, CancellationToken.None, Open));
        Assert.Contains("size mismatch", error.Message);
        AssertUnchanged(staging, before);
    }

    [Fact]
    public void Manifest_reading_is_bounded_before_parsing()
    {
        using var oversized = new MemoryStream(new byte[(4 << 20) + 1]);
        var error = Assert.Throws<InvalidDataException>(() => RuntimeSourceArtifact.ReadManifest(_ => oversized));
        Assert.Contains("size limit", error.Message);
    }

    [Fact]
    public async Task Existing_owned_tree_is_not_overwritten_and_installed_directories_are_never_patched()
    {
        var staging = Stage();
        Directory.CreateDirectory(Path.Combine(staging, "omp", "engine"));
        File.WriteAllText(Path.Combine(staging, "omp", "engine", "host.ts"), "unexpected source");
        var before = Snapshot(staging);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeSourceArtifact.ApplyAsync(staging, CancellationToken.None));
        AssertUnchanged(staging, before);
        var installed = Path.Combine(Path.GetDirectoryName(staging)!, RuntimePack.Name);
        Directory.Move(staging, installed);
        before = Snapshot(installed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeSourceArtifact.ApplyAsync(installed, CancellationToken.None));
        AssertUnchanged(installed, before);
    }

    [Fact]
    public async Task Source_collision_leaves_completed_install_unchanged_and_never_runs_unverified_host()
    {
        var root = TestProcesses.TempDir("engine-install-failure");
        var tarball = RuntimeInstallerTests.Tarball();
        var installer = RuntimeInstallerTests.Installer(root, tarball, new RuntimeInstallerTests.FakeBun());
        var installed = await installer.InstallAsync(null, CancellationToken.None);
        var before = Snapshot(installed.Directory);
        var badBun = new RuntimeInstallerTests.FakeBun(afterInstall: omp => Directory.CreateDirectory(Path.Combine(omp, "engine")));
        var failing = RuntimeInstallerTests.Installer(root, tarball, badBun);
        await Assert.ThrowsAsync<InvalidDataException>(() => failing.InstallAsync(null, CancellationToken.None));
        Assert.Single(badBun.Calls);
        Assert.Equal(installed, installer.FindInstalled());
        AssertUnchanged(installed.Directory, before);
        Assert.Equal([RuntimePack.Name], Directory.EnumerateDirectories(root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Cancellation_during_source_validation_leaves_staging_dependencies_untouched()
    {
        var staging = Stage();
        var before = Snapshot(staging);
        using var cancel = new CancellationTokenSource();
        Stream Open(string name)
        {
            if (name == "engine/host.ts") cancel.Cancel();
            return RuntimePack.OpenPackFile(name);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeSourceArtifact.ApplyAsync(staging, cancel.Token, Open));
        AssertUnchanged(staging, before);
    }

    [Theory]
    [InlineData("omp-18.8.0-bun-1.4.2")]
    [InlineData("omp-18.8.0-bun-1.4.2-ombra-history2")]
    [InlineData("ombra-engine-18.8.0-bun-1.4.2-v1")]
    public async Task Historical_pack_is_preserved_but_never_selected_as_default(string previousName)
    {
        var root = TestProcesses.TempDir("engine-previous");
        var previous = Path.Combine(root, previousName);
        Directory.CreateDirectory(Path.Combine(previous, "bun"));
        File.WriteAllText(Path.Combine(previous, "bun", "bun"), "old bun");
        var cli = Path.Combine(previous, "omp", RuntimePack.HistoricalCliEntry);
        Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
        File.WriteAllText(cli, "old omp");
        File.WriteAllText(Path.Combine(previous, "installed.json"), JsonSerializer.Serialize(new
        {
            pack = previousName, omp = RuntimePack.OmpVersion, platform = RuntimeInstallerTests.Key,
        }));
        var before = Snapshot(previous);
        var installer = RuntimeInstallerTests.Installer(root, RuntimeInstallerTests.Tarball(), new RuntimeInstallerTests.FakeBun());
        Assert.NotNull(installer.FindPrevious());
        Assert.Null(installer.FindInstalled());
        var missing = installer.ApplyDefaultTo(new OmpRuntimeOptions());
        Assert.StartsWith(installer.TargetDirectory, missing.Command);
        Assert.False(File.Exists(missing.Command));
        Assert.NotEqual("omp", missing.Command);
        var custom = new OmpRuntimeOptions { Command = "my-engine" };
        Assert.Same(custom, installer.ApplyDefaultTo(custom));
        var installed = await installer.InstallAsync(null, CancellationToken.None);
        Assert.Equal(installed.Host, installer.ApplyDefaultTo(new OmpRuntimeOptions()).PrefixArgs.Last());
        AssertUnchanged(previous, before);
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(installed.Directory, "installed.json")));
        Assert.Equal(RuntimePack.OmpIntegrity, marker.RootElement.GetProperty("ompIntegrity").GetString());
        Assert.Equal(RuntimePack.ManifestSha256, marker.RootElement.GetProperty("packManifestSha256").GetString());
        Assert.Equal(RuntimePack.SourceSha256, marker.RootElement.GetProperty("sourceArtifact").GetProperty("sha256").GetString());
        Assert.True(marker.RootElement.GetProperty("sourceArtifact").GetProperty("files").GetArrayLength() > 1700);
        File.Delete(installed.Host);
        Assert.Null(installer.FindInstalled());
    }
}
