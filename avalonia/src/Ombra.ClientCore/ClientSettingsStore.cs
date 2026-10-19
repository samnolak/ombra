using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ombra.ClientCore;

/// <summary>
/// The client's own settings file (<see cref="OmpRuntimeOptions"/>). Never one of omp's files.
/// Updates merge only changed properties into the latest file, preserving unknown and invalid fields.
/// External versions are backed up before atomic replacement; malformed JSON is never replaced.
/// If persistence is unavailable, changes stay in this store for this run and overlay subsequent disk reads.
/// </summary>
public sealed class ClientSettingsStore(string path)
{
    private readonly object _gate = new();
    private readonly JsonObject _pendingChanges = new();
    private readonly HashSet<string> _pendingRemovals = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastWritten;
    private bool _persistenceChecked;

    public string Path { get; } = path;
    public string BackupPath => Path + ".bak";

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly IReadOnlyDictionary<string, SettingProperty> Properties = CreateProperties();
    private sealed record SettingProperty(PropertyInfo Property, string Name, bool AllowsNull);

    private static IReadOnlyDictionary<string, SettingProperty> CreateProperties()
    {
        var nullability = new NullabilityInfoContext();
        return typeof(OmpRuntimeOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.SetMethod is { IsPublic: true } && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p => new SettingProperty(p, p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? OmpRuntimeOptions.Json.PropertyNamingPolicy?.ConvertName(p.Name) ?? p.Name,
                nullability.Create(p).WriteState == NullabilityState.Nullable))
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Canonical field names only, never the rejected values or deserializer messages. The UI localizes the explanation.</summary>
    public IReadOnlyList<string> InvalidFields { get; private set; } = [];

    /// <summary>Changes are effective in this store but cannot currently be saved across app restarts.</summary>
    public bool IsPersistenceUnavailable { get; private set; }

    /// <summary>Raised when invalid fields or persistence availability change. May be raised off the UI thread.</summary>
    public event Action? WarningsChanged;

    public OmpRuntimeOptions Load()
    {
        lock (_gate)
        {
            var node = Read(out _);
            ApplyPending(node);
            var options = Decode(node, out var invalidFields);
            var persistenceUnavailable = _persistenceChecked ? IsPersistenceUnavailable : !CheckPersistence();
            SetWarnings(invalidFields, persistenceUnavailable);
            return options;
        }
    }

    /// <summary>
    /// Applies a change to the latest settings. I/O failures retain changes in memory; the next update retries saving.
    /// Pending local changes win on the same field, while external edits to other fields remain effective.
    /// </summary>
    /// <exception cref="InvalidDataException">The file on disk is not valid settings JSON (left untouched).</exception>
    public OmpRuntimeOptions Update(Func<OmpRuntimeOptions, OmpRuntimeOptions> change)
    {
        lock (_gate)
        {
            var node = Read(out var existingText);
            ApplyPending(node);
            var current = Decode(node, out _);
            var updated = change(current);
            var before = JsonSerializer.SerializeToNode(current, OmpRuntimeOptions.Json)!.AsObject();
            var after = JsonSerializer.SerializeToNode(updated, OmpRuntimeOptions.Json)!.AsObject();
            foreach (var (key, value) in after)
            {
                if (JsonNode.DeepEquals(before[key], value)) continue;
                Set(_pendingChanges, key, value?.DeepClone());
                _pendingRemovals.Remove(key);
            }
            foreach (var (key, _) in before)
            {
                if (after.ContainsKey(key)) continue;
                Remove(_pendingChanges, key);
                _pendingRemovals.Add(key);
            }
            ApplyPending(node);
            var effective = Decode(node, out var invalidFields);
            if (existingText is not null && _pendingChanges.Count == 0 && _pendingRemovals.Count == 0 && !IsPersistenceUnavailable)
            {
                SetWarnings(invalidFields, false);
                return effective;
            }
            var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
            _persistenceChecked = true;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
                if (existingText is not null && existingText != _lastWritten) File.Copy(Path, BackupPath, overwrite: true);
                AtomicFile.WriteAllText(Path, json, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SetWarnings(invalidFields, true);
                return effective;
            }
            _lastWritten = json;
            _pendingChanges.Clear();
            _pendingRemovals.Clear();
            SetWarnings(invalidFields, false);
            return effective;
        }
    }

    private void ApplyPending(JsonObject node)
    {
        foreach (var (key, value) in _pendingChanges) Set(node, key, value?.DeepClone());
        foreach (var key in _pendingRemovals) Remove(node, key);
    }

    private bool CheckPersistence()
    {
        _persistenceChecked = true;
        // Probe the actual directory, not permission bits: ACLs and read-only volumes also matter.
        var probe = Path + ".probe-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SetWarnings(IReadOnlyList<string> invalidFields, bool persistenceUnavailable)
    {
        var changed = !InvalidFields.SequenceEqual(invalidFields) || IsPersistenceUnavailable != persistenceUnavailable;
        InvalidFields = invalidFields;
        IsPersistenceUnavailable = persistenceUnavailable;
        if (changed) WarningsChanged?.Invoke();
    }

    /// <summary>Matches existing keys case-insensitively, just like the reader.</summary>
    private static void Set(JsonObject node, string key, JsonNode? value)
    {
        Remove(node, key);
        node[key] = value;
    }

    private static void Remove(JsonObject node, string key)
    {
        foreach (var k in node.Select(p => p.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
            node.Remove(k);
    }

    private JsonObject Read(out string? text)
    {
        text = null;
        try { text = File.ReadAllText(Path); }
        catch (FileNotFoundException) { return new JsonObject(); }
        catch (DirectoryNotFoundException) { return new JsonObject(); }
        try
        {
            return JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
                ?? throw new JsonException();
        }
        catch (JsonException e)
        {
            // Parser messages can contain settings values. Keep them out of user-facing warnings.
            throw new InvalidDataException("The settings file is not valid JSON; fix or remove it. It has not been overwritten.", e);
        }
    }

    private static OmpRuntimeOptions Decode(JsonObject node, out IReadOnlyList<string> invalidFields)
    {
        var options = new OmpRuntimeOptions();
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, jsonValue) in node)
        {
            if (!Properties.TryGetValue(key, out var setting)) continue;
            try
            {
                if (jsonValue is null && !setting.AllowsNull) throw new JsonException();
                var value = jsonValue?.Deserialize(setting.Property.PropertyType, OmpRuntimeOptions.Json);
                setting.Property.SetValue(options, value);
                invalid.Remove(setting.Name);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                invalid.Add(setting.Name);
            }
        }
        invalidFields = invalid.Order(StringComparer.Ordinal).ToArray();
        return options;
    }
}
