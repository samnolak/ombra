using System.Security.Cryptography;

namespace OmpGui.App.Services.Speech;

/// <summary>One file of the pinned speech model.</summary>
/// <param name="Sha256">LFS SHA-256 (lowercase hex); null for small non-LFS files (the size is checked instead).</param>
public sealed record SpeechModelFile(string Role, string Name, long Bytes, string? Sha256);

/// <summary>
/// On-device dictation model: NVIDIA Parakeet TDT 0.6B v3 (int8) for sherpa-onnx, the same engine and model as omp's
/// own dictation (stt.modelName: parakeet). Pinned to a repository revision; every file is checked against its
/// size and SHA-256 after download. Downloaded only when the user asks for it.
/// </summary>
public static class SpeechModel
{
    public const string Id = "parakeet-tdt-0.6b-v3-int8";
    public const string Repo = "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";
    public const string Revision = "2bda32ec70b097a55adaa07d9a7173915b43cc78";
    public const string ModelType = "nemo_transducer";
    public const int SampleRate = 16_000;

    public static readonly IReadOnlyList<SpeechModelFile> Files =
    [
        new("encoder", "encoder.int8.onnx", 652_184_281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
        new("decoder", "decoder.int8.onnx", 11_845_275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
        new("joiner", "joiner.int8.onnx", 6_355_277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
        new("tokens", "tokens.txt", 93_939, null),
    ];

    public static long TotalBytes => Files.Sum(f => f.Bytes);

    public static Uri UrlOf(SpeechModelFile f, string baseUrl = "https://huggingface.co") =>
        new($"{baseUrl.TrimEnd('/')}/{Repo}/resolve/{Revision}/{f.Name}");

    /// <summary>Machine-level cache (not the omp profile, not the client settings); OMPGUI_STT_DIR overrides the root.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetEnvironmentVariable("OMPGUI_STT_DIR") is { Length: > 0 } root ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "OmpGui", "speech-models"), Id);

    /// <summary>All files present with the pinned sizes (hashes were checked when they were written).</summary>
    public static bool IsInstalled(string directory) =>
        Files.All(f => new FileInfo(Path.Combine(directory, f.Name)) is { Exists: true } fi && fi.Length == f.Bytes);

    public static string PathOf(string directory, string role) => Path.Combine(directory, Files.Single(f => f.Role == role).Name);
}

/// <summary>Downloads the pinned model files and admits a file only when its size and hash match.</summary>
public sealed class SpeechModelDownloader(HttpClient http, string directory, string baseUrl = "https://huggingface.co", IReadOnlyList<SpeechModelFile>? files = null)
{
    private readonly IReadOnlyList<SpeechModelFile> _files = files ?? SpeechModel.Files;

    /// <summary>Downloads what is missing. Reports bytes done / total. A file that fails its check is deleted.</summary>
    /// <exception cref="InvalidDataException">A file did not match its pinned size or SHA-256.</exception>
    public async Task DownloadAsync(IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        long done = 0;
        var total = _files.Sum(f => f.Bytes);
        foreach (var file in _files)
        {
            var target = Path.Combine(directory, file.Name);
            if (new FileInfo(target) is { Exists: true } existing && existing.Length == file.Bytes)
            {
                done += file.Bytes;
                progress?.Report((done, total));
                continue;
            }
            var part = target + ".part";
            try
            {
                using var response = await http.GetAsync(SpeechModel.UrlOf(file, baseUrl), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[1 << 16];
                    long written = 0;
                    int n;
                    while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        written += n;
                        if (written > file.Bytes) throw new InvalidDataException($"{file.Name} is larger than pinned ({file.Bytes} bytes)");
                        sha.AppendData(buffer, 0, n);
                        await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        progress?.Report((done + written, total));
                    }
                    if (written != file.Bytes) throw new InvalidDataException($"{file.Name}: {written} bytes, pinned {file.Bytes}");
                    var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
                    if (file.Sha256 is { } expected && hash != expected)
                        throw new InvalidDataException($"{file.Name}: SHA-256 {hash} does not match the pinned {expected}");
                }
                File.Move(part, target, overwrite: true);
                done += file.Bytes;
            }
            finally
            {
                if (File.Exists(part)) File.Delete(part);
            }
        }
    }
}
