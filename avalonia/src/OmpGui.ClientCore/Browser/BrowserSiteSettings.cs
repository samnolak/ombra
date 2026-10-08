using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmpGui.ClientCore.Browser;

/// <summary>
/// What the browser pane remembers between questions: "Always allow on this site" answers per origin and permission,
/// and the folder the user last saved a download to. Saved to <see cref="Path"/> (a small JSON file next to the client
/// settings) when one is given; in memory only otherwise. Thread-safe. Never holds credentials.
/// </summary>
public sealed class BrowserSiteSettings
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<PermissionKind>> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private string? _downloadFolder;

    public BrowserSiteSettings(string? path = null)
    {
        Path = path;
        Load();
    }

    /// <summary>The file it is kept in; null: this run only.</summary>
    public string? Path { get; }

    /// <summary>The folder the user last saved a download to (chosen in the save panel); null until they chose one.</summary>
    public string? DownloadFolder
    {
        get
        {
            lock (_gate) return _downloadFolder;
        }
        set
        {
            lock (_gate)
            {
                if (_downloadFolder == value) return;
                _downloadFolder = value;
            }
            Save();
        }
    }

    /// <summary>The site's remembered answer for <paramref name="kind"/>: true when the user chose "Always allow on this site".</summary>
    public bool IsAlwaysAllowed(string origin, PermissionKind kind)
    {
        lock (_gate) return _allowed.TryGetValue(Key(origin), out var kinds) && Covers(kinds, kind);
    }

    /// <summary>Remembers "Always allow on this site" (camera and microphone together also cover each alone).</summary>
    public void AlwaysAllow(string origin, PermissionKind kind)
    {
        lock (_gate)
        {
            var key = Key(origin);
            if (!_allowed.TryGetValue(key, out var kinds)) _allowed[key] = kinds = [];
            if (!kinds.Add(kind)) return;
        }
        Save();
    }

    /// <summary>Forgets every remembered answer of <paramref name="origin"/> (Settings, "clear site permissions").</summary>
    public void Forget(string origin)
    {
        lock (_gate)
        {
            if (!_allowed.Remove(Key(origin))) return;
        }
        Save();
    }

    /// <summary>The origins with remembered answers.</summary>
    public IReadOnlyList<string> Sites
    {
        get
        {
            lock (_gate) return [.. _allowed.Keys.Order(StringComparer.Ordinal)];
        }
    }

    private static bool Covers(HashSet<PermissionKind> kinds, PermissionKind kind) =>
        kinds.Contains(kind)
        || (kind is PermissionKind.Camera or PermissionKind.Microphone && kinds.Contains(PermissionKind.CameraAndMicrophone))
        || (kind == PermissionKind.CameraAndMicrophone && kinds.Contains(PermissionKind.Camera) && kinds.Contains(PermissionKind.Microphone));

    /// <summary>"https://example.com:8443" (scheme and host lower case, default port left out).</summary>
    private static string Key(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.Host.Length > 0
            ? u.IsDefaultPort ? $"{u.Scheme}://{u.Host}" : $"{u.Scheme}://{u.Host}:{u.Port}"
            : origin.Trim().ToLowerInvariant();

    private void Load()
    {
        if (Path is null || !File.Exists(Path)) return;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject;
            if (root?["downloadFolder"] is JsonValue folder && folder.TryGetValue<string>(out var f) && f.Length > 0) _downloadFolder = f;
            if (root?["alwaysAllow"] is not JsonObject sites) return;
            foreach (var (origin, list) in sites)
            {
                if (list is not JsonArray array) continue;
                var kinds = new HashSet<PermissionKind>();
                foreach (var item in array)
                    if (item is JsonValue v && v.TryGetValue<string>(out var name) && Enum.TryParse<PermissionKind>(name, ignoreCase: true, out var kind))
                        kinds.Add(kind);
                if (kinds.Count > 0) _allowed[Key(origin)] = kinds;
            }
        }
        catch (Exception)
        {
            // An unreadable file: start empty (it is rewritten on the next change)
        }
    }

    private void Save()
    {
        if (Path is null) return;
        JsonObject root;
        lock (_gate)
        {
            var sites = new JsonObject();
            foreach (var (origin, kinds) in _allowed.OrderBy(p => p.Key, StringComparer.Ordinal))
                sites[origin] = new JsonArray([.. kinds.Order().Select(k => (JsonNode)JsonValue.Create(k.ToString())!)]);
            root = new JsonObject { ["downloadFolder"] = _downloadFolder, ["alwaysAllow"] = sites };
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception)
        {
            // Not saved: remembered for this run only
        }
    }
}
