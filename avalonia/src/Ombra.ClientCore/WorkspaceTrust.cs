using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ombra.Rpc;

namespace Ombra.ClientCore;

public enum ProjectTrustStatus
{
    /// <summary>Trusted, and its configuration is what it was when trusted.</summary>
    Trusted,
    /// <summary>Never trusted (every folder starts here, including folders opened before this check existed).</summary>
    Untrusted,
    /// <summary>Trusted once, but its configuration has changed since.</summary>
    Changed,
}

public sealed record ProjectTrustCheck(ProjectTrustStatus Status, string Folder)
{
    /// <summary>omp starts here with <see cref="WorkspaceTrust.UntrustedProjectFlag"/>.</summary>
    public bool Restricted => Status != ProjectTrustStatus.Trusted;
}

/// <summary>A folder the user trusted: the configuration fingerprint at that moment and the files omp reported for it
/// (relative to the folder), which later fingerprints include.</summary>
public sealed record TrustedFolder(string Path, string Fingerprint, DateTimeOffset TrustedAt)
{
    public string[] Sources { get; init; } = [];
}

/// <summary>A folder's configuration as it was when the user was shown what omp left out there: trusting binds to
/// this, so whatever the folder gained after the card was shown is never trusted unseen.</summary>
public sealed record ProjectTrustSnapshot(string Root, string[] Sources, string Fingerprint);

/// <summary>
/// Workspace trust (SECURITY.md "Open a folder only if you trust it"). Fail-closed: every folder the user has not
/// trusted starts its sessions, omp's terminal UI and the CLI subcommands that load extensions with omp's
/// <c>--untrusted-project</c> (or outside the folder): none of the folder's own settings, MCP servers, extensions,
/// hooks, commands, skills, prompt templates, tools, plugins or language servers load. What was left out is what omp itself reports
/// (<see cref="OmpSessionState.UntrustedProject"/>), and only that is shown to the user. Trust is kept per resolved
/// folder in a private file next to the app's settings with a fingerprint of the folder's configuration files; when it
/// changes, the folder is restricted again and asked about once more.
/// </summary>
public sealed class WorkspaceTrust(string? path)
{
    public const string UntrustedProjectFlag = "--untrusted-project";
    public const string FileName = "workspace-trust.json";

    /// <summary>omp subcommands that read the project's settings or plugin list as data and import no project code;
    /// every other subcommand (usage, login, models…) runs outside an untrusted folder.</summary>
    private static readonly string[] DataOnlySubcommands = ["config", "plugin"];

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDir = PrivateFile | UnixFileMode.UserExecute;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed record Document(int Version, TrustedFolder[] Folders);

    private readonly Lock _gate = new();
    private List<TrustedFolder> _memory = [];

    /// <summary>The store's file (null: kept in memory only).</summary>
    public string? Path { get; } = path;

    /// <summary>Beside the app's settings file (each settings folder, its own trust).</summary>
    public static string PathFor(string settingsPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(settingsPath))!, FileName);

    /// <summary>An empty private folder of the app's, no project's: where CLI subcommands run instead of an untrusted folder.</summary>
    public string NeutralDirectory
    {
        get
        {
            var dir = Path is null
                ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ombra-neutral-{Environment.ProcessId}")
                : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!, "neutral");
            CreatePrivateDirectory(dir);
            return dir;
        }
    }

    /// <summary>A folder was trusted or revoked (from any window of this app). May be raised off the UI thread.</summary>
    public event Action? Changed;

    /// <summary>The trusted folders, newest first.</summary>
    public IReadOnlyList<TrustedFolder> TrustedFolders
    {
        get { lock (_gate) return [.. Read().OrderByDescending(f => f.TrustedAt)]; }
    }

    /// <summary>Whether omp may load the folder's own code: only when trusted and unchanged since.</summary>
    public ProjectTrustCheck Check(string folder)
    {
        var root = ProjectFingerprint.Root(folder);
        TrustedFolder? record;
        lock (_gate) record = Read().FirstOrDefault(f => SessionCatalog.PathComparer.Equals(f.Path, root));
        if (record is null) return new ProjectTrustCheck(ProjectTrustStatus.Untrusted, root);
        return new ProjectTrustCheck(
            ProjectFingerprint.Compute(root, record.Sources) == record.Fingerprint ? ProjectTrustStatus.Trusted : ProjectTrustStatus.Changed, root);
    }

    /// <summary>A chat's launch: restricted (<see cref="UntrustedProjectFlag"/> among the session flags) unless trusted.</summary>
    public OmpLaunchSpec Apply(OmpLaunchSpec spec)
    {
        if (spec.SessionArguments.Contains(UntrustedProjectFlag)) return spec;
        return Check(FolderOf(spec)).Restricted
            ? spec with { SessionArguments = [.. spec.SessionArguments, UntrustedProjectFlag] }
            : spec;
    }

    /// <summary>omp's own terminal UI in the folder: the same restriction, on its command line.</summary>
    public OmpLaunchSpec ApplyToTerminal(OmpLaunchSpec spec)
    {
        if (spec.Arguments.Contains(UntrustedProjectFlag)) return spec;
        return Check(FolderOf(spec)).Restricted
            ? spec with { Arguments = [.. spec.Arguments, UntrustedProjectFlag] }
            : spec;
    }

    /// <summary>
    /// An omp CLI subcommand (<paramref name="subcommand"/>: its first argument). Subcommands that load extensions
    /// (<c>usage</c>, <c>login</c>…) take the folder's code with them, and accept no session flag: from an untrusted
    /// folder they run in <see cref="NeutralDirectory"/>, which is fine for what they do (accounts and providers).
    /// </summary>
    public OmpLaunchSpec ApplyToCli(OmpLaunchSpec spec, string? subcommand)
    {
        if (subcommand is not null && DataOnlySubcommands.Contains(subcommand) || !Check(FolderOf(spec)).Restricted) return spec;
        return spec with { WorkingDirectory = NeutralDirectory };
    }

    private static string FolderOf(OmpLaunchSpec spec) => spec.WorkingDirectory ?? Directory.GetCurrentDirectory();

    /// <summary>The folder's configuration now, with <paramref name="reported"/> (the files omp said it left out there,
    /// kept in the fingerprint from then on): take it when the user is shown that report, trust it with
    /// <see cref="Trust(ProjectTrustSnapshot)"/>.</summary>
    public static ProjectTrustSnapshot Snapshot(string folder, IEnumerable<string> reported)
    {
        var root = ProjectFingerprint.Root(folder);
        string[] sources = [.. reported.Select(s => ProjectFingerprint.Relative(root, s)).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return new ProjectTrustSnapshot(root, sources, ProjectFingerprint.Compute(root, sources));
    }

    /// <summary>Trusts the folder as it is now (<see cref="Snapshot"/> of it). A later change to the folder's
    /// configuration asks again.</summary>
    /// <exception cref="IOException">The trust file could not be written (nothing changed).</exception>
    public void Trust(string folder, IEnumerable<string> reported) => Save(Snapshot(folder, reported));

    /// <summary>Trusts the folder as it was <paramref name="shown"/> to the user. False, trusting nothing, when its
    /// configuration has changed since: the user has not seen what it would run now.</summary>
    /// <exception cref="IOException">The trust file could not be written (nothing changed).</exception>
    public bool Trust(ProjectTrustSnapshot shown)
    {
        if (ProjectFingerprint.Compute(shown.Root, shown.Sources) != shown.Fingerprint) return false;
        Save(shown);
        return true;
    }

    private void Save(ProjectTrustSnapshot snapshot)
    {
        var record = new TrustedFolder(snapshot.Root, snapshot.Fingerprint, DateTimeOffset.Now) { Sources = snapshot.Sources };
        Update(folders =>
        {
            folders.RemoveAll(f => SessionCatalog.PathComparer.Equals(f.Path, snapshot.Root));
            folders.Add(record);
        });
    }

    /// <summary>Takes the trust back: the folder's sessions start restricted again.</summary>
    /// <exception cref="IOException">The trust file could not be written (nothing changed).</exception>
    public void Revoke(string folder) =>
        Update(folders => folders.RemoveAll(f => f.Path == folder || SessionCatalog.PathComparer.Equals(f.Path, folder)));

    private void Update(Action<List<TrustedFolder>> change)
    {
        lock (_gate)
        {
            if (Path is null)
            {
                var copy = new List<TrustedFolder>(_memory);
                change(copy);
                _memory = copy;
            }
            else
            {
                CreatePrivateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
                using var held = AtomicFile.AcquireLock(Path + ".lock"); // other windows read-modify-write the same file
                var folders = Read();
                change(folders);
                Write(folders);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Creates the folder owner-only, and makes an existing one owner-only again (every write).</summary>
    private static void CreatePrivateDirectory(string dir)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(dir); return; }
        Directory.CreateDirectory(dir, PrivateDir);
        File.SetUnixFileMode(dir, PrivateDir);
    }

    /// <summary>The saved folders; a missing, unreadable or foreign-writable file trusts nothing.</summary>
    private List<TrustedFolder> Read()
    {
        if (Path is null) return [.. _memory];
        try
        {
            if (!OperatingSystem.IsWindows() && File.Exists(Path)
                && (File.GetUnixFileMode(Path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return [];
            using var stream = File.OpenRead(Path);
            var document = JsonSerializer.Deserialize<Document>(stream, Json);
            return document is { Version: 1, Folders: not null }
                ? [.. document.Folders.Where(f => f is { Path.Length: > 0, Fingerprint.Length: > 0 }).Select(f => f with { Sources = f.Sources ?? [] })]
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private void Write(List<TrustedFolder> folders)
    {
        var temp = $"{Path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
            using (var stream = new FileStream(temp, options))
                JsonSerializer.Serialize(stream, new Document(1, [.. folders]), Json);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, PrivateFile);
            File.Move(temp, Path!, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// The fingerprint of a folder's configuration, to notice a change after it was trusted. It covers files, not
/// everything they run: the project config folders omp reads (<c>.omp</c>, <c>.claude</c>, <c>.codex</c>,
/// <c>.gemini</c>, <c>.cursor</c>, <c>.windsurf</c>, <c>.opencode</c>, <c>.agents</c>, <c>.agent</c>,
/// <c>.github/skills</c>; Markdown left out as prompt text, except under <c>skills</c>, <c>commands</c>,
/// <c>prompts</c> and <c>agents</c>: those are the folder's skills, slash commands, prompt templates and subagent
/// definitions, which load only once trusted),
/// the root config files (<c>mcp.json</c>, <c>.mcp.json</c>, <c>opencode.json(c)</c>, <c>lsp.*</c>, <c>dap.*</c>,
/// <c>.vscode/mcp.json</c>), the project plugin registry omp finds by walking up, the files omp reported at trust time
/// (local language-server binaries, extension targets, skill and subagent files), and scripts inside the folder that MCP servers
/// or <c>shellPath</c> name. Code those files import from elsewhere, plugin packages and binaries outside the folder
/// are not covered (SECURITY.md).
/// </summary>
internal static class ProjectFingerprint
{
    private static readonly string[] ConfigDirs = [".omp", ".claude", ".codex", ".gemini", ".cursor", ".windsurf", ".opencode", ".agents", ".agent", ".github/skills"];
    private static readonly string[] RootFiles =
    [
        "mcp.json", ".mcp.json", "opencode.json", "opencode.jsonc", ".vscode/mcp.json",
        "lsp.json", ".lsp.json", "lsp.yaml", ".lsp.yaml", "lsp.yml", ".lsp.yml",
        "dap.json", ".dap.json", "dap.yaml", ".dap.yaml", "dap.yml", ".dap.yml",
    ];
    private static readonly string[] SkippedDirs = ["node_modules", ".git", "sessions", "plans"];
    private static readonly string[] TextExtensions = [".md", ".mdc"];
    /// <summary>Folders whose Markdown is a skill, slash command, prompt template or subagent definition rather than prompt text.</summary>
    private static readonly string[] PromptResourceDirs = ["skills", "commands", "prompts", "agents"];
    private static readonly string[] PluginRegistryFiles = ["installed_plugins.json", "package.json", "omp-plugins.lock.json"];
    private const int MaxFiles = 4000;
    private static readonly JsonDocumentOptions JsonRead = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The folder as omp sees it: full path, symbolic links resolved.</summary>
    public static string Root(string folder)
    {
        try { return SessionCatalog.ResolveLinks(System.IO.Path.GetFullPath(folder)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return folder; }
    }

    /// <summary>The path relative to the folder when it is inside it, else null.</summary>
    public static string? Relative(string root, string file)
    {
        string full;
        try { full = SessionCatalog.ResolveLinks(System.IO.Path.GetFullPath(file, root)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var relative = System.IO.Path.GetRelativePath(root, full);
        return relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative)
            ? null : relative.Replace('\\', '/');
    }

    public static string Compute(string root, IReadOnlyList<string> sources)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(string full)
        {
            if (files.Count < MaxFiles && File.Exists(full)) files[System.IO.Path.GetRelativePath(root, full).Replace('\\', '/')] = full;
        }
        foreach (var dir in ConfigDirs) Walk(root, Combine(root, dir), Add);
        foreach (var file in RootFiles) Add(Combine(root, file));
        if (PluginRoot(root) is { } plugins) foreach (var file in PluginRegistryFiles) Add(System.IO.Path.Combine(plugins, file));
        foreach (var source in sources) Add(Combine(root, source));
        foreach (var script in ReferencedScripts(root, [.. files.Values])) Add(script);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relative, full) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            try
            {
                using var stream = File.OpenRead(full);
                hash.AppendData(SHA256.HashData(stream));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { hash.AppendData("?"u8); }
            hash.AppendData("\n"u8);
        }
        if (files.Count >= MaxFiles) hash.AppendData("+more"u8);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Combine(string root, string relative) => System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static void Walk(string root, string dir, Action<string> add)
    {
        if (!Directory.Exists(dir)) return;
        var pending = new Stack<string>([dir]);
        while (pending.Count > 0)
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(pending.Pop()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (!SkippedDirs.Contains(System.IO.Path.GetFileName(entry), StringComparer.Ordinal)) pending.Push(entry);
                }
                else if (!TextExtensions.Contains(System.IO.Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase)
                    || System.IO.Path.GetRelativePath(root, entry).Split('/', '\\').Any(part => PromptResourceDirs.Contains(part, StringComparer.Ordinal)))
                    add(entry);
            }
        }
    }

    /// <summary><c>.omp/plugins</c> at the nearest <c>.omp/</c> (or git root) above the folder, as omp finds it.</summary>
    private static string? PluginRoot(string root)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var marker in new[] { ".omp", ".git" })
        {
            for (var dir = root; dir is not null; dir = System.IO.Path.GetDirectoryName(dir))
            {
                if (home.Length > 0 && SessionCatalog.SamePath(dir, home)) break;
                var candidate = System.IO.Path.Combine(dir, marker);
                if (marker == ".omp" ? Directory.Exists(candidate) : System.IO.Path.Exists(candidate)) return System.IO.Path.Combine(dir, ".omp", "plugins");
            }
        }
        return null;
    }

    /// <summary>Files inside the folder that the JSON config names as programs: MCP <c>command</c>/<c>args</c>, <c>shellPath</c>.</summary>
    private static IEnumerable<string> ReferencedScripts(string root, IReadOnlyList<string> configFiles)
    {
        foreach (var file in configFiles.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase)))
        {
            JsonNode? json;
            try { json = JsonNode.Parse(File.ReadAllText(file), documentOptions: JsonRead); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { continue; }
            foreach (var value in Strings(json, null))
            {
                if (value.Length == 0 || value.Length > 1024) continue;
                foreach (var candidate in new[] { System.IO.Path.GetDirectoryName(file)!, root })
                {
                    string full;
                    try { full = System.IO.Path.GetFullPath(value, candidate); }
                    catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                    if (Relative(root, full) is not null && File.Exists(full)) yield return full;
                }
            }
        }
    }

    /// <summary>String values under <c>command</c>, <c>args</c> and <c>shellPath</c> keys, at any depth.</summary>
    private static IEnumerable<string> Strings(JsonNode? node, string? key)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                    foreach (var s in Strings(v, k)) yield return s;
                break;
            case JsonArray a:
                foreach (var v in a)
                    foreach (var s in Strings(v, key)) yield return s;
                break;
            case JsonValue v when key is "command" or "args" or "shellPath" && v.TryGetValue<string>(out var text):
                yield return text;
                break;
        }
    }
}
