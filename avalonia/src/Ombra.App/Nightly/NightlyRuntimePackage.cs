using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Ombra.App.Localization;
using Ombra.App.Services.Runtime;

namespace Ombra.App.Nightly;

/// <summary>
/// The Nightly runtime Ombra installs for itself (K1 delivery), pinned by <c>runtime/nightly/nightly-runtime.json</c>
/// (built into the client): the package's SHA-256, the Nightly version and build id inside it, and the embed protocol
/// it speaks. Until a signed release build exists, the package is a zip of the desktop repo's build
/// (<c>runtime/nightly/pack-nightly.py</c>: the dev <c>dist/Nightly.app</c> or <c>./mach package</c> output). It is
/// installed into <c>&lt;runtimes&gt;/nightly/&lt;version&gt;-&lt;build&gt;/Nightly.app</c>, only after the archive matches the pinned
/// hash and the app inside matches the pinned version; a new pin installs next to the old one, which is removed once
/// no runtime uses it (never under a live process).
/// </summary>
internal static class NightlyRuntimePackage
{
    /// <summary>A package file (zip) to install from, instead of <see cref="PackagesDirectory"/>.</summary>
    public const string PackageVariable = "OMBRA_NIGHTLY_PACKAGE";
    private const string Marker = "installed.json";
    private const string WorkMarker = ".ombra-nightly-installer";

    /// <param name="Archive">The package's file name (looked up in <see cref="PackagesDirectory"/>).</param>
    /// <param name="Url">Where to download it from, when the pin names a published package (http(s)); null: local only.</param>
    /// <param name="Cdhash">The code directory hash of the package's ad-hoc signed <c>Contents/MacOS/nightly</c>
    /// (<c>pack-nightly.py</c>): the only Nightly code Ombra trusts on its embed and team channels.</param>
    public sealed record Manifest(string Version, string BuildId, uint Protocol, string Sha256, long Size, string Archive, string? Url, string Cdhash)
    {
        public string DirectoryName => $"{Version}-{BuildId}";
    }

    /// <summary>Why the pinned runtime cannot be used yet (null: it is installed).</summary>
    public enum Problem { None, NotInstalled, UpdateRequired, PackageMissing, NoPin }

    public static Manifest? Pinned { get; } = Load();

    /// <summary>Install root: next to the owned engine packs (<see cref="RuntimePack.DefaultRoot"/>).</summary>
    public static string Root => Path.Combine(RuntimePack.DefaultRoot, "nightly");

    /// <summary>Where <c>pack-nightly.py --stage</c> puts packages for this machine's Ombra.</summary>
    public static string PackagesDirectory => Path.Combine(Root, "packages");

    /// <summary>A code directory hash as codesign prints it (CDHash=): 40 lowercase hex digits.</summary>
    public static bool IsCdhash(string? s) => s is { Length: 40 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static Manifest? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string n) => r.GetProperty(n).GetString() ?? throw new InvalidDataException(n);
            var sha = S("sha256").ToLowerInvariant();
            if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return null;
            var cdhash = S("cdhash");
            if (!IsCdhash(cdhash)) return null;
            return new Manifest(S("version"), S("buildId"), r.GetProperty("protocol").GetUInt32(), sha, r.GetProperty("size").GetInt64(), S("archive"),
                r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null, cdhash);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException or FormatException)
        {
            return null;
        }
    }

    private static Manifest? Load()
    {
        using var s = typeof(NightlyRuntimePackage).Assembly.GetManifestResourceStream("nightly-runtime.json");
        if (s is null) return null;
        using var reader = new StreamReader(s);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>The pinned version's app, when installed and intact; else why not.</summary>
    public static (string? App, Problem Problem) Find(Manifest? pin = null, string? root = null)
    {
        pin ??= Pinned;
        root ??= Root;
        if (pin is null) return (null, Problem.NoPin);
        var dir = Path.Combine(root, pin.DirectoryName);
        if (Installed(dir, pin) is { } app) return (app, Problem.None);
        var older = Directory.Exists(root) && Directory.EnumerateDirectories(root).Any(d => File.Exists(Path.Combine(d, Marker)));
        if (FindPackage(pin, root) is null && pin.Url is null) return (null, Problem.PackageMissing);
        return (null, older ? Problem.UpdateRequired : Problem.NotInstalled);
    }

    private static string? Installed(string dir, Manifest pin)
    {
        try
        {
            var marker = Path.Combine(dir, Marker);
            if (!File.Exists(marker)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            if (doc.RootElement.GetProperty("sha256").GetString() != pin.Sha256) return null;
            var app = Path.Combine(dir, "Nightly.app");
            var (version, build) = ReadAppIni(app);
            return version == pin.Version && build == pin.BuildId && File.Exists(Path.Combine(app, "Contents", "MacOS", "nightly")) ? app : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The package file to install from: <see cref="PackageVariable"/>, else <see cref="PackagesDirectory"/>/archive.</summary>
    public static string? FindPackage(Manifest pin, string? root = null)
    {
        if (Environment.GetEnvironmentVariable(PackageVariable) is { Length: > 0 } p && File.Exists(p)) return p;
        var staged = Path.Combine(root ?? Root, "packages", pin.Archive);
        return File.Exists(staged) ? staged : null;
    }

    /// <summary>Version and BuildID of a Nightly.app (<c>Contents/Resources/application.ini</c>).</summary>
    public static (string? Version, string? BuildId) ReadAppIni(string app)
    {
        var ini = Path.Combine(app, "Contents", "Resources", "application.ini");
        if (!File.Exists(ini)) return (null, null);
        string? version = null, build = null, section = null;
        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) section = line;
            else if (section == "[App]" && line.StartsWith("Version=", StringComparison.Ordinal)) version = line[8..];
            else if (section == "[App]" && line.StartsWith("BuildID=", StringComparison.Ordinal)) build = line[8..];
        }
        return (version, build);
    }

    /// <summary>
    /// Installs the pinned package: the archive (local, or downloaded from the pin's URL) must hash to the pin, its app
    /// must carry the pinned version and build, and the pin's protocol must be the one this Ombra speaks. Then the older
    /// versions no runtime uses (<paramref name="inUse"/>: an app path a live runtime runs) are removed.
    /// </summary>
    /// <exception cref="InvalidDataException">The package or the app in it is not the pinned one.</exception>
    /// <exception cref="IOException">No package, another install running, or unpacking failed.</exception>
    public static async Task<string> InstallAsync(IProgress<string>? progress, Func<string, bool> inUse, CancellationToken ct,
        Manifest? pin = null, string? root = null, HttpClient? http = null)
    {
        pin ??= Pinned ?? throw new IOException(Loc.T("This Ombra build pins no Nightly runtime."));
        root ??= Root;
        if (pin.Protocol != NightlyProtocol.Version)
            throw new InvalidDataException(Loc.F("The pinned Nightly speaks embed protocol {0}; Ombra needs {1}.", pin.Protocol, NightlyProtocol.Version));
        Directory.CreateDirectory(root);
        FileStream installLock;
        try { installLock = new FileStream(Path.Combine(root, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException(Loc.T("Another installation of Nightly is running; try again when it has finished.")); }
        using var _ = installLock;
        var target = Path.Combine(root, pin.DirectoryName);
        if (Installed(target, pin) is { } already)
        {
            RemoveOlder(root, pin, inUse);
            return already;
        }
        foreach (var stale in Directory.EnumerateDirectories(root)
                     .Where(d => Path.GetFileName(d).StartsWith(".staging-", StringComparison.Ordinal) && File.Exists(Path.Combine(d, WorkMarker))).ToList())
            TryDelete(stale);
        var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, WorkMarker), "created by Ombra's Nightly installer; safe to delete", ct).ConfigureAwait(false);
        try
        {
            var package = FindPackage(pin, root);
            if (package is null)
            {
                if (pin.Url is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    throw new IOException(Loc.F("The Nightly runtime package {0} was not found. Build it with runtime/nightly/pack-nightly.py, or set {1}.", pin.Archive, PackageVariable));
                progress?.Report(Loc.F("Downloading Nightly {0}", pin.Version));
                package = Path.Combine(staging, pin.Archive);
                using var client = http is null ? new HttpClient() : null;
                await using (var response = await (http ?? client!).GetStreamAsync(uri, ct).ConfigureAwait(false))
                await using (var file = File.Create(package))
                    await CopyWithProgressAsync(response, file, pin, progress, ct).ConfigureAwait(false);
            }
            progress?.Report(Loc.T("Checking the Nightly package"));
            string sha;
            await using (var stream = File.OpenRead(package))
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            if (sha != pin.Sha256)
                throw new InvalidDataException(Loc.F("The Nightly package {0} is not the pinned one (SHA-256 {1}, expected {2}).", Path.GetFileName(package), sha, pin.Sha256));

            progress?.Report(Loc.F("Installing Nightly {0}", pin.Version));
            var unpack = new ProcessStartInfo("/usr/bin/ditto") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-x", "-k", package, staging }) unpack.ArgumentList.Add(a);
            var result = await RuntimeInstaller.RunProcessAsync(unpack, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new IOException(Loc.F("Unpacking the Nightly package failed: {0}", result.Output.Trim()));
            var app = Path.Combine(staging, "Nightly.app");
            var (version, build) = ReadAppIni(app);
            if (version != pin.Version || build != pin.BuildId)
                throw new InvalidDataException(Loc.F("The Nightly package holds version {0} ({1}); the pin says {2} ({3}).", version ?? "?", build ?? "?", pin.Version, pin.BuildId));
            if (Path.Combine(staging, pin.Archive) is var downloaded && File.Exists(downloaded)) File.Delete(downloaded);
            File.Delete(Path.Combine(staging, WorkMarker));
            await File.WriteAllTextAsync(Path.Combine(staging, Marker), JsonSerializer.Serialize(new
            {
                version = pin.Version, buildId = pin.BuildId, protocol = pin.Protocol, sha256 = pin.Sha256, installedAt = DateTimeOffset.UtcNow,
            }), ct).ConfigureAwait(false);
            if (Directory.Exists(target)) TryDelete(target); // an incomplete earlier copy (its marker did not match)
            Directory.Move(staging, target);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
        RemoveOlder(root, pin, inUse);
        return Path.Combine(target, "Nightly.app");
    }

    /// <summary>Copies the download, saying how much of the pin's size has arrived (at most a few times a second).</summary>
    private static async Task CopyWithProgressAsync(Stream from, Stream to, Manifest pin, IProgress<string>? progress, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long done = 0;
        var last = System.Diagnostics.Stopwatch.StartNew();
        int read;
        while ((read = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (progress is null || last.ElapsedMilliseconds < 250) continue;
            last.Restart();
            progress.Report(Loc.F("Downloading Nightly {0}: {1:0} / {2:0} MB", pin.Version, done / 1_000_000.0, pin.Size / 1_000_000.0));
        }
    }

    /// <summary>Removes installed versions other than the pin's that no runtime runs; returns how many.</summary>
    public static int RemoveOlder(string root, Manifest pin, Func<string, bool> inUse)
    {
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(root).ToList())
        {
            var name = Path.GetFileName(dir);
            if (name == pin.DirectoryName || name.StartsWith('.') || name == "packages" || !File.Exists(Path.Combine(dir, Marker))) continue;
            if (inUse(Path.Combine(dir, "Nightly.app"))) continue;
            TryDelete(dir);
            if (!Directory.Exists(dir)) removed++;
        }
        return removed;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
