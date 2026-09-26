using OmpGui.Rpc;

namespace OmpGui.ClientCore;

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

    /// <summary>A subagent's own conversation (the newest messages), or the reason it could not be read.</summary>
    public async Task<(IReadOnlyList<SubagentMessage> Messages, string? Error)> GetSubagentMessagesAsync(string subagentId, CancellationToken ct = default)
    {
        if (_omp?.Connection is not { } conn) return ([], "omp is not running");
        if (Snapshot().SigningIn is { } provider) return ([], $"omp is busy with the sign-in to {provider}");
        try
        {
            var r = await conn.RequestAsync("get_subagent_messages", w => w.WriteString("subagentId", subagentId), SubagentRequestTimeout, ct).ConfigureAwait(false);
            if (!r.Success) return ([], r.Error ?? "omp could not read it");
            return r.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } d ? (ConversationState.ParseSubagentMessages(d), null) : ([], null);
        }
        catch (Exception e) when (e is TimeoutException or RpcConnectionClosedException)
        {
            return ([], e.Message);
        }
    }
}
