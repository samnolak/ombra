using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AvaloniaEdit.Document;

namespace Ombra.App.Services.Documents;

internal sealed record DocumentRecoveryRecord(int Version, string FullPath, string BaseHash, long Revision,
    string Encoding, bool HasBom, string Text);

internal sealed record DocumentRecoverySnapshot(string FullPath, string BaseHash, long Revision,
    DocumentEncoding Encoding, ITextSource Text)
{
    internal DocumentRecoveryRecord ToRecord() => new(1, FullPath, BaseHash, Revision, Encoding.Id, Encoding.HasBom, Text.Text);
}

/// <summary>One leased journal per physical document. Failed loads/writes never replace or remove the last recovery.</summary>
internal sealed class DocumentRecoveryStore : IAsyncDisposable
{
    private const int MaxJournalBytes = WorkspaceDocumentService.MaxTextLength * 6 + 16384;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly FileStream? _lease;
    private readonly Timer? _timer;
    private readonly string? _path;
    private readonly Exception? _loadError;
    private DocumentRecoverySnapshot? _pending;
    private long _generation;
    private long _written;
    private bool _disposed;
    internal DocumentRecoveryRecord? Recovered { get; private set; }
    internal bool IsPersistent => _path is not null;
    internal string? LastError { get; private set; }
    internal event Action<string?>? ErrorChanged;

    internal DocumentRecoveryStore(string? directory, string fullPath)
    {
        if (directory is null) return; // Unsaved, settings-free windows keep recovery only in their live buffer.
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _path = PathFor(directory, fullPath);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        // Keep the lock inode after close so another process cannot acquire a replacement lock inode.
        _lease = new FileStream(_path + ".lock", options);
        try { Recovered = ReadRecord(_path, fullPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            _loadError = e;
            LastError = $"Recovery could not be read; the original journal is retained: {e.Message}";
        }
        _timer = new Timer(_ => _ = BackgroundFlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal DocumentRecoveryRecord? TakeRecovered()
    {
        var record = Recovered;
        Recovered = null;
        return record;
    }

    internal static string PathFor(string directory, string fullPath)
    {
        var identity = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        return Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }

    internal static IReadOnlyList<string> RecoverablePaths(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var paths = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    if (ReadRecord(path) is { } record && DocumentFile.PathComparer.Equals(path, PathFor(directory, record.FullPath)))
                        paths.Add(record.FullPath);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
                {
                    // Unknown/corrupt entries remain on disk. Opening their original path exposes the read error.
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return paths;
    }

    private static DocumentRecoveryRecord? ReadRecord(string path, string? expectedPath = null)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (stream)
        {
            if (stream.Length > MaxJournalBytes) throw new JsonException("The recovery journal exceeds the size limit.");
            var record = JsonSerializer.Deserialize<DocumentRecoveryRecord>(stream) ?? throw new JsonException("Empty document recovery journal.");
            if (record.Version != 1 || string.IsNullOrWhiteSpace(record.FullPath) || !Path.IsPathFullyQualified(record.FullPath)
                || record.BaseHash is not { Length: 64 } || !record.BaseHash.All(char.IsAsciiHexDigit) || record.Revision < 1
                || record.Revision == long.MaxValue || record.Text is null
                || expectedPath is not null && !DocumentFile.PathComparer.Equals(expectedPath, record.FullPath))
                throw new JsonException("Unsupported or incomplete document recovery journal.");
            _ = DocumentEncoding.FromId(record.Encoding, record.HasBom);
            DocumentEncoding.ValidateText(record.Text);
            return record;
        }
    }

    internal void Queue(DocumentRecoverySnapshot? snapshot)
    {
        lock (_gate)
        {
            if (_disposed || _path is null) return;
            _pending = snapshot;
            _generation++;
            _timer?.Change(350, Timeout.Infinite);
        }
    }

    private async Task BackgroundFlushAsync()
    {
        try { await FlushAsync().ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
    }

    internal async Task FlushAsync(CancellationToken ct = default)
    {
        if (_path is null) return;
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                DocumentRecoverySnapshot? pending;
                long generation;
                lock (_gate)
                {
                    if (_disposed || _written == _generation) return;
                    _timer?.Change(Timeout.Infinite, Timeout.Infinite);
                    generation = _generation;
                    pending = _pending;
                }
                if (_loadError is not null) throw new IOException(LastError, _loadError);
                if (pending is null) File.Delete(_path);
                else
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(pending.ToRecord());
                    var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        await DocumentFile.StageAsync(temp, bytes, null, ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        File.Move(temp, _path, overwrite: true);
                    }
                    finally { DocumentFile.DeleteTemporary(temp); }
                }
                lock (_gate) _written = generation;
                SetError(null);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            SetError($"Recovery could not be saved; previous recovery is retained: {e.Message}");
            throw;
        }
        finally { _writeGate.Release(); }
    }

    private void SetError(string? error)
    {
        if (LastError == error) return;
        LastError = error;
        ErrorChanged?.Invoke(error);
    }

    public async ValueTask DisposeAsync()
    {
        try { await FlushAsync().ConfigureAwait(false); }
        finally
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    _disposed = true;
                    _timer?.Dispose();
                    _lease?.Dispose();
                }
            }
            finally { _writeGate.Release(); }
        }
    }
}
