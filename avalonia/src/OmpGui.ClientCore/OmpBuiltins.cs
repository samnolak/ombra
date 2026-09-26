namespace OmpGui.ClientCore;

/// <summary>
/// omp's builtin slash commands as omp 18.2.0 runs them over RPC. A builtin with a handler runs inside omp when it is
/// sent as a prompt and prints its result as <c>command_output</c>; the others (settings, plan, login, branch…) exist
/// only in omp's terminal UI, and sent over RPC they would reach the model as an ordinary message.
/// Source: packages/coding-agent/src/slash-commands/builtin-*.ts (the specs with <c>handle</c>) and rpc-mode.ts.
/// </summary>
public static class OmpBuiltins
{
    /// <summary>Builtins (and aliases) that run over RPC.</summary>
    public static readonly IReadOnlySet<string> OverRpc = new HashSet<string>(StringComparer.Ordinal)
    {
        // collaboration
        "advisor", "export", "trace", "dump", "share", "browser",
        // control
        "force",
        // lifecycle
        "ssh", "fresh", "compact", "shake", "handoff", "pin", "retry", "memory", "rename", "move", "wt", "worktree",
        "add-dir", "remove-dir", "dirs",
        // marketplace
        "marketplace", "plugins", "reload-plugins", // "install" is a subcommand (/marketplace install), not a builtin
        // modes
        "security", "model", "models", "switch", "fast", "skillful", "extended-context", "computer", "prewalk",
        // session
        "todo", "session", "jobs", "usage", "stats", "changelog", "tools", "context", "mcp", // not "append" (/todo append) nor "smithery-search" (/mcp smithery-search)
    };

    /// <summary>Builtins that exist only in omp's terminal UI (the GUI offers them in the omp terminal, or its own way).</summary>
    public static readonly IReadOnlySet<string> TuiOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "collab", "join", "leave", "copy", "open", "live", "pause", "quit", "q", "new", "clear", "drop", "resume", "btw",
        "tan", "omfg", "cleanse", "debug", "exit", "restart", "settings", "setup", "providers", "plan", "plan-review",
        "vibe", "goal", "guided-goal", "loop", "queue", "hotkeys", "extensions", "status", "agents", "git", "hub",
        "branch", "rewind", "fork", "tree", "login", "logout",
    };

    /// <summary>The command's name: "mcp" for "/mcp add x --url …"; null when the text is not a slash command.</summary>
    public static string? NameOf(string text)
    {
        var t = text.TrimStart();
        if (t.Length < 2 || t[0] != '/') return null;
        var end = 1;
        while (end < t.Length && !char.IsWhiteSpace(t[end])) end++;
        return t[1..end];
    }

    /// <summary>True when omp runs <paramref name="text"/> itself over RPC (instead of sending it to the model).</summary>
    public static bool RunsOverRpc(string text) => NameOf(text) is { } n && OverRpc.Contains(n);
}

/// <summary>What a builtin slash command printed, and whether it started an agent run (e.g. /retry, /handoff).</summary>
public sealed record SlashCommandResult(bool Ok, string Output, bool AgentInvoked = false, string? Error = null);
