using System.Text.Json;
using Avalonia.Threading;
using Ombra.ClientCore;
using Ombra.Rpc;

namespace Ombra.App.Services.Documents;

/// <summary>What a file's language server answers besides completion, hover, definition, rename and diagnostics.</summary>
public sealed record EditorCapabilities(bool Formatting, bool RangeFormatting, bool DocumentSymbol)
{
    public static EditorCapabilities None { get; } = new(false, false, false);
}

/// <summary>Application-owned document transport. The engine's existing LSP client is the only server owner.</summary>
public sealed class EditorLanguageService : IDisposable
{
    private readonly WorkspaceDocumentService _documents;
    private readonly HashSet<SessionController> _controllers = [];
    private readonly HashSet<WorkspaceDocument> _observed = [];
    private readonly Dictionary<string, JsonElement> _diagnostics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _diagnosticRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<(RpcConnection Connection, string Uri), (long Version, string Hash, bool Dirty, bool Conflict, string Language)> _sent = [];
    private readonly Dictionary<(int Generation, string Language), EditorCapabilities> _capabilities = [];
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    // Made on the UI thread: RPC continuations return to this dispatcher, even after another Application is created.
    private readonly Dispatcher _ui = Dispatcher.UIThread;
    private SessionController? _controller;
    private RpcConnection? _connection;
    private string? _root;
    private int _generation;
    private bool _disposed;
    public bool IsTrusted { get; private set; }
    public string Status { get; private set; } = "Language services require explicit project trust";
    public event Action? Changed;
    public event Action<string, JsonElement>? DiagnosticsChanged;

    public EditorLanguageService(WorkspaceDocumentService documents)
    {
        _documents = documents;
        documents.DocumentOpened += Observe;
        foreach (var document in documents.Documents) Observe(document);
    }

    public void Attach(SessionController? controller, string? projectRoot)
    {
        if (_disposed) return;
        if (ReferenceEquals(controller, _controller) && string.Equals(projectRoot, _root, StringComparison.Ordinal)) return;
        if (_connection is { } previousConnection) _ = RevokeTrustAsync(previousConnection);
        _generation++;
        _controller = controller;
        _connection = null;
        _root = projectRoot;
        IsTrusted = false;
        ClearDiagnostics();
        if (controller is not null && _controllers.Add(controller))
        {
            controller.EditorConnectionChanged += ConnectionChangedAsync;
            controller.EditorFrameReceived += ApplyEditAsync;
        }
        SetStatus(controller is null ? "Editor disconnected" : "Language services require explicit project trust");
        _ = ReconnectSafelyAsync();
    }

    private async Task ConnectionChangedAsync()
    {
        // All retained transports remain callback-capable while background chats run.
        // Reopening the active transport transfers the process-global overlay owner.
        await ReconnectSafelyAsync(failClosed: true).ConfigureAwait(false);
    }

    private async Task ReconnectSafelyAsync(bool failClosed = false)
    {
        try
        {
            var controller = _controller;
            var connection = controller?.Connection;
            if (_disposed || ReferenceEquals(connection, _connection)) return;
            var generation = ++_generation;
            _connection = connection;
            IsTrusted = false;
            if (connection is null) { SetStatus("Editor disconnected"); return; }
            await connection.SetEditorTrustAsync(false, _lifetime.Token).ConfigureAwait(false);
            var documents = _documents.Documents;
            foreach (var document in documents)
            {
                if (generation != _generation) return;
                await SynchronizeAsync(connection, document, "open", _lifetime.Token).ConfigureAwait(false);
            }
            if (generation == _generation) SetStatus("Language services require explicit project trust");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_disposed) SetStatus(error.Message);
            if (failClosed) throw;
        }
    }

    public async Task SetTrustedAsync(bool trusted, CancellationToken ct = default)
    {
        var connection = _connection ?? throw new InvalidOperationException("Start a project session before enabling language services");
        var generation = _generation;
        await connection.SetEditorTrustAsync(trusted, ct).ConfigureAwait(false);
        if (_disposed || generation != _generation) return;
        IsTrusted = trusted;
        SetStatus(trusted ? "Language services enabled for this project" : "Language services require explicit project trust");
        if (trusted) await _ui.InvokeAsync(() =>
        {
            foreach (var document in _documents.Documents) RefreshDiagnostics(document);
        });
        else await _ui.InvokeAsync(ClearDiagnostics);
    }

    public bool TryGetDiagnostics(string uri, out JsonElement diagnostics) => _diagnostics.TryGetValue(uri, out diagnostics);

    public async Task<JsonElement?> RequestAsync(WorkspaceDocument document, string method, int line, int character,
        string? newName = null, CancellationToken ct = default, EditorRequestOptions? options = null)
    {
        if (!IsTrusted) throw new InvalidOperationException("Explicit project trust is required for language services");
        var connection = _connection ?? throw new InvalidOperationException("Editor disconnected");
        var generation = _generation;
        var snapshot = await SynchronizeAsync(connection, document, "sync", ct).ConfigureAwait(false);
        var response = await connection.EditorDocumentAsync("request", snapshot, method, line, character, newName, ct, options).ConfigureAwait(false);
        return await _ui.InvokeAsync<JsonElement?>(() =>
        {
            if (_disposed || generation != _generation || (!IsTrusted) || (method != "rename" && document.Revision != snapshot.Version)) return null;
            if (!response.TryGetProperty("version", out var version) || version.GetInt64() != snapshot.Version ||
                !response.TryGetProperty("positionEncoding", out var encoding) || encoding.GetString() != "utf-16")
                throw new InvalidDataException("Stale editor response or unsupported position encoding");
            if (!response.TryGetProperty("result", out var result)) return null;
            if (method == "diagnostics" && result.ValueKind == JsonValueKind.Array)
            {
                _diagnostics[document.Uri] = result;
                DiagnosticsChanged?.Invoke(document.Uri, result);
            }
            return result.ValueKind == JsonValueKind.Null ? null : result;
        });
    }

    /// <summary>
    /// What the file's language server can do (format document / selection, list symbols), asked once per language
    /// while trust and the connection last. Null: no language services (untrusted, disconnected, or no server).
    /// </summary>
    public async Task<EditorCapabilities?> CapabilitiesAsync(WorkspaceDocument document, CancellationToken ct = default)
    {
        if (!IsTrusted || _connection is null) return null;
        var key = (_generation, string.IsNullOrEmpty(document.LanguageId) ? "plaintext" : document.LanguageId);
        if (_capabilities.TryGetValue(key, out var known)) return known;
        EditorCapabilities? found;
        try
        {
            var result = await RequestAsync(document, "capabilities", 0, 0, ct: ct);
            found = result is { ValueKind: JsonValueKind.Object } value
                ? new EditorCapabilities(Flag(value, "formatting"), Flag(value, "rangeFormatting"), Flag(value, "documentSymbol"))
                : null;
        }
        catch (Exception error) when (error is RpcCommandException or InvalidDataException) { found = EditorCapabilities.None; }
        if (found is not null) _capabilities[key] = found;
        return found;

        static bool Flag(JsonElement value, string name) => value.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
    }

    private void Observe(WorkspaceDocument document)
    {
        if (!_observed.Add(document)) return;
        document.Changed += DocumentChanged;
        DocumentChanged(document);
    }

    private void DocumentChanged(WorkspaceDocument document)
    {
        if (_disposed) return;
        _diagnostics.Remove(document.Uri);
        _ = SynchronizeChangedAsync(document);
        RefreshDiagnostics(document);
    }

    private async Task SynchronizeChangedAsync(WorkspaceDocument document)
    {
        try
        {
            var connection = _connection;
            if (connection is null) return;
            // Full snapshots also carry saved base hashes and conflict state; no second buffer exists here.
            await SynchronizeAsync(connection, document, "sync", _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!_disposed) SetStatus(error.Message); }
    }

    private async Task<EditorDocumentSnapshot> SynchronizeAsync(RpcConnection connection, WorkspaceDocument document, string action, CancellationToken ct)
    {
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await _ui.InvokeAsync(() =>
            {
                var language = string.IsNullOrEmpty(document.LanguageId) ? "plaintext" : document.LanguageId;
                var currentMetadata = (document.Revision, document.BaseHash, document.IsDirty, document.HasConflict, language);
                var unchanged = action != "open" && _sent.TryGetValue((connection, document.Uri), out var sent) && sent == currentMetadata;
                // Requests carry only metadata; avoid flattening a multi-megabyte canonical buffer
                // again when this connection already owns its exact revision.
                return Snapshot(document, unchanged ? "" : null);
            });
            var key = (connection, snapshot.Uri);
            var metadata = (snapshot.Version, snapshot.BaseHash, snapshot.Dirty, snapshot.Conflict, snapshot.LanguageId);
            var hadPrevious = _sent.TryGetValue(key, out var previous);
            if (!hadPrevious || previous != metadata || action == "open")
            {
                await connection.EditorDocumentAsync(action == "open" || !hadPrevious ? "open" : "change", snapshot, ct: ct).ConfigureAwait(false);
                if (hadPrevious && !snapshot.Dirty && (previous.Dirty || previous.Hash != snapshot.BaseHash))
                    await connection.EditorDocumentAsync("save", snapshot, ct: ct).ConfigureAwait(false);
                _sent[key] = metadata;
            }
            return snapshot;
        }
        finally { _sync.Release(); }
    }

    private static async Task RevokeTrustAsync(RpcConnection connection)
    {
        try { await connection.SetEditorTrustAsync(false).ConfigureAwait(false); }
        catch (Exception error) when (error is RpcConnectionClosedException or RpcCommandException or TimeoutException or ObjectDisposedException) { }
    }

    private static EditorDocumentSnapshot Snapshot(WorkspaceDocument document, string? text = null) => new(document.Uri, document.Revision,
        text ?? document.Buffer.Text, string.IsNullOrEmpty(document.LanguageId) ? "plaintext" : document.LanguageId,
        document.BaseHash, document.IsDirty, document.HasConflict);

    private async Task ApplyEditAsync(RpcConnection connection, RpcFrame frame)
    {
        var requestId = frame.Json.GetProperty("requestId").GetString()!;
        var uri = frame.Json.GetProperty("uri").GetString()!;
        var revision = frame.Json.GetProperty("expectedRevision").GetInt64();
        var hash = frame.Json.GetProperty("expectedHash").GetString()!;
        var text = frame.Json.GetProperty("text").GetString()!;
        EditorDocumentSnapshot? result = null;
        string? error = null;
        await _ui.InvokeAsync(() =>
        {
            if (_disposed || _controller is null) { error = "Editor disconnected"; return; }
            if (!_documents.TryGet(new Uri(uri).LocalPath, out var document)) { error = "Editor document is no longer open"; return; }
            if (!document.ApplyEdit(revision, hash, text)) { error = document.Status; return; }
            result = Snapshot(document, text);
        });
        await connection.SendEditorEditResultAsync(requestId, result, error, _lifetime.Token).ConfigureAwait(false);
    }

    private void RefreshDiagnostics(WorkspaceDocument document)
    {
        if (_diagnosticRequests.Remove(document.Uri, out var previous)) { previous.Cancel(); previous.Dispose(); }
        if (!IsTrusted || _connection is null || _disposed) return;
        if (string.IsNullOrWhiteSpace(_root)) return;
        var relative = Path.GetRelativePath(_root, document.FullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _diagnosticRequests[document.Uri] = cancellation;
        _ = RefreshDiagnosticsAsync(document, cancellation.Token);
    }

    private async Task RefreshDiagnosticsAsync(WorkspaceDocument document, CancellationToken ct)
    {
        try
        {
            await Task.Delay(350, ct).ConfigureAwait(false);
            await RequestAsync(document, "diagnostics", 0, 0, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!_disposed && !ct.IsCancellationRequested) SetStatus(error.Message); }
    }

    private void ClearDiagnostics()
    {
        foreach (var request in _diagnosticRequests.Values) { request.Cancel(); request.Dispose(); }
        _diagnosticRequests.Clear();
        var empty = JsonSerializer.SerializeToElement(Array.Empty<object>());
        foreach (var uri in _diagnostics.Keys) DiagnosticsChanged?.Invoke(uri, empty);
        _diagnostics.Clear();
    }

    private void SetStatus(string status)
    {
        var generation = _generation;
        _ui.Post(() => { if (_disposed || generation != _generation) return; Status = status; Changed?.Invoke(); });
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_connection is { } connection) _ = RevokeTrustAsync(connection);
        _disposed = true;
        _generation++;
        _lifetime.Cancel();
        _documents.DocumentOpened -= Observe;
        foreach (var document in _observed) document.Changed -= DocumentChanged;
        foreach (var controller in _controllers)
        {
            controller.EditorConnectionChanged -= ConnectionChangedAsync;
            controller.EditorFrameReceived -= ApplyEditAsync;
        }
        ClearDiagnostics();
        _lifetime.Dispose();
    }
}
