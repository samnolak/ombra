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
    /// <summary>The keyboard shortcut sheet.</summary>
    Shortcuts,
    /// <summary>The GUI does it another way; the card says how (no terminal needed).</summary>
    Explain,
}

/// <summary>A builtin slash command of omp 18.2.0 without an RPC handler, and its answer in the GUI.</summary>
/// <param name="Description">omp's own description (builtin-*.ts), shown in the slash menu.</param>
/// <param name="Explanation">For <see cref="GuiEquivalent.Explain"/>: how the GUI does it.</param>
public sealed record TerminalOnlyCommand(string Name, string Description, GuiEquivalent Gui, string Explanation = "");

/// <summary>
/// omp's terminal-only builtins (the specs with only <c>handleTui</c> in slash-commands/builtin-*.ts; the same set as
/// <see cref="OmpGui.ClientCore.OmpBuiltins.TuiOnly"/>). Sent over RPC they would reach the model as a message, so
/// the message box handles them itself: the GUI's own equivalent where there is one, else omp's terminal.
/// </summary>
public static class TerminalOnlyCommands
{
    private const string Quit = "Close the window to quit: omp stops with it, and the conversation is saved.";

    public static readonly IReadOnlyList<TerminalOnlyCommand> All =
    [
        new("settings", "Open settings menu", GuiEquivalent.Settings),
        new("setup", "Open provider setup", GuiEquivalent.Providers),
        new("providers", "Open provider setup", GuiEquivalent.Providers),
        new("login", "Login with OAuth provider", GuiEquivalent.Providers),
        new("new", "Start a new session", GuiEquivalent.NewSession),
        new("resume", "Resume a different session", GuiEquivalent.Sessions),
        new("restart", "Restart omp with the same launch flags, resuming this session", GuiEquivalent.RestartOmp),
        new("extensions", "Open Extension Control Center dashboard", GuiEquivalent.Plugins),
        new("status", "Open Extension Control Center dashboard", GuiEquivalent.Plugins),
        new("hub", "Open the live Agent Hub", GuiEquivalent.BackgroundTasks),
        new("hotkeys", "Show all keyboard shortcuts", GuiEquivalent.Shortcuts),
        new("quit", "Quit the application", GuiEquivalent.Explain, Quit),
        new("q", "Quit the application", GuiEquivalent.Explain, Quit),
        new("exit", "Exit the application", GuiEquivalent.Explain, Quit),
        new("copy", "Pick text or code from the conversation to copy", GuiEquivalent.Explain,
            "Select text in the conversation, or use the copy button under a reply or a code block."),
        new("open", "Open the last link from the conversation in your browser", GuiEquivalent.Explain,
            "Links in the conversation open in your browser when you click them."),
        new("queue", "Queue a message for after the agent yields", GuiEquivalent.Explain,
            "While omp works, Enter queues your message for when it finishes, and Alt+Enter hands it over at its next step."),
        new("plan", "Toggle plan mode (agent plans before executing)", GuiEquivalent.Terminal),
        new("plan-review", "Re-open the plan review for the latest plan (plan mode only)", GuiEquivalent.Terminal),
        new("vibe", "Toggle vibe mode (direct persistent fast/good worker sessions; read-only toolset)", GuiEquivalent.Terminal),
        new("goal", "Toggle goal mode (persistent autonomous objective for this session)", GuiEquivalent.Terminal),
        new("guided-goal", "Have the agent interview you in chat, then set up goal mode", GuiEquivalent.Terminal),
        new("loop", "Toggle loop mode: the next prompt re-submits after every yield", GuiEquivalent.Terminal),
        new("agents", "Open the agents hub (per-agent model, prewalk, and advisor)", GuiEquivalent.Terminal),
        new("git", "Open the git UI (split diff viewer, staging, commit composer)", GuiEquivalent.Terminal),
        new("branch", "Rewind to a previous message, keeping the old path as a branch", GuiEquivalent.Rewind),
        new("rewind", "Rewind to a previous message, keeping the old path as a branch", GuiEquivalent.Rewind),
        new("fork", "Create a new fork from a previous message", GuiEquivalent.Terminal),
        new("tree", "Navigate session tree (switch branches)", GuiEquivalent.Terminal),
        new("logout", "Logout from OAuth provider", GuiEquivalent.Terminal),
        new("clear", "Clear the conversation context in place, keeping the session", GuiEquivalent.Terminal),
        new("drop", "Delete the current session and start a new one", GuiEquivalent.DeleteSession),
        new("btw", "Ask a side question, or browse this session's BTW history", GuiEquivalent.Terminal),
        new("tan", "Run a full background agent on tangential work", GuiEquivalent.Terminal),
        new("omfg", "Forge a TTSR rule from a complaint to stop a recurring behavior", GuiEquivalent.Terminal),
        new("cleanse", "Detect and fix project diagnostics with weighted parallel subagents", GuiEquivalent.Terminal),
        new("debug", "Open debug tools selector", GuiEquivalent.Terminal),
        new("collab", "Share this session live via a relay", GuiEquivalent.Terminal),
        new("join", "Join a shared collab session", GuiEquivalent.Terminal),
        new("leave", "Leave the collab session", GuiEquivalent.Terminal),
        new("live", "Start Codex-backed realtime voice mode", GuiEquivalent.Terminal),
        new("pause", "Freeze all agents (main, subagents, advisor) until resumed", GuiEquivalent.Terminal),
    ];

    private static readonly Dictionary<string, TerminalOnlyCommand> ByName = All.ToDictionary(c => c.Name, StringComparer.Ordinal);

    public static TerminalOnlyCommand? Find(string name) => ByName.GetValueOrDefault(name);
}
