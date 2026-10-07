using System.Text.Json;
using OmpGui.Rpc;

namespace OmpGui.ClientCore;

/// <summary>What <c>get_state</c> says about the session's model options and context (rpc-mode.ts, omp 18.2.0).</summary>
/// <param name="FastModeEnabled">The priority service tier is on for the model's family (<c>/fast</c>).</param>
/// <param name="FastModeActive">…and the current model really gets it on the wire.</param>
/// <param name="AutoCompaction">omp compacts the context by itself when it fills up (<c>compaction.enabled</c>).</param>
public sealed record SessionOptionsState(bool FastModeEnabled, bool FastModeActive, bool AutoCompaction, bool IsCompacting,
    long? ContextTokens, long? ContextWindow, double? ContextPercent);

/// <summary>
/// Session-level commands beyond the conversation: the model options omp has RPC setters for (fast mode,
/// auto-compaction, auto-retry), waiting for what a background builtin (<c>/compact</c>, <c>/handoff</c>) prints when
/// it ends, and following a <c>/move</c>.
/// </summary>
public sealed partial class SessionController
{
    /// <summary>get_state's option fields; null when omp is not running or does not answer.</summary>
    public async Task<SessionOptionsState?> GetOptionsStateAsync(CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return null;
        try
        {
            var r = await conn.RequestAsync("get_state", ct: ct).ConfigureAwait(false);
            if (!r.Success || r.Data is not { ValueKind: JsonValueKind.Object } d) return null;
            static bool B(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
            static double? D(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
            var usage = d.TryGetProperty("contextUsage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
            var hasUsage = usage.ValueKind == JsonValueKind.Object;
            return new SessionOptionsState(B(d, "fastModeEnabled"), B(d, "fastModeActive"), B(d, "autoCompactionEnabled"), B(d, "isCompacting"),
                hasUsage && D(usage, "tokens") is { } t ? (long)t : null,
                hasUsage && D(usage, "contextWindow") is { } w ? (long)w : null,
                hasUsage ? D(usage, "percent") : null);
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException or InvalidOperationException) { return null; }
    }

    /// <summary><c>get_session_stats</c>: this session's tokens and cost; null when omp is not running or does not answer.</summary>
    public async Task<SessionTokenStats?> GetSessionStatsAsync(CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return null;
        try
        {
            var r = await conn.RequestAsync("get_session_stats", ct: ct).ConfigureAwait(false);
            if (!r.Success || r.Data is not { ValueKind: JsonValueKind.Object } d) return null;
            static double N(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
            var t = d.TryGetProperty("tokens", out var tokens) ? tokens : default;
            return new SessionTokenStats((long)N(t, "input"), (long)N(t, "output"), (long)N(t, "cacheRead"), (long)N(t, "cacheWrite"),
                (long)N(t, "total"), N(d, "cost"), (long)N(d, "premiumRequests"));
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// <c>set_fast_mode</c>: the priority service tier for the current model's family, recorded in the session. omp
    /// refuses to turn it on for a model without a tier (its error is returned).
    /// </summary>
    public async Task<(bool Enabled, string? Error)> SetFastModeAsync(bool enabled, CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return (false, "omp is not running");
        try
        {
            var r = await conn.RequestAsync("set_fast_mode", w => w.WriteBoolean("enabled", enabled), ct: ct).ConfigureAwait(false);
            if (!r.Success) return (false, r.Error ?? "unknown error");
            var on = r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
            return (on, null);
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException) { return (false, e.Message); }
    }

    /// <summary><c>set_auto_compaction</c>: omp saves it in its settings (<c>compaction.enabled</c>, every session).</summary>
    public Task<string?> SetAutoCompactionAsync(bool enabled, CancellationToken ct = default) => SetFlagAsync("set_auto_compaction", enabled, ct);

    /// <summary><c>set_auto_retry</c>: omp saves it in its settings (<c>retry.enabled</c>, every session).</summary>
    public Task<string?> SetAutoRetryAsync(bool enabled, CancellationToken ct = default) => SetFlagAsync("set_auto_retry", enabled, ct);

    private async Task<string?> SetFlagAsync(string command, bool enabled, CancellationToken ct)
    {
        if (_omp?.Connection is not { } conn) return "omp is not running";
        try
        {
            var r = await conn.RequestAsync(command, w => w.WriteBoolean("enabled", enabled), ct: ct).ConfigureAwait(false);
            return r.Success ? null : r.Error ?? "unknown error";
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException) { return e.Message; }
    }

    /// <summary>
    /// Stops what omp is doing in the background for a builtin (<c>/compact</c>, <c>/handoff</c>): omp routes
    /// <c>abort</c> to the compaction or handoff, which then ends without printing anything.
    /// </summary>
    public async Task<bool> StopBackgroundCommandAsync(CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return false;
        try
        {
            await conn.AbortAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException) { return false; }
    }

    /// <summary>
    /// Waits for the line a background builtin prints when it ends. Over RPC, <c>/compact</c> and <c>/handoff</c>
    /// answer at once and print their result later, as <c>command_output</c> in the event stream; that line joins the
    /// conversation (as omp's own record of it) and is returned here. Null on timeout, or when the conversation was
    /// replaced (another session) meanwhile.
    /// </summary>
    /// <param name="since">The snapshot taken before the command was sent: only output that came after it counts.</param>
    public async Task<string?> WaitForCommandOutputAsync(SessionSnapshot since, Func<string, bool> isFinal, TimeSpan timeout, CancellationToken ct = default)
    {
        var seen = since.Items.OfType<CommandOutputItem>().Select(i => i.Key).ToHashSet();
        var deadline = _clock.GetUtcNow() + timeout;
        while (_clock.GetUtcNow() < deadline)
        {
            var s = Snapshot();
            if (s.TranscriptEpoch != since.TranscriptEpoch) return null;
            foreach (var item in s.Items.OfType<CommandOutputItem>())
                if (!seen.Contains(item.Key) && isFinal(item.Text)) return item.Text;
            if (s.Phase is SessionPhase.Faulted or SessionPhase.Stopped) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(100), _clock, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>
    /// Runs a shell command in the session (<c>!command</c>, omp's RPC <c>bash</c>): omp runs it in the project and keeps
    /// the command and its output in the session and the model's context. omp answers when the command ends; it runs
    /// beside the command queue, so <see cref="StopShellCommandAsync"/> can end it. The conversation shows it as a row.
    /// </summary>
    public async Task<bool> RunShellCommandAsync(string command, CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return false;
        string id = "";
        Mutate(s => id = s.AddShellCommand(command, Now));
        try
        {
            var r = await conn.RequestAsync("bash", w => w.WriteString("command", command), TimeSpan.FromHours(4), ct).ConfigureAwait(false);
            if (!r.Success)
            {
                Mutate(s => s.FinishShellCommand(id, r.Error ?? "omp did not run it", 1, false, Now));
                return false;
            }
            var d = r.Data is { ValueKind: JsonValueKind.Object } data ? data : default;
            var hasData = d.ValueKind == JsonValueKind.Object;
            var output = hasData ? ConversationState.Str(d, "output") ?? "" : "";
            int? exit = hasData && d.TryGetProperty("exitCode", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var code) ? code : null;
            var cancelled = hasData && d.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.True;
            Mutate(s => s.FinishShellCommand(id, output, exit, cancelled, Now));
            return !cancelled && exit is null or 0;
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException or OperationCanceledException or ArgumentException)
        {
            Mutate(s => s.FinishShellCommand(id, e.Message, null, true, Now));
            return false;
        }
    }

    /// <summary><c>abort_bash</c>: ends the running shell command.</summary>
    public async Task StopShellCommandAsync(CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return;
        try { await conn.RequestAsync("abort_bash", ct: ct).ConfigureAwait(false); }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException) { }
    }

    /// <summary>The session's own messages a rewind can start from (<c>get_branch_messages</c>), oldest first.</summary>
    public async Task<IReadOnlyList<(string EntryId, string Text)>> GetBranchMessagesAsync(CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return [];
        try
        {
            var r = await conn.RequestAsync("get_branch_messages", ct: ct).ConfigureAwait(false);
            if (!r.Success || r.Data is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("messages", out var ms) || ms.ValueKind != JsonValueKind.Array) return [];
            return [.. ms.EnumerateArray()
                .Where(m => m.ValueKind == JsonValueKind.Object && ConversationState.Str(m, "entryId") is not null)
                .Select(m => (ConversationState.Str(m, "entryId")!, ConversationState.Str(m, "text") ?? ""))];
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException) { return []; }
    }

    /// <summary>
    /// Rewinds to before one of the user's messages (<c>branch</c>, what omp's /branch and its extensions use): omp
    /// starts a new session file with the conversation up to that message — the old session stays as it was — and the
    /// window loads it. Returns the message's text (to edit and send again); null when omp or an extension declined.
    /// </summary>
    public async Task<string?> RewindAsync(string entryId, CancellationToken ct = default)
    {
        string? text = null;
        await SessionChangeAsync(async conn =>
        {
            var r = await conn.RequestAsync("branch", w => w.WriteString("entryId", entryId), TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
            if (!r.Success) throw new RpcCommandException("branch", r.Error ?? "unknown error");
            if (r.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.True)
            {
                Mutate(s => s.AddNotice(NoticeLevel.Warning, "An extension cancelled the rewind."));
                return;
            }
            text = r.Data is { ValueKind: JsonValueKind.Object } data ? ConversationState.Str(data, "text") ?? "" : "";
            await LoadSessionAsync(conn, _request.WorkingDirectory, ct, newProcess: false).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return text;
    }

    /// <summary>Asks get_state again (context usage after a compaction, the model after a change).</summary>
    public void RefreshState()
    {
        if (_omp?.Connection is { } conn) RequestStateRefresh(conn);
    }

    /// <summary>
    /// After omp moved the session to another folder (<c>/move</c> moves the file and re-scopes omp in place): follow
    /// it — the session file omp now writes, the new project folder, and that folder for the next start of omp.
    /// </summary>
    public async Task<bool> FollowMovedSessionAsync(string folder, CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return false;
        try
        {
            var state = await conn.GetStateAsync(ct).ConfigureAwait(false);
            _request = _request with { WorkingDirectory = folder, ResumeSessionFile = null };
            Mutate(s =>
            {
                s.Cwd = folder;
                if (state.SessionFile is { } f) s.SessionFile = f;
                s.Log(Now, "move", "session moved to " + folder); // also a new snapshot version
            });
            return true;
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException) { return false; }
    }
}
