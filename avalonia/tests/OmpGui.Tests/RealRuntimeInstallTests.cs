using OmpGui.App.Services.Runtime;

namespace OmpGui.Tests;

/// <summary>
/// The real install from the npm registry: Bun checked against its pinned integrity, omp from the pack lockfile, omp
/// answering with its version. Runs when OMPGUI_RUNTIME_INSTALL_DIR names the install root (CI smoke, all OSes).
/// </summary>
public sealed class RealRuntimeInstallTests
{
    [Fact]
    public async Task Installs_the_pinned_runtime_from_npm_and_omp_reports_its_version()
    {
        var root = Environment.GetEnvironmentVariable("OMPGUI_RUNTIME_INSTALL_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "OMPGUI_RUNTIME_INSTALL_DIR not set");
        var platform = OmpGui.App.Platform.RuntimePlatform.Key(out var description);
        var installer = new RuntimeInstaller(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, root!, platform);
        Assert.True(installer.Supported, $"no pinned build for {description}");
        var started = DateTime.UtcNow;
        var rt = installer.FindInstalled() ?? await installer.InstallAsync(null, TestContext.Current.CancellationToken);
        var version = new System.Diagnostics.ProcessStartInfo(rt.Bun) { WorkingDirectory = rt.Directory };
        foreach (var a in new[] { "--no-install", rt.Cli, "--version" }) version.ArgumentList.Add(a);
        var result = await RuntimeInstaller.RunProcessAsync(version, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(RuntimePack.OmpVersion, result.Output);
        var bytes = new DirectoryInfo(rt.Directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        TestContext.Current.TestOutputHelper?.WriteLine($"runtime {rt.Platform}: {bytes / 1_000_000} MB on disk, {(DateTime.UtcNow - started).TotalSeconds:0} s; bun={rt.Bun} cli={rt.Cli}");
    }
}
