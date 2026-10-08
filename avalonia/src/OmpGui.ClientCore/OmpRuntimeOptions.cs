using System.Text.Json;
using System.Text.Json.Serialization;
using OmpGui.Rpc;

namespace OmpGui.ClientCore;

/// <summary>What changes between omp launches of one window: project folder, session to resume, approval mode.</summary>
public sealed record LaunchRequest(string? WorkingDirectory = null, string? ResumeSessionFile = null, string? ApprovalMode = null);

/// <summary>
/// Where omp lives and how to start it. Machine-specific, so it is read from a local JSON file
/// (never committed) rather than compiled in. Unset values fall back to omp's own defaults.
/// </summary>
public sealed record OmpRuntimeOptions
{
    /// <summary><c>omp.exe</c>, or a runtime such as <c>bun.exe</c> when <see cref="PrefixArgs"/> names the omp CLI entry. Default: <c>omp</c> on PATH.</summary>
    public string? Command { get; init; }
    /// <summary>Arguments before omp's own (e.g. the path to <c>cli.ts</c> for a Bun runtime pack).</summary>
    public string[] PrefixArgs { get; init; } = [];
    /// <summary>omp profile (sets <c>OMP_PROFILE</c>). The existing <c>harness</c> profile is used in place, never copied.</summary>
    public string? Profile { get; init; }
    /// <summary><c>provider/model</c> for this session (<c>--model</c>). Null keeps the profile's default model role.</summary>
    public string? Model { get; init; }
    /// <summary><c>rpc-ui</c> (session has a UI: tool approvals are asked through extension UI requests) or <c>rpc</c>.</summary>
    public string Mode { get; init; } = "rpc-ui";
    /// <summary><c>--approval-mode</c>: always-ask | write | yolo. Default write (edits run, commands ask).</summary>
    public string? ApprovalMode { get; init; } = "write";
    public string? WorkingDirectory { get; init; }
    /// <summary>Extra environment for omp; a null value removes the variable. Keep secrets out of committed files.</summary>
    public Dictionary<string, string?> Environment { get; init; } = [];
    public string[] ExtraArgs { get; init; } = [];

    // Client preferences (not passed to omp).
    /// <summary>Project folder used last; omp starts there next time when <see cref="WorkingDirectory"/> is not set.</summary>
    public string? LastWorkingDirectory { get; init; }
    /// <summary>system | light | dark.</summary>
    public string? Theme { get; init; }
    /// <summary>OS notification when a run ends or omp asks while the window is in the background (default on).</summary>
    public bool? Notifications { get; init; }
    /// <summary>How the message box sends: <c>enter</c> (default; Shift+Enter adds a line) or <c>mod-enter</c> (⌘/Ctrl+Enter sends, Enter adds a line).</summary>
    public string? SendKey { get; init; }
    /// <summary>Update manifest address (https, signed with the release key); unset = this client's release feed.</summary>
    public string? UpdateFeed { get; init; }
    /// <summary>Check the feed shortly after start and once a day (default on). Installing always waits for the user.</summary>
    public bool? CheckForUpdates { get; init; }
    /// <summary>The pixel pet above the message box (<see cref="PetOptions"/>); null means the defaults.</summary>
    public PetOptions? Pet { get; init; }
    /// <summary>Project folders added from the sidebar: listed even before they have a session.</summary>
    public string[]? SidebarProjects { get; init; }
    /// <summary>Project folders removed from the sidebar: their sessions are not listed (nothing is deleted on disk).</summary>
    public string[]? HiddenProjects { get; init; }
    /// <summary>The sidebar's width as last dragged (220–480 px); null means the default.</summary>
    public double? SidebarWidth { get; init; }
    /// <summary>"Don't ask again" rules for omp's approval requests (<see cref="ApprovalRuleSet"/>); session rules are never saved.</summary>
    public SavedApprovalRule[]? ApprovalRules { get; init; }
    /// <summary>How many chats keep their own omp running at once (default 4): opening one more closes the idle chat used
    /// longest ago; a working one is never closed.</summary>
    public int? MaxOpenSessions { get; init; }
    /// <summary>omp's browser tool drives the app's built-in browser (default on): see
    /// <see cref="Browser.AgentBrowserDefaults"/>. Off: omp's own browser settings decide, the app adds nothing.</summary>
    public bool? AgentUsesGuiBrowser { get; init; }
    /// <summary>Settings › Advanced › Corporate network certificates (<see cref="Network.CorporateTrust"/>): null (off),
    /// <c>system</c> (macOS: Bun trusts what macOS trusts) or <c>pem</c> (Bun also trusts <see cref="CorporateTrustBundle"/>).</summary>
    public string? CorporateTrust { get; init; }
    /// <summary>The app's own copy of the user's CA file (mode <c>pem</c>), in its settings folder.</summary>
    public string? CorporateTrustBundle { get; init; }

    /// <summary>The environment omp inherits from the app, as launches see it (null: this process's; tests replace it).</summary>
    [JsonIgnore]
    public Func<string, string?>? InheritedEnvironment { get; init; }

    public const string ConfigEnvVar = "OMPGUI_CONFIG";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Default local config: <c>%APPDATA%\OmpGui\omp-gui.local.json</c> (XDG config dir elsewhere).</summary>
    public static string DefaultConfigPath =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData, System.Environment.SpecialFolderOption.DoNotVerify), "OmpGui", "omp-gui.local.json");

    /// <summary>Loads from an explicit path, else <c>OMPGUI_CONFIG</c>, else the default path; a missing default file means defaults.</summary>
    public static OmpRuntimeOptions Load(string? explicitPath = null)
    {
        var path = explicitPath ?? System.Environment.GetEnvironmentVariable(ConfigEnvVar);
        if (path is null)
        {
            if (!File.Exists(DefaultConfigPath)) return new OmpRuntimeOptions();
            path = DefaultConfigPath;
        }
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<OmpRuntimeOptions>(stream, Json) ?? new OmpRuntimeOptions();
    }

    public static readonly IReadOnlyList<string> ApprovalModes = ["always-ask", "write", "yolo"];
    public const string DefaultApprovalMode = "write";

    /// <summary>
    /// The mode omp runs with: exactly one of <see cref="ApprovalModes"/>, else <see cref="DefaultApprovalMode"/>.
    /// Never left out: omp's own default is yolo, and it ignores an invalid flag with only a log line.
    /// </summary>
    public static string EffectiveApprovalMode(string? mode) => mode is not null && ApprovalModes.Contains(mode) ? mode : DefaultApprovalMode;

    /// <summary>A warning when the configured approval mode is not one omp knows (then "write" is used).</summary>
    public string? ApprovalModeWarning =>
        ApprovalMode is not null && !ApprovalModes.Contains(ApprovalMode)
            ? $"Unknown approval mode \"{ApprovalMode}\" in the settings; using \"{DefaultApprovalMode}\" (ask before commands)."
            : null;

    /// <summary>
    /// omp's own terminal UI (for features omp 18.2.0 offers only there: plan, goal, vibe…), started in a terminal
    /// panel. A separate omp process with its own session: it never writes the session the window has open.
    /// </summary>
    public OmpLaunchSpec ToTuiLaunchSpec(string? workingDirectory)
    {
        var rpc = ToLaunchSpec(new LaunchRequest(workingDirectory));
        // The same approval mode as the window's session: omp's own default is yolo.
        var args = new List<string>(PrefixArgs) { "--approval-mode", EffectiveApprovalMode(ApprovalMode) };
        if (Model is not null) args.AddRange(["--model", Model]);
        args.AddRange(ExtraArgs);
        // The user's environment settings only: the RPC process's hints (NO_COLOR, C.UTF-8 locale) would take the
        // colours and the locale away from an interactive terminal UI. A null value still means "not set for omp".
        var env = new Dictionary<string, string?>(Environment);
        if (Profile is not null) env["OMP_PROFILE"] = Profile;
        Network.CorporateTrust.Apply(env, this, InheritedEnvironment ?? System.Environment.GetEnvironmentVariable);
        return new OmpLaunchSpec { FileName = rpc.FileName, Arguments = args, WorkingDirectory = rpc.WorkingDirectory, Environment = env };
    }

    /// <summary>
    /// omp's command line for one of its CLI subcommands (<c>plugin list --json</c>, <c>config set … --json</c>): the
    /// same binary, prefix arguments, profile and environment as the session, without the RPC and session flags.
    /// </summary>
    public OmpLaunchSpec ToCliLaunchSpec(IEnumerable<string> arguments, string? workingDirectory = null)
    {
        var rpc = ToLaunchSpec(new LaunchRequest(workingDirectory));
        var args = new List<string>(PrefixArgs);
        args.AddRange(arguments);
        return rpc with { Arguments = args };
    }

    public OmpLaunchSpec ToLaunchSpec(LaunchRequest? request = null)
    {
        var args = new List<string>(PrefixArgs) { "--mode", Mode };
        args.AddRange(["--approval-mode", EffectiveApprovalMode(request?.ApprovalMode ?? ApprovalMode)]);
        if (request?.ResumeSessionFile is { } resume) args.AddRange(["--resume", resume]);
        if (Model is not null) args.AddRange(["--model", Model]);
        args.AddRange(ExtraArgs);
        // UTF-8 hints for the shells and tools omp spawns (Git Bash, Python on Windows) and no colour codes in omp's
        // output. Explicit Environment entries win.
        var env = new Dictionary<string, string?>
        {
            ["LANG"] = "C.UTF-8",
            ["LC_ALL"] = "C.UTF-8",
            ["PYTHONIOENCODING"] = "utf-8:replace",
            ["PYTHONUTF8"] = "1",
            ["PYTHONLEGACYWINDOWSSTDIO"] = "utf-8",
            ["NO_COLOR"] = "1",
        };
        foreach (var (k, v) in Environment) env[k] = v;
        if (Profile is not null) env["OMP_PROFILE"] = Profile;
        // Corporate network certificates (Settings › Advanced), unless the user set the variable themselves
        Network.CorporateTrust.Apply(env, this, InheritedEnvironment ?? System.Environment.GetEnvironmentVariable);
        return new OmpLaunchSpec
        {
            FileName = Command ?? "omp",
            Arguments = args,
            WorkingDirectory = request?.WorkingDirectory ?? WorkingDirectory,
            Environment = env,
        };
    }
}
