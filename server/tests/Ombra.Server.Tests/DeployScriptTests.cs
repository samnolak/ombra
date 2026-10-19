using System.Diagnostics;

namespace Ombra.Server.Tests;

/// <summary>Runs the bash regression harness of server/deploy (install.sh, make-release.sh, compose.yaml, systemd unit).</summary>
public sealed class DeployScriptTests
{
    [Fact]
    public async Task Deploy_scripts_pass_the_regression_harness()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the deploy scripts target Linux (bash)");
        var repo = FindRepoRoot();
        var psi = new ProcessStartInfo("bash")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(repo, "server", "deploy", "tests", "run.sh"));

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, $"server/deploy/tests/run.sh exited with {process.ExitCode}:\n{output}");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "server", "deploy")))
                return dir.FullName;
        throw new InvalidOperationException($"no server/deploy above {AppContext.BaseDirectory}");
    }
}
