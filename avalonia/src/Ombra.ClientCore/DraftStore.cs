using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ombra.ClientCore;

public sealed record DraftContent(string Text, ImageAttachment[] Images, string[] Pastes, LocalMark[] Marks)
{
    public static DraftContent Empty { get; } = new("", [], [], []);
    public bool IsEmpty => Text.Length == 0 && Images.Length == 0 && Pastes.Length == 0 && Marks.Length == 0;
}
public sealed record DraftSubmission(string Id, string Text, ImageAttachment[] Images);
public sealed record SavedDraft(string Id, string Project, string? SessionFile, DraftContent Composer, QueuedMessage[] Queue, DraftSubmission[] Pending, string QuestionReply = "")
{
    public long[] RecoveredSequences { get; init; } = [];
    public bool IsEmpty => Composer.IsEmpty && Queue.Length == 0 && Pending.Length == 0 && QuestionReply.Length == 0;
}

/// <summary>A detached disk snapshot, readable while a writer owns the journal. It cannot write or acquire ownership.</summary>
public sealed class DraftSnapshot(IReadOnlyList<SavedDraft> all, string? lastError)
{
    public IReadOnlyList<SavedDraft> All { get; } = all;
    public string? LastError { get; } = lastError;
    public SavedDraft? Get(string id) => All.FirstOrDefault(d => d.Id == id);
    public bool HasDraftInProject(string? project) => project is not null && All.Any(d => SessionCatalog.SamePath(d.Project, project));
}

/// <summary>A cooperating writer already owns this journal. No journal bytes were read or changed.</summary>
public sealed class DraftStoreInUseException(string path, Exception inner)
    : IOException($"Another Ombra instance owns the draft journal: {path}", inner);

/// <summary>Client-owned recovery journal. A successful Flush is the save-before-clear boundary. Runtime queues and
/// uncertain submissions are restored for editing, never replayed. A disk store owns its OS file lease from before
/// loading until disposal; this does not lock omp's session files. Without a settings store this stays in memory.</summary>
public sealed class DraftStore : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SavedDraft> _drafts = [];
    private readonly HashSet<string> _deletedIds = [];
    private readonly HashSet<string> _deletedSessions = new(SessionCatalog.PathComparer);
    /// <summary>Upper bound on unsaved typing lost to a crash; send/close boundaries flush immediately.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private readonly ITimer _timer;
    private readonly Exception? _loadError;
    private readonly FileStream? _ownership;
    private bool _dirty;
    private bool _disposed;
    public string? Path { get; }
    public string? LastError { get; private set; }
    public event Action<string>? SaveFailed;

    public DraftStore(string? path, TimeProvider? time = null)
    {
        Path = path is null ? null : SessionCatalog.ResolveLinks(System.IO.Path.GetFullPath(path));
        // Keep the sidecar inode: deleting it on exit would let another process lock a replacement while an
        // existing owner still holds the old file. The OS releases the handle even after a crash.
        if (Path is not null) _ownership = AcquireOwnership(Path);
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => DebouncedFlush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _loadError = Load(Path, _drafts);
        LastError = LoadError(_loadError);
    }

    /// <summary>Own an existing journal while its directory and filename migrate, without loading or saving drafts.
    /// The caller must also hold application admission for both configuration identities.</summary>
    public static IDisposable AcquireMigrationLease(string path) =>
        AcquireOwnership(SessionCatalog.ResolveLinks(System.IO.Path.GetFullPath(path)), allowMove: true);

    private static FileStream AcquireOwnership(string path, bool allowMove = false)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Windows needs delete-sharing to rename a held sidecar. On Unix keep exactly None: .NET maps other
        // FileShare values to a shared flock, whereas None is exclusive.
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite,
            Share = allowMove && OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { return new FileStream(path + ".lock", options); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 || !OperatingSystem.IsWindows() && (e.HResult & 0xffff) is 11 or 35)
        {
            throw new DraftStoreInUseException(path, e);
        }
    }

    /// <summary>Read the latest committed journal without a writer lease, a timer, or any filesystem changes.</summary>
    public static DraftSnapshot ReadSnapshot(string path)
    {
        var drafts = new Dictionary<string, SavedDraft>();
        var error = Load(path, drafts);
        return new DraftSnapshot(drafts.Values.Where(d => !d.IsEmpty).ToArray(), LoadError(error));
    }

    private static string? LoadError(Exception? error) =>
        error is null ? null : $"Could not read drafts; the original file was left untouched: {error.Message}";

    private static Exception? Load(string? path, Dictionary<string, SavedDraft> drafts)
    {
        if (path is null) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var root = JsonNode.Parse(stream) as JsonObject ?? throw new JsonException("Empty draft document");
            var version = root["Version"]?.GetValue<int>();
            if (version is not (1 or Document.CurrentVersion) || root["Drafts"] is not JsonArray list) throw new JsonException("Unsupported or incomplete draft document");
            // Version 1 kept code comments and the page comments' text apart; both become local marks losslessly
            if (version == 1)
                foreach (var node in list)
                    if (node?["Composer"] is JsonObject composer) LocalMarks.MigrateComposer(composer);
            var document = root.Deserialize<Document>() ?? throw new JsonException("Empty draft document");
            if (document.Drafts is null) throw new JsonException("Unsupported or incomplete draft document");
            foreach (var draft in document.Drafts)
            {
                Validate(draft);
                drafts.Add(draft.Id, draft);
            }
            return null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            drafts.Clear();
            return e;
        }
    }

    public static string PathFor(string settingsPath) =>
        System.IO.Path.ChangeExtension(SessionCatalog.ResolveLinks(System.IO.Path.GetFullPath(settingsPath)), ".drafts.json");
    public IReadOnlyList<SavedDraft> All { get { lock (_gate) return _drafts.Values.Where(d => !d.IsEmpty).ToArray(); } }
    public SavedDraft? Get(string id) { lock (_gate) return _drafts.GetValueOrDefault(id); }

    /// <summary>A draft also owns its working directory, even before omp has written a session file.</summary>
    public bool HasDraftInProject(string? project)
    {
        if (project is null) return false;
        lock (_gate)
            foreach (var draft in _drafts.Values)
                if (!draft.IsEmpty && SessionCatalog.SamePath(draft.Project, project)) return true;
        return false;
    }

    /// <summary>Forget every recovery record for a successfully deleted session, including its not-yet-filed live draft.
    /// Late callbacks cannot recreate the discarded identity. A retained composer must use a fresh identity.</summary>
    public void RemoveSession(string sessionFile, string? draftId = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _deletedSessions.Add(System.IO.Path.GetFullPath(sessionFile));
            if (draftId is not null) _deletedIds.Add(draftId);
            foreach (var draft in _drafts.Values.ToArray())
            {
                if (draft.Id != draftId && !SessionCatalog.SamePath(draft.SessionFile, sessionFile)) continue;
                _deletedIds.Add(draft.Id);
                _drafts.Remove(draft.Id);
            }
            _dirty = true;
            Flush();
        }
    }

    public void Update(string id, Func<SavedDraft, SavedDraft> update, string project = "", string? sessionFile = null, bool flush = false)
    {
        lock (_gate)
        {
            if (_disposed || _deletedIds.Contains(id)) return;
            var current = _drafts.GetValueOrDefault(id) ?? new SavedDraft(id, project, sessionFile, DraftContent.Empty, [], []);
            var next = update(current);
            if (next.SessionFile is { } file && _deletedSessions.Contains(System.IO.Path.GetFullPath(file))) return;
            _drafts[id] = next;
            _dirty = true;
            if (flush) Flush();
            else _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_loadError is not null) throw new IOException(LastError, _loadError);
            if (!_dirty || Path is null) return;
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
            Directory.CreateDirectory(directory);
            var temp = Path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temp, options))
                {
                    JsonSerializer.Serialize(stream, new Document(Document.CurrentVersion, _drafts.Values.Where(d => !d.IsEmpty).ToArray()));
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, Path, overwrite: true);
                _dirty = false;
                LastError = null;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private void DebouncedFlush()
    {
        try { Flush(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastError = "Drafts could not be saved: " + e.Message;
            SaveFailed?.Invoke(e.Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { Flush(); }
            finally
            {
                _disposed = true;
                _timer.Dispose();
                _ownership?.Dispose();
            }
        }
    }

    private static void Validate(SavedDraft? draft)
    {
        if (draft is null || string.IsNullOrEmpty(draft.Id) || string.IsNullOrEmpty(draft.Project) ||
            !System.IO.Path.IsPathFullyQualified(draft.Project) ||
            draft.SessionFile is not null && !System.IO.Path.IsPathFullyQualified(draft.SessionFile) ||
            draft.Composer is not { } composer || composer.Text is null || composer.Images is null ||
            composer.Pastes is null || composer.Marks is null ||
            draft.Queue is null || draft.Pending is null || draft.QuestionReply is null || draft.RecoveredSequences is null)
            throw new JsonException("Incomplete draft");
        _ = System.IO.Path.GetFullPath(draft.Project);
        if (draft.SessionFile is not null) _ = System.IO.Path.GetFullPath(draft.SessionFile);
        if (composer.Pastes.Any(p => p is null) ||
            composer.Marks.Any(m => m is null || !m.IsValid) ||
            composer.Images.Any(BadImage) ||
            draft.Queue.Any(q => q is null || q.Text is null || q.Images is null || q.Images.Any(BadImage)) ||
            draft.Pending.Any(p => p is null || p.Id is null || p.Text is null || p.Images is null || p.Images.Any(BadImage)))
            throw new JsonException("Incomplete draft content");
    }

    private static bool BadImage(ImageAttachment? image) => image is null || image.Name is null || image.MimeType is null || image.Data is null;

    private sealed record Document(int Version, SavedDraft[] Drafts)
    {
        public const int CurrentVersion = 2;
    }
}
