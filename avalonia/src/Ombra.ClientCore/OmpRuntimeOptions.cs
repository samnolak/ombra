using System.Text.Json;
using System.Text.Json.Serialization;
using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>What changes between omp launches of one window: project folder, session to resume, approval mode.</summary>
public sealed record LaunchRequest(string? WorkingDirectory = null, string? ResumeSessionFile = null, string? ApprovalMode = null,
    bool ResumeOnCurrentModel = false)
{
    public bool ReadOnly { get; init; }
}

/// <summary>
/// Where omp lives and how to start it. Machine-specific, so it is read from a local JSON file
/// (never committed) rather than compiled in. Unset values fall back to omp's own defaults.
/// </summary>
public sealed record OmpRuntimeOptions
{
    /// <summary>The app-owned engine executable, or a runtime when <see cref="PrefixArgs"/> names its host entry.</summary>
    public string? Command { get; init; }
    /// <summary>Executable-prefix arguments identifying the shared engine host.</summary>
    public string[] PrefixArgs { get; init; } = [];
    /// <summary>Executable-prefix arguments for explicit CLI management and terminal launches, not GUI sessions.</summary>
    public string[] ManagementPrefixArgs { get; init; } = [];
    /// <summary>omp profile (sets <c>OMP_PROFILE</c>). The existing <c>harness</c> profile is used in place, never copied.</summary>
    public string? Profile { get; init; }
    /// <summary><c>provider/model</c> for this session (<c>--model</c>). Null keeps the profile's default model role.</summary>
    public string? Model { get; init; }
    /// <summary><c>--approval-mode</c>: always-ask | write | yolo. Default write (edits run, commands ask).</summary>
    public string? ApprovalMode { get; init; } = "write";
    /// <summary><c>--thinking</c> for a new session: off | minimal | low | medium | high | xhigh | max | auto. Null (or a
    /// level omp does not know) means <see cref="DefaultThinkingLevelValue"/>. A resumed session keeps its own level.</summary>
    public string? DefaultThinkingLevel { get; init; }
    public string? WorkingDirectory { get; init; }
    /// <summary>Extra environment for omp; a null value removes the variable. Keep secrets out of committed files.</summary>
    public Dictionary<string, string?> Environment { get; init; } = [];
    public string[] ExtraArgs { get; init; } = [];

    // Client preferences (not passed to omp).
    /// <summary>Project folder used last; omp starts there next time when <see cref="WorkingDirectory"/> is not set.</summary>
    public string? LastWorkingDirectory { get; init; }
    /// <summary>system | light | dark.</summary>
    public string? Theme { get; init; }
    /// <summary>The app's language (<c>en</c>, <c>ru</c>, or a code of the user's own file in <c>languages/</c>); null follows the system.</summary>
    public string? Language { get; init; }
    /// <summary>OS notification when a run ends or omp asks while the window is in the background (default on).</summary>
    public bool? Notifications { get; init; }
    /// <summary>Watch the open projects' git remotes in the background (git fetch, never a merge; default on; off under strict network privacy).</summary>
    public bool? WatchGitRemotes { get; init; }
    /// <summary>How the message box sends: <c>enter</c> (default; Shift+Enter adds a line) or <c>mod-enter</c> (⌘/Ctrl+Enter sends, Enter adds a line).</summary>
    public string? SendKey { get; init; }
    /// <summary>Update manifest address (https, signed with the release key); unset = this client's release feed.</summary>
    public string? UpdateFeed { get; init; }
    /// <summary>Check the feed at every start and once a day, and get a newer version ready for the next restart or quit (default on).</summary>
    public bool? CheckForUpdates { get; init; }
    /// <summary>The pixel pet above the message box (<see cref="PetOptions"/>); null means the defaults.</summary>
    public PetOptions? Pet { get; init; }
    /// <summary>Project folders added from the sidebar: listed even before they have a session.</summary>
    public string[]? SidebarProjects { get; init; }
    /// <summary>Project folders removed from the sidebar: their sessions are not listed (nothing is deleted on disk).</summary>
    public string[]? HiddenProjects { get; init; }
    /// <summary>Project folders pinned to the top of the sidebar, in the order they were pinned.</summary>
    public string[]? PinnedProjects { get; init; }
    /// <summary>Sidebar groups folded by the user (project folders, and Chats by its root): their rows stay hidden until unfolded.</summary>
    public string[]? CollapsedProjects { get; init; }
    /// <summary>Names the user gave project folders in the sidebar (folder → name); a folder not here shows its own name.</summary>
    public Dictionary<string, string>? ProjectNames { get; init; }
    /// <summary>Session files archived from the sidebar: listed only under Archived (nothing is deleted on disk).</summary>
    public string[]? ArchivedSessions { get; init; }
    /// <summary>Chats explicitly marked unread by the user, until opened or marked read.</summary>
    public string[]? MarkedUnreadSessions { get; init; }
    /// <summary>The sidebar's width as last dragged (220–480 px); null means the default.</summary>
    public double? SidebarWidth { get; init; }
    /// <summary>Window placement and panel layout from the last normal close (client only).</summary>
    public WindowLayoutOptions? WindowLayout { get; init; }
    /// <summary>The command palette's entries last run, newest first (ids such as <c>settings:permissions</c>); listed first.</summary>
    public string[]? PaletteRecent { get; init; }
    /// <summary>"Don't ask again" rules for omp's approval requests (<see cref="ApprovalRuleSet"/>); session rules are never saved.</summary>
    public SavedApprovalRule[]? ApprovalRules { get; init; }
    /// <summary>omp's browser tool drives the app's built-in browser (default on): see
    /// <see cref="Browser.AgentBrowserDefaults"/>. Off: omp's own browser settings decide, the app adds nothing.</summary>
    public bool? AgentUsesGuiBrowser { get; init; }
    /// <summary>Settings › General › Agent browser: null (the built-in preview) or <c>nightly</c> (experimental, macOS:
    /// pages render in the embedded Nightly runtime).</summary>
    public string? AgentBrowserEngine { get; init; }
    /// <summary>Settings › Advanced › Corporate network certificates (<see cref="Network.CorporateTrust"/>): null (off),
    /// <c>system</c> (macOS: Bun trusts what macOS trusts) or <c>pem</c> (Bun also trusts <see cref="CorporateTrustBundle"/>).</summary>
    public string? CorporateTrust { get; init; }
    /// <summary>The app's own copy of the user's CA file (mode <c>pem</c>), in its settings folder.</summary>
    public string? CorporateTrustBundle { get; init; }
    /// <summary>Settings › Advanced › Network privacy (<see cref="Network.NetworkPrivacy"/>): omp and its tools reach only
    /// the added providers and <see cref="StrictNetworkAllowedHosts"/>, through the app's filter. Default off.</summary>
    public bool? StrictNetworkPrivacy { get; init; }
    /// <summary>Addresses the user lets through in strict mode besides the added providers (<c>host</c> or <c>*.domain</c>).</summary>
    public string[]? StrictNetworkAllowedHosts { get; init; }

    /// <summary>The environment omp inherits from the app, as launches see it (null: this process's; tests replace it).</summary>
    [JsonIgnore]
    public Func<string, string?>? InheritedEnvironment { get; init; }

    public const string ConfigEnvVar = "OMBRA_CONFIG";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IgnoreReadOnlyProperties = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Default local config: <c>%APPDATA%\Ombra\ombra.local.json</c> (XDG config dir elsewhere).</summary>
    public static string DefaultConfigPath =>
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData, System.Environment.SpecialFolderOption.DoNotVerify), "Ombra", "ombra.local.json");

    /// <summary>Loads from an explicit path, else <c>OMBRA_CONFIG</c>, else the default path; a missing default file means defaults.</summary>
    public static OmpRuntimeOptions Load(string? explicitPath = null)
    {
        var path = explicitPath ?? System.Environment.GetEnvironmentVariable(ConfigEnvVar);
        // An explicitly selected missing file is an error, not a request for default runtime settings.
        if (path is not null) _ = File.GetAttributes(path);
        return new ClientSettingsStore(path ?? DefaultConfigPath).Load();
    }

    public static readonly IReadOnlyList<string> ApprovalModes = ["always-ask", "write", "yolo"];
    public const string DefaultApprovalMode = "write";

    /// <summary>omp's own default is high: slow and costly for everyday chats, so a new chat starts at medium (the
    /// thinking chip changes it per chat).</summary>
    public const string DefaultThinkingLevelValue = "medium";
    public static readonly IReadOnlyList<string> ThinkingLevels = ["off", "minimal", "low", "medium", "high", "xhigh", "max", "auto"];

    /// <summary>The level a new session starts at: <see cref="DefaultThinkingLevel"/> when omp knows it, else medium.</summary>
    [JsonIgnore]
    public string EffectiveThinkingLevel => DefaultThinkingLevel is { } l && ThinkingLevels.Contains(l) ? l : DefaultThinkingLevelValue;

    /// <summary>
    /// The mode omp runs with: exactly one of <see cref="ApprovalModes"/>, else <see cref="DefaultApprovalMode"/>.
    /// Never left out: omp's own default is yolo, and it ignores an invalid flag with only a log line.
    /// </summary>
    public static string EffectiveApprovalMode(string? mode) => mode is not null && ApprovalModes.Contains(mode) ? mode : DefaultApprovalMode;

    /// <summary>A warning when the configured approval mode is not one omp knows (then "write" is used).</summary>
    [JsonIgnore]
    public string? ApprovalModeWarning =>
        ApprovalMode is not null && !ApprovalModes.Contains(ApprovalMode)
            ? "Unknown approval mode in the settings; using write (ask before commands)."
            : null;

    /// <summary>
    /// omp's own terminal UI (for features omp 18.2.0 offers only there: plan, goal, vibe…), started in a terminal
    /// panel. A separate omp process with its own session: it never writes the session the window has open.
    /// </summary>
    public OmpLaunchSpec ToTuiLaunchSpec(string? workingDirectory)
    {
        var rpc = ToLaunchSpec(new LaunchRequest(workingDirectory));
        // The same approval mode as the window's session (omp's own default is yolo), and the same thinking level as
        // a new chat's.
        var args = new List<string>(ManagementPrefixArgs) { "--approval-mode", EffectiveApprovalMode(ApprovalMode) };
        if (!ExtraArgs.Contains("--thinking")) args.AddRange(["--thinking", EffectiveThinkingLevel]);
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
        var args = new List<string>(ManagementPrefixArgs);
        args.AddRange(arguments);
        return rpc with { Arguments = args, SessionArguments = [] };
    }

    public OmpLaunchSpec ToLaunchSpec(LaunchRequest? request = null)
    {
        var args = new List<string> { "--approval-mode", EffectiveApprovalMode(request?.ApprovalMode ?? ApprovalMode) };
        if (request?.ResumeSessionFile is { } resume) args.AddRange(["--resume", resume]);
        // A new session starts at the app's level; a resumed one at its own (omp restores it from the file). A
        // --thinking among the user's extra arguments wins.
        else if (!ExtraArgs.Contains("--thinking")) args.AddRange(["--thinking", EffectiveThinkingLevel]);
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
            Arguments = PrefixArgs,
            SessionArguments = args,
            ResumeOnCurrentModel = request?.ResumeOnCurrentModel == true,
            WorkingDirectory = request?.WorkingDirectory ?? WorkingDirectory,
            Environment = env,
        };
    }
}

/// <summary>Position uses desktop pixels; sizes use device-independent pixels, as Avalonia windows do.</summary>
public sealed record WindowLayoutOptions
{
    public int X { get; init; }
    public int Y { get; init; }
    public double Width { get; init; } = 1200;
    public double Height { get; init; } = 800;
    public bool Maximized { get; init; }
    public bool SidebarVisible { get; init; } = true;
    public double RightPanelWidth { get; init; } = 480;
    public double EventsWidth { get; init; } = 420;
    public double TerminalHeight { get; init; } = 260;
    public string? ActivePane { get; init; }
    public bool EventsVisible { get; init; }
    public bool TerminalVisible { get; init; }
}
