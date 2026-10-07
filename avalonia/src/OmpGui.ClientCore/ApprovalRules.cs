using System.Text.RegularExpressions;

namespace OmpGui.ClientCore;

/// <summary>How long a "don't ask again" rule lasts.</summary>
public enum ApprovalScope
{
    /// <summary>The omp session it was given in, until the app quits (never saved).</summary>
    Session,
    /// <summary>Every session in one project folder (saved in the client's settings).</summary>
    Project,
    /// <summary>Every session in every project (saved in the client's settings).</summary>
    Always,
}

/// <summary>
/// What one approval request asks for, read from omp's prompt (tools/approval.ts formatApprovalPrompt):
/// "Allow tool: bash", then optional "Origin: …" / "Reason: …" lines, then the tool's details (bash: "Command: …").
/// </summary>
/// <param name="Command">bash's command (everything after "Command: "); null for other tools.</param>
/// <param name="Truncated">omp shortened the command for the prompt ("[…Nch elided…]"): what runs is not all shown.</param>
/// <param name="SafetyChecks">The provider asked for its own safety checks: only a person may confirm those.</param>
public sealed record ApprovalRequest(string Tool, string? Command, bool Truncated, bool SafetyChecks)
{
    private const string ToolPrefix = "Allow tool: ";
    private const string CommandLabel = "Command: ";
    // truncateForPrompt's mark at the end of a shortened value
    private static readonly Regex Elided = new(@"\[…\d+ch elided…\]", RegexOptions.CultureInvariant);

    public static ApprovalRequest? From(PendingDialog dialog)
    {
        if (dialog.Kind != DialogKind.Approval || !dialog.Headline.StartsWith(ToolPrefix, StringComparison.Ordinal)) return null;
        var tool = dialog.Headline[ToolPrefix.Length..].Trim();
        if (tool.Length == 0) return null;
        var details = dialog.Details;
        var safety = details.Contains("Provider safety checks:", StringComparison.Ordinal);
        string? command = null;
        if (tool == "bash")
        {
            // bash.ts formatApprovalDetails: one "Command: …" detail, after omp's own Origin/Reason lines
            var at = details.StartsWith(CommandLabel, StringComparison.Ordinal) ? 0
                : details.IndexOf("\n" + CommandLabel, StringComparison.Ordinal) is var i and >= 0 ? i + 1 : -1;
            if (at >= 0) command = details[(at + CommandLabel.Length)..].Trim();
        }
        var truncated = command is not null && Elided.IsMatch(command);
        return new ApprovalRequest(tool, command is { Length: > 0 } ? command : null, truncated, safety);
    }
}

/// <summary>
/// One "don't ask again" rule, written as Claude Code writes permission rules: <c>bash(npm test:*)</c> (commands starting
/// with "npm test"), <c>bash(npm test)</c> (exactly that command), <c>write</c> (every request of that tool).
/// </summary>
public sealed record ApprovalRule(string Tool, string? Command, bool Prefix)
{
    private static readonly Regex Syntax = new(@"^\s*([A-Za-z0-9_.\-]+)\s*(?:\((.*)\))?\s*$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Commands offered as exact rules only, never as a prefix: wrappers (sudo, env, bash -c…), where whatever follows is a
    /// program of its own, and destructive ones, where "rm" for <c>rm -rf build</c> would also cover <c>rm -rf ~</c>.
    /// </summary>
    private static readonly HashSet<string> ExactOnly = new(StringComparer.Ordinal)
    {
        "sudo", "doas", "su", "env", "xargs", "exec", "eval", "command", "builtin", "nohup", "time", "timeout", "nice", "watch",
        "bash", "sh", "zsh", "fish", "dash", "ksh", "pwsh", "powershell", "cmd", "source", ".",
        "rm", "rmdir", "mv", "dd", "shred", "mkfs", "chmod", "chown", "chgrp", "kill", "killall", "pkill", "shutdown", "reboot",
    };

    /// <summary>Tools whose second word is a subcommand (<c>git status</c>, <c>npm test</c>): a prefix rule keeps it.</summary>
    private static readonly HashSet<string> WithSubcommands = new(StringComparer.Ordinal)
    {
        "git", "npm", "pnpm", "yarn", "bun", "deno", "cargo", "go", "dotnet", "docker", "kubectl", "helm", "terraform", "gh",
        "brew", "pip", "pip3", "uv", "poetry", "rustup", "swift", "mvn", "gradle", "make", "just", "nx", "turbo", "mix", "flutter",
    };

    /// <summary>Package runners whose third word names the script (<c>npm run build</c>).</summary>
    private static readonly HashSet<string> ScriptRunners = new(StringComparer.Ordinal) { "run", "exec", "x", "dlx" };

    private static readonly Regex Subcommand = new("^[a-z][a-z0-9_:-]*$", RegexOptions.CultureInvariant);

    /// <summary>The rule as text (what the settings file and the Settings page show).</summary>
    public string Text => Command is null ? Tool : $"{Tool}({Command}{(Prefix ? ":*" : "")})";

    public override string ToString() => Text;

    /// <summary>Reads <see cref="Text"/>; null for anything else (an argument on a tool other than bash, an empty command).</summary>
    public static ApprovalRule? Parse(string? text)
    {
        if (text is null || Syntax.Match(text) is not { Success: true } m) return null;
        var tool = m.Groups[1].Value;
        if (!m.Groups[2].Success) return new ApprovalRule(tool, null, false);
        if (tool != "bash") return null;
        var inner = m.Groups[2].Value.Trim();
        var prefix = inner.EndsWith(":*", StringComparison.Ordinal);
        if (prefix) inner = inner[..^2].TrimEnd();
        return inner.Length == 0 ? null : new ApprovalRule(tool, inner, prefix);
    }

    /// <summary>
    /// Whether this rule answers <paramref name="request"/>. A command rule needs the whole command in view (not shortened
    /// by omp); a prefix rule also needs one plain command — anything chained (<c>;</c> <c>&amp;&amp;</c> <c>|</c>), redirected,
    /// substituted or on several lines is asked, whatever it starts with — and matches only at a word boundary
    /// (<c>npm test</c> covers <c>npm test --watch</c>, not <c>npm testing</c>).
    /// </summary>
    public bool Matches(ApprovalRequest request)
    {
        if (request.SafetyChecks || !string.Equals(Tool, request.Tool, StringComparison.Ordinal)) return false;
        if (Command is null) return true;
        if (request.Command is not { } command || request.Truncated) return false;
        if (!Prefix) return command == Command;
        return IsPlainCommand(command) && (command == Command || command.StartsWith(Command + " ", StringComparison.Ordinal));
    }

    /// <summary>One simple command: no chaining, pipes, redirection, substitution, subshells, or more than one line.</summary>
    public static bool IsPlainCommand(string command) =>
        command.Length > 0
        && command.IndexOfAny([';', '&', '|', '<', '>', '`', '(', ')', '\n', '\r', '\t']) < 0
        && !command.Contains("$(", StringComparison.Ordinal)
        && !command.Contains("${", StringComparison.Ordinal);

    /// <summary>
    /// The rule offered on the card: for a plain command its first word (and the subcommand of tools that have one:
    /// <c>git status:*</c>, <c>npm run build:*</c>); for anything else — chained, an environment assignment, a wrapper
    /// or a destructive command (<see cref="ExactOnly"/>) — exactly that command; for other tools the tool.
    /// Null when no rule fits: omp shortened the command, or the provider's own safety checks are part of the request.
    /// </summary>
    public static ApprovalRule? Suggest(ApprovalRequest? request)
    {
        if (request is null || request.SafetyChecks) return null;
        if (request.Tool != "bash") return new ApprovalRule(request.Tool, null, false);
        if (request.Command is not { } command || request.Truncated) return null;
        if (!IsPlainCommand(command)) return new ApprovalRule("bash", command, false);
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // An environment assignment, a wrapper (sudo, env, bash -c…) or a destructive command (rm): that command only
        if (words[0].Contains('=', StringComparison.Ordinal) || words[0].Contains('$', StringComparison.Ordinal) || ExactOnly.Contains(words[0]))
            return new ApprovalRule("bash", command, false);
        var take = 1;
        if (words.Length > 1 && WithSubcommands.Contains(words[0]) && Subcommand.IsMatch(words[1]))
        {
            take = 2;
            if (words.Length > 2 && ScriptRunners.Contains(words[1]) && Subcommand.IsMatch(words[2])) take = 3;
        }
        return new ApprovalRule("bash", string.Join(' ', words.Take(take)), true);
    }

    /// <summary>The rule in words: "commands starting with “npm test”", "writing files".</summary>
    public string Description => Command is { } c
        ? Prefix ? $"commands starting with “{c}”" : $"the command “{Shorten(c)}”"
        : Tool switch
        {
            "bash" => "every command",
            "write" => "writing files",
            "edit" or "ast_edit" => "edits",
            "read" => "reading files",
            "eval" => "running code",
            "fetch" or "browser" => "opening web pages",
            "task" => "starting subagents",
            _ when Tool.StartsWith("mcp__", StringComparison.Ordinal) && Tool.Split("__") is [_, var server, .. var rest] && rest.Length > 0
                => $"“{string.Join("__", rest)}” from {server}",
            _ => $"the {Tool} tool",
        };

    private static string Shorten(string s)
    {
        var line = s.Split('\n')[0];
        return line.Length > 60 || line.Length < s.Length ? line[..Math.Min(line.Length, 60)].TrimEnd() + "…" : line;
    }
}

/// <summary>A rule as it is in force: where it applies.</summary>
/// <param name="Project">The project folder of a <see cref="ApprovalScope.Project"/> rule.</param>
/// <param name="Session">The omp session (its file) of a <see cref="ApprovalScope.Session"/> rule.</param>
public sealed record ApprovalGrant(ApprovalRule Rule, ApprovalScope Scope, string? Project = null, string? Session = null)
{
    public string ScopeLabel => Scope switch
    {
        ApprovalScope.Session => "this session",
        ApprovalScope.Project => "this project",
        _ => "always",
    };

    /// <summary>"commands starting with “npm test” (this project)".</summary>
    public string Describe() => $"{Rule.Description} ({ScopeLabel})";

    public bool AppliesTo(string? cwd, string? session) => Scope switch
    {
        ApprovalScope.Session => session is not null && Session == session,
        ApprovalScope.Project => SessionCatalog.SamePath(Project, cwd),
        _ => true,
    };
}

/// <summary>A saved rule in the client's settings file (<c>approvalRules</c>): <see cref="Project"/> null means every project.</summary>
public sealed record SavedApprovalRule(string Rule, string? Project = null);

/// <summary>
/// The "don't ask again" rules: session rules in memory, project and always rules in the client's settings file (never
/// omp's: omp 18.8's own <c>tools.approval.&lt;tool&gt;: allow</c> can only allow a whole tool, every command alike).
/// Thread-safe: the session pump matches on its thread while the UI adds and removes.
/// </summary>
public sealed class ApprovalRuleSet
{
    private readonly ClientSettingsStore? _store;
    private readonly object _gate = new();
    private List<ApprovalGrant> _saved = [];
    private readonly List<ApprovalGrant> _session = [];

    /// <param name="store">Where project and always rules are kept; null keeps them for this run only (tests).</param>
    public ApprovalRuleSet(ClientSettingsStore? store = null)
    {
        _store = store;
        Reload();
    }

    /// <summary>Raised (on any thread) after a rule was added or removed, or the saved ones were read again.</summary>
    public event Action? Changed;

    /// <summary>Why the saved rules could not be read (the settings file does not parse); null when they were.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Every rule in force: session rules first, then project, then always.</summary>
    public IReadOnlyList<ApprovalGrant> Grants
    {
        get { lock (_gate) return [.. _session, .. _saved.OrderBy(g => g.Scope)]; }
    }

    /// <summary>Reads the saved rules again (another window may have changed them). Unreadable entries are skipped.</summary>
    public void Reload()
    {
        if (_store is null) return;
        try
        {
            var saved = _store.Load().ApprovalRules ?? [];
            lock (_gate) _saved = [.. saved.Select(ToGrant).OfType<ApprovalGrant>().Distinct()];
            LoadError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            LoadError = e.Message;
        }
        Changed?.Invoke();
    }

    /// <summary>The first rule that answers <paramref name="request"/> in this project and session; null when none does.</summary>
    public ApprovalGrant? Match(ApprovalRequest? request, string? cwd, string? session)
    {
        if (request is null) return null;
        lock (_gate)
            return _session.Concat(_saved.OrderBy(g => g.Scope)).FirstOrDefault(g => g.AppliesTo(cwd, session) && g.Rule.Matches(request));
    }

    /// <summary>
    /// Adds a rule. A project or always rule is saved first; it is in force only once saved.
    /// </summary>
    /// <exception cref="ArgumentException">A project rule without a project, or a session rule without a session.</exception>
    /// <exception cref="IOException">The settings file could not be written (also <see cref="UnauthorizedAccessException"/>,
    /// <see cref="InvalidDataException"/>).</exception>
    public ApprovalGrant Add(ApprovalRule rule, ApprovalScope scope, string? cwd, string? session)
    {
        var grant = scope switch
        {
            ApprovalScope.Session => new ApprovalGrant(rule, scope, Session: session ?? throw new ArgumentException("no session to keep the rule for", nameof(session))),
            ApprovalScope.Project => new ApprovalGrant(rule, scope, Project: cwd ?? throw new ArgumentException("no project to keep the rule for", nameof(cwd))),
            _ => new ApprovalGrant(rule, scope),
        };
        if (scope == ApprovalScope.Session)
        {
            lock (_gate) if (!_session.Contains(grant)) _session.Add(grant);
        }
        else
        {
            var saved = ToSaved(grant);
            var all = Save(list => list.Any(s => SameSaved(s, saved)) ? list : [.. list, saved]);
            lock (_gate) _saved = all;
        }
        Changed?.Invoke();
        return grant;
    }

    /// <summary>Takes a rule away; false when it was not in force.</summary>
    /// <exception cref="IOException">The settings file could not be written (also <see cref="UnauthorizedAccessException"/>,
    /// <see cref="InvalidDataException"/>).</exception>
    public bool Remove(ApprovalGrant grant)
    {
        bool removed;
        if (grant.Scope == ApprovalScope.Session)
        {
            lock (_gate) removed = _session.Remove(grant);
        }
        else
        {
            var saved = ToSaved(grant);
            lock (_gate) removed = _saved.Contains(grant);
            var all = Save(list => [.. list.Where(s => !SameSaved(s, saved))]);
            lock (_gate) _saved = all;
        }
        Changed?.Invoke();
        return removed;
    }

    /// <summary>Applies a change to the file's list (re-read, so another window's rules stay) and returns what is in force.</summary>
    private List<ApprovalGrant> Save(Func<SavedApprovalRule[], SavedApprovalRule[]> change)
    {
        if (_store is null)
        {
            lock (_gate) return [.. change([.. _saved.Select(ToSaved)]).Select(ToGrant).OfType<ApprovalGrant>().Distinct()];
        }
        var updated = _store.Update(o =>
        {
            var next = change(o.ApprovalRules ?? []);
            return o with { ApprovalRules = next.Length > 0 ? next : null };
        });
        return [.. (updated.ApprovalRules ?? []).Select(ToGrant).OfType<ApprovalGrant>().Distinct()];
    }

    private static bool SameSaved(SavedApprovalRule a, SavedApprovalRule b) =>
        ApprovalRule.Parse(a.Rule) is { } rule && rule == ApprovalRule.Parse(b.Rule)
        && (a.Project is null ? b.Project is null : b.Project is not null && SessionCatalog.SamePath(a.Project, b.Project));

    private static SavedApprovalRule ToSaved(ApprovalGrant g) => new(g.Rule.Text, g.Scope == ApprovalScope.Project ? g.Project : null);

    private static ApprovalGrant? ToGrant(SavedApprovalRule s) =>
        ApprovalRule.Parse(s.Rule) is { } rule
            ? s.Project is { Length: > 0 } p ? new ApprovalGrant(rule, ApprovalScope.Project, Project: p) : new ApprovalGrant(rule, ApprovalScope.Always)
            : null;
}
