using Avalonia.Threading;

namespace Ombra.App.Services.Documents;

/// <summary>Owned by an app/window model, not a view or logical session. Every alias uses one canonical UI buffer.</summary>
public sealed class WorkspaceDocumentService : IAsyncDisposable
{
    public const int MaxFileBytes = 8 * 1024 * 1024;
    public const int MaxTextLength = 4 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkspaceDocument> _documents = new(DocumentFile.PathComparer);
    private readonly Dictionary<string, Task<WorkspaceDocument>> _opening = new(DocumentFile.PathComparer);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(DocumentFile.PathComparer);
    private readonly HashSet<string> _recoverable = new(DocumentFile.PathComparer);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _recoveryDirectory;
    private readonly bool _watchFiles;
    private readonly Task _discovery;
    // Made on the UI thread: background work returns to this dispatcher, even after another Application is created.
    private readonly Dispatcher _ui = Dispatcher.UIThread;
    private bool _disposed;

    public WorkspaceDocumentService(string? recoveryDirectory = null, bool watchFiles = true)
    {
        _recoveryDirectory = recoveryDirectory is null ? null : Path.GetFullPath(recoveryDirectory);
        _watchFiles = watchFiles;
        _discovery = DiscoverRecoveryAsync();
    }

    public IReadOnlyList<WorkspaceDocument> Documents { get { lock (_gate) return _documents.Values.ToArray(); } }
    public IReadOnlyList<string> RecoverablePaths { get { lock (_gate) return _recoverable.Order(StringComparer.Ordinal).ToArray(); } }
    public event Action<WorkspaceDocument>? DocumentOpened;
    public event Action? Changed;

    private async Task DiscoverRecoveryAsync()
    {
        if (_recoveryDirectory is null) return;
        var paths = await Task.Run(() => DocumentRecoveryStore.RecoverablePaths(_recoveryDirectory)).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var path in paths)
                if (!_documents.ContainsKey(path)) _recoverable.Add(path);
        }
        // Discovery/disposal must not need a running UI loop in a settings-only model owner.
        _ui.Post(() => { if (!_disposed) Changed?.Invoke(); });
    }

    /// <summary>Wait for initial journal discovery without opening any file or modifying a journal.</summary>
    public Task DiscoverRecoveryAsync(CancellationToken ct) => _discovery.WaitAsync(ct);

    public async Task<WorkspaceDocument> OpenAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var physical = await Task.Run(() => DocumentFile.CanonicalPath(path), ct).ConfigureAwait(false);
        var symlink = !DocumentFile.PathComparer.Equals(Path.GetFullPath(path), physical);
        Task<WorkspaceDocument> opening;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_documents.TryGetValue(physical, out var known)) opening = Task.FromResult(known);
            else if (!_opening.TryGetValue(physical, out opening!))
            {
                opening = OpenCoreAsync(physical, symlink);
                _opening.Add(physical, opening);
            }
        }
        WorkspaceDocument document;
        try { document = await opening.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
                if (opening.IsCompleted && _opening.TryGetValue(physical, out var pending) && ReferenceEquals(pending, opening))
                    _opening.Remove(physical);
        }
        if (symlink) await WorkspaceDocument.OnUi(_ui, document.NoteSymlink);
        return document;
    }

    private async Task<WorkspaceDocument> OpenCoreAsync(string path, bool symlink)
    {
        var recovery = await Task.Run(() => new DocumentRecoveryStore(_recoveryDirectory, path), _lifetime.Token).ConfigureAwait(false);
        try
        {
            DocumentDiskSnapshot? disk = null;
            Exception? diskError = null;
            try { disk = await DocumentFile.ReadAsync(path, _lifetime.Token).ConfigureAwait(false); }
            catch (Exception e) when (recovery.Recovered is not null && e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                diskError = e;
            }
            return await WorkspaceDocument.OnUi(_ui, () =>
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var document = new WorkspaceDocument(_ui, path, symlink, disk, recovery, diskError);
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _documents.Add(path, document);
                    document.Changed += OnDocumentChanged;
                    UpdateRecoveryPath(document);
                    if (_watchFiles) Watch(document);
                }
                DocumentOpened?.Invoke(document);
                Changed?.Invoke();
                return document;
            });
        }
        catch
        {
            await recovery.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public bool TryGet(string path, out WorkspaceDocument document)
    {
        try
        {
            var physical = DocumentFile.CanonicalPath(path);
            lock (_gate) return _documents.TryGetValue(physical, out document!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            document = null!;
            return false;
        }
    }

    public Task ObserveExternalChangeAsync(string path, CancellationToken ct = default)
    {
        WorkspaceDocument? document;
        lock (_gate) _documents.TryGetValue(Path.GetFullPath(path), out document);
        if (document is null && !TryGet(path, out document)) return Task.CompletedTask;
        return document.ObserveExternalChangeAsync(ct);
    }

    public async Task FlushRecoveryAsync(CancellationToken ct = default)
    {
        foreach (var document in Documents) await document.FlushRecoveryAsync(ct).ConfigureAwait(false);
    }

    private void UpdateRecoveryPath(WorkspaceDocument document)
    {
        if (document.IsDirty || document.HasConflict || document.RecoveryError is not null) _recoverable.Add(document.FullPath);
        else _recoverable.Remove(document.FullPath);
    }

    private void OnDocumentChanged(WorkspaceDocument document)
    {
        lock (_gate) UpdateRecoveryPath(document);
        Changed?.Invoke();
    }

    private void Watch(WorkspaceDocument document)
    {
        var directory = Path.GetDirectoryName(document.FullPath)!;
        if (_watchers.ContainsKey(directory)) return;
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                IncludeSubdirectories = false
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += (_, e) => { ObserveWatchedPath(e.OldFullPath); ObserveWatchedPath(e.FullPath); };
            watcher.Error += (_, _) =>
            {
                foreach (var open in Documents.Where(d => DocumentFile.PathComparer.Equals(Path.GetDirectoryName(d.FullPath), directory)))
                    _ = ObserveSafelyAsync(open);
            };
            watcher.EnableRaisingEvents = true;
            _watchers.Add(directory, watcher);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            watcher?.Dispose();
            document.SetObservationError($"External file watching is unavailable; save still checks the disk hash: {e.Message}");
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => ObserveWatchedPath(e.FullPath);

    private void ObserveWatchedPath(string path)
    {
        WorkspaceDocument? document;
        lock (_gate)
        {
            if (_disposed || !_documents.TryGetValue(path, out document)) return;
        }
        _ = ObserveSafelyAsync(document);
    }

    private async Task ObserveSafelyAsync(WorkspaceDocument document)
    {
        try { await document.ObserveExternalChangeAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Task<WorkspaceDocument>[] opening;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
            opening = _opening.Values.ToArray();
        }
        foreach (var task in opening)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException or ObjectDisposedException) { }
        }
        List<Exception>? errors = null;
        foreach (var document in Documents)
        {
            document.Changed -= OnDocumentChanged;
            try { await document.DisposeAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                (errors ??= []).Add(e);
            }
        }
        await _discovery.ConfigureAwait(false);
        _lifetime.Dispose();
        if (errors is not null) throw new IOException("Some document recovery journals could not be flushed; existing recovery files were retained.", new AggregateException(errors));
    }
}
