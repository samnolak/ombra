using System.Formats.Cbor;

namespace Ombra.Server.Contracts;

/// <summary>
/// Signed request wrapper (SERVER_DESIGN §6.1). Wire form: deterministic CBOR (RFC 8949 §4.2.1)
/// map with text keys <c>v, opId, deviceId, scope, baseRevision, kind, body, ts</c>.
/// The signature (header <see cref="Headers.Signature"/>) is produced by the device identityKey over
/// the domain-separated encoded bytes; the exact signing input is defined by ombra-core
/// (<c>OmbraPublic.VerifyEnvelope</c>), not here.
/// </summary>
public sealed record Envelope(
    int V,
    Guid OpId,
    Guid DeviceId,
    byte[] Scope,
    ulong BaseRevision,
    string Kind,
    ReadOnlyMemory<byte> Body,
    ulong Ts)
{
    public const int CurrentVersion = 1;
    public const int MaxEncodedBytes = 96 * 1024;

    public byte[] Encode()
    {
        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        w.WriteStartMap(8);
        w.WriteTextString("v"); w.WriteUInt32((uint)V);
        w.WriteTextString("opId"); w.WriteByteString(Ids.ToBytes(OpId));
        w.WriteTextString("deviceId"); w.WriteByteString(Ids.ToBytes(DeviceId));
        w.WriteTextString("scope"); w.WriteByteString(Scope);
        w.WriteTextString("baseRevision"); w.WriteUInt64(BaseRevision);
        w.WriteTextString("kind"); w.WriteTextString(Kind);
        w.WriteTextString("body"); w.WriteEncodedValue(Body.Span);
        w.WriteTextString("ts"); w.WriteUInt64(Ts);
        w.WriteEndMap();
        return w.Encode();
    }

    /// <summary>Strict decode: canonical encoding, exactly the eight known keys, no trailing bytes.</summary>
    public static Envelope Decode(ReadOnlyMemory<byte> encoded)
    {
        if (encoded.Length > MaxEncodedBytes)
            throw new WireFormatException("envelope too large");
        try
        {
            var map = CborMap.Read(encoded);
            map.RequireExactly("v", "opId", "deviceId", "scope", "baseRevision", "kind", "body", "ts");
            var env = new Envelope(
                V: (int)map.UInt("v"),
                OpId: map.Uuid("opId"),
                DeviceId: map.Uuid("deviceId"),
                Scope: map.Bytes("scope"),
                BaseRevision: map.UInt("baseRevision"),
                Kind: map.Text("kind"),
                Body: map.Raw("body"),
                Ts: map.UInt("ts"));
            if (env.Scope.Length is not (16 or 32))
                throw new WireFormatException("scope must be 16 or 32 bytes");
            return env;
        }
        catch (CborContentException e)
        {
            throw new WireFormatException("envelope is not canonical CBOR: " + e.Message);
        }
        catch (InvalidOperationException e)
        {
            throw new WireFormatException("envelope has unexpected CBOR structure: " + e.Message);
        }
    }

    public CborMap BodyMap() => CborMap.Read(Body);
}

public static class EnvelopeKinds
{
    public const string OpPut = "op.put";
    public const string BlobPut = "blob.put";
    public const string BlobDelete = "blob.delete";
    public const string TransferCreate = "transfer.create";
    public const string InviteCreate = "invite.create";
    public const string JoinRequest = "join.request";
    public const string QuarantineRelease = "team.quarantine.release";
    /// <summary>§2.3 п.5 / §5.4: DeviceRevocation of an own device, scope = memberId.</summary>
    public const string DeviceRevoke = "device.revoke";
    /// <summary>§2.3: not sent as an operation; the issuer signs which teams a newly paired device may trust (scope = its deviceId).</summary>
    public const string DeviceGrant = "device.grant";
}

public static class Headers
{
    /// <summary>Base64url Ed25519 signature over the envelope (format: ombra-core).</summary>
    public const string Signature = "X-Ombra-Sig";
    /// <summary>Base64url envelope for streaming uploads where the HTTP body is ciphertext.</summary>
    public const string Envelope = "X-Ombra-Envelope";
    public const string EnvelopeContentType = "application/ombra-envelope+cbor";
    public const string MlsContentType = "application/mls-message";
}

public sealed class WireFormatException(string message) : Exception(message);
