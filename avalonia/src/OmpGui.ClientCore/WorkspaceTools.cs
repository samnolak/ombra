using OmpGui.Rpc;

namespace OmpGui.ClientCore;

/// <summary>A command of a tool on this computer (git, gh, ssh, xdpyinfo) for the settings pages.</summary>
public sealed record ToolCommand(string File, IReadOnlyList<string> Args, string? WorkingDirectory = null, TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string?>? Environment = null)
{
    public override string ToString() => File + (Args.Count > 0 ? " " + string.Join(' ', Args) : "");
}

/// <summary>What the tool printed. <see cref="NotFound"/>: it is not installed (not on PATH).</summary>
public sealed record ToolResult(int ExitCode, string Stdout, string Stderr, bool NotFound = false, bool TimedOut = false)
{
    public bool Ok => ExitCode == 0 && !NotFound && !TimedOut;

    /// <summary>The tool's own words for a failure: the last non-empty line of stderr (else stdout).</summary>
    public string Message
    {
        get
        {
            var lines = (Stderr.Trim().Length > 0 ? Stderr : Stdout).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return lines.Count == 0 ? $"exit code {ExitCode}" : lines[^1];
        }
    }
}

/// <summary>Runs git, gh and ssh without a window or a prompt, with a deadline, off the UI thread.</summary>
public static class WorkspaceTools
{
    /// <summary>
    /// For every git the client runs itself: the opened folder is not trusted, and its <c>.git/config</c> could name an
    /// fsmonitor command or a hooks folder that git would run. Both are overridden (a missing folder means no hooks).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> UntrustedRepoGit = new Dictionary<string, string?>
    {
        ["GIT_CONFIG_COUNT"] = "2",
        ["GIT_CONFIG_KEY_0"] = "core.fsmonitor",
        ["GIT_CONFIG_VALUE_0"] = "false",
        ["GIT_CONFIG_KEY_1"] = "core.hooksPath",
        ["GIT_CONFIG_VALUE_1"] = Path.Combine(Path.GetTempPath(), "ompgui-no-git-hooks"),
    };

    /// <summary>No terminal prompts, no index lock taken by a status read, no pager; <see cref="UntrustedRepoGit"/>.</summary>
    private static readonly Dictionary<string, string?> Quiet = new(UntrustedRepoGit)
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["GIT_PAGER"] = "cat",
        ["GH_PROMPT_DISABLED"] = "1",
        ["GH_NO_UPDATE_NOTIFIER"] = "1",
        ["NO_COLOR"] = "1",
    };

    public static Task<ToolResult> RunAsync(ToolCommand command, CancellationToken ct = default) => Task.Run(async () =>
    {
        if (Which(command.File) is not { } path) return new ToolResult(-1, "", $"{command.File} was not found", NotFound: true);
        var env = new Dictionary<string, string?>(Quiet);
        if (command.Environment is { } extra) foreach (var (k, v) in extra) env[k] = v;
        var timeout = command.Timeout ?? TimeSpan.FromSeconds(15);
        var r = await OmpCli.RunAsync(new OmpLaunchSpec
        {
            FileName = path,
            Arguments = command.Args,
            WorkingDirectory = command.WorkingDirectory,
            Environment = env,
        }, timeout, ct).ConfigureAwait(false);
        var timedOut = r.ExitCode == -1 && r.Stderr.EndsWith("timed out", StringComparison.Ordinal);
        // What the tool printed before the deadline stays (ssh's log shows how far it got); the last line says why it stopped
        return new ToolResult(r.ExitCode, r.Stdout,
            timedOut ? r.Stderr[..^"timed out".Length] + $"{command.File} did not answer within {timeout.TotalSeconds:0} s" : r.Stderr, TimedOut: timedOut);
    }, ct);

    /// <summary>The full path of an executable on PATH (with PATHEXT on Windows), or null.</summary>
    public static string? Which(string file)
    {
        if (Path.IsPathRooted(file)) return File.Exists(file) ? file : null;
        IEnumerable<string> exts = OperatingSystem.IsWindows() && !Path.HasExtension(file)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim('"'), file + ext); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate) && (OperatingSystem.IsWindows() || IsExecutable(candidate))) return candidate;
            }
        }
        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        try { return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
