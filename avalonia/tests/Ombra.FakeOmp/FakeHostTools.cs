using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Ombra.FakeOmp;

/// <summary>
/// Host tools like the owned engine (host-tools.ts, rpc-mode.ts <c>set_host_tools</c>): the client registers its tools;
/// a turn calls one with <c>host_tool_call</c> and waits for <c>host_tool_result</c>; an aborted turn sends
/// <c>host_tool_cancel</c> and stops waiting. Registrations are appended to FAKE_OMP_HOST_TOOLS_LOG ("set_host_tools a,b").
/// </summary>
internal sealed class FakeHostTools(Action<object> emit, Action<string?, string, object?, string?, string?> respond)
{
    private volatile string[] _names = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private int _seq;

    public bool Has(string name) => _names.Contains(name);

    /// <summary>Handles the host tool frames; false for every other command.</summary>
    public bool Handle(string? id, string type, JsonObject? cmd)
    {
        switch (type)
        {
            case "set_host_tools":
                _names = [.. (cmd?["tools"] as JsonArray ?? []).Select(t => t?["name"]?.GetValue<string>()).OfType<string>()];
                if (Environment.GetEnvironmentVariable("FAKE_OMP_HOST_TOOLS_LOG") is { Length: > 0 } log)
                    FakeEngineHost.AppendLog(log, "set_host_tools " + string.Join(',', _names) + "\n");
                respond(id, type, new { toolNames = _names }, null, null);
                return true;
            case "host_tool_result":
                // A control frame (no response); an answer to a cancelled call is dropped, as in host-tools.ts
                if (id is not null && _pending.TryRemove(id, out var pending)) pending.TrySetResult(cmd);
                return true;
            case "host_tool_update":
                return true;
            default:
                return false;
        }
    }

    /// <summary>Calls a registered tool; the text it answered and whether it failed, or null when <paramref name="ct"/>
    /// ended the wait (then the host was told to cancel).</summary>
    public async Task<(string Text, bool IsError)?> CallAsync(string toolName, string toolCallId, JsonObject arguments, CancellationToken ct)
    {
        var id = $"host{Interlocked.Increment(ref _seq)}";
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        emit(new { type = "host_tool_call", id, toolCallId, toolName, arguments });
        using (ct.Register(() =>
        {
            if (!_pending.TryRemove(id, out _)) return;
            emit(new { type = "host_tool_cancel", id = $"cancel-{id}", targetId = id });
            answer.TrySetResult(null);
        }))
        {
            var frame = await answer.Task;
            if (frame is null) return null;
            var text = string.Join("\n", (frame["result"]?["content"] as JsonArray ?? []).Select(p => p?["text"]?.GetValue<string>()).OfType<string>());
            return (text, frame["isError"]?.GetValue<bool>() == true);
        }
    }
}
