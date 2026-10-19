using System.Buffers.Binary;
using System.Text;
using Ombra.Server.Contracts;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Ombra.Server.Tests.Infra;

/// <summary>
/// A client device in tests: Ed25519 identity key (BouncyCastle — an implementation independent of ombra-core, so
/// signature inputs are cross-checked) and helpers producing the canonical envelope and hello signing inputs.
/// </summary>
public sealed class TestDevice
{
    private static readonly SecureRandom Random = new();
    private readonly Ed25519PrivateKeyParameters _key = new(Random);

    public TestDevice(Guid memberId, Guid? deviceId = null)
    {
        MemberId = memberId;
        DeviceId = deviceId ?? Guid.CreateVersion7();
        IdentityKey = _key.GeneratePublicKey().GetEncoded();
        MlsSignatureKey = new Ed25519PrivateKeyParameters(Random).GeneratePublicKey().GetEncoded();
    }

    public Guid MemberId { get; }
    public Guid DeviceId { get; }
    public byte[] IdentityKey { get; }
    public byte[] MlsSignatureKey { get; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }

    public byte[] Sign(byte[] message)
    {
        var s = new Ed25519Signer();
        s.Init(true, _key);
        s.BlockUpdate(message, 0, message.Length);
        return s.GenerateSignature();
    }

    /// <summary>"ombra/v1/envelope" ‖ 0x00 ‖ canonical CBOR envelope.</summary>
    public static byte[] EnvelopeSigningInput(byte[] encoded) => Concat(Encoding.ASCII.GetBytes("ombra/v1/envelope"), [0], encoded);

    /// <summary>"ombra/v1/hello" ‖ 0x00 ‖ serverId[16] ‖ nonce[32] ‖ deviceId[16] ‖ time u64 BE.</summary>
    public byte[] SignHello(Guid serverId, byte[] nonce, ulong time)
    {
        var t = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(t, time);
        return Sign(Concat(Encoding.ASCII.GetBytes("ombra/v1/hello"), [0], Ids.ToBytes(serverId), nonce, Ids.ToBytes(DeviceId), t));
    }

    public (byte[] Encoded, string Sig) Envelope(string kind, byte[] scope, CborMapWriter body, DateTimeOffset now, Guid? opId = null, ulong baseRevision = 0)
    {
        var env = new Envelope(1, opId ?? Guid.CreateVersion7(), DeviceId, scope, baseRevision, kind, body.Encode(), (ulong)now.ToUnixTimeMilliseconds());
        var encoded = env.Encode();
        return (encoded, Base64Url(Sign(EnvelopeSigningInput(encoded))));
    }

    /// <summary>
    /// DeviceCertificate (canonical CBOR, ombra-core format) for this device's keys under <paramref name="memberId"/>,
    /// signed by <paramref name="issuer"/> (default: self-issued root).
    /// </summary>
    public byte[] Certificate(Guid serverId, Guid memberId, ulong issuedAt, TestDevice? issuer = null)
    {
        issuer ??= this;
        CborMapWriter Tbs() => new CborMapWriter().Uuid("serverId", serverId).Uuid("memberId", memberId).Uuid("deviceId", DeviceId)
            .Bytes("identityKey", IdentityKey).Bytes("mlsSignatureKey", MlsSignatureKey).Uuid("issuerDeviceId", issuer.DeviceId)
            .Bytes("pairingNonce", Array.Empty<byte>()).UInt("issuedAt", issuedAt);
        var sig = issuer.Sign(Concat(Encoding.ASCII.GetBytes("ombra/v1/device-cert"), [0], Tbs().Encode()));
        return Tbs().Bytes("sig", sig).Encode();
    }

    public static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        var o = 0;
        foreach (var p in parts) { p.CopyTo(r, o); o += p.Length; }
        return r;
    }
}
