using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ombra.ClientCore.Browser;

/// <summary>
/// What the browser pane remembers between questions: "Always allow on this site" answers per origin and permission,
/// and the folder the user last saved a download to. Saved to <see cref="Path"/> (a small JSON file next to the client
/// settings) when one is given; in memory only otherwise. Thread-safe. Never holds credentials.
/// </summary>
public sealed class BrowserSiteSettings
{
    private static readonly System.Text.UTF8Encoding Utf8 = new(false);
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<PermissionKind>> _allowed = new(StringComparer.OrdinalIgnoreCase);
    // Failed revocations deny locally and are retried before the next explicit mutation.
    private HashSet<string>? _pendingRevocations;
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
        set => Update(() => _downloadFolder = value);
    }

    /// <summary>
    /// The site's remembered answer for <paramref name="kind"/>. With a backing file, each decision reads its current
    /// contents; missing, unreadable or malformed files do not authorize. Unsaved grants are ignored; failed revocations
    /// deny locally and are applied before the explicit change in the next successful persisted mutation.
    /// Without a backing file, grants are remembered for this instance only.
    /// </summary>
    public bool IsAlwaysAllowed(string origin, PermissionKind kind)
    {
        lock (_gate)
        {
            RefreshPermissions();
            return _allowed.TryGetValue(Key(origin), out var kinds) && Covers(kinds, kind);
        }
    }

    /// <summary>Remembers "Always allow on this site" (camera and microphone together also cover each alone).</summary>
    public void AlwaysAllow(string origin, PermissionKind kind)
    {
        Update(() =>
        {
            var key = Key(origin);
            if (!_allowed.TryGetValue(key, out var kinds)) _allowed[key] = kinds = [];
            kinds.Add(kind);
        });
    }

    /// <summary>Forgets every remembered answer of <paramref name="origin"/> (Settings, "clear site permissions").</summary>
    public void Forget(string origin)
    {
        lock (_gate)
        {
            var key = Key(origin);
            if (!Update(() => _allowed.Remove(key)))
                (_pendingRevocations ??= new(StringComparer.OrdinalIgnoreCase)).Add(key);
        }
    }

    /// <summary>The origins with remembered answers, refreshed from the backing file when configured.</summary>
    public IReadOnlyList<string> Sites
    {
        get
        {
            lock (_gate)
            {
                RefreshPermissions();
                return [.. _allowed.Keys.Order(StringComparer.Ordinal)];
            }
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

    private void RefreshPermissions()
    {
        Load();
        ApplyPendingRevocations();
    }

    private void ApplyPendingRevocations()
    {
        if (_pendingRevocations is null) return;
        foreach (var origin in _pendingRevocations) _allowed.Remove(origin);
    }

    private void Load(bool forUpdate = false)
    {
        if (Path is null) return;
        // Never retain a previous grant when opening or parsing the latest snapshot fails.
        _allowed.Clear();
        _downloadFolder = null;
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
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (!forUpdate || e is JsonException)
        {
            _allowed.Clear();
            _downloadFolder = null;
            // Malformed files start empty and can be replaced on the next change.
            // Other read failures abort mutations rather than overwriting unreadable state.
        }
    }

    private bool Update(Action change)
    {
        lock (_gate)
        {
            if (Path is null)
            {
                change();
                return true;
            }
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
                using var fileLock = AtomicFile.AcquireLock(Path + ".lock");
                // Apply pending revocations and this operation to the latest snapshot: stale
                // grants must never discard another writer's change or be resurrected.
                Load(forUpdate: true);
                ApplyPendingRevocations();
                change();
                var sites = new JsonObject();
                foreach (var (origin, kinds) in _allowed.OrderBy(p => p.Key, StringComparer.Ordinal))
                    sites[origin] = new JsonArray([.. kinds.Order().Select(k => (JsonNode)JsonValue.Create(k.ToString())!)]);
                var root = new JsonObject { ["downloadFolder"] = _downloadFolder, ["alwaysAllow"] = sites };
                AtomicFile.WriteAllText(Path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8);
                _pendingRevocations?.Clear();
                return true;
            }
            catch (Exception)
            {
                // The requested change was not saved. Non-permission state remains available until
                // the next refresh; permission getters always reload rather than trust unsaved grants.
                // These mutations are idempotent, including when writing failed after applying one.
                change();
                return false;
            }
        }
    }
}
