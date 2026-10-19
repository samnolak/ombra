using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Ombra.Core.Interop;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;
using Ombra.Server.Revocation;
using Ombra.Server.Transfers;

namespace Ombra.Server.Devices;

/// <summary>A DeviceCertificate as parsed by ombra-core (<c>cert.parse</c>); <see cref="Signed"/> is the exact signing input.</summary>
public sealed record ParsedCertificate(Guid ServerId, Guid MemberId, Guid DeviceId, byte[] IdentityKey, byte[] MlsSignatureKey, Guid IssuerDeviceId,
    byte[] Signed, byte[] Signature)
{
    public bool SelfIssued => IssuerDeviceId == DeviceId;

    public static ParsedCertificate Parse(byte[] certificate)
    {
        JsonNode r;
        try { r = OmbraPublic.Call("cert.parse", new JsonObject { ["cert"] = OmbraPublic.Hex(certificate) }); }
        catch (OmbraException e) { throw ApiException.Bad($"certificate: {e.Code}: {e.Detail}"); }
        var c = r["cert"]!;
        Guid G(string k) => Guid.ParseExact(c[k]!.GetValue<string>(), "N");
        byte[] B(JsonNode n) => Convert.FromHexString(n.GetValue<string>());
        return new ParsedCertificate(G("serverId"), G("memberId"), G("deviceId"), B(c["identityKey"]!), B(c["mlsSignatureKey"]!), G("issuerDeviceId"),
            B(r["signed"]!), B(c["sig"]!));
    }
}

/// <summary>
/// Devices of a member beyond bootstrap/join (§2.3, §5.4, §6.1 <c>/v1/devices/*</c>):
/// <list type="bullet">
/// <item>the SAS pairing channel — the server only relays commit → keys → reveal and cannot learn or choose the words;</item>
/// <item>registration of a device by a DeviceCertificate issued by an active device of the same member (it can say hello
/// immediately; team rosters list it once the member publishes the RosterUpdate);</item>
/// <item>DeviceRevocation of an own device: revoked server-wide (tokens, sockets, every team's queues/transfers via the
/// same block as a roster revocation), with the cascade over devices it issued that were registered at or after
/// <c>compromisedSince</c> — by the server's registration time, never by the certificate's claimed <c>issuedAt</c> (R2-10).</item>
/// </list>
/// </summary>
public sealed class DeviceService(NpgsqlDataSource db, TimeProvider clock, ServerIdentity server, RevocationService revocation, TransferStore store)
{
    public static readonly TimeSpan PairTtl = TimeSpan.FromMinutes(10);
    private const int MaxOpenPairsPerDevice = 3;
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string NormalizeCode(string code) =>
        new((code ?? "").ToUpperInvariant().Where(c => CodeAlphabet.Contains(c)).ToArray());

    private static string NewCode()
    {
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++) chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(chars);
    }

    public async Task<PairOpenResponse> OpenPairAsync(DeviceIdentity device, PairOpenRequest req)
    {
        if (req.Commitment is not { Length: 32 }) throw ApiException.Bad("commitment must be 32 bytes");
        var now = clock.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync();
        var open = await conn.ScalarAsync<long>("SELECT count(*) FROM pairing_sessions WHERE existing_device_id = @d AND expires_at > @now AND certified_at IS NULL", null,
            ("d", device.DeviceId), ("now", now));
        if (open >= MaxOpenPairsPerDevice) throw new ApiException(429, Reasons.RateLimited, "too many open pairing sessions");
        for (var attempt = 0; ; attempt++)
        {
            var code = NewCode();
            try
            {
                await conn.ExecAsync(
                    "INSERT INTO pairing_sessions (code, member_id, existing_device_id, existing_commitment, created_at, expires_at) VALUES (@c, @m, @d, @x, @now, @exp)", null,
                    ("c", code), ("m", device.MemberId), ("d", device.DeviceId), ("x", req.Commitment), ("now", now), ("exp", now + PairTtl));
                return new PairOpenResponse(code, device.MemberId, now + PairTtl);
            }
            catch (PostgresException e) when (e.IsUniqueViolation() && attempt < 5)
            {
            }
        }
    }

    private static Task<PairSession?> SessionAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string code, DateTimeOffset now, bool forUpdate = false) =>
        conn.SingleAsync(
            "SELECT p.code, p.member_id, p.existing_device_id, d.identity_key, d.mls_signature_key, p.existing_commitment, p.new_device_id, p.new_identity_key, " +
            "p.new_mls_key, p.new_nonce, p.existing_nonce, p.certified_at IS NOT NULL, p.expires_at " +
            "FROM pairing_sessions p JOIN devices d ON d.device_id = p.existing_device_id " +
            "WHERE p.code = @c AND p.expires_at > @now AND d.revoked_at IS NULL" + (forUpdate ? " FOR UPDATE OF p" : ""), tx,
            r => new PairSession(r.GetString(0), r.GetGuid(1), r.GetGuid(2), r.Bytes(3), r.Bytes(4), r.Bytes(5), r.GuidOrNull(6),
                r.IsDBNull(7) ? null : r.Bytes(7), r.IsDBNull(8) ? null : r.Bytes(8), r.IsDBNull(9) ? null : r.Bytes(9), r.IsDBNull(10) ? null : r.Bytes(10),
                r.GetBoolean(11), r.Time(12)),
            ("c", NormalizeCode(code)), ("now", now));

    /// <summary>Unauthenticated (the new device is not registered yet); the code only locates the session.</summary>
    public async Task<PairSession> GetPairAsync(string code)
    {
        await using var conn = await db.OpenConnectionAsync();
        return await SessionAsync(conn, null, code, clock.GetUtcNow()) ?? throw ApiException.NotFound("pairing session");
    }

    /// <summary>The new device answers once (first wins), in clear, before the existing device reveals its nonce.</summary>
    public async Task<PairSession> JoinPairAsync(string code, PairJoinRequest req)
    {
        if (req.IdentityKey is not { Length: 32 } || req.MlsSignatureKey is not { Length: 32 } || req.Nonce is not { Length: 32 })
            throw ApiException.Bad("keys and nonce must be 32 bytes");
        if (req.Product is not ("gui" or "browser")) throw ApiException.Bad("product must be gui|browser");
        var now = clock.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var s = await SessionAsync(conn, tx, code, now, forUpdate: true) ?? throw ApiException.NotFound("pairing session");
        if (s.NewDeviceId is not null) throw ApiException.Conflict(Reasons.Exists, "another device already answered this pairing session");
        if (await conn.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM used_ids WHERE id = @d)", tx, ("d", req.DeviceId)))
            throw ApiException.Conflict(Reasons.DeviceIdReused);
        await conn.ExecAsync(
            "UPDATE pairing_sessions SET new_device_id = @d, new_identity_key = @ik, new_mls_key = @mk, new_nonce = @n, new_product = @p WHERE code = @c", tx,
            ("d", req.DeviceId), ("ik", req.IdentityKey), ("mk", req.MlsSignatureKey), ("n", req.Nonce), ("p", req.Product), ("c", s.Code));
        var updated = (await SessionAsync(conn, tx, s.Code, now))!;
        await tx.CommitAsync();
        return updated;
    }

    /// <summary>The existing device reveals its nonce only after the new device's answer is recorded.</summary>
    public async Task<PairSession> RevealPairAsync(DeviceIdentity device, string code, PairRevealRequest req)
    {
        if (req.Nonce is not { Length: 32 }) throw ApiException.Bad("nonce must be 32 bytes");
        var now = clock.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var s = await SessionAsync(conn, tx, code, now, forUpdate: true) ?? throw ApiException.NotFound("pairing session");
        if (s.ExistingDeviceId != device.DeviceId) throw ApiException.Forbidden(Reasons.Forbidden, "not the device that opened this session");
        if (s.NewDeviceId is null) throw ApiException.Conflict(Reasons.Exists, "the new device has not answered yet");
        if (s.ExistingNonce is not null) throw ApiException.Conflict(Reasons.Exists, "already revealed");
        await conn.ExecAsync("UPDATE pairing_sessions SET existing_nonce = @n WHERE code = @c", tx, ("n", req.Nonce), ("c", s.Code));
        var updated = (await SessionAsync(conn, tx, s.Code, now))!;
        await tx.CommitAsync();
        return updated;
    }

    /// <summary>
    /// §2.3 п.3: register a device of the requesting member from a certificate the requesting device signed for it,
    /// together with the issuer-signed grant of teams. With <c>pairingCode</c> the certified keys must be the ones the
    /// new device answered with in that (revealed) session.
    /// </summary>
    public async Task<DeviceCertificateResponse> RegisterAsync(DeviceIdentity issuer, DeviceCertificateRequest req, Envelopes envelopes)
    {
        if (req.Product is not ("gui" or "browser")) throw ApiException.Bad("product must be gui|browser");
        var c = ParsedCertificate.Parse(req.Certificate);
        if (c.ServerId != server.ServerId) throw ApiException.Bad("certificate is for another server");
        if (c.MemberId != issuer.MemberId || c.IssuerDeviceId != issuer.DeviceId || c.SelfIssued)
            throw ApiException.Forbidden(Reasons.Forbidden, "the certificate must be issued by the requesting device for another device of its member");
        if (!OmbraPublic.Ed25519Verify(issuer.IdentityKey, c.Signed, c.Signature)) throw new ApiException(401, Reasons.BadSignature, "certificate signature");
        var grant = envelopes.Verify(req.GrantEnvelope, req.GrantSignature, issuer, EnvelopeKinds.DeviceGrant);
        if (!grant.Envelope.Scope.AsSpan().SequenceEqual(Ids.ToBytes(c.DeviceId))) throw ApiException.Bad("grant scope must be the new deviceId");
        try { DeviceGrantBody.Parse(grant.Envelope.BodyMap()); }
        catch (WireFormatException e) { throw ApiException.Bad(e.Message); }

        var now = clock.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // S2 review: registration and the cascading revoke of the same member are serialised on the member row, and
        // the issuer is re-checked under that lock — authentication happened before this transaction.
        await LockMemberAsync(conn, tx, issuer.MemberId);
        var issuerRevoked = await conn.ScalarAsync<bool?>("SELECT revoked_at IS NOT NULL FROM devices WHERE device_id = @d", tx, ("d", issuer.DeviceId));
        if (issuerRevoked != false) throw ApiException.Forbidden(Reasons.Revoked, "the issuing device is revoked");
        if (req.PairingCode is { } code)
        {
            var s = await SessionAsync(conn, tx, code, now, forUpdate: true) ?? throw ApiException.NotFound("pairing session");
            if (s.ExistingDeviceId != issuer.DeviceId || s.ExistingNonce is null || s.Certified
                || s.NewDeviceId != c.DeviceId || !s.NewIdentityKey!.AsSpan().SequenceEqual(c.IdentityKey) || !s.NewMlsSignatureKey!.AsSpan().SequenceEqual(c.MlsSignatureKey))
                throw ApiException.Conflict(Reasons.Exists, "the certificate does not match the revealed pairing session");
            await conn.ExecAsync("UPDATE pairing_sessions SET certified_at = @now WHERE code = @c", tx, ("now", now), ("c", s.Code));
        }
        await UsedIds.ClaimAsync(conn, tx, c.DeviceId, "device", "pairing", now);
        await conn.ExecAsync(
            "INSERT INTO devices (device_id, member_id, product, identity_key, mls_signature_key, created_at, issuer_device_id, certificate, grant_envelope, grant_signature) " +
            "VALUES (@d, @m, @p, @ik, @mk, @now, @i, @c, @g, @gs)", tx,
            ("d", c.DeviceId), ("m", c.MemberId), ("p", req.Product), ("ik", c.IdentityKey), ("mk", c.MlsSignatureKey), ("now", now),
            ("i", issuer.DeviceId), ("c", req.Certificate), ("g", req.GrantEnvelope), ("gs", req.GrantSignature));
        await conn.ExecAsync("INSERT INTO audit (audit_id, at, actor, action, subject) VALUES (@a, @now, @actor, 'device.pair', @s)", tx,
            ("a", Guid.CreateVersion7()), ("now", now), ("actor", "device:" + issuer.DeviceId), ("s", c.DeviceId.ToString()));
        await tx.CommitAsync();
        return new DeviceCertificateResponse(c.DeviceId, c.MemberId);
    }

    /// <summary>The certificate, its issuer key and the grant (with its signer's key) of a device: for its member's devices only.</summary>
    public async Task<DeviceRecord> RecordAsync(DeviceIdentity requester, Guid deviceId)
    {
        await using var conn = await db.OpenConnectionAsync();
        var rec = await conn.SingleAsync(
            "SELECT d.device_id, d.member_id, d.certificate, d.issuer_device_id, i.identity_key, d.grant_envelope, d.grant_signature " +
            "FROM devices d JOIN devices i ON i.device_id = d.issuer_device_id WHERE d.device_id = @d AND d.certificate IS NOT NULL", null,
            r => new { Id = r.GetGuid(0), Member = r.GetGuid(1), Cert = r.Bytes(2), Issuer = r.GetGuid(3), IssuerKey = r.Bytes(4), Grant = r.Bytes(5), Sig = r.Bytes(6) },
            ("d", deviceId)) ?? throw ApiException.NotFound("device certificate");
        if (rec.Member != requester.MemberId) throw ApiException.Forbidden(Reasons.Forbidden);
        var signer = Envelope.Decode(rec.Grant).DeviceId;
        var signerKey = await conn.ScalarAsync<byte[]>("SELECT identity_key FROM devices WHERE device_id = @d", null, ("d", signer))
                        ?? throw ApiException.NotFound("grant signer");
        return new DeviceRecord(rec.Id, rec.Member, rec.Cert, rec.Issuer, rec.IssuerKey, rec.Grant, rec.Sig, signer, signerKey);
    }

    /// <summary>
    /// §2.3 п.4: a sibling device (active, same member) replaces the team grant of a paired device after adding it to
    /// teams created or joined since pairing. The grant is opaque to the server; the paired device verifies the signer.
    /// </summary>
    public async Task UpdateGrantAsync(DeviceIdentity signer, Guid deviceId, DeviceGrantRequest req, Envelopes envelopes)
    {
        var grant = envelopes.Verify(req.GrantEnvelope, req.GrantSignature, signer, EnvelopeKinds.DeviceGrant);
        if (!grant.Envelope.Scope.AsSpan().SequenceEqual(Ids.ToBytes(deviceId))) throw ApiException.Bad("grant scope must be the deviceId");
        try { DeviceGrantBody.Parse(grant.Envelope.BodyMap()); }
        catch (WireFormatException e) { throw ApiException.Bad(e.Message); }
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await LockMemberAsync(conn, tx, signer.MemberId);
        var signerActive = await conn.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM devices WHERE device_id = @d AND revoked_at IS NULL)", tx, ("d", signer.DeviceId));
        if (!signerActive) throw ApiException.Forbidden(Reasons.Revoked, "the signing device is revoked");
        var n = await conn.ExecAsync(
            "UPDATE devices SET grant_envelope = @g, grant_signature = @s WHERE device_id = @d AND member_id = @m AND revoked_at IS NULL AND certificate IS NOT NULL", tx,
            ("g", req.GrantEnvelope), ("s", req.GrantSignature), ("d", deviceId), ("m", signer.MemberId));
        if (n == 0) throw ApiException.NotFound("paired device of this member");
        await tx.CommitAsync();
    }

    /// <summary>Devices of the requesting member (for adding its paired devices to its teams).</summary>
    public async Task<List<OwnDevice>> OwnDevicesAsync(DeviceIdentity requester)
    {
        await using var conn = await db.OpenConnectionAsync();
        return await conn.ListAsync(
            "SELECT device_id, certificate, issuer_device_id, revoked_at IS NOT NULL, created_at FROM devices WHERE member_id = @m ORDER BY created_at", null,
            r => new OwnDevice(r.GetGuid(0), r.IsDBNull(1) ? null : r.Bytes(1), r.GuidOrNull(2), r.GetBoolean(3), r.Time(4)), ("m", requester.MemberId));
    }

    /// <summary>
    /// §5.4 / §2.3 п.5: revoke an own device server-wide and cascade. Every team that lists a revoked device gets the
    /// same block as a roster revocation (queues purged, transfers deleted, sockets closed); the member still publishes
    /// the signed DeviceRevocation in each team's roster.
    /// </summary>
    public async Task<DeviceRevokeResponse> RevokeAsync(DeviceIdentity signer, VerifiedEnvelope env)
    {
        DeviceRevokeBody body;
        try { body = DeviceRevokeBody.Parse(env.Envelope.BodyMap()); }
        catch (WireFormatException e) { throw ApiException.Bad(e.Message); }
        if (!env.Envelope.Scope.AsSpan().SequenceEqual(Ids.ToBytes(signer.MemberId))) throw ApiException.Bad("scope must be the memberId");
        var now = clock.GetUtcNow();
        var since = DateTimeOffset.FromUnixTimeSeconds((long)Math.Min(body.CompromisedSince, (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds()));
        List<Guid> transfers = [];
        DeviceRevokeResponse response;
        await using (var conn = await db.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            await LockMemberAsync(conn, tx, signer.MemberId);
            var stored = await EnvelopeDedupe.ClaimAsync(conn, tx, env, now);
            if (stored is not null) return EnvelopeDedupe.Read<DeviceRevokeResponse>(stored);
            var target = await conn.ScalarAsync<Guid?>("SELECT member_id FROM devices WHERE device_id = @d FOR UPDATE", tx, ("d", body.DeviceId));
            if (target != signer.MemberId) throw ApiException.Forbidden(Reasons.Forbidden, "only a device of the same member revokes it");
            var cascade = await conn.ListAsync(
                """
                WITH RECURSIVE c(device_id) AS (
                    SELECT device_id FROM devices WHERE device_id = @d
                    UNION
                    SELECT dv.device_id FROM devices dv JOIN c ON dv.issuer_device_id = c.device_id
                    WHERE dv.device_id <> c.device_id AND (c.device_id <> @d OR dv.created_at >= @since)
                ) SELECT device_id FROM c ORDER BY 1
                """, tx, r => r.GetGuid(0), ("d", body.DeviceId), ("since", since));
            var revoked = await conn.ListAsync("UPDATE devices SET revoked_at = @now WHERE device_id = ANY(@ids) AND revoked_at IS NULL RETURNING device_id", tx,
                r => r.GetGuid(0), ("now", now), ("ids", cascade.ToArray()));
            await TokenService.RevokeDevicesAsync(conn, tx, cascade);
            var auditId = Guid.CreateVersion7();
            await conn.ExecAsync("INSERT INTO audit (audit_id, at, actor, action, subject, details) VALUES (@a, @now, @actor, 'device.revoke', @s, @d::jsonb)", tx,
                ("a", auditId), ("now", now), ("actor", "device:" + signer.DeviceId), ("s", body.DeviceId.ToString()),
                ("d", JsonSerializer.Serialize(new { cascade, compromisedSince = body.CompromisedSince })));
            var inTeams = await conn.ListAsync("SELECT team_id, device_id FROM roster_devices WHERE device_id = ANY(@ids) ORDER BY team_id, device_id", tx,
                r => (Team: r.Bytes(0), Device: r.GetGuid(1)), ("ids", cascade.ToArray()));
            foreach (var (team, dev) in inTeams)
            {
                var (_, t) = await revocation.ApplyAsync(conn, tx,
                    new RevocationRequest(RevocationKind.DeviceRevoke, team, dev, "admin", "device:" + signer.DeviceId, "device revoked by its member"),
                    Guid.CreateVersion7(), live: true, journal: false);
                transfers.AddRange(t);
            }
            // Devices outside any team (e.g. paired, not yet in a roster): close their sockets too.
            await conn.ExecAsync("SELECT pg_notify(@c, @p)", tx, ("c", RevocationService.NotifyChannel),
                ("p", JsonSerializer.Serialize(new RevokedNotice(null, cascade, Reasons.Revoked, false))));
            response = new DeviceRevokeResponse(revoked);
            await EnvelopeDedupe.StoreAsync(conn, tx, env, 200, response);
            await tx.CommitAsync();
        }
        foreach (var t in transfers) store.DeleteFiles(t);
        return response;
    }

    private static Task LockMemberAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid member) =>
        conn.ExecAsync("SELECT 1 FROM members WHERE member_id = @m FOR UPDATE", tx, ("m", member));
}
