using Ombra.Rpc;

namespace Ombra.ClientCore;

public sealed partial class SessionController
{
    private const int HistoryPageSize = 100;
    private string? _historyCursor;
    private string? _historySnapshot;
    private int _historyStart;
    private RpcConnection? _historyEventConnection;
    private long _historyAppliedSequence;
    private long _historyTransitionKey;
    private TaskCompletionSource? _historyEventsAdvanced;

    private async Task StartReadOnlyAsync(CancellationToken ct)
    {
        var file = _request.ResumeSessionFile ?? throw new InvalidOperationException("Read-only history requires a saved session file.");
        var spec = await BuildEngineLaunchAsync(ct).ConfigureAwait(false);
        _launchSpec = _host?.UseEngineConfiguration(spec) ?? spec;
        await LoadSavedHistoryAsync(file, _launchSpec, ct).ConfigureAwait(false);
    }

    private void BeginHistoryEvents(RpcConnection connection)
    {
        lock (_lock)
        {
            _historyEventConnection = connection;
            _historyAppliedSequence = 0;
            _historyEventsAdvanced?.TrySetResult();
            _historyEventsAdvanced = null;
        }
    }

    // Called under the state lock after a frame is folded, including malformed frames that were reported as notices.
    private void NoteHistoryEvent(RpcConnection connection, RpcFrame frame, long revisionBefore)
    {
        if (!ReferenceEquals(connection, _historyEventConnection)) return;
        _historyAppliedSequence = frame.Sequence;
        // Any row mutation counts, including IRC, terminal summaries and future event kinds. Streaming builders
        // and phase/compaction changes can advance without replacing a row object.
        if (_state.TranscriptRevision != revisionBefore || frame.Type is "agent_start" or "message_update" or "auto_compaction_end")
            _state.HistoryMutationReadAt = frame.ReadTimestamp;
        _historyEventsAdvanced?.TrySetResult();
        _historyEventsAdvanced = null;
    }

    private async Task DrainHistoryEventsAsync(RpcConnection connection, RpcTimings timings, CancellationToken ct)
    {
        while (true)
        {
            Task progress;
            lock (_lock)
            {
                if (!ReferenceEquals(connection, _historyEventConnection)) throw new OperationCanceledException("The session changed.", ct);
                if (_historyAppliedSequence >= timings.EventsBefore) return;
                progress = (_historyEventsAdvanced ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            var completed = await Task.WhenAny(progress, connection.Completion).WaitAsync(ct).ConfigureAwait(false);
            if (completed == connection.Completion) throw new RpcConnectionClosedException("omp stopped while history was loading", connection.CloseReason);
        }
    }

    private void AdoptHistoryPage(ConversationState state, OmpMessagesPage page)
    {
        _historyCursor = page.NextCursor;
        _historySnapshot = page.SnapshotId;
        _historyStart = page.StartOffset;
        state.SetHistory(new SessionHistory(page.TotalMessages - page.StartOffset, page.TotalMessages, page.NextCursor is not null));
    }

    /// <summary>Loads only the next older page. Single-flight and serialized with new/switch/rewind/restart.</summary>
    public Task LoadOlderMessagesAsync(CancellationToken ct = default) => ReadHistoryAsync(reload: false, ct);

    /// <summary>Explicitly replaces the visible history with a bounded recent tail; the UI explains the move to latest.</summary>
    public Task ReloadHistoryAsync(CancellationToken ct = default) => ReadHistoryAsync(reload: true, ct);

    private async Task ReadHistoryAsync(bool reload, CancellationToken ct)
    {
        var savedOnly = ReadsSavedHistory;
        var host = savedOnly ? _host : null;
        if (host is not null) await host.EnterSessionStartAsync(ct).ConfigureAwait(false);
        try { await ReadHistoryCoreAsync(reload, savedOnly, ct).ConfigureAwait(false); }
        finally { host?.ExitSessionStart(); }
    }

    private async Task ReadHistoryCoreAsync(bool reload, bool savedOnly, CancellationToken ct)
    {
        if (!await _lifecycle.WaitAsync(0, ct).ConfigureAwait(false)) return;
        RpcConnection? connection = null;
        long epoch = 0;
        long version = 0;
        try
        {
            connection = _omp?.Connection;
            if (!savedOnly && (connection is null || connection.CloseReason is not null)) return;
            if (savedOnly && _launchSpec is null) return;
            string? cursor;
            lock (_lock)
            {
                var history = _state.History;
                if (history.IsLoading || (!reload && (!history.HasOlder || history.NeedsReload))) return;
                if ((!savedOnly && _state.Phase != SessionPhase.Ready) || _state.SigningIn is not null)
                {
                    _state.SetHistory(history with { Error = "History is paused while omp is working. Try again when the session is idle." });
                    _changes.Writer.TryWrite(true);
                    return;
                }
                epoch = _state.TranscriptEpoch;
                cursor = reload ? null : _historyCursor;
                _state.SetHistory(history with { IsLoading = true, Error = null });
                version = _state.TranscriptRevision;
            }
            _changes.Writer.TryWrite(true);
            var page = savedOnly
                ? await (_host is { } host
                    ? host.ReadSessionPageAsync(this, _request.ResumeSessionFile!, cursor, HistoryPageSize, ct)
                    : _engine.ReadSessionPageAsync(_request.ResumeSessionFile!, _launchSpec!, _readyTimeout, cursor, HistoryPageSize, ct)).ConfigureAwait(false)
                : await connection!.GetMessagesPageAsync(cursor, HistoryPageSize, ct).ConfigureAwait(false);
            if (!savedOnly) await DrainHistoryEventsAsync(connection!, page.Timings, ct).ConfigureAwait(false);
            Mutate(state =>
            {
                if (state.TranscriptEpoch != epoch || !ReferenceEquals(connection, _omp?.Connection)) return;
                if (page.SessionId != state.SessionId)
                    throw new RpcCommandException("get_messages_page", "The session changed while history was loading.", "stale_cursor");
                if (reload)
                {
                    // A local send/shell command or a stream that advanced while the request was outstanding must not
                    // be replaced by its earlier snapshot. Leave all rows alone and let the reader retry at idle.
                    if (state.TranscriptRevision != version || (!savedOnly && state.Phase != SessionPhase.Ready))
                        throw new RpcCommandException("get_messages_page", "The session changed while history was loading.", "session_busy");
                    if (page.EndOffset != page.TotalMessages) throw new InvalidDataException("omp did not return the recent tail.");
                    if (!state.TryReplaceHistory(page.Messages, page.Timings.ResponseRead))
                        throw new RpcCommandException("get_messages_page", "The session changed while history was loading.", "session_busy");
                }
                else
                {
                    if (page.SnapshotId != _historySnapshot || page.EndOffset != _historyStart || page.TotalMessages != state.History.TotalMessages)
                        throw new RpcCommandException("get_messages_page", "The message snapshot changed.", "stale_cursor");
                    state.PrependHistory(page.Messages);
                }
                AdoptHistoryPage(state, page);
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Mutate(state => { if (state.TranscriptEpoch == epoch) state.SetHistory(state.History with { IsLoading = false }); });
            throw;
        }
        catch (Exception error) when (error is RpcCommandException or TimeoutException or RpcConnectionClosedException or IOException or InvalidDataException or NotSupportedException or ArgumentException or OmpStartException)
        {
            Mutate(state =>
            {
                if (state.TranscriptEpoch != epoch || !ReferenceEquals(connection, _omp?.Connection)) return;
                var stale = error is RpcCommandException { Code: "stale_cursor" };
                var busy = error is RpcCommandException { Code: "session_busy" };
                state.SetHistory(state.History with
                {
                    IsLoading = false, NeedsReload = state.History.NeedsReload || stale,
                    Error = stale ? "History changed since it was loaded. Reload recent messages to browse the new history; your current view has been kept."
                        : busy ? "History is paused while omp is working. Try again when the session is idle."
                        : error.Message,
                });
            });
        }
        finally
        {
            lock (_lock)
                if (_state.TranscriptEpoch == epoch && ReferenceEquals(connection, _omp?.Connection) && _state.History.IsLoading)
                {
                    _state.SetHistory(_state.History with { IsLoading = false });
                    _changes.Writer.TryWrite(true);
                }
            _lifecycle.Release();
        }
    }
}
