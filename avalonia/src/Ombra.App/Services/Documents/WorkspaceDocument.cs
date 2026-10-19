using System.ComponentModel;
using Avalonia.Threading;
using AvaloniaEdit.Document;

namespace Ombra.App.Services.Documents;

/// <summary>The UI-owned buffer, undo history, disk base and recovery state shared by every view of one physical file.</summary>
public sealed class WorkspaceDocument
{
    // Background I/O comes back to the dispatcher that made the document, never to whichever one is current then.
    private readonly Dispatcher _ui;
    private readonly DocumentRecoveryStore _recovery;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private DocumentEncoding _encoding;
    private bool _suppress;
    private bool _textChanged;
    private string _languageId = "";
    private string? _message;
    // The changed disk file the user has not accepted yet (Keep mine makes it the save base).
    private DocumentDiskSnapshot? _conflictDisk;

    public string FullPath { get; }
    public string Uri { get; }
    public TextDocument Buffer { get; }
    public long Revision { get; private set; } = 1;
    public string BaseHash { get; private set; }
    public bool IsDirty { get; private set; }
    public bool IsReadOnly { get; private set; }
    public bool IsEditable => !IsReadOnly;
    public bool HasConflict { get; private set; }
    public bool IsRecovered { get; private set; }
    public bool IsSymlink { get; private set; }
    public string? RecoveryError { get; private set; }
    public string EncodingName => _encoding.Id;
    public bool HasBom => _encoding.HasBom;
    public string NewLine { get; private set; }
    public string Status => string.Join(" · ", new[]
    {
        HasConflict ? "Disk conflict — unsaved buffer preserved" : null,
        IsReadOnly ? "Read-only" : null,
        IsRecovered ? "Recovered unsaved changes" : null,
        IsDirty ? "Unsaved changes" : null,
        IsSymlink ? "Symbolic link — editing physical target" : null,
        _message, RecoveryError
    }.Where(s => !string.IsNullOrEmpty(s)));
    public string LanguageId
    {
        get => _languageId;
        set
        {
            _ui.VerifyAccess();
            ArgumentNullException.ThrowIfNull(value);
            if (_languageId == value) return;
            _languageId = value;
            Notify();
        }
    }
    public event Action<WorkspaceDocument>? Changed;
    /// <summary>Keep mine needs the changed disk text that the next save will replace.</summary>
    public bool CanKeepMine => HasConflict && _conflictDisk is not null;

    internal WorkspaceDocument(Dispatcher ui, string path, bool symlink, DocumentDiskSnapshot? disk, DocumentRecoveryStore recovery, Exception? diskError)
    {
        _ui = ui;
        _ui.VerifyAccess();
        FullPath = path;
        Uri = new Uri(path).AbsoluteUri;
        IsSymlink = symlink;
        _recovery = recovery;
        RecoveryError = recovery.LastError;
        var recovered = recovery.TakeRecovered();
        if (disk is null && recovered is null) throw new IOException("Neither disk text nor valid recovery is available.", diskError);
        _encoding = disk?.Encoding ?? DocumentEncoding.FromId(recovered!.Encoding, recovered.HasBom);
        BaseHash = disk?.Hash ?? recovered!.BaseHash;
        IsReadOnly = disk?.IsReadOnly ?? false;
        Buffer = new TextDocument(disk?.Text ?? recovered!.Text) { FileName = path };
        Buffer.UndoStack.MarkAsOriginalFile();
        if (recovered is not null)
        {
            Revision = recovered.Revision + 1;
            if (disk is null || recovered.Text != disk.Text || recovered.Encoding != disk.Encoding.Id || recovered.HasBom != disk.Encoding.HasBom)
            {
                _encoding = DocumentEncoding.FromId(recovered.Encoding, recovered.HasBom);
                BaseHash = recovered.BaseHash;
                HasConflict = disk is null || !string.Equals(disk.Hash, recovered.BaseHash, StringComparison.OrdinalIgnoreCase);
                Buffer.Replace(0, Buffer.TextLength, recovered.Text);
                if (HasConflict || Buffer.UndoStack.IsOriginalFile) Buffer.UndoStack.DiscardOriginalFileMarker();
                IsDirty = true;
                IsRecovered = true;
                _message = diskError?.Message;
            }
            // Identical disk text and encoding mean the last save committed before journal cleanup.
        }
        if (HasConflict) _conflictDisk = disk;
        NewLine = DocumentEncoding.FirstNewLine(Buffer.Text);
        Buffer.Changing += OnChanging;
        Buffer.Changed += (_, _) => _textChanged = true;
        Buffer.UpdateFinished += OnUpdateFinished;
        Buffer.UndoStack.PropertyChanged += OnUndoStateChanged;
        recovery.ErrorChanged += OnRecoveryError;
        if (recovered is not null && !IsRecovered) recovery.Queue(null);
    }

    internal void NoteSymlink()
    {
        _ui.VerifyAccess();
        if (IsSymlink) return;
        IsSymlink = true;
        Notify();
    }

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        if (_suppress) return;
        if (IsReadOnly) throw new InvalidOperationException("The document is read-only.");
        if ((long)Buffer.TextLength - e.RemovalLength + e.InsertionLength > WorkspaceDocumentService.MaxTextLength)
            throw new NotSupportedException("The edit exceeds the editor's 4 Mi UTF-16 character limit.");
        // Validate the replacement and its two boundaries without copying/scanning the whole buffer per keystroke.
        var high = e.Offset > 0 && char.IsHighSurrogate(Buffer.GetCharAt(e.Offset - 1));
        for (var i = 0; i < e.InsertionLength; i++)
        {
            var c = e.InsertedText.GetCharAt(i);
            if (DocumentEncoding.IsBinaryCharacter(c) || high && !char.IsLowSurrogate(c) || !high && char.IsLowSurrogate(c))
                throw new NotSupportedException("The edit would introduce binary content or an unpaired UTF-16 surrogate.");
            high = char.IsHighSurrogate(c);
        }
        var end = e.Offset + e.RemovalLength;
        var followingLow = end < Buffer.TextLength && char.IsLowSurrogate(Buffer.GetCharAt(end));
        if (high != followingLow) throw new NotSupportedException("The edit would split a Unicode character.");
    }

    private void OnUpdateFinished(object? sender, EventArgs e)
    {
        if (!_textChanged) return;
        _textChanged = false;
        if (_suppress) return;
        Revision++;
        IsDirty = !Buffer.UndoStack.IsOriginalFile;
        if (!IsDirty) IsRecovered = false;
        _message = null;
        QueueRecovery();
        Notify();
    }

    private void OnUndoStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppress || Buffer.IsInUpdate || e.PropertyName != nameof(UndoStack.IsOriginalFile)) return;
        var dirty = !Buffer.UndoStack.IsOriginalFile;
        if (dirty == IsDirty) return;
        IsDirty = dirty;
        if (!IsDirty) IsRecovered = false;
        QueueRecovery();
        Notify();
    }

    private void OnRecoveryError(string? error) => _ui.Post(() =>
    {
        RecoveryError = error;
        Notify();
    });

    private void Notify() => Changed?.Invoke(this);

    private void QueueRecovery()
    {
        if (!_recovery.IsPersistent) return;
        _recovery.Queue(IsDirty || HasConflict
            ? new(FullPath, BaseHash, Revision, _encoding, Buffer.CreateSnapshot()) : null);
    }

    internal Task FlushRecoveryAsync(CancellationToken ct = default) => _recovery.FlushAsync(ct);

    /// <summary>Agent edits are compare-and-swap against a clean UI buffer. They are one undoable, unsaved edit.
    /// The engine writes the file on disk instead when the buffer is dirty; the user then sees the disk-change banner.</summary>
    public bool ApplyEdit(long expectedRevision, string expectedHash, string text)
    {
        _ui.VerifyAccess();
        if (IsReadOnly || IsDirty || HasConflict || expectedRevision != Revision
            || !string.Equals(expectedHash, BaseHash, StringComparison.OrdinalIgnoreCase))
            return RejectEdit("Agent edit rejected: the document is read-only, dirty, conflicted, or its revision/disk hash is stale.");
        try { _ = _encoding.ValidateForSave(text); }
        catch (Exception e) when (e is NotSupportedException or ArgumentException)
        {
            return RejectEdit($"Agent edit rejected: {e.Message}");
        }
        if (Buffer.Text == text) return true;
        Buffer.Replace(0, Buffer.TextLength, text);
        return true;
    }

    private bool RejectEdit(string message)
    {
        _message = message;
        Notify();
        return false;
    }

    /// <summary>Accepts the changed disk file as the save base while keeping the buffer; the next save overwrites it.</summary>
    public void KeepMine()
    {
        _ui.VerifyAccess();
        if (_conflictDisk is not { } disk) throw new InvalidOperationException("No changed disk file is known.");
        BaseHash = disk.Hash;
        IsReadOnly = disk.IsReadOnly;
        HasConflict = false;
        _conflictDisk = null;
        _message = null;
        _suppress = true;
        try
        {
            if (Buffer.Text == disk.Text) Buffer.UndoStack.MarkAsOriginalFile();
            else Buffer.UndoStack.DiscardOriginalFileMarker();
        }
        finally { _suppress = false; }
        IsDirty = !Buffer.UndoStack.IsOriginalFile;
        if (!IsDirty) IsRecovered = false;
        QueueRecovery();
        Notify();
    }

    /// <summary>Saves the unsaved buffer next to the file as a new copy, then reloads the disk text. Returns the copy path.</summary>
    public async Task<string> ReloadKeepingCopyAsync(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(FullPath)!;
        var name = Path.GetFileNameWithoutExtension(FullPath);
        var extension = Path.GetExtension(FullPath);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string copy;
        var attempt = 0;
        do copy = Path.Combine(directory, $"{name}.my-unsaved-{stamp}{(attempt++ == 0 ? "" : "-" + attempt)}{extension}");
        while (File.Exists(copy));
        await SaveCopyAsync(copy, ct).ConfigureAwait(false);
        await ReloadAsync(ct).ConfigureAwait(false);
        return copy;
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var save = await OnUi(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (IsReadOnly) throw new UnauthorizedAccessException("The document is read-only.");
                if (HasConflict) throw new DocumentConflictException("Resolve the disk conflict by explicitly reloading or saving a separate copy.");
                return (Revision, BaseHash, Encoding: _encoding, Text: Buffer.CreateSnapshot());
            });
            await _recovery.FlushAsync(ct).ConfigureAwait(false);
            var bytes = await Task.Run(() => save.Encoding.Encode(save.Text.Text), ct).ConfigureAwait(false);
            await DocumentFile.SaveAsync(FullPath, bytes, save.BaseHash, ct).ConfigureAwait(false);
            var hash = DocumentFile.Hash(bytes);
            await OnUi(() =>
            {
                BaseHash = hash;
                _suppress = true;
                try
                {
                    if (Revision == save.Revision) Buffer.UndoStack.MarkAsOriginalFile();
                    else Buffer.UndoStack.DiscardOriginalFileMarker();
                }
                finally { _suppress = false; }
                IsDirty = !Buffer.UndoStack.IsOriginalFile;
                HasConflict = false;
                _conflictDisk = null;
                IsRecovered = false;
                _message = null;
                NewLine = DocumentEncoding.FirstNewLine(save.Text.Text);
                QueueRecovery();
                Notify();
            });
            // A failed cleanup leaves a valid recovery file and is surfaced rather than silently discarded.
            await _recovery.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await OnUi(() =>
            {
                if (e is DocumentConflictException) HasConflict = true;
                if (e is UnauthorizedAccessException) IsReadOnly = true;
                _message = $"Save failed: {e.Message}";
                QueueRecovery();
                Notify();
            });
            throw;
        }
        finally { _ioGate.Release(); }
    }

    /// <summary>Export to a new file only. Never overwrites the document, another file, or the recovery journal.</summary>
    public async Task SaveCopyAsync(string destination, CancellationToken ct = default)
    {
        var copy = await OnUi(() => (Encoding: _encoding, Text: Buffer.CreateSnapshot()));
        var path = await Task.Run(() => DocumentFile.CanonicalPath(destination), ct).ConfigureAwait(false);
        if (DocumentFile.PathComparer.Equals(path, FullPath)) throw new IOException("Save a copy requires a different, new file.");
        var bytes = await Task.Run(() => copy.Encoding.Encode(copy.Text.Text), ct).ConfigureAwait(false);
        await DocumentFile.SaveCopyAsync(path, bytes, ct).ConfigureAwait(false);
    }

    /// <summary>Explicit discard. A failed read or recovery cleanup leaves the current buffer unchanged.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var revision = await OnUi(() => Revision);
            var disk = await DocumentFile.ReadAsync(FullPath, ct).ConfigureAwait(false);
            await OnUi(() =>
            {
                if (Revision != revision) throw new DocumentConflictException("The buffer changed while reloading; no edits were discarded.");
                _recovery.Queue(null);
            });
            await _recovery.FlushAsync(ct).ConfigureAwait(false);
            await OnUi(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (Revision != revision) throw new DocumentConflictException("The buffer changed while reloading; no edits were discarded.");
                ReplaceWithDisk(disk);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException)
        {
            await OnUi(() =>
            {
                _message = $"Reload failed: {e.Message}";
                QueueRecovery();
                Notify();
            });
            throw;
        }
        finally { _ioGate.Release(); }
    }

    internal async Task ObserveExternalChangeAsync(CancellationToken ct)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var revision = await OnUi(() => Revision);
            var disk = await DocumentFile.ReadAsync(FullPath, ct).ConfigureAwait(false);
            await OnUi(() =>
            {
                IsReadOnly = disk.IsReadOnly;
                if (string.Equals(disk.Hash, BaseHash, StringComparison.OrdinalIgnoreCase))
                {
                    if (HasConflict) { HasConflict = false; _conflictDisk = null; _message = null; QueueRecovery(); }
                    Notify();
                }
                else if (IsDirty || Revision != revision)
                {
                    HasConflict = true;
                    // Keep mine accepts exactly the disk text last observed.
                    _conflictDisk = disk;
                    _message = "The file changed on disk (for example, omp edited it): reload keeping your text as a copy, or keep yours.";
                    QueueRecovery();
                    Notify();
                }
                else ReplaceWithDisk(disk);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            await OnUi(() =>
            {
                HasConflict = true;
                _conflictDisk = null;
                _message = $"The disk file is unavailable or no longer editable; the buffer is preserved: {e.Message}";
                QueueRecovery();
                Notify();
            });
        }
        finally { _ioGate.Release(); }
    }

    private void ReplaceWithDisk(DocumentDiskSnapshot disk)
    {
        _suppress = true;
        try
        {
            Buffer.Replace(0, Buffer.TextLength, disk.Text);
            Buffer.UndoStack.ClearAll();
            Buffer.UndoStack.MarkAsOriginalFile();
        }
        finally { _suppress = false; }
        _encoding = disk.Encoding;
        BaseHash = disk.Hash;
        IsReadOnly = disk.IsReadOnly;
        IsDirty = false;
        HasConflict = false;
        IsRecovered = false;
        _message = null;
        _conflictDisk = null;
        Revision++;
        NewLine = DocumentEncoding.FirstNewLine(disk.Text);
        QueueRecovery();
        Notify();
    }

    internal void SetObservationError(string error)
    {
        _message = error;
        Notify();
    }

    private Task<T> OnUi<T>(Func<T> action) => OnUi(_ui, action);

    private Task OnUi(Action action) => OnUi(_ui, action);

    internal static async Task<T> OnUi<T>(Dispatcher ui, Func<T> action)
    {
        if (ui.CheckAccess()) return action();
        return await ui.InvokeAsync(action);
    }

    internal static async Task OnUi(Dispatcher ui, Action action)
    {
        if (ui.CheckAccess()) action();
        else await ui.InvokeAsync(action);
    }

    internal async ValueTask DisposeAsync()
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _recovery.ErrorChanged -= OnRecoveryError;
            await _recovery.DisposeAsync().ConfigureAwait(false);
        }
        finally { _ioGate.Release(); }
    }
}
