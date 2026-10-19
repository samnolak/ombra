using System.Text.Json;

namespace Ombra.Rpc;

/// <summary>A bounded, chronological slice of one immutable runtime snapshot. Cursor and snapshot identity are opaque.</summary>
public sealed record OmpMessagesPage(JsonElement Messages, string? NextCursor, int TotalMessages, int StartOffset,
    int EndOffset, string SessionId, string SnapshotId, RpcTimings Timings)
{
    public const string IncompatibleRuntime = "This omp runtime cannot load recent history safely. Install the current Ombra managed runtime, or configure an omp build supporting Ombra backward message pages. Full history was not downloaded.";

    public static OmpMessagesPage Parse(RpcResponse response)
    {
        if (response.Data is not { ValueKind: JsonValueKind.Object } data ||
            !data.TryGetProperty("direction", out var direction) || direction.ValueKind != JsonValueKind.String || !direction.ValueEquals("backward"))
            throw new NotSupportedException(IncompatibleRuntime);
        if (!data.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array ||
            !data.TryGetProperty("totalMessages", out var totalJson) || totalJson.ValueKind != JsonValueKind.Number || !totalJson.TryGetInt32(out var total) ||
            !data.TryGetProperty("startOffset", out var startJson) || startJson.ValueKind != JsonValueKind.Number || !startJson.TryGetInt32(out var start) ||
            !data.TryGetProperty("endOffset", out var endJson) || endJson.ValueKind != JsonValueKind.Number || !endJson.TryGetInt32(out var end) ||
            !data.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(session.GetString()) ||
            !data.TryGetProperty("snapshotId", out var snapshot) || snapshot.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(snapshot.GetString()) ||
            start < 0 || end < start || total < end || end - start != messages.GetArrayLength())
            throw new InvalidDataException("omp returned an invalid backward message page; the displayed history was kept.");
        var cursor = data.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        if ((start > 0) != !string.IsNullOrEmpty(cursor) || (start > 0 && start == end))
            throw new InvalidDataException("omp returned a non-advancing message page; the displayed history was kept.");
        if (cursor is not null)
        {
            if (cursor.Length > 2048) throw new InvalidDataException("omp returned an oversized history cursor; the displayed history was kept.");
            foreach (var character in cursor)
                if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                    throw new InvalidDataException("omp returned an invalid history cursor; the displayed history was kept.");
        }
        foreach (var message in messages.EnumerateArray())
            if (message.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("omp returned an invalid history message; the displayed history was kept.");
        return new OmpMessagesPage(messages, cursor, total, start, end, session.GetString()!, snapshot.GetString()!, response.Timings);
    }
}
