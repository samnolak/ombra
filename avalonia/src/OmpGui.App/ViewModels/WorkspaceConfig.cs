using System.Text.Json;
using System.Text.Json.Nodes;
using OmpGui.ClientCore;

namespace OmpGui.App.ViewModels;

/// <summary>
/// omp's settings as its CLI reports them (<c>omp config list --json</c>: every key with its effective value, project
/// overrides included) and changes through <c>omp config set &lt;key&gt; &lt;value&gt; --json</c>, which writes omp's global
/// config.yml (of the session's profile). omp reads config.yml only when it starts.
/// </summary>
public sealed class WorkspaceConfig
{
    private readonly Dictionary<string, JsonElement> _values;

    private WorkspaceConfig(Dictionary<string, JsonElement> values) => _values = values;

    public static async Task<(WorkspaceConfig? Config, string? Error)> LoadAsync(MainViewModel main, CancellationToken ct)
    {
        var r = await main.RunOmpCliAsync(["config", "list", "--json"], TimeSpan.FromSeconds(45), ct);
        if (!r.Ok) return (null, "omp's settings could not be read: " + CliError(r));
        try
        {
            using var doc = JsonDocument.Parse(r.Stdout);
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("value", out var v)) values[p.Name] = v.Clone();
            return (new WorkspaceConfig(values), null);
        }
        catch (JsonException e)
        {
            return (null, "omp's settings could not be read: " + e.Message);
        }
    }

    public bool? Bool(string key) => _values.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public string? String(string key) => _values.TryGetValue(key, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    } : null;

    public JsonObject? Record(string key) => _values.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Object ? JsonNode.Parse(v.GetRawText()) as JsonObject : null;

    /// <summary>Sets one key. <c>Value</c> is what omp reads back afterwards: a project's own settings may still override it.</summary>
    public static async Task<(bool Ok, JsonElement? Value, string? Error)> SetAsync(MainViewModel main, string key, string value, CancellationToken ct)
    {
        var r = await main.RunOmpCliAsync(["config", "set", key, value, "--json"], TimeSpan.FromSeconds(45), ct);
        if (!r.Ok) return (false, null, CliError(r));
        try
        {
            using var doc = JsonDocument.Parse(r.Stdout);
            return (true, doc.RootElement.TryGetProperty("value", out var v) ? v.Clone() : null, null);
        }
        catch (JsonException)
        {
            return (true, null, null); // saved (exit 0); only the echo was unreadable
        }
    }

    /// <summary>omp's own message ("Error: Invalid number: abc" → "Invalid number: abc").</summary>
    public static string CliError(OmpCliResult r)
    {
        var lines = (r.Stderr.Trim().Length > 0 ? r.Stderr : r.Stdout).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var line = lines.Count == 0 ? $"omp exited with code {r.ExitCode}" : lines[^1];
        return line.StartsWith("Error: ", StringComparison.Ordinal) ? line[7..] : line;
    }

    /// <summary>A saved value that reads back differently: this project's .omp settings set it.</summary>
    public static string OverriddenNote(string what) =>
        $"Saved, but this project's own omp settings (.omp/config.yml in the project) set {what} differently, and they win.";
}
