using System.Security.Cryptography;
using System.Text;

namespace Ombra.ClientCore.Teams;

/// <summary>
/// Which git repository a folder is, for team items that only make sense in it (an edit's patch, a code mark):
/// <see cref="Fingerprint"/> is the same in every clone (the hash of the root commits, not the folder or the remote
/// URL), <see cref="Commit"/> is HEAD.
/// </summary>
public sealed record RepoIdentity(string Fingerprint, string Commit)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The identity of the repository <paramref name="folder"/> is in; null when it is none (or has no commit) or git is missing.</summary>
    public static RepoIdentity? Of(string folder) => OfAsync(folder).GetAwaiter().GetResult();

    /// <inheritdoc cref="Of"/>
    public static async Task<RepoIdentity?> OfAsync(string folder, CancellationToken ct = default, Func<ToolCommand, CancellationToken, Task<ToolResult>>? run = null)
    {
        run ??= WorkspaceTools.RunAsync;
        var roots = await run(new ToolCommand("git", ["rev-list", "--max-parents=0", "HEAD"], folder, Timeout), ct);
        if (!roots.Ok || roots.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is not { Length: > 0 } ids) return null;
        var head = await run(new ToolCommand("git", ["rev-parse", "--verify", "HEAD^{commit}"], folder, Timeout), ct);
        return head.Ok && head.Stdout.Trim() is { Length: > 0 } commit ? new RepoIdentity(FingerprintOf(ids), commit.ToLowerInvariant()) : null;
    }

    /// <summary>SHA-256 of the root commit ids (lowercase, sorted, one per line), its first 16 bytes in hex.</summary>
    public static string FingerprintOf(IEnumerable<string> rootCommits)
    {
        var ids = rootCommits.Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct().Order(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ids)))[..16]);
    }

    /// <summary>The repository in <paramref name="folder"/> has commit <paramref name="sha"/> (a full or abbreviated id).</summary>
    public static async Task<bool> HasCommitAsync(string folder, string sha, CancellationToken ct = default, Func<ToolCommand, CancellationToken, Task<ToolResult>>? run = null)
    {
        if (sha.Length is < 4 or > 64 || !sha.All(Uri.IsHexDigit)) return false;
        var r = await (run ?? WorkspaceTools.RunAsync)(new ToolCommand("git", ["cat-file", "-e", sha + "^{commit}"], folder, Timeout), ct);
        return r.Ok;
    }
}
