using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ombra.ClientCore;

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

/// <summary>What a tool call is allowed to do: read, change files, or run programs (the engine's tool tier).</summary>
public enum ApprovalRisk { Read, Write, Exec }

/// <summary>
/// The engine's typed approval request (<c>extension_ui_request.approval</c>, tools/approval.ts <c>ToolApprovalRequest</c>).
/// An omp without structured approval sends only text: then there is no request, the card offers Allow and Deny, and no
/// rule may answer for the user.
/// </summary>
/// <param name="Command">bash's complete command; null for other tools, and for a command too long to send whole.</param>
/// <param name="Truncated">A bash command the engine did not send whole: what runs is not all shown.</param>
/// <param name="SafetyChecks">The answer may not be kept at all (provider safety checks, or the engine offers only "once").</param>
public sealed record ApprovalRequest(string Tool, string? Command, bool Truncated, bool SafetyChecks)
{
    public string? ToolCallId { get; init; }
    public ApprovalRisk Risk { get; init; } = ApprovalRisk.Exec;
    /// <summary>The tool's own reason for asking.</summary>
    public string? Reason { get; init; }
    /// <summary>A tool of an MCP server; <see cref="McpServer"/> names it when the engine does.</summary>
    public bool FromMcp { get; init; }
    public string? McpServer { get; init; }
    /// <summary>The tool's argument summary lines ("Command: …", "File: …"), bounded by the engine.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];
    /// <summary>The provider's safety checks, which only a person may confirm.</summary>
    public IReadOnlyList<string> SafetyCheckLines { get; init; } = [];
    /// <summary>The engine offers "session": a rule kept for this session may answer it.</summary>
    public bool AllowsSession { get; init; } = true;
    /// <summary>The engine offers "always": a saved rule (this project, every project) may answer it.</summary>
    public bool AllowsSaved { get; init; } = true;
    /// <summary>bash's <c>cwd</c> argument as the model gave it: the command runs there instead of the session's folder.</summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// Whether a "don't ask again" rule may answer this request or be kept from it. Never with provider safety checks;
    /// never when the engine insists a person answers (a critical command, a bash pattern with <c>approval: prompt</c> —
    /// also from an engine that still offered every scope for those); never for a command run in another folder, whose
    /// scripts and build files the rule's folder never vouched for.
    /// </summary>
    public bool RulesMayAnswer =>
        !SafetyChecks
        && Reason?.StartsWith("Critical pattern", StringComparison.Ordinal) != true
        && Reason?.StartsWith("Prompt required by bash pattern", StringComparison.Ordinal) != true
        && Cwd is null or "." or "./";

    /// <summary>A rule kept for <paramref name="scope"/> may answer this request (the engine's offered scopes).</summary>
    public bool Allows(ApprovalScope scope) => RulesMayAnswer && (scope == ApprovalScope.Session ? AllowsSession : AllowsSaved);

    public static ApprovalRequest? From(PendingDialog dialog) => dialog.Kind == DialogKind.Approval ? dialog.Approval : null;

    /// <summary>Reads the wire object; null when it is not a usable request (then the dialog is treated as text only).</summary>
    public static ApprovalRequest? Parse(JsonElement approval)
    {
        if (approval.ValueKind != JsonValueKind.Object || Text(approval, "tool") is not { Length: > 0 } tool) return null;
        var risk = Text(approval, "risk") switch { "read" => ApprovalRisk.Read, "write" => ApprovalRisk.Write, "exec" => ApprovalRisk.Exec, _ => (ApprovalRisk?)null };
        if (risk is null) return null;
        var scopes = Strings(approval, "scopes");
        var checks = Strings(approval, "safetyChecks");
        var command = tool == "bash" ? Text(approval, "command") : null;
        return new ApprovalRequest(tool, command is { Length: > 0 } ? command : null, tool == "bash" && command is null,
            checks.Count > 0 || !scopes.Contains("session") && !scopes.Contains("always"))
        {
            ToolCallId = Text(approval, "toolCallId"),
            Risk = risk.Value,
            Reason = Text(approval, "reason"),
            FromMcp = Text(approval, "origin") == "mcp",
            McpServer = Text(approval, "server"),
            Details = Strings(approval, "details"),
            SafetyCheckLines = checks,
            Cwd = Text(approval, "cwd"),
            AllowsSession = scopes.Contains("session"),
            AllowsSaved = scopes.Contains("always"),
        };

        static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static List<string> Strings(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? [.. v.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!)]
                : [];
    }
}

/// <summary>Visible approval text only. Never use this representation as a command or a saved rule.</summary>
public static class ApprovalDisplay
{
    internal static bool Hidden(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
        || Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator && rune.Value != ' '
        || rune.Value is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x2800 or 0x3164 or 0xFFA0
            or >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF;

    public static bool HasHiddenCharacters(string text, bool preserveNewlines = false)
    {
        foreach (var rune in text.EnumerateRunes())
            if (Hidden(rune) && !(preserveNewlines && rune.Value == '\n')) return true;
        return false;
    }

    public static string Format(string text, bool preserveNewlines = false)
    {
        StringBuilder? visible = null;
        var offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Hidden(rune) && !(preserveNewlines && rune.Value == '\n'))
            {
                visible ??= new StringBuilder(text.Length + 16).Append(text.AsSpan(0, offset));
                visible.Append("[U+").Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture)).Append(']');
                if (rune.Value == '\n') visible.Append('\n');
            }
            else if (visible is not null) visible.Append(text.AsSpan(offset, rune.Utf16SequenceLength));
            offset += rune.Utf16SequenceLength;
        }
        return visible?.ToString() ?? text;
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
    /// Commands that require a fresh approval every time: wrappers may hide another program,
    /// and destructive commands must not inherit either exact or prefix grants.
    /// </summary>
    private static readonly HashSet<string> NeverAutomatic = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "doas", "su", "env", "xargs", "exec", "eval", "command", "builtin", "nohup", "time", "timeout", "nice", "watch",
        "bash", "sh", "zsh", "fish", "dash", "ksh", "pwsh", "powershell", "cmd", "source", ".",
        "rm", "rmdir", "mv", "dd", "shred", "mkfs", "chmod", "chown", "chgrp", "kill", "killall", "pkill", "shutdown", "reboot",
        "del", "erase", "rd", "move", "ren", "rename", "format", "diskpart", "Remove-Item", "Clear-Content",
        "Stop-Process", "Stop-Computer", "Restart-Computer", "Clear-Disk", "Format-Volume", "Remove-Partition",
        // Remote shells and raw sockets run whatever their arguments say; disks, boot and services change the machine.
        "ssh", "scp", "sftp", "rsync", "nc", "ncat", "netcat", "socat", "telnet",
        "cryptsetup", "wipefs", "fdisk", "sfdisk", "gdisk", "parted", "diskutil", "mount", "umount", "losetup",
        "poweroff", "halt", "init", "telinit", "systemctl", "launchctl", "service", "crontab", "at",
        "iptables", "ip6tables", "nft", "ufw", "pfctl",
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

    // Only direct, known Git subcommands get prefixes. Global options and aliases are not parsed here.
    private static readonly HashSet<string> GitPrefixSubcommands = new(StringComparer.Ordinal)
    {
        "status", "log", "diff", "show", "ls-files", "ls-tree", "rev-parse", "rev-list", "describe", "blame", "grep",
    };

    /// <summary>The rule as text (what the settings file and the Settings page show).</summary>
    public string Text => Command is null ? Tool
        // The legacy :* suffix denotes a prefix. An exact command ending in those characters must not share its text.
        : !Prefix && Command.EndsWith(":*", StringComparison.Ordinal) ? $"{Tool}={System.Text.Json.JsonSerializer.Serialize(Command)}"
        : $"{Tool}({Command}{(Prefix ? ":*" : "")})";

    public override string ToString() => Text;

    /// <summary>Reads <see cref="Text"/>; null for anything else (an argument on a tool other than bash, an empty command).</summary>
    public static ApprovalRule? Parse(string? text)
    {
        if (text is not null && text.StartsWith("bash=", StringComparison.Ordinal))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(text.AsSpan(5)) is { Length: > 0 } exact
                    ? new ApprovalRule("bash", exact, false) : null;
            }
            catch (System.Text.Json.JsonException) { return null; }
        }
        if (text is null || Syntax.Match(text) is not { Success: true } m) return null;
        var tool = m.Groups[1].Value;
        if (!m.Groups[2].Success) return new ApprovalRule(tool, null, false);
        if (tool != "bash") return null;
        var inner = m.Groups[2].Value;
        var prefix = inner.EndsWith(":*", StringComparison.Ordinal);
        if (prefix) inner = inner[..^2];
        return inner.Length == 0 || prefix && !IsSafePrefix(inner) ? null : new ApprovalRule(tool, inner, prefix);
    }

    /// <summary>
    /// Whether this rule answers <paramref name="request"/>. A command rule needs the whole command in view (not shortened
    /// by omp); a prefix rule also needs one plain command — anything chained (<c>;</c> <c>&amp;&amp;</c> <c>|</c>), redirected,
    /// substituted or on several lines is asked, whatever it starts with — and matches only at a word boundary
    /// (<c>npm test</c> covers <c>npm test --watch</c>, not <c>npm testing</c>).
    /// </summary>
    public bool Matches(ApprovalRequest request)
    {
        if (!request.RulesMayAnswer || !string.Equals(Tool, request.Tool, StringComparison.Ordinal)) return false;
        if (request.Command is { } source && ApprovalDisplay.HasHiddenCharacters(source)) return false;
        // Apply before tool-wide and exact matches too, including rules saved by older clients.
        if (Tool == "bash" && (request.Truncated || request.Command is not { } candidate || !IsSafePrefix(candidate))) return false;
        if (Command is null) return !Prefix;
        if (request.Command is not { } command || request.Truncated) return false;
        if (!Prefix) return command == Command;
        return IsSafePrefix(Command) && IsSafePrefix(command)
            && (command == Command || command.StartsWith(Command, StringComparison.Ordinal)
                && command.Length > Command.Length && command[Command.Length] == ' ');
    }

    /// <summary>
    /// A deliberately narrow ASCII token alphabet, not a shell parser. Quotes, escapes, expansions, globbing,
    /// comments, operators and non-ASCII text require fresh approval even when quoted syntax could be harmless.
    /// </summary>
    public static bool IsPlainCommand(string command)
    {
        if (command.Length == 0 || command[0] == ' ' || command[^1] == ' ') return false;
        foreach (var c in command)
            if (!(char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-' or '.' or '/' or ':' or '=' or '+' or ',' or '@' or '%'))
                return false;
        return true;
    }

    private static bool IsSafePrefix(string command)
    {
        if (!IsPlainCommand(command)) return false;
        var end = command.IndexOf(' ');
        var executable = end < 0 ? command.AsSpan() : command.AsSpan(0, end);
        var name = executable[(executable.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (executable.Contains('=') || NeverAutomatic.GetAlternateLookup<ReadOnlySpan<char>>().Contains(name) || name.StartsWith("mkfs.", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("git", StringComparison.OrdinalIgnoreCase))
        {
            if (end < 0) return false;
            var arguments = command.AsSpan(end + 1).TrimStart();
            var boundary = arguments.IndexOf(' ');
            var subcommand = boundary < 0 ? arguments : arguments[..boundary];
            if (!GitPrefixSubcommands.GetAlternateLookup<ReadOnlySpan<char>>().Contains(subcommand)) return false;
        }
        return true;
    }

    /// <summary>
    /// The rule offered on the card: for a plain command its first word (and the subcommand of tools that have one:
    /// <c>git status:*</c>, <c>npm run build:*</c>). Chaining, environment assignments, wrappers,
    /// destructive commands and ambiguous syntax never receive reusable grants; other tools use the tool name.
    /// Null when no rule fits: omp shortened the command, or the provider's own safety checks are part of the request.
    /// </summary>
    public static ApprovalRule? Suggest(ApprovalRequest? request)
    {
        if (request is null || !request.RulesMayAnswer) return null;
        if (request.Tool != "bash") return new ApprovalRule(request.Tool, null, false);
        if (request.Command is not { } command || request.Truncated || ApprovalDisplay.HasHiddenCharacters(command)) return null;
        if (!IsSafePrefix(command)) return null;
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var take = 1;
        var name = words[0][(words[0].LastIndexOf('/') + 1)..];
        if (words.Length > 1 && WithSubcommands.Contains(name) && Subcommand.IsMatch(words[1]))
        {
            take = 2;
            if (words.Length > 2 && ScriptRunners.Contains(words[1]) && Subcommand.IsMatch(words[2])) take = 3;
        }
        return new ApprovalRule("bash", string.Join(' ', words.Take(take)), true);
    }

    /// <summary>The rule in words: "commands starting with “npm test”", "writing files".</summary>
    public string Description => Command is { } c
        ? Prefix ? $"commands starting with “{ApprovalDisplay.Format(c)}”" : $"the command “{Shorten(ApprovalDisplay.Format(c))}”"
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
        return line.Length > 60 || line.Length < s.Length ? line[..GraphemeText.PrefixLength(line, 60, trimEnd: true)] + "…" : line;
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
            return _session.Concat(_saved.OrderBy(g => g.Scope)).FirstOrDefault(g => g.AppliesTo(cwd, session) && request.Allows(g.Scope) && g.Rule.Matches(request));
    }

    /// <summary>
    /// Adds a rule. A project or always rule is saved first; it is in force only once saved.
    /// </summary>
    /// <exception cref="ArgumentException">A project rule without a project, or a session rule without a session.</exception>
    /// <exception cref="IOException">The settings file could not be written (also <see cref="UnauthorizedAccessException"/>,
    /// <see cref="InvalidDataException"/>).</exception>
    public ApprovalGrant Add(ApprovalRule rule, ApprovalScope scope, string? cwd, string? session)
    {
        if (ApprovalRule.Parse(rule.Text) != rule)
            throw new ArgumentException("The rule must round-trip without changing its tool, command or prefix scope.", nameof(rule));
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
