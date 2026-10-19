using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Ombra.Server.Contracts;

namespace Ombra.ClientCore.Teams;

/// <summary>
/// C3 attachments (SERVER_DESIGN §3.3): a resource's content (file, skill, snapshot) travels as a blob the server stores
/// as ciphertext. Each blob has a fresh 256-bit key that exists only inside the end-to-end encrypted item op
/// (<c>attachments[{blobRef, key}]</c>); the server sees size, SHA-256 of the ciphertext and the pair group it may hand
/// it to. Content is sealed in AES-256-GCM chunks of <see cref="ChunkBytes"/>: nonce = 4 zero bytes ‖ chunk index (BE),
/// AAD = "ombra-blob-v1" ‖ blobId ‖ index ‖ last-flag — chunks cannot be reordered, dropped, cut short or moved to
/// another blob. Download happens only for an item this member accepted, and checks size and SHA-256 of the plaintext
/// against the signed item body before anything is returned.
/// </summary>
public sealed partial class TeamItems
{
    public const int ChunkBytes = 64 * 1024;
    private const int TagBytes = 16;
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("ombra-blob-v1");

    public async Task<TeamBlob> UploadAsync(string teamId, string recipientMemberId, Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var plain = buffer.ToArray();
        var blobId = Guid.CreateVersion7();
        var key = RandomNumberGenerator.GetBytes(32);
        var sealedBytes = Seal(plain, key, blobId);
        var gid = await _client.EnsurePairGroupAsync(teamId, recipientMemberId, ct);
        var body = new BlobPutBody(blobId, (ulong)sealedBytes.Length, SHA256.HashData(sealedBytes));
        var env = _client.Server.SignEnvelope(EnvelopeKinds.BlobPut, Convert.FromHexString(gid), body.ToCbor());
        await _client.Server.PutEnvelopedAsync<BlobPutResponse>($"v1/blobs/{blobId:D}", env, sealedBytes, ct);
        return new TeamBlob(blobId.ToString("D"), (ulong)plain.Length, Convert.ToHexStringLower(SHA256.HashData(plain)), Convert.ToHexStringLower(key));
    }

    public async Task<byte[]> DownloadAsync(TeamItem item, CancellationToken ct = default)
    {
        var me = Me;
        if (item.RecipientMemberId != me) throw new InvalidOperationException("the item is not addressed to this member");
        if (item.Statuses.FirstOrDefault(s => s.RecipientMemberId == me)?.Status != TeamItemStatuses.Accepted)
            throw new InvalidOperationException("accept the item before its content is downloaded");
        var blobRef = item.Body?["blobRef"]?.GetValue<string>() ?? throw new InvalidDataException("the item has no blob");
        var key = TeamBlob.KeyOf(item, blobRef) ?? throw new InvalidDataException("the item carries no key for its blob");
        if (!Guid.TryParse(blobRef, out var blobId)) throw new InvalidDataException("bad blob reference");
        var sealedBytes = await _client.Server.GetAsync<byte[]>($"v1/blobs/{blobId:D}", ct);
        var plain = Open(sealedBytes, key, blobId);
        var size = item.Body?["size"]?.GetValue<ulong>();
        var sha = item.Body?["sha256"]?.GetValue<string>();
        if (size != (ulong)plain.Length || !string.Equals(sha, Convert.ToHexStringLower(SHA256.HashData(plain)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("the downloaded content does not match the shared item (size or SHA-256)");
        return plain;
    }

    /// <summary>Removes an own blob from the server (after the item that names it was withdrawn).</summary>
    public async Task DeleteBlobAsync(string teamId, string recipientMemberId, string blobRef, CancellationToken ct = default)
    {
        if (!Guid.TryParse(blobRef, out var blobId)) throw new InvalidDataException("bad blob reference");
        var gid = _client.PairGroupId(teamId, recipientMemberId) ?? throw new InvalidOperationException("no pair group with that member");
        await _client.Server.PostEnvelopeDeleteAsync($"v1/blobs/{blobId:D}", EnvelopeKinds.BlobDelete, Convert.FromHexString(gid), new BlobDeleteBody(blobId).ToCbor(), ct);
    }

    private static byte[] Aad(Guid blobId, long index, bool last)
    {
        var aad = new byte[Domain.Length + 16 + 8 + 1];
        Domain.CopyTo(aad, 0);
        blobId.ToByteArray(bigEndian: true).CopyTo(aad, Domain.Length);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(Domain.Length + 16), index);
        aad[^1] = last ? (byte)1 : (byte)0;
        return aad;
    }

    private static byte[] Nonce(long index)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), index);
        return nonce;
    }

    internal static byte[] Seal(ReadOnlySpan<byte> plain, byte[] key, Guid blobId)
    {
        var chunks = Math.Max(1, (plain.Length + ChunkBytes - 1) / ChunkBytes);
        var output = new byte[plain.Length + chunks * TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        for (var i = 0; i < chunks; i++)
        {
            var p = plain.Slice(i * ChunkBytes, Math.Min(ChunkBytes, plain.Length - i * ChunkBytes));
            var at = i * (ChunkBytes + TagBytes);
            aes.Encrypt(Nonce(i), p, output.AsSpan(at, p.Length), output.AsSpan(at + p.Length, TagBytes), Aad(blobId, i, i == chunks - 1));
        }
        return output;
    }

    internal static byte[] Open(ReadOnlySpan<byte> sealedBytes, byte[] key, Guid blobId)
    {
        if (sealedBytes.Length < TagBytes) throw new InvalidDataException("blob too short");
        var chunks = (sealedBytes.Length + ChunkBytes + TagBytes - 1) / (ChunkBytes + TagBytes);
        var output = new byte[sealedBytes.Length - chunks * TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        for (var i = 0; i < chunks; i++)
        {
            var at = i * (ChunkBytes + TagBytes);
            var len = Math.Min(ChunkBytes + TagBytes, sealedBytes.Length - at) - TagBytes;
            if (len < 0) throw new InvalidDataException("blob chunk too short");
            try
            {
                aes.Decrypt(Nonce(i), sealedBytes.Slice(at, len), sealedBytes.Slice(at + len, TagBytes), output.AsSpan(i * ChunkBytes, len), Aad(blobId, i, i == chunks - 1));
            }
            catch (AuthenticationTagMismatchException) { throw new InvalidDataException("the blob was altered, cut short or belongs to another item"); }
        }
        return output;
    }
}
