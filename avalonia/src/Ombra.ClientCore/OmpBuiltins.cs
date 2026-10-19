namespace Ombra.ClientCore;

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

    /// <summary>The builtin parser's name, ending at the first whitespace or colon (omp 18.8.0 helpers/parse.ts).</summary>
    public static string? NameOf(string text)
    {
        if (text.Length < 2 || text[0] != '/') return null;
        var end = 1;
        while (end < text.Length && !IsRuntimeWhitespace(text[end]) && text[end] != ':') end++;
        return end == 1 ? null : text[1..end];
    }

    // JavaScript /\s/: unlike Char.IsWhiteSpace, BOM is a separator and NEXT LINE is not.
    private static bool IsRuntimeWhitespace(char value) => value == '\uFEFF' || value != '\u0085' && char.IsWhiteSpace(value);

    /// <summary>True when omp runs <paramref name="text"/> itself over RPC (instead of sending it to the model).</summary>
    public static bool RunsOverRpc(string text) => NameOf(text) is { } n && OverRpc.Contains(n);

    /// <summary>
    /// True when omp runs <paramref name="text"/> itself according to the commands it listed (get_available_commands
    /// lists exactly the builtins it runs over RPC). An omp that lacks the builtin would hand the text to the model as
    /// a message, so a command it does not list must never be sent. Only an empty catalog (not read yet) falls back
    /// to <see cref="OverRpc"/>. Catalog names and aliases are authoritative, including new runtime builtins.
    /// </summary>
    public static bool RunsOverRpc(string text, IReadOnlyList<SlashCommand> catalog)
    {
        if (NameOf(text) is not { } name) return false;
        if (catalog.Count == 0) return OverRpc.Contains(name);
        return catalog.Any(c => c.Source == "builtin" && (c.Name == name || c.Aliases.Contains(name)));
    }
}

/// <summary>
/// What a builtin slash command printed, and whether it started an agent run (e.g. /retry, /handoff).
/// <paramref name="Unsupported"/>: this omp has no such command, so it was never sent (an optional feature is missing).
/// </summary>
public sealed record SlashCommandResult(bool Ok, string Output, bool AgentInvoked = false, string? Error = null, bool Unsupported = false);
