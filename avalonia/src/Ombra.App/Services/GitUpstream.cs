using System.Text.RegularExpressions;
using Ombra.App.Localization;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.Services;

/// <summary>How safely a repository can take what its branch's upstream has and it does not.</summary>
public enum UpdateRisk
{
    /// <summary>The branch tracks nothing (or git could not say): nothing to update.</summary>
    NoUpstream,
    /// <summary>Nothing new upstream.</summary>
    UpToDate,
    /// <summary>No local changes, nothing unpushed: <c>git pull --ff-only</c>.</summary>
    Clean,
    /// <summary>Uncommitted changes in other files than the incoming ones: the fast-forward leaves them where they are.</summary>
    KeepChanges,
    /// <summary>Incoming commits touch files I changed (uncommitted or unpushed): not updated here.</summary>
    Overlap,
    /// <summary>I have unpushed commits and upstream has new ones: not updated here.</summary>
    Diverged,
}

/// <summary>New commits on a remote branch I do not have: how many, by whom, which files.</summary>
public sealed record IncomingCommits(string Ref, int Count, IReadOnlyList<string> Authors, IReadOnlyList<string> Files);

/// <summary>
/// A repository against its upstream after the last fetch: the current branch's incoming commits, my changes they could
/// collide with, and, on a feature branch, what came to the remote's default branch.
/// </summary>
public sealed record UpstreamState(
    string Top,
    string Branch,
    string? Upstream,
    int Ahead,
    IncomingCommits? Incoming,
    IReadOnlyList<string> LocalFiles,
    IReadOnlyList<string> UnpushedFiles,
    IncomingCommits? DefaultBranch)
{
    public int Behind => Incoming?.Count ?? 0;

    /// <summary>Incoming files I also changed, uncommitted or in unpushed commits.</summary>
    public IReadOnlyList<string> Overlap =>
        Incoming is null ? [] : Incoming.Files.Where(f => LocalFiles.Contains(f) || UnpushedFiles.Contains(f)).ToList();

    public UpdateRisk Risk =>
        Upstream is null ? UpdateRisk.NoUpstream
        : Behind == 0 ? UpdateRisk.UpToDate
        : Overlap.Count > 0 ? UpdateRisk.Overlap
        : Ahead > 0 ? UpdateRisk.Diverged
        : LocalFiles.Count > 0 ? UpdateRisk.KeepChanges
        : UpdateRisk.Clean;
}

/// <summary>
/// The remote side of a project's git: <c>git fetch</c> in the background (the user's own credentials, never a prompt),
/// reading how far the branch is behind its upstream, and the careful update (fast-forward only: git carries
/// uncommitted changes in other files over and touches nothing when it can't).
/// <para>The folder's own git configuration is not trusted: whatever it (and what it includes) sets that would run a
/// command — an ssh command, a credential helper, an upload-pack, a filter or merge driver, an ext:: transport, hooks,
/// fsmonitor — is overridden on the command line with the user's own (global or system) value or a harmless one.</para>
/// </summary>
public static partial class GitUpstream
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(90);

    private static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>
    /// git with no prompt of any kind (terminal, credential manager, ssh askpass) and English messages. The rest of the
    /// app's environment is git's: SSH_AUTH_SOCK reaches ssh's agent, PATH (the login shell's, read at start) the
    /// credential helpers — on macOS with Homebrew's folders added, should the app have started without them.
    /// </summary>
    internal static Dictionary<string, string?> Environment()
    {
        var env = new Dictionary<string, string?>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["SSH_ASKPASS_REQUIRE"] = "never",
            ["GIT_ASKPASS"] = OperatingSystem.IsWindows() ? null : "/usr/bin/true",
            ["SSH_ASKPASS"] = OperatingSystem.IsWindows() ? null : "/usr/bin/true",
            ["GIT_PAGER"] = "cat",
            ["GIT_EDITOR"] = "true",
            ["GIT_CONFIG_PARAMETERS"] = null,
            ["GIT_CONFIG_COUNT"] = null,
            ["LC_ALL"] = "C",
            ["LANGUAGE"] = "C",
        };
        if (OperatingSystem.IsMacOS())
        {
            var dirs = (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var dir in (string[])["/opt/homebrew/bin", "/usr/local/bin"])
                if (!dirs.Contains(dir)) dirs.Add(dir);
            env["PATH"] = string.Join(':', dirs);
        }
        return env;
    }

    private static string Program => WorkspaceTools.Which(Git.Program) ?? Git.Program;

    private static async Task<OmpCliResult> RunAsync(string top, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        // The fixed safety settings come last: they win over the user's own values too (no hooks, no fsmonitor, no ext::)
        string[] safe = [.. await GuardAsync(top, ct), "-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + NullDevice, "-c", "core.quotepath=off",
            "-c", "submodule.recurse=false", "-c", "protocol.ext.allow=never"];
        return await OmpCli.RunAsync(new OmpLaunchSpec
        {
            FileName = Program,
            Arguments = [.. safe, .. args],
            WorkingDirectory = top,
            Environment = Environment(),
        }, timeout ?? ReadTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>-c</c> overrides for every setting the repository's own configuration (local or worktree scope, includes
    /// expanded) gives that could run a command: the user's global/system value when there is one, else a harmless one.
    /// Reading the configuration runs nothing.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> GuardAsync(string top, CancellationToken ct)
    {
        var list = await OmpCli.RunAsync(new OmpLaunchSpec
        {
            FileName = Program,
            Arguments = ["config", "--list", "--show-scope", "--includes", "-z"],
            WorkingDirectory = top,
            Environment = Environment(),
        }, ReadTimeout, ct).ConfigureAwait(false);
        // "scope\0key\nvalue\0" per entry
        var parts = list.Stdout.Split('\0');
        var entries = new List<(string Scope, string Key, string Value)>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var nl = parts[i + 1].IndexOf('\n');
            var key = nl < 0 ? parts[i + 1] : parts[i + 1][..nl];
            entries.Add((parts[i].Trim(), key, nl < 0 ? "" : parts[i + 1][(nl + 1)..]));
        }
        var args = new List<string>();
        foreach (var key in entries.Where(e => e.Scope is "local" or "worktree" && Neutral(e.Key) is not null).Select(e => e.Key).Distinct())
        {
            var trusted = entries.Where(e => e.Key == key && e.Scope is "system" or "global").Select(e => e.Value).ToList();
            if (key.StartsWith("credential.", StringComparison.Ordinal))
            {
                // A list: an empty value clears it, then the user's own helpers come back in order
                args.AddRange(["-c", key + "="]);
                foreach (var helper in trusted) args.AddRange(["-c", $"{key}={helper}"]);
                continue;
            }
            args.AddRange(["-c", $"{key}={(trusted.Count > 0 ? trusted[^1] : Neutral(key))}"]);
            if (key.StartsWith("filter.", StringComparison.Ordinal))
                args.AddRange(["-c", key[..key.LastIndexOf('.')] + ".required=false"]);
        }
        return args;
    }

    /// <summary>The harmless value of a setting that runs a command; null for any other setting.</summary>
    private static string? Neutral(string key)
    {
        var dot = key.LastIndexOf('.');
        var section = key[..key.IndexOf('.')];
        var name = key[(dot + 1)..];
        return (section, name) switch
        {
            ("core", "sshcommand") => "ssh",
            ("core", "gitproxy") => "none",
            ("core", "askpass") => "",
            ("core", "alternaterefscommand") => "",
            ("core", "fsmonitor") => "false",
            ("core", "hookspath") => NullDevice,
            ("core", "editor") => "true",
            ("core", "pager") => "cat",
            ("sequence", "editor") => "true",
            ("gpg", "program") => "gpg",
            ("credential", "helper") => "",
            ("filter", "clean" or "smudge" or "process") => "",
            ("merge", "driver") => "git merge-file %A %O %B",
            ("diff", "command" or "textconv") => "",
            ("protocol", "allow") => key == "protocol.ext.allow" ? "never" : "user",
            _ => null,
        };
    }

    private static IReadOnlyList<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string Message(OmpCliResult r) =>
        Lines(r.Stderr).FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
        ?? Lines(r.Stderr).FirstOrDefault() ?? Lines(r.Stdout).FirstOrDefault() ?? Loc.T("git failed");

    /// <summary>Why a fetch or pull from <paramref name="remote"/> failed: what to do when the remote refused access, else git's own line.</summary>
    private static async Task<string> ReasonAsync(string top, string? remote, OmpCliResult r, CancellationToken ct)
    {
        var url = remote is null ? null : Lines((await RunAsync(top, ["remote", "get-url", remote], ct)).Stdout).FirstOrDefault();
        return AccessProblem(r.Stderr, url) ?? Message(r);
    }

    /// <summary>
    /// The remote at <paramref name="url"/> refusing access, said so that the user knows what to do: not found (on
    /// GitHub also what a private repository looks like to an account without access), a sign-in git has no saved
    /// credentials for, an SSH key or host key ssh refused. Null for any other failure and for a local remote.
    /// </summary>
    internal static string? AccessProblem(string stderr, string? url)
    {
        if (RemoteParts(url) is not (var host, var repo, var user)) return null;
        bool Says(string text) => stderr.Contains(text, StringComparison.OrdinalIgnoreCase);
        var github = host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase);
        if (Says("Repository not found") || NotFoundLine().IsMatch(stderr) || Says("could not be found or you don't have permission"))
            return github
                ? Loc.F("GitHub says {0} was not found. A private repository looks like that to an account without access to it: sign in with one that has it (gh auth login in Terminal), or check the address (git remote -v) — the repository may have been renamed or deleted.", repo)
                : Loc.F("{0} says {1} was not found: it may be private and the account git signs in with has no access to it, or it was renamed or deleted. Check the address (git remote -v) and the account.", host, repo);
        if (Says("could not read Username") || Says("could not read Password") || Says("terminal prompts disabled") || Says("Authentication failed")
            || Says("Invalid username or password") || Says("HTTP Basic: Access denied") || Says("returned error: 401"))
            return github
                ? Loc.T("GitHub asked for a sign-in and git has no saved credentials for it. Sign in in Terminal: gh auth login, then gh auth setup-git.")
                : Loc.F("{0} asked for a sign-in and git has no saved credentials for it. Sign in once in Terminal: a git fetch there saves them with your credential helper.", host);
        if (Says("Permission denied (publickey"))
            return Loc.F("{0} refused your SSH key: check that it is loaded (ssh-add -l in Terminal) and added to your account there.", host);
        if (Says("Host key verification failed"))
            return Loc.F("{0}'s SSH host key is not known yet: connect once in Terminal ({1}) to accept it.", host, $"ssh -T {(user is null ? "" : user + "@")}{host}");
        return null;
    }

    /// <summary>A remote address's host, repository (<c>owner/name</c>, no <c>.git</c>) and SSH user; null for a local path.</summary>
    private static (string Host, string Repo, string? User)? RemoteParts(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string host, path;
        string? user = null;
        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.IsFile || u.Host.Length == 0) return null;
            host = u.Host;
            path = Uri.UnescapeDataString(u.AbsolutePath);
            // Never a password or token: only an SSH user name is shown
            if (u.Scheme == "ssh" && u.UserInfo.Length > 0) user = Uri.UnescapeDataString(u.UserInfo.Split(':')[0]);
        }
        else if (ScpLike().Match(url) is { Success: true } m)
        {
            host = m.Groups["host"].Value;
            path = m.Groups["path"].Value;
            user = m.Groups["user"].Success ? m.Groups["user"].Value : null;
        }
        else return null;
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return (host, path, user);
    }

    /// <summary><c>[user@]host:path</c>; the host is longer than one letter (<c>C:\repo</c> is a Windows path).</summary>
    [GeneratedRegex(@"^(?:(?<user>[^@/:]+)@)?(?<host>[^@/:\\]{2,}):(?<path>.+)$")]
    private static partial Regex ScpLike();

    [GeneratedRegex(@"repository '[^']*' not found", RegexOptions.IgnoreCase)]
    private static partial Regex NotFoundLine();

    /// <summary>The remote the branch tracks (else <c>origin</c>, when there is one); null without remotes.</summary>
    private static async Task<string?> RemoteAsync(string top, CancellationToken ct)
    {
        var branch = await RunAsync(top, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct);
        if (branch.Ok && Lines(branch.Stdout).FirstOrDefault() is { } b)
        {
            var tracked = await RunAsync(top, ["config", "--get", $"branch.{b}.remote"], ct);
            if (tracked.Ok && Lines(tracked.Stdout).FirstOrDefault() is { } r && r != ".") return r;
        }
        var remotes = Lines((await RunAsync(top, ["remote"], ct)).Stdout);
        return remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault();
    }

    /// <summary><c>git fetch --quiet</c> of the branch's remote (no merge); null when done or there is no remote, else git's reason.</summary>
    public static async Task<string?> FetchAsync(string top, CancellationToken ct)
    {
        if (await RemoteAsync(top, ct) is not { } remote) return null;
        // remote.*.uploadpack takes the first value it finds (a -c after it does not win): the option does
        var r = await RunAsync(top, ["fetch", "--quiet", "--no-recurse-submodules", "--no-write-fetch-head", "--upload-pack=git-upload-pack", remote], ct, NetworkTimeout);
        return r.Ok ? null : await ReasonAsync(top, remote, r, ct);
    }

    /// <summary>The repository against its upstream as last fetched; null when <paramref name="top"/> is no repository on a branch.</summary>
    public static async Task<UpstreamState?> ReadAsync(string top, CancellationToken ct)
    {
        var head = await RunAsync(top, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct);
        if (!head.Ok || Lines(head.Stdout).FirstOrDefault() is not { } branch) return null;
        var up = await RunAsync(top, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], ct);
        var upstream = up.Ok ? Lines(up.Stdout).FirstOrDefault() : null;
        int ahead = 0, behind = 0;
        if (upstream is not null)
        {
            var counts = await RunAsync(top, ["rev-list", "--left-right", "--count", "HEAD...@{upstream}"], ct);
            var parts = counts.Stdout.Split((char[])['\t', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (!counts.Ok || parts.Length != 2 || !int.TryParse(parts[0], out ahead) || !int.TryParse(parts[1], out behind)) upstream = null;
        }
        var incoming = upstream is not null && behind > 0 ? await IncomingAsync(top, upstream, behind, ct) : null;
        var status = await RunAsync(top, ["status", "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=all"], ct);
        // Not trimmed: the two status letters and the space are columns (" M b.txt")
        var local = status.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 3)
            .SelectMany(l => l[3..].Split(" -> ")).Select(Unquote).Distinct().ToList();
        var unpushed = upstream is not null && ahead > 0
            ? Lines((await RunAsync(top, ["diff", "--name-only", "--no-renames", "@{upstream}...HEAD"], ct)).Stdout)
            : [];

        // On a feature branch: what came to the remote's default branch that this branch does not have
        IncomingCommits? defaults = null;
        if (await DefaultBranchAsync(top, upstream, ct) is { } main && main != upstream)
        {
            var n = await RunAsync(top, ["rev-list", "--count", "HEAD.." + main], ct);
            if (n.Ok && int.TryParse(n.Stdout.Trim(), out var count) && count > 0) defaults = await IncomingAsync(top, main, count, ct);
        }
        return new UpstreamState(top, branch, upstream, ahead, incoming, local, unpushed, defaults);
    }

    private static string Unquote(string path) =>
        path.Length >= 2 && path[0] == '"' && path[^1] == '"' ? path[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\") : path;

    private static async Task<IncomingCommits> IncomingAsync(string top, string target, int count, CancellationToken ct)
    {
        var authors = Lines((await RunAsync(top, ["log", "--format=%an", "HEAD.." + target], ct)).Stdout).Distinct().ToList();
        var files = Lines((await RunAsync(top, ["diff", "--name-only", "--no-renames", "HEAD..." + target], ct)).Stdout);
        return new IncomingCommits(target, count, authors, files);
    }

    /// <summary>The remote's default branch as a remote-tracking name (<c>origin/main</c>); null when it is unknown.</summary>
    private static async Task<string?> DefaultBranchAsync(string top, string? upstream, CancellationToken ct)
    {
        var remote = upstream is { } u && u.IndexOf('/') is > 0 and var slash ? u[..slash] : await RemoteAsync(top, ct);
        if (remote is null) return null;
        var head = await RunAsync(top, ["symbolic-ref", "--quiet", "--short", $"refs/remotes/{remote}/HEAD"], ct);
        if (head.Ok && Lines(head.Stdout).FirstOrDefault() is { } name) return name;
        foreach (var guess in new[] { "main", "master" })
            if ((await RunAsync(top, ["rev-parse", "--verify", "--quiet", $"refs/remotes/{remote}/{guess}"], ct)).Ok) return $"{remote}/{guess}";
        return null;
    }

    /// <summary>The incoming commits and their changes (<c>git log --stat</c>, then the diff) as one text to read.</summary>
    public static async Task<string> IncomingPatchAsync(string top, string target, CancellationToken ct)
    {
        var log = await RunAsync(top, ["log", "--stat", "--no-color", "--no-ext-diff", "--no-textconv", "HEAD.." + target], ct);
        var diff = await RunAsync(top, ["diff", "--no-color", "--no-ext-diff", "--no-textconv", "HEAD..." + target], ct);
        return log.Stdout + "\n" + diff.Stdout;
    }

    /// <summary>
    /// Takes the upstream's new commits when it is safe (read again first): <see cref="UpdateRisk.Clean"/> and
    /// <see cref="UpdateRisk.KeepChanges"/> by <c>git pull --ff-only</c>. A fast-forward keeps uncommitted changes in
    /// files it does not touch where they are, and git checks every file before it writes any: when one of my changes
    /// (or an untracked file) would be overwritten it stops with nothing changed. So there is nothing to set aside and
    /// nothing to restore — no stash, no reset, no clean. Null when updated, else why not.
    /// </summary>
    public static async Task<string?> UpdateAsync(string top, CancellationToken ct)
    {
        if (await ReadAsync(top, ct) is not { } state) return Loc.T("This folder is not on a git branch.");
        switch (state.Risk)
        {
            case UpdateRisk.UpToDate: return null;
            case UpdateRisk.NoUpstream: return Loc.T("This branch tracks no remote branch.");
            case UpdateRisk.Overlap: return Loc.T("The incoming commits change files you changed too.");
            case UpdateRisk.Diverged: return Loc.T("You have commits that are not pushed yet: the histories diverged.");
        }
        var pull = await RunAsync(top, ["pull", "--ff-only", "--no-rebase", "--quiet", "--no-recurse-submodules", "--upload-pack=git-upload-pack"], ct, NetworkTimeout);
        return pull.Ok ? null : Loc.T("Nothing was changed: ") + await ReasonAsync(top, await RemoteAsync(top, ct), pull, ct);
    }
}
