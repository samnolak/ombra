using System.Diagnostics;
using System.Threading.Channels;
using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// One logical engine session: lifecycle, protocol negotiation, the event pump and private conversation state.
/// All omp I/O happens on background tasks; the UI only calls the async commands and reads snapshots.
/// State keeps advancing whether or not anyone is looking (minimized window, blocked UI thread).
/// </summary>
public sealed partial class SessionController : IAsyncDisposable
{
    private readonly Func<LaunchRequest, CancellationToken, Task<OmpLaunchSpec>> _launch;
    private readonly OmpEngine _engine;
    private bool _ownsEngine;
    private SessionHost? _host;
    private OmpLaunchSpec? _launchSpec;
    private LaunchRequest _request;
    private readonly TimeSpan _readyTimeout;
    // Start, restart and session switches never overlap.
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly ConversationState _state;
    private readonly object _lock = new();
    // Capacity-1 "something changed" signal: bursts of frames collapse into one wake-up for the consumer.
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private OmpSession? _omp;
    private Task? _pump;
    private int _settleProbeRunning;
    private long _lastFrameTimestamp;
    private volatile bool _stopRequested;

    /// <summary>How long the stream must be quiet after a non-terminal agent_end before get_state is asked.</summary>
    public TimeSpan SettleQuietPeriod { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>A fixed launch (tests, tools): restarts reuse it whatever the request says.</summary>
    public SessionController(OmpLaunchSpec spec, TimeSpan? readyTimeout = null, TimeProvider? clock = null)
        : this(_ => spec, new LaunchRequest(), readyTimeout, clock)
    {
    }

    /// <param name="launch">Builds omp's command line for a launch request (project folder, session to resume, approval mode).</param>
    public SessionController(Func<LaunchRequest, OmpLaunchSpec> launch, LaunchRequest initial, TimeSpan? readyTimeout = null, TimeProvider? clock = null)
        : this((r, _) => Task.FromResult(launch(r)), initial, readyTimeout, clock)
    {
    }

    /// <param name="launch">Builds omp's command line for a launch request, with work before omp starts (the app's
    /// network privacy asks omp's runtime which providers the user added); a failure fails the start.</param>
    public SessionController(Func<LaunchRequest, CancellationToken, Task<OmpLaunchSpec>> launch, LaunchRequest initial, TimeSpan? readyTimeout = null, TimeProvider? clock = null)
        : this(launch, initial, new OmpEngine(), ownsEngine: true, readyTimeout, clock)
    {
    }

    private SessionController(Func<LaunchRequest, CancellationToken, Task<OmpLaunchSpec>> launch, LaunchRequest initial,
        OmpEngine engine, bool ownsEngine, TimeSpan? readyTimeout, TimeProvider? clock)
    {
        _engine = engine;
        _ownsEngine = ownsEngine;
        _launch = launch;
        _request = initial;
        _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(60);
        _clock = clock ?? TimeProvider.System;
        _state = new ConversationState(_clock);
        _lastFrameTimestamp = _clock.GetTimestamp();
        _state.QueueChanged += () =>
        {
            if (_request.ReadOnly) return;
            try { QueueCheckpoint?.Invoke(_state.Queued.ToArray()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _state.AddNotice(NoticeLevel.Error, "Queue recovery could not be saved: " + e.Message);
            }
        };
        _state.PromptConfirmed += ConfirmSubmission;
    }

    /// <summary>What the running omp was started with.</summary>
    public LaunchRequest CurrentLaunch => _request;

    /// <summary>Consumer-owned launch preparation, inside the startup failure boundary (or before restart validation
    /// so an invalid target cannot stop a live process). Runs once per launch and follows sibling controllers;
    /// it must not send input or change conversation state.</summary>
    public Action<LaunchRequest>? PrepareLaunch { get; set; }

    /// <summary>A private logical chat in the same engine, with the same launch builder and user approval rules.</summary>
    public SessionController Sibling(LaunchRequest request) =>
        new(_launch, request, _engine, ownsEngine: false, _readyTimeout, _clock)
        { SettleQuietPeriod = SettleQuietPeriod, WakeResponseTimeout = WakeResponseTimeout, WakeQuietPeriod = WakeQuietPeriod,
            ApprovalRules = ApprovalRules, PrepareLaunch = PrepareLaunch, AutoRecover = AutoRecover, HostTools = HostTools, _host = _host };

    // Root-only work needs runtime configuration, not a logical chat, draft preparation or transcript reset.
    internal Task<OmpLaunchSpec> BuildEngineLaunchAsync(CancellationToken ct) =>
        _launch(_request with { ResumeSessionFile = null }, ct);
    internal TimeSpan EngineReadyTimeout => _readyTimeout;

    internal OmpEngine TransferEngineOwnership(SessionHost host)
    {
        if (!_ownsEngine) throw new InvalidOperationException("The engine already belongs to a session host.");
        _ownsEngine = false;
        _host = host;
        return _engine;
    }

    internal OmpLaunchSpec? EngineLaunchSpec => _launchSpec;

    /// <summary>The session file omp has open now (null before omp reported it). Cheaper than a whole snapshot.</summary>
    public string? SessionFile
    {
        get { lock (_lock) return _state.SessionFile; }
    }

    /// <summary>Wakes whenever the snapshot may have changed. Read it, then call <see cref="Snapshot"/>.</summary>
    public ChannelReader<bool> Changes => _changes.Reader;

    /// <summary>Shared engine diagnostics (prefixed [engine]), raised on a background thread; not chat output.</summary>
    public event Action<string>? StderrLine;

    public int? ProcessId => _omp?.ProcessId;
    /// <summary>Durable consumer checkpoint, called under the state lock before queued input is sent and after delivery.</summary>
    public Action<IReadOnlyList<QueuedMessage>>? QueueCheckpoint { get; set; }
    public Action<string>? SubmissionDelivered { get; set; }
    /// <summary>Records omp's delivery of a durable submission (its prompt echoed, or consumed by a local command).</summary>
    private void ConfirmSubmission(string id)
    {
        try { SubmissionDelivered?.Invoke(id); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _state.AddNotice(NoticeLevel.Error, "Delivery checkpoint could not be saved: " + e.Message);
        }
    }

    /// <summary>Guarded by _lock: the latest prompt (or interrupt-and-send). Only its acknowledgement may settle the
    /// pending run; an older one, superseded while omp was admitting it, settles its own row alone.</summary>
    private long _promptTicket;
    /// <summary>Guarded by _lock: the latest prompt's row key while omp has not acknowledged it (0 once it has). An
    /// interrupt-and-send supersedes only this one: omp may still cancel it.</summary>
    private long _promptAwaitingAck;

    public void AcknowledgeRecovered(long sequence) => Mutate(s =>
    {
        if (s.Queued.Any(q => q.Seq == sequence && q.Recovered)) s.RemoveQueued(sequence);
    });

    public static void ValidateMessage(string text, IReadOnlyList<ImageAttachment> images) =>
        OmpCommands.ValidateUserInput(text, Wire(images));
    public RpcConnection? Connection => _omp?.Connection;

    public SessionSnapshot Snapshot()
    {
        lock (_lock) return _state.Snapshot() with { IsReadOnly = _request.ReadOnly, ApprovalMode = _request.ApprovalMode is null ? null : OmpRuntimeOptions.EffectiveApprovalMode(_request.ApprovalMode), Lease = _lease };
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        CancelAutoRecover();
        if (_host is { } host) await host.EnterSessionStartAsync(ct).ConfigureAwait(false);
        try
        {
            await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
            try { await StartCoreAsync(ct).ConfigureAwait(false); }
            finally { _lifecycle.Release(); }
        }
        finally { _host?.ExitSessionStart(); }
    }

    private async Task StartCoreAsync(CancellationToken ct, bool launchPrepared = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_omp?.Connection is { CloseReason: null }) return;
        await DisposeSessionAsync().ConfigureAwait(false);
        _stopRequested = false;
        lock (_lock) _lease = null;
        Mutate(s => s.SetPhase(SessionPhase.Starting, Now));
        OmpLaunchSpec? spec = null;
        try
        {
            // Consume consent even if preparation fails; retrying is never another implicit override.
            if (_request.ReadOnly)
            {
                await StartReadOnlyAsync(ct).ConfigureAwait(false);
                return;
            }
            var launchRequest = _request;
            _request = _request with { ResumeOnCurrentModel = false };
            ct.ThrowIfCancellationRequested();
            if (!launchPrepared) PrepareLaunch?.Invoke(launchRequest);
            spec = await _launch(launchRequest, ct).ConfigureAwait(false);
            spec = _host?.UseEngineConfiguration(spec) ?? spec;
            _omp = await _engine.OpenSessionAsync(spec, _readyTimeout, ct).ConfigureAwait(false);
            _launchSpec = spec;
            if (Volatile.Read(ref _disposed) != 0) throw new OperationCanceledException("The chat closed while its engine session opened.");
            _omp.StderrLine += OnStderr;
            var conn = _omp.Connection;
            var version = await conn.NegotiateAsync(ct).ConfigureAwait(false);
            await conn.SetDeltaMessageUpdatesAsync(ct).ConfigureAwait(false);
            await EnableAskFormAsync(conn, ct).ConfigureAwait(false); // SessionController.AskTodos.cs
            await RegisterHostToolsAsync(conn, ct).ConfigureAwait(false); // SessionController.HostTools.cs
            Mutate(s =>
            {
                s.ResetTranscript(newProcess: true);
                s.ProtocolVersion = version;
                s.Log(Now, "ready", $"shared engine pid {_omp.ProcessId}, protocol v{version}, supported [{string.Join(',', _omp.Ready.SupportedProtocolVersions)}]");
            });
            BeginHistoryEvents(conn);
            var process = _omp;
            _pump = Task.Run(() => PumpAsync(process));
            await NotifyEditorConnectionAsync().ConfigureAwait(false);
            await LoadSessionAsync(conn, spec.WorkingDirectory, ct).ConfigureAwait(false);
            _ = SubscribeSubagentsAsync(conn); // SessionController.Subagents.cs
            NoteLive(); // SessionController.AutoRecover.cs
        }
        catch (OmpStartException e) when (e.Code == "session_lease_held" && spec is not null && _request.ResumeSessionFile is not null)
        {
            // Not a failure: another window or a terminal omp has the chat open. Show it read-only from its file,
            // with "Open here" (SessionController.Lease.cs); never retried automatically.
            _engineStartFailed = false;
            await DisposeSessionAsync().ConfigureAwait(false);
            await ShowLeasedSessionAsync(spec, e.LeaseHolder, ct).ConfigureAwait(false);
        }
        catch (OmpStartException e)
        {
            _engineStartFailed = e.EngineStartFailed; // SessionController.AutoRecover.cs
            await DisposeSessionAsync().ConfigureAwait(false);
            var detail = string.IsNullOrWhiteSpace(e.StderrTail) ? "" : "\n" + LastLines(e.StderrTail, 12);
            Mutate(s => s.Fail(e.Message + detail, Now, ClassifyStartFailure(e), e.UnavailableSessionModel, e.Text));
            throw;
        }
        catch (OperationCanceledException)
        {
            // Keep the lifecycle until the old pump has consumed its final frames and exit.
            // Otherwise an immediate retry can receive old transcript events or an old-pump fault.
            await DisposeSessionAsync().ConfigureAwait(false);
            Mutate(s => s.SetPhase(SessionPhase.Stopped, Now));
            throw;
        }
        catch (Exception e)
        {
            bool startFailed;
            lock (_lock) startFailed = _state.Phase == SessionPhase.Starting;
            await DisposeSessionAsync().ConfigureAwait(false);
            Mutate(s =>
            {
                if (startFailed) s.SetPhase(SessionPhase.Starting, Now);
                s.Fail("Starting omp failed: " + e.Message, Now, text: new ClientText("Starting omp failed: {0}", e.Message));
            });
            throw;
        }
    }

    private async Task DisposeSessionAsync()
    {
        _stopRequested = true;
        CancelHostToolCalls();
        if (_omp is { } omp)
        {
            omp.StderrLine -= OnStderr;
            await omp.DisposeAsync().ConfigureAwait(false);
        }
        if (_pump is { } pump) await pump.ConfigureAwait(false);
        _omp = null;
        _pump = null;
        await NotifyEditorConnectionAsync().ConfigureAwait(false);
    }

    /// <summary>Replaces the transcript with omp's current session (after start, switch or new session).</summary>
    private async Task LoadSessionAsync(RpcConnection conn, string? launchDirectory, CancellationToken ct, bool newProcess = true)
    {
        var state = await conn.GetStateAsync(ct).ConfigureAwait(false);
        if (newProcess) NoteProjectReport(state); // SessionController.Trust.cs
        await DrainHistoryEventsAsync(conn, state.Timings!, ct).ConfigureAwait(false);
        var header = state.SessionFile is { } file
            ? (await SessionCatalog.ReadSummaryAsync(file, ct).ConfigureAwait(false)).Summary : null;
        IReadOnlyList<SlashCommand>? commands = null;
        try { commands = ConversationState.ParseCommands(await conn.GetAvailableCommandsAsync(ct).ConfigureAwait(false)); }
        catch (Exception e) when (e is RpcCommandException or TimeoutException) { } // the startup update frame has them too
        Mutate(s =>
        {
            if (!newProcess) s.ResetHistorySession(_historyTransitionKey);
            s.ApplyHistorySessionMetadata(state, Now);
            if (commands is { Count: > 0 }) s.Commands = commands;
            s.Cwd = header?.Cwd is { } savedCwd && Directory.Exists(savedCwd)
                ? savedCwd : Path.GetFullPath(launchDirectory ?? Environment.CurrentDirectory);
            s.SessionTitleHint = header is { IsEmpty: false } ? header.Title : null;
            _historyCursor = _historySnapshot = null;
            _historyStart = 0;
            s.SetHistory(new SessionHistory(TotalMessages: state.MessageCount, IsLoading: true));
        });
        try
        {
            var page = await conn.GetMessagesPageAsync(limit: HistoryPageSize, ct: ct).ConfigureAwait(false);
            await DrainHistoryEventsAsync(conn, page.Timings, ct).ConfigureAwait(false);
            Mutate(s =>
            {
                if (page.SessionId != state.SessionId || page.EndOffset != page.TotalMessages)
                    throw new InvalidDataException("omp returned history for a different session or an incomplete tail.");
                if (conn.CloseReason is not null) throw new RpcConnectionClosedException("omp stopped while history was loading.", conn.CloseReason);
                if (s.Phase is SessionPhase.Running or SessionPhase.Aborting || !s.TryReplaceHistory(page.Messages, page.Timings.ResponseRead))
                    throw new RpcCommandException("get_messages_page", "The session started changing while history was loading.", "session_busy");
                AdoptHistoryPage(s, page);
                s.SetPhase(SessionPhase.Ready, Now);
            });
        }
        catch (RpcCommandException error) when (error.Code == "session_busy")
        {
            Mutate(s =>
            {
                s.SetHistory(s.History with { IsLoading = false, NeedsReload = true,
                    Error = "Recent history is unavailable while omp is working. Live messages remain visible; reload recent messages when idle." });
                if (s.Phase == SessionPhase.Starting) s.SetPhase(state.IsStreaming ? SessionPhase.Running : SessionPhase.Ready, Now);
            });
        }
        catch (Exception error) when (error is RpcCommandException or TimeoutException or RpcConnectionClosedException or InvalidDataException or NotSupportedException)
        {
            Mutate(s => s.SetHistory(s.History with { IsLoading = false, NeedsReload = true, Error = error.Message }));
            throw;
        }
    }

    /// <summary>Starts a fresh session in the current project.</summary>
    public Task NewSessionAsync(CancellationToken ct = default) =>
        SessionChangeAsync(async conn =>
        {
            if (!await conn.NewSessionAsync(ct).ConfigureAwait(false))
            {
                Mutate(s => s.AddNotice(NoticeLevel.Warning, "An extension cancelled the new session."));
                return;
            }
            await LoadSessionAsync(conn, _request.WorkingDirectory, ct, newProcess: false).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// Opens a saved session. Same project: switch the logical session in place (<c>switch_session</c>).
    /// Other project or unavailable saved model: replace this logical lifetime in the shared engine.
    /// </summary>
    public async Task OpenSessionAsync(string sessionFile, string cwd, CancellationToken ct = default)
    {
        var current = Snapshot();
        if (SessionCatalog.SamePath(sessionFile, current.SessionFile)) return;
        if (current.Phase is SessionPhase.Running or SessionPhase.Aborting)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Warning, "Stop the current run before changing sessions."));
            return;
        }
        if (current.Phase == SessionPhase.Ready && SessionCatalog.SamePath(cwd, current.Cwd))
        {
            var declined = false;
            await SessionChangeAsync(async conn =>
            {
                try
                {
                    if (await conn.SwitchSessionAsync(sessionFile, ct).ConfigureAwait(false))
                        await LoadSessionAsync(conn, _request.WorkingDirectory, ct, newProcess: false).ConfigureAwait(false);
                    else
                        declined = true;
                }
                catch (RpcCommandException error) when (error.Code is "session_model_unavailable" or "session_lease_held")
                {
                    // The switch rolled back. Open a replacement logical lifetime so the failed target owns its
                    // actionable startup state (a model to choose, or a chat open elsewhere shown read-only).
                    declined = true;
                }
            }, ct).ConfigureAwait(false);
            if (!declined) return;
            // A declined or unrestorable switch opens a replacement logical lifetime.
            // The shared engine and peer chats remain alive; startup owns any recovery card.
            Mutate(s => s.Log(Now, "switch", "Opening a replacement logical session after switch_session"));
        }
        await RestartAsync(_request with { WorkingDirectory = cwd, ResumeSessionFile = sessionFile }, ct).ConfigureAwait(false);
    }

    /// <summary>Opens a project folder: omp restarts there with a new session.</summary>
    public Task OpenFolderAsync(string folder, CancellationToken ct = default) =>
        RestartAsync(_request with { WorkingDirectory = folder, ResumeSessionFile = null }, ct);

    public async Task RenameSessionAsync(string name, CancellationToken ct = default)
    {
        var conn = RequireConnection();
        if (RefuseWhileSigningIn()) return;
        try
        {
            await conn.SetSessionNameAsync(name, ct).ConfigureAwait(false);
            Mutate(s => s.SessionName = name);
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or ArgumentException)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Error, "Rename failed: " + e.Message));
        }
    }

    /// <summary>
    /// Closes this logical session and opens its replacement (other project, approval mode or manual crash recovery).
    /// Peers and the shared engine remain alive. The transcript is rebuilt from the session's persisted history.
    /// </summary>
    /// <param name="force">Also when a run is active (the user asked to force-stop a stuck run).</param>
    public async Task RestartAsync(LaunchRequest request, CancellationToken ct = default, bool force = false)
    {
        CancelAutoRecover();
        if (_host is { } host) await host.EnterSessionStartAsync(ct).ConfigureAwait(false);
        try { await RestartCoreAsync(request, ct, force).ConfigureAwait(false); }
        finally { _host?.ExitSessionStart(); }
    }

    /// <param name="automatic">An automatic recovery (SessionController.AutoRecover.cs): nothing to do once the chat is live again.</param>
    private async Task RestartCoreAsync(LaunchRequest request, CancellationToken ct, bool force = false, bool automatic = false)
    {
        if (_request.ReadOnly || request.ReadOnly)
            throw new InvalidOperationException("Read-only history cannot be restarted as a live session. Open a separate chat instead.");
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (automatic && IsLive()) return;
            // Never kill a run the user did not stop, and never stop a working omp for a launch that cannot work.
            if (!force && Snapshot().Phase is SessionPhase.Running or SessionPhase.Aborting)
            {
                Mutate(s => s.AddNotice(NoticeLevel.Warning, "Stop the current run before changing sessions."));
                return;
            }
            var launchPrepared = false;
            if (request.WorkingDirectory is { } dir && !Directory.Exists(dir))
            {
                // A recoverable draft can own a missing chat folder. Prepare it before deciding whether restart
                // is valid, while keeping the old process intact if preparation or validation fails.
                if (PrepareLaunch is { } prepare)
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        prepare(request);
                        launchPrepared = true;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        Mutate(s =>
                        {
                            var text = new ClientText("Starting omp failed: {0}", e.Message);
                            var error = text.ToString();
                            if (s.Phase is SessionPhase.NotStarted or SessionPhase.Stopped or SessionPhase.Faulted)
                            {
                                s.SetPhase(SessionPhase.Starting, Now);
                                s.Fail(error, Now, text: text);
                            }
                            else s.AddNotice(NoticeLevel.Error, error, text);
                        });
                        return;
                    }
                }
                if (!Directory.Exists(dir))
                {
                    Mutate(s => s.AddNotice(NoticeLevel.Error, $"Folder not found: {dir}"));
                    return;
                }
            }
            if (request.ResumeSessionFile is { } file && !File.Exists(file))
            {
                Mutate(s => s.AddNotice(NoticeLevel.Error, $"Session file not found: {file}"));
                return;
            }
            if (_omp is { } old)
            {
                await StopSessionAsync().ConfigureAwait(false);
                await old.DisposeAsync().ConfigureAwait(false);
            }
            _omp = null;
            _pump = null;
            _stopRequested = false;
            _request = request;
            try { await StartCoreAsync(ct, launchPrepared).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Reported in the transcript by StartCoreAsync; the caller only needs to know it did not start.
            }
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Models omp can use now; empty when omp is not running or the request fails (reported as a notice).</summary>
    public async Task<IReadOnlyList<OmpModel>> GetModelsAsync(CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return [];
        try { return await conn.GetAvailableModelsAsync(ct).ConfigureAwait(false); }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Error, "Could not list models: " + e.Message));
            return [];
        }
    }

    /// <summary>Switches the session's model (recorded by omp in the session).</summary>
    public async Task SetModelAsync(OmpModel model, CancellationToken ct = default)
    {
        var conn = RequireConnection();
        if (RefuseWhileSigningIn()) return;
        long epoch;
        long transition;
        lock (_lock) { epoch = _state.TranscriptEpoch; transition = _historyTransitionKey; }
        try
        {
            await conn.SetModelAsync(model.Provider, model.Id, ct).ConfigureAwait(false);
            var state = await conn.GetStateAsync(ct).ConfigureAwait(false);
            await DrainHistoryEventsAsync(conn, state.Timings!, ct).ConfigureAwait(false);
            Mutate(s =>
            {
                if (!ReferenceEquals(conn, Connection) || s.TranscriptEpoch != epoch
                    || _historyTransitionKey != transition || state.SessionId != s.SessionId || _disposed != 0 || _stopRequested) return;
                var readAt = state.Timings!.ResponseRead;
                if (s.HistoryModelReadAt <= readAt) s.Model = state.ModelProvider is null ? model.Key : $"{state.ModelProvider}/{state.ModelId}";
                s.ApplyUsageLimit(state);
                if (s.HistoryThinkingReadAt <= readAt) s.ThinkingLevel = state.ThinkingLevel;
            });
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Error, $"Could not switch to {model.Key}: {e.Message}"));
        }
    }

    /// <summary>The thinking levels the current model accepts; null when omp cannot say (older omp, not running).</summary>
    public async Task<IReadOnlyList<string>?> GetThinkingLevelsAsync(CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return null;
        try
        {
            var levels = await conn.GetAvailableThinkingLevelsAsync(ct).ConfigureAwait(false);
            return levels.Count > 0 ? levels : null;
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sets the session's thinking level. While omp works or input waits, the level is kept for the next message
    /// (<see cref="SessionSnapshot.NextThinkingLevel"/>): set now, omp would apply it to the current run.
    /// </summary>
    public async Task SetThinkingLevelAsync(string level, CancellationToken ct = default)
    {
        var conn = RequireConnection();
        if (RefuseWhileSigningIn()) return;
        var later = false;
        Mutate(s =>
        {
            var busy = _dispatchingDeferred is not null || _applyingNextThinking;
            later = busy || s.Phase != SessionPhase.Ready || s.Queued.Any(q => !q.Recovered);
            s.NextThinkingLevel = later && (busy || level != s.ThinkingLevel || s.QueueCarriesThinking) ? level : null;
        });
        if (later) return;
        try
        {
            var applied = await ApplyThinkingLevelAsync(conn, level, ct).ConfigureAwait(false);
            Mutate(s => s.ThinkingLevel = applied);
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Error, $"Could not set thinking to {level}: {e.Message}"));
        }
    }

    /// <summary><c>set_thinking_level</c>, then the level omp actually uses (it can clamp to what the model offers).</summary>
    private static async Task<string> ApplyThinkingLevelAsync(RpcConnection conn, string level, CancellationToken ct)
    {
        await conn.SetThinkingLevelAsync(level, ct).ConfigureAwait(false);
        var state = await conn.GetStateAsync(ct).ConfigureAwait(false);
        return state.ThinkingLevel ?? level;
    }

    public async Task<IReadOnlyList<OmpLoginProvider>> GetLoginProvidersAsync(CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return [];
        try { return await conn.GetLoginProvidersAsync(ct).ConfigureAwait(false); }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Error, "Could not list sign-in providers: " + e.Message));
            return [];
        }
    }

    /// <summary>omp's own sign-in flow; the browser link and any code prompt arrive as extension UI requests.</summary>
    public async Task<bool> LoginAsync(string providerId, CancellationToken ct = default)
    {
        var conn = RequireConnection();
        long epoch, transition;
        lock (_lock) { epoch = _state.TranscriptEpoch; transition = _historyTransitionKey; }
        // omp runs commands one after another: until the sign-in ends, every other command would wait behind it
        // (and could run long after the client gave up on it). The client holds its own commands meanwhile.
        Mutate(s => s.SigningIn = providerId);
        try
        {
            await conn.LoginAsync(providerId, ct).ConfigureAwait(false);
            RefreshAccountUsage(conn, epoch, transition);
            Mutate(s => s.AddNotice(NoticeLevel.Info, $"Signed in: {providerId}"));
            return true;
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or OperationCanceledException)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Error, $"Sign-in to {providerId} failed: {e.Message}"));
            return false;
        }
        finally
        {
            Mutate(s => { if (s.SigningIn == providerId) s.SigningIn = null; });
        }
    }

    /// <summary>The credentials omp has stored for a provider and can remove, active first.</summary>
    /// <exception cref="OmpNotRunningException" />
    /// <exception cref="RpcCommandException">omp refused, or has no such command (before 18.8.0).</exception>
    public Task<IReadOnlyList<OmpLogoutAccount>> GetLogoutAccountsAsync(string providerId, CancellationToken ct = default) =>
        RequireConnection().GetLogoutAccountsAsync(providerId, ct);

    /// <summary>
    /// omp's own sign-out: removes one stored credential and refreshes the provider's models. Returns what still
    /// signs omp in to the provider (an API key in the environment…), if anything.
    /// </summary>
    /// <exception cref="OmpNotRunningException" />
    /// <exception cref="RpcCommandException">omp refused (the credential is gone already), or has no such command.</exception>
    public async Task<string?> LogoutAsync(string providerId, OmpLogoutAccount account, CancellationToken ct = default)
    {
        var conn = RequireConnection();
        long epoch, transition;
        lock (_lock) { epoch = _state.TranscriptEpoch; transition = _historyTransitionKey; }
        var remaining = await conn.LogoutAsync(providerId, account.CredentialId, ct).ConfigureAwait(false);
        RefreshAccountUsage(conn, epoch, transition);
        Mutate(s => s.AddNotice(NoticeLevel.Info, $"Signed out: {providerId} ({account.Label})"));
        return remaining;
    }

    /// <summary>The session's MCP servers: connection, tool count and sign-in state (owned runtime).</summary>
    /// <exception cref="OmpNotRunningException" />
    /// <exception cref="RpcCommandException">omp has no such command.</exception>
    public Task<IReadOnlyList<OmpMcpServer>> GetMcpServersAsync(CancellationToken ct = default) =>
        RequireConnection().GetMcpServersAsync(ct);

    /// <summary>
    /// Signs in to an MCP server with OAuth; omp sends its authorization page as <c>open_url</c> with
    /// <c>mcpServer</c>. On success the server's sign-in prompt goes away.
    /// </summary>
    /// <exception cref="OmpNotRunningException" />
    /// <exception cref="RpcCommandException">omp refused, or the sign-in was cancelled (code <c>mcp_login_cancelled</c>).</exception>
    public async Task<OmpMcpServer?> McpLoginAsync(string serverName, CancellationToken ct = default)
    {
        var server = await RequireConnection().McpLoginAsync(serverName, ct).ConfigureAwait(false);
        Mutate(s => s.ClearMcpAuthRequest(serverName));
        return server;
    }

    /// <summary>Stops a running <see cref="McpLoginAsync"/>; false when none was running.</summary>
    public Task<bool> McpLoginCancelAsync(string serverName, CancellationToken ct = default) =>
        RequireConnection().McpLoginCancelAsync(serverName, ct);

    /// <summary>Reconnects one server, or every server with null; the prompts of servers now signed in go away.</summary>
    public async Task<IReadOnlyList<OmpMcpServer>> McpReconnectAsync(string? serverName, CancellationToken ct = default)
    {
        var servers = await RequireConnection().McpReconnectAsync(serverName, ct).ConfigureAwait(false);
        Mutate(s => { foreach (var m in servers) if (!m.NeedsSignIn && m.Status == "connected") s.ClearMcpAuthRequest(m.Name); });
        return servers;
    }

    /// <summary>Signs out of an MCP server: omp forgets its stored sign-in and disconnects it.</summary>
    /// <exception cref="OmpNotRunningException" />
    /// <exception cref="RpcCommandException">omp refused (an older omp has no such command).</exception>
    public Task<OmpMcpServer?> McpLogoutAsync(string serverName, CancellationToken ct = default) =>
        RequireConnection().McpLogoutAsync(serverName, ct);

    /// <summary>The user closed a connector's sign-in prompt without signing in.</summary>
    public void DismissMcpAuthRequest(string serverName) => Mutate(s => s.ClearMcpAuthRequest(serverName));

    private void RefreshAccountUsage(RpcConnection conn, long epoch, long transition)
    {
        Mutate(s =>
        {
            if (!ReferenceEquals(conn, Connection) || s.TranscriptEpoch != epoch
                || _historyTransitionKey != transition || _disposed != 0 || _stopRequested) return;
            // Response-read order alone cannot reject a pre-login read delivered after login completes.
            s.InvalidateAccountUsage(Stopwatch.GetTimestamp());
            RequestStateRefresh(conn);
        });
    }

    /// <summary>
    /// Changes <c>--approval-mode</c>. It is a launch flag, so omp restarts on the same session. Choosing
    /// <c>yolo</c> is the user's explicit decision; the client never picks it by itself.
    /// </summary>
    public Task SetApprovalModeAsync(string mode, CancellationToken ct = default) =>
        RestartAsync(_request with { ApprovalMode = mode, ResumeSessionFile = ExistingSessionFile() }, ct);

    /// <summary>Starts omp again after a crash or a failed start, resuming the session it had open.</summary>
    public Task RecoverAsync(CancellationToken ct = default) =>
        RestartAsync(_request with { ResumeSessionFile = ExistingSessionFile() }, ct);

    /// <summary>The user explicitly accepts replacing an unavailable saved model with today's launch preference.</summary>
    public Task OpenOnCurrentModelAsync(CancellationToken ct = default)
    {
        if (Snapshot().StartProblem != StartProblem.SessionModelUnavailable) return Task.CompletedTask;
        return RestartAsync(_request with { ResumeSessionFile = ExistingSessionFile(), ResumeOnCurrentModel = true }, ct);
    }

    // The host already holds startup admission for the complete engine replacement.
    internal Task RecoverAfterEngineRestartAsync(CancellationToken ct) =>
        RestartCoreAsync(_request with { ResumeSessionFile = ExistingSessionFile() }, ct);

    /// <summary>
    /// The approval mode for omp's next start, without starting it now: for an omp that is not running (stopped, or
    /// crashed and waiting for the user to start it again), which then starts with the mode the user chose since.
    /// </summary>
    public async Task SetNextApprovalModeAsync(string mode, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try { _request = _request with { ApprovalMode = mode }; }
        finally { _lifecycle.Release(); }
        _changes.Writer.TryWrite(true);
    }

    /// <summary>
    /// The run does not stop: close only this logical session and open it again on the same persisted session.
    /// Other chats and the shared engine are not stopped.
    /// </summary>
    public async Task ForceStopAsync(CancellationToken ct = default)
    {
        Mutate(s => s.AddNotice(NoticeLevel.Warning, "Force-stopping this chat; it reopens on the same session."));
        // A deferred process-settings change must not prevent the explicit stop that makes this chat idle.
        // Release the chat lifecycle before waiting for admission into the replacement engine.
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
        await RestartAsync(_request with { ResumeSessionFile = ExistingSessionFile() }, ct, force: true).ConfigureAwait(false);
    }

    private string? ExistingSessionFile()
    {
        var snapshot = Snapshot();
        var file = snapshot.StartFailed ? _request.ResumeSessionFile ?? snapshot.SessionFile
            : snapshot.SessionFile ?? _request.ResumeSessionFile;
        return file is not null && File.Exists(file) ? file : null;
    }

    /// <summary>Runs a session switch against the live omp while nothing else is running; errors become notices.</summary>
    private async Task SessionChangeAsync(Func<RpcConnection, Task> change, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = RequireConnection();
            if (Snapshot().Phase != SessionPhase.Ready)
            {
                Mutate(s => s.AddNotice(NoticeLevel.Warning, "Stop the current run before changing sessions."));
                return;
            }
            if (Snapshot().SigningIn is { } provider)
            {
                Mutate(s => s.AddNotice(NoticeLevel.Warning, $"Finish or cancel the sign-in to {provider} first."));
                return;
            }
            var before = Snapshot().SessionFile;
            Mutate(s =>
            {
                _historyTransitionKey = s.LastTranscriptKey;
                s.SetPhase(SessionPhase.Starting, Now);
            });
            try { await change(conn).ConfigureAwait(false); }
            catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or InvalidDataException or NotSupportedException)
            {
                Mutate(s => s.AddNotice(NoticeLevel.Error, "Changing sessions failed: " + e.Message));
                await ResyncSessionAsync(conn, before, ct).ConfigureAwait(false);
            }
            finally
            {
                Mutate(s => { if (s.Phase == SessionPhase.Starting) s.SetPhase(conn.CloseReason is null ? SessionPhase.Ready : SessionPhase.Faulted, Now); });
            }
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// After a failed switch omp may still have changed sessions (the command went through after our deadline, or the
    /// history reload failed). Ask omp which session it has and show that one, so the next prompt never goes into a
    /// session the window is not showing.
    /// </summary>
    private async Task ResyncSessionAsync(RpcConnection conn, string? shownFile, CancellationToken ct)
    {
        try
        {
            var state = await conn.GetStateAsync(ct).ConfigureAwait(false);
            if (state.SessionFile == shownFile && Snapshot().SessionFile == shownFile) return;
            await LoadSessionAsync(conn, _request.WorkingDirectory, ct, newProcess: false).ConfigureAwait(false);
            Mutate(s => s.AddNotice(NoticeLevel.Warning, "omp changed sessions after all; showing the session omp has open."));
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or InvalidDataException or NotSupportedException)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Error, "Could not confirm which session omp has open (" + e.Message + "). Use Restart omp in Settings if the conversation looks wrong."));
        }
    }


    /// <summary>Sends a prompt. The ack arrives at once; the run ends with a terminal agent_end.</summary>
    public Task PromptAsync(string text, CancellationToken ct = default) => PromptAsync(text, [], ct);

    public async Task<bool> PromptAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken ct = default, string? submissionId = null)
    {
        var conn = RequireConnection();
        text = SkillTokens.ForOmp(text); // one skill runs per message: the others go as skill:// references
        if (RefuseWhileSigningIn()) return false;
        long ticket = 0, key = 0;
        Mutate(s =>
        {
            ticket = ++_promptTicket;
            key = s.AddUserPrompt(text, images, submissionId);
            _promptAwaitingAck = key;
            s.BeginRun(Now);
        });
        try
        {
            return ApplyPromptAcknowledgement(await conn.PromptAsync(text, Wire(images), ct: ct).ConfigureAwait(false), ticket, key, submissionId);
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or ArgumentException)
        {
            // ArgumentException: the prompt does not fit omp's 1 MiB line limit.
            Mutate(s =>
            {
                s.AddNotice(NoticeLevel.Error, "Prompt failed: " + e.Message);
                if (_promptTicket == ticket)
                {
                    _promptAwaitingAck = 0;
                    s.EndRun(interrupted: true, Now);
                }
                else if (e is RpcCommandException or ArgumentException) s.DropPrompt(key);
                else s.SettlePrompt(key);
            });
            return false;
        }
    }

    /// <summary>
    /// Settles a prompt's admission (also <c>abort_and_prompt</c>'s): true when omp took it. A superseded prompt (an
    /// interrupt-and-send followed while omp admitted it) settles only its own row: the pending run, the queue and the
    /// next prompt's echo belong to the replacement.
    /// </summary>
    private bool ApplyPromptAcknowledgement(RpcResponse ack, long ticket, long key, string? submissionId)
    {
        var admission = OmpCommands.InputAcknowledgement(ack);
        Mutate(s =>
        {
            var current = _promptTicket == ticket;
            if (current) _promptAwaitingAck = 0;
            if (admission.Disposition == OmpInputDisposition.Admitted)
            {
                // Admitted before the replacement's abort reached omp: it waits for its own echo.
                if (!current) s.ReleaseSupersededPrompt(key);
            }
            else if (admission.Disposition == OmpInputDisposition.Local)
            {
                if (!current) s.SettlePrompt(key);
                else
                {
                    // Ending a replacement's pending run first: a held agent_start must not keep it running.
                    if (!admission.AgentInvoked) s.EndPendingRun(interrupted: false, Now);
                    s.PromptHandledLocally(key);
                }
                if (submissionId is not null) ConfirmSubmission(submissionId);
            }
            else if (!current)
            {
                // The replacement invalidated it before admission: the caller returns it to the message box.
                if (admission.Disposition == OmpInputDisposition.Cancelled) s.DropPrompt(key);
                else
                {
                    s.SettlePrompt(key);
                    s.AddNotice(NoticeLevel.Error, "The engine returned an invalid input acknowledgement. Delivery is uncertain; recovered input is not sent automatically.");
                }
            }
            else
            {
                s.RecoverQueue();
                if (!admission.AgentInvoked) s.EndPendingRun(interrupted: true, Now);
                s.AddNotice(NoticeLevel.Error, admission.Disposition == OmpInputDisposition.Cancelled
                    ? "The input was cancelled before admission. Recovered input is not sent automatically."
                    : "The engine returned an invalid input acknowledgement. Delivery is uncertain; recovered input is not sent automatically.");
            }
        });
        return admission.Disposition is OmpInputDisposition.Admitted or OmpInputDisposition.Local;
    }

    private readonly SemaphoreSlim _commands = new(1, 1);

    /// <summary>
    /// Runs one of omp's builtin slash commands (<see cref="OmpBuiltins.OverRpc"/>, e.g. "/mcp list", "/usage") and
    /// returns output captured before its acknowledgement is handled, without it appearing in the conversation.
    /// Background commands such as /compact can print later; that output stays in the conversation. A command
    /// that starts an agent run (/retry, /handoff) marks the session running like a prompt. Commands run one at
    /// a time; a TUI-only command is refused, never sent (omp would pass it to the model as a message).
    /// </summary>
    public async Task<SlashCommandResult> RunSlashCommandAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var catalog = Snapshot().Commands;
        if (!OmpBuiltins.RunsOverRpc(command, catalog))
        {
            if (OmpBuiltins.NameOf(command) is not { } name || OmpBuiltins.TuiOnly.Contains(name))
                return new(false, "", Error: $"/{OmpBuiltins.NameOf(command)} is not available over omp's RPC");
            return new(false, "", Error: $"This version of omp has no /{name} command", Unsupported: true);
        }
        if (_omp?.Connection is not { } conn) return new(false, "", Error: "omp is not running");
        if (Snapshot().SigningIn is { } provider) return new(false, "", Error: $"omp is busy with the sign-in to {provider}");
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        var output = new System.Text.StringBuilder();
        var acceptingOutput = true; // guarded by output, including the handoff back to the event pump
        try
        {
            conn.Intercept = (type, frame) =>
            {
                if (type != "command_output") return false;
                var text = frame.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString()
                    : frame.TryGetProperty("output", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.String ? o.GetString() : null;
                lock (output)
                {
                    // The read loop may already hold this delegate when the request finishes. Never consume
                    // a background chunk after the returned output has been copied.
                    if (!acceptingOutput) return false;
                    if (output.Length > 0) output.Append('\n');
                    output.Append(text ?? "");
                    if (text?.StartsWith("Compaction ", StringComparison.Ordinal) == true && SessionOutputs.ParseCompactEnd(text) is not null)
                        RequestStateRefresh(conn);
                }
                return true;
            };
            long epoch = 0, transition = 0;
            Mutate(s =>
            {
                epoch = s.TranscriptEpoch;
                transition = _historyTransitionKey;
                if (s.Phase == SessionPhase.Ready) s.BeginRun(Now);
            });
            var ack = await conn.RequestAsync("prompt", w => w.WriteString("message", command), timeout ?? TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            string printed;
            lock (output)
            {
                acceptingOutput = false;
                printed = output.ToString().TrimEnd();
            }
            var admission = OmpCommands.InputAcknowledgement(ack);
            if (!ack.Success || admission.Disposition is OmpInputDisposition.Unknown or OmpInputDisposition.Cancelled)
            {
                Mutate(s => s.EndPendingRun(interrupted: true, Now));
                return new(false, printed, Error: ack.Error ?? "The command's admission was cancelled or not confirmed.");
            }
            if (!admission.AgentInvoked) Mutate(s => s.EndPendingRun(interrupted: false, Now));
            if (OmpBuiltins.NameOf(command) == "fresh")
            {
                Mutate(s =>
                {
                    if (ReferenceEquals(conn, Connection) && s.TranscriptEpoch == epoch && _historyTransitionKey == transition
                        && _disposed == 0 && !_stopRequested && s.NoteFreshProviderSession(printed, ack.Timings.ResponseRead))
                        RequestStateRefresh(conn);
                });
            }
            return new(true, printed, admission.AgentInvoked);
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException or ArgumentException)
        {
            Mutate(s => s.EndPendingRun(interrupted: true, Now));
            string printed;
            lock (output)
            {
                acceptingOutput = false;
                printed = output.ToString().TrimEnd();
            }
            return new(false, printed, Error: e.Message);
        }
        finally
        {
            lock (output) acceptingOutput = false;
            conn.Intercept = null;
            _commands.Release();
        }
    }

    /// <summary>
    /// A message while the agent works: <see cref="QueueKind.Steer"/> reaches it at the next tool boundary,
    /// <see cref="QueueKind.FollowUp"/> when it would otherwise stop. Builtins and later follow-ups wait locally in order.
    /// </summary>
    public async Task<bool> QueueAsync(QueueKind kind, string text, IReadOnlyList<ImageAttachment> images, CancellationToken ct = default, string? submissionId = null)
    {
        var conn = RequireConnection();
        text = SkillTokens.ForOmp(text);
        ValidateMessage(text, images);
        ct.ThrowIfCancellationRequested();
        QueuedMessage? queued = null;
        try
        {
            Mutate(s =>
            {
                // Builtins need prompt dispatch. Follow-ups behind them must not overtake them in omp's queue.
                // Explicit steering still interrupts the active run; a builtin submitted as a steer must wait.
                if (OmpBuiltins.RunsOverRpc(text, s.Commands)) kind = QueueKind.BuiltinCommand;
                else if (kind == QueueKind.BuiltinCommand) throw new ArgumentException("This command is no longer an omp builtin.");
                else if (kind == QueueKind.FollowUp && (_dispatchingDeferred is not null || s.Queued.Any(q => q.Deferred && !q.Recovered)))
                    kind = QueueKind.DeferredFollowUp;
                // A level picked while omp works belongs to this message. omp takes none with a message and, set now,
                // would change the current run: Ombra keeps the message and sets the level right before omp starts it.
                // A steer joins the current run, so it keeps that run's level.
                string? level = null;
                if (kind != QueueKind.Steer && s.NextThinkingLevel is { } next && (next != s.ThinkingLevel || s.QueueCarriesThinking))
                {
                    level = next;
                    if (kind == QueueKind.FollowUp) kind = QueueKind.DeferredFollowUp;
                }
                queued = s.Enqueue(kind, text, images, submissionId, level);
                try { QueueCheckpoint?.Invoke(s.Queued.ToArray()); }
                catch
                {
                    // A concurrent Ready transition may dispatch as soon as this lock is released.
                    // Failed durable admission must become ineligible before that can happen.
                    s.RecoverQueued(queued.Seq);
                    throw;
                }
                if (queued.Deferred && (_restoringQueue || s.Phase is not (SessionPhase.Ready or SessionPhase.Running)))
                    s.RecoverQueued(queued.Seq);
            });
            if (queued!.Deferred) return true;
            var disposition = SkillTokens.HasInvocation(text)
                ? await conn.QueuePromptAsync(text, Wire(images), steer: kind == QueueKind.Steer, ct).ConfigureAwait(false)
                : kind == QueueKind.Steer
                    ? await conn.SteerAsync(text, Wire(images), ct).ConfigureAwait(false)
                    : await conn.FollowUpAsync(text, Wire(images), ct).ConfigureAwait(false);
            return ApplyQueuedInputDisposition(queued, disposition, pendingRun: false);
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or ArgumentException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Mutate(s =>
            {
                // A timeout may still be accepted by a live runtime. A closed connection cannot deliver later;
                // recover here too, since the crash may have finalized before this input entered the queue.
                if (queued is not null && e is RpcCommandException or RpcConnectionClosedException or ArgumentException or IOException or UnauthorizedAccessException)
                    s.RecoverQueued(queued.Seq);
                s.AddNotice(NoticeLevel.Error, $"Could not confirm the queued message ({e.Message}). Recovered input is not resent automatically.");
            });
            return false;
        }
    }

    /// <summary>
    /// Takes a queued message back before omp delivers it (omp's <c>remove_queued_message</c>). Returns its images
    /// when omp withdrew it; null when omp had already delivered it or did not answer. An omp without the command
    /// (before 18.4.4) throws <see cref="RpcCommandException"/>.
    /// </summary>
    public async Task<IReadOnlyList<ImageAttachment>?> RemoveQueuedAsync(QueuedMessage message, CancellationToken ct = default, bool recover = false)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            // A chip snapshot can predate live steering. Never withdraw an identical later queue entry instead.
            var current = _state.Queued.FirstOrDefault(q => q.Seq == message.Seq && !q.Recovered);
            if (current is null || current.LiveSteered) return null;
            message = current;
        }
        if (message.Deferred)
        {
            lock (_lock)
            {
                if (_dispatchingDeferred?.Seq == message.Seq) return null;
                var current = _state.Queued.FirstOrDefault(q => q.Seq == message.Seq && !q.Recovered);
                if (current is null) return null;
                if (current.Deferred) return RemoveDeferredInput(current, recover);
                message = current; // admitted since the chip's snapshot: use the ordinary runtime withdrawal
            }
        }
        var conn = RequireConnection();
        try
        {
            var (removed, images) = await conn.RemoveQueuedMessageAsync(message.Text, message.Kind == QueueKind.Steer, ct).ConfigureAwait(false);
            if (!removed) return null;
            IReadOnlyList<ImageAttachment> restoredImages = [.. images.Select((i, n) => new ImageAttachment($"image {n + 1}", i.MimeType, i.Data))];
            Mutate(s =>
            {
                // The wire command withdraws the first matching text, not the chip's local sequence number.
                bool Matches(QueuedMessage q) => !q.Recovered && !q.LiveSteered && q.Kind == message.Kind && q.Text == message.Text;
                var withdrawn = restoredImages.Count > 0 ? s.Queued.FirstOrDefault(q => Matches(q) && SameImages(q.Images, restoredImages)) : null;
                withdrawn ??= s.Queued.FirstOrDefault(Matches);
                if (restoredImages.Count == 0) restoredImages = withdrawn?.Images ?? message.Images;
                else if (withdrawn is not null && SameImages(withdrawn.Images, restoredImages))
                    restoredImages = withdrawn.Images;
                if (recover)
                {
                    withdrawn ??= s.Enqueue(message.Kind, message.Text, restoredImages);
                    s.RecoverQueued(withdrawn.Seq, restoredImages);
                }
                else if (withdrawn is not null) s.RemoveQueued(withdrawn.Seq);
            });
            return restoredImages;
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException)
        {
            return null;
        }

        static bool SameImages(IReadOnlyList<ImageAttachment> a, IReadOnlyList<ImageAttachment> b)
        {
            if (a.Count != b.Count) return false;
            for (var i = 0; i < a.Count; i++)
                if (a[i].MimeType != b[i].MimeType || !a[i].Data.AsSpan().SequenceEqual(b[i].Data)) return false;
            return true;
        }
    }

    private bool RefuseWhileSigningIn()
    {
        if (Snapshot().SigningIn is not { } provider) return false;
        Mutate(s => s.AddNotice(NoticeLevel.Warning, $"omp is busy with the sign-in to {provider}; finish or cancel it first."));
        return true;
    }

    private int _stateRefreshRequests;

    /// <summary>
    /// Reads what only get_state reports (todos, context usage, model, thinking, session name). Requests during a
    /// refresh collapse into one more; runs off the pump so a slow answer never holds events back.
    /// </summary>
    private void RequestStateRefresh(RpcConnection conn)
    {
        if (Interlocked.Increment(ref _stateRefreshRequests) != 1) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var seen = Volatile.Read(ref _stateRefreshRequests);
                long epoch;
                long transition;
                long run;
                lock (_lock)
                {
                    epoch = _state.TranscriptEpoch;
                    transition = _historyTransitionKey;
                    run = _state.RunGeneration;
                }
                try
                {
                    var state = await conn.GetStateAsync().ConfigureAwait(false);
                    await DrainHistoryEventsAsync(conn, state.Timings!, CancellationToken.None).ConfigureAwait(false);
                    Mutate(s =>
                    {
                        // Drain first: pre-response events may request another refresh or replace the session.
                        // Never publish an obsolete answer while that authoritative follow-up is in flight.
                        if (!ReferenceEquals(conn, Connection) || s.TranscriptEpoch != epoch
                            || _historyTransitionKey != transition
                            || s.RunGeneration != run || _disposed != 0 || _stopRequested
                            || Volatile.Read(ref _stateRefreshRequests) != seen || !s.AcceptStateSession(state)) return;
                        var readAt = state.Timings!.ResponseRead;
                        if (s.HistoryModelReadAt <= readAt && state.ModelProvider is not null) s.Model = $"{state.ModelProvider}/{state.ModelId}";
                        s.ApplyUsageLimit(state);
                        s.ApplyPlanModeState(state.PlanMode, readAt);
                        s.ApplyGoalState(state, readAt, Now);
                        if (state.QueuedMessages is { } engineQueue) s.ApplyEngineQueue(engineQueue.Steering, engineQueue.FollowUp, readAt, engineQueue.LiveSteered);
                        if (s.HistoryThinkingReadAt <= readAt) s.ThinkingLevel = state.ThinkingLevel;
                        if (s.HistoryTitleReadAt <= readAt) s.SessionName = state.SessionName ?? s.SessionName;
                        if (s.HistoryMutationReadAt <= readAt)
                        {
                            s.Todos = ConversationState.ParseTodos(state.TodoPhases);
                            s.ContextPercent = state.ContextPercent;
                        }
                    });
                }
                catch (Exception e)
                {
                    // Best effort, and it must not stop: an answer it cannot read (e.g. no data) is skipped, and the
                    // next request refreshes again. (Only RPC errors were caught; anything else ended this loop with
                    // the counter still set, so todos and context usage never refreshed again.)
                    if (e is not (RpcCommandException or TimeoutException or RpcConnectionClosedException))
                        Mutate(s => s.Log(Now, "state_refresh_error", e.GetType().Name + ": " + e.Message));
                }
                if (Interlocked.CompareExchange(ref _stateRefreshRequests, 0, seen) == seen) return;
                if (Connection is { } current) conn = current;
            }
        });
    }

    private static (string, byte[])[] Wire(IReadOnlyList<ImageAttachment> images) => [.. images.Select(i => (i.MimeType, i.Data))];

    /// <summary>
    /// After a run ends with messages still shown as queued, ask omp whether it still holds them. omp keeps queued
    /// messages after an explicit abort (they go with the next run); an empty queue means they were not delivered.
    /// </summary>
    private async Task ReconcileQueueAsync(RpcConnection conn)
    {
        try
        {
            // Let a follow-up that omp is starting right now show up as a new run first.
            await Task.Delay(TimeSpan.FromSeconds(1), _clock).ConfigureAwait(false);
            if (!ReferenceEquals(conn, Connection) || _restoringQueue || Snapshot() is not { Phase: SessionPhase.Ready, Queued.Count: > 0 }) return;
            var state = await conn.GetStateAsync().ConfigureAwait(false);
            if (state.QueuedMessageCount > 0 || state.IsStreaming) return;
            Mutate(s =>
            {
                if (ReferenceEquals(conn, Connection) && !_restoringQueue && s.Phase == SessionPhase.Ready)
                    s.RecoverQueue(includeDeferred: false);
            });
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException) { }
    }

    private volatile bool _restoringQueue;
    public async Task AbortAsync(CancellationToken ct = default)
    {
        var conn = RequireConnection();
        _restoringQueue = true;
        List<PendingDialog> open = [];
        Mutate(s =>
        {
            if (s.Phase == SessionPhase.Running) s.SetPhase(SessionPhase.Aborting, Now);
            s.HoldQueueRemovals = true;
            RecoverDeferredInput(s);
            open = s.CloseAllDialogs("the run was stopped");
        });
        try
        {
            // omp 18.2.0 does not pass the run's abort signal to a tool approval prompt: until it is answered
            // the tool, and with it the abort, would wait forever. Answer open dialogs first.
            foreach (var d in open) await conn.CancelExtensionUiAsync(d.Id, ct).ConfigureAwait(false);
            if (Snapshot().Queued.Any(q => !q.Recovered && !q.Deferred))
            {
                var restored = await conn.AbortAndRestoreQueueAsync(ct).ConfigureAwait(false);
                Mutate(s =>
                {
                    s.RestoreQueue(restored.Messages.Select((m, index) => new QueuedMessage(index,
                        m.Steering ? QueueKind.Steer : QueueKind.FollowUp, m.Text,
                        m.Images.Select((i, n) => new ImageAttachment($"image {n + 1}", i.MimeType, i.Data)).ToArray())).ToArray(), restored.Truncated, restored.ImagesDropped);
                    if (restored.Truncated || restored.ImagesDropped)
                        s.AddNotice(NoticeLevel.Warning, "omp returned a limited queue recovery payload. Locally saved text and images were retained; input from outside this client may be incomplete.");
                });
            }
            else await conn.AbortAsync(ct).ConfigureAwait(false);
            // omp answers abort only once the agent is idle, so the run is over even if no agent_end
            // follows (e.g. a local-only command). Settle it; a late agent_end is harmless.
            Mutate(s => { if (s.Phase == SessionPhase.Aborting) s.EndRun(interrupted: true, Now); });
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Error, "Stop did not finish (" + e.Message + "). Press Force stop to restart omp on this session."));
        }
        finally
        {
            _restoringQueue = false;
            Mutate(s => s.HoldQueueRemovals = false);
        }
    }

    /// <summary>
    /// Answers a pending dialog. Returns false when it is no longer pending (withdrawn by omp, expired,
    /// already answered, or omp stopped); nothing is sent then, so a late click is never presented as an answer.
    /// </summary>
    public async Task<bool> AnswerDialogAsync(string id, DialogAnswer answer, CancellationToken ct = default)
    {
        var conn = RequireConnection();
        if ((answer switch { DialogAnswer.Value v => v.Text, DialogAnswer.Denied d => d.Reason, DialogAnswer.Asked a => string.Concat(a.Answers.Select(x => x.CustomInput)), _ => null }) is { } text
            && System.Text.Encoding.UTF8.GetByteCount(text) > MaxAnswerBytes)
        {
            // Keep the dialog (and the typed text) instead of dropping it unanswered: omp would wait forever.
            Mutate(s => s.AddNotice(NoticeLevel.Error, $"The answer is too long for omp ({System.Text.Encoding.UTF8.GetByteCount(text) / 1024} KB; at most {MaxAnswerBytes / 1024} KB). Shorten it or dismiss the question."));
            return false;
        }
        PendingDialog? dialog = null;
        Mutate(s => dialog = s.TakeDialog(id));
        if (dialog is null) return false;
        try
        {
            switch (answer)
            {
                case DialogAnswer.Value v: await conn.SendExtensionUiValueAsync(id, v.Text, ct).ConfigureAwait(false); break;
                case DialogAnswer.Denied d: await conn.SendExtensionUiDenyAsync(id, d.Reason, ct).ConfigureAwait(false); break;
                case DialogAnswer.Confirmed c: await conn.SendExtensionUiConfirmAsync(id, c.Yes, ct).ConfigureAwait(false); break;
                case DialogAnswer.Elicited e: await conn.SendExtensionUiElicitAsync(id, e.Accept, e.Content, ct).ConfigureAwait(false); break;
                case DialogAnswer.Asked a:
                    await conn.SendExtensionUiAskAsync(id, [.. a.Answers.Select(x => (x.Id, x.SelectedOptions, x.CustomInput))], ct).ConfigureAwait(false);
                    break;
                default: await conn.CancelExtensionUiAsync(id, ct).ConfigureAwait(false); break;
            }
            Mutate(s => s.RecordAnswer(dialog, answer, "by you"));
            return true;
        }
        catch (Exception e) when (e is RpcConnectionClosedException or ArgumentException or OperationCanceledException)
        {
            // ArgumentException: an answer over omp's 1 MiB line limit.
            Mutate(s => s.AddNotice(NoticeLevel.Error, $"Your answer did not reach omp ({e.Message}) — {dialog.Headline}"));
            return false;
        }
    }

    /// <summary>omp reads each command as one line of up to 1 MiB; leave room for the envelope.</summary>
    public const int MaxAnswerBytes = 1024 * 1024 - 4096;

    private async Task ExpireAtDeadlineAsync(RpcConnection conn, PendingDialog d)
    {
        // Timers and the wall clock are different clocks: a timer can fire a moment before the deadline by the
        // wall clock, so wait again until the dialog really is past it (or no longer pending).
        while (true)
        {
            var wait = d.Deadline!.Value - Now;
            if (wait > TimeSpan.Zero) await DelayUnlessClosedAsync(conn, wait + TimeSpan.FromMilliseconds(5)).ConfigureAwait(false);
            var pending = false;
            Mutate(s =>
            {
                if (!ReferenceEquals(conn, Connection) || conn.CloseReason is not null || !s.Dialogs.Contains(d)) return;
                s.ExpireDialogs(Now);
                pending = s.Dialogs.Contains(d);
            });
            if (!pending) return;
        }
    }

    /// <summary>A cheap non-model round trip (get_state); used for latency measurement and health checks.</summary>
    public async Task<RpcResponse> ProbeAsync(CancellationToken ct = default) =>
        await RequireConnection().RequestAsync("get_state", ct: ct).ConfigureAwait(false);

    /// <summary>Stops this logical session on request; an explicit stop never restarts automatically.</summary>
    public Task StopAsync(TimeSpan? grace = null)
    {
        // Before the cancel: an exit seen after it is a stop, never a crash to recover from.
        if (_omp is not null) _stopRequested = true;
        CancelAutoRecover();
        return StopSessionAsync(grace);
    }

    private async Task StopSessionAsync(TimeSpan? grace = null)
    {
        if (_omp is not { } omp) return;
        _stopRequested = true;
        // A session that already failed or stopped keeps that phase; only a live one goes through Stopping.
        Mutate(s => { if (s.Phase is not (SessionPhase.Faulted or SessionPhase.Stopped)) s.SetPhase(SessionPhase.Stopping, Now); });
        await omp.StopAsync(grace ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (_pump is not null)
        {
            // Logical completion follows engine_session_closed, not shared stdout EOF.
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException) { await omp.Connection.DisposeAsync().ConfigureAwait(false); await _pump.ConfigureAwait(false); }
        }
        else
        {
            Mutate(s => { if (s.Phase == SessionPhase.Stopping) s.SetPhase(SessionPhase.Stopped, Now); });
        }
    }

    /// <summary>The live connection; between the stop and the start of a restart there is none, and a command sent
    /// then fails with <see cref="OmpNotRunningException"/> (a caller can keep what the user typed for the retry).</summary>
    private RpcConnection RequireConnection() =>
        _omp?.Connection ?? throw new OmpNotRunningException();

    private async Task PumpAsync(OmpSession omp)
    {
        var conn = omp.Connection;
        var leaseLost = false;
        await foreach (var frame in conn.Events.ReadAllAsync().ConfigureAwait(false))
        {
            if (frame.Type == "host_tool_call")
            {
                _ = HandleHostToolCallAsync(conn, frame); // SessionController.HostTools.cs
                continue;
            }
            if (frame.Type == "host_tool_cancel")
            {
                CancelHostToolCall(frame);
                continue;
            }
            if (frame.Type == "editor_apply_edit")
            {
                _ = HandleEditorFrameAsync(conn, frame);
                continue;
            }
            if (frame.Type == "session_lease_lost")
            {
                // Another window took this chat over; the runtime ends this logical session next (not a crash).
                leaseLost = true;
                var holder = frame.Json.TryGetProperty("holder", out var h) ? OmpSessionLeaseHolder.Parse(h) : null;
                lock (_lock) _lease = new SessionLease(SessionLeaseState.Lost, holder);
                Mutate(s => s.Log(Now, "lease", $"session_lease_lost: now open in {holder?.Kind ?? "another process"} (pid {holder?.Pid?.ToString() ?? "unknown"})"));
                // "Open here" elsewhere stops the reply in progress here: say so, so it is not mistaken for a finished one.
                if (frame.Json.TryGetProperty("interrupted", out var i) && i.ValueKind == System.Text.Json.JsonValueKind.True)
                    Mutate(s => s.AddNotice(NoticeLevel.Warning, "The reply in progress was stopped: this chat was opened in another window"));
                continue;
            }
            PendingDialog[] deadlines = [];
            string[] toCancel = [];
            List<(PendingDialog Dialog, ApprovalGrant Grant)>? autoAllowed = null;
            var reconcile = false;
            // When the line was read, not when the pump got to it: frames can wait in the queue (e.g. while history
            // loads at start), and dialog deadlines count from omp's send time.
            var readAt = Now - Stopwatch.GetElapsedTime(frame.ReadTimestamp);
            lock (_lock)
            {
                // Quiet periods use elapsed time, never UTC (which can jump after sleep or clock sync).
                _lastFrameTimestamp = _clock.GetTimestamp();
                var wasActive = _state.Phase is SessionPhase.Running or SessionPhase.Aborting;
                var historyRevision = _state.TranscriptRevision;
                NoteNewFile(frame); // SessionController.Files.cs: a write that creates its file, seen before omp writes it
                try { _state.Apply(frame, readAt); }
                catch (Exception e) when (e is InvalidOperationException or FormatException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException or OverflowException)
                {
                    // A frame of an unexpected shape must not stop the session; report it and go on.
                    _state.AddNotice(NoticeLevel.Warning, $"Could not apply a '{frame.Type}' event from omp: {e.Message}");
                }
                NoteHistoryEvent(conn, frame, historyRevision);
                if (_state.JobsSupported == false) _jobsUnsupported.TrySetResult(); // SessionController.Jobs.cs
                if (_state.NewDeadlines.Count > 0)
                {
                    deadlines = [.. _state.NewDeadlines];
                    _state.NewDeadlines.Clear();
                }
                reconcile = wasActive && _state.Phase == SessionPhase.Ready && _state.Queued.Count > 0;
                if (_state.StateRefreshWanted)
                {
                    _state.StateRefreshWanted = false;
                    RequestStateRefresh(conn);
                }
                if (_state.DialogsToCancel.Count > 0)
                {
                    toCancel = [.. _state.DialogsToCancel];
                    _state.DialogsToCancel.Clear();
                }
                if (_state.NewApprovals.Count > 0)
                {
                    autoAllowed = TakeCoveredApprovals(_state.NewApprovals);
                    _state.NewApprovals.Clear();
                }
            }
            DispatchDeferredInput();
            if (reconcile) _ = ReconcileQueueAsync(conn);
            foreach (var id in toCancel)
            {
                try { await conn.CancelExtensionUiAsync(id).ConfigureAwait(false); }
                catch (RpcConnectionClosedException) { }
            }
            if (autoAllowed is not null) await AutoAllowAsync(conn, autoAllowed).ConfigureAwait(false);
            _changes.Writer.TryWrite(true);
            if (NeedsSettleProbe(conn)) EnsureSettleProbe(conn);
            foreach (var d in deadlines) _ = ExpireAtDeadlineAsync(conn, d);
        }

        // The logical stream ended. The engine transport owns process failures and fans them out to every chat.
        var protocolError = conn.CloseReason?.InnerException is RpcProtocolException;
        int? code = null;
        try { code = await omp.Exited.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { await omp.DisposeAsync().ConfigureAwait(false); }
        var crashed = false;
        var recoverEpoch = 0;
        Mutate(s =>
        {
            // Read before the stop flag: an explicit stop sets the flag before it moves the epoch on.
            recoverEpoch = AutoRecoverEpoch;
            s.CloseAllDialogs(leaseLost ? "this chat was opened elsewhere" : _stopRequested ? "omp was stopped" : "omp is no longer running");
            if (leaseLost)
            {
                s.Log(Now, "exit", "the chat was opened elsewhere; shown read-only");
                s.EndRun(interrupted: true, Now);
                s.EndRunningSubagents("aborted", Now);
                s.SetPhase(SessionPhase.Stopped, Now);
            }
            else if (_stopRequested)
            {
                // We asked omp to stop: a kill after the grace period is still a stop, not a fault.
                s.Log(Now, "exit", code == 0 ? "omp exited normally" : $"omp stopped (exit code {code?.ToString() ?? "unknown"})");
                s.EndRun(interrupted: true, Now);
                s.EndRunningSubagents("aborted", Now); // they ran inside omp: they stopped with it
                if (s.Phase != SessionPhase.Faulted) s.SetPhase(SessionPhase.Stopped, Now);
            }
            else
            {
                var tail = LastLines(omp.StderrTail, 12);
                var reason = conn.CloseReason is { InnerException: not null } r ? r.Message : null;
                var text = protocolError
                    ? new ClientText("omp RPC stream broke ({0}); the session was stopped", conn.CloseReason!.InnerException!.Message)
                    : code is { } c
                        ? reason is null ? new ClientText("omp exited unexpectedly (exit code {0})", c) : new ClientText("omp exited unexpectedly (exit code {0}): {1}", c, reason)
                        : reason is null ? new ClientText("omp exited unexpectedly") : new ClientText("omp exited unexpectedly: {0}", reason);
                s.Fail(text + (tail.Length > 0 ? "\n" + tail : ""), Now, text: text);
                s.EndRunningSubagents("failed", Now); // they ran inside omp: a crash ended them too
                crashed = true;
            }
        });
        if (crashed) OnUnexpectedExit(recoverEpoch); // SessionController.AutoRecover.cs
    }

    /// <summary>
    /// Workaround for omp 18.2.0 (UPSTREAM OMP ISSUE): after agent_end with isTerminal:false omp may go idle
    /// with no terminal agent_end. Once no frame has arrived for <see cref="SettleQuietPeriod"/>, ask get_state;
    /// if nothing is running, compacting or queued, end the run. Deadline-based, off the UI thread.
    /// At most one probe runs; it never outlives the connection or a requested stop.
    /// </summary>
    private void EnsureSettleProbe(RpcConnection conn)
    {
        if (Interlocked.CompareExchange(ref _settleProbeRunning, 1, 0) == 0) _ = Task.Run(() => SettleProbeAsync(conn));
    }

    internal bool SettleProbeActive => Volatile.Read(ref _settleProbeRunning) == 1;

    private async Task SettleProbeAsync(RpcConnection conn)
    {
        while (true)
        {
            await ProbeUntilSettledAsync(conn).ConfigureAwait(false);
            Interlocked.Exchange(ref _settleProbeRunning, 0);
            // A non-terminal agent_end applied while this probe was finishing saw the flag still set and did not
            // start a probe: check again now that the flag is released (on the current omp, after a restart).
            if (_omp?.Connection is not { } current) return;
            conn = current;
            if (!NeedsSettleProbe(conn) || Interlocked.CompareExchange(ref _settleProbeRunning, 1, 0) != 0) return;
        }
    }

    private bool NeedsSettleProbe(RpcConnection conn)
    {
        if (!ReferenceEquals(conn, Connection) || conn.CloseReason is not null || _stopRequested) return false;
        lock (_lock) return _state.AwaitingSettle;
    }

    private async Task ProbeUntilSettledAsync(RpcConnection conn)
    {
        while (NeedsSettleProbe(conn))
        {
            long frames;
            long last;
            long run;
            long epoch;
            long transition;
            lock (_lock)
            {
                frames = _state.FramesReceived;
                last = _lastFrameTimestamp;
                run = _state.RunGeneration;
                epoch = _state.TranscriptEpoch;
                transition = _historyTransitionKey;
            }
            var quietFor = _clock.GetElapsedTime(last);
            if (quietFor < SettleQuietPeriod)
            {
                await DelayUnlessClosedAsync(conn, SettleQuietPeriod - quietFor).ConfigureAwait(false);
                continue;
            }
            OmpSessionState state;
            try
            {
                state = await conn.GetStateAsync().ConfigureAwait(false);
            }
            catch (RpcConnectionClosedException)
            {
                return;
            }
            catch (Exception e) when (e is TimeoutException or RpcCommandException)
            {
                await DelayUnlessClosedAsync(conn, SettleQuietPeriod).ConfigureAwait(false);
                continue;
            }
            var settled = false;
            Mutate(s =>
            {
                // Re-check under the lock: a frame may have arrived while get_state was in flight.
                if (!ReferenceEquals(conn, Connection) || !s.AwaitingSettle || s.FramesReceived != frames || !state.LooksSettled
                    || state.IsStreaming || state.IsCompacting || s.RunStartPending || s.RunGeneration != run
                    || s.TranscriptEpoch != epoch || _historyTransitionKey != transition || _disposed != 0 || _stopRequested) return;
                s.Log(Now, "settle", "no terminal agent_end after isTerminal:false; get_state reports idle, run ended (omp 18.2.0 workaround)");
                s.EndRun(interrupted: false, Now);
                settled = true;
            });
            if (settled) return;
            await DelayUnlessClosedAsync(conn, SettleQuietPeriod).ConfigureAwait(false);
        }
    }

    /// <summary>Waits, but returns as soon as the connection ends (the probe then exits instead of sleeping on).</summary>
    private Task DelayUnlessClosedAsync(RpcConnection conn, TimeSpan delay) =>
        Task.WhenAny(Task.Delay(delay, _clock), conn.Completion);

    private void OnStderr(string line)
    {
        StderrLine?.Invoke(line);
        Mutate(s => s.Log(Now, "stderr", line.Length > 300 ? line[..GraphemeText.PrefixLength(line, 300)] : line));
    }

    internal void Mutate(Action<ConversationState> change)
    {
        lock (_lock) change(_state);
        DispatchDeferredInput();
        _changes.Writer.TryWrite(true);
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>
    /// omp exits when no provider is set up: 18.2.0 printed "No models available. Use /login or set an API key…",
    /// 18.8.0 prints "No default model selected. Use /login, set an API key environment variable…"
    /// (UNDOCUMENTED / VERSION-PINNED text; an unrecognized message still shows as a plain start failure).
    /// </summary>
    internal static StartProblem ClassifyStartFailure(OmpStartException e) =>
        e.LaunchFailed ? StartProblem.NotFound
        : e.Code == "session_model_unavailable" ? StartProblem.SessionModelUnavailable
        : NoModelMessages.Any(m => e.StderrTail.Contains(m, StringComparison.OrdinalIgnoreCase)) ? StartProblem.NoModel
        : StartProblem.Other;

    private static readonly string[] NoModelMessages = ["No models available", "No default model selected"];

    private static string LastLines(string text, int n)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.Skip(Math.Max(0, lines.Length - n)));
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelAutoRecover();
        _powerLifetime.Cancel();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_omp is { } omp)
            {
                try { await StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception e) when (e is TimeoutException or InvalidOperationException) { }
                await omp.DisposeAsync().ConfigureAwait(false);
            }
            if (_ownsEngine) await _engine.DisposeAsync().ConfigureAwait(false);
            _changes.Writer.TryComplete();
        }
        finally { _lifecycle.Release(); }
    }
}
