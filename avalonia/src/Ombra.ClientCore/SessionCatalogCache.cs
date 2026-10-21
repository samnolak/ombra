using System.Collections.Concurrent;
using System.Text.Json;

namespace Ombra.ClientCore;

/// <summary>Disposable derived summaries, scoped to one client profile and session root. Every scan reconciles
/// filesystem metadata; only complete reads with unchanged size/write time can bypass session content next time.
/// A missing or damaged cache never makes the session scan fail. Null storage keeps the same cache in memory.</summary>
public sealed class SessionCatalogCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, Stream> _openRead;
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private string? _root;
    public string? Path { get; }

    private sealed record Entry(SessionSummary Summary, long Size, long LastWriteUtcTicks);
    private sealed record Document(int Version, string Profile, string Root, Entry[] Entries);

    public SessionCatalogCache(string? path) : this(path, SessionCatalog.OpenSummary) { }

    internal SessionCatalogCache(string? path, Func<string, Stream> openRead)
    {
        Path = path is null ? null : SessionCatalog.ResolveLinks(System.IO.Path.GetFullPath(path));
        _openRead = openRead;
    }

    /// <summary>Private sidecar of the client's settings, never a file inside omp's session folder.</summary>
    public static string PathFor(string settingsPath) =>
        SessionCatalog.ResolveLinks(System.IO.Path.GetFullPath(settingsPath)) + ".session-catalog.json";

    /// <summary>The session root the last saved scan covered, so the sidebar can list it before omp has started and
    /// reported its own; null without a readable cache of this profile.</summary>
    public string? StoredRoot()
    {
        if (Path is null) return null;
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var r = doc.RootElement;
            return r.ValueKind == JsonValueKind.Object
                && r.TryGetProperty(nameof(Document.Version), out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1
                && r.TryGetProperty(nameof(Document.Profile), out var p) && p.ValueKind == JsonValueKind.String && p.GetString() == Path
                && r.TryGetProperty(nameof(Document.Root), out var root) && root.GetString() is { } s && System.IO.Path.IsPathFullyQualified(s)
                ? s : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    public Task<SessionCatalogScanResult> ScanAsync(string sessionsRoot, int limit = 500, CancellationToken ct = default) =>
        ScanAsync(sessionsRoot, limit, ct, static root => root.EnumerateDirectories(),
            static project => project.EnumerateFiles("*.jsonl"), static file => file.LastWriteTimeUtc);

    internal async Task<SessionCatalogScanResult> ScanAsync(string sessionsRoot, int limit, CancellationToken ct,
        Func<DirectoryInfo, IEnumerable<DirectoryInfo>> enumerateProjects,
        Func<DirectoryInfo, IEnumerable<FileInfo>> enumerateFiles, Func<FileInfo, DateTime> lastWriteTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(sessionsRoot));
            using var ownership = TryAcquireOwnership();
            var stored = Load(root);
            var previous = string.Equals(_root, root, StringComparison.Ordinal)
                ? new Dictionary<string, Entry>(_entries, StringComparer.Ordinal)
                : new Dictionary<string, Entry>(StringComparer.Ordinal);
            // A committed snapshot may be newer than this instance's retained rows.
            if (stored is not null)
                foreach (var (path, entry) in stored) previous[path] = entry;
            var updated = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
            var result = await SessionCatalog.ScanAsync(root, limit, ct, enumerateProjects, enumerateFiles, lastWriteTime,
                async (file, stamp, token) =>
                {
                    if (previous.TryGetValue(file.FullName, out var cached)
                        && cached.Size == stamp.Size && cached.LastWriteUtcTicks == stamp.LastWriteUtc.Ticks)
                    {
                        updated[file.FullName] = cached;
                        return new(cached.Summary, null);
                    }
                    var read = await SessionCatalog.ReadSummaryAsync(file, token, stamp, _openRead).ConfigureAwait(false);
                    if (read is { Summary: { } summary, Error: null })
                        updated[file.FullName] = new(summary, stamp.Size, stamp.LastWriteUtc.Ticks);
                    return read;
                }).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            // Incomplete scans cannot establish deletion, including on the first scan after a restart.
            var next = result.IsAuthoritative ? new Dictionary<string, Entry>(StringComparer.Ordinal) : previous;
            foreach (var (path, entry) in updated) next[path] = entry;
            var merged = result.IsAuthoritative ? result : result with
            {
                Sessions = result.Merge(root, previous.Values.Select(e => e.Summary).ToArray()),
            };
            // Retained live rows are not evidence for disk: another writer may already have proved their
            // deletion. Persist only the latest committed baseline plus entries validated by this scan.
            var persisted = result.IsAuthoritative ? next : stored is null
                ? new Dictionary<string, Entry>(StringComparer.Ordinal)
                : new Dictionary<string, Entry>(stored, StringComparer.Ordinal);
            if (!result.IsAuthoritative)
                foreach (var (path, entry) in updated) persisted[path] = entry;
            if (ownership is not null && (stored is null || stored.Count != persisted.Count
                || persisted.Any(pair => !stored.TryGetValue(pair.Key, out var entry) || entry != pair.Value)))
                await SaveAsync(root, persisted, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _entries = next;
            _root = root;
            return merged;
        }
        finally { _gate.Release(); }
    }

    private FileStream? TryAcquireOwnership()
    {
        if (Path is null) return null;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            // A contending process can still scan in memory, but must not publish an older disk snapshot.
            // Keep the lock inode, as with the draft journal, so all processes lock the same identity.
            return new FileStream(Path + ".lock", options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private Dictionary<string, Entry>? Load(string root)
    {
        if (Path is null) return null;
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var document = JsonSerializer.Deserialize<Document>(stream);
            if (document is not { Version: 1, Entries: not null } || document.Profile != Path || document.Root != root) return null;
            var entries = new Dictionary<string, Entry>(document.Entries.Length, StringComparer.Ordinal);
            foreach (var entry in document.Entries)
            {
                if (entry is not { Summary: { Path: not null, Id: not null, Cwd: not null, Title: not null } summary }
                    || entry.Size < 0 || entry.LastWriteUtcTicks < DateTime.MinValue.Ticks || entry.LastWriteUtcTicks > DateTime.MaxValue.Ticks
                    || !System.IO.Path.IsPathFullyQualified(summary.Path)
                    || System.IO.Path.GetFullPath(summary.Path) != summary.Path
                    || System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(summary.Path)) != root
                    || !summary.Path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                    || !entries.TryAdd(summary.Path, entry)) return null;
            }
            return entries;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task SaveAsync(string root, Dictionary<string, Entry> entries, CancellationToken ct)
    {
        var temp = Path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
            {
                await JsonSerializer.SerializeAsync(stream, new Document(1, Path!, root, entries.Values.ToArray()), cancellationToken: ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, Path!, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
