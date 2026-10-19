using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// One window's shared engine and private logical chats. Closing a chat never stops its peers or the engine;
/// the engine stays owned by the window until disposal. Used on the UI thread except asynchronous shutdown.
/// </summary>
public sealed class SessionHost : IAsyncDisposable
{
    private readonly List<SessionController> _open = [];
    private readonly OmpEngine _engine;
    private readonly SemaphoreSlim _engineChange = new(1, 1);
    private readonly HashSet<SessionController> _restartAfterStop = [];
    private readonly object _admissionLock = new();
    private TaskCompletionSource? _startsDeferred;
    private OmpLaunchSpec? _launchConfiguration;
    private int _disposed;
    private readonly CancellationTokenSource _powerLifetime = new();
    private CancellationTokenSource? _resumeLifetime;
    private Task _resume = Task.CompletedTask;
    private long? _lastResume;

    /// <summary>Called on the owning UI thread. Invalidates reconciliation from an earlier sleep cycle.</summary>
    public void NotifySuspending()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _resumeLifetime?.Cancel();
        _lastResume = null;
    }

    /// <summary>Reconciles every hosted live chat, not just Active. Repeated native wake signals coalesce.</summary>
    public Task ReconcileAfterResumeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        var clock = Active.Clock;
        if (_lastResume is { } last && (!_resume.IsCompleted || clock.GetElapsedTime(last) < TimeSpan.FromSeconds(2))) return _resume;
        _lastResume = clock.GetTimestamp();
        _resumeLifetime?.Cancel();
        _resumeLifetime?.Dispose();
        _resumeLifetime = CancellationTokenSource.CreateLinkedTokenSource(_powerLifetime.Token);
        var token = _resumeLifetime.Token;
        var chats = _open.ToArray(); // UI-owned list; never enumerate it after an await.
        return _resume = Task.WhenAll(chats.Select(chat => chat.ReconcileAfterResumeAsync(token)));
    }

    public SessionHost(SessionController first)
    {
        _engine = first.TransferEngineOwnership(this);
        _launchConfiguration = first.EngineLaunchSpec;
        _open.Add(first);
        Active = first;
    }

    public SessionController Active { get; private set; }
    public IReadOnlyList<SessionController> All => _open;
    public int? EngineProcessId => _engine.ProcessId;
    public event Action<SessionController>? Closed;
    /// <summary>A removed chat has finished closing and no longer writes its session file.</summary>
    public event Action<SessionController>? Stopped;

    public SessionController? Find(string sessionFile) =>
        _open.FirstOrDefault(c => (c.SessionFile ?? c.CurrentLaunch.ResumeSessionFile) is { } f && SessionCatalog.SamePath(f, sessionFile));

    public async Task DeleteSessionAsync(string path, CancellationToken ct = default)
    {
        await EnterSessionStartAsync(ct).ConfigureAwait(false);
        try
        {
            var spec = _launchConfiguration ?? UseEngineConfiguration(await Active.BuildEngineLaunchAsync(ct).ConfigureAwait(false));
            await _engine.DeleteSessionAsync(path, spec, Active.EngineReadyTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OmpStartException or RpcProtocolException or Network.NetworkPrivacyException)
        {
            throw new IOException("The session was not deleted: " + error.Message, error);
        }
        finally { ExitSessionStart(); }
    }

    internal async Task<OmpMessagesPage> ReadSessionPageAsync(SessionController reader, string path, string? cursor,
        int limit, CancellationToken ct)
    {
        // Reader holds startup admission before taking its private lifecycle, matching StartAsync lock order.
        var spec = _launchConfiguration ?? UseEngineConfiguration(await reader.BuildEngineLaunchAsync(ct).ConfigureAwait(false));
        return await _engine.ReadSessionPageAsync(path, spec, reader.EngineReadyTimeout, cursor, limit, ct).ConfigureAwait(false);
    }

    /// <summary>Creates a private logical chat in the existing engine; the caller starts it.</summary>
    public SessionController Open(LaunchRequest request)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var chat = Active.Sibling(request);
        _open.Add(chat);
        Active = chat;
        return chat;
    }

    public void Activate(SessionController chat)
    {
        if (!_open.Contains(chat)) throw new InvalidOperationException("Not an open chat of this window.");
        Active = chat;
    }

    public Task CloseAsync(SessionController chat)
    {
        if (chat == Active) throw new InvalidOperationException("The chat shown cannot be closed; show another one first.");
        if (!_open.Remove(chat)) return Task.CompletedTask;
        _restartAfterStop.Remove(chat);
        Closed?.Invoke(chat);
        return CloseSessionAsync(chat);
    }

    private async Task CloseSessionAsync(SessionController chat)
    {
        await chat.DisposeAsync().ConfigureAwait(false);
        Stopped?.Invoke(chat);
    }

    /// <summary>No engine work or unanswered user interaction would be interrupted by a coordinated restart.</summary>
    public static bool IsIdle(SessionSnapshot snapshot) =>
        snapshot.Phase is SessionPhase.NotStarted or SessionPhase.Ready or SessionPhase.Stopped or SessionPhase.Faulted
        && snapshot.Dialogs.Count == 0 && snapshot.Queued.All(q => q.Recovered) && snapshot.SigningIn is null;

    /// <summary>
    /// Holds new logical starts while a process-settings change waits for busy chats. Existing chats keep running;
    /// waiting controllers have not entered Starting and do not prevent the coordinated idle restart.
    /// </summary>
    public void SetRestartPending(bool pending)
    {
        // A coordinated settings restart supersedes automatic recovery: an attempt already holding admission would
        // otherwise finish starting an engine on the configuration being replaced.
        if (pending) CancelAutoRecovery();
        lock (_admissionLock)
        {
            if (pending)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _startsDeferred ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                _startsDeferred?.TrySetResult();
                _startsDeferred = null;
            }
        }
    }

    internal async Task EnterSessionStartAsync(CancellationToken ct)
    {
        while (true)
        {
            Task? deferred;
            lock (_admissionLock)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                deferred = _startsDeferred?.Task;
            }
            if (deferred is not null) await deferred.WaitAsync(ct).ConfigureAwait(false);
            await _engineChange.WaitAsync(ct).ConfigureAwait(false);
            lock (_admissionLock)
            {
                if (_startsDeferred is null && Volatile.Read(ref _disposed) == 0)
                {
                    if (_engine.ProcessId is null) _launchConfiguration = null;
                    return;
                }
            }
            _engineChange.Release();
        }
    }

    internal void ExitSessionStart() => _engineChange.Release();

    // Guarded by startup admission (_engineChange): the clock timestamp of the last automatic recovery whose shared
    // engine did not come back. Chats that crashed with it adopt that outcome instead of each starting a process.
    private long? _engineRecoveryFailedAt;

    /// <summary>Caller holds startup admission. True when another chat's automatic engine restart failed after
    /// <paramref name="scheduledAt"/>: this chat counts the same failed attempt and backs off without a start of its own.</summary>
    internal bool EngineRecoveryFailedSince(long scheduledAt) => _engine.ProcessId is null && _engineRecoveryFailedAt > scheduledAt;

    /// <summary>Caller holds startup admission.</summary>
    internal void NoteEngineRecoveryFailed(long at) => _engineRecoveryFailedAt = at;

    internal OmpLaunchSpec UseEngineConfiguration(OmpLaunchSpec spec)
    {
        lock (_admissionLock)
        {
            // A settings save may arrive between independent launch factories. The root generation stays
            // immutable until StopEngineCoreAsync; only cwd and session arguments vary between its chats.
            if (_launchConfiguration is not { } active) return _launchConfiguration = spec;
            if (spec.FileName == active.FileName && ReferenceEquals(spec.Arguments, active.Arguments)
                && ReferenceEquals(spec.Environment, active.Environment)) return spec;
            return spec with { FileName = active.FileName, Arguments = active.Arguments, Environment = active.Environment };
        }
    }

    /// <summary>
    /// Applies process-wide runtime/environment changes only after every chat is idle. Sessions stopped for an
    /// installation are reopened too; already faulted sessions remain faulted until the user explicitly recovers them.
    /// </summary>
    public async Task RestartEngineAsync(CancellationToken ct = default)
    {
        CancelAutoRecovery();
        await _engineChange.WaitAsync(ct);
        try
        {
            await StopEngineCoreAsync(ct);
            var resume = _restartAfterStop.Where(_open.Contains).ToArray();
            _restartAfterStop.Clear();
            await Task.WhenAll(resume.Select(chat => chat.RecoverAfterEngineRestartAsync(ct)));
        }
        finally { _engineChange.Release(); }
    }

    /// <summary>Closes all idle logical sessions and releases the engine's immutable launch configuration.</summary>
    public async Task StopEngineAsync(CancellationToken ct = default)
    {
        CancelAutoRecovery();
        await _engineChange.WaitAsync(ct);
        try { await StopEngineCoreAsync(ct); }
        finally { _engineChange.Release(); }
    }

    private async Task StopEngineCoreAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();
        var chats = _open.ToArray();
        if (chats.Any(chat => !IsIdle(chat.Snapshot())))
            throw new InvalidOperationException("The shared engine can restart only after all chats and sign-ins are idle.");
        foreach (var chat in chats)
            if (chat.Snapshot().Phase == SessionPhase.Ready) _restartAfterStop.Add(chat);
        await Task.WhenAll(chats.Select(chat => chat.StopAsync()));
        await _engine.StopAsync(ct);
        lock (_admissionLock) _launchConfiguration = null;
    }

    /// <summary>Owner-thread only. Cancels every chat's pending or running automatic restart, including one holding
    /// startup admission, so an engine-wide change or window close never waits for its engine start.</summary>
    private void CancelAutoRecovery()
    {
        foreach (var chat in _open.ToArray()) chat.CancelAutoRecover();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _powerLifetime.Cancel();
        _resumeLifetime?.Dispose();
        SetRestartPending(false);
        CancelAutoRecovery();
        await _engineChange.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await Task.WhenAll(_open.Select(CloseSessionAsync)).ConfigureAwait(false); }
            finally { await _engine.DisposeAsync().ConfigureAwait(false); }
        }
        finally { _engineChange.Release(); }
    }
}
