using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>
/// Subagents over omp's RPC (rpc-mode.ts, rpc-subagents.ts): the client subscribes to their lifecycle and progress
/// once omp is up (<c>set_subagent_subscription</c>, level "progress"; omp keeps the level across session switches),
/// asks what runs now (<c>get_subagents</c>) and reads a subagent's own conversation on request
/// (<c>get_subagent_messages</c>). An omp without these commands answers "Unknown command" without an id; that is
/// recorded as "not supported" (ConversationState.Subagents.cs), never shown as an error.
/// </summary>
public sealed partial class SessionController
{
    private static readonly TimeSpan SubagentRequestTimeout = TimeSpan.FromSeconds(15);
    /// <summary>omp holds the steer answer until the subagent takes the message (its setup between turns included).</summary>
    private static readonly TimeSpan SubagentSteerTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Called once omp is up (start, restart, recovery): subscribe, then take what already runs.</summary>
    private async Task SubscribeSubagentsAsync(RpcConnection conn)
    {
        try
        {
            var r = await conn.RequestAsync("set_subagent_subscription", w => w.WriteString("level", "progress"), SubagentRequestTimeout).ConfigureAwait(false);
            Mutate(s => s.SubagentsSupported = r.Success);
            if (r.Success) await RefreshSubagentsAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException or OperationCanceledException)
        {
            // No answer with our id: an omp without the command answered without one (handled as a frame), or omp went away.
        }
    }

    /// <summary>Asks omp which subagents run now and merges them into the session's list; false when it could not.</summary>
    public async Task<bool> RefreshSubagentsAsync(CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn || Snapshot() is not { SubagentsSupported: true, SigningIn: null }) return false;
        try
        {
            var r = await conn.RequestAsync("get_subagents", timeout: SubagentRequestTimeout, ct: ct).ConfigureAwait(false);
            if (!r.Success || r.Data is not { ValueKind: System.Text.Json.JsonValueKind.Object } d || !d.TryGetProperty("subagents", out var list)) return false;
            var copy = list.Clone();
            Mutate(s => s.MergeSubagents(copy, Now));
            return true;
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Reads complete transcript lines from a byte cursor; errors leave the caller's cursor unchanged.</summary>
    public async Task<(SubagentMessagesPage? Page, string? Error)> GetSubagentMessagesAsync(string subagentId, long fromByte = 0, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromByte);
        if (_omp?.Connection is not { } conn) return (null, "omp is not running");
        if (Snapshot().SigningIn is { } provider) return (null, $"omp is busy with the sign-in to {provider}");
        try
        {
            var r = await conn.RequestAsync("get_subagent_messages", w =>
            {
                w.WriteString("subagentId", subagentId);
                w.WriteNumber("fromByte", fromByte);
            }, SubagentRequestTimeout, ct).ConfigureAwait(false);
            if (!r.Success) return (null, r.Error ?? "omp could not read it");
            return r.Data is { } d && ConversationState.ParseSubagentMessagesPage(d, fromByte) is { } page
                ? (page, null) : (null, "omp returned an invalid subagent transcript");
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException)
        {
            return (null, e.Message);
        }
    }

    /// <summary>
    /// Stops one subagent (<c>cancel_subagent</c>). omp answers whether it was still running: when it was, the row
    /// ends as stopped now (omp's lifecycle frame confirms); when not, the list is read again. Null on success.
    /// </summary>
    public async Task<string?> CancelSubagentAsync(string subagentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subagentId);
        if (_omp?.Connection is not { } conn) return "omp is not running";
        if (Snapshot().SigningIn is { } provider) return $"omp is busy with the sign-in to {provider}";
        try
        {
            var r = await conn.RequestAsync("cancel_subagent", w => w.WriteString("subagentId", subagentId), SubagentRequestTimeout, ct).ConfigureAwait(false);
            if (!r.Success) return r.Error ?? "omp could not stop it";
            if (r.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } d && d.TryGetProperty("cancelled", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.True)
                Mutate(s => s.EndRunningSubagents("aborted", Now, subagentId));
            else await RefreshSubagentsAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Sends a message to a running subagent (<c>steer_subagent</c>); omp answers once the subagent took it, which
    /// between its turns can take a while. Null on success, else why it was not delivered.
    /// </summary>
    public async Task<string?> SteerSubagentAsync(string subagentId, string message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subagentId);
        if (string.IsNullOrWhiteSpace(message)) return "the message is empty";
        if (_omp?.Connection is not { } conn) return "omp is not running";
        if (Snapshot().SigningIn is { } provider) return $"omp is busy with the sign-in to {provider}";
        try
        {
            var r = await conn.RequestAsync("steer_subagent", w =>
            {
                w.WriteString("subagentId", subagentId);
                w.WriteString("message", message);
            }, SubagentSteerTimeout, ct).ConfigureAwait(false);
            return r.Success ? null : r.Error ?? "omp could not deliver it";
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException)
        {
            return e.Message;
        }
    }
}
