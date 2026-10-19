using System.Collections.Concurrent;
using System.Text.Json;
using Ombra.Rpc;

namespace Ombra.ClientCore;

/// <summary>What a host tool produced for the agent: its text, or why it failed (<see cref="IsError"/>).</summary>
public sealed record HostToolResult(string Text, bool IsError = false);

/// <summary>A tool the client runs for the agent: its definition and what runs it (arguments, omp's cancellation).</summary>
public sealed record HostTool(HostToolDefinition Definition, Func<JsonElement, CancellationToken, Task<HostToolResult>> ExecuteAsync);

/// <summary>
/// Host tools (omp's <c>set_host_tools</c>, host-tools.ts): registered on every connection (start, restart, recovery)
/// and whenever the list changes; omp asks for a run with <c>host_tool_call</c>, takes the answer as
/// <c>host_tool_result</c> and cancels with <c>host_tool_cancel</c> (an aborted turn). Approval stays omp's: a host tool
/// is gated like any other tool (its tier, the approval mode, the user's <c>tools.approval</c> rules) before omp calls it.
/// </summary>
public sealed partial class SessionController
{
    private IReadOnlyList<HostTool> _hostTools = [];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _hostToolCalls = new(StringComparer.Ordinal);

    /// <summary>The tools this chat's agent can call in the client; follows sibling chats.</summary>
    public IReadOnlyList<HostTool> HostTools
    {
        get => Volatile.Read(ref _hostTools);
        set
        {
            Volatile.Write(ref _hostTools, value);
            if (_omp?.Connection is { CloseReason: null } conn) _ = RegisterHostToolsAsync(conn, CancellationToken.None);
        }
    }

    private async Task RegisterHostToolsAsync(RpcConnection conn, CancellationToken ct)
    {
        var tools = HostTools;
        if (tools.Count == 0) return;
        try
        {
            var names = await conn.SetHostToolsAsync([.. tools.Select(t => t.Definition)], ct).ConfigureAwait(false);
            Mutate(s => s.Log(Now, "host_tools", "registered " + string.Join(", ", names)));
        }
        catch (Exception e) when (e is RpcCommandException or TimeoutException or RpcConnectionClosedException or IOException or ObjectDisposedException)
        {
            Mutate(s => s.Log(Now, "host_tools", "set_host_tools failed: " + e.Message));
        }
    }

    /// <summary>Runs one <c>host_tool_call</c> off the frame pump and answers it, unless omp cancelled it meanwhile.</summary>
    private async Task HandleHostToolCallAsync(RpcConnection conn, RpcFrame frame)
    {
        if (HostToolCall.Parse(frame.Json) is not { } call) return;
        var tool = HostTools.FirstOrDefault(t => t.Definition.Name == call.ToolName);
        var cancel = new CancellationTokenSource();
        _hostToolCalls[call.Id] = cancel;
        Mutate(s => s.Log(Now, "host_tool_call", call.ToolName));
        HostToolResult result;
        try
        {
            result = tool is null
                ? new HostToolResult($"The Ombra has no host tool named {call.ToolName}.", IsError: true)
                : await tool.ExecuteAsync(call.Arguments, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            result = new HostToolResult(e.Message, IsError: true);
        }
        finally { _hostToolCalls.TryRemove(call.Id, out _); }
        // omp has already settled a cancelled call: an answer now would be dropped
        if (cancel.IsCancellationRequested || conn.CloseReason is not null) return;
        try { await conn.SendHostToolResultAsync(call.Id, result.Text, result.IsError).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or RpcConnectionClosedException or OperationCanceledException) { }
    }

    /// <summary><c>host_tool_cancel {targetId}</c>: the turn that called the tool was aborted.</summary>
    private void CancelHostToolCall(RpcFrame frame)
    {
        if (frame.Json.TryGetProperty("targetId", out var target) && target.ValueKind == JsonValueKind.String
            && _hostToolCalls.TryRemove(target.GetString()!, out var cancel))
        {
            cancel.Cancel();
            Mutate(s => s.Log(Now, "host_tool_cancel", target.GetString()!));
        }
    }

    /// <summary>The connection is gone: every call still running stops.</summary>
    private void CancelHostToolCalls()
    {
        foreach (var id in _hostToolCalls.Keys)
            if (_hostToolCalls.TryRemove(id, out var cancel)) cancel.Cancel();
    }
}
