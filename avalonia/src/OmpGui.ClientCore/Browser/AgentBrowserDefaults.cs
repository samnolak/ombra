using System.Text;
using System.Text.Json;
using OmpGui.Rpc;

namespace OmpGui.ClientCore.Browser;

/// <summary>Which of omp's browser backends the app serves to the omp processes it starts.</summary>
public enum AgentBrowserBackend
{
    /// <summary>omp's cmux backend (<see cref="AgentBrowserBridge"/>).</summary>
    Cmux,
    /// <summary>omp's Tern backend (<see cref="TernHost"/>), ranked above cmux by omp; the cmux variables stay as well.</summary>
    Tern,
}

/// <summary>
/// Makes the app's built-in browser the agent's default for every omp it starts (chats, the terminal's omp tab),
/// by omp's official mechanisms only (omp 18.8.0, pi-coding-agent src):
/// <list type="bullet">
/// <item>per-process environment: the Tern host's socket, a pane id of its own and <c>PI_BROWSER_TERN=1</c>
/// (<see cref="TernHost"/>; Tern ranks above cmux, tools/browser.ts), plus the cmux bridge's socket and
/// <c>PI_BROWSER_CMUX=1</c> (env wins over the user's <c>browser.cmux</c>, tools/browser/cmux/rpc.ts), and
/// <c>PI_BROWSER_RELAY=0</c> (the relay ranks above every other backend, tools/browser/relay/kind.ts);</item>
/// <item>a settings overlay the app owns, appended to <c>PI_CONFIG_FILES</c> (config/settings.ts: overlays load after
/// the global and project config and win over them; never written back): browser on, no <c>cdpUrl</c> (it ranks above
/// cmux and has no env override), the visible page never reaped after idling;</item>
/// <item>an extension passed with <c>--extension</c> (merged with the user's discovered extensions) whose
/// <c>before_agent_start</c> handler appends a short note to the system prompt: the page is visible to the user.
/// Not <c>--append-system-prompt</c>, which replaces the user's own APPEND_SYSTEM.md.</item>
/// </list>
/// The user's omp files are never touched. With the setting off (<see cref="OmpRuntimeOptions.AgentUsesGuiBrowser"/>)
/// omp starts exactly as its own configuration says.
/// </summary>
public sealed class AgentBrowserDefaults
{
    public const string ConfigFilesVariable = "PI_CONFIG_FILES";
    public const string RelayFlagVariable = "PI_BROWSER_RELAY";
    public const string ExtensionFlag = "--extension";
    public const string OverlayFileName = "omp-browser-overlay.yml";
    public const string ExtensionFileName = "omp-browser-note.js";

    /// <summary>A phrase only the note has: how the extension sees it is already in the prompt.</summary>
    public const string NoteMarker = "the OMP GUI's built-in browser";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AgentBrowserBridge _bridge;
    private readonly TernHost? _tern;
    private readonly Func<string, string?> _inherited;
    private readonly Lock _gate = new();

    /// <param name="bridge">Serves the page omp drives over cmux.</param>
    /// <param name="directory">The app's own folder for the overlay and the extension (created when missing).</param>
    /// <param name="tern">Serves it over Tern instead (omp prefers Tern when both are offered); null: cmux only.</param>
    /// <param name="inherited">The app's environment as omp inherits it (tests replace it).</param>
    public AgentBrowserDefaults(AgentBrowserBridge bridge, string directory, TernHost? tern = null, Func<string, string?>? inherited = null)
    {
        _bridge = bridge;
        _tern = tern;
        _inherited = inherited ?? System.Environment.GetEnvironmentVariable;
        Directory = directory;
        OverlayPath = Path.Combine(directory, OverlayFileName);
        ExtensionPath = Path.Combine(directory, ExtensionFileName);
    }

    public AgentBrowserBackend Backend => _tern is null ? AgentBrowserBackend.Cmux : AgentBrowserBackend.Tern;

    /// <summary>The app's variable that picks the backend: <c>tern</c> or <c>cmux</c> (else <see cref="DefaultBackend"/>).</summary>
    public const string BackendVariable = "OMPGUI_AGENT_BROWSER";

    /// <summary>The backend the app serves unless <see cref="BackendVariable"/> says otherwise.</summary>
    public const AgentBrowserBackend DefaultBackend = AgentBrowserBackend.Cmux;

    /// <summary>Which backend to serve: <see cref="BackendVariable"/> (from <paramref name="environment"/>, default the
    /// process's), else <see cref="DefaultBackend"/>.</summary>
    public static AgentBrowserBackend ChooseBackend(Func<string, string?>? environment = null) =>
        (environment ?? System.Environment.GetEnvironmentVariable)(BackendVariable)?.Trim().ToLowerInvariant() switch
        {
            "tern" => AgentBrowserBackend.Tern,
            "cmux" => AgentBrowserBackend.Cmux,
            _ => DefaultBackend,
        };

    public string Directory { get; }
    public string OverlayPath { get; }
    public string ExtensionPath { get; }

    /// <summary>A chat's omp (RPC).</summary>
    public OmpLaunchSpec ForSession(OmpRuntimeOptions options, LaunchRequest request) =>
        Apply(options.ToLaunchSpec(request), options.AgentUsesGuiBrowser != false);

    /// <summary>omp's own terminal UI in the terminal pane.</summary>
    public OmpLaunchSpec ForTui(OmpRuntimeOptions options, string? workingDirectory) =>
        Apply(options.ToTuiLaunchSpec(workingDirectory), options.AgentUsesGuiBrowser != false);

    /// <summary>
    /// <paramref name="spec"/> sent to the built-in browser; unchanged when <paramref name="enabled"/> is false or the
    /// user's settings environment names their own cmux or Tern socket. A flag the user set in the settings' environment
    /// (e.g. <c>PI_BROWSER_RELAY=1</c>) stays as they set it. With Tern, the cmux variables stay too (a Tern-less omp
    /// still finds the pane); with cmux only, <c>PI_BROWSER_TERN=0</c> keeps an inherited Tern pane from winning. When
    /// the app cannot write its files, omp still gets the sockets and the flags (without the overlay and the note), and
    /// a line goes to stderr.
    /// </summary>
    public OmpLaunchSpec Apply(OmpLaunchSpec spec, bool enabled)
    {
        if (!enabled) return spec;
        var withSocket = _bridge.AddTo(spec);
        if (ReferenceEquals(withSocket, spec)) return spec;
        if (_tern is not null)
        {
            var withTern = _tern.AddTo(withSocket);
            if (ReferenceEquals(withTern, withSocket)) return spec;
            withSocket = withTern;
        }
        var env = new Dictionary<string, string?>(withSocket.Environment);
        env.TryAdd(RelayFlagVariable, "0");
        if (_tern is null) env.TryAdd(TernHost.TernFlagVariable, "0");
        if (!TryWriteFiles()) return withSocket with { Environment = env };

        // The user's overlays first (settings environment, else inherited), ours last so it wins for the browser keys
        var existing = env.TryGetValue(ConfigFilesVariable, out var set) ? set : _inherited(ConfigFilesVariable);
        var files = (existing ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!files.Contains(OverlayPath)) files.Add(OverlayPath);
        env[ConfigFilesVariable] = string.Join(Path.PathSeparator, files);

        var args = new List<string>(withSocket.Arguments) { ExtensionFlag, ExtensionPath };
        return withSocket with { Arguments = args, Environment = env };
    }

    /// <summary>The overlay: only the browser keys, so every other omp setting stays the user's.</summary>
    public string OverlayYaml() =>
        "# Written by OMP GUI for the omp processes it starts (Settings > General > Agent uses the OMP GUI browser).\n"
        + "# omp reads it through PI_CONFIG_FILES only while OMP GUI starts it; your own omp config is never changed.\n"
        + "browser:\n"
        + "  enabled: true\n"
        + "  cdpUrl: \"\"\n"
        + "  cmux: true\n"
        + (_tern is null ? "" : "  tern: true\n")
        + "  idleCloseSec: 0\n";

    /// <summary>What the agent is told about the browser, for this backend.</summary>
    public string PromptNote()
    {
        var note = "# Browser\n"
            + "The `browser` object in eval drives " + NoteMarker + ": a pane in the app that the user watches live, not a hidden Chromium. "
            + "For any page the user should see or sign in to, and for anything on localhost, open it there with `browser.open`. "
            + "Never use `bash open`, `xdg-open`, `start` or another way to launch the system browser. "
            + "Pass `persist: true` when opening a tab for a login or another multi-step flow.";
        return Backend switch
        {
            AgentBrowserBackend.Cmux => note + "\nThis browser is a cmux surface: read pages with `tab.ariaSnapshot()` rather than `tab.observe()`; "
                + "dialogs, frames, downloads and device emulation are not available.",
            _ => note + "\nEach tab you open is a tab of that pane, driven with trusted native input. The user can close a tab: "
                + "when a request says so, open a new one if you still need the page. Sign-in forms and password prompts are the user's to fill in.",
        };
    }

    /// <summary>The extension: one <c>before_agent_start</c> handler adding <see cref="PromptNote"/> once.</summary>
    public string ExtensionSource() =>
        "// Written by OMP GUI for the omp processes it starts (Settings > General > Agent uses the OMP GUI browser).\n"
        + "// Tells the agent that the browser it drives is the app's built-in one, which the user sees.\n"
        + "const NOTE = " + JsonSerializer.Serialize(PromptNote()) + ";\n"
        + "const MARKER = " + JsonSerializer.Serialize(NoteMarker) + ";\n"
        + "export default function ompGuiBrowser(pi) {\n"
        + "\tpi.on(\"before_agent_start\", event => {\n"
        + "\t\tconst prompt = Array.isArray(event.systemPrompt) ? event.systemPrompt : [];\n"
        + "\t\tif (prompt.some(part => typeof part === \"string\" && part.includes(MARKER))) return undefined;\n"
        + "\t\treturn { systemPrompt: [...prompt, NOTE] };\n"
        + "\t});\n"
        + "}\n";

    /// <summary>Writes the overlay and the extension where they differ from what is on disk (omp fails to start when
    /// an overlay it was given is missing, so they are checked before every launch).</summary>
    private bool TryWriteFiles()
    {
        lock (_gate)
        {
            try
            {
                if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(Directory);
                else System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                WriteIfChanged(OverlayPath, OverlayYaml());
                WriteIfChanged(ExtensionPath, ExtensionSource());
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("The agent's browser settings could not be written (" + e.Message + "); omp starts without them.");
                return false;
            }
        }
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path, Utf8) == content) return;
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, Utf8);
        File.Move(temp, path, overwrite: true);
    }
}
