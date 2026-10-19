using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Ombra.ClientCore;

namespace Ombra.App.ViewModels;

/// <summary>Which text of a row a find match is in: the view opens what hides it (a tool's output, the thinking).</summary>
public enum FindField { Text, Thinking, Error, Args, Output, Diff, Label }

/// <summary>
/// One match of Find in chat: its row, the text of the row it is in, which occurrence of the query it is in that row
/// (the view highlights the same occurrence among the row's shown text) and where it starts in the field (after NFC
/// normalization).
/// </summary>
public sealed record FindMatch(RowViewModel Row, FindField Field, int Occurrence, int Start, int Length);

/// <summary>
/// Find in chat over the conversation's rows themselves (their <see cref="TranscriptItem"/>s: what was written, a tool's
/// arguments and output), not over the controls on screen: the transcript is virtualized, so most rows have none.
/// Case-insensitive without a culture (ordinal case folding: Turkish i is not special), both sides NFC-normalized so a
/// decomposed "é" matches a composed one. A row's matches are kept until its item changes: while a reply streams only
/// its own row is searched again.
/// </summary>
internal static class TranscriptFind
{
    private sealed record Hits(TranscriptItem Model, string Query, FindMatch[] Matches);

    private static readonly ConditionalWeakTable<RowViewModel, Hits> Cache = new();

    /// <summary>The query as it is compared (NFC); empty when there is nothing to find.</summary>
    public static string Normalize(string? query) => string.IsNullOrEmpty(query) ? "" : Nfc(query);

    /// <summary>Every match of <paramref name="query"/> (normalized) in the rows, in their order. The rows a turn's line
    /// or a step group's line stand for are searched in their own place; those lines have no text of their own.</summary>
    public static List<FindMatch> Search(IReadOnlyList<RowViewModel> rows, string query)
    {
        var all = new List<FindMatch>();
        if (query.Length == 0) return all;
        foreach (var row in rows)
        {
            if (row is StepGroupRowViewModel or TurnFoldRowViewModel) continue;
            if (!Cache.TryGetValue(row, out var hits) || !ReferenceEquals(hits.Model, row.Model) || !string.Equals(hits.Query, query, StringComparison.Ordinal))
            {
                hits = new Hits(row.Model, query, Find(row, query));
                Cache.AddOrUpdate(row, hits);
            }
            all.AddRange(hits.Matches);
        }
        return all;
    }

    /// <summary>The non-overlapping occurrences of a normalized query in a text, as (start, length) in the normalized text.</summary>
    public static IEnumerable<int> Occurrences(string text, string query)
    {
        if (query.Length == 0 || text.Length < query.Length) yield break;
        var at = 0;
        while (at <= text.Length - query.Length && text.IndexOf(query, at, StringComparison.OrdinalIgnoreCase) is var i and >= 0)
        {
            yield return i;
            at = i + query.Length;
        }
    }

    public static string Nfc(string text) => text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);

    private static FindMatch[] Find(RowViewModel row, string query)
    {
        List<FindMatch>? found = null;
        var occurrence = 0;
        foreach (var (field, text) in Fields(row))
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var start in Occurrences(Nfc(text), query))
                (found ??= []).Add(new FindMatch(row, field, occurrence++, start, query.Length));
        }
        return found is null ? [] : [.. found];
    }

    /// <summary>The searchable texts of a row, in the order the row shows them.</summary>
    private static IEnumerable<(FindField, string?)> Fields(RowViewModel row)
    {
        switch (row.Model)
        {
            case UserItem u:
                yield return (FindField.Text, u.Text);
                break;
            case AssistantItem a:
                yield return (FindField.Thinking, a.Thinking);
                yield return (FindField.Text, a.Text);
                // A stop is the user's own, not an error the row shows
                if (a.StopReason != "aborted") yield return (FindField.Error, a.Error);
                break;
            case ToolItem t:
                yield return (FindField.Args, ArgValues(t.Args));
                // As the row shows it: control sequences and unsafe characters taken out
                yield return (FindField.Output, row is ToolRowViewModel tool ? tool.Output : t.Output);
                yield return (FindField.Diff, t.Diff);
                break;
            case NoticeItem n:
                yield return (FindField.Text, n.Text);
                break;
            case CommandOutputItem c:
                yield return (FindField.Label, c.Label);
                yield return (FindField.Text, row is CommandOutputRowViewModel output ? output.Text : c.Text);
                break;
            case AgentMessageItem m:
                yield return (FindField.Text, m.Text);
                break;
        }
    }

    /// <summary>A tool call's arguments as the words in them (its string values, one per line), not the JSON around
    /// them: "command" or a quote escaped as \" is not something the reader sees. Arguments that are not JSON (cut off
    /// while streaming) are searched as they are.</summary>
    private static string ArgValues(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return "";
        try
        {
            using var doc = JsonDocument.Parse(args);
            var sb = new StringBuilder();
            Collect(doc.RootElement, sb);
            return sb.ToString();
        }
        catch (JsonException)
        {
            return args;
        }

        static void Collect(JsonElement e, StringBuilder sb)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String:
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(e.GetString());
                    break;
                case JsonValueKind.Number:
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(e.GetRawText());
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Collect(item, sb);
                    break;
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject()) Collect(p.Value, sb);
                    break;
            }
        }
    }
}
