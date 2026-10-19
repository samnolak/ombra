using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ombra.App.Services.Runtime;

/// <summary>Installs the complete owned engine beside, never over, the pinned npm dependency tree.</summary>
internal static class RuntimeSourceArtifact
{
    internal const int MaxFiles = 8192;
    internal const long MaxFileBytes = 16L << 20;
    internal const long MaxSourceBytes = 128L << 20;
    private const int MaxManifestBytes = 4 << 20;

    internal static JsonDocument ReadManifest(Func<string, Stream> open)
    {
        using var stream = open("pack.json");
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (bytes.Length + count > MaxManifestBytes) throw new InvalidDataException("Runtime manifest exceeds its size limit.");
            bytes.Write(buffer, 0, count);
        }
        Verify(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)), RuntimePack.ManifestSha256, "pack.json");
        var manifest = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
        var root = manifest.RootElement;
        if (root.GetProperty("name").GetString() != RuntimePack.Name ||
            root.GetProperty("sourceArtifact").GetProperty("identity").GetString() != RuntimePack.SourceIdentity ||
            root.GetProperty("sourceArtifact").GetProperty("sha256").GetString() != RuntimePack.SourceSha256)
        {
            manifest.Dispose();
            throw new InvalidDataException("Runtime source artifact identity does not match this client.");
        }
        return manifest;
    }

    internal static async Task<JsonElement> ApplyAsync(string staging, CancellationToken ct, Func<string, Stream>? open = null)
    {
        if (!Path.GetFileName(staging).StartsWith(".staging-", StringComparison.Ordinal) ||
            !File.Exists(Path.Combine(staging, ".ombra-runtime-work")))
            throw new InvalidOperationException("Owned runtime source can only be installed in installer staging.");

        open ??= RuntimePack.OpenPackFile;
        using var manifest = ReadManifest(open);
        var artifact = manifest.RootElement.GetProperty("sourceArtifact");
        var engine = Path.Combine(staging, "omp", "engine");
        if (Directory.Exists(engine) || File.Exists(engine))
            throw new InvalidDataException("Unexpected existing owned runtime source directory.");
        var files = artifact.GetProperty("files");
        if (files.GetArrayLength() is 0 or > MaxFiles) throw new InvalidDataException("Invalid runtime source file count.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var digest = new StringBuilder();
        long total = 0;
        var buffer = new byte[81920];
        // Validate all resources before creating the owned tree; reuse a fixed-size streaming buffer.
        foreach (var file in files.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var relative = file.GetProperty("path").GetString()!;
            if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Contains(':') ||
                relative.Split('/').Any(p => p is "" or "." or "..") || !paths.Add(relative))
                throw new InvalidDataException($"Invalid runtime source path: {relative}");
            var size = file.GetProperty("size").GetInt64();
            if (size < 0 || size > MaxFileBytes || (total += size) > MaxSourceBytes)
                throw new InvalidDataException("Runtime source exceeds its size limit.");
            await CopySourceAsync(file, open, null, buffer, ct).ConfigureAwait(false);
            digest.Append(relative).Append('\0').Append(file.GetProperty("sha256").GetString()).Append('\n');
        }
        Verify(Encoding.UTF8.GetBytes(digest.ToString()), RuntimePack.SourceSha256, "source artifact");
        foreach (var file in files.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(engine, file.GetProperty("path").GetString()!);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopySourceAsync(file, open, destination, buffer, ct).ConfigureAwait(false);
        }
        return artifact.Clone();
    }

    private static async Task CopySourceAsync(JsonElement file, Func<string, Stream> open, Stream? destination,
        byte[] buffer, CancellationToken ct)
    {
        var relative = file.GetProperty("path").GetString()!;
        var size = file.GetProperty("size").GetInt64();
        await using var source = open("engine/" + relative);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long copied = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            copied += count;
            if (copied > size) throw new InvalidDataException($"Runtime source size mismatch: {relative}");
            hash.AppendData(buffer, 0, count);
            if (destination is not null)
                await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        if (copied != size) throw new InvalidDataException($"Runtime source size mismatch: {relative}");
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Runtime source SHA256 integrity mismatch: {relative}");
    }

    private static void Verify(ReadOnlySpan<byte> bytes, string expected, string name)
    {
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Runtime source SHA256 integrity mismatch: {name}");
    }
}
