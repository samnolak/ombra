using System.Text.Json;

namespace Ombra.Rpc;

/// <summary>
/// A tool the client runs for the agent (<c>RpcHostToolDefinition</c>, rpc-types.ts). <see cref="ParametersJson"/> is the
/// JSON schema of its arguments; <see cref="LoadMode"/> "essential" puts it in every request, "discoverable" only after
/// the model searched for it.
/// </summary>
public sealed record HostToolDefinition(string Name, string Label, string Description, string ParametersJson, string LoadMode = "essential");

/// <summary>omp's <c>host_tool_call</c>: run <see cref="ToolName"/> with <see cref="Arguments"/> and answer <see cref="Id"/>.</summary>
public sealed record HostToolCall(string Id, string ToolCallId, string ToolName, JsonElement Arguments)
{
    public static HostToolCall? Parse(JsonElement frame) =>
        frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
        && frame.TryGetProperty("toolName", out var name) && name.ValueKind == JsonValueKind.String
            ? new HostToolCall(id.GetString()!,
                frame.TryGetProperty("toolCallId", out var call) && call.ValueKind == JsonValueKind.String ? call.GetString()! : "",
                name.GetString()!,
                frame.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object ? args.Clone() : default)
            : null;
}

/// <summary>Host tools over RPC (host-tools.ts): registration, results; omp sends calls and cancellations as frames.</summary>
public static class HostToolCommands
{
    /// <summary><c>set_host_tools</c>: replaces the tools this client runs for the agent. Returns the names omp took.</summary>
    public static async Task<IReadOnlyList<string>> SetHostToolsAsync(this RpcConnection connection, IReadOnlyList<HostToolDefinition> tools,
        CancellationToken ct = default)
    {
        var response = await connection.RequestAsync("set_host_tools", writer =>
        {
            writer.WriteStartArray("tools");
            foreach (var tool in tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("label", tool.Label);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("parameters");
                using (var schema = JsonDocument.Parse(tool.ParametersJson)) schema.RootElement.WriteTo(writer);
                writer.WriteString("loadMode", tool.LoadMode);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, ct: ct).ConfigureAwait(false);
        if (!response.Success) throw new RpcCommandException("set_host_tools", response.Error ?? "set_host_tools failed", response.Code);
        if (response.Data is not { ValueKind: JsonValueKind.Object } data || !data.TryGetProperty("toolNames", out var names) || names.ValueKind != JsonValueKind.Array)
            return [];
        return [.. names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!)];
    }

    /// <summary><c>host_tool_result</c>: the text the tool produced (or why it failed, with <paramref name="isError"/>).</summary>
    public static Task SendHostToolResultAsync(this RpcConnection connection, string id, string text, bool isError, CancellationToken ct = default) =>
        connection.SendAsync("host_tool_result", id, writer =>
        {
            writer.WriteStartObject("result");
            writer.WriteStartArray("content");
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            if (isError) writer.WriteBoolean("isError", true);
        }, ct);
}
