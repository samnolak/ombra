using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmpGui.ClientCore;

/// <summary>An installed plugin as <c>omp plugin list --json</c> reports it (npm package or marketplace plugin).</summary>
/// <param name="Id">npm package name, or <c>name@marketplace</c>.</param>
/// <param name="Marketplace">null for npm plugins.</param>
/// <param name="Scope">user or project (marketplace plugins); null for npm plugins.</param>
/// <param name="Shadowed">A user install hidden by a project install of the same plugin.</param>
public sealed record InstalledPluginInfo(string Id, string Name, string? Marketplace, string Version, string? Description, bool Enabled, string? Scope, bool Shadowed);

/// <summary>A configured marketplace (<c>omp plugin marketplace list</c>).</summary>
public sealed record MarketplaceInfo(string Name, string Source);

/// <summary>A plugin a marketplace offers (<c>omp plugin discover &lt;marketplace&gt;</c>).</summary>
public sealed record MarketplacePluginInfo(string Name, string? Version, string? Description);

/// <summary>
/// omp's plugin command line as it prints (omp 18.2.0, cli/plugin-cli.ts) and the settings the Plugins and skills page
/// reads from <c>omp config list --json</c>.
/// </summary>
public static class OmpPlugins
{
    /// <summary><c>{ npm: InstalledPlugin[], marketplace: InstalledPluginSummary[] }</c>; null when it is not that JSON.</summary>
    public static IReadOnlyList<InstalledPluginInfo>? ParseList(string stdout)
    {
        if (ParseObject(stdout) is not { } root) return null;
        if (root["npm"] is not JsonArray npm || root["marketplace"] is not JsonArray market) return null;
        var list = new List<InstalledPluginInfo>();
        foreach (var p in npm.OfType<JsonObject>())
        {
            var name = Str(p, "name");
            if (name is null) continue;
            var manifest = p["manifest"] as JsonObject;
            list.Add(new(name, name, null, Str(p, "version") ?? Str(manifest, "version") ?? "", Str(manifest, "description"),
                p["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var on) || on, null, false));
        }
        foreach (var p in market.OfType<JsonObject>())
        {
            var id = Str(p, "id");
            if (id is null) continue;
            var at = id.LastIndexOf('@');
            var entry = (p["entries"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            // omp reads the first entry for version and state (/plugins list); "enabled" absent means enabled.
            var enabled = entry?["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var on) || on;
            list.Add(new(id, at > 0 ? id[..at] : id, at > 0 ? id[(at + 1)..] : null, Str(entry, "version") ?? "", null, enabled,
                Str(p, "scope"), Str(p, "shadowedBy") is not null));
        }
        return list;
    }

    /// <summary>"Configured Marketplaces:" then "  name  source" lines, or "No marketplaces configured"; null otherwise.</summary>
    public static IReadOnlyList<MarketplaceInfo>? ParseMarketplaces(string stdout)
    {
        var text = stdout.Trim();
        if (text.StartsWith("No marketplaces configured", StringComparison.Ordinal)) return [];
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("Configured Marketplaces", StringComparison.Ordinal)
                             && !lines[0].StartsWith("Marketplaces:", StringComparison.Ordinal)) return null;
        var list = new List<MarketplaceInfo>();
        foreach (var line in lines.Skip(1))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            var gap = t.IndexOf("  ", StringComparison.Ordinal);
            list.Add(gap > 0 ? new(t[..gap], t[(gap + 2)..].Trim()) : new(t, ""));
        }
        return list;
    }

    /// <summary>
    /// "Available Plugins (mkt):" then "  name@version" with an indented description line under each; "No plugins
    /// found…" is an empty list; null when omp printed something else. Also reads omp's slash-command form
    /// ("Available plugins:", "  - name@version").
    /// </summary>
    public static IReadOnlyList<MarketplacePluginInfo>? ParseDiscover(string stdout)
    {
        var text = stdout.Trim();
        if (text.StartsWith("No plugins", StringComparison.Ordinal)) return [];
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("Available", StringComparison.OrdinalIgnoreCase)) return null;
        var list = new List<MarketplacePluginInfo>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Trim().Length == 0) continue;
            var indent = line.Length - line.TrimStart().Length;
            var t = line.Trim();
            if (indent <= 2 || t.StartsWith("- ", StringComparison.Ordinal))
            {
                if (t.StartsWith("- ", StringComparison.Ordinal)) t = t[2..];
                var at = t.IndexOf('@', StringComparison.Ordinal);
                list.Add(at > 0 ? new(t[..at], t[(at + 1)..], null) : new(t, null, null));
            }
            else if (list.Count > 0)
            {
                var last = list[^1];
                list[^1] = last with { Description = last.Description is null ? t : last.Description + " " + t };
            }
        }
        return list;
    }

    /// <summary>The values of <c>omp config list --json</c> (key → value); null when it is not that JSON.</summary>
    public static IReadOnlyDictionary<string, JsonNode?>? ParseConfigList(string stdout)
    {
        if (ParseObject(stdout) is not { } root) return null;
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (k, v) in root)
            if (v is JsonObject o && o.ContainsKey("type")) values[k] = o["value"]?.DeepClone();
        return values;
    }

    /// <summary>The value of <c>omp config get &lt;key&gt; --json</c> (<c>{key, value, type, description}</c>).</summary>
    public static JsonNode? ParseConfigGet(string stdout, out bool ok)
    {
        var root = ParseObject(stdout);
        ok = root is not null && root.ContainsKey("value");
        return root?["value"]?.DeepClone();
    }

    public static bool Bool(IReadOnlyDictionary<string, JsonNode?>? config, string key, bool fallback) =>
        config?.GetValueOrDefault(key) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public static string? String(IReadOnlyDictionary<string, JsonNode?>? config, string key) =>
        config?.GetValueOrDefault(key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray a ? [.. a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>()] : [];

    /// <summary>The value for <c>omp config set &lt;key&gt;</c> of an array setting (omp parses it as JSON).</summary>
    public static string ArrayValue(IEnumerable<string> items) => JsonSerializer.Serialize(items.ToArray());

    /// <summary>Skill names switched off through omp's <c>disabledExtensions</c> (ids <c>skill:&lt;name&gt;</c>).</summary>
    public static IReadOnlyList<string> DisabledSkills(IEnumerable<string> disabledExtensions) =>
        [.. disabledExtensions.Where(id => id.StartsWith("skill:", StringComparison.Ordinal)).Select(id => id[6..]).Where(n => n.Length > 0)];

    /// <summary>omp's CLI marks with ✔ / ✘ and prints "Error: " inside its messages; the words are what matter.</summary>
    public static string Clean(string text) =>
        string.Join("\n", text.Replace("\r", "", StringComparison.Ordinal).Split('\n').Select(l => l.TrimStart('✔', '✘', ' ').TrimEnd()).Where(l => l.Length > 0));

    private static JsonObject? ParseObject(string stdout)
    {
        var start = stdout.IndexOf('{', StringComparison.Ordinal);
        var end = stdout.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonNode.Parse(stdout[start..(end + 1)]) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
