using System.Globalization;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Ombra.ClientCore;

/// <summary>The wire protocols a custom provider can speak (omp's <c>api</c> values the providers page offers).</summary>
public static class ModelApis
{
    public const string ChatCompletions = "openai-completions";
    public const string Responses = "openai-responses";
    public const string AnthropicMessages = "anthropic-messages";

    public static readonly IReadOnlyList<string> All = [ChatCompletions, Responses, AnthropicMessages];
}

/// <summary>
/// One provider of omp's <c>models.yml</c> as the providers page shows and edits it: the fields of its form. omp reads
/// more (headers, compat, per-model limits…); an entry using any of them is listed with <see cref="NotEditable"/> set
/// and is never rewritten by the app, only removed on request.
/// </summary>
public sealed record ModelProviderEntry
{
    public required string Id { get; init; }
    public string BaseUrl { get; init; } = "";
    /// <summary>omp's <c>api</c> (<see cref="ModelApis"/>); null when the entry leaves it out.</summary>
    public string? Api { get; init; }
    /// <summary>omp resolves it at use: an environment variable of that name, else <c>!command</c>'s output, else the text itself.</summary>
    public string? ApiKey { get; init; }
    /// <summary><c>auth: none</c>: omp sends no key (local servers).</summary>
    public bool NoAuth { get; init; }
    /// <summary><c>discovery.type</c>: omp asks the server for its models at start (ollama, llama.cpp, lm-studio, openai-models-list…).</summary>
    public string? Discovery { get; init; }
    /// <summary><c>discovery.injectV1</c> (openai-models-list only): false asks <c>&lt;baseUrl&gt;/models</c> as written.</summary>
    public bool? InjectV1 { get; init; }
    /// <summary>The model ids listed in the entry (with discovery omp adds the server's own).</summary>
    public IReadOnlyList<string> Models { get; init; } = [];
    /// <summary>Why the page can't edit the entry without losing something (null: it can).</summary>
    public string? NotEditable { get; init; }

    public bool SameAs(ModelProviderEntry o) =>
        Id == o.Id && BaseUrl == o.BaseUrl && Api == o.Api && (ApiKey ?? "") == (o.ApiKey ?? "") && NoAuth == o.NoAuth
        && Discovery == o.Discovery && InjectV1 == o.InjectV1 && Models.SequenceEqual(o.Models);
}

/// <summary>Why <c>models.yml</c> was not read or not changed. The file is left as it was.</summary>
public sealed class ModelsConfigException(string message) : Exception(message);

/// <summary>
/// omp's <c>models.yml</c> in its agent folder (custom and local model providers; omp reads it when it starts). The app
/// edits it in place: a provider is added, replaced or removed by splicing its own lines, so other providers, other
/// settings and comments stay as the user wrote them. Each edit is checked by reading the result back (every other
/// provider and setting unchanged, the edited one as intended) and written atomically; a file that does not parse, or
/// an edit that would not read back, is refused with the reason and the file is not touched.
/// </summary>
public sealed class ModelsConfig
{
    public const string FileName = "models.yml";

    private readonly YamlMappingNode? _root;
    private readonly YamlScalarNode? _providersKey;
    private readonly YamlNode? _providersValue;

    private ModelsConfig(string path, string text, YamlMappingNode? root, YamlScalarNode? providersKey, YamlNode? providersValue,
        IReadOnlyList<ModelProviderEntry> providers)
    {
        Path = path;
        Text = text;
        _root = root;
        _providersKey = providersKey;
        _providersValue = providersValue;
        Providers = providers;
    }

    public string Path { get; }
    public string Text { get; }
    public IReadOnlyList<ModelProviderEntry> Providers { get; }

    /// <summary>
    /// The file omp reads in <paramref name="agentDir"/>: models.yml, or models.yaml when only that one exists
    /// (omp's ConfigFile fallback).
    /// </summary>
    public static string PathIn(string agentDir)
    {
        var yml = System.IO.Path.Combine(agentDir, FileName);
        var yaml = System.IO.Path.Combine(agentDir, "models.yaml");
        return !File.Exists(yml) && File.Exists(yaml) ? yaml : yml;
    }

    /// <summary>Reads <paramref name="path"/>; a missing file is an empty one.</summary>
    /// <exception cref="ModelsConfigException">It can't be read or is not valid YAML.</exception>
    public static ModelsConfig Load(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path))
            {
                // omp moves a models.json into models.yml when it starts; until then the JSON file is the one it uses
                var json = System.IO.Path.ChangeExtension(path, ".json");
                if (File.Exists(json))
                    throw new ModelsConfigException($"omp keeps these providers in {System.IO.Path.GetFileName(json)} and moves them to {FileName} when it starts. Start omp once, then try again.");
                text = "";
            }
            else text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ModelsConfigException($"Couldn't read {System.IO.Path.GetFileName(path)}: {e.Message}");
        }
        return Parse(path, text);
    }

    /// <exception cref="ModelsConfigException">Not valid YAML, or not the shape omp reads.</exception>
    public static ModelsConfig Parse(string path, string text)
    {
        var name = System.IO.Path.GetFileName(path);
        var stream = new YamlStream();
        try { stream.Load(new StringReader(text)); }
        catch (YamlException e)
        {
            var why = e.InnerException?.Message ?? e.Message;
            throw new ModelsConfigException($"{name} is not valid YAML (line {e.Start.Line}): {Clean(why)}");
        }
        if (stream.Documents.Count > 1) throw new ModelsConfigException($"{name} holds more than one YAML document; omp reads only one.");
        var rootNode = stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
        if (rootNode is null || rootNode is YamlScalarNode { Value: null or "" or "~" or "null" })
            return new ModelsConfig(path, text, null, null, null, []);
        if (rootNode is not YamlMappingNode root) throw new ModelsConfigException($"{name} is not a mapping of settings (providers: …).");

        var key = root.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == "providers");
        if (key is null) return new ModelsConfig(path, text, root, null, null, []);
        var value = root.Children[key];
        var providers = new List<ModelProviderEntry>();
        if (value is YamlMappingNode map)
            foreach (var (k, v) in map.Children)
                providers.Add(ReadEntry(k is YamlScalarNode { Value: { } id } ? id : k.ToString(), v));
        else if (!IsNull(value)) throw new ModelsConfigException($"In {name}, providers is not a mapping of providers.");
        return new ModelsConfig(path, text, root, key, value, providers);
    }

    private static string Clean(string message) => message.Replace("\r", " ").Replace("\n", " ").Trim();

    private static bool IsNull(YamlNode n) => n is YamlScalarNode { Style: ScalarStyle.Plain, Value: null or "" or "~" or "null" or "Null" or "NULL" };

    private static readonly HashSet<string> EditableKeys = ["baseUrl", "api", "apiKey", "auth", "discovery", "models"];

    /// <summary>One provider entry, with what the form can't show (other keys, other shapes) named in <see cref="ModelProviderEntry.NotEditable"/>.</summary>
    private static ModelProviderEntry ReadEntry(string id, YamlNode node)
    {
        var problems = new List<string>();
        if (node is not YamlMappingNode m || !node.Anchor.IsEmpty)
            return new ModelProviderEntry { Id = id, NotEditable = "it is written in a form this page doesn't edit" };
        string? Str(string k)
        {
            if (!m.Children.TryGetValue(new YamlScalarNode(k), out var v)) return null;
            if (v is YamlScalarNode s && s.Anchor.IsEmpty && !IsNull(s)) return s.Value;
            problems.Add(k);
            return null;
        }
        var other = m.Children.Keys.Select(k => (k as YamlScalarNode)?.Value ?? "?").Where(k => !EditableKeys.Contains(k)).ToList();
        problems.AddRange(other);
        var baseUrl = Str("baseUrl") ?? "";
        var api = Str("api");
        if (api is not null && !ModelApis.All.Contains(api)) problems.Add("api: " + api);
        var apiKey = Str("apiKey");
        var auth = Str("auth");
        if (auth is not null and not "none" and not "apiKey") problems.Add("auth: " + auth);
        string? discovery = null;
        bool? injectV1 = null;
        if (m.Children.TryGetValue(new YamlScalarNode("discovery"), out var d))
        {
            if (d is YamlMappingNode dm && dm.Anchor.IsEmpty)
            {
                foreach (var (dk, dv) in dm.Children)
                {
                    var dkey = (dk as YamlScalarNode)?.Value;
                    if (dkey == "type" && dv is YamlScalarNode { Value: { Length: > 0 } t }) discovery = t;
                    else if (dkey == "injectV1" && dv is YamlScalarNode { Style: ScalarStyle.Plain, Value: "true" or "false" } iv) injectV1 = iv.Value == "true";
                    else problems.Add("discovery." + dkey);
                }
            }
            else problems.Add("discovery");
        }
        var models = new List<string>();
        if (m.Children.TryGetValue(new YamlScalarNode("models"), out var ms))
        {
            if (ms is YamlSequenceNode seq && seq.Anchor.IsEmpty)
            {
                foreach (var item in seq.Children)
                {
                    if (item is YamlMappingNode im && im.Children.TryGetValue(new YamlScalarNode("id"), out var idNode) && idNode is YamlScalarNode { Value: { Length: > 0 } mid })
                    {
                        models.Add(mid);
                        if (im.Children.Count > 1 || !im.Anchor.IsEmpty) problems.Add("model settings beyond the id");
                    }
                    else problems.Add("models");
                }
            }
            else if (!IsNull(ms)) problems.Add("models");
        }
        return new ModelProviderEntry
        {
            Id = id,
            BaseUrl = baseUrl,
            Api = api,
            ApiKey = apiKey,
            NoAuth = auth == "none",
            Discovery = discovery,
            InjectV1 = injectV1,
            Models = models,
            NotEditable = problems.Count == 0 ? null : "it uses settings this page doesn't edit (" + string.Join(", ", problems.Distinct()) + ")",
        };
    }

    /// <summary>A provider id for <paramref name="name"/>: lower-case letters, digits, “.”, “-” and “_”, starting with a letter, not in <paramref name="taken"/> (a number is appended).</summary>
    public static string Slug(string name, IEnumerable<string> taken)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-', '.', '_');
        if (slug.Length == 0) slug = "custom";
        if (!char.IsAsciiLetter(slug[0])) slug = "p-" + slug;
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-', '.', '_');
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(slug)) return slug;
        for (var n = 2; ; n++)
        {
            var candidate = slug + "-" + n.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(candidate)) return candidate;
        }
    }

    // ── Editing ──

    /// <summary>The file's text with <paramref name="entry"/> added, or in place of the provider of the same id.</summary>
    /// <exception cref="ModelsConfigException">The file's shape doesn't allow a safe edit.</exception>
    public string WithProvider(ModelProviderEntry entry)
    {
        var nl = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string result;
        if (_providersValue is YamlMappingNode { Style: not MappingStyle.Flow } map && map.Children.Count > 0)
        {
            var keys = map.Children.Keys.ToList();
            var at = keys.FindIndex(k => k is YamlScalarNode { Value: var v } && v == entry.Id);
            var indent = IndentOf(keys[0]);
            if (at >= 0)
            {
                var (start, end) = Region(map, at);
                result = Text[..start] + Serialize(entry, indent, nl) + Text[end..];
            }
            else
            {
                var (_, end) = Region(map, keys.Count - 1);
                var before = end > 0 && Text[end - 1] != '\n' ? nl : "";
                result = Text[..end] + before + Serialize(entry, indent, nl) + Text[end..];
            }
        }
        else if (_providersKey is not null)
        {
            // providers: (nothing), providers: null, providers: {}: the line becomes the start of a block
            if (_providersValue is not null && !IsNull(_providersValue) && _providersValue is not YamlMappingNode { Children.Count: 0 })
                throw new ModelsConfigException($"providers in {System.IO.Path.GetFileName(Path)} is written on one line; edit {System.IO.Path.GetFileName(Path)} itself.");
            var start = LineStart(Index(_providersKey.Start));
            var end = LineEnd(Index(_providersValue?.End ?? _providersKey.End));
            var indent = IndentOf(_providersKey);
            result = Text[..start] + new string(' ', indent) + "providers:" + nl + Serialize(entry, indent + 2, nl) + Text[end..];
        }
        else
        {
            if (_root is { Style: MappingStyle.Flow })
                throw new ModelsConfigException($"{System.IO.Path.GetFileName(Path)} is written on one line; edit it itself.");
            var indent = _root is { Children.Count: > 0 } r ? IndentOf(r.Children.Keys.First()) : 0;
            var before = Text.Length > 0 && !Text.EndsWith('\n') ? nl : "";
            result = Text + before + new string(' ', indent) + "providers:" + nl + Serialize(entry, indent + 2, nl);
        }
        Verify(result, entry.Id, entry);
        return result;
    }

    /// <summary>The file's text without the provider <paramref name="id"/>.</summary>
    /// <exception cref="ModelsConfigException">No such provider, or the file's shape doesn't allow a safe edit.</exception>
    public string WithoutProvider(string id)
    {
        if (_providersValue is not YamlMappingNode map || map.Children.Keys.ToList() is var keys && keys.FindIndex(k => k is YamlScalarNode { Value: var v } && v == id) is var at && at < 0)
            throw new ModelsConfigException($"{id} is no longer in {System.IO.Path.GetFileName(Path)}.");
        if (map.Style == MappingStyle.Flow)
            throw new ModelsConfigException($"providers in {System.IO.Path.GetFileName(Path)} is written on one line; edit {System.IO.Path.GetFileName(Path)} itself.");
        string result;
        if (keys.Count == 1)
        {
            // The last provider: "providers: {}" (an empty mapping, as omp's schema wants), comments after it kept
            var (_, end) = Region(map, 0);
            var start = LineStart(Index(_providersKey!.Start));
            var nl = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            result = Text[..start] + new string(' ', IndentOf(_providersKey)) + "providers: {}" + nl + Text[end..];
        }
        else
        {
            var (start, end) = Region(map, at);
            // Its own comment lines just above it go with it
            while (start > 0)
            {
                var above = LineStart(start - 1);
                var line = Text.AsSpan(above, start - above).Trim();
                if (line.Length == 0 || line[0] != '#' || above <= LineStart(Index(_providersKey!.Start))) break;
                start = above;
            }
            // and the blank lines that set it apart from the next
            while (end < Text.Length && Text.IndexOf('\n', end) is var nl && nl >= 0 && Text.AsSpan(end, nl - end).Trim().Length == 0) end = nl + 1;
            result = Text[..start] + Text[end..];
        }
        Verify(result, id, null);
        return result;
    }

    /// <summary>
    /// Text offsets of provider <paramref name="at"/>: from the start of its key's line to the start of the next
    /// provider's line (the last one: to where the providers block ends), less the blank and comment lines just before
    /// that, which belong to what follows.
    /// </summary>
    private (int Start, int End) Region(YamlMappingNode map, int at)
    {
        var keys = map.Children.Keys.ToList();
        var keyIndex = Index(keys[at].Start);
        var start = LineStart(keyIndex);
        if (Text.AsSpan(start, keyIndex - start).Trim().Length > 0 || keys[at] is not YamlScalarNode)
            throw new ModelsConfigException($"The provider list in {System.IO.Path.GetFileName(Path)} is written in a form this page doesn't edit.");
        int end;
        if (at + 1 < keys.Count) end = LineStart(Index(keys[at + 1].Start));
        else
        {
            // The block ends where the next top-level setting starts (YamlDotNet's mapping end mark is not where the block ends)
            var rootKeys = _root!.Children.Keys.ToList();
            var next = rootKeys.IndexOf(_providersKey!) + 1;
            end = next < rootKeys.Count ? LineStart(Index(rootKeys[next].Start)) : Text.Length;
        }
        // Give the trailing blank and comment lines back to what follows
        while (end > start)
        {
            var lineStart = LineStart(end - 1);
            if (lineStart <= start) break;
            var line = Text.AsSpan(lineStart, end - lineStart).Trim();
            if (line.Length == 0 || line[0] == '#') end = lineStart;
            else break;
        }
        return (start, end);
    }

    private static int Index(Mark m) => checked((int)m.Index);

    private int LineStart(int index) => index <= 0 ? 0 : Text.LastIndexOf('\n', Math.Min(index, Text.Length) - 1) + 1;

    /// <summary>The offset just past the line holding <paramref name="index"/> (its line break included).</summary>
    private int LineEnd(int index)
    {
        if (index >= Text.Length) return Text.Length;
        // A node's end mark sits past its last character, at the next line's start when the node closed its line
        if (index > 0 && Text[index - 1] == '\n') return index;
        var nl = Text.IndexOf('\n', index);
        return nl < 0 ? Text.Length : nl + 1;
    }

    private int IndentOf(YamlNode key)
    {
        var i = Index(key.Start);
        return i - LineStart(i);
    }

    /// <summary>The entry as a block at <paramref name="indent"/>: strings JSON-quoted (valid YAML), so no value is read as anything else.</summary>
    internal static string Serialize(ModelProviderEntry e, int indent, string nl = "\n")
    {
        var p = new string(' ', indent);
        var sb = new StringBuilder();
        sb.Append(p).Append(Key(e.Id)).Append(':').Append(nl);
        var q = p + "  ";
        sb.Append(q).Append("baseUrl: ").Append(Quote(e.BaseUrl)).Append(nl);
        if (e.Api is { } api) sb.Append(q).Append("api: ").Append(Quote(api)).Append(nl);
        if (e.ApiKey is { Length: > 0 } key) sb.Append(q).Append("apiKey: ").Append(Quote(key)).Append(nl);
        if (e.NoAuth) sb.Append(q).Append("auth: none").Append(nl);
        if (e.Discovery is { } d)
        {
            sb.Append(q).Append("discovery:").Append(nl);
            sb.Append(q).Append("  type: ").Append(Quote(d)).Append(nl);
            if (e.InjectV1 is { } v1) sb.Append(q).Append("  injectV1: ").Append(v1 ? "true" : "false").Append(nl);
        }
        if (e.Models.Count > 0)
        {
            sb.Append(q).Append("models:").Append(nl);
            foreach (var m in e.Models) sb.Append(q).Append("  - id: ").Append(Quote(m)).Append(nl);
        }
        return sb.ToString();
    }

    private static string Quote(string s) => JsonSerializer.Serialize(s);

    private static string Key(string id) =>
        id.Length > 0 && char.IsAsciiLetter(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? id : Quote(id);

    /// <summary>
    /// Reads <paramref name="result"/> back: every setting and every other provider exactly as before, provider
    /// <paramref name="id"/> as <paramref name="expected"/> (or gone when null), the order of the others kept.
    /// </summary>
    private void Verify(string result, string id, ModelProviderEntry? expected)
    {
        var name = System.IO.Path.GetFileName(Path);
        ModelsConfig after;
        try { after = Parse(Path, result); }
        catch (ModelsConfigException) { throw new ModelsConfigException($"The change to {name} would not read back correctly, so it was not made. Edit {name} itself."); }

        string Canon(YamlMappingNode? root, bool withProviders) => root is null ? "" : string.Join("\n",
            root.Children.Where(kv => withProviders || kv.Key is not YamlScalarNode { Value: "providers" }).Select(kv => Canonical(kv.Key) + "=" + Canonical(kv.Value)));
        IEnumerable<(string, string)> Others(ModelsConfig c) => c._providersValue is YamlMappingNode m
            ? m.Children.Where(kv => kv.Key is not YamlScalarNode { Value: var v } || v != id).Select(kv => (Canonical(kv.Key), Canonical(kv.Value)))
            : [];
        var ok = Canon(_root, false) == Canon(after._root, false) && Others(this).SequenceEqual(Others(after));
        var got = after.Providers.Where(p => p.Id == id).ToList();
        ok &= expected is null ? got.Count == 0 : got.Count == 1 && got[0].NotEditable is null && got[0].SameAs(expected);
        if (!ok) throw new ModelsConfigException($"The change to {name} would not read back correctly, so it was not made. Edit {name} itself.");
    }

    /// <summary>A node's content without its layout: what omp would read (plain scalars kept apart from quoted ones, which YAML types differently).</summary>
    private static string Canonical(YamlNode node, int depth = 0)
    {
        if (depth > 64) return "…";
        return node switch
        {
            YamlScalarNode s => (s.Style is ScalarStyle.Plain ? "p:" : "s:") + s.Tag + ":" + JsonSerializer.Serialize(s.Value ?? ""),
            YamlSequenceNode q => "[" + string.Join(",", q.Children.Select(c => Canonical(c, depth + 1))) + "]",
            YamlMappingNode m => "{" + string.Join(",", m.Children.Select(kv => Canonical(kv.Key, depth + 1) + ":" + Canonical(kv.Value, depth + 1))) + "}",
            _ => "?",
        };
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/> atomically (a temporary file renamed over it), keeping the file's permissions; a new file is readable by its owner only, as it may hold API keys.</summary>
    /// <exception cref="ModelsConfigException">The file could not be written; it is unchanged.</exception>
    public static void Save(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, File.Exists(path) ? File.GetUnixFileMode(path) : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            throw new ModelsConfigException($"Couldn't write {System.IO.Path.GetFileName(path)}: {e.Message}");
        }
    }
}
