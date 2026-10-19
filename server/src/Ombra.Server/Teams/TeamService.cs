using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;
using Ombra.Server.Delivery;
using Ombra.Server.Streaming;

namespace Ombra.Server.Teams;

/// <summary>
/// Invites and JoinRequests (§3.4) as the server sees them: routing data, <c>verifier = SHA-256(k_srv)</c>, TTL and use
/// counting. The server never sees <c>k</c>/<c>k_adm</c> and cannot verify or forge <c>bind</c>; admins verify the
/// stored JoinRequest themselves. Also: quarantine release by an owner device (§1 R2-08) and /admin/pending.
/// </summary>
public sealed class TeamService(NpgsqlDataSource db, TimeProvider clock, Envelopes envelopes, StreamHub hub, IOptions<OmbraOptions> options, ServerIdentity serverIdentity)
{
    private const int MaxInviteDays = 30;
    private readonly OmbraOptions _o = options.Value;

    public async Task<InviteResponse> CreateInviteAsync(DeviceIdentity device, byte[] teamId, VerifiedEnvelope env)
    {
        if (!env.Envelope.Scope.AsSpan().SequenceEqual(teamId)) throw ApiException.Bad("scope must be the teamId");
        InviteCreateBody body;
        try { body = InviteCreateBody.Parse(env.Envelope.BodyMap()); }
        catch (WireFormatException e) { throw ApiException.Bad(e.Message); }
        var now = clock.GetUtcNow();
        var expires = DateTimeOffset.FromUnixTimeSeconds((long)Math.Min(body.ExpiresAt, (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds()));
        if (expires <= now || expires > now.AddDays(MaxInviteDays)) throw ApiException.Bad($"expiresAt must be within {MaxInviteDays} days");

        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var revision = await conn.ScalarAsync<long?>("SELECT access_revision FROM teams WHERE team_id = @t FOR NO KEY UPDATE", tx, ("t", teamId))
                       ?? throw ApiException.NotFound("team");
        var (_, access) = await AccessService.RequireContentAsync(conn, tx, teamId, device.DeviceId);
        if (!access.IsAdmin) throw ApiException.Forbidden(Reasons.NotAdmin);
        var stored = await EnvelopeDedupe.ClaimAsync(conn, tx, env, now);
        if (stored is not null) return EnvelopeDedupe.Read<InviteResponse>(stored);
        if ((long)env.Envelope.BaseRevision != revision) throw ApiException.Conflict(Reasons.Revision, $"access revision is {revision}");
        var head = await conn.ScalarAsync<byte[]>("SELECT hash FROM roster_versions WHERE team_id = @t AND version = @v", tx,
            ("t", teamId), ("v", (long)body.RosterHeadVersion));
        if (head is null || !head.AsSpan().SequenceEqual(body.RosterHeadHash)) throw ApiException.Conflict(Reasons.RosterConflict, "rosterHead is not in the chain");
        try
        {
            await conn.ExecAsync(
                "INSERT INTO invites (invite_id, team_id, signed_invite, roster_head_version, roster_head_hash, role, expires_at, max_uses, verifier, created_by_device, created_at) " +
                "VALUES (@id, @t, @si, @hv, @hh, @role, @exp, @max, @ver, @d, @now)", tx,
                ("id", body.InviteId), ("t", teamId), ("si", body.SignedInvite), ("hv", (long)body.RosterHeadVersion), ("hh", body.RosterHeadHash),
                ("role", body.Role), ("exp", expires), ("max", (int)body.MaxUses), ("ver", body.Verifier), ("d", device.DeviceId), ("now", now));
        }
        catch (PostgresException e) when (e.IsUniqueViolation())
        {
            throw ApiException.Conflict(Reasons.Exists);
        }
        await conn.ExecAsync("UPDATE teams SET access_revision = access_revision + 1 WHERE team_id = @t", tx, ("t", teamId));
        var response = await InviteResponseAsync(conn, tx, body.InviteId);
        await EnvelopeDedupe.StoreAsync(conn, tx, env, 200, response);
        await tx.CommitAsync();
        return response;
    }

    private static async Task<InviteResponse> InviteResponseAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid inviteId)
    {
        var inv = await conn.SingleAsync(
            "SELECT team_id, signed_invite, roster_head_version, roster_head_hash, role, expires_at FROM invites WHERE invite_id = @id", tx,
            r => new { Team = r.Bytes(0), Signed = r.Bytes(1), HeadV = r.GetInt64(2), HeadH = r.Bytes(3), Role = r.GetString(4), Exp = r.Time(5) },
            ("id", inviteId)) ?? throw ApiException.NotFound("invite");
        // Full chain from genesis: the invitee verifies it starts at t and contains rosterHead (R2-01).
        var chain = await conn.ListAsync("SELECT version, roster_update FROM roster_versions WHERE team_id = @t ORDER BY version", tx,
            r => new SyncRoster(Ids.Hex(inv.Team), r.GetInt64(0), r.Bytes(1)), ("t", inv.Team));
        return new InviteResponse(inviteId, Ids.Hex(inv.Team), inv.Signed, inv.HeadV, inv.HeadH, inv.Role, inv.Exp, chain);
    }

    /// <summary>Unauthenticated: the invitee has no device on this server yet. Expired/used-up invites are 404.</summary>
    public async Task<InviteResponse> GetInviteAsync(Guid inviteId)
    {
        await using var conn = await db.OpenConnectionAsync();
        var live = await conn.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM invites WHERE invite_id = @id AND expires_at > @now AND uses < max_uses)", null,
            ("id", inviteId), ("now", clock.GetUtcNow()));
        if (!live) throw ApiException.NotFound("invite");
        return await InviteResponseAsync(conn, null, inviteId);
    }

    /// <summary>
    /// JoinRequest: the envelope is signed by devices[0].identityKey from the body (proof of possession). Checks
    /// SHA-256(proof_srv) = verifier (constant time), TTL and uses; consumes memberId and every deviceId forever.
    /// </summary>
    public async Task<JoinResponse> JoinAsync(Guid inviteId, HttpRequest request)
    {
        var bytes = await Envelopes.ReadBodyAsync(request, Envelope.MaxEncodedBytes);
        var sig = Envelopes.Signature(request);
        Envelope unverified;
        JoinRequestBody body;
        try
        {
            unverified = Envelope.Decode(bytes);
            body = JoinRequestBody.Parse(unverified.BodyMap());
        }
        catch (WireFormatException e) { throw ApiException.Bad(e.Message); }
        var first = body.Devices[0];
        var env = envelopes.Verify(bytes, sig, first.DeviceId, first.IdentityKey, EnvelopeKinds.JoinRequest);
        if (body.InviteId != inviteId) throw ApiException.Bad("inviteId mismatch");

        var now = clock.GetUtcNow();
        Guid requestId;
        byte[] teamId;
        await using (var conn = await db.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            var inv = await conn.SingleAsync(
                "SELECT team_id, verifier, uses, max_uses, expires_at FROM invites WHERE invite_id = @id FOR UPDATE", tx,
                r => new { Team = r.Bytes(0), Verifier = r.Bytes(1), Uses = r.GetInt32(2), Max = r.GetInt32(3), Exp = r.Time(4) }, ("id", inviteId));
            if (inv is null || inv.Exp <= now || inv.Uses >= inv.Max
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(body.ProofSrv), inv.Verifier))
                throw ApiException.Forbidden(Reasons.InviteInvalid);
            if (!env.Envelope.Scope.AsSpan().SequenceEqual(inv.Team)) throw ApiException.Bad("scope must be the teamId");
            var stored = await EnvelopeDedupe.ClaimAsync(conn, tx, env, now);
            if (stored is not null) return EnvelopeDedupe.Read<JoinResponse>(stored);

            await UsedIds.ClaimAsync(conn, tx, body.MemberId, "member", "join", now);
            await conn.ExecAsync("INSERT INTO members (member_id, created_at) VALUES (@m, @now)", tx, ("m", body.MemberId), ("now", now));
            foreach (var d in body.Devices)
            {
                // Sanity only (the admin verifies bind and the roster validator the signatures): the certificate is
                // for exactly these keys, this member and this server.
                var c = Devices.ParsedCertificate.Parse(d.Certificate);
                if (c.DeviceId != d.DeviceId || c.MemberId != body.MemberId || c.ServerId != serverIdentity.ServerId
                    || !c.IdentityKey.AsSpan().SequenceEqual(d.IdentityKey) || !c.MlsSignatureKey.AsSpan().SequenceEqual(d.MlsSignatureKey))
                    throw ApiException.Bad($"certificate of device {d.DeviceId} does not match its keys");
                await UsedIds.ClaimAsync(conn, tx, d.DeviceId, "device", "join", now);
                await conn.ExecAsync(
                    "INSERT INTO devices (device_id, member_id, product, identity_key, mls_signature_key, created_at, issuer_device_id, certificate) " +
                    "VALUES (@d, @m, @p, @ik, @mk, @now, @i, @c)", tx,
                    ("d", d.DeviceId), ("m", body.MemberId), ("p", d.Product), ("ik", d.IdentityKey), ("mk", d.MlsSignatureKey), ("now", now),
                    ("i", c.IssuerDeviceId), ("c", d.Certificate));
            }
            await conn.ExecAsync("UPDATE invites SET uses = uses + 1 WHERE invite_id = @id", tx, ("id", inviteId));
            requestId = Guid.CreateVersion7();
            await ChangeLog.LockAsync(conn, tx);
            await conn.ExecAsync(
                "INSERT INTO join_requests (request_id, invite_id, team_id, member_id, join_envelope, join_signature, created_at) VALUES (@r, @i, @t, @m, @e, @s, @now)", tx,
                ("r", requestId), ("i", inviteId), ("t", inv.Team), ("m", body.MemberId), ("e", bytes), ("s", sig), ("now", now));
            var response = new JoinResponse(requestId);
            await EnvelopeDedupe.StoreAsync(conn, tx, env, 200, response);
            await tx.CommitAsync();
            teamId = inv.Team;
        }
        await NotifyAdminsAsync(teamId);
        return new JoinResponse(requestId);
    }

    private async Task NotifyAdminsAsync(byte[] teamId)
    {
        await using var conn = await db.OpenConnectionAsync();
        var admins = await conn.ListAsync("SELECT device_id FROM team_device_access WHERE team_id = @t AND role IN ('owner', 'admin')", null,
            r => r.GetGuid(0), ("t", teamId));
        hub.Send(admins, new StreamEvent(StreamEventTypes.Pending, TeamId: Ids.Hex(teamId)));
    }

    /// <summary>GET /v1/admin/pending: for teams where the device is owner/admin.</summary>
    public async Task<PendingResponse> PendingAsync(DeviceIdentity device)
    {
        await using var conn = await db.OpenConnectionAsync();
        var teams = (await conn.ListAsync(
            "SELECT a.team_id FROM team_device_access a JOIN teams t ON t.team_id = a.team_id WHERE a.device_id = @d AND a.role IN ('owner', 'admin')", null,
            r => r.Bytes(0), ("d", device.DeviceId))).ToArray();
        if (teams.Length == 0) throw ApiException.Forbidden(Reasons.NotAdmin);
        var rotations = await conn.ListAsync(
            "SELECT rotation_id, team_id, blocked_member_id, blocked_device_id, audit_id, group_ids, created_at FROM pending_rotations " +
            "WHERE team_id = ANY(@t) AND resolved_at IS NULL ORDER BY change_seq", null,
            r => new SyncPendingRotation(r.GetGuid(0), Ids.Hex(r.Bytes(1)), r.GuidOrNull(2), r.GuidOrNull(3), r.GetGuid(4),
                ((byte[][])r[5]).Select(g => Ids.Hex(g)).ToList(), r.Time(6)), ("t", teams));
        var joins = await conn.ListAsync(
            "SELECT request_id, invite_id, team_id, member_id, join_envelope, join_signature, created_at FROM join_requests " +
            "WHERE team_id = ANY(@t) AND resolved_at IS NULL ORDER BY change_seq", null,
            r => new PendingJoin(r.GetGuid(0), r.GetGuid(1), Ids.Hex(r.Bytes(2)), r.GetGuid(3), r.Bytes(4), r.Bytes(5), r.Time(6)), ("t", teams));
        return new PendingResponse(rotations, joins);
    }

    /// <summary>§1 карантин п.4: only an owner device of the team (not any admin) releases quarantine.</summary>
    public async Task ReleaseQuarantineAsync(DeviceIdentity device, byte[] teamId, VerifiedEnvelope env)
    {
        if (!env.Envelope.Scope.AsSpan().SequenceEqual(teamId)) throw ApiException.Bad("scope must be the teamId");
        try { QuarantineReleaseBody.Parse(env.Envelope.BodyMap()); }
        catch (WireFormatException e) { throw ApiException.Bad(e.Message); }
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var access = await AccessService.RequireActiveAsync(conn, tx, teamId, device.DeviceId);
        if (access.Role != "owner") throw ApiException.Forbidden(Reasons.NotOwner);
        await Quarantine.ReleaseAsync(conn, tx, teamId, "owner", "device:" + device.DeviceId, clock.GetUtcNow());
        await tx.CommitAsync();
    }
}

public static class Quarantine
{
    public static async Task ReleaseAsync(NpgsqlConnection conn, NpgsqlTransaction tx, byte[] teamId, string by, string actor, DateTimeOffset now)
    {
        var n = await conn.ExecAsync("UPDATE teams SET quarantine = false, quarantine_released_by = @by WHERE team_id = @t AND quarantine", tx,
            ("by", by), ("t", teamId));
        if (n == 0) throw ApiException.Conflict(Reasons.Exists, "team is not in quarantine");
        await conn.ExecAsync(
            "INSERT INTO audit (audit_id, at, actor, action, team_id, details) VALUES (@a, @now, @actor, 'quarantine.release', @t, @d::jsonb)", tx,
            ("a", Guid.CreateVersion7()), ("now", now), ("actor", actor), ("t", teamId), ("d", $"{{\"by\":\"{by}\"}}"));
    }
}
