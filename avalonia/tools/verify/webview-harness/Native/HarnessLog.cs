using System.Diagnostics;
using System.Text.Json.Nodes;

namespace OmpGui.WebViewHarness.Native;

/// <summary>Every delegate callback the harness saw, in order, with milliseconds since start (part of the JSON result).</summary>
internal static class HarnessLog
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly List<JsonObject> Entries = [];
    private static readonly Lock Gate = new();

    public static void Add(string source, string ev, JsonObject? data = null)
    {
        var entry = new JsonObject { ["t"] = Clock.ElapsedMilliseconds, ["view"] = source, ["event"] = ev };
        if (data is not null)
            foreach (var (k, v) in data.ToList())
            {
                data.Remove(k);
                entry[k] = v;
            }
        lock (Gate) Entries.Add(entry);
        if (Environment.GetEnvironmentVariable("HARNESS_VERBOSE") == "1")
            Console.Error.WriteLine(entry.ToJsonString());
    }

    public static IReadOnlyList<JsonObject> Snapshot()
    {
        lock (Gate) return Entries.ToList();
    }

    public static int Count(string ev)
    {
        lock (Gate) return Entries.Count(e => (string?)e["event"] == ev);
    }

    public static JsonArray ToJson()
    {
        lock (Gate) return new JsonArray(Entries.Select(e => (JsonNode)e.DeepClone()).ToArray());
    }
}
