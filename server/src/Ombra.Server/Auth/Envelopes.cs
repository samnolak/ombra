using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Core.Interop;
using Ombra.Server.Contracts;
using Ombra.Server.Data;

namespace Ombra.Server.Auth;

public sealed record VerifiedEnvelope(Envelope Envelope, byte[] Encoded, byte[] Signature, byte[] RequestHash);

/// <summary>
/// Reads and verifies signed Envelopes (§6.1). Canonical-CBOR and signature checks are done by ombra-core
/// (<see cref="OmbraPublic.VerifyEnvelope"/>); this class enforces the server-side envelope rules:
/// version, signer = token device, timestamp window, expected kind.
/// </summary>
public sealed class Envelopes(TimeProvider clock, IOptions<OmbraOptions> options)
{
    private readonly LimitsOptions _limits = options.Value.Limits;

    public static async Task<byte[]> ReadBodyAsync(HttpRequest request, int max)
    {
        if (request.ContentLength > max) throw new ApiException(413, Reasons.TooLarge);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await request.Body.ReadAsync(buf)) > 0)
        {
            if (ms.Length + n > max) throw new ApiException(413, Reasons.TooLarge);
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>Envelope in the HTTP body (<see cref="Headers.EnvelopeContentType"/>).</summary>
    public async Task<VerifiedEnvelope> FromBodyAsync(HttpRequest request, DeviceIdentity signer, string kind)
    {
        var bytes = await ReadBodyAsync(request, Envelope.MaxEncodedBytes);
        return Verify(bytes, Signature(request), signer, kind);
    }

    /// <summary>Envelope in <see cref="Headers.Envelope"/> (streaming uploads: HTTP body is ciphertext).</summary>
    public VerifiedEnvelope FromHeader(HttpRequest request, DeviceIdentity signer, string kind)
    {
        var header = request.Headers[Headers.Envelope].ToString();
        if (header.Length == 0 || header.Length > Envelope.MaxEncodedBytes * 2) throw ApiException.Bad("missing envelope header");
        return Verify(FromBase64Url(header), Signature(request), signer, kind);
    }

    public static byte[] Signature(HttpRequest request)
    {
        var sig = FromBase64Url(request.Headers[Headers.Signature].ToString());
        if (sig.Length != 64) throw new ApiException(401, Reasons.BadSignature, "X-Ombra-Sig must be 64 bytes");
        return sig;
    }

    public VerifiedEnvelope Verify(byte[] encoded, byte[] sig, DeviceIdentity signer, string kind) =>
        Verify(encoded, sig, signer.DeviceId, signer.IdentityKey, kind);

    /// <summary>Verification with an explicit key (JoinRequest: the key travels in the body, proof of possession).</summary>
    public VerifiedEnvelope Verify(byte[] encoded, byte[] sig, Guid expectedDevice, byte[] identityKey, string kind)
    {
        if (!OmbraPublic.VerifyEnvelope(encoded, sig, identityKey))
            throw new ApiException(401, Reasons.BadSignature);
        Envelope env;
        try { env = Envelope.Decode(encoded); }
        catch (WireFormatException e) { throw ApiException.Bad(e.Message); }
        if (env.V != Envelope.CurrentVersion) throw new ApiException(426, Reasons.UpgradeRequired, "envelope v");
        if (env.DeviceId != expectedDevice) throw new ApiException(401, Reasons.BadSignature, "envelope deviceId is not the authenticated device");
        if (env.Kind != kind) throw ApiException.Bad($"expected kind {kind}");
        var now = (ulong)clock.GetUtcNow().ToUnixTimeMilliseconds();
        var skew = (ulong)_limits.ClockSkewSeconds * 1000;
        if (env.Ts + skew < now || env.Ts > now + skew) throw ApiException.Bad("envelope ts outside the accepted window");
        return new VerifiedEnvelope(env, encoded, sig, SHA256.HashData(encoded));
    }

    public static byte[] FromBase64Url(string s)
    {
        if (s.Length == 0) return [];
        var t = s.Replace('-', '+').Replace('_', '/');
        t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(t); }
        catch (FormatException) { throw ApiException.Bad("bad base64url"); }
    }
}

/// <summary>
/// Envelope dedupe by (deviceId, opId) with the stored result (§6.1). A claim row is inserted at the start of the
/// transaction; a concurrent duplicate blocks on it and then reads the stored result. A failed operation rolls the
/// claim back, so only successful results are replayed.
/// </summary>
public static class EnvelopeDedupe
{
    public sealed record Stored(int Status, JsonElement Response);

    public static async Task<Stored?> ClaimAsync(NpgsqlConnection conn, NpgsqlTransaction tx, VerifiedEnvelope env, DateTimeOffset now)
    {
        var inserted = await conn.ExecAsync(
            "INSERT INTO envelope_results (device_id, op_id, request_hash, status, response, created_at) " +
            "VALUES (@d, @o, @h, 0, '{}'::jsonb, @now) ON CONFLICT (device_id, op_id) DO NOTHING", tx,
            ("d", env.Envelope.DeviceId), ("o", env.Envelope.OpId), ("h", env.RequestHash), ("now", now));
        if (inserted == 1) return null;
        var rows = await conn.ListAsync(
            "SELECT request_hash, status, response::text FROM envelope_results WHERE device_id = @d AND op_id = @o", tx,
            r => (Hash: r.Bytes(0), Status: r.GetInt32(1), Json: r.GetString(2)),
            ("d", env.Envelope.DeviceId), ("o", env.Envelope.OpId));
        var (hash, status, json) = rows[0];
        if (!hash.AsSpan().SequenceEqual(env.RequestHash) || status == 0)
            throw ApiException.Conflict(Reasons.OpIdReused);
        return new Stored(status, JsonDocument.Parse(json).RootElement.Clone());
    }

    public static Task StoreAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, VerifiedEnvelope env, int status, T response) =>
        conn.ExecAsync(
            "UPDATE envelope_results SET status = @s, response = @r::jsonb WHERE device_id = @d AND op_id = @o", tx,
            ("s", status), ("r", JsonSerializer.Serialize(response, JsonDefaults.Options)), ("d", env.Envelope.DeviceId), ("o", env.Envelope.OpId));

    public static T Read<T>(Stored stored) => stored.Response.Deserialize<T>(JsonDefaults.Options)!;
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
