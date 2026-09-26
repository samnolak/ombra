using System.Text.RegularExpressions;

namespace OmpGui.ClientCore;

/// <summary>What omp's <c>/computer status</c> printed (builtin-modes.ts formatComputerUseStatus, omp 18.2.0).</summary>
/// <param name="Enabled">computer.enabled as the session sees it (the saved setting or a /computer on|off override).</param>
/// <param name="Active">The computer prelude is loaded in eval: omp can really use the desktop now.</param>
public sealed record ComputerUseStatus(bool Enabled, bool Active, string Display, string MaxWidth, string MaxHeight);

/// <summary>One line of <c>git status --porcelain=v2 --branch</c>, summed up.</summary>
public sealed record GitStatusInfo(
    string? Branch, string? Commit, bool Detached, bool Unborn, string? Upstream, int Ahead, int Behind,
    int Staged, int Unstaged, int Untracked, int Conflicts)
{
    /// <summary>Files with any change (a file both staged and changed again counts once).</summary>
    public int ChangedFiles { get; init; }
    public bool Clean => ChangedFiles == 0;
}

/// <summary>One entry of <c>git worktree list --porcelain</c>.</summary>
public sealed record GitWorktreeInfo(string Path, string? Head, string? Branch, bool Detached, bool Bare, bool Locked, string? LockReason,
    bool Prunable, string? PruneReason)
{
    /// <summary>The first entry is always the repository's main working tree.</summary>
    public bool IsMain { get; init; }
}

/// <summary>An account <c>gh auth status</c> reports.</summary>
public sealed record GhAccount(string Host, string Login, bool Ok, bool Active, string? Problem);

/// <summary>What <c>gh auth status</c> said: the accounts per host, or that none is signed in.</summary>
public sealed record GhAuthInfo(IReadOnlyList<GhAccount> Accounts)
{
    public bool SignedIn => Accounts.Any(a => a.Ok);

    /// <summary>The account omp's github tool uses: the active one on github.com, else the first that works.</summary>
    public GhAccount? Primary => Accounts.FirstOrDefault(a => a.Ok && a.Active && a.Host == "github.com")
                                 ?? Accounts.FirstOrDefault(a => a.Ok && a.Active) ?? Accounts.FirstOrDefault(a => a.Ok);
}

/// <summary>A host from <c>/ssh list</c> (helpers/ssh.ts): project entries win over user entries of the same name.</summary>
public sealed record SshHostEntry(string Name, string Host, string? User, int Port, string Scope)
{
    public bool IsProject => Scope == "project";
}

/// <summary>
/// Parsers for what omp's builtins and git, gh print, for the Computer use, Git and worktrees and SSH settings pages.
/// Formats are omp 18.2.0's (checked against its source) and git's porcelain formats (stable by design).
/// </summary>
public static partial class WorkspaceParsers
{
    [GeneratedRegex(@"Computer use:\s*(?<on>enabled|disabled)\s*[·|]\s*prelude:\s*(?<active>active|inactive)\s*[·|]\s*configured:\s*display=(?<display>.*?),\s*maxWidth=(?<w>[^,]*),\s*maxHeight=(?<h>\S*)")]
    private static partial Regex ComputerStatusRegex();

    /// <summary>"Computer use: disabled · prelude: inactive · configured: display=all, maxWidth=3840, maxHeight=2400";
    /// also found inside "/computer on"'s "Computer use enabled for this session. Computer use: enabled · …".</summary>
    public static ComputerUseStatus? ParseComputerStatus(string? text)
    {
        if (text is null || ComputerStatusRegex().Match(text) is not { Success: true } m) return null;
        return new ComputerUseStatus(m.Groups["on"].Value == "enabled", m.Groups["active"].Value == "active",
            m.Groups["display"].Value.Trim(), m.Groups["w"].Value.Trim(), m.Groups["h"].Value.Trim());
    }

    /// <summary><c>/tools</c>: "* name" (active), "- name" (available, off); "~ xd://name" lines are devices, skipped.</summary>
    public static IReadOnlyDictionary<string, bool> ParseTools(string? text)
    {
        var tools = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 3 || line[1] != ' ') continue;
            if (line[0] is '*' or '-') tools[line[2..].Trim()] = line[0] == '*';
        }
        return tools;
    }

    /// <summary><c>git status --porcelain=v2 --branch</c>.</summary>
    public static GitStatusInfo ParseGitStatus(string text)
    {
        string? oid = null, head = null, upstream = null;
        int ahead = 0, behind = 0, staged = 0, unstaged = 0, untracked = 0, conflicts = 0, changed = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# branch.oid ", StringComparison.Ordinal)) oid = line[13..].Trim();
            else if (line.StartsWith("# branch.head ", StringComparison.Ordinal)) head = line[14..].Trim();
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal)) upstream = line[18..].Trim();
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                foreach (var part in line[12..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.StartsWith('+') && int.TryParse(part[1..], out var a)) ahead = a;
                    else if (part.StartsWith('-') && int.TryParse(part[1..], out var b)) behind = b;
                }
            }
            else if (line.Length > 4 && line[0] is '1' or '2' && line[1] == ' ')
            {
                changed++;
                if (line[2] != '.') staged++;
                if (line[3] != '.') unstaged++;
            }
            else if (line.StartsWith("u ", StringComparison.Ordinal)) { conflicts++; changed++; }
            else if (line.StartsWith("? ", StringComparison.Ordinal)) { untracked++; changed++; }
        }
        var detached = head == "(detached)";
        var unborn = oid == "(initial)";
        return new GitStatusInfo(detached ? null : head, unborn ? null : oid, detached, unborn, upstream, ahead, behind,
            staged, unstaged, untracked, conflicts) { ChangedFiles = changed };
    }

    /// <summary><c>git worktree list --porcelain</c>: blocks separated by a blank line.</summary>
    public static IReadOnlyList<GitWorktreeInfo> ParseWorktrees(string text)
    {
        var list = new List<GitWorktreeInfo>();
        string? path = null, head = null, branch = null, lockReason = null, pruneReason = null;
        bool detached = false, bare = false, locked = false, prunable = false;
        void Flush()
        {
            if (path is not null)
                list.Add(new GitWorktreeInfo(path, head, branch, detached, bare, locked, lockReason, prunable, pruneReason) { IsMain = list.Count == 0 });
            path = head = branch = lockReason = pruneReason = null;
            detached = bare = locked = prunable = false;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }
            var (key, value) = line.IndexOf(' ') is var i and > 0 ? (line[..i], line[(i + 1)..]) : (line, null);
            switch (key)
            {
                case "worktree": Flush(); path = value; break;
                case "HEAD": head = value; break;
                case "branch": branch = value is not null && value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value[11..] : value; break;
                case "detached": detached = true; break;
                case "bare": bare = true; break;
                case "locked": locked = true; lockReason = value; break;
                case "prunable": prunable = true; pruneReason = value; break;
            }
        }
        Flush();
        return list;
    }

    [GeneratedRegex(@"(?:Logged in to (?<host>\S+) (?:account|as) (?<login>[^\s(]+))|(?:Failed to log in to (?<fhost>\S+)(?: (?:account|as) (?<flogin>[^\s(]+))?)")]
    private static partial Regex GhAccountRegex();

    /// <summary>
    /// <c>gh auth status</c> (stdout and stderr together; gh ≥ 2.40 prints "account &lt;login&gt;" and "Active
    /// account", older versions "as &lt;login&gt;"). "You are not logged into any GitHub hosts" gives no accounts.
    /// </summary>
    public static GhAuthInfo ParseGhAuth(string text)
    {
        var accounts = new List<GhAccount>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (GhAccountRegex().Match(line) is { Success: true } m)
            {
                var ok = m.Groups["host"].Success;
                // Old gh lists one account per host, always the one in use.
                accounts.Add(new GhAccount(ok ? m.Groups["host"].Value : m.Groups["fhost"].Value, ok ? m.Groups["login"].Value : m.Groups["flogin"].Value,
                    ok, Active: !line.Contains(" account ", StringComparison.Ordinal), Problem: null));
                continue;
            }
            if (accounts.Count == 0) continue;
            var last = accounts[^1];
            var detail = line.TrimStart('-', ' ', '✓', 'X', '!');
            if (detail.StartsWith("Active account:", StringComparison.Ordinal))
                accounts[^1] = last with { Active = detail.EndsWith("true", StringComparison.OrdinalIgnoreCase) };
            else if (!last.Ok && last.Problem is null && detail.Length > 0 && !detail.StartsWith("To ", StringComparison.Ordinal))
                accounts[^1] = last with { Problem = detail };
        }
        return new GhAuthInfo(accounts);
    }

    [GeneratedRegex(@"^(?<name>\S+) \| (?<host>.+?) \| (?<user>.+?) \| (?<port>\d+) \[(?<scope>user|project)\]$")]
    private static partial Regex SshLineRegex();

    /// <summary><c>/ssh list</c>: "name | host | user | port [scope]" per line, or "No SSH hosts configured.". Null when
    /// the text is neither (an error omp printed instead).</summary>
    public static IReadOnlyList<SshHostEntry>? ParseSshList(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || lines is ["No SSH hosts configured."]) return [];
        var hosts = new List<SshHostEntry>();
        foreach (var line in lines)
        {
            if (SshLineRegex().Match(line) is not { Success: true } m) return null;
            var user = m.Groups["user"].Value;
            hosts.Add(new SshHostEntry(m.Groups["name"].Value, m.Groups["host"].Value, user == "-" ? null : user,
                int.Parse(m.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups["scope"].Value));
        }
        return hosts;
    }

    [GeneratedRegex(@"Moved to worktree (?<path>.+?) on branch (?<branch>\S+) \(")]
    private static partial Regex MovedToWorktreeRegex();

    /// <summary><c>/wt &lt;branch&gt;</c>'s success line: "Moved to worktree &lt;path&gt; on branch &lt;branch&gt; (…)".</summary>
    public static (string Path, string Branch)? ParseMovedToWorktree(string text) =>
        MovedToWorktreeRegex().Match(text) is { Success: true } m ? (m.Groups["path"].Value, m.Groups["branch"].Value) : null;

    /// <summary>The branch names omp's <c>/wt</c> accepts (session-worktree.ts createSessionWorktree).</summary>
    public static string? BranchNameProblem(string branch)
    {
        if (branch.Length == 0) return null; // omp picks wt/<date-time>
        if (!Regex.IsMatch(branch, @"^[^\s~^:?*\[\\]+$") || branch.StartsWith('-') || branch.EndsWith('/') || branch.Contains(".."))
            return "Use letters, digits, / - _ . and no spaces (not ~ ^ : ? * [ \\ or ..).";
        return null;
    }

    [GeneratedRegex(@"^(?<scheme>[a-z][a-z0-9+.-]*://)(?<userinfo>[^@/]*@)?(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>A remote URL without any user name or token in it (https://user:token@host/… → https://host/…).</summary>
    public static string RedactRemoteUrl(string url)
    {
        var m = UrlRegex().Match(url.Trim());
        return m.Success ? m.Groups["scheme"].Value + m.Groups["rest"].Value : url.Trim();
    }

    /// <summary>Quotes a value for omp's slash-command argument splitter (utils/command-args.ts: quotes, no escapes).
    /// Null when it cannot be expressed (both quote kinds in it).</summary>
    public static string? QuoteArg(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"', '\'']) < 0) return value;
        if (!value.Contains('"')) return "\"" + value + "\"";
        if (!value.Contains('\'')) return "'" + value + "'";
        return null;
    }
}
