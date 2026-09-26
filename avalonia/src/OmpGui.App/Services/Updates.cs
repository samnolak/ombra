using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmpGui.App.Services;

/// <summary>One package in an update manifest.</summary>
public sealed record UpdateAsset(string File, string Url, string Sha256, long Size);

/// <summary>The omp runtime a version brings (its pinned pack): omp moves with the client, never on its own.</summary>
public sealed record UpdateRuntime(string Pack, string Omp, string Bun);

/// <summary>
/// <c>update.json</c> published with a release (tools/release/make-manifest.py), signed next to it
/// (<c>update.json.sig</c>, tools/release/sign-manifest.sh). Schema 2 adds the runtime the version brings.
/// </summary>
public sealed record UpdateManifest(int Schema, string Version, string? Notes, Dictionary<string, UpdateAsset> Assets, UpdateRuntime? Runtime = null);

public sealed record UpdateCheck(bool Available, string Current, string? Latest, string? Notes, UpdateAsset? Asset, string Message,
    UpdateRuntime? Runtime = null)
{
    /// <summary>The version brings another omp than the one this client runs: it installs itself after the update.</summary>
    public bool BringsNewOmp => Runtime is { } r && r.Pack != OmpGui.App.Services.Runtime.RuntimePack.Name;
}

/// <summary>
/// Asks the update feed whether a newer version exists and downloads this platform's package, verified by size and
/// SHA-256 against the manifest. The manifest itself is trusted only with a valid signature from the release key built
/// into the client (ECDSA P-256, SHA-256): the feed and the packages are only a place to fetch from. HTTPS, except for
/// a feed on this computer (tests). Installing is <see cref="UpdateInstaller"/>'s part.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string feedUrl, string currentVersion, string rid, string? publicKey = null)
{
    /// <summary>Where the releases of this client publish their manifest.</summary>
    public const string DefaultFeed = "https://github.com/samnolak/oh-my-pi-gui/releases/latest/download/update.json";
    private const long MaxPackageBytes = 1L << 30;

    /// <summary>
    /// The release key's public half (SubjectPublicKeyInfo, base64), from packaging/update-signing.pub built into the
    /// client. OMPGUI_UPDATE_PUBLIC_KEY replaces it (the update E2E signs with a throwaway key).
    /// </summary>
    public static string? BuiltInPublicKey()
    {
        if (Environment.GetEnvironmentVariable("OMPGUI_UPDATE_PUBLIC_KEY") is { Length: > 0 } k) return k.Trim();
        using var s = typeof(UpdateChecker).Assembly.GetManifestResourceStream("update-signing.pub");
        return s is null ? null : new StreamReader(s).ReadToEnd().Trim();
    }

    public string Current { get; } = currentVersion;
    public string Rid { get; } = rid;
    public string FeedUrl => feedUrl;

    /// <summary>How long the feed may stay silent (the whole check, or between two reads of a download) before giving up.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>This build's version (<c>InformationalVersion</c> without build metadata).</summary>
    public static string ThisVersion()
    {
        var v = typeof(UpdateChecker).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";
        return v.Split('+')[0];
    }

    public static string ThisRid() => Platform.RuntimePlatform.PackageRid();

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct)
    {
        RequireHttps(feedUrl, "update feed");
        if (publicKey is not { Length: > 0 }) throw new InvalidDataException("this build has no release key to verify updates with");
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            return await CheckCoreAsync(stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"the update server did not answer within {StallTimeout.TotalSeconds:0} s");
        }
    }

    private async Task<UpdateCheck> CheckCoreAsync(CancellationToken ct)
    {
        var (status, bytes) = await FetchSmallAsync(feedUrl, 1 << 20, ct).ConfigureAwait(false);
        if (bytes is null)
            return new UpdateCheck(false, Current, null, null, null, status == 404
                ? "No release is published yet." : $"No update information available (HTTP {status}).");
        var (sigStatus, sig) = await FetchSmallAsync(feedUrl + ".sig", 16 << 10, ct).ConfigureAwait(false);
        if (sig is null) throw new InvalidDataException($"the update manifest is not signed (no {Path.GetFileName(new Uri(feedUrl).AbsolutePath)}.sig, HTTP {sigStatus})");
        if (!Verify(bytes, sig, publicKey!)) throw new InvalidDataException("the update manifest's signature does not match the release key: not trusted");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json) ?? throw new InvalidDataException("empty update manifest");
        if (manifest.Schema is not (1 or 2) || string.IsNullOrWhiteSpace(manifest.Version)) throw new InvalidDataException("unknown update manifest format");
        if (CompareVersions(manifest.Version, Current) <= 0)
            return new UpdateCheck(false, Current, manifest.Version, manifest.Notes, null, $"OMP GUI {Current} is up to date.", manifest.Runtime);
        if (manifest.Assets is null || !manifest.Assets.TryGetValue(Rid, out var asset) || asset is null)
            return new UpdateCheck(false, Current, manifest.Version, manifest.Notes, null, $"Version {manifest.Version} exists, but not for this platform ({Rid}).", manifest.Runtime);
        Validate(asset);
        return new UpdateCheck(true, Current, manifest.Version, manifest.Notes, asset, $"Version {manifest.Version} is available (you have {Current}).", manifest.Runtime);
    }

    /// <summary>A small file, read to at most <paramref name="max"/> bytes; null bytes when the server said no.</summary>
    private async Task<(int Status, byte[]? Bytes)> FetchSmallAsync(string url, int max, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return ((int)response.StatusCode, null);
        var bytes = new MemoryStream();
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[16384];
        int n;
        while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + n > max) throw new InvalidDataException($"{Path.GetFileName(new Uri(url).AbsolutePath)} is too large");
            bytes.Write(buffer, 0, n);
        }
        return ((int)response.StatusCode, bytes.ToArray());
    }

    /// <summary>
    /// The manifest's exact bytes against its signature: base64 of a DER ECDSA signature over SHA-256, as
    /// <c>openssl dgst -sha256 -sign</c> writes it; the key is a P-256 SubjectPublicKeyInfo in base64.
    /// </summary>
    public static bool Verify(byte[] manifest, byte[] signatureFile, string publicKeyBase64)
    {
        try
        {
            var signature = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(signatureFile).Trim());
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            if (ecdsa.KeySize != 256) return false;
            return ecdsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Downloads <paramref name="asset"/> into <paramref name="folder"/>; returns the verified file.</summary>
    /// <exception cref="InvalidDataException">Size or SHA-256 differ from the manifest (nothing is kept).</exception>
    public async Task<string> DownloadAsync(UpdateAsset asset, string folder, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        Validate(asset);
        Directory.CreateDirectory(folder);
        var target = FreeName(folder, asset.File);
        var part = target + ".part";
        // Restarted before every read: a server that stops sending fails the download instead of hanging it.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long written = 0;
                int n;
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    n = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    stall.CancelAfter(Timeout.InfiniteTimeSpan); // only the server's silence counts, not our disk
                    if (n <= 0) break;
                    written += n;
                    if (written > asset.Size) throw new InvalidDataException($"{asset.File} is larger than the manifest says ({asset.Size} bytes)");
                    sha.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    progress?.Report((written, asset.Size));
                }
                if (written != asset.Size) throw new InvalidDataException($"{asset.File}: {written} bytes, the manifest says {asset.Size}");
                var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
                if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{asset.File}: SHA-256 {hash} does not match the manifest");
            }
            File.Move(part, target, overwrite: false);
            return target;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"the update server sent nothing for {StallTimeout.TotalSeconds:0} s");
        }
        finally
        {
            if (File.Exists(part)) File.Delete(part);
        }
    }

    /// <summary><paramref name="name"/> in <paramref name="folder"/>, or "name (2).ext"… when a file of that name exists: never replaces a user's file.</summary>
    private static string FreeName(string folder, string name)
    {
        var target = Path.Combine(folder, name);
        var ext = name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz" : Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var i = 2; File.Exists(target) || File.Exists(target + ".part"); i++) target = Path.Combine(folder, $"{stem} ({i}){ext}");
        return target;
    }

    private static void Validate(UpdateAsset? asset)
    {
        if (asset is null || asset.File is null || asset.Url is null || asset.Sha256 is null)
            throw new InvalidDataException("the update manifest lists a package without file, url or sha256");
        RequireHttps(asset.Url, "package");
        if (string.IsNullOrWhiteSpace(asset.File) || asset.File != Path.GetFileName(asset.File) || asset.File.Contains("..", StringComparison.Ordinal)
            || asset.File.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || asset.File.Contains('/') || asset.File.Contains('\\'))
            throw new InvalidDataException($"unsafe package file name \"{asset.File}\"");
        if (asset.Size <= 0 || asset.Size > MaxPackageBytes) throw new InvalidDataException($"implausible package size {asset.Size}");
        if (asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("the package has no valid SHA-256");
    }

    private static void RequireHttps(string url, string what)
    {
        // Plain http only on this computer (the update E2E serves its feed from 127.0.0.1); the signature is what is trusted
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !(u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp && u.IsLoopback))
            throw new InvalidDataException($"the {what} address must be https ({url})");
    }

    /// <summary>SemVer 2 precedence (build metadata ignored); numeric identifiers compare as numbers.</summary>
    public static int CompareVersions(string a, string b)
    {
        static (string[] Core, string[] Pre) Split(string v)
        {
            v = v.Split('+')[0].TrimStart('v', 'V');
            var dash = v.IndexOf('-');
            var core = (dash < 0 ? v : v[..dash]).Split('.');
            return (core, dash < 0 ? [] : v[(dash + 1)..].Split('.'));
        }
        static int Part(string x, string y) =>
            long.TryParse(x, out var nx) && long.TryParse(y, out var ny) ? nx.CompareTo(ny)
            : long.TryParse(x, out _) ? -1 : long.TryParse(y, out _) ? 1 : string.CompareOrdinal(x, y);

        var (ca, pa) = Split(a);
        var (cb, pb) = Split(b);
        for (var i = 0; i < Math.Max(ca.Length, cb.Length); i++)
        {
            var c = Part(i < ca.Length ? ca[i] : "0", i < cb.Length ? cb[i] : "0");
            if (c != 0) return Math.Sign(c);
        }
        if (pa.Length == 0 || pb.Length == 0) return pa.Length == pb.Length ? 0 : pa.Length == 0 ? 1 : -1; // a release outranks its pre-releases
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var c = Part(pa[i], pb[i]);
            if (c != 0) return Math.Sign(c);
        }
        return pa.Length.CompareTo(pb.Length);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
}
