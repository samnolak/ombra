using Ombra.App.Localization;

namespace Ombra.App.ViewModels;

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
    /// <summary>The session menu's Fork (omp's RPC <c>fork</c>): the chat goes on in a copy of the session; a reply's
    /// "Fork from here" forks at that reply.</summary>
    Fork,
    /// <summary>The keyboard shortcut sheet.</summary>
    Shortcuts,
    /// <summary>Plan mode over RPC (MainViewModel.PlanMode.cs) when this omp has it; else omp's terminal.</summary>
    PlanMode,
    /// <summary>Goal mode over RPC (MainViewModel.BtwGoal.cs: the goal bar) when this omp has it; else omp's terminal.</summary>
    Goal,
    /// <summary>Side questions over RPC (MainViewModel.BtwGoal.cs: the BTW panel) when this omp has them; else omp's terminal.</summary>
    Btw,
    /// <summary>The GUI does it another way; the card says how (no terminal needed).</summary>
    Explain,
}

/// <summary>A builtin slash command of omp 18.2.0 without an RPC handler, and its answer in the GUI.</summary>
/// <param name="Description">omp's own description (builtin-*.ts), shown in the slash menu.</param>
/// <param name="Explanation">For <see cref="GuiEquivalent.Explain"/>: how the GUI does it.</param>
public sealed record TerminalOnlyCommand(string Name, string Description, GuiEquivalent Gui, string Explanation = "");

/// <summary>
/// omp's terminal-only builtins (the specs with only <c>handleTui</c> in slash-commands/builtin-*.ts; the same set as
/// <see cref="Ombra.ClientCore.OmpBuiltins.TuiOnly"/>). Sent over RPC they would reach the model as a message, so
/// the message box handles them itself: the GUI's own equivalent where there is one, else omp's terminal.
/// </summary>
public static class TerminalOnlyCommands
{
    private static string Quit => Loc.T("Close the window to quit: omp stops with it, and the conversation is saved.");

    public static readonly IReadOnlyList<TerminalOnlyCommand> All =
    [
        new("settings", Loc.T("Open settings menu"), GuiEquivalent.Settings),
        new("setup", Loc.T("Open provider setup"), GuiEquivalent.Providers),
        new("providers", Loc.T("Open provider setup"), GuiEquivalent.Providers),
        new("login", Loc.T("Login with OAuth provider"), GuiEquivalent.Providers),
        new("new", Loc.T("Start a new session"), GuiEquivalent.NewSession),
        new("resume", Loc.T("Resume a different session"), GuiEquivalent.Sessions),
        new("restart", Loc.T("Restart omp with the same launch flags, resuming this session"), GuiEquivalent.RestartOmp),
        new("extensions", Loc.T("Open Extension Control Center dashboard"), GuiEquivalent.Plugins),
        new("status", Loc.T("Open Extension Control Center dashboard"), GuiEquivalent.Plugins),
        new("hub", Loc.T("Open the live Agent Hub"), GuiEquivalent.BackgroundTasks),
        new("hotkeys", Loc.T("Show all keyboard shortcuts"), GuiEquivalent.Shortcuts),
        new("quit", Loc.T("Quit the application"), GuiEquivalent.Explain, Quit),
        new("q", Loc.T("Quit the application"), GuiEquivalent.Explain, Quit),
        new("exit", Loc.T("Exit the application"), GuiEquivalent.Explain, Quit),
        new("copy", Loc.T("Pick text or code from the conversation to copy"), GuiEquivalent.Explain,
            Loc.T("Select text in the conversation, or use the copy button under a reply or a code block.")),
        new("open", Loc.T("Open the last link from the conversation in your browser"), GuiEquivalent.Explain,
            Loc.T("Links in the conversation open in your browser when you click them.")),
        new("queue", Loc.T("Queue a message for after the agent yields"), GuiEquivalent.Explain,
            Loc.T("While omp works, Enter queues your message for when it finishes, and Alt+Enter hands it over at its next step.")),
        new("plan", Loc.T("Toggle plan mode (agent plans before executing)"), GuiEquivalent.PlanMode),
        new("plan-review", Loc.T("Re-open the plan review for the latest plan (plan mode only)"), GuiEquivalent.PlanMode),
        new("vibe", Loc.T("Toggle vibe mode (direct persistent fast/good worker sessions; read-only toolset)"), GuiEquivalent.Terminal),
        new("goal", Loc.T("Toggle goal mode (persistent autonomous objective for this session)"), GuiEquivalent.Goal),
        new("guided-goal", Loc.T("Have the agent interview you in chat, then set up goal mode"), GuiEquivalent.Terminal),
        new("loop", Loc.T("Toggle loop mode: the next prompt re-submits after every yield"), GuiEquivalent.Terminal),
        new("agents", Loc.T("Open the agents hub (per-agent model, prewalk, and advisor)"), GuiEquivalent.Terminal),
        new("git", Loc.T("Open the git UI (split diff viewer, staging, commit composer)"), GuiEquivalent.Terminal),
        new("branch", Loc.T("Rewind to a previous message, keeping the old path as a branch"), GuiEquivalent.Rewind),
        new("rewind", Loc.T("Rewind to a previous message, keeping the old path as a branch"), GuiEquivalent.Rewind),
        new("fork", Loc.T("Create a new fork from a previous message"), GuiEquivalent.Fork),
        new("tree", Loc.T("Navigate session tree (switch branches)"), GuiEquivalent.Terminal),
        new("logout", Loc.T("Logout from OAuth provider"), GuiEquivalent.Providers),
        new("clear", Loc.T("Clear the conversation context in place, keeping the session"), GuiEquivalent.Terminal),
        new("drop", Loc.T("Delete the current session and start a new one"), GuiEquivalent.DeleteSession),
        new("btw", Loc.T("Ask a side question, or browse this session's BTW history"), GuiEquivalent.Btw),
        new("tan", Loc.T("Run a full background agent on tangential work"), GuiEquivalent.Terminal),
        new("omfg", Loc.T("Forge a TTSR rule from a complaint to stop a recurring behavior"), GuiEquivalent.Terminal),
        new("cleanse", Loc.T("Detect and fix project diagnostics with weighted parallel subagents"), GuiEquivalent.Terminal),
        new("debug", Loc.T("Open debug tools selector"), GuiEquivalent.Terminal),
        new("collab", Loc.T("Share this session live via a relay"), GuiEquivalent.Terminal),
        new("join", Loc.T("Join a shared collab session"), GuiEquivalent.Terminal),
        new("leave", Loc.T("Leave the collab session"), GuiEquivalent.Terminal),
        new("live", Loc.T("Start Codex-backed realtime voice mode"), GuiEquivalent.Terminal),
        new("pause", Loc.T("Freeze all agents (main, subagents, advisor) until resumed"), GuiEquivalent.Terminal),
    ];

    private static readonly Dictionary<string, TerminalOnlyCommand> ByName = All.ToDictionary(c => c.Name, StringComparer.Ordinal);

    public static TerminalOnlyCommand? Find(string name) => ByName.GetValueOrDefault(name);
}
