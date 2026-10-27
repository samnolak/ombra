using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ombra.App.Localization;
using Ombra.ClientCore;

namespace Ombra.App.Services.Runtime;

/// <summary>One Bun build on npm: package <c>@oven/bun-&lt;key&gt;</c>, its registry integrity and the executable in the tarball.</summary>
public sealed record BunBuild(string Key, string Integrity, string Exe)
{
    public string Package => "@oven/bun-" + Key;
    public string TarballPath => $"{Package}/-/bun-{Key}-{RuntimePack.BunVersion}.tgz";
}

/// <summary>
/// The complete owned Ombra engine runs on pinned Bun with integrity-locked npm dependencies.
/// Canonical sources are embedded byte-for-byte and installed beside the unmodified npm tree.
/// </summary>
public static partial class RuntimePack
{
    public const string Name = "ombra-engine-18.12.2-bun-1.4.2-v1";
    public const string SourceDirectoryName = "ombra-engine-18.12.2-bun-1.4.2-v1";
    public const string SourceIdentity = "ombra-engine-v1";
    public const string OmpVersion = "18.12.2";
    public const string OmpIntegrity = "sha512-1qh9EwuiMBUTVix0ZWZNOMPTW++nWhL6t3EaRhGe05sVIBfDTyVl36iTyDv0TvM+R/EmOL4PvFrmIPgXd3xYQQ==";
    public const string BunVersion = "1.4.2";
    public const string CliEntry = "engine/coding-agent/src/cli.ts";
    public const string HostEntry = "engine/host.ts";
    public const string HistoricalCliEntry = "node_modules/@oh-my-pi/pi-coding-agent/src/cli.ts";
    public const string DefaultRegistry = "https://registry.npmjs.org";
    public static readonly IReadOnlyList<string> PackFiles = ["package.json", "bun.lock", "bunfig.toml"];

    /// <summary>npm <c>dist.integrity</c> of <c>@oven/bun-*@1.4.2</c> (read from registry.npmjs.org when pinned).</summary>
    public static readonly IReadOnlyDictionary<string, BunBuild> Bun = new[]
    {
        new BunBuild("linux-x64", "sha512-9/E/UXOTpSo3YsV5g+FhtTd/qTpiWoKuxS12cqtuYA1ssu9fRAoPQnipFgGyck3tWO63iUdxBiygq+kELFawng==", "bin/bun"),
        new BunBuild("linux-x64-baseline", "sha512-OKKzX/SOVIUVARCzTg+2r4Rcc4fPyWnQmBsd3K08buxeu7DAof4iwW9zSb6Lt1NhTCu4Uhi8k9UeI6f7GEE4Zg==", "bin/bun"),
        new BunBuild("linux-aarch64", "sha512-3BBP9ovJ2RGHFH6Ae1CAtxNtG1+YY6GD6rmYbsUosoAk9+OEl6zeDQ/k4fBkc6dYOJCtWnx8hUxzNzQATSmvYQ==", "bin/bun"),
        new BunBuild("darwin-aarch64", "sha512-MXdZkP1featqxZ+/VTXWG1BVjM4OGBehVY2Q88EeUj/7L0UMeCGItmyPYTN+wxvlGJ6F66JEtzsw+GvQWewnag==", "bin/bun"),
        new BunBuild("darwin-x64", "sha512-gZTxZuLjkUhAWjTETu3tw0WhsEdNkJ64daj60ybhPf835a2yollV3yTkK9JozvzKPx4TRFzLSl8C+U525pxVbw==", "bin/bun"),
        new BunBuild("darwin-x64-baseline", "sha512-lVDbk/HkN+RP8QKXBe0aou7EifA2PC9+Z4cvy2VDpmR1ONdDm8e2h/HsU7ABq7mvVs/76iq5H86nMvAhwJDadQ==", "bin/bun"),
        new BunBuild("windows-x64", "sha512-+bN6OuVld/9diT/RLSXSW7JE6CvNE3gL9XsAEjULi1nUsXd6DNO6GuA9jNdNb3r8PdJFnYHr5aypNV1Oj3Rd9g==", "bin/bun.exe"),
        new BunBuild("windows-x64-baseline", "sha512-lq82LqVjSXAx9gNUcibCdfQdtrelh86FxQqlhpOdiV4SZQAP8HHUxWhnfbjx2fqz+jzglwBvjmlEs7xFyONBZw==", "bin/bun.exe"),
        new BunBuild("windows-aarch64", "sha512-8EJ1ST7339WJE3poPW5nBgVW/lWf9HBz4W27ZUNhburKmcBLOByPyE6DP9fHD8FQGm5c+ilUN2hX1mrW0jxq9Q==", "bin/bun.exe"),
    }.ToDictionary(b => b.Key);

    /// <summary>An integrity-pinned install input or complete canonical engine source embedded in the client.</summary>
    public static Stream OpenPackFile(string name) =>
        typeof(RuntimePack).Assembly.GetManifestResourceStream("runtime-pack/" + name)
        ?? throw new FileNotFoundException("runtime pack file not built into the client", name);

    /// <summary>Machine-level install root (never the omp profile or the client settings); OMBRA_RUNTIME_DIR overrides it.</summary>
    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("OMBRA_RUNTIME_DIR") is { Length: > 0 } root ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Ombra", "runtimes");

    /// <summary>
    /// Where a package carries its installed pack (tools/package/package.sh): <c>Ombra.app/Contents/Resources/engine</c>
    /// on macOS, <c>engine</c> beside the executable elsewhere. Absent in builds run from source.
    /// </summary>
    public static string BundledRoot =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) == "MacOS"
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "engine"))
            : Path.Combine(AppContext.BaseDirectory, "engine");
}

/// <summary>An installed owned engine; standalone CLI is reserved for explicit management/terminal actions.</summary>
public sealed record InstalledRuntime(string Directory, string Bun, string Cli, string Platform)
{
    /// <summary>
    /// Where Bun keeps its runtime transpiler cache. Bun's default is the user's global <c>~/.bun/install/cache</c>;
    /// the pack keeps it in its own folder so the client writes nothing outside its folders.
    /// </summary>
    public const string TranspilerCacheVariable = "BUN_RUNTIME_TRANSPILER_CACHE_PATH";
    public static string TranspilerCacheIn(string packDirectory) => Path.Combine(packDirectory, "transpiler-cache");

    /// <summary>Bun's transpiler cache for this pack: in the pack's folder, or under the user's runtimes folder for the
    /// read-only pack inside the app (writing there would break the app's code signature).</summary>
    public string TranspilerCache { get; init; } = TranspilerCacheIn(Directory);

    /// <summary>The pack shipped inside the app package, used in place.</summary>
    public bool Bundled { get; init; }

    /// <summary>The pack's own <c>bunfig.toml</c>, next to its <c>package.json</c>.</summary>
    public string BunConfig => Path.Combine(Directory, "omp", "bunfig.toml");
    public string Host => Path.Combine(Directory, "omp", RuntimePack.HostEntry.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Points omp at the pack's Ombra harness (rules, agents, extensions, skills, settings defaults), a layer
    /// below the user's profile and the project.</summary>
    public const string HarnessVariable = "OMBRA_HARNESS_DIR";
    /// <summary>The Ombra harness the pack carries (<c>harness/profile</c> in the repository).</summary>
    public string Harness => Path.Combine(Directory, "omp", "harness");

    /// <summary>The settings with this runtime filled in where the user left the command empty.</summary>
    public OmpRuntimeOptions ApplyTo(OmpRuntimeOptions o)
    {
        if (o.Command is not null || o.PrefixArgs.Length > 0) return o;
        var env = new Dictionary<string, string?>(o.Environment, o.Environment.Comparer);
        env.TryAdd(TranspilerCacheVariable, TranspilerCache); // a value the user set wins
        env.TryAdd(HarnessVariable, Harness); // so does a harness the user points at (e.g. the repository's harness/profile)
        // Bun reads bunfig.toml (preload scripts) and .env from its working directory, which is the opened project:
        // a folder could run code in omp or redirect its provider traffic. Bun gets the pack's config and no env file;
        // omp still loads its own .env from the profile.
        return o with
        {
            Command = Bun,
            PrefixArgs = ["--config=" + BunConfig, "--no-env-file", "--no-install", Host],
            ManagementPrefixArgs = ["--config=" + BunConfig, "--no-env-file", "--no-install", Cli],
            Environment = env,
        };
    }
}

/// <summary>A pack an earlier client version installed, with the omp version it runs.</summary>
public sealed record PreviousRuntime(InstalledRuntime Runtime, string Pack, string Omp, DateTimeOffset InstalledAt);

public sealed record RuntimeInstallProgress(string Stage, long Done = 0, long Total = 0);

/// <summary>Result of a child process the installer runs (bun install, owned host --self-check).</summary>
public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>
/// Installs <see cref="RuntimePack"/> under <c>root/&lt;pack name&gt;</c>. Everything happens in a staging folder that
/// replaces the target only once the owned host imports successfully and reports its pinned protocol/version; a failure or cancel leaves the previous
/// state as it was and no partial files. Only folders this installer created are ever removed.
/// </summary>
public sealed class RuntimeInstaller
{
    private const long MaxTarballBytes = 300L << 20;
    private const string Marker = "installed.json";
    /// <summary>Written into every folder this installer creates: only such folders are ever removed as leftovers.</summary>
    private const string WorkMarker = ".ombra-runtime-work";
    /// <summary>Bun's codes for a certificate chain it cannot verify: what an HTTPS-inspecting network causes.</summary>
    private static readonly string[] CertificateErrors =
        ["SELF_SIGNED_CERT_IN_CHAIN", "UNABLE_TO_GET_ISSUER_CERT_LOCALLY", "UNABLE_TO_VERIFY_LEAF_SIGNATURE", "DEPTH_ZERO_SELF_SIGNED_CERT"];
    private readonly HttpClient _http;
    private readonly string _registry;
    private readonly IReadOnlyDictionary<string, BunBuild> _builds;

    public RuntimeInstaller(HttpClient http, string root, string? platform, string registry = RuntimePack.DefaultRegistry,
        IReadOnlyDictionary<string, BunBuild>? builds = null)
    {
        _http = http;
        Root = root;
        Platform = platform;
        _registry = registry.TrimEnd('/');
        _builds = builds ?? RuntimePack.Bun;
    }

    public string Root { get; }
    public string? Platform { get; }
    public string TargetDirectory => Path.Combine(Root, RuntimePack.Name);
    public bool Supported => Platform is not null && _builds.ContainsKey(Platform);

    /// <summary>Runs a child process to completion; replaceable in tests.</summary>
    public Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ProcessResult>> Run { get; init; } = RunProcessAsync;

    /// <summary>How long the download may go without a byte before it fails as stalled; shorter in tests.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Variables the user's network trust settings add for Bun (Settings › Advanced › Corporate network certificates,
    /// <see cref="Ombra.ClientCore.Network.CorporateTrust"/>), read at each install: <c>bun install</c> fetches the pinned
    /// packages through the same network as the engine, so it gets the same trust. Null: none.
    /// </summary>
    public Func<IReadOnlyDictionary<string, string?>>? NetworkEnvironment { get; set; }

    /// <summary>
    /// Folder holding the pack built into the app package (<see cref="RuntimePack.BundledRoot"/>); null: none. A bundled
    /// pack for this pin and platform runs in place, read-only, with its transpiler cache under <see cref="Root"/>.
    /// </summary>
    public string? BundledRoot { get; init; }

    /// <summary>The bundled pack's folder (whether or not it exists).</summary>
    public string? BundledDirectory => BundledRoot is null ? null : Path.Combine(BundledRoot, RuntimePack.Name);

    /// <summary>
    /// The pack to run, or null when there is none or it is incomplete. A pack this installer put in <see cref="Root"/>
    /// while a valid bundled pack was present (Repair engine) comes first; then the bundled pack, when it was built for
    /// this pin and this machine's platform; then a pack in <see cref="Root"/> installed without a bundle (source builds,
    /// earlier versions).
    /// </summary>
    public InstalledRuntime? FindInstalled()
    {
        var own = Read(TargetDirectory, out var overridesBundled);
        if (own is not null && overridesBundled) return own;
        return FindBundled() ?? own;
    }

    /// <summary>The bundled pack when it matches this client's pin and this machine's platform; else null.</summary>
    public InstalledRuntime? FindBundled()
    {
        if (BundledDirectory is not { } dir || Read(dir, out _) is not { } rt || rt.Platform != Platform) return null;
        return rt with { Bundled = true, TranspilerCache = Path.Combine(Root, ".transpiler-cache", RuntimePack.Name) };
    }

    private InstalledRuntime? Read(string directory, out bool overridesBundled)
    {
        overridesBundled = false;
        try
        {
            var marker = Path.Combine(directory, Marker);
            if (!File.Exists(marker)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            if (doc.RootElement.GetProperty("pack").GetString() != RuntimePack.Name) return null;
            if (doc.RootElement.GetProperty("packManifestSha256").GetString() != RuntimePack.ManifestSha256 ||
                doc.RootElement.GetProperty("sourceArtifact").GetProperty("identity").GetString() != RuntimePack.SourceIdentity ||
                doc.RootElement.GetProperty("sourceArtifact").GetProperty("sha256").GetString() != RuntimePack.SourceSha256)
                return null;
            overridesBundled = doc.RootElement.TryGetProperty("overridesBundled", out var o) && o.ValueKind == JsonValueKind.True;
            var platform = doc.RootElement.GetProperty("platform").GetString() ?? "";
            var rt = Layout(directory, platform);
            return rt is not null && File.Exists(rt.Bun) && File.Exists(rt.Cli) && File.Exists(rt.Host) && Directory.Exists(rt.Harness) ? rt : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// A pack an earlier version of the client installed (another pack name), retained for migration and cleanup.
    /// It is not a compatible fallback: this client requires the shared engine protocol.
    /// </summary>
    public PreviousRuntime? FindPrevious() => Previous().OrderByDescending(p => p.InstalledAt).FirstOrDefault();

    /// <summary>Only the owned pack is a default runtime. A missing pack enters the normal install recovery path,
    /// never an unpatched earlier pack or an arbitrary omp found on PATH. Explicit commands remain user-owned.</summary>
    public OmpRuntimeOptions ApplyDefaultTo(OmpRuntimeOptions options)
    {
        if (options.Command is not null || options.PrefixArgs.Length > 0) return options;
        return FindInstalled() is { } runtime ? runtime.ApplyTo(options)
            : options with { Command = Path.Combine(TargetDirectory, "install-required") };
    }

    /// <summary>Folders in the root that this installer completed (their <c>installed.json</c> names the folder) for another pack.</summary>
    private IEnumerable<PreviousRuntime> Previous()
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.Exists(Root) ? Directory.EnumerateDirectories(Root).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (name == RuntimePack.Name || name.StartsWith('.')) continue;
            PreviousRuntime? found = null;
            try
            {
                var marker = Path.Combine(dir, Marker);
                if (!File.Exists(marker)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(marker));
                var root = doc.RootElement;
                if (root.GetProperty("pack").GetString() != name) continue;
                var rt = Layout(dir, root.GetProperty("platform").GetString() ?? "", historical: true);
                if (rt is null || !File.Exists(rt.Bun) || !File.Exists(rt.Cli)) continue;
                var at = root.TryGetProperty("installedAt", out var t) && t.TryGetDateTimeOffset(out var d) ? d : DateTimeOffset.MinValue;
                found = new PreviousRuntime(rt, name, root.TryGetProperty("omp", out var o) ? o.GetString() ?? "?" : "?", at);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
            if (found is not null) yield return found;
        }
    }

    private InstalledRuntime? Layout(string dir, string platform, bool historical = false) =>
        _builds.TryGetValue(platform, out var b)
            ? new InstalledRuntime(dir, Path.Combine(dir, "bun", Path.GetFileName(b.Exe)),
                Path.Combine(dir, "omp", (historical && !File.Exists(Path.Combine(dir, "omp", RuntimePack.CliEntry))
                    ? RuntimePack.HistoricalCliEntry : RuntimePack.CliEntry).Replace('/', Path.DirectorySeparatorChar)), platform)
            : null;

    /// <summary>Ownership is the pack marker, not completeness; an owned incomplete pack can be repaired.</summary>
    private bool ValidateInstallTarget()
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(TargetDirectory); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            throw new IOException(Loc.F("Cannot replace runtime target '{0}': it is not an owned directory.", TargetDirectory));
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TargetDirectory, Marker)));
            if (doc.RootElement.GetProperty("pack").GetString() == RuntimePack.Name) return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new IOException(Loc.F("Cannot replace runtime target '{0}': its ownership marker is missing or invalid.", TargetDirectory), e);
        }
        throw new IOException(Loc.F("Cannot replace runtime target '{0}': its ownership marker names another pack.", TargetDirectory));
    }

    /// <exception cref="NotSupportedException">No pinned Bun build for this platform.</exception>
    /// <exception cref="InvalidDataException">An integrity check failed, or the owned host did not report its pinned protocol/version.</exception>
    /// <exception cref="IOException">bun install failed (the message carries the end of its output).</exception>
    public async Task<InstalledRuntime> InstallAsync(IProgress<RuntimeInstallProgress>? progress, CancellationToken ct)
    {
        if (Platform is null || !_builds.TryGetValue(Platform, out var build))
            throw new NotSupportedException(Loc.F("No pinned Bun {0} build for this platform ({1}).", RuntimePack.BunVersion, Platform ?? Loc.T("unknown")));
        Directory.CreateDirectory(Root);
        // One install at a time per root (two windows, or the app and --self-test): the other one says so.
        FileStream installLock;
        try { installLock = new FileStream(Path.Combine(Root, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException(Loc.T("Another installation of the omp runtime is running; try again when it has finished.")); }
        using var _ = installLock;
        ValidateInstallTarget();
        // Leftovers of an earlier attempt that could not be removed then (a file still in use on Windows) — only
        // folders this installer made (they carry its marker; the root may be a folder the user shares with other things).
        foreach (var stale in Directory.EnumerateDirectories(Root)
                     .Where(d => Path.GetFileName(d) is var n && (n.StartsWith(".staging-", StringComparison.Ordinal) || n.StartsWith(".old-", StringComparison.Ordinal)))
                     .Where(d => File.Exists(Path.Combine(d, WorkMarker))).ToList())
            TryDelete(stale);
        var staging = Path.Combine(Root, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, WorkMarker), "created by Ombra's runtime installer; safe to delete", ct).ConfigureAwait(false);
        try
        {
            var layout = Layout(staging, Platform)!;
            await DownloadBunAsync(build, layout.Bun, progress, ct).ConfigureAwait(false);

            progress?.Report(new RuntimeInstallProgress(Loc.F("Installing omp {0} from the pinned lockfile", RuntimePack.OmpVersion)));
            var ompDir = Path.Combine(staging, "omp");
            Directory.CreateDirectory(ompDir);
            foreach (var name in RuntimePack.PackFiles)
            {
                await using var src = RuntimePack.OpenPackFile(name);
                await using var dst = File.Create(Path.Combine(ompDir, name));
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
            var cache = Path.Combine(staging, "cache");
            var install = new ProcessStartInfo(layout.Bun) { WorkingDirectory = ompDir };
            // Bun's default 48 concurrent requests stalled on the large ONNX tarballs during cold installs;
            // the same pinned inputs completed with four. Bound transfer fan-out, not integrity or the deadline.
            foreach (var a in new[] { "install", "--frozen-lockfile", "--ignore-scripts", "--no-progress", "--network-concurrency=4" })
                install.ArgumentList.Add(a);
            install.Environment["BUN_INSTALL_CACHE_DIR"] = cache; // no writes to the user's global Bun cache
            install.Environment[InstalledRuntime.TranspilerCacheVariable] = InstalledRuntime.TranspilerCacheIn(staging);
            install.Environment["DO_NOT_TRACK"] = "1";
            var trust = NetworkEnvironment?.Invoke() ?? new Dictionary<string, string?>();
            foreach (var (key, value) in trust) install.Environment[key] = value;
            var result = await Run(install, TimeSpan.FromMinutes(20), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, "install.log"), result.Output, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new IOException($"bun install failed (exit {result.ExitCode}):\n{Tail(result.Output, 12)}"
                    + (trust.Count == 0 && CertificateErrors.Any(result.Output.Contains)
                        ? "\n\n" + Loc.T("Your network inspects HTTPS connections. Turn on Settings › Advanced › Corporate network certificates, then repair the engine.")
                        : ""));
            TryDelete(cache);
            // Before the source artifact and the self-check: the host must start from the pruned tree.
            var pruned = RuntimePrune.Prune(ompDir, Platform, ct);
            await File.AppendAllTextAsync(Path.Combine(staging, "install.log"), $"\npruned {pruned} bytes the engine never loads on {Platform}\n", ct).ConfigureAwait(false);

            progress?.Report(new RuntimeInstallProgress(Loc.T("Installing verified owned engine")));
            var sourceArtifact = await RuntimeSourceArtifact.ApplyAsync(staging, ct).ConfigureAwait(false);

            progress?.Report(new RuntimeInstallProgress(Loc.T("Checking owned engine")));
            await CheckHostAsync(layout, InstalledRuntime.TranspilerCacheIn(staging), ct).ConfigureAwait(false);

            var marker = JsonSerializer.Serialize(new
            {
                pack = RuntimePack.Name,
                omp = RuntimePack.OmpVersion,
                bun = RuntimePack.BunVersion,
                platform = Platform,
                bunPackage = build.Package,
                bunIntegrity = build.Integrity,
                packManifestSha256 = RuntimePack.ManifestSha256,
                sourceArtifact,
                ompIntegrity = RuntimePack.OmpIntegrity,
                installedAt = DateTimeOffset.UtcNow,
                // Installed while the app carries a valid pack of its own (Repair engine): this one is used instead.
                overridesBundled = FindBundled() is not null,
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(staging, Marker), marker, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Recheck ownership after staging, before moving any existing target. Keep the old install
            // until the new one is in place, so a failed move can be undone.
            string? old = null;
            if (ValidateInstallTarget())
            {
                old = Path.Combine(Root, ".old-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.Move(TargetDirectory, old);
            }
            try
            {
                Directory.Move(staging, TargetDirectory);
            }
            catch
            {
                if (old is not null) Directory.Move(old, TargetDirectory);
                throw;
            }
            if (old is not null) TryDelete(old);
            progress?.Report(new RuntimeInstallProgress(Loc.T("Installed")));
            return Layout(TargetDirectory, Platform)!;
        }
        finally
        {
            if (Directory.Exists(staging)) TryDelete(staging);
        }
    }

    /// <summary>Runs the owned host's <c>--self-check</c> from <paramref name="runtime"/>; it must report protocol 1 and
    /// the pinned omp version.</summary>
    /// <exception cref="InvalidDataException">It did not.</exception>
    public async Task CheckHostAsync(InstalledRuntime runtime, string transpilerCache, CancellationToken ct)
    {
        var version = new ProcessStartInfo(runtime.Bun) { WorkingDirectory = Path.Combine(runtime.Directory, "omp") };
        foreach (var a in new[] { "--config=" + runtime.BunConfig, "--no-env-file", "--no-install", runtime.Host, "--self-check" })
            version.ArgumentList.Add(a);
        version.Environment[InstalledRuntime.TranspilerCacheVariable] = transpilerCache;
        var v = await Run(version, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        var valid = false;
        try
        {
            using var check = JsonDocument.Parse(v.Output.Trim());
            valid = check.RootElement.GetProperty("engineProtocolVersion").GetInt32() == 1 &&
                check.RootElement.GetProperty("ompVersion").GetString() == RuntimePack.OmpVersion;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        if (v.ExitCode != 0 || !valid)
            throw new InvalidDataException($"Owned engine did not report protocol 1 / omp {RuntimePack.OmpVersion} (exit {v.ExitCode}):\n{Tail(v.Output, 6)}");
    }

    private async Task DownloadBunAsync(BunBuild build, string exePath, IProgress<RuntimeInstallProgress>? progress, CancellationToken ct)
    {
        var stage = Loc.F("Downloading Bun {0}", RuntimePack.BunVersion);
        progress?.Report(new RuntimeInstallProgress(stage));
        if (!build.Integrity.StartsWith("sha512-", StringComparison.Ordinal)) throw new InvalidDataException("only sha512 integrities are accepted");
        var expected = Convert.FromBase64String(build.Integrity["sha512-".Length..]);
        var tgz = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(exePath)!)!, "bun.tgz");
        // The client has no total timeout (a pack is tens of MB on any connection); a connection that stays open and
        // sends nothing would show the last progress forever, and --self-test --install-runtime has no Cancel. Each wait
        // (the headers, then every read) gets StallTimeout; a chunk that arrives restarts it.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await _http.GetAsync($"{_registry}/{build.TarballPath}", HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var output = new FileStream(tgz, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            var buffer = new byte[1 << 16];
            long written = 0;
            int n;
            stall.CancelAfter(StallTimeout);
            while ((n = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                stall.CancelAfter(StallTimeout);
                written += n;
                if (written > MaxTarballBytes) throw new InvalidDataException(Loc.F("{0} is larger than {1} MB", build.Package, MaxTarballBytes >> 20));
                sha.AppendData(buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                progress?.Report(new RuntimeInstallProgress(stage, written, total));
            }
            if (!CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), expected))
                throw new InvalidDataException(Loc.F("{0}@{1} does not match its pinned integrity.", build.Package, RuntimePack.BunVersion));
        }
        catch (Exception e) when (e is OperationCanceledException or IOException && stall.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.F("The download stalled (no data for {0:0} s). Check the connection and try again.", StallTimeout.TotalSeconds));
        }

        // Only the verified tarball is opened, and only the Bun executable is taken out of it.
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        await using (var file = File.OpenRead(tgz))
        await using (var gz = new GZipStream(file, CompressionMode.Decompress))
        {
            var reader = new TarReader(gz);
            var found = false;
            while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
            {
                if (entry.Name != "package/" + build.Exe || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
                await entry.ExtractToFileAsync(exePath, overwrite: false, ct).ConfigureAwait(false); // keeps the executable mode on Unix
                found = true;
                break;
            }
            if (!found) throw new InvalidDataException($"{build.Package} has no {build.Exe}");
        }
        File.Delete(tgz);
    }

    private static string Tail(string text, int lines) =>
        string.Join('\n', text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines));

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Runs <paramref name="psi"/> with output captured; the process tree it started is killed on cancel or timeout.</summary>
    public static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception) { }
            // Our own child only; if it cannot be killed, do not wait forever for it.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(psi.FileName)} did not finish within {timeout.TotalMinutes:0} min");
        }
        process.WaitForExit(); // drains the redirected output
        lock (output) return new ProcessResult(process.ExitCode, output.ToString());
    }
}
