using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmpGui.ClientCore;

/// <summary>
/// The client's own settings file (<see cref="OmpRuntimeOptions"/>). Never one of omp's files.
/// Every update re-reads the file and changes only the properties that differ, so keys the client does not know
/// and edits made outside it are kept. Writes are atomic (temp file + replace). Before overwriting a version the
/// client did not write itself (a hand edit), it is copied to <c>.bak</c>, so the user's last own version survives any
/// number of automatic writes. JSON comments are not kept in the rewritten file (they are in the backup).
/// A file that does not parse is never replaced: the update fails and says so.
/// </summary>
public sealed class ClientSettingsStore(string path)
{
    private readonly object _gate = new();
    private string? _lastWritten;

    public string Path { get; } = path;
    public string BackupPath => Path + ".bak";

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public OmpRuntimeOptions Load()
    {
        lock (_gate) return Read(out _, out _);
    }

    /// <summary>Applies <paramref name="change"/> to the current file content and saves it.</summary>
    /// <exception cref="InvalidDataException">The file on disk is not valid settings JSON (left untouched).</exception>
    public OmpRuntimeOptions Update(Func<OmpRuntimeOptions, OmpRuntimeOptions> change)
    {
        lock (_gate)
        {
            var current = Read(out var existingText, out var existingNode);
            var updated = change(current);
            var before = JsonSerializer.SerializeToNode(current, OmpRuntimeOptions.Json)!.AsObject();
            var after = JsonSerializer.SerializeToNode(updated, OmpRuntimeOptions.Json)!.AsObject();
            var node = existingNode ?? new JsonObject();
            var changed = existingText is null;
            // Only what the change touched: every other key (known or not) stays as the file had it.
            foreach (var (key, value) in after)
            {
                if (JsonNode.DeepEquals(before[key], value)) continue;
                Set(node, key, value?.DeepClone());
                changed = true;
            }
            foreach (var (key, _) in before)
            {
                if (after.ContainsKey(key)) continue;
                Remove(node, key);
                changed = true;
            }
            if (!changed) return updated;
            var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            var temp = Path + ".tmp-" + Environment.ProcessId;
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (existingText is not null && existingText != _lastWritten) File.Copy(Path, BackupPath, overwrite: true);
            File.Move(temp, Path, overwrite: true);
            _lastWritten = json;
            return updated;
        }
    }

    /// <summary>Sets a property, matching an existing key case-insensitively (the reader is case-insensitive too).</summary>
    private static void Set(JsonObject node, string key, JsonNode? value)
    {
        var existing = node.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing != key) node.Remove(existing);
        node[key] = value;
    }

    private static void Remove(JsonObject node, string key)
    {
        foreach (var k in node.Select(p => p.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
            node.Remove(k);
    }

    private OmpRuntimeOptions Read(out string? text, out JsonObject? node)
    {
        text = null;
        node = null;
        if (!File.Exists(Path)) return new OmpRuntimeOptions();
        text = File.ReadAllText(Path);
        try
        {
            node = JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
                ?? throw new JsonException("the top level is not a JSON object");
            return node.Deserialize<OmpRuntimeOptions>(OmpRuntimeOptions.Json) ?? new OmpRuntimeOptions();
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{Path} is not valid settings JSON ({e.Message}); fix or remove it.", e);
        }
    }
}
