using System.Diagnostics;
using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>A command of a tool on this computer (git, gh, ssh, xdpyinfo) for the settings pages.</summary>
public sealed record ToolCommand(string File, IReadOnlyList<string> Args, string? WorkingDirectory = null, TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string?>? Environment = null, string? GitTargetDirectory = null)
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
    /// Baseline for client-owned git only, never omp's user-authorized tools. Filter drivers need the per-invocation
    /// overrides from <see cref="PrepareGitEnvironmentAsync"/> as Git has no wildcard "disable filters" setting.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string?> UntrustedRepoGit = new Dictionary<string, string?>
    {
        ["GIT_CONFIG_PARAMETERS"] = null,
        ["GIT_CONFIG_COUNT"] = "6",
        ["GIT_CONFIG_KEY_0"] = "core.fsmonitor",
        ["GIT_CONFIG_VALUE_0"] = "false",
        ["GIT_CONFIG_KEY_1"] = "core.hooksPath",
        ["GIT_CONFIG_VALUE_1"] = "/dev/null",
        ["GIT_CONFIG_KEY_2"] = "submodule.recurse",
        ["GIT_CONFIG_VALUE_2"] = "false",
        // A submodule has a different config, not covered by this invocation's filter-driver enumeration.
        ["GIT_CONFIG_KEY_3"] = "diff.ignoreSubmodules",
        ["GIT_CONFIG_VALUE_3"] = "all",
        ["GIT_CONFIG_KEY_4"] = "core.pager",
        ["GIT_CONFIG_VALUE_4"] = "cat",
        ["GIT_CONFIG_KEY_5"] = "maintenance.auto",
        ["GIT_CONFIG_VALUE_5"] = "false",
        // Read/review must not turn a missing partial-clone object into an implicit network/helper invocation.
        ["GIT_NO_LAZY_FETCH"] = "1",
        ["GIT_ALLOW_PROTOCOL"] = "",
    };

    /// <summary>No terminal prompts, no index lock taken by a status read, no pager.</summary>
    private static readonly Dictionary<string, string?> Quiet = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["GIT_PAGER"] = "cat",
        ["GH_PROMPT_DISABLED"] = "1",
        ["GH_NO_UPDATE_NOTIFIER"] = "1",
        ["NO_COLOR"] = "1",
    };
    private static readonly string[] FilterSettings = ["clean", "smudge", "process", "required"];
    // All names, filtered here: Git's --get-regexp skips names its locale regex cannot match (non-UTF-8 bytes),
    // which would hide exactly the drivers that must fail closed under strict decoding.
    private static readonly string[] FilterKeyArguments = ["config", "--includes", "--null", "--name-only", "--list"];
    private static readonly string[] StatusSafetyArguments = ["--ignore-submodules=all"];
    private static readonly string[] DiffSafetyArguments = ["--ignore-submodules=all", "--no-ext-diff", "--no-textconv"];
    private const int MaxGitProbeChars = 1024 * 1024, MaxGitFilterDrivers = 32, MaxGitFilterEnvironmentBytes = 16 * 1024;
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Client calls have optional leading -c pairs; read flags override even per-submodule config.</summary>
    public static IReadOnlyList<string> GitReadArguments(IReadOnlyList<string> arguments)
    {
        var command = 0;
        while (command + 1 < arguments.Count && arguments[command] == "-c") command += 2;
        if (command >= arguments.Count) return arguments;
        var flags = arguments[command] switch { "status" => StatusSafetyArguments, "diff" => DiffSafetyArguments, _ => null };
        if (flags is null) return arguments;
        var result = new string[arguments.Count + flags.Length];
        var at = 0;
        for (; at <= command; at++) result[at] = arguments[at];
        foreach (var flag in flags) result[at++] = flag;
        for (var i = command + 1; i < arguments.Count; i++) result[at++] = arguments[i];
        return result;
    }

    /// <summary>
    /// Reads driver names without running them, including included/global/worktree config, then overrides every
    /// clean/smudge/process command and required flag for this launch only. Empty commands are not executed by Git
    /// (convert.c/apply_filter); required=false permits raw content review. No repository/user config is rewritten.
    /// Failure is closed: callers must not launch their original command when the configuration cannot be read.
    /// </summary>
    public static async Task<(IReadOnlyDictionary<string, string?>? Environment, ToolResult? Error)> PrepareGitEnvironmentAsync(
        string program, string? directory, TimeSpan timeout, CancellationToken ct,
        IReadOnlyDictionary<string, string?>? extra = null, string? targetDirectory = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try { return await PrepareGitEnvironmentCoreAsync(program, directory, limit.Token, extra, targetDirectory).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return (null, new ToolResult(-1, "", "git safety preparation timed out", TimedOut: true));
        }
    }

    private static async Task<(IReadOnlyDictionary<string, string?>? Environment, ToolResult? Error)> PrepareGitEnvironmentCoreAsync(
        string program, string? directory, CancellationToken ct, IReadOnlyDictionary<string, string?>? extra, string? targetDirectory)
    {
        var env = new Dictionary<string, string?>(Quiet);
        if (extra is not null) foreach (var (key, value) in extra) env[key] = value;
        foreach (var (key, value) in UntrustedRepoGit) env[key] = value;
        var drivers = new HashSet<string>(StringComparer.Ordinal);
        var count = 6;
        var filterEnvironmentBytes = 0;
        IReadOnlyList<string?> directories = targetDirectory is null ? [directory] : [directory, targetDirectory];
        foreach (var cwd in directories)
        {
            if (cwd is not null && LocalFilePaths.Resolve(null, cwd) is null)
                return (null, new ToolResult(-1, "", "Only local working directories can be inspected by git."));
            var config = await RunGitProbeAsync(program, cwd, env, FilterKeyArguments, ct).ConfigureAwait(false);
            // An empty configuration may exit 1 without output.
            if (config.ExitCode != 0 && !(config.ExitCode == 1 && config.Stdout.Length == 0 && config.Stderr.Length == 0))
                return (null, config);
            if (config.Stdout.Length > 0 && !config.Stdout.EndsWith('\0'))
                return (null, new ToolResult(-1, "", "git returned incomplete filter configuration"));
            foreach (var key in config.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!key.StartsWith("filter.", StringComparison.Ordinal)) continue;
                var dot = key.LastIndexOf('.');
                if (!FilterSettings.Contains(key[(dot + 1)..], StringComparer.Ordinal)) continue;
                if (dot <= "filter.".Length)
                    return (null, new ToolResult(-1, "", "git returned an invalid filter configuration key"));
                var driver = key[..dot];
                if (!drivers.Add(driver)) continue;
                if (drivers.Count > MaxGitFilterDrivers)
                    return (null, new ToolResult(-1, "", "git filter configuration exceeds the driver limit"));
                // Four pairs per driver, including environment key names, separators and values. Conservatively
                // bound the generated block below Windows' environment limit, independently of probe output size.
                filterEnvironmentBytes += 4 * (StrictUtf8.GetByteCount(driver) + 100);
                if (filterEnvironmentBytes > MaxGitFilterEnvironmentBytes)
                    return (null, new ToolResult(-1, "", "git filter configuration exceeds the environment limit"));
                foreach (var setting in FilterSettings)
                {
                    env[$"GIT_CONFIG_KEY_{count}"] = driver + "." + setting;
                    env[$"GIT_CONFIG_VALUE_{count}"] = setting == "required" ? "false" : "";
                    count++;
                }
            }
        }
        env["GIT_CONFIG_COUNT"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (targetDirectory is not null)
        {
            // worktree remove runs its own status with --ignore-submodules=none. Its target's filters are covered
            // above; nested repositories are not. Refuse those rather than bypass Git's dirty/untracked guard.
            var index = await RunGitProbeAsync(program, targetDirectory, env, ["ls-files", "--stage", "-z"], ct).ConfigureAwait(false);
            if (!index.Ok) return (null, index);
            if (index.Stdout.Length > 0 && !index.Stdout.EndsWith('\0'))
                return (null, new ToolResult(-1, "", "git returned an incomplete worktree index"));
            if (index.Stdout.Split('\0').Any(entry => entry.StartsWith("160000 ", StringComparison.Ordinal)))
                return (null, new ToolResult(-1, "", "This worktree contains submodules. Remove it in a trusted terminal; the client cannot safely inspect nested Git filters."));
        }
        return (env, null);
    }

    private static async Task<ToolResult> RunGitProbeAsync(string program, string? directory,
        IReadOnlyDictionary<string, string?> environment, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = directory ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = StrictUtf8,
            StandardErrorEncoding = StrictUtf8,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        foreach (var (key, value) in environment)
        {
            if (value is null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }
        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ToolResult(-1, "", $"could not read git configuration: {e.Message}");
        }
        process.StandardInput.Close();
        async Task<string> ReadOutputAsync(StreamReader reader)
        {
            try
            {
                var text = new System.Text.StringBuilder();
                var buffer = new char[4096];
                int n;
                while ((n = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (text.Length + n > MaxGitProbeChars) throw new InvalidDataException("git safety probe exceeded the output limit");
                    text.Append(buffer, 0, n);
                }
                return text.ToString();
            }
            catch
            {
                KillProbe(process);
                throw;
            }
        }
        try
        {
            // Decode security-sensitive names strictly; replacement characters would disable the wrong driver.
            // A read failure kills immediately rather than leaving Git blocked on a full pipe.
            var stdout = ReadOutputAsync(process.StandardOutput);
            var stderr = ReadOutputAsync(process.StandardError);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(ct)).ConfigureAwait(false);
            return new ToolResult(process.ExitCode, await stdout, await stderr);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return new ToolResult(-1, "", "git safety probe returned invalid UTF-8");
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return new ToolResult(-1, "", e.Message);
        }
        finally
        {
            KillProbe(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void KillProbe(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public static Task<ToolResult> RunAsync(ToolCommand command, CancellationToken ct = default) => Task.Run(async () =>
    {
        var timeout = command.Timeout ?? TimeSpan.FromSeconds(15);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try { return await RunCoreAsync(command, timeout, limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return new ToolResult(-1, "", $"{command.File} did not answer within {timeout.TotalSeconds:0} s", TimedOut: true);
        }
    }, ct);

    private static async Task<ToolResult> RunCoreAsync(ToolCommand command, TimeSpan timeout, CancellationToken ct)
    {
        if (Which(command.File) is not { } path) return new ToolResult(-1, "", $"{command.File} was not found", NotFound: true);
        ct.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, string?> env = Quiet;
        var arguments = command.Args;
        if (Path.GetFileNameWithoutExtension(command.File).Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            var prepared = await PrepareGitEnvironmentAsync(path, command.WorkingDirectory, timeout, ct, command.Environment, command.GitTargetDirectory).ConfigureAwait(false);
            if (prepared.Error is { } error) return error;
            env = prepared.Environment!;
            arguments = GitReadArguments(arguments);
        }
        else if (command.Environment is { } extra)
        {
            var merged = new Dictionary<string, string?>(Quiet);
            foreach (var (k, v) in extra) merged[k] = v;
            env = merged;
        }
        ct.ThrowIfCancellationRequested();
        var r = await OmpCli.RunAsync(new OmpLaunchSpec
        {
            FileName = path,
            Arguments = arguments,
            WorkingDirectory = command.WorkingDirectory,
            Environment = env,
        }, timeout, ct).ConfigureAwait(false);
        var timedOut = r.ExitCode == -1 && r.Stderr.EndsWith("timed out", StringComparison.Ordinal);
        // What the tool printed before the deadline stays (ssh's log shows how far it got); the last line says why it stopped
        return new ToolResult(r.ExitCode, r.Stdout,
            timedOut ? r.Stderr[..^"timed out".Length] + $"{command.File} did not answer within {timeout.TotalSeconds:0} s" : r.Stderr, TimedOut: timedOut);
    }

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
