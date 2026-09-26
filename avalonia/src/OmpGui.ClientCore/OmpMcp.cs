using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmpGui.ClientCore;

/// <summary>An MCP server as omp's <c>/mcp list</c> prints it (omp 18.2.0, slash-commands/helpers/mcp.ts handleListCommand).</summary>
/// <param name="Transport">stdio, http or sse.</param>
/// <param name="Location">The command (stdio) or the URL without its query (http/sse); "(unknown)" or "(hidden)" as omp prints them.</param>
/// <param name="Scope">user (the profile's mcp.json) or project (&lt;cwd&gt;/.omp/mcp.json).</param>
public sealed record McpServerEntry(string Name, string Transport, bool Enabled, string Location, string Scope);

/// <summary>What <c>/mcp test &lt;name&gt;</c> printed.</summary>
public sealed record McpTestResult(bool Connected, int ToolCount, IReadOnlyList<string> Tools, string Message);

/// <summary>A resource or a prompt from <c>/mcp resources</c> / <c>/mcp prompts</c> ("server/uri", "server/name — description").</summary>
public sealed record McpItem(string Server, string Name, string? Description);

/// <summary>A Smithery registry result from <c>/mcp smithery-search</c> ("Display Name (qualified/name) — description").</summary>
public sealed record SmitheryResult(string DisplayName, string QualifiedName, string? Description);

/// <summary>
/// omp's <c>/mcp</c> builtin as it runs over RPC (omp 18.2.0, slash-commands/helpers/mcp.ts): the command lines it
/// takes and the text it prints. Every error comes back as ordinary output, so each parser also says when the text is
/// not the success shape (the caller shows omp's text as the error).
/// </summary>
public static partial class OmpMcp
{
    public const string NoServers = "No MCP servers configured.";

    [GeneratedRegex(@"^(?<name>\S+) \| (?<type>stdio|http|sse) \| (?<state>enabled|disabled) \| (?<loc>.*) \[(?<scope>user|project)\]$")]
    private static partial Regex ListLine();

    [GeneratedRegex(@"^Server ""(?<name>.+)"" connected \((?<n>\d+) tools?\)\.$")]
    private static partial Regex TestConnected();

    [GeneratedRegex(@"^Connection to ""(?<name>.+)"" failed: (?<why>.*)$", RegexOptions.Singleline)]
    private static partial Regex TestFailed();

    /// <summary>omp's rule for a server name (mcp/config-writer.ts validateServerName).</summary>
    [GeneratedRegex(@"^[a-zA-Z0-9_.:-]+$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^(?<display>.*) \((?<name>[^()\s]+)\)$")]
    private static partial Regex SmitheryHead();

    [GeneratedRegex("[^a-z_]+")]
    private static partial Regex NotToolNameChars();

    [GeneratedRegex("_+")]
    private static partial Regex Underscores();

    /// <summary>The servers <c>/mcp list</c> printed; null when the text is not a list (an error: show it).</summary>
    public static IReadOnlyList<McpServerEntry>? ParseList(string output)
    {
        var text = output.Trim();
        if (text == NoServers) return [];
        var list = new List<McpServerEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var m = ListLine().Match(line);
            if (!m.Success) return null;
            list.Add(new(m.Groups["name"].Value, m.Groups["type"].Value, m.Groups["state"].Value == "enabled",
                m.Groups["loc"].Value, m.Groups["scope"].Value));
        }
        return list.Count == 0 ? null : list;
    }

    /// <summary>
    /// <c>Server "x" connected (2 tools).</c> followed by "  - tool" lines, or <c>Connection to "x" failed: why</c>;
    /// anything else (not found, usage) is a failure whose message is omp's text.
    /// </summary>
    public static McpTestResult ParseTest(string output)
    {
        var text = output.Trim();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && TestConnected().Match(lines[0]) is { Success: true } ok)
        {
            var tools = lines.Skip(1).Select(l => l.Trim()).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..].Trim()).ToList();
            return new(true, int.Parse(ok.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture), tools, lines[0]);
        }
        if (TestFailed().Match(text) is { Success: true } failed) return new(false, 0, [], failed.Groups["why"].Value.Trim());
        return new(false, 0, [], text.Length == 0 ? "omp printed nothing" : text);
    }

    /// <summary>"server/uri" lines (resources) or "server/name — description" lines (prompts); null for omp's
    /// "nothing" messages, which <paramref name="none"/> receives.</summary>
    public static IReadOnlyList<McpItem> ParseItems(string output, out string? none)
    {
        none = null;
        var text = output.Trim();
        if (text.Length == 0 || text == NoServers || text.StartsWith("No resources available", StringComparison.Ordinal)
            || text.StartsWith("No prompts available", StringComparison.Ordinal))
        {
            none = text.Length == 0 ? "omp printed nothing" : text;
            return [];
        }
        var items = new List<McpItem>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var slash = line.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0) continue;
            var server = line[..slash];
            var rest = line[(slash + 1)..];
            var dash = rest.IndexOf(" — ", StringComparison.Ordinal);
            items.Add(dash >= 0 ? new(server, rest[..dash], rest[(dash + 3)..]) : new(server, rest, null));
        }
        return items;
    }

    /// <summary>Smithery results, or null when omp printed a message instead (no results, sign-in needed, an error).</summary>
    public static IReadOnlyList<SmitheryResult>? ParseSmithery(string output)
    {
        var results = new List<SmitheryResult>();
        foreach (var raw in output.Trim().Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var dash = line.IndexOf(" — ", StringComparison.Ordinal);
            var head = dash >= 0 ? line[..dash] : line;
            if (SmitheryHead().Match(head) is not { Success: true } m) return null;
            results.Add(new(m.Groups["display"].Value, m.Groups["name"].Value, dash >= 0 ? line[(dash + 3)..] : null));
        }
        return results.Count == 0 ? null : results;
    }

    /// <summary>True when omp said Smithery needs its API key (sign-in happens in omp's terminal UI).</summary>
    public static bool IsSmitherySignInNeeded(string output) => output.Contains("Smithery authentication required", StringComparison.Ordinal);

    /// <summary>omp's own server-name check; null when the name is fine.</summary>
    public static string? ValidateName(string name) =>
        name.Length == 0 ? "Give the connector a name."
        : name.Length > 100 ? "The name is too long (100 characters at most)."
        : !ValidName().IsMatch(name) ? "Use letters, numbers, dash, underscore, dot or colon (no spaces)."
        : null;

    /// <summary>
    /// <c>/mcp add</c> in omp's grammar: <c>/mcp add &lt;name&gt; [--scope project|user] [--url &lt;url&gt; --transport
    /// http|sse] [--token &lt;token&gt;] [-- &lt;command...&gt;]</c>. Arguments are quoted for omp's tokenizer
    /// (utils/command-args.ts: quotes group, no escapes).
    /// </summary>
    public static string AddCommand(string name, string scope, string transport, string? command, IReadOnlyList<string> args, string? url, string? token)
    {
        var sb = new StringBuilder("/mcp add ").Append(Quote(name)).Append(" --scope ").Append(scope);
        if (transport == "stdio")
        {
            sb.Append(" --");
            foreach (var a in new[] { command ?? "" }.Concat(args)) sb.Append(' ').Append(Quote(a));
        }
        else
        {
            sb.Append(" --url ").Append(Quote(url ?? "")).Append(" --transport ").Append(transport);
            if (!string.IsNullOrEmpty(token)) sb.Append(" --token ").Append(Quote(token));
        }
        return sb.ToString();
    }

    public static string RemoveCommand(string name, string scope) => $"/mcp remove {Quote(name)} --scope {scope}";

    /// <summary>
    /// One argument for omp's slash-command tokenizer: plain when it has no blank or quote; otherwise in double
    /// quotes, with any double quote itself inside single quotes (adjacent quoted runs join into one argument).
    /// </summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"' or '\'')) return arg;
        var sb = new StringBuilder();
        foreach (var part in arg.Split('"'))
        {
            if (sb.Length > 0) sb.Append("'\"'");
            if (part.Length > 0 || arg.Length == 0) sb.Append('"').Append(part).Append('"');
        }
        return sb.ToString();
    }

    /// <summary>The start of omp's tool names for a server: <c>mcp__&lt;server&gt;_</c> (mcp/tool-bridge.ts createMCPToolName).</summary>
    public static string ToolPrefix(string server)
    {
        var s = Underscores().Replace(NotToolNameChars().Replace(server.ToLowerInvariant(), "_"), "_").Trim('_');
        return "mcp__" + (s.Length > 0 ? s : "server") + "_";
    }

    /// <summary>The MCP tools <c>/tools</c> lists ("* name", "- name", "~ xd://name").</summary>
    public static IReadOnlyList<string> McpToolsIn(string toolsOutput)
    {
        var tools = new List<string>();
        foreach (var raw in toolsOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 3 || line[1] != ' ' || line[0] is not ('*' or '-' or '~')) continue;
            var name = line[2..].Trim();
            if (name.StartsWith("xd://", StringComparison.Ordinal)) name = name[5..];
            if (name.StartsWith("mcp__", StringComparison.Ordinal) && !tools.Contains(name)) tools.Add(name);
        }
        return tools;
    }

    /// <summary>
    /// Which server each MCP tool of the session belongs to (the longest matching <see cref="ToolPrefix"/>); tools no
    /// listed server claims come from servers configured elsewhere (Claude Code, Cursor, a project .mcp.json…).
    /// </summary>
    public static (Dictionary<string, List<string>> ByServer, List<string> Other) AttributeTools(IEnumerable<string> servers, IReadOnlyList<string> tools)
    {
        var prefixes = servers.Distinct(StringComparer.Ordinal).Select(s => (server: s, prefix: ToolPrefix(s))).OrderByDescending(x => x.prefix.Length).ToList();
        var by = prefixes.ToDictionary(x => x.server, _ => new List<string>(), StringComparer.Ordinal);
        var other = new List<string>();
        foreach (var t in tools)
        {
            var owner = prefixes.FirstOrDefault(x => t.StartsWith(x.prefix, StringComparison.Ordinal));
            if (owner.server is null) other.Add(t);
            else by[owner.server].Add(t);
        }
        return (by, other);
    }

    /// <summary>omp leaves Exa servers out and uses its own Exa search instead (mcp/config.ts isExaMCPServer).</summary>
    public static bool IsExa(string name, string location) =>
        name.Equals("exa", StringComparison.OrdinalIgnoreCase) || location.Contains("mcp.exa.ai", StringComparison.OrdinalIgnoreCase);

    /// <summary>A success line of <c>/mcp add|enable|disable|remove</c> (anything else is omp's error text).</summary>
    public static bool IsAdded(string output, string name) => output.Trim().StartsWith($"Added MCP server \"{name}\"", StringComparison.Ordinal);
    public static bool IsToggled(string output, string name, bool enabled) =>
        output.Trim().StartsWith($"Server \"{name}\" {(enabled ? "enabled" : "disabled")}", StringComparison.Ordinal);
    public static bool IsRemoved(string output, string name) => output.Trim().StartsWith($"Removed server \"{name}\"", StringComparison.Ordinal);
}

/// <summary>
/// One server entry of an omp MCP config file (<c>mcp.json</c>), edited in place: only the fields the form changes are
/// touched, every other field (timeout, auth, oauth, enabled, envPolicy, fields newer omp versions add) and every other
/// server stay as they were. The file is written as omp writes it (mcp/config-writer.ts: <c>$schema</c> first,
/// two-space JSON, written to a temp file then renamed, mode 600).
/// </summary>
public static class McpConfigFile
{
    public const string SchemaUrl = "https://raw.githubusercontent.com/can1357/oh-my-pi/main/packages/coding-agent/src/config/mcp-schema.json";

    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The server's entry, or null when the file or the entry does not exist.</summary>
    public static JsonObject? ReadServer(string path, string name)
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root
                   && root["mcpServers"] is JsonObject servers && servers[name] is JsonObject entry
                ? entry
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Every server of a file, in its order (empty when the file is missing or not JSON).</summary>
    public static IReadOnlyList<(string Name, JsonObject Entry)> ReadServers(string path)
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && root["mcpServers"] is JsonObject servers
                ? [.. servers.Where(kv => kv.Value is JsonObject).Select(kv => (kv.Key, (JsonObject)kv.Value!))]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>A top-level list of names (<c>disabledServers</c>, <c>enabledServers</c>).</summary>
    public static IReadOnlyList<string> ReadNames(string path, string field)
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && root[field] is JsonArray names
                ? [.. names.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>()]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A server of another tool's file as omp reads it (discovery/mcp-json.ts): the transport from <c>type</c>, else
    /// stdio for a command and http for a URL; the location as <c>/mcp list</c> would print it (a URL without query).
    /// </summary>
    public static McpServerEntry EntryOf(string name, JsonObject entry, string scope, IReadOnlyCollection<string> disabled)
    {
        var command = GetString(entry, "command");
        var url = GetString(entry, "url");
        var declared = GetString(entry, "type");
        var type = declared is "stdio" or "http" or "sse" ? declared : command is not null ? "stdio" : url is not null ? "http" : "stdio";
        var location = type == "stdio" ? command ?? "(unknown)"
            : url is null ? "(unknown)"
            : Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) + (u.AbsolutePath != "/" ? u.AbsolutePath : "") : "(hidden)";
        var enabled = !(entry["enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) && !on) && !disabled.Contains(name);
        return new(name, type, enabled, location, scope);
    }

    /// <summary>Changes one existing server's entry through <paramref name="edit"/> and writes the file back.</summary>
    public static void UpdateServer(string path, string name, Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException($"{path} is not a JSON object");
        if (root["mcpServers"] is not JsonObject servers || servers[name] is not JsonObject entry)
            throw new InvalidDataException($"Server \"{name}\" not found in {path}");
        edit(entry);
        var ordered = new JsonObject { ["$schema"] = root["$schema"]?.DeepClone() ?? SchemaUrl };
        foreach (var (k, v) in root)
            if (k != "$schema") ordered[k] = v?.DeepClone();
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, ordered.ToJsonString(Write));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>Sets a string map (<c>env</c>, <c>headers</c>); an empty map removes the field.</summary>
    public static void SetMap(JsonObject entry, string field, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        if (values.Count == 0)
        {
            entry.Remove(field);
            return;
        }
        var map = new JsonObject();
        foreach (var (k, v) in values) map[k] = v;
        entry[field] = map;
    }

    /// <summary>A string map of the entry (<c>env</c>, <c>headers</c>) as lines of the form "KEY=value" / "Name: value".</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> GetMap(JsonObject? entry, string field) =>
        entry?[field] is JsonObject map
            ? [.. map.Where(kv => kv.Value is JsonValue).Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value!.ToString()))]
            : [];

    public static IReadOnlyList<string> GetArgs(JsonObject? entry) =>
        entry?["args"] is JsonArray a ? [.. a.Where(x => x is JsonValue).Select(x => x!.ToString())] : [];

    public static string? GetString(JsonObject? entry, string field) =>
        entry?[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The server signs in with OAuth (omp's <c>auth.type</c> "oauth" or an <c>oauth</c> block).</summary>
    public static bool UsesOAuth(JsonObject? entry) =>
        entry?["oauth"] is JsonObject || (entry?["auth"] is JsonObject auth && GetString(auth, "type") == "oauth");
}
