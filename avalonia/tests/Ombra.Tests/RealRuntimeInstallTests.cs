using Ombra.App.Services.Runtime;

namespace Ombra.Tests;

/// <summary>
/// The real installed owned host with integrity-pinned Bun/npm dependencies, plus real backwards/forward pager tests.
/// Runs when OMBRA_RUNTIME_INSTALL_DIR names the install root (CI, all OSes).
/// </summary>
public sealed class RealRuntimeInstallTests
{
    [Fact]
    public async Task Installs_the_complete_owned_engine_and_imports_real_host_dependencies()
    {
        var root = Environment.GetEnvironmentVariable("OMBRA_RUNTIME_INSTALL_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "OMBRA_RUNTIME_INSTALL_DIR not set");
        var platform = Ombra.App.Platform.RuntimePlatform.Key(out var description);
        var installer = new RuntimeInstaller(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, root!, platform);
        Assert.True(installer.Supported, $"no pinned build for {description}");
        var started = DateTime.UtcNow;
        var rt = installer.FindInstalled() ?? await installer.InstallAsync(null, TestContext.Current.CancellationToken);
        var version = new System.Diagnostics.ProcessStartInfo(rt.Bun) { WorkingDirectory = rt.Directory };
        foreach (var a in new[] { "--config=" + rt.BunConfig, "--no-env-file", "--no-install", rt.Host, "--self-check" })
            version.ArgumentList.Add(a);
        version.Environment[InstalledRuntime.TranspilerCacheVariable] = InstalledRuntime.TranspilerCacheIn(rt.Directory);
        var result = await RuntimeInstaller.RunProcessAsync(version, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        using var check = System.Text.Json.JsonDocument.Parse(result.Output.Trim());
        Assert.Equal(1, check.RootElement.GetProperty("engineProtocolVersion").GetInt32());
        Assert.Equal(RuntimePack.OmpVersion, check.RootElement.GetProperty("ompVersion").GetString());
        var history = new System.Diagnostics.ProcessStartInfo(rt.Bun) { WorkingDirectory = rt.Directory };
        foreach (var a in new[]
        {
            "--config=" + rt.BunConfig, "--no-env-file", "--no-install", "test",
            RuntimeInstallerTests.RepoFile("runtime/tests/rpc-messages-backward.test.ts"),
        }) history.ArgumentList.Add(a);
        foreach (var relative in new[]
        {
            "engine-host.test.ts", "session-isolation.test.ts", "browser-isolation.test.ts", "untrusted-project.test.ts",
            "coding-agent/src/modes/rpc/rpc-input-admission.test.ts",
            "coding-agent/src/modes/runtime-init.test.ts",
            "coding-agent/src/session/turn-recovery-admission.test.ts",
        }) history.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(rt.Host)!, relative));
        var temporary = TestProcesses.TempDir("engine-contracts");
        foreach (var variable in new[] { "TMPDIR", "TMP", "TEMP" }) history.Environment[variable] = temporary;
        history.Environment["OMBRA_HISTORY_RPC_DIR"] = Path.Combine(Path.GetDirectoryName(rt.Cli)!, "modes", "rpc");
        history.Environment[InstalledRuntime.TranspilerCacheVariable] = InstalledRuntime.TranspilerCacheIn(rt.Directory);
        var paging = await RuntimeInstaller.RunProcessAsync(history, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(paging.Output);
        Assert.True(paging.ExitCode == 0, paging.Output);
        var bytes = new DirectoryInfo(rt.Directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        TestContext.Current.TestOutputHelper?.WriteLine($"runtime {rt.Platform}: {bytes / 1_000_000} MB on disk, {(DateTime.UtcNow - started).TotalSeconds:0} s; bun={rt.Bun} host={rt.Host}");
    }
}
