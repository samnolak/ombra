using System.Diagnostics;
using System.Text.Json;

namespace OmpGui.Tests;

/// <summary>The app binary's <c>--self-test</c>, run as a child process the way the package smoke runs it.</summary>
public sealed class SelfTestProcessTests
{
    private static string AppDll()
    {
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var dll = RuntimeInstallerTests.RepoFile(Path.Combine("avalonia", "src", "OmpGui.App", "bin", config, "net10.0", "OmpGui.dll"));
        return dll;
    }

    [Fact]
    public async Task A_home_without_config_folders_still_gets_absolute_storage_paths()
    {
        // Regression (package smoke, Linux): with no ~/.config or ~/.local/share yet, GetFolderPath returned "" and the
        // settings file, the runtime (1.2 GB) and the speech model would have been written relative to the current folder.
        var home = TestProcesses.TempDir("empty-home");
        var report = Path.Combine(TestProcesses.TempDir("selftest"), "report.json");
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { AppDll(), "--self-test", report }) psi.ArgumentList.Add(a);
        psi.Environment["HOME"] = home;
        foreach (var v in new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "OMPGUI_RUNTIME_DIR", "OMPGUI_STT_DIR" }) psi.Environment.Remove(v);
        // Never a real omp: the settings name one that does not exist (the check only needs to run, not to find omp).
        // (A restricted PATH broke the terminal check on Windows.)
        var config = Path.Combine(TestProcesses.TempDir("selftest-config"), "omp-gui.local.json");
        File.WriteAllText(config, System.Text.Json.JsonSerializer.Serialize(new { command = Path.Combine(home, "no-omp-here") }));
        psi.Environment["OMPGUI_CONFIG"] = config;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(report), $"no report (exit {p.ExitCode}):\n{await stdout}\n{await stderr}");
        using var doc = JsonDocument.Parse(File.ReadAllText(report));
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var name in new[] { "defaultSettings", "runtimes", "speechModel" })
        {
            var path = paths.GetProperty(name).GetString()!;
            Assert.True(Path.IsPathRooted(path), $"{name} is relative: {path}");
        }
        var checks = doc.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("ok").GetBoolean());
        Assert.True(checks["skia"] && checks["harfbuzz"] && checks["pty"], string.Join(", ", checks));
        Assert.False(checks["omp"]); // no omp on this PATH: reported, not hidden
        Assert.Equal(1, p.ExitCode);
    }
}
