using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

public enum GitProbeState { Ok, NotARepo, GitMissing, Failed }

/// <summary>Where a folder is in git: its checkout's root, the branch (or commit), and whether it is a linked worktree.</summary>
public sealed record GitHead(string TopLevel, string? Branch, string? ShortCommit, bool IsLinkedWorktree, string CommonDir)
{
    /// <summary>The branch, or the commit when HEAD is detached; null in a repository without commits and no branch.</summary>
    public string? Label => Branch ?? ShortCommit;

    /// <summary>The main checkout of a linked worktree (the folder that holds the shared .git).</summary>
    public string? MainCheckout => IsLinkedWorktree && Path.GetFileName(CommonDir.TrimEnd('/', '\\')) == ".git" ? Path.GetDirectoryName(CommonDir.TrimEnd('/', '\\')) : null;
}

/// <summary>Cheap git reads shared by the header's branch chip and the Git and worktrees page (two or three git calls).</summary>
public static class GitProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<(GitProbeState State, GitHead? Head, string? Error)> ReadHeadAsync(
        Func<ToolCommand, CancellationToken, Task<ToolResult>> run, string folder, CancellationToken ct)
    {
        var r = await run(new ToolCommand("git", ["rev-parse", "--show-toplevel", "--absolute-git-dir", "--git-common-dir"], folder, Timeout), ct);
        if (r.NotFound) return (GitProbeState.GitMissing, null, "git was not found on this computer");
        if (!r.Ok)
            return r.Stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                ? (GitProbeState.NotARepo, null, null)
                : (GitProbeState.Failed, null, r.Message);
        var lines = r.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length < 3) return (GitProbeState.Failed, null, "git printed something unexpected: " + r.Stdout.Trim());
        var top = Full(folder, lines[0]);
        var gitDir = Full(folder, lines[1]);
        var common = Full(folder, lines[2]);
        string? branch = null, commit = null;
        var sym = await run(new ToolCommand("git", ["symbolic-ref", "--short", "-q", "HEAD"], folder, Timeout), ct);
        if (sym.Ok && sym.Stdout.Trim() is { Length: > 0 } b) branch = b;
        else
        {
            var sha = await run(new ToolCommand("git", ["rev-parse", "--short", "HEAD"], folder, Timeout), ct);
            if (sha.Ok && sha.Stdout.Trim() is { Length: > 0 } s) commit = s;
        }
        return (GitProbeState.Ok, new GitHead(top, branch, commit, !SessionCatalog.SamePath(gitDir, common), common), null);
    }

    private static string Full(string folder, string path)
    {
        try { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(folder, path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>A path for reading: the home folder as ~.</summary>
    public static string Tilde(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 1 && path.StartsWith(home, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
               && (path.Length == home.Length || path[home.Length] is '/' or '\\')
            ? "~" + path[home.Length..]
            : path;
    }

    /// <summary>
    /// A folder as the user knows it: macOS's /private/var, /private/tmp, /private/etc as /var, /tmp, /etc (the same
    /// folder reached through a link, so one project never reads two ways), then the home folder as ~.
    /// </summary>
    public static string ShownPath(string path)
    {
        if (OperatingSystem.IsMacOS() && path.StartsWith("/private/", StringComparison.Ordinal) && path[8..] is var rest
            && (rest.StartsWith("/var", StringComparison.Ordinal) || rest.StartsWith("/tmp", StringComparison.Ordinal) || rest.StartsWith("/etc", StringComparison.Ordinal))
            && (rest.Length == 4 || rest[4] == '/'))
            path = rest;
        return Tilde(path);
    }
}
