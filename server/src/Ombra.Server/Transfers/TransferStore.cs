using Microsoft.Extensions.Options;

namespace Ombra.Server.Transfers;

/// <summary>
/// On-disk ciphertext of transfers (<c>&lt;data&gt;/transfers/&lt;transferId&gt;/&lt;index&gt;</c>) and blobs
/// (<c>&lt;data&gt;/blobs/&lt;blobId&gt;</c>). Paths are built only from parsed GUIDs and integers.
/// Writes go to a temp file and are renamed into place.
/// </summary>
public sealed class TransferStore(IOptions<OmbraOptions> options)
{
    private readonly OmbraOptions _o = options.Value;

    public string TransferDir(Guid id) => Path.Combine(_o.TransferDir, id.ToString("N"));
    public string ChunkPath(Guid id, int index) => Path.Combine(TransferDir(id), index.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public string BlobPath(Guid id) => Path.Combine(_o.BlobDir, id.ToString("N"));

    public void DeleteFiles(Guid transferId)
    {
        var dir = TransferDir(transferId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    public void DeleteBlob(Guid blobId)
    {
        var p = BlobPath(blobId);
        if (File.Exists(p)) File.Delete(p);
    }

    /// <summary>Streams the body into a temp file in <paramref name="dir"/>; enforces the byte limit while copying.</summary>
    public static async Task<(string TempPath, long Length, byte[] Sha256)> SpoolAsync(Stream body, string dir, long max, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, ".tmp-" + Guid.NewGuid().ToString("N"));
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        long total = 0;
        try
        {
            await using var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buf = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buf, ct)) > 0)
            {
                total += n;
                if (total > max) throw new ApiException(413, Contracts.Reasons.TooLarge);
                sha.AppendData(buf, 0, n);
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
            }
            await fs.FlushAsync(ct);
            fs.Flush(flushToDisk: true);
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
        return (tmp, total, sha.GetHashAndReset());
    }
}
