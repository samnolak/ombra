namespace OmpGui.App.ViewModels;

/// <summary>What the GUI does instead of a command that exists only in omp's terminal UI.</summary>
public enum GuiEquivalent
{
    /// <summary>None: the command runs in omp's terminal (a separate omp in the terminal panel).</summary>
    Terminal,
    Settings,
    Providers,
    Plugins,
    NewSession,
    Sessions,
    RestartOmp,
    BackgroundTasks,
    /// <summary>The session menu's Delete session (omp's /session delete, then a new session), after a confirmation.</summary>
    DeleteSession,
    /// <summary>The session menu's Rewind (omp's RPC <c>branch</c>): pick a message, a new session from before it.</summary>
    Rewind,
    /// <summary>The GUI does it another way; the card says how (no terminal needed).</summary>
    Explain,
}

/// <summary>A builtin slash command of omp 18.2.0 without an RPC handler, and its answer in the GUI.</summary>
/// <param name="Description">omp's own description (builtin-*.ts), shown in the slash menu.</param>
/// <param name="Hint">Where it goes in the GUI, for the slash menu's right column ("terminal", "Settings", …).</param>
/// <param name="Explanation">For <see cref="GuiEquivalent.Explain"/>: how the GUI does it.</param>
public sealed record TerminalOnlyCommand(string Name, string Description, GuiEquivalent Gui, string Hint, string Explanation = "");

/// <summary>
/// omp's terminal-only builtins (the specs with only <c>handleTui</c> in slash-commands/builtin-*.ts; the same set as
/// <see cref="OmpGui.ClientCore.OmpBuiltins.TuiOnly"/>). Sent over RPC they would reach the model as a message, so
/// the message box handles them itself: the GUI's own equivalent where there is one, else omp's terminal.
/// </summary>
public static class TerminalOnlyCommands
{
    public static readonly IReadOnlyList<TerminalOnlyCommand> All =
    [
        new("settings", "Open settings menu", GuiEquivalent.Settings, "Settings"),
        new("setup", "Open provider setup", GuiEquivalent.Providers, "Settings"),
        new("providers", "Open provider setup", GuiEquivalent.Providers, "Settings"),
        new("login", "Login with OAuth provider", GuiEquivalent.Providers, "Settings"),
        new("new", "Start a new session", GuiEquivalent.NewSession, "new session"),
        new("resume", "Resume a different session", GuiEquivalent.Sessions, "sessions"),
        new("restart", "Restart omp with the same launch flags, resuming this session", GuiEquivalent.RestartOmp, "restart omp"),
        new("extensions", "Open Extension Control Center dashboard", GuiEquivalent.Plugins, "Settings"),
        new("status", "Open Extension Control Center dashboard", GuiEquivalent.Plugins, "Settings"),
        new("hub", "Open the live Agent Hub", GuiEquivalent.BackgroundTasks, "tasks"),
        new("quit", "Quit the application", GuiEquivalent.Explain, "window",
            "Close the window to quit: omp stops with it, and the conversation is saved."),
        new("q", "Quit the application", GuiEquivalent.Explain, "window",
            "Close the window to quit: omp stops with it, and the conversation is saved."),
        new("exit", "Exit the application", GuiEquivalent.Explain, "window",
            "Close the window to quit: omp stops with it, and the conversation is saved."),
        new("copy", "Pick text or code from the conversation to copy", GuiEquivalent.Explain, "copy button",
            "Select text in the conversation, or use the copy button under a reply or a code block."),
        new("open", "Open the last link from the conversation in your browser", GuiEquivalent.Explain, "links",
            "Links in the conversation open in your browser when you click them."),
        new("queue", "Queue a message for after the agent yields", GuiEquivalent.Explain, "Enter",
            "While omp works, Enter queues your message for when it finishes, and Alt+Enter hands it over at its next step."),
        new("hotkeys", "Show all keyboard shortcuts", GuiEquivalent.Explain, "shortcuts",
            "Enter sends · Shift+Enter adds a line · Alt+Enter steers a running reply · Esc stops · Ctrl+N new session · " +
            "Ctrl+B sessions · Ctrl+` terminal · Ctrl+Shift+B browser · F12 events. omp's own terminal keys are in its terminal."),
        new("plan", "Toggle plan mode (agent plans before executing)", GuiEquivalent.Terminal, "terminal"),
        new("plan-review", "Re-open the plan review for the latest plan (plan mode only)", GuiEquivalent.Terminal, "terminal"),
        new("vibe", "Toggle vibe mode (direct persistent fast/good worker sessions; read-only toolset)", GuiEquivalent.Terminal, "terminal"),
        new("goal", "Toggle goal mode (persistent autonomous objective for this session)", GuiEquivalent.Terminal, "terminal"),
        new("guided-goal", "Have the agent interview you in chat, then set up goal mode", GuiEquivalent.Terminal, "terminal"),
        new("loop", "Toggle loop mode: the next prompt re-submits after every yield", GuiEquivalent.Terminal, "terminal"),
        new("agents", "Open the agents hub (per-agent model, prewalk, and advisor)", GuiEquivalent.Terminal, "terminal"),
        new("git", "Open the git UI (split diff viewer, staging, commit composer)", GuiEquivalent.Terminal, "terminal"),
        new("branch", "Rewind to a previous message, keeping the old path as a branch", GuiEquivalent.Rewind, "session menu"),
        new("rewind", "Rewind to a previous message, keeping the old path as a branch", GuiEquivalent.Rewind, "session menu"),
        new("fork", "Create a new fork from a previous message", GuiEquivalent.Terminal, "terminal"),
        new("tree", "Navigate session tree (switch branches)", GuiEquivalent.Terminal, "terminal"),
        new("logout", "Logout from OAuth provider", GuiEquivalent.Terminal, "terminal"),
        new("clear", "Clear the conversation context in place, keeping the session", GuiEquivalent.Terminal, "terminal"),
        new("drop", "Delete the current session and start a new one", GuiEquivalent.DeleteSession, "session menu"),
        new("btw", "Ask a side question, or browse this session's BTW history", GuiEquivalent.Terminal, "terminal"),
        new("tan", "Run a full background agent on tangential work", GuiEquivalent.Terminal, "terminal"),
        new("omfg", "Forge a TTSR rule from a complaint to stop a recurring behavior", GuiEquivalent.Terminal, "terminal"),
        new("cleanse", "Detect and fix project diagnostics with weighted parallel subagents", GuiEquivalent.Terminal, "terminal"),
        new("debug", "Open debug tools selector", GuiEquivalent.Terminal, "terminal"),
        new("collab", "Share this session live via a relay", GuiEquivalent.Terminal, "terminal"),
        new("join", "Join a shared collab session", GuiEquivalent.Terminal, "terminal"),
        new("leave", "Leave the collab session", GuiEquivalent.Terminal, "terminal"),
        new("live", "Start Codex-backed realtime voice mode", GuiEquivalent.Terminal, "terminal"),
        new("pause", "Freeze all agents (main, subagents, advisor) until resumed", GuiEquivalent.Terminal, "terminal"),
    ];

    private static readonly Dictionary<string, TerminalOnlyCommand> ByName = All.ToDictionary(c => c.Name, StringComparer.Ordinal);

    public static TerminalOnlyCommand? Find(string name) => ByName.GetValueOrDefault(name);
}
