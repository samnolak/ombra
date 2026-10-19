using System.Diagnostics;
using Ombra.ClientCore;

namespace Ombra.Tests;

public sealed class LoginShellPathTests : IDisposable
{
    private readonly string home = TestProcesses.TempDir("login shell ' home");
    public void Dispose() => Directory.Delete(home, true);

    private string Shell(string body)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Shell fixture requires Unix.");
        var file = Path.Combine(home, "shell ' with spaces");
        File.WriteAllText(file, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return file;
    }

    private static void UnixOnly()
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Login-shell recovery is Unix-only.");
    }

    [Fact]
    public async Task Repaired_base_reaches_child_and_explicit_app_or_project_launch_PATH_wins()
    {
        UnixOnly();
        var shell = Shell("printf 'startup noise secret\\n\\000OMBRA_PATH\\000/opt/user/bin:/usr/bin\\n\\000OMBRA_END\\000'\n");
        var result = await LoginShellPath.RecoverAsync(shell, home, "/usr/bin:/bin");
        Assert.Equal("/opt/user/bin:/usr/bin:/bin", result.Path);
        Assert.DoesNotContain("secret", result.Diagnostic);
        foreach (var overridden in new[] { false, true })
        {
            var options = new OmpRuntimeOptions { Environment = overridden ? new() { ["PATH"] = "/project/explicit" } : new() };
            foreach (var spec in new[] { options.ToLaunchSpec(new(home)), options.ToCliLaunchSpec(["version"], home), options.ToTuiLaunchSpec(home) })
            {
                var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false, WorkingDirectory = home };
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add("printf '%s' \"$PATH\"");
                start.Environment["PATH"] = result.Path;
                foreach (var (key, value) in spec.Environment)
                    if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;
                using var child = Process.Start(start)!;
                Assert.Equal(overridden ? "/project/explicit" : result.Path, await child.StandardOutput.ReadToEndAsync());
                await child.WaitForExitAsync();
                Assert.Equal(0, child.ExitCode);
            }
        }
        var removed = new OmpRuntimeOptions { Environment = new() { ["PATH"] = null } };
        Assert.Null(removed.ToLaunchSpec().Environment["PATH"]);
    }

    [Fact]
    public void Shell_uses_only_fixed_arguments_home_cwd_and_no_project_startup_redirects()
    {
        var start = LoginShellPath.CreateStartInfo("/shell with ' spaces", home);
        Assert.Equal("/shell with ' spaces", start.FileName);
        Assert.Equal(home, start.WorkingDirectory);
        Assert.Equal(home, start.Environment["HOME"]);
        Assert.Equal(home, start.Environment["PWD"]);
        Assert.Equal("-ilc", start.ArgumentList[0]);
        Assert.Equal(2, start.ArgumentList.Count);
        Assert.DoesNotContain(home, start.ArgumentList[1]);
        foreach (var key in new[] { "ENV", "BASH_ENV", "ZDOTDIR", "OLDPWD" }) Assert.False(start.Environment.ContainsKey(key));
    }

    [Fact]
    public async Task Failure_preserves_known_PATH_without_echoing_shell_output()
    {
        UnixOnly();
        var result = await LoginShellPath.RecoverAsync(Shell("echo credentials >&2\nexit 12\n"), home, "/known/path");
        Assert.Equal("/known/path", result.Path);
        Assert.Contains("failed", result.Diagnostic);
        Assert.DoesNotContain("credentials", result.Diagnostic);
        var missing = await LoginShellPath.RecoverAsync(Path.Combine(home, "missing shell"), home, "/known/path");
        Assert.Equal("/known/path", missing.Path);
    }

    [Fact]
    public async Task Timeout_preserves_PATH_and_stops_shell_and_its_child()
    {
        UnixOnly();
        var result = await LoginShellPath.RecoverAsync(Shell("echo $$ > shell.pid\n/bin/sleep 60 &\necho $! > child.pid\nwait\n"), home, "/known/path", TimeSpan.FromMilliseconds(500));
        Assert.Equal("/known/path", result.Path);
        Assert.Contains("timed out", result.Diagnostic);
        foreach (var file in new[] { "shell.pid", "child.pid" })
        {
            var pid = int.Parse(File.ReadAllText(Path.Combine(home, file)));
            await TestProcesses.Eventually(() => Alive(pid), alive => !alive, TimeSpan.FromSeconds(3), "timed-out shell tree exited");
        }
    }

    [Fact]
    public async Task Noisy_shell_has_bounded_output_and_retains_known_PATH()
    {
        UnixOnly();
        var result = await LoginShellPath.RecoverAsync(Shell("while :; do printf '0123456789abcdef0123456789abcdef'; done\n"), home, "/known/path");
        Assert.Equal("/known/path", result.Path);
        Assert.Contains("output limit", result.Diagnostic);
        Assert.True(result.Diagnostic.Length < 160);
    }

    [Fact]
    public async Task Windows_does_not_start_a_shell_or_change_PATH()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await LoginShellPath.RecoverAsync("C:\\not-a-shell.exe", home, "C:\\known");
        Assert.Equal("C:\\known", result.Path);
        Assert.Contains("skipped", result.Diagnostic);
    }

    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
