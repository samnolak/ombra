using System.Text;
using System.Text.Json;

namespace Ombra.ClientCore;

/// <summary>One saved omp session as listed in the sidebar.</summary>
/// <param name="IsEmpty">No title and no user message yet (omp creates the file when it starts).</param>
/// <param name="LastMessageAt">When the newest user or assistant message was written (the file's write time when
/// none is found): the list's sort key, so opening a session (which can append non-message entries) does not reorder it.</param>
public sealed record SessionSummary(string Path, string Id, string Cwd, string Title, DateTimeOffset LastMessageAt, bool IsEmpty = false);

public sealed record SessionCatalogError(string Path, string Message);

public sealed record SessionSummaryReadResult(SessionSummary? Summary, SessionCatalogError? Error);

/// <summary>Only a complete, uncapped, error-free scan can prove that a previously listed session was deleted.</summary>
public sealed record SessionCatalogScanResult(
    string Root,
    IReadOnlyList<SessionSummary> Sessions,
    bool EnumerationComplete,
    bool IsCapped,
    IReadOnlyList<SessionCatalogError> Errors)
{
    public bool IsAuthoritative => EnumerationComplete && !IsCapped && Errors.Count == 0;

    /// <summary>Incomplete reads retain known rows, but never from a different session root / account.</summary>
    public IReadOnlyList<SessionSummary> Merge(string? previousRoot, IReadOnlyList<SessionSummary> previous)
    {
        if (IsAuthoritative || previousRoot is null || !SessionCatalog.SamePath(previousRoot, Root)) return Sessions;
        var merged = new Dictionary<string, SessionSummary>(StringComparer.Ordinal);
        foreach (var session in previous) merged[session.Path] = session;
        foreach (var session in Sessions) merged[session.Path] = session;
        return [.. merged.Values.OrderByDescending(s => s.LastMessageAt)];
    }
}

/// <summary>
/// Lists omp's saved sessions. omp 18.2.0 has no RPC or CLI for this, so the catalog reads the session folder
/// omp itself reports (the parent of <c>get_state.sessionFile</c>'s project folder), and only the head of each file:
/// an optional <c>{"type":"title"}</c> line, the <c>{"type":"session"}</c> header (id, cwd) and the first user message
/// as a fallback title; and its tail, for the time of the last message (the sort key).
/// <c>UNDOCUMENTED / VERSION-PINNED</c> (session file layout v3, omp 18.2.0). Read-only:
/// deletion goes through the runtime's session ownership lease, not this catalog.
/// </summary>
public static class SessionCatalog
{
    private const int HeadBytes = 64 * 1024;
    private const int MaxTitleChars = 80;

    internal readonly record struct FileStamp(long Size, DateTime LastWriteUtc);

    /// <summary>The folder that holds one sub-folder per project: <c>…/agent/sessions</c>.</summary>
    public static string? SessionsRootOf(string? sessionFile) =>
        sessionFile is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(sessionFile));

    public static Task<SessionCatalogScanResult> ScanAsync(string sessionsRoot, int limit = 500, CancellationToken ct = default) =>
        ScanAsync(sessionsRoot, limit, ct, static root => root.EnumerateDirectories(),
            static project => project.EnumerateFiles("*.jsonl"), static file => file.LastWriteTimeUtc);

    // Explicit IO boundaries let regressions exercise failures during lazy enumeration and metadata access
    // without depending on platform permissions (which do not fail when the test runner owns the files).
    internal static async Task<SessionCatalogScanResult> ScanAsync(string sessionsRoot, int limit, CancellationToken ct,
        Func<DirectoryInfo, IEnumerable<DirectoryInfo>> enumerateProjects,
        Func<DirectoryInfo, IEnumerable<FileInfo>> enumerateFiles,
        Func<FileInfo, DateTime> lastWriteTime,
        Func<FileInfo, FileStamp, CancellationToken, Task<SessionSummaryReadResult>>? readSummary = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ct.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(sessionsRoot);
        var files = new List<(FileInfo File, FileStamp Stamp)>();
        var errors = new List<SessionCatalogError>();
        var enumerationComplete = true;
        try
        {
            // Exists suppresses access / IO errors, including a temporarily unavailable root.
            foreach (var dir in enumerateProjects(new DirectoryInfo(root)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    foreach (var file in enumerateFiles(dir))
                    {
                        ct.ThrowIfCancellationRequested();
                        try { files.Add((file, new(file.Length, lastWriteTime(file)))); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            errors.Add(new(file.FullName, e.Message));
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    enumerationComplete = false;
                    errors.Add(new(dir.FullName, e.Message));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            enumerationComplete = false;
            errors.Add(new(root, e.Message));
        }
        var newest = files.OrderByDescending(f => f.Stamp.LastWriteUtc).Take(limit).ToList();
        var reads = new SessionSummaryReadResult[newest.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, newest.Count), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (i, token) => reads[i] = await (readSummary is null
                ? ReadSummaryAsync(newest[i].File, token, newest[i].Stamp)
                : readSummary(newest[i].File, newest[i].Stamp, token)).ConfigureAwait(false)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        foreach (var read in reads)
            if (read.Error is { } error) errors.Add(error);
        return new(root, [.. reads.Select(r => r.Summary).OfType<SessionSummary>().OrderByDescending(s => s.LastMessageAt)],
            enumerationComplete, files.Count > limit, errors);
    }

    public static Task<SessionSummaryReadResult> ReadSummaryAsync(string path, CancellationToken ct = default) =>
        ReadSummaryAsync(new FileInfo(path), ct);

    internal static Stream OpenSummary(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);

    internal static async Task<SessionSummaryReadResult> ReadSummaryAsync(FileInfo file, CancellationToken ct,
        FileStamp? expected = null, Func<string, Stream>? openRead = null)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            file.Refresh();
            if (!file.Exists) throw new FileNotFoundException("Session disappeared while being read.", file.FullName);
            var before = new FileStamp(file.Length, file.LastWriteTimeUtc);
            if (expected is { } stamp && stamp != before) throw new IOException("Session changed while being read.");
            await using var stream = (openRead ?? OpenSummary)(file.FullName);
            if (stream.Length != before.Size) throw new IOException("Session changed while being read.");
            var buffer = new byte[(int)Math.Min(HeadBytes, before.Size)];
            var read = await ReadFullyAsync(stream, buffer, ct).ConfigureAwait(false);
            if (read != Math.Min(HeadBytes, before.Size)) throw new IOException("Session read was incomplete.");
            var completeLine = read > 0 && buffer[read - 1] == (byte)'\n';
            // A multi-byte character cut at the end of the buffer decodes as U+FFFD; only whole lines are used.
            var head = Encoding.UTF8.GetString(buffer, 0, read);
            DateTimeOffset? lastMessage = before.Size <= read ? LastMessageTime(head, headIsWhole: true) : null;
            // The tail holds the newest message; a long tool result can push it further back, so look twice.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (lastMessage is not null || before.Size <= read) break;
                var size = attempt == 0 ? HeadBytes : 16 * HeadBytes;
                var start = Math.Max(read, before.Size - size);
                stream.Seek(start, SeekOrigin.Begin);
                var tail = new byte[before.Size - start];
                var n = await ReadFullyAsync(stream, tail, ct).ConfigureAwait(false);
                if (n != tail.Length) throw new IOException("Session read was incomplete.");
                completeLine = n > 0 && tail[n - 1] == (byte)'\n';
                lastMessage = LastMessageTime(Encoding.UTF8.GetString(tail, 0, n), headIsWhole: false);
                if (start == read) break;
            }
            // FileInfo metadata can fail too; it belongs to the same failure boundary as the stream.
            file.Refresh();
            if (!file.Exists) throw new FileNotFoundException("Session disappeared while being read.", file.FullName);
            if (new FileStamp(file.Length, file.LastWriteTimeUtc) != before || stream.Length != before.Size)
                throw new IOException("Session changed while being read.");
            var summary = Parse(head, file.FullName, before.LastWriteUtc);
            ct.ThrowIfCancellationRequested();
            if (summary is null) return new(null, new(file.FullName, "No complete session header could be read."));
            return new(lastMessage is { } at ? summary with { LastMessageAt = at } : summary,
                completeLine ? null : new(file.FullName, "Session ends with an incomplete entry."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ct.ThrowIfCancellationRequested();
            return new(null, new(file.FullName, e.Message));
        }
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Keys = new(StringComparer.Ordinal);

    /// <summary>
    /// One string per folder: full path, links resolved (macOS /var → /private/var), no trailing separator, and
    /// upper-cased on a case-insensitive volume. Two paths of the same existing folder get the same key.
    /// </summary>
    public static string PathKey(string path)
    {
        if (Keys.TryGetValue(path, out var known)) return known;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        var resolved = ResolveLinks(full);
        if (!Directory.Exists(resolved)) return resolved; // may appear later: not remembered
        var key = IsCaseInsensitive(resolved) ? resolved.ToUpperInvariant() : resolved;
        if (Keys.Count > 4096) Keys.Clear();
        Keys[path] = key;
        return key;
    }

    /// <summary>Compares folders by <see cref="PathKey"/> (for sets and lookups of project folders).</summary>
    public static readonly IEqualityComparer<string?> PathComparer = new FolderComparer();

    private sealed class FolderComparer : IEqualityComparer<string?>
    {
        public bool Equals(string? x, string? y) =>
            ReferenceEquals(x, y) || x is not null && y is not null && (string.Equals(x, y, StringComparison.Ordinal) || PathKey(x) == PathKey(y));

        public int GetHashCode(string? obj) => obj is null ? 0 : StringComparer.Ordinal.GetHashCode(PathKey(obj));
    }

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
