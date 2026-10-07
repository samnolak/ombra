using System.Diagnostics;
using System.Threading.Channels;
using OmpGui.Rpc;

namespace OmpGui.ClientCore;

/// <summary>
/// One omp session: process lifecycle, protocol negotiation, the event pump and the conversation state.
/// All omp I/O happens on background tasks; the UI only calls the async commands and reads snapshots.
/// State keeps advancing whether or not anyone is looking (minimized window, blocked UI thread).
/// </summary>
public sealed partial class SessionController : IAsyncDisposable
{
    private readonly Func<LaunchRequest, OmpLaunchSpec> _launch;
    private LaunchRequest _request;
    private readonly TimeSpan _readyTimeout;
    // Start, restart and session switches never overlap.
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly ConversationState _state = new();
    private readonly object _lock = new();
    // Capacity-1 "something changed" signal: bursts of frames collapse into one wake-up for the consumer.
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private OmpProcess? _omp;
    private Task? _pump;
    private int _settleProbeRunning;
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
    {
        _launch = launch;
        _request = initial;
        _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(60);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>What the running omp was started with.</summary>
    public LaunchRequest CurrentLaunch => _request;

    /// <summary>Wakes whenever the snapshot may have changed. Read it, then call <see cref="Snapshot"/>.</summary>
    public ChannelReader<bool> Changes => _changes.Reader;

    /// <summary>Called with each raw stderr line of omp (on a background thread).</summary>
    public event Action<string>? StderrLine;

    public int? ProcessId => _omp?.ProcessId;
    public RpcConnection? Connection => _omp?.Connection;

    public SessionSnapshot Snapshot()
    {
        lock (_lock) return _state.Snapshot() with { ApprovalMode = _request.ApprovalMode is null ? null : OmpRuntimeOptions.EffectiveApprovalMode(_request.ApprovalMode) };
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try { await StartCoreAsync(ct).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        Mutate(s => s.SetPhase(SessionPhase.Starting, Now));
        try
        {
            var spec = _launch(_request);
            _omp = await OmpProcess.StartAsync(spec, _readyTimeout, ct: ct).ConfigureAwait(false);
            _omp.StderrLine += OnStderr;
            _ = CloseAfterExitAsync(_omp);
            var conn = _omp.Connection;
            var version = await conn.NegotiateAsync(ct).ConfigureAwait(false);
            Mutate(s =>
            {
                s.ProtocolVersion = version;
                s.Log(Now, "ready", $"pid {_omp.ProcessId}, protocol v{version}, supported [{string.Join(',', _omp.Ready.SupportedProtocolVersions)}]");
            });
            await LoadSessionAsync(conn, spec.WorkingDirectory, ct).ConfigureAwait(false);
            // Start folding events only after history is in place; startup frames wait in the bounded queue.
            _pump = Task.Run(() => PumpAsync(_omp));
            _ = SubscribeSubagentsAsync(conn); // SessionController.Subagents.cs
        }
        catch (OmpStartException e)
        {
            var detail = string.IsNullOrWhiteSpace(e.StderrTail) ? "" : "\n" + LastLines(e.StderrTail, 12);
            Mutate(s => s.Fail(e.Message + detail, Now, ClassifyStartFailure(e)));
            throw;
        }
        catch (OperationCanceledException)
        {
            // Closed while starting: do not leave a live omp behind.
            if (_omp is { } started)
            {
                _stopRequested = true;
                await started.DisposeAsync().ConfigureAwait(false);
            }
            Mutate(s => s.SetPhase(SessionPhase.Stopped, Now));
            throw;
        }
        catch (Exception e)
        {
            Mutate(s => s.Fail("Starting omp failed: " + e.Message, Now));
            if (_omp is { } started)
            {
                _stopRequested = true;
                await started.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>Replaces the transcript with omp's current session (after start, switch or new session).</summary>
    private async Task LoadSessionAsync(RpcConnection conn, string? launchDirectory, CancellationToken ct, bool newProcess = true)
    {
        var state = await conn.GetStateAsync(ct).ConfigureAwait(false);
        var messages = await conn.GetMessagesAsync(ct).ConfigureAwait(false);
        var header = state.SessionFile is { } file && File.Exists(file) ? await SessionCatalog.ReadSummaryAsync(file, ct).ConfigureAwait(false) : null;
        IReadOnlyList<SlashCommand>? commands = null;
        try { commands = ConversationState.ParseCommands(await conn.GetAvailableCommandsAsync(ct).ConfigureAwait(false)); }
        catch (Exception e) when (e is RpcCommandException or TimeoutException) { } // the startup update frame has them too
        Mutate(s =>
        {
            s.ResetTranscript(newProcess);
            s.Model = state.ModelProvider is null ? null : $"{state.ModelProvider}/{state.ModelId}";
            s.SessionId = state.SessionId;
            s.SessionFile = state.SessionFile;
            s.SessionName = state.SessionName;
            s.ThinkingLevel = state.ThinkingLevel;
            s.Todos = ConversationState.ParseTodos(state.TodoPhases);
            s.ContextPercent = state.ContextPercent;
            if (commands is { Count: > 0 }) s.Commands = commands;
            s.Cwd = header?.Cwd ?? Path.GetFullPath(launchDirectory ?? Environment.CurrentDirectory);
            s.Hydrate(messages);
            s.SetPhase(state.IsStreaming ? SessionPhase.Running : SessionPhase.Ready, Now);
        });
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
    /// Opens a saved session. Same project: omp switches in place (<c>switch_session</c>). Other project: omp 18.2.0
    /// refuses to switch across working directories, so omp is restarted there with <c>--resume</c>.
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
                if (await conn.SwitchSessionAsync(sessionFile, ct).ConfigureAwait(false))
                    await LoadSessionAsync(conn, _request.WorkingDirectory, ct, newProcess: false).ConfigureAwait(false);
                else
                    declined = true;
            }, ct).ConfigureAwait(false);
            if (!declined) return;
            // omp declined to switch in place (it compares the recorded working directory exactly, or an extension
            // vetoed it); the user asked for this session, so open it the way a fresh omp would.
            Mutate(s => s.Log(Now, "switch", "switch_session declined; restarting omp on the session"));
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
    /// Stops omp and starts it again with <paramref name="request"/> (other project, other approval mode, or recovery
    /// after a crash, resuming the same session file). The transcript is rebuilt from omp's history.
    /// </summary>
    /// <param name="force">Also when a run is active (the user asked to force-stop a stuck run).</param>
    public async Task RestartAsync(LaunchRequest request, CancellationToken ct = default, bool force = false)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Never kill a run the user did not stop, and never stop a working omp for a launch that cannot work.
            if (!force && Snapshot().Phase is SessionPhase.Running or SessionPhase.Aborting)
            {
                Mutate(s => s.AddNotice(NoticeLevel.Warning, "Stop the current run before changing sessions."));
                return;
            }
            if (request.WorkingDirectory is { } dir && !Directory.Exists(dir))
            {
                Mutate(s => s.AddNotice(NoticeLevel.Error, $"Folder not found: {dir}"));
                return;
            }
            if (request.ResumeSessionFile is { } file && !File.Exists(file))
            {
                Mutate(s => s.AddNotice(NoticeLevel.Error, $"Session file not found: {file}"));
                return;
            }
            if (_omp is { } old)
            {
                await StopAsync().ConfigureAwait(false);
                await old.DisposeAsync().ConfigureAwait(false);
            }
            _omp = null;
            _pump = null;
            _stopRequested = false;
            _request = request;
            Mutate(s => s.ResetTranscript());
            try { await StartCoreAsync(ct).ConfigureAwait(false); }
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
        try
        {
            await conn.SetModelAsync(model.Provider, model.Id, ct).ConfigureAwait(false);
            var state = await conn.GetStateAsync(ct).ConfigureAwait(false);
            Mutate(s =>
            {
                s.Model = state.ModelProvider is null ? model.Key : $"{state.ModelProvider}/{state.ModelId}";
                s.ThinkingLevel = state.ThinkingLevel;
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

    public async Task SetThinkingLevelAsync(string level, CancellationToken ct = default)
    {
        var conn = RequireConnection();
        if (RefuseWhileSigningIn()) return;
        try
        {
            await conn.SetThinkingLevelAsync(level, ct).ConfigureAwait(false);
            var state = await conn.GetStateAsync(ct).ConfigureAwait(false);
            Mutate(s => s.ThinkingLevel = state.ThinkingLevel ?? level);
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Error, $"Could not set thinking to {level}: {e.Message}"));
        }
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
        // omp runs commands one after another: until the sign-in ends, every other command would wait behind it
        // (and could run long after the client gave up on it). The client holds its own commands meanwhile.
        Mutate(s => s.SigningIn = providerId);
        try
        {
            await conn.LoginAsync(providerId, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Changes <c>--approval-mode</c>. It is a launch flag, so omp restarts on the same session. Choosing
    /// <c>yolo</c> is the user's explicit decision; the client never picks it by itself.
    /// </summary>
    public Task SetApprovalModeAsync(string mode, CancellationToken ct = default) =>
        RestartAsync(_request with { ApprovalMode = mode, ResumeSessionFile = ExistingSessionFile() }, ct);

    /// <summary>Starts omp again after a crash or a failed start, resuming the session it had open.</summary>
    public Task RecoverAsync(CancellationToken ct = default) =>
        RestartAsync(_request with { ResumeSessionFile = ExistingSessionFile() }, ct);

    /// <summary>
    /// The run does not stop (a tool ignores the abort, a sign-in waits for a browser): stop omp (stdin EOF, then the
    /// process tree is killed after the grace period) and start it again on the same session.
    /// </summary>
    public async Task ForceStopAsync(CancellationToken ct = default)
    {
        Mutate(s => s.AddNotice(NoticeLevel.Warning, "Force-stopping omp; it starts again on the same session."));
        await RestartAsync(_request with { ResumeSessionFile = ExistingSessionFile() }, ct, force: true).ConfigureAwait(false);
    }

    private string? ExistingSessionFile() =>
        Snapshot().SessionFile is { } f && File.Exists(f) ? f : null;

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
            Mutate(s => s.SetPhase(SessionPhase.Starting, Now));
            try { await change(conn).ConfigureAwait(false); }
            catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException)
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
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Error, "Could not confirm which session omp has open (" + e.Message + "). Use Restart omp in Settings if the conversation looks wrong."));
        }
    }

    /// <summary>
    /// stdout normally ends with the process. If something else still holds the pipe open (a descendant that
    /// inherited it), end the connection a few seconds after the exit so the session can report and close.
    /// </summary>
    private static async Task CloseAfterExitAsync(OmpProcess omp)
    {
        await omp.Exited.ConfigureAwait(false);
        var eof = await Task.WhenAny(omp.Connection.Completion, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        if (eof != omp.Connection.Completion) await omp.Connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Sends a prompt. The ack arrives at once; the run ends with a terminal agent_end.</summary>
    public Task PromptAsync(string text, CancellationToken ct = default) => PromptAsync(text, [], ct);

    public async Task PromptAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken ct = default)
    {
        var conn = RequireConnection();
        if (RefuseWhileSigningIn()) return;
        Mutate(s =>
        {
            s.AddUserPrompt(text, images.Count);
            s.BeginRun(Now);
        });
        try
        {
            var ack = await conn.PromptAsync(text, Wire(images), ct: ct).ConfigureAwait(false);
            if (ack.Data is { } d && d.TryGetProperty("agentInvoked", out var inv) && inv.ValueKind == System.Text.Json.JsonValueKind.False)
                Mutate(s => s.EndRun(interrupted: false, Now));
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or ArgumentException)
        {
            // ArgumentException: the prompt does not fit omp's 1 MiB line limit.
            Mutate(s =>
            {
                s.AddNotice(NoticeLevel.Error, "Prompt failed: " + e.Message);
                s.EndRun(interrupted: true, Now);
            });
        }
    }

    private readonly SemaphoreSlim _commands = new(1, 1);

    /// <summary>
    /// Runs one of omp's builtin slash commands (<see cref="OmpBuiltins.OverRpc"/>, e.g. "/mcp list", "/usage") and
    /// returns what it printed, without it appearing in the conversation. omp prints before it answers the request,
    /// so the output is complete when this returns. A command that starts an agent run (/retry, /handoff) marks the
    /// session running like a prompt. Commands run one at a time; a TUI-only command is refused, never sent (omp
    /// would pass it to the model as a message).
    /// </summary>
    public async Task<SlashCommandResult> RunSlashCommandAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!OmpBuiltins.RunsOverRpc(command))
            return new(false, "", Error: $"/{OmpBuiltins.NameOf(command)} is not available over omp's RPC");
        // This omp lacks the builtin (an older version): sent anyway it would become a message to the model.
        if (!OmpBuiltins.RunsOverRpc(command, Snapshot().Commands))
            return new(false, "", Error: $"This version of omp has no /{OmpBuiltins.NameOf(command)} command", Unsupported: true);
        if (_omp?.Connection is not { } conn) return new(false, "", Error: "omp is not running");
        if (Snapshot().SigningIn is { } provider) return new(false, "", Error: $"omp is busy with the sign-in to {provider}");
        await _commands.WaitAsync(ct).ConfigureAwait(false);
        var output = new System.Text.StringBuilder();
        try
        {
            conn.Intercept = (type, frame) =>
            {
                if (type != "command_output") return false;
                var text = frame.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString()
                    : frame.TryGetProperty("output", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.String ? o.GetString() : null;
                lock (output)
                {
                    if (output.Length > 0) output.Append('\n');
                    output.Append(text ?? "");
                }
                return true;
            };
            var ack = await conn.RequestAsync("prompt", w => w.WriteString("message", command), timeout ?? TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            string printed;
            lock (output) printed = output.ToString().TrimEnd();
            if (!ack.Success) return new(false, printed, Error: ack.Error ?? "unknown error");
            var invoked = !(ack.Data is { } d && d.ValueKind == System.Text.Json.JsonValueKind.Object
                            && d.TryGetProperty("agentInvoked", out var inv) && inv.ValueKind == System.Text.Json.JsonValueKind.False);
            if (invoked) Mutate(s => s.BeginRun(Now));
            return new(true, printed, invoked);
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException or ArgumentException)
        {
            string printed;
            lock (output) printed = output.ToString().TrimEnd();
            return new(false, printed, Error: e.Message);
        }
        finally
        {
            conn.Intercept = null;
            _commands.Release();
        }
    }

    /// <summary>
    /// A message while the agent works: <see cref="QueueKind.Steer"/> reaches it at the next tool boundary,
    /// <see cref="QueueKind.FollowUp"/> when it would otherwise stop. Shown as queued until omp delivers it.
    /// </summary>
    public async Task QueueAsync(QueueKind kind, string text, IReadOnlyList<ImageAttachment> images, CancellationToken ct = default)
    {
        var conn = RequireConnection();
        QueuedMessage? queued = null;
        Mutate(s => queued = s.Enqueue(kind, text, images.Count));
        try
        {
            if (kind == QueueKind.Steer) await conn.SteerAsync(text, Wire(images), ct).ConfigureAwait(false);
            else await conn.FollowUpAsync(text, Wire(images), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or ArgumentException)
        {
            Mutate(s =>
            {
                s.RemoveQueued(queued!.Seq);
                s.AddNotice(NoticeLevel.Error, $"Could not queue the message ({e.Message}): {text}");
            });
        }
    }

    /// <summary>
    /// Takes a queued message back before omp delivers it (omp's <c>remove_queued_message</c>). Returns its images
    /// when omp withdrew it; null when omp had already delivered it or did not answer. An omp without the command
    /// (before 18.4.4) throws <see cref="RpcCommandException"/>.
    /// </summary>
    public async Task<IReadOnlyList<ImageAttachment>?> RemoveQueuedAsync(QueuedMessage message, CancellationToken ct = default)
    {
        var conn = RequireConnection();
        try
        {
            var (removed, images) = await conn.RemoveQueuedMessageAsync(message.Text, message.Kind == QueueKind.Steer, ct).ConfigureAwait(false);
            if (!removed) return null;
            Mutate(s => s.RemoveQueued(message.Seq));
            return [.. images.Select((i, n) => new ImageAttachment($"image {n + 1}", i.MimeType, i.Data))];
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException)
        {
            return null;
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
                try
                {
                    var state = await conn.GetStateAsync().ConfigureAwait(false);
                    Mutate(s =>
                    {
                        if (state.ModelProvider is not null) s.Model = $"{state.ModelProvider}/{state.ModelId}";
                        s.ThinkingLevel = state.ThinkingLevel;
                        s.SessionName = state.SessionName ?? s.SessionName;
                        s.Todos = ConversationState.ParseTodos(state.TodoPhases);
                        s.ContextPercent = state.ContextPercent;
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
            if (Snapshot() is not { Phase: SessionPhase.Ready, Queued.Count: > 0 }) return;
            var state = await conn.GetStateAsync().ConfigureAwait(false);
            if (state.QueuedMessageCount > 0 || state.IsStreaming) return;
            Mutate(s => { if (s.Phase == SessionPhase.Ready) s.DropUndeliveredQueue(); });
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException) { }
    }

    public async Task AbortAsync(CancellationToken ct = default)
    {
        var conn = RequireConnection();
        List<PendingDialog> open = [];
        Mutate(s =>
        {
            if (s.Phase == SessionPhase.Running) s.SetPhase(SessionPhase.Aborting, Now);
            open = s.CloseAllDialogs("the run was stopped");
        });
        try
        {
            // omp 18.2.0 does not pass the run's abort signal to a tool approval prompt: until it is answered
            // the tool, and with it the abort, would wait forever. Answer open dialogs first.
            foreach (var d in open) await conn.CancelExtensionUiAsync(d.Id, ct).ConfigureAwait(false);
            await conn.AbortAsync(ct).ConfigureAwait(false);
            // omp answers abort only once the agent is idle, so the run is over even if no agent_end
            // follows (e.g. a local-only command). Settle it; a late agent_end is harmless.
            Mutate(s => { if (s.Phase == SessionPhase.Aborting) s.EndRun(interrupted: true, Now); });
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException)
        {
            Mutate(s => s.AddNotice(NoticeLevel.Error, "Stop did not finish (" + e.Message + "). Press Force stop to restart omp on this session."));
        }
    }

    /// <summary>
    /// Answers a pending dialog. Returns false when it is no longer pending (withdrawn by omp, expired,
    /// already answered, or omp stopped); nothing is sent then, so a late click is never presented as an answer.
    /// </summary>
    public async Task<bool> AnswerDialogAsync(string id, DialogAnswer answer, CancellationToken ct = default)
    {
        var conn = RequireConnection();
        if (answer is DialogAnswer.Value { Text: var text } && System.Text.Encoding.UTF8.GetByteCount(text) > MaxAnswerBytes)
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
                case DialogAnswer.Confirmed c: await conn.SendExtensionUiConfirmAsync(id, c.Yes, ct).ConfigureAwait(false); break;
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

    private async Task ExpireAtDeadlineAsync(PendingDialog d)
    {
        // Timers and the wall clock are different clocks: a timer can fire a moment before the deadline by the
        // wall clock, so wait again until the dialog really is past it (or no longer pending).
        while (true)
        {
            var wait = d.Deadline!.Value - Now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait + TimeSpan.FromMilliseconds(5), _clock).ConfigureAwait(false);
            var pending = false;
            Mutate(s =>
            {
                s.ExpireDialogs(Now);
                pending = s.Dialogs.Any(x => x.Id == d.Id);
            });
            if (!pending) return;
        }
    }

    /// <summary>A cheap non-model round trip (get_state); used for latency measurement and health checks.</summary>
    public async Task<RpcResponse> ProbeAsync(CancellationToken ct = default) =>
        await RequireConnection().RequestAsync("get_state", ct: ct).ConfigureAwait(false);

    public async Task StopAsync(TimeSpan? grace = null)
    {
        if (_omp is not { } omp) return;
        _stopRequested = true;
        // A session that already failed or stopped keeps that phase; only a live one goes through Stopping.
        Mutate(s => { if (s.Phase is not (SessionPhase.Faulted or SessionPhase.Stopped)) s.SetPhase(SessionPhase.Stopping, Now); });
        await omp.StopAsync(grace ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (_pump is not null)
        {
            // The pump ends at stdout EOF; CloseAfterExitAsync guarantees that shortly after the exit.
            try { await _pump.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException) { await omp.Connection.DisposeAsync().ConfigureAwait(false); await _pump.ConfigureAwait(false); }
        }
        else
        {
            Mutate(s => { if (s.Phase == SessionPhase.Stopping) s.SetPhase(SessionPhase.Stopped, Now); });
        }
    }

    private RpcConnection RequireConnection() =>
        _omp?.Connection ?? throw new InvalidOperationException("omp is not running");

    private async Task PumpAsync(OmpProcess omp)
    {
        var conn = omp.Connection;
        await foreach (var frame in conn.Events.ReadAllAsync().ConfigureAwait(false))
        {
            PendingDialog[] deadlines = [];
            string[] toCancel = [];
            var reconcile = false;
            // When the line was read, not when the pump got to it: frames can wait in the queue (e.g. while history
            // loads at start), and dialog deadlines count from omp's send time.
            var readAt = Now - Stopwatch.GetElapsedTime(frame.ReadTimestamp);
            lock (_lock)
            {
                var wasActive = _state.Phase is SessionPhase.Running or SessionPhase.Aborting;
                try { _state.Apply(frame, readAt); }
                catch (Exception e) when (e is InvalidOperationException or FormatException or KeyNotFoundException or IndexOutOfRangeException or ArgumentException or OverflowException)
                {
                    // A frame of an unexpected shape must not stop the session; report it and go on.
                    _state.AddNotice(NoticeLevel.Warning, $"Could not apply a '{frame.Type}' event from omp: {e.Message}");
                }
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
            }
            if (reconcile) _ = ReconcileQueueAsync(conn);
            foreach (var id in toCancel)
            {
                try { await conn.CancelExtensionUiAsync(id).ConfigureAwait(false); }
                catch (RpcConnectionClosedException) { }
            }
            _changes.Writer.TryWrite(true);
            if (NeedsSettleProbe(conn)) EnsureSettleProbe(conn);
            foreach (var d in deadlines) _ = ExpireAtDeadlineAsync(d);
        }

        // stdout ended: the process is exiting (or was stopped), or the stream broke. Report how it ended.
        var protocolError = conn.CloseReason?.InnerException is RpcProtocolException;
        if (protocolError) omp.Kill(); // omp is alive but its stream can no longer be trusted
        int? code = null;
        try { code = await omp.Exited.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { omp.Kill(); }
        Mutate(s =>
        {
            s.CloseAllDialogs(_stopRequested ? "omp was stopped" : "omp is no longer running");
            if (_stopRequested)
            {
                // We asked omp to stop: a kill after the grace period is still a stop, not a fault.
                s.Log(Now, "exit", code == 0 ? "omp exited normally" : $"omp stopped (exit code {code?.ToString() ?? "unknown"})");
                s.EndRun(interrupted: true, Now);
                s.SetPhase(SessionPhase.Stopped, Now);
            }
            else
            {
                var tail = LastLines(omp.StderrTail, 12);
                var what = protocolError
                    ? $"omp RPC stream broke ({conn.CloseReason!.InnerException!.Message}); omp was stopped"
                    // The process is gone: the end of its output says nothing the exit code does not (a read error does)
                    : (code is { } c ? $"omp exited unexpectedly (exit code {c})" : "omp exited unexpectedly")
                      + (conn.CloseReason is { InnerException: not null } r ? ": " + r.Message : "");
                s.Fail(what + (tail.Length > 0 ? "\n" + tail : ""), Now);
            }
        });
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
        if (conn.CloseReason is not null || _stopRequested) return false;
        lock (_lock) return _state.AwaitingSettle;
    }

    private async Task ProbeUntilSettledAsync(RpcConnection conn)
    {
        while (NeedsSettleProbe(conn))
        {
            long frames;
            DateTimeOffset? last;
            lock (_lock)
            {
                frames = _state.FramesReceived;
                last = _state.LastFrameAt;
            }
            var quietFor = Now - (last ?? Now);
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
                if (!s.AwaitingSettle || s.FramesReceived != frames || !state.LooksSettled) return;
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
        Mutate(s => s.Log(Now, "stderr", line.Length > 300 ? line[..300] : line));
    }

    internal void Mutate(Action<ConversationState> change)
    {
        lock (_lock) change(_state);
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
        if (_omp is { } omp)
        {
            try { await StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception e) when (e is TimeoutException or InvalidOperationException) { }
            await omp.DisposeAsync().ConfigureAwait(false);
        }
        _changes.Writer.TryComplete();
    }
}
