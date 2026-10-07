using System.Text;
using System.Text.Json;

namespace OmpGui.ClientCore;

/// <summary>One saved omp session as listed in the sidebar.</summary>
/// <param name="IsEmpty">No title and no user message yet (omp creates the file when it starts).</param>
/// <param name="LastMessageAt">When the newest user or assistant message was written (the file's write time when
/// none is found): the list's sort key, so opening a session (which can append non-message entries) does not reorder it.</param>
public sealed record SessionSummary(string Path, string Id, string Cwd, string Title, DateTimeOffset LastMessageAt, bool IsEmpty = false);

/// <summary>
/// Lists omp's saved sessions. omp 18.2.0 has no RPC or CLI for this, so the catalog reads the session folder
/// omp itself reports (the parent of <c>get_state.sessionFile</c>'s project folder), and only the head of each file:
/// an optional <c>{"type":"title"}</c> line, the <c>{"type":"session"}</c> header (id, cwd) and the first user message
/// as a fallback title; and its tail, for the time of the last message (the sort key).
/// <c>UNDOCUMENTED / VERSION-PINNED</c> (session file layout v3, omp 18.2.0). Only <see cref="DeleteSession"/> writes
/// (the sidebar's delete of a session omp does not have open).
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
        return [.. result.OfType<SessionSummary>().OrderByDescending(s => s.LastMessageAt)];
    }

    /// <summary>
    /// Deletes a saved session that omp does not have open, as omp's own session picker does
    /// (<c>FileSessionStorage.deleteSessionWithArtifacts</c>, omp 18.x): the file, its artifacts folder (the path
    /// without <c>.jsonl</c>) and stale rewrite backups (<c>&lt;name&gt;.jsonl.*.bak</c>). The only write in this class.
    /// The open session goes through omp instead (<c>/session delete</c>).
    /// </summary>
    public static void DeleteSession(string path)
    {
        if (!path.EndsWith(".jsonl", StringComparison.Ordinal)) throw new IOException($"Not a session file: {path}");
        File.Delete(path);
        var artifacts = path[..^".jsonl".Length];
        if (Directory.Exists(artifacts)) Directory.Delete(artifacts, recursive: true);
        if (Path.GetDirectoryName(path) is not { } dir) return;
        foreach (var bak in Directory.EnumerateFiles(dir, Path.GetFileName(path) + ".*.bak"))
        {
            try { File.Delete(bak); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // best effort, as omp
        }
    }

    public static Task<SessionSummary?> ReadSummaryAsync(string path, CancellationToken ct = default) =>
        ReadSummaryAsync(new FileInfo(path), ct);

    private static async Task<SessionSummary?> ReadSummaryAsync(FileInfo file, CancellationToken ct)
    {
        string head;
        DateTimeOffset? lastMessage;
        try
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            var buffer = new byte[HeadBytes];
            var read = await ReadFullyAsync(stream, buffer, ct).ConfigureAwait(false);
            // A multi-byte character cut at the end of the buffer decodes as U+FFFD; only whole lines are used.
            head = Encoding.UTF8.GetString(buffer, 0, read);
            lastMessage = stream.Length <= read ? LastMessageTime(head, headIsWhole: true) : null;
            // The tail holds the newest message; a long tool result can push it further back, so look twice.
            foreach (var size in (long[])[HeadBytes, 16 * HeadBytes])
            {
                if (lastMessage is not null || stream.Length <= read) break;
                var start = Math.Max(read, stream.Length - size);
                stream.Seek(start, SeekOrigin.Begin);
                var tail = new byte[stream.Length - start];
                var n = await ReadFullyAsync(stream, tail, ct).ConfigureAwait(false);
                lastMessage = LastMessageTime(Encoding.UTF8.GetString(tail, 0, n), headIsWhole: false);
                if (start == read) break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var summary = Parse(head, file.FullName, file.LastWriteTimeUtc);
        return summary is not null && lastMessage is { } at ? summary with { LastMessageAt = at } : summary;
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        return read;
    }

    /// <summary>The time of the last user or assistant message among the whole lines of <paramref name="text"/>
    /// (the first line is skipped unless it starts the file, as it may be cut). The message's own millisecond
    /// <c>timestamp</c>, else the entry's ISO <c>timestamp</c>.</summary>
    internal static DateTimeOffset? LastMessageTime(string text, bool headIsWhole)
    {
        var lines = text.Split('\n');
        var first = headIsWhole ? 0 : 1;
        for (var i = lines.Length - 1; i >= first; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '{' || !line.Contains("\"message\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (ConversationState.Str(e, "type") != "message" || !e.TryGetProperty("message", out var m)
                    || ConversationState.Str(m, "role") is not ("user" or "assistant")) continue;
                if (m.TryGetProperty("timestamp", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetInt64(out var v)
                    && v > 0 && v < 253402300799999)
                    return DateTimeOffset.FromUnixTimeMilliseconds(v);
                if (ConversationState.Str(e, "timestamp") is { } iso && DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                    return at;
            }
            catch (JsonException) { }
        }
        return null;
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
