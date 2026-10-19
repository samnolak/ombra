using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// Session ownership (the runtime's writer lease, session-storage.ts): a chat whose file another Ombra window or a terminal
/// omp has open is shown read-only from its file, and "Open here" asks the holder to let go (<c>engine_take_session</c>)
/// before the chat is opened again. Nothing is taken by force, and nothing retries on its own.
/// </summary>
public sealed partial class SessionController
{
    /// <summary>Guarded by _lock. Non-null while the chat is read-only because another process owns its file.</summary>
    private SessionLease? _lease;

    /// <summary>The history comes from the saved file through the root host, not from a live logical session.</summary>
    private bool ReadsSavedHistory
    {
        get
        {
            if (_request.ReadOnly) return true;
            lock (_lock) return _lease is { State: SessionLeaseState.Held };
        }
    }

    /// <summary>After <c>session_lease_held</c>: the saved conversation, read-only, with who has it.</summary>
    private async Task ShowLeasedSessionAsync(OmpLaunchSpec spec, OmpSessionLeaseHolder? holder, CancellationToken ct)
    {
        _launchSpec = spec;
        lock (_lock) _lease = new SessionLease(SessionLeaseState.Held, holder);
        Mutate(s => s.Log(Now, "lease", $"session_lease_held: open in {holder?.Kind ?? "another process"} (pid {holder?.Pid?.ToString() ?? "unknown"})"));
        try { await LoadSavedHistoryAsync(_request.ResumeSessionFile!, spec, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or IOException or InvalidDataException or OmpStartException)
        {
            Mutate(s =>
            {
                s.ResetTranscript(newProcess: true);
                s.SessionFile = _request.ResumeSessionFile;
                s.Cwd = _request.WorkingDirectory;
                s.SetHistory(new SessionHistory(NeedsReload: true, Error: "The saved conversation could not be read: " + e.Message));
                s.SetPhase(SessionPhase.Stopped, Now);
            });
        }
    }

    /// <summary>The newest page of a saved session, shown without opening it (Stopped: nothing runs).</summary>
    private async Task LoadSavedHistoryAsync(string file, OmpLaunchSpec spec, CancellationToken ct)
    {
        var page = await _engine.ReadSessionPageAsync(file, spec, _readyTimeout, limit: HistoryPageSize, ct: ct).ConfigureAwait(false);
        var header = (await SessionCatalog.ReadSummaryAsync(file, ct).ConfigureAwait(false)).Summary;
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Mutate(s =>
        {
            s.ResetTranscript(newProcess: true);
            s.SessionFile = file;
            s.SessionId = page.SessionId;
            s.Cwd = header?.Cwd ?? _request.WorkingDirectory;
            s.SessionTitleHint = header is { IsEmpty: false } ? header.Title : null;
            s.ProtocolVersion = 2;
            s.ReplaceHistory(page.Messages);
            AdoptHistoryPage(s, page);
            s.SetPhase(SessionPhase.Stopped, Now);
        });
    }

    /// <summary>
    /// "Open here": asks whoever has this chat's file to let go (it finishes writing and closes it), then opens the chat
    /// here. False, with the reason in <see cref="SessionLease.Error"/>, when the holder did not let go in time.
    /// </summary>
    public async Task<bool> TakeOverSessionAsync(CancellationToken ct = default)
    {
        SessionLease lease;
        string? file;
        string? cwd;
        lock (_lock)
        {
            if (_lease is not { IsTakingOver: false } current) return false;
            // Lost: the file this chat had open (a switch may have moved it from the one it started with)
            file = current.State == SessionLeaseState.Lost ? _state.SessionFile ?? _request.ResumeSessionFile : _request.ResumeSessionFile;
            cwd = _state.Cwd ?? _request.WorkingDirectory;
            if (file is null) return false;
            lease = _lease = current with { IsTakingOver = true, Error = null };
        }
        _changes.Writer.TryWrite(true);
        try
        {
            var spec = _launchSpec;
            if (spec is null)
            {
                spec = await BuildEngineLaunchAsync(ct).ConfigureAwait(false);
                spec = _host?.UseEngineConfiguration(spec) ?? spec;
            }
            await _engine.TakeOverSessionAsync(file, spec, _readyTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or OmpStartException or IOException or OperationCanceledException)
        {
            var holder = (e as RpcCommandException)?.Code == "session_lease_held"
                ? OmpSessionLeaseHolder.FromErrorData(((RpcCommandException)e).ErrorData) ?? lease.Holder : lease.Holder;
            var error = (e as RpcCommandException)?.Code == "session_lease_held"
                ? "It is still open there and did not let go. Close it there, then try again."
                : e.Message;
            lock (_lock) if (ReferenceEquals(_lease, lease)) _lease = lease with { Holder = holder, IsTakingOver = false, Error = error };
            Mutate(s => s.Log(Now, "lease", "engine_take_session failed: " + e.Message));
            if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
            return false;
        }
        // Still read-only until the chat opens: StartCoreAsync clears the lease, or sets it again if another holder won
        await RestartAsync(_request with { ResumeSessionFile = file, WorkingDirectory = cwd }, ct).ConfigureAwait(false);
        lock (_lock)
            if (ReferenceEquals(_lease, lease)) _lease = lease with { IsTakingOver = false, Error = "The chat could not be opened here." };
        _changes.Writer.TryWrite(true);
        return Snapshot() is { Lease: null, Phase: not (SessionPhase.Faulted or SessionPhase.Stopped) };
    }
}
