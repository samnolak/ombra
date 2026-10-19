namespace Ombra.Server.Contracts;

// Envelope bodies (canonical CBOR maps). Each Parse rejects unknown or missing keys.

/// <summary><c>op.put</c>, scope = groupId: one MLS application message of an item operation (§3.0).</summary>
public sealed record OpPutBody(ulong Epoch, byte[] Ciphertext, bool Ephemeral)
{
    public CborMapWriter ToCbor() => new CborMapWriter().UInt("epoch", Epoch).Bytes("ciphertext", Ciphertext).Bool("ephemeral", Ephemeral);
    public static OpPutBody Parse(CborMap m)
    {
        m.RequireExactly("epoch", "ciphertext", "ephemeral");
        return new(m.UInt("epoch"), m.Bytes("ciphertext"), m.Bool("ephemeral"));
    }
}

/// <summary><c>blob.put</c>, scope = groupId; travels in <see cref="Headers.Envelope"/>, HTTP body is the ciphertext.</summary>
public sealed record BlobPutBody(Guid BlobId, ulong Size, byte[] Sha256)
{
    public CborMapWriter ToCbor() => new CborMapWriter().Uuid("blobId", BlobId).UInt("size", Size).Bytes("sha256", Sha256);
    public static BlobPutBody Parse(CborMap m)
    {
        m.RequireExactly("blobId", "size", "sha256");
        return new(m.Uuid("blobId"), m.UInt("size"), m.Bytes("sha256", 32));
    }
}

/// <summary><c>blob.delete</c>, scope = groupId of the blob.</summary>
public sealed record BlobDeleteBody(Guid BlobId)
{
    public CborMapWriter ToCbor() => new CborMapWriter().Uuid("blobId", BlobId);
    public static BlobDeleteBody Parse(CborMap m)
    {
        m.RequireExactly("blobId");
        return new(m.Uuid("blobId"));
    }
}

/// <summary><c>transfer.create</c>, scope = teamId (§3.3 R5-09).</summary>
public sealed record TransferCreateBody(Guid TransferId, Guid TargetDeviceId, ulong Size, byte[] Sha256, uint TotalChunks)
{
    public CborMapWriter ToCbor() => new CborMapWriter()
        .Uuid("transferId", TransferId).Uuid("targetDeviceId", TargetDeviceId)
        .UInt("size", Size).Bytes("sha256", Sha256).UInt("totalChunks", TotalChunks);
    public static TransferCreateBody Parse(CborMap m)
    {
        m.RequireExactly("transferId", "targetDeviceId", "size", "sha256", "totalChunks");
        var chunks = m.UInt("totalChunks");
        if (chunks is 0 or > uint.MaxValue) throw new WireFormatException("totalChunks out of range");
        return new(m.Uuid("transferId"), m.Uuid("targetDeviceId"), m.UInt("size"), m.Bytes("sha256", 32), (uint)chunks);
    }
}

/// <summary>
/// <c>invite.create</c>, scope = teamId (§3.4). <c>signedInvite</c> is the admin-signed Invite, opaque to the
/// server (clients verify it against fingerprint <c>a</c>); the server stores only routing fields and
/// <c>verifier = SHA-256(k_srv)</c>.
/// </summary>
public sealed record InviteCreateBody(Guid InviteId, byte[] SignedInvite, ulong RosterHeadVersion, byte[] RosterHeadHash,
    string Role, ulong ExpiresAt, uint MaxUses, byte[] Verifier)
{
    public CborMapWriter ToCbor() => new CborMapWriter()
        .Uuid("inviteId", InviteId).Bytes("signedInvite", SignedInvite)
        .UInt("rosterHeadVersion", RosterHeadVersion).Bytes("rosterHeadHash", RosterHeadHash)
        .Text("role", Role).UInt("expiresAt", ExpiresAt).UInt("maxUses", MaxUses).Bytes("verifier", Verifier);
    public static InviteCreateBody Parse(CborMap m)
    {
        m.RequireExactly("inviteId", "signedInvite", "rosterHeadVersion", "rosterHeadHash", "role", "expiresAt", "maxUses", "verifier");
        var role = m.Text("role");
        if (role is not ("member" or "admin")) throw new WireFormatException("role must be member|admin");
        var maxUses = m.UInt("maxUses");
        if (maxUses is 0 or > 1000) throw new WireFormatException("maxUses out of range");
        return new(m.Uuid("inviteId"), m.Bytes("signedInvite"), m.UInt("rosterHeadVersion"), m.Bytes("rosterHeadHash", 32),
            role, m.UInt("expiresAt"), (uint)maxUses, m.Bytes("verifier", 32));
    }
}

/// <summary>
/// A joining device: keys, a KeyPackage and its DeviceCertificate (the member's root is self-issued, siblings are
/// issued by the root). The admin checks the certificate against the keys covered by <c>bind</c>.
/// </summary>
public sealed record JoinDevice(Guid DeviceId, string Product, byte[] IdentityKey, byte[] MlsSignatureKey, byte[] KeyPackage, byte[] Certificate)
{
    public CborMapWriter ToCbor() => new CborMapWriter()
        .Uuid("deviceId", DeviceId).Text("product", Product).Bytes("identityKey", IdentityKey)
        .Bytes("mlsSignatureKey", MlsSignatureKey).Bytes("keyPackage", KeyPackage).Bytes("certificate", Certificate);
    public static JoinDevice Parse(CborMap m)
    {
        m.RequireExactly("deviceId", "product", "identityKey", "mlsSignatureKey", "keyPackage", "certificate");
        var product = m.Text("product");
        if (product is not ("gui" or "browser")) throw new WireFormatException("product must be gui|browser");
        return new(m.Uuid("deviceId"), product, m.Bytes("identityKey", 32), m.Bytes("mlsSignatureKey", 32), m.Bytes("keyPackage"), m.Bytes("certificate"));
    }
}

/// <summary>
/// <c>join.request</c>, scope = teamId (§3.4 п.3). Signed by the identityKey of <c>devices[0]</c> carried in the body
/// (proof of possession). <c>bind</c> is opaque to the server: it cannot verify or forge it without <c>k_adm</c>.
/// </summary>
public sealed record JoinRequestBody(Guid InviteId, Guid MemberId, IReadOnlyList<JoinDevice> Devices, byte[] ProofSrv, byte[] Bind)
{
    public CborMapWriter ToCbor() => new CborMapWriter()
        .Uuid("inviteId", InviteId).Uuid("memberId", MemberId)
        .MapArray("devices", Devices.Select(d => d.ToCbor()))
        .Bytes("proofSrv", ProofSrv).Bytes("bind", Bind);
    public static JoinRequestBody Parse(CborMap m)
    {
        m.RequireExactly("inviteId", "memberId", "devices", "proofSrv", "bind");
        var devices = m.MapArray("devices").Select(JoinDevice.Parse).ToList();
        if (devices.Count is 0 or > 8) throw new WireFormatException("devices count out of range");
        if (devices.Select(d => d.DeviceId).Distinct().Count() != devices.Count) throw new WireFormatException("duplicate deviceId");
        return new(m.Uuid("inviteId"), m.Uuid("memberId"), devices, m.Bytes("proofSrv", 32), m.Bytes("bind", 32));
    }
}

/// <summary><c>team.quarantine.release</c>, scope = teamId; only an owner device may send it (§1 R2-08).</summary>
public sealed record QuarantineReleaseBody
{
    public CborMapWriter ToCbor() => new();
    public static QuarantineReleaseBody Parse(CborMap m)
    {
        m.RequireExactly();
        return new();
    }
}

/// <summary><c>device.revoke</c>, scope = memberId (§2.3 п.5, §5.4): revoke an own device; the server also revokes every
/// device it issued that was registered at or after <c>compromisedSince</c> (Unix seconds).</summary>
public sealed record DeviceRevokeBody(Guid DeviceId, ulong CompromisedSince)
{
    public CborMapWriter ToCbor() => new CborMapWriter().Uuid("deviceId", DeviceId).UInt("compromisedSince", CompromisedSince);
    public static DeviceRevokeBody Parse(CborMap m)
    {
        m.RequireExactly("deviceId", "compromisedSince");
        return new(m.Uuid("deviceId"), m.UInt("compromisedSince"));
    }
}

/// <summary><c>device.grant</c>, scope = the new deviceId: teamIds (n × 32 bytes) the issuer vouches for (§2.3).</summary>
public sealed record DeviceGrantBody(IReadOnlyList<byte[]> TeamIds)
{
    public CborMapWriter ToCbor() => new CborMapWriter().Bytes("teamIds", TeamIds.SelectMany(t => t).ToArray());
    public static DeviceGrantBody Parse(CborMap m)
    {
        m.RequireExactly("teamIds");
        var all = m.Bytes("teamIds");
        if (all.Length % 32 != 0 || all.Length > 32 * 256) throw new WireFormatException("teamIds must be n × 32 bytes");
        return new(all.Chunk(32).ToList());
    }
}
