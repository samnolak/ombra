using System.Text.Json;

namespace Ombra.ClientCore;

/// <summary>
/// RPC mode stamps <c>message_start</c>, every <c>message_update</c> and <c>message_end</c> of one message with the same
/// <c>messageId</c> (rpc-types.ts <c>RpcMessageEventFrame</c>). Assistant rows are found by it, so a late delta or end
/// still lands on its own row after another message started. Frames without it (an older omp) use the single
/// streaming-row heuristics.
/// </summary>
public sealed partial class ConversationState
{
    private readonly Dictionary<string, Row> _messageRows = new(StringComparer.Ordinal);

    private static string? MessageId(JsonElement j) =>
        j.TryGetProperty("messageId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } s ? s : null;

    /// <summary>The row a stamped frame belongs to; null when the frame carries no known id.</summary>
    private Row? RowForMessage(JsonElement j) =>
        MessageId(j) is { } id && _messageRows.TryGetValue(id, out var row) ? row : null;

    /// <summary>Whether the streaming row was started by a stamped frame: it then ends by its own id, not by the next start.</summary>
    private bool StreamingRowHasId => _streamingAssistant is { } row && _messageRows.ContainsValue(row);

    /// <summary>Restores the ids of rows a transcript replacement kept.</summary>
    private void RetainMessageRows(KeyValuePair<string, Row>[] previous, Row[] kept)
    {
        foreach (var (id, row) in previous)
            if (Array.IndexOf(kept, row) >= 0) _messageRows[id] = row;
    }
}
