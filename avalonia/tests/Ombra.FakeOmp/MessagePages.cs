using System.Text;
using System.Text.Json;

namespace Ombra.FakeOmp;

internal sealed class MessagePages
{
    private readonly Dictionary<string, (string Session, int Count, int Offset)> _cursors = [];

    public object Page(IReadOnlyList<object> messages, string sessionId, string? cursor, int limit)
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        var end = messages.Count;
        if (cursor is not null)
        {
            if (!_cursors.TryGetValue(cursor, out var previous) || previous.Session != sessionId || previous.Count != messages.Count)
                throw new InvalidOperationException("stale_cursor");
            end = previous.Offset;
        }
        var start = end;
        var bytes = 2;
        while (start > 0 && end - start < limit)
        {
            var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(messages[start - 1])) + (start == end ? 0 : 1);
            if (start < end && bytes + size > 768 * 1024) break;
            bytes += size;
            start--;
        }
        string? next = null;
        if (start > 0)
        {
            next = Guid.NewGuid().ToString("N");
            _cursors[next] = (sessionId, messages.Count, start);
        }
        return new
        {
            messages = messages.Skip(start).Take(end - start).ToArray(), nextCursor = next,
            totalMessages = messages.Count, direction = "backward", startOffset = start, endOffset = end,
            sessionId, snapshotId = sessionId + ":" + messages.Count,
        };
    }
}
