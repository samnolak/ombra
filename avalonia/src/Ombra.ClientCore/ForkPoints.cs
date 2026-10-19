using System.Text.Json;

namespace Ombra.ClientCore;

/// <summary>
/// Finds the session entry of a message in the conversation, for omp's <c>fork {entryId}</c>. Transcript rows carry no
/// entry ids (<c>get_messages_page</c> pages agent messages, not entries), so the row is matched against the message
/// entries on the session's current path (<c>get_entries</c>: every entry, all branches, and the leaf): same role and
/// text, the message's own <c>timestamp</c> (omp stores the message object as it streamed it) when the row has it, else
/// the same place counted from the end, as rewind does with <c>get_branch_messages</c>.
/// </summary>
public static class ForkPoints
{
    /// <summary>The <c>message</c> entries from the root to the leaf of a <c>get_entries</c> answer.</summary>
    public static IReadOnlyList<(string Id, JsonElement Message)> ActivePath(JsonElement entriesData)
    {
        if (entriesData.ValueKind != JsonValueKind.Object || !entriesData.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return [];
        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
            if (ConversationState.Str(entry, "id") is { } id) byId[id] = entry;
        var path = new List<(string, JsonElement)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var id = ConversationState.Str(entriesData, "leafId"); id is not null && seen.Add(id) && byId.TryGetValue(id, out var entry); id = ConversationState.Str(entry, "parentId"))
            if (ConversationState.Str(entry, "type") == "message" && entry.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                path.Add((id, message));
        path.Reverse();
        return path;
    }

    /// <summary>
    /// The entry id of the <paramref name="role"/> message with <paramref name="text"/>: the one written at
    /// <paramref name="at"/> when one was, else the <paramref name="occurrenceFromEnd"/>-th such message from the end
    /// (0: the last). Null when the path has no such message.
    /// </summary>
    public static string? Find(IReadOnlyList<(string Id, JsonElement Message)> path, string role, string text, DateTimeOffset? at, int occurrenceFromEnd)
    {
        var same = path.Where(p => ConversationState.Str(p.Message, "role") == role && SameText(ConversationState.ContentText(p.Message, "text"), text)).ToList();
        if (at is { } written)
        {
            var ms = written.ToUnixTimeMilliseconds();
            for (var i = same.Count - 1; i >= 0; i--)
                if (same[i].Message.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var v) && v == ms)
                    return same[i].Id;
        }
        return occurrenceFromEnd >= 0 && occurrenceFromEnd < same.Count ? same[^(occurrenceFromEnd + 1)].Id : null;
    }

    /// <summary>What a transcript row is matched by: its role, text, time and place among the rows like it, counted from
    /// the end. Null for a row that is not a finished message of yours or of the agent.</summary>
    public static (string Role, string Text, DateTimeOffset? At, int OccurrenceFromEnd)? Target(IReadOnlyList<TranscriptItem> items, long key)
    {
        var index = -1;
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i].Key == key) { index = i; break; }
        if (index < 0) return null;
        var (role, text, at) = items[index] switch
        {
            AssistantItem { Streaming: false } a => ("assistant", a.Text, a.At),
            UserItem { Confirmed: true } u => ("user", u.Text, u.At),
            _ => ((string?)null, "", (DateTimeOffset?)null),
        };
        if (role is null) return null;
        var occurrence = 0;
        for (var i = items.Count - 1; i > index; i--)
            if (items[i] switch
                {
                    AssistantItem { Streaming: false } a => role == "assistant" && SameText(a.Text, text),
                    UserItem { Confirmed: true } u => role == "user" && SameText(u.Text, text),
                    _ => false,
                }) occurrence++;
        return (role, text, at, occurrence);
    }

    private static bool SameText(string a, string b) =>
        string.Equals(a.Trim().ReplaceLineEndings("\n"), b.Trim().ReplaceLineEndings("\n"), StringComparison.Ordinal);
}
