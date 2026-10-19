using System.Diagnostics;
using Ombra.App.Services;
using Ombra.ClientCore;

namespace Ombra.Tests;

/// <summary>An opened folder is not trusted: the git the client runs itself must not run the folder's code.</summary>
public sealed class UntrustedFolderTests
{
    [Fact]
    public async Task Reading_git_status_does_not_run_the_repos_fsmonitor_or_hooks()
    {
        if (OperatingSystem.IsWindows() || WorkspaceTools.Which("git") is null) return;
        var repo = TestProcesses.TempDir("untrusted");
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
        var files = await Ombra.App.Services.Git.RunAsync(repo, ["status", "--porcelain=v1", "-z"], CancellationToken.None, TimeSpan.FromSeconds(20));
        Assert.Equal(0, files?.ExitCode);

        Assert.False(File.Exists(marker), "git status ran the repository's core.fsmonitor command");
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("include")]
    [InlineData("worktree")]
    [InlineData("process")]
    public async Task Read_diff_and_revert_never_execute_configured_filters(string source)
    {
        if (WorkspaceTools.Which("git") is null) Assert.Skip("git is not installed");
        var repo = TestProcesses.TempDir("untrusted-filter");
        RawGit(repo, "init", "-q");
        RawGit(repo, "config", "user.email", "test@example.com");
        RawGit(repo, "config", "user.name", "test");
        RawGit(repo, "config", "core.autocrlf", "false");
        var file = Path.Combine(repo, "a.txt");
        const string original = "one\ntwo\nthree\n";
        const string changed = "one\nchanged line\nthree\n";
        File.WriteAllText(file, original);
        RawGit(repo, "add", "a.txt");
        RawGit(repo, "-c", "commit.gpgsign=false", "commit", "-qm", "initial");
        // Mixed case, a dot, and non-ASCII are significant in a filter subsection.
        const string driver = "MiXeD.driver-é";
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), $"*.txt filter={driver} diff=hostile\n");
        var marker = Path.Combine(repo, "marker");
        const string command = "echo ran >> marker; cat";
        string[] scope = [];
        var configPath = Path.Combine(repo, ".git", "config");
        if (source == "include")
        {
            configPath = Path.Combine(repo, ".git", "included-config");
            RawGit(repo, "config", "include.path", "included-config");
            scope = ["--file", configPath];
        }
        else if (source == "worktree")
        {
            RawGit(repo, "config", "extensions.worktreeConfig", "true");
            scope = ["--worktree"];
            configPath = Path.Combine(repo, ".git", "config.worktree");
        }
        RawGit(repo, ["config", .. scope, $"filter.{driver}.clean", command]);
        RawGit(repo, ["config", .. scope, $"filter.{driver}.smudge", command]);
        RawGit(repo, ["config", .. scope, $"filter.{driver}.required", "true"]);
        if (source == "process")
            RawGit(repo, "config", $"filter.{driver}.process", "echo ran >> marker; exit 1");
        RawGit(repo, "config", "diff.hostile.command", command);
        RawGit(repo, "config", "diff.hostile.textconv", command);
        var configBefore = File.ReadAllText(configPath);
        File.WriteAllText(file, changed);

        // Positive control: the exact old policy and diff flags still execute a clean/process filter.
        _ = RawGitResult(repo, "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null",
            "diff", "--no-ext-diff", "--no-textconv", "HEAD", "--", "a.txt");
        Assert.True(File.Exists(marker), "The hostile fixture must execute under the old client policy.");
        File.Delete(marker);

        var status = await WorkspaceTools.RunAsync(new ToolCommand("git", ["status", "--porcelain=v1"], repo));
        Assert.True(status.Ok, status.Stderr);
        Assert.Contains("a.txt", status.Stdout);
        Assert.False(File.Exists(marker));
        var files = await Ombra.App.Services.Git.StatusAsync(repo, CancellationToken.None);
        Assert.NotNull(files);
        Assert.False(File.Exists(marker));
        var diff = await GitReview.ReadAsync(repo, "a.txt", false, CancellationToken.None);
        Assert.NotNull(diff);
        Assert.Contains("+changed line", diff.Text);
        Assert.False(File.Exists(marker));
        Assert.Null(await GitReview.RevertHunkAsync(WorkspaceTools.RunAsync, repo, diff, Assert.Single(diff.Hunks), CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(file));
        Assert.False(File.Exists(marker));

        File.WriteAllText(file, changed);
        Assert.Null(await GitReview.RevertFileAsync(WorkspaceTools.RunAsync, repo, "a.txt", false, null, CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(file));
        Assert.False(File.Exists(marker), "A restore must not run the smudge/process filter.");
        Assert.Equal(configBefore, File.ReadAllText(configPath));
        var clean = await WorkspaceTools.RunAsync(new ToolCommand("git", ["status", "--porcelain=v1", "--", "a.txt"], repo));
        Assert.True(clean.Ok, clean.Stderr);
        Assert.Equal("", clean.Stdout);
    }

    [Fact]
    public async Task Git_reads_stay_safe_with_unusual_driver_names_and_global_filters_and_fail_closed_on_bad_config()
    {
        if (WorkspaceTools.Which("git") is null) Assert.Skip("git is not installed");
        var repo = TestProcesses.TempDir("filter-config-keys");
        RawGit(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "old-a\n");
        File.WriteAllText(Path.Combine(repo, "b.txt"), "old-b\n");
        RawGit(repo, "add", "a.txt", "b.txt");
        RawGit(repo, "-c", "user.email=test@example.com", "-c", "user.name=test", "-c", "commit.gpgsign=false", "commit", "-qm", "baseline");
        RawGit(repo, "config", "filter.é.clean", "echo unsafe > unicode-marker; cat");
        var global = Path.Combine(repo, "global-config");
        RawGit(repo, "config", "--file", global, "filter.fromGlobal.clean", "echo unsafe > global-marker; cat");
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "a.txt filter=fromGlobal\nb.txt filter=é\n");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "changed-a\n");
        File.WriteAllText(Path.Combine(repo, "b.txt"), "changed-b\n");
        var diff = await WorkspaceTools.RunAsync(new ToolCommand("git", ["diff", "--", "a.txt", "b.txt"], repo,
            Environment: new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = global }));
        Assert.True(diff.Ok, diff.Stderr);
        Assert.Contains("+changed-a", diff.Stdout);
        Assert.Contains("+changed-b", diff.Stdout);
        Assert.False(File.Exists(Path.Combine(repo, "global-marker")));
        Assert.False(File.Exists(Path.Combine(repo, "unicode-marker")));
        var fileDiff = await Ombra.App.Services.Git.RunAsync(repo, ["diff", "--", "b.txt"], CancellationToken.None, TimeSpan.FromSeconds(10));
        Assert.Equal(0, fileDiff?.ExitCode);
        Assert.Contains("+changed-b", fileDiff!.Output);
        Assert.False(File.Exists(Path.Combine(repo, "unicode-marker")));
        File.AppendAllText(Path.Combine(repo, ".git", "config"), "\n[broken\n");
        var failed = await WorkspaceTools.RunAsync(new ToolCommand("git", ["status", "--porcelain"], repo));
        Assert.False(failed.Ok);
        var files = await Ombra.App.Services.Git.RunAsync(repo, ["status", "--porcelain"], CancellationToken.None, TimeSpan.FromSeconds(10));
        Assert.Null(files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_utf8_filter_names_fail_closed_in_read_revert_and_target_worktree_consumers(bool worktree)
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("Raw non-UTF-8 Git names are a Unix fixture.");
        if (WorkspaceTools.Which("git") is null) Assert.Skip("git is not installed");
        var root = TestProcesses.TempDir("invalid-filter");
        var repo = Directory.CreateDirectory(Path.Combine(root, "main")).FullName;
        RawGit(repo, "init", "-q");
        RawGit(repo, "config", "user.email", "test@example.com");
        RawGit(repo, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "original\n");
        RawGit(repo, "add", ".");
        RawGit(repo, "-c", "commit.gpgsign=false", "commit", "-qm", "initial");
        var target = repo;
        var config = Path.Combine(repo, ".git", "config");
        if (worktree)
        {
            target = Path.Combine(root, "target");
            RawGit(repo, "worktree", "add", "-qb", "target", target);
            RawGit(repo, "config", "extensions.worktreeConfig", "true");
            config = RawGitResult(target, "rev-parse", "--git-path", "config.worktree").Stdout.Trim();
            config = Path.GetFullPath(config, target);
        }
        var file = Path.Combine(target, "a.txt");
        File.WriteAllText(file, "changed contents\n");
        var diff = await GitReview.ReadAsync(target, "a.txt", false, CancellationToken.None);
        Assert.NotNull(diff);
        var marker = Path.Combine(root, "marker");
        var command = $"echo ran >> '{marker.Replace("'", "'\\''")}'; cat";
        byte[] malformed = [.. System.Text.Encoding.UTF8.GetBytes("\n[filter \"bad"), 0xff,
            .. System.Text.Encoding.UTF8.GetBytes($"\"]\n\tclean = {command}\n\tsmudge = {command}\n\trequired = true\n")];
        using (var stream = new FileStream(config, FileMode.Append)) stream.Write(malformed);
        var configBefore = File.ReadAllBytes(config);
        File.WriteAllBytes(Path.Combine(target, ".gitattributes"), [.. "a.txt filter=bad"u8.ToArray(), 0xff, (byte)'\n']);

        var read = await WorkspaceTools.RunAsync(new ToolCommand("git", ["diff", "--", "a.txt"], target));
        Assert.False(read.Ok);
        Assert.Contains("invalid UTF-8", read.Stderr);
        Assert.Null(await Ombra.App.Services.Git.RunAsync(target, ["diff", "--", "a.txt"], CancellationToken.None, TimeSpan.FromSeconds(10)));
        Assert.Contains("invalid UTF-8", await GitReview.RevertHunkAsync(WorkspaceTools.RunAsync, target, diff, Assert.Single(diff.Hunks), CancellationToken.None));
        Assert.Contains("invalid UTF-8", await GitReview.RevertFileAsync(WorkspaceTools.RunAsync, target, "a.txt", false, null, CancellationToken.None));
        if (worktree)
        {
            var remove = await WorkspaceTools.RunAsync(new ToolCommand("git", ["worktree", "remove", target], repo, GitTargetDirectory: target));
            Assert.False(remove.Ok);
            Assert.Contains("invalid UTF-8", remove.Stderr);
            Assert.True(Directory.Exists(target));
        }
        Assert.Equal("changed contents\n", File.ReadAllText(file));
        Assert.Equal(configBefore, File.ReadAllBytes(config));
        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData("output")]
    [InlineData("driver")]
    [InlineData("environment")]
    public async Task Oversized_filter_preparation_fails_closed_in_real_consumers(string limit)
    {
        if (WorkspaceTools.Which("git") is null) Assert.Skip("git is not installed");
        var repo = TestProcesses.TempDir("filter-limits");
        RawGit(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "original\n");
        RawGit(repo, "add", ".");
        RawGit(repo, "-c", "user.email=test@example.com", "-c", "user.name=test", "-c", "commit.gpgsign=false", "commit", "-qm", "initial");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "changed contents\n");
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "a.txt filter=hostile\n");
        RawGit(repo, "config", "filter.hostile.clean", "echo unsafe > marker; cat");
        var config = Path.Combine(repo, ".git", "config");
        if (limit == "driver")
            File.AppendAllText(config, string.Concat(Enumerable.Range(0, 33).Select(i => $"\n[filter \"d{i}\"]\nclean = cat\n")));
        else
            File.AppendAllText(config, $"\n[filter \"{new string('x', limit == "output" ? 1024 * 1024 + 100 : 5000)}\"]\nclean = cat\n");
        var result = await WorkspaceTools.RunAsync(new ToolCommand("git", ["diff", "--", "a.txt"], repo));
        Assert.False(result.Ok);
        Assert.Contains(limit, result.Stderr);
        Assert.Null(await Ombra.App.Services.Git.RunAsync(repo, ["diff", "--", "a.txt"], CancellationToken.None, TimeSpan.FromSeconds(10)));
        Assert.NotNull(await GitReview.RevertFileAsync(WorkspaceTools.RunAsync, repo, "a.txt", false, null, CancellationToken.None));
        Assert.Equal("changed contents\n", File.ReadAllText(Path.Combine(repo, "a.txt")));
        Assert.False(File.Exists(Path.Combine(repo, "marker")));
    }

    [Fact]
    public async Task Invalid_probe_output_kills_and_reaps_the_process_before_returning()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("This fixture uses a POSIX process."); return; }
        var root = TestProcesses.TempDir("invalid-probe-process");
        var script = Path.Combine(root, "git");
        File.WriteAllText(script, "#!/bin/sh\necho $$ > probe-pid\nprintf 'filter.bad\\377.clean\\000'\nsleep 10\necho leaked > marker\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var result = await WorkspaceTools.RunAsync(new ToolCommand(script, ["status"], root, TimeSpan.FromSeconds(5)));
        Assert.False(result.Ok);
        Assert.Contains("invalid UTF-8", result.Stderr);
        var pid = int.Parse(File.ReadAllText(Path.Combine(root, "probe-pid")).Trim());
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, "The rejected probe must not keep running.");
        }
        catch (ArgumentException) { } // Reaped: no process with that PID remains.
        Assert.False(File.Exists(Path.Combine(root, "marker")));
    }

    [Fact]
    public async Task One_deadline_covers_all_probes_and_the_actual_command_in_both_launchers()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("This fixture uses a POSIX process."); return; }
        var realGit = WorkspaceTools.Which("git");
        if (realGit is null) Assert.Skip("git is not installed");
        var root = TestProcesses.TempDir("git-total-deadline");
        RawGit(root, "init", "-q");
        var script = Path.Combine(root, "git");
        // Leave two seconds between the shared deadline and the marker: terminating a process tree is
        // asynchronous. A fresh timeout for the command would still let both three-second stages finish.
        File.WriteAllText(script, $"#!/bin/sh\nsleep 3\nif [ \"$1\" != config ]; then echo ran > actual-marker; fi\nexec '{realGit!.Replace("'", "'\\''")}' \"$@\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var timeout = TimeSpan.FromSeconds(4);
        var workspace = await WorkspaceTools.RunAsync(new ToolCommand(script, ["status", "--porcelain"], root, timeout));
        Assert.True(workspace.TimedOut, workspace.Stderr);
        Assert.False(File.Exists(Path.Combine(root, "actual-marker")));
        Assert.Null(await Ombra.App.Services.Git.RunWithProgramAsync(script, root, ["status", "--porcelain"], CancellationToken.None, timeout));
        Assert.False(File.Exists(Path.Combine(root, "actual-marker")));
        // Two config reads fit; the subsequent index probe must share their remaining time. Its own delay is far past
        // the deadline, so a slow kill under a loaded test run cannot let it finish and still pass for the wrong reason.
        File.WriteAllText(script, $"#!/bin/sh\nif [ \"$1\" = config ]; then sleep 0.4; else sleep 3; echo ran > actual-marker; fi\nexec '{realGit!.Replace("'", "'\\''")}' \"$@\"\n");
        var elapsed = Stopwatch.StartNew();
        var (_, preparation) = await WorkspaceTools.PrepareGitEnvironmentAsync(script, root, TimeSpan.FromSeconds(1), CancellationToken.None, targetDirectory: root);
        Assert.True(preparation?.TimedOut == true);
        var remaining = TimeSpan.FromSeconds(3.5) - elapsed.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining); // past the probe's own delay
        Assert.False(File.Exists(Path.Combine(root, "actual-marker")));
    }

    [Fact]
    public async Task Worktree_removal_covers_target_config_without_bypassing_dirty_checks_and_refuses_submodules()
    {
        if (WorkspaceTools.Which("git") is null) Assert.Skip("git is not installed");
        var root = TestProcesses.TempDir("worktree-filter");
        var repo = Directory.CreateDirectory(Path.Combine(root, "main")).FullName;
        var target = Path.Combine(root, "target");
        var marker = Path.Combine(root, "marker");
        RawGit(repo, "init", "-q");
        RawGit(repo, "config", "user.email", "test@example.com");
        RawGit(repo, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "original\n");
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "*.txt filter=target\n");
        RawGit(repo, "add", ".");
        RawGit(repo, "-c", "commit.gpgsign=false", "commit", "-qm", "initial");
        RawGit(repo, "worktree", "add", "-qb", "target", target);
        RawGit(repo, "config", "extensions.worktreeConfig", "true");
        RawGit(target, "config", "--worktree", "filter.target.clean", $"echo ran >> '{marker.Replace("'", "'\\''")}'; cat");
        var file = Path.Combine(target, "a.txt");
        File.WriteAllText(file, "changed contents\n");
        var remove = new ToolCommand("git", ["worktree", "remove", target], repo, GitTargetDirectory: target);
        var dirty = await WorkspaceTools.RunAsync(remove);
        Assert.False(dirty.Ok);
        Assert.Equal("changed contents\n", File.ReadAllText(file));
        Assert.False(File.Exists(marker), "Target worktree filter must not run during Git's own dirty check.");

        // Even an uninitialized gitlink is refused: its nested config is not covered by the target's policy.
        var commit = RawGitResult(repo, "rev-parse", "HEAD").Stdout.Trim();
        RawGit(target, "update-index", "--add", "--cacheinfo", $"160000,{commit},nested");
        var nested = await WorkspaceTools.RunAsync(remove);
        Assert.False(nested.Ok);
        Assert.Contains("contains submodules", nested.Stderr);
        Assert.True(Directory.Exists(target));
        Assert.False(File.Exists(marker));
        RawGit(target, "update-index", "--force-remove", "nested");

        File.WriteAllText(file, "original\n");
        var clean = await WorkspaceTools.RunAsync(remove);
        Assert.True(clean.Ok, clean.Stderr);
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Parent_status_and_diff_do_not_enter_submodule_filter_configuration()
    {
        if (WorkspaceTools.Which("git") is null) Assert.Skip("git is not installed");
        var repo = TestProcesses.TempDir("submodule-filter");
        var sub = Directory.CreateDirectory(Path.Combine(repo, "sub")).FullName;
        foreach (var dir in new[] { repo, sub })
        {
            RawGit(dir, "init", "-q");
            RawGit(dir, "config", "user.email", "test@example.com");
            RawGit(dir, "config", "user.name", "test");
        }
        File.WriteAllText(Path.Combine(sub, "a.txt"), "original\n");
        RawGit(sub, "add", ".");
        RawGit(sub, "-c", "commit.gpgsign=false", "commit", "-qm", "initial");
        File.WriteAllText(Path.Combine(repo, ".gitmodules"), "[submodule \"sub\"]\n\tpath = sub\n\turl = ./sub\n\tignore = none\n");
        RawGit(repo, "add", ".");
        RawGit(repo, "-c", "commit.gpgsign=false", "commit", "-qm", "initial");
        RawGit(repo, "config", "submodule.sub.ignore", "none");
        File.WriteAllText(Path.Combine(sub, ".gitattributes"), "*.txt filter=nested\n");
        RawGit(sub, "config", "filter.nested.clean", "echo ran >> marker; cat");
        File.WriteAllText(Path.Combine(sub, "a.txt"), "changed contents\n");
        var marker = Path.Combine(sub, "marker");
        var status = await WorkspaceTools.RunAsync(new ToolCommand("git", ["status", "--porcelain"], repo));
        Assert.True(status.Ok, status.Stderr);
        Assert.False(File.Exists(marker));
        Assert.NotNull(await Ombra.App.Services.Git.StatusAsync(repo, CancellationToken.None));
        Assert.NotNull(await GitReview.ReadAsync(repo, "sub", false, CancellationToken.None));
        Assert.False(File.Exists(marker));
    }

    private static void RawGit(string dir, params string[] args)
    {
        var result = RawGitResult(dir, args);
        Assert.True(result.Ok, result.Stderr);
    }

    private static ToolResult RawGitResult(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(20_000), "fixture git timed out");
        return new ToolResult(p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static void Git(string dir, string args)
    {
        var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
