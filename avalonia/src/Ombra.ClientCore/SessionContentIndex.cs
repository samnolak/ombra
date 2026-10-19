using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Ombra.ClientCore;

/// <summary>A session whose messages contain the query: the newest message with it, cut around the match.
/// <paramref name="Before"/> and <paramref name="After"/> carry an ellipsis where the message goes on.</summary>
public sealed record SessionContentMatch(string Path, string Before, string Match, string After)
{
    public string Text => Before + Match + After;
}

/// <summary>Matches in the sessions that could be read; <paramref name="Errors"/> name the journals that could not
/// (their last indexed text, if any, still answered).</summary>
public sealed record SessionContentSearchResult(IReadOnlyList<SessionContentMatch> Matches, IReadOnlyList<SessionCatalogError> Errors);

/// <summary>
/// Full-text search over the saved sessions' user and assistant messages (no tool output, no thinking). Builds on the
/// session catalog: the journals it lists are indexed with the catalog's invalidation, so a journal whose size and write
/// time did not change is never read again, here or (through the private sidecar next to the catalog cache) after a
/// restart. Each journal contributes at most <see cref="MaxSessionChars"/> of text and is read only up to
/// <see cref="MaxReadBytes"/>; lines over <see cref="MaxLineBytes"/> (tool payloads) are skipped unparsed, so memory and
/// time stay bounded. A missing or damaged sidecar is rebuilt; an unreadable journal keeps its last indexed text.
/// </summary>
public sealed class SessionContentIndex
{
    internal const int MaxMessageChars = 2000;
    internal const int MaxSessionChars = 64 * 1024;
    internal const int MaxLineBytes = 1024 * 1024;
    internal const long MaxReadBytes = 64L * 1024 * 1024;
    private const int SnippetBeforeChars = 32;
    private const int SnippetAfterChars = 96;
    private const int FormatVersion = 1;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, Stream> _openRead;
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private string? _root;
    // The in-memory entries differ from the sidecar (a journal was read or a listed one is gone).
    private bool _dirty;
    public string? Path { get; }

    private sealed record Entry(string Path, long Size, long LastWriteUtcTicks, string[] Messages);
    private sealed record Document(int Version, string Profile, string Root, Entry[] Entries);

    public SessionContentIndex(string? path) : this(path, SessionCatalog.OpenSummary) { }

    internal SessionContentIndex(string? path, Func<string, Stream> openRead)
    {
        Path = path is null ? null : SessionCatalog.ResolveLinks(System.IO.Path.GetFullPath(path));
        _openRead = openRead;
    }

    /// <summary>Private sidecar of the client's settings, beside the catalog cache; never inside omp's session folder.</summary>
    public static string PathFor(string settingsPath) =>
        SessionCatalog.ResolveLinks(System.IO.Path.GetFullPath(settingsPath)) + ".session-search.json";

    /// <summary>
    /// Brings the index up to date with <paramref name="sessions"/> (the catalog's rows under
    /// <paramref name="sessionsRoot"/>), then finds <paramref name="query"/> in it, in the order of
    /// <paramref name="sessions"/>. Cancelling keeps the journals read so far for the next search.
    /// </summary>
    public async Task<SessionContentSearchResult> SearchAsync(string sessionsRoot, IReadOnlyList<SessionSummary> sessions, string query,
        CancellationToken ct = default)
    {
        var q = Searchable(query, int.MaxValue);
        if (q.Length == 0) return new([], []);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(sessionsRoot));
            if (!string.Equals(_root, root, StringComparison.Ordinal))
            {
                // Another profile's or account's root never inherits this one's text.
                _entries = Load(root) ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
                _dirty = false;
                _root = root;
            }
            var errors = await RefreshAsync(root, sessions, ct).ConfigureAwait(false);
            var matches = new List<SessionContentMatch>();
            foreach (var session in sessions)
            {
                ct.ThrowIfCancellationRequested();
                if (_entries.TryGetValue(session.Path, out var entry) && Find(entry, q) is { } match) matches.Add(match);
            }
            if (_dirty && await SaveAsync(root, ct).ConfigureAwait(false)) _dirty = false;
            return new(matches, errors);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Re-reads the listed journals whose size or write time changed and drops the ones no longer listed.</summary>
    private async Task<List<SessionCatalogError>> RefreshAsync(string root, IReadOnlyList<SessionSummary> sessions, CancellationToken ct)
    {
        var errors = new List<SessionCatalogError>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var stale = new List<(string Path, SessionCatalog.FileStamp Stamp)>();
        foreach (var session in sessions)
        {
            ct.ThrowIfCancellationRequested();
            if (!listed.Add(session.Path) || !InRoot(session.Path, root)) continue;
            try
            {
                var file = new FileInfo(session.Path);
                if (!file.Exists)
                {
                    listed.Remove(session.Path); // deleted since the catalog listed it
                    continue;
                }
                var stamp = new SessionCatalog.FileStamp(file.Length, file.LastWriteTimeUtc);
                if (_entries.TryGetValue(session.Path, out var known) && known.Size == stamp.Size && known.LastWriteUtcTicks == stamp.LastWriteUtc.Ticks)
                    continue;
                stale.Add((session.Path, stamp));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add(new(session.Path, e.Message));
            }
        }
        var fresh = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
        var failed = new ConcurrentBag<SessionCatalogError>();
        var gone = new ConcurrentBag<string>();
        try
        {
            await Parallel.ForEachAsync(stale, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (item, token) =>
            {
                try
                {
                    var messages = await ReadMessagesAsync(item.Path, item.Stamp, token).ConfigureAwait(false);
                    fresh[item.Path] = new(item.Path, item.Stamp.Size, item.Stamp.LastWriteUtc.Ticks, messages);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    gone.Add(item.Path); // deleted since the catalog listed it: nothing to search, nothing failed
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failed.Add(new(item.Path, e.Message));
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            // Journals read before a cancellation are kept: the next keystroke does not read them again.
            foreach (var (path, entry) in fresh) _entries[path] = entry;
            if (!fresh.IsEmpty) _dirty = true;
        }
        foreach (var path in gone) listed.Remove(path);
        errors.AddRange(failed.OrderBy(e => e.Path, StringComparer.Ordinal));
        // Only after a complete pass: entries of journals no longer listed go (a failed one keeps its last text).
        foreach (var path in _entries.Keys.Where(p => !listed.Contains(p)).ToArray())
        {
            _entries.Remove(path);
            _dirty = true;
        }
        return errors;
    }

    private static bool InRoot(string path, string root) =>
        System.IO.Path.IsPathFullyQualified(path)
        && string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(path)), root, StringComparison.Ordinal)
        && path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);

    /// <summary>The searchable text of the first <paramref name="stamp"/>.Size bytes: what the catalog's metadata vouches
    /// for (an entry appended meanwhile changes the stamp, so the next search reads the journal again).</summary>
    private async Task<string[]> ReadMessagesAsync(string path, SessionCatalog.FileStamp stamp, CancellationToken ct)
    {
        var messages = new List<string>();
        await using (var stream = _openRead(path))
        {
            if (stream.Length < stamp.Size) throw new IOException("Session changed while being read.");
            var limit = Math.Min(stamp.Size, MaxReadBytes);
            var budget = MaxSessionChars;
            var chunk = new byte[64 * 1024];
            var line = new byte[16 * 1024];
            var lineLength = 0;
            var skipping = false;
            long total = 0;
            while (total < limit && budget > 0)
            {
                var n = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - total)), ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("Session read was incomplete.");
                total += n;
                var rest = 0;
                while (rest < n && budget > 0)
                {
                    var span = chunk.AsSpan(rest, n - rest);
                    var newline = span.IndexOf((byte)'\n');
                    var part = newline < 0 ? span : span[..newline];
                    if (!skipping)
                    {
                        if (lineLength + part.Length > MaxLineBytes)
                        {
                            // A tool payload (or a pasted dump): skipped without being kept or parsed.
                            skipping = true;
                            lineLength = 0;
                        }
                        else
                        {
                            if (line.Length < lineLength + part.Length)
                                Array.Resize(ref line, Math.Min(MaxLineBytes, Math.Max(line.Length * 2, lineLength + part.Length)));
                            part.CopyTo(line.AsSpan(lineLength));
                            lineLength += part.Length;
                        }
                    }
                    if (newline < 0) break;
                    if (!skipping && lineLength > 0) budget -= AddMessage(line.AsMemory(0, lineLength), messages, budget);
                    skipping = false;
                    lineLength = 0;
                    rest += newline + 1;
                }
            }
            // A last line without its newline is an entry still being written: left for the next read.
        }
        var after = new FileInfo(path);
        if (!after.Exists) throw new FileNotFoundException("Session disappeared while being read.", path);
        // Appended entries keep the read prefix valid; a shorter or rewritten journal does not.
        if (after.Length < stamp.Size || (after.Length == stamp.Size && after.LastWriteTimeUtc != stamp.LastWriteUtc))
            throw new IOException("Session changed while being read.");
        return [.. messages];
    }

    /// <summary>Adds one journal line's user or assistant text; returns the characters it took from the budget.</summary>
    private static int AddMessage(ReadOnlyMemory<byte> line, List<string> messages, int budget)
    {
        var span = line.Span;
        if (span.IndexOf("\"message\""u8) < 0) return 0;
        // Tool results are most of a journal's bytes; omp writes the role ahead of their content.
        if (span[..Math.Min(span.Length, 512)].IndexOf("\"role\":\"toolResult\""u8) >= 0) return 0;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (ConversationState.Str(e, "type") != "message" || !e.TryGetProperty("message", out var m)
                || ConversationState.Str(m, "role") is not ("user" or "assistant")
                || !m.TryGetProperty("content", out var content)) return 0;
            var max = Math.Min(MaxMessageChars, budget);
            string? text = null;
            if (content.ValueKind == JsonValueKind.String) text = content.GetString();
            else if (content.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (ConversationState.Str(part, "type") != "text" || ConversationState.Str(part, "text") is not { Length: > 0 } s) continue;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(s, 0, Math.Min(s.Length, max * 2 - Math.Min(sb.Length, max * 2)));
                    if (sb.Length >= max * 2) break;
                }
                text = sb.ToString();
            }
            var searchable = Searchable(text ?? "", max);
            if (searchable.Length == 0) return 0;
            messages.Add(searchable);
            return searchable.Length;
        }
        catch (JsonException) { return 0; }
    }

    /// <summary>One line (whitespace runs as one space), NFC (macOS input is NFD), at most <paramref name="max"/>
    /// characters, cut between grapheme clusters. Queries go through the same form.</summary>
    internal static string Searchable(string text, int max)
    {
        var sb = new StringBuilder(Math.Min(text.Length, max == int.MaxValue ? text.Length : max + 16));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
            // Room for normalization to shorten the text before the final cut.
            if (max != int.MaxValue && sb.Length >= max + 16) break;
        }
        var one = sb.ToString();
        try { one = one.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { } // invalid UTF-16 stays as it is, compared ordinally
        return one.Length <= max ? one : one[..GraphemeText.PrefixLength(one, max)];
    }

    /// <summary>The newest message with the query (the one a reopened chat shows nearest), cut around the match.</summary>
    private static SessionContentMatch? Find(Entry entry, string query)
    {
        for (var i = entry.Messages.Length - 1; i >= 0; i--)
        {
            var text = entry.Messages[i];
            var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var end = at + query.Length;
            var head = text[..at];
            var start = GraphemeText.SuffixStart(head, SnippetBeforeChars);
            var tail = text.AsSpan(end);
            var after = GraphemeText.PrefixLength(tail, SnippetAfterChars);
            return new(entry.Path, (start > 0 ? "…" : "") + head[start..], text[at..end],
                tail[..after].ToString() + (after < tail.Length ? "…" : ""));
        }
        return null;
    }

    private Dictionary<string, Entry>? Load(string root)
    {
        if (Path is null) return null;
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var document = JsonSerializer.Deserialize<Document>(stream);
            if (document is not { Version: FormatVersion, Entries: not null } || document.Profile != Path || document.Root != root) return null;
            var entries = new Dictionary<string, Entry>(document.Entries.Length, StringComparer.Ordinal);
            foreach (var entry in document.Entries)
            {
                if (entry is not { Path: not null, Messages: not null } || entry.Size < 0
                    || entry.LastWriteUtcTicks < DateTime.MinValue.Ticks || entry.LastWriteUtcTicks > DateTime.MaxValue.Ticks
                    || !InRoot(entry.Path, root) || System.IO.Path.GetFullPath(entry.Path) != entry.Path
                    || entry.Messages.Any(m => m is null || m.Length > MaxMessageChars)
                    || entry.Messages.Sum(m => (long)m.Length) > MaxSessionChars
                    || !entries.TryAdd(entry.Path, entry)) return null;
            }
            return entries;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes the sidecar (owner-only, atomically, under the same ownership lock discipline as the catalog
    /// cache); false when another process holds it or the storage refuses, so the next search tries again.</summary>
    private async Task<bool> SaveAsync(string root, CancellationToken ct)
    {
        if (Path is not { } path) return true;
        FileStream? ownership;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var lockOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            ownership = new FileStream(path + ".lock", lockOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        using (ownership)
        {
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous,
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(temp, options))
                {
                    await JsonSerializer.SerializeAsync(stream, new Document(FormatVersion, path, root, [.. _entries.Values]), cancellationToken: ct)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
            finally
            {
                try { File.Delete(temp); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
