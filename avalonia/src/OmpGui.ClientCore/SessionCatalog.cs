using System.Text;
using System.Text.Json;

namespace OmpGui.ClientCore;

/// <summary>One saved omp session as listed in the sidebar.</summary>
/// <param name="IsEmpty">No title and no user message yet (omp creates the file when it starts).</param>
public sealed record SessionSummary(string Path, string Id, string Cwd, string Title, DateTimeOffset LastModified, bool IsEmpty = false);

/// <summary>
/// Lists omp's saved sessions. omp 18.2.0 has no RPC or CLI for this, so the catalog reads the session folder
/// omp itself reports (the parent of <c>get_state.sessionFile</c>'s project folder), read-only, and only the head
/// of each file: an optional <c>{"type":"title"}</c> line, the <c>{"type":"session"}</c> header (id, cwd) and the first
/// user message as a fallback title. <c>UNDOCUMENTED / VERSION-PINNED</c> (session file layout v3, omp 18.2.0).
/// Nothing here writes to omp's files.
/// </summary>
public static class SessionCatalog
{
    private const int HeadBytes = 64 * 1024;
    private const int MaxTitleChars = 80;

    /// <summary>The folder that holds one sub-folder per project: <c>…/agent/sessions</c>.</summary>
    public static string? SessionsRootOf(string? sessionFile) =>
        sessionFile is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(sessionFile));

    public static async Task<IReadOnlyList<SessionSummary>> ScanAsync(string sessionsRoot, int limit = 500, CancellationToken ct = default)
    {
        if (!Directory.Exists(sessionsRoot)) return [];
        var files = new List<FileInfo>();
        try
        {
            foreach (var dir in new DirectoryInfo(sessionsRoot).EnumerateDirectories())
            {
                try { files.AddRange(dir.EnumerateFiles("*.jsonl")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var newest = files.OrderByDescending(f => f.LastWriteTimeUtc).Take(limit).ToList();
        var result = new SessionSummary?[newest.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, newest.Count), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (i, token) => result[i] = await ReadSummaryAsync(newest[i], token).ConfigureAwait(false)).ConfigureAwait(false);
        return [.. result.OfType<SessionSummary>()];
    }

    public static Task<SessionSummary?> ReadSummaryAsync(string path, CancellationToken ct = default) =>
        ReadSummaryAsync(new FileInfo(path), ct);

    private static async Task<SessionSummary?> ReadSummaryAsync(FileInfo file, CancellationToken ct)
    {
        string head;
        try
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            var buffer = new byte[HeadBytes];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            // A multi-byte character cut at the end of the buffer decodes as U+FFFD; only whole lines are used.
            head = Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Parse(head, file.FullName, file.LastWriteTimeUtc);
    }

    internal static SessionSummary? Parse(string head, string path, DateTime lastWriteUtc)
    {
        string? title = null, id = null, cwd = null, firstUser = null;
        var lines = head.Split('\n');
        // The last line may be cut off by the head limit.
        for (var i = 0; i < lines.Length - (head.EndsWith('\n') ? 0 : 1); i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                switch (ConversationState.Str(e, "type"))
                {
                    case "title":
                        title = ConversationState.Str(e, "title");
                        break;
                    case "session":
                        id = ConversationState.Str(e, "id");
                        cwd = ConversationState.Str(e, "cwd");
                        title ??= ConversationState.Str(e, "title");
                        break;
                    case "message" when firstUser is null && e.TryGetProperty("message", out var m) && ConversationState.Str(m, "role") == "user":
                        firstUser = FirstText(m);
                        break;
                }
            }
            catch (JsonException) { }
            if (cwd is not null && (title is { Length: > 0 } || firstUser is not null)) break;
        }
        if (cwd is null) return null;
        var display = title is { Length: > 0 } t ? t.Trim() : firstUser is { Length: > 0 } u ? OneLine(u) : "New session";
        return new SessionSummary(path, id ?? Path.GetFileNameWithoutExtension(path), cwd, display, new DateTimeOffset(lastWriteUtc, TimeSpan.Zero),
            IsEmpty: title is not { Length: > 0 } && firstUser is null);
    }

    private static string? FirstText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var c)) return null;
        if (c.ValueKind == JsonValueKind.String) return c.GetString();
        if (c.ValueKind != JsonValueKind.Array) return null;
        foreach (var p in c.EnumerateArray())
            if (ConversationState.Str(p, "type") == "text" && ConversationState.Str(p, "text") is { Length: > 0 } s) return s;
        return null;
    }

    private static string OneLine(string s)
    {
        var line = string.Join(' ', s.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= MaxTitleChars ? line : line[..MaxTitleChars] + "…";
    }

    /// <summary>
    /// Path equality as the file system sees it. Case-insensitive only where the file system is (probed, not
    /// guessed from the OS: macOS volumes can be case-sensitive, Linux ones case-insensitive).
    /// </summary>
    public static bool SamePath(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var fa = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        var fb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        if (Equal(fa, fb)) return true;
        // omp records process.cwd(), which has symbolic links resolved (e.g. macOS /var → /private/var).
        var ra = ResolveLinks(fa);
        var rb = ResolveLinks(fb);
        return (ra != fa || rb != fb) && Equal(ra, rb);
    }

    private static bool Equal(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal)
        || (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) && Directory.Exists(a) && IsCaseInsensitive(a));

    /// <summary>The path with every symbolic link / junction along it resolved (portable <c>realpath</c>).</summary>
    public static string ResolveLinks(string path) => ResolveLinks(path, depth: 0);

    private static string ResolveLinks(string path, int depth)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (depth > 32) return full; // a link cycle
            var root = Path.GetPathRoot(full) ?? "";
            var current = root;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    // The target is itself a path that may run through links (macOS: /var → /private/var).
                    current = ResolveLinks(target.FullName, depth + 1);
            }
            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }

    private static bool IsCaseInsensitive(string existingDir)
    {
        var swapped = new string([.. existingDir.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c))]);
        return swapped != existingDir && Directory.Exists(swapped);
    }
}
