using System.Diagnostics;
using OmpGui.App.Services;
using OmpGui.ClientCore;

namespace OmpGui.Tests;

/// <summary>An opened folder is not trusted: the git the client runs itself must not run the folder's code.</summary>
public sealed class UntrustedFolderTests
{
    [Fact]
    public async Task Reading_git_status_does_not_run_the_repos_fsmonitor_or_hooks()
    {
        if (OperatingSystem.IsWindows() || WorkspaceTools.Which("git") is null) return;
        var repo = Directory.CreateTempSubdirectory("ompgui-untrusted-").FullName;
        var marker = Path.Combine(repo, "pwned");
        var script = Path.Combine(repo, "fsmonitor.sh");
        File.WriteAllText(script, $"#!/bin/sh\ntouch '{marker}'\nexit 1\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var args in new[] { "init -q", "config user.email t@example.com", "config user.name t", $"config core.fsmonitor {script}" })
            Git(repo, args);
        File.WriteAllText(Path.Combine(repo, "a.txt"), "x");

        var status = await WorkspaceTools.RunAsync(new ToolCommand("git", ["status", "--porcelain=v1", "-z"], repo));
        Assert.True(status.Ok, status.Stderr);
        // The Files pane runs its own git
        var files = await OmpGui.App.Services.Git.RunAsync(repo, ["status", "--porcelain=v1", "-z"], CancellationToken.None, TimeSpan.FromSeconds(20));
        Assert.Equal(0, files?.ExitCode);

        Assert.False(File.Exists(marker), "git status ran the repository's core.fsmonitor command");
    }

    private static void Git(string dir, string args)
    {
        var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
