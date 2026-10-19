using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Ombra.Core.Interop;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;
using Ombra.Server.Delivery;
using Ombra.Server.Revocation;
using Ombra.Server.Transfers;

namespace Ombra.Server.Teams;

/// <summary>Head projection as returned by ombra-core (<c>roster.append</c>); ids are lowercase hex.</summary>
internal sealed record ProjectionDto(
    long Version, string Owner, List<ProjectionMember> Members, List<ProjectionAccess> Access);
internal sealed record ProjectionMember(string MemberId, string Role, List<ProjectionDevice> Devices);
internal sealed record ProjectionDevice(string DeviceId, string IdentityKeyHash, string MlsKeyHash, string CertHash, long AddedInVersion, ProjectionRevocation? Revoked);
internal sealed record ProjectionRevocation(long Version, long CompromisedSince);
internal sealed record ProjectionAccess(string ProjectId, string MemberId, string Level);

/// <summary>
/// Roster chain on the server (§4.1, §4.2, §1 карантин п.3, R2-08). Every RosterUpdate is verified by ombra-core
/// (<see cref="OmbraPublic.VerifyRosterAppend"/>: signatures, field rights, prevHash/version, N2b) against the chain
/// state stored per team; the server never re-implements those rules. The server only:
/// <list type="bullet">
/// <item>extends linearly under the team row lock (CAS); a second valid version with an existing number signed with
/// authority over it is equivocation → the team is frozen (content 403 frozen), no branch is chosen by length, and both
/// versions are kept as evidence; an alternative without that authority (e.g. a member's) is 409 roster-conflict;</item>
/// <item>unfreezes only on the owner's signed decision (R2-08, §1 карантин п.3), verified by ombra-core: keep the known
/// version, or switch to the conflicting one (versions ≥ it are replaced, the projection mirror follows);</item>
/// <item>mirrors the verified head projection into roster_* tables (ACL source) and, for members removed / devices
/// revoked by the update, applies the same block+purge as a CLI removal in the same transaction (§5.1 п.3);</item>
/// <item>genesis: only a ServerOwner device that is the owner device of the genesis (§2.4).</item>
/// </list>
/// </summary>
public sealed class RosterService(NpgsqlDataSource db, TimeProvider clock, ServerIdentity server, RevocationService revocation, TransferStore store)
{
    public const string ContentType = "application/ombra-roster+cbor";
    private static readonly JsonSerializerOptions ProjectionJson = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };

    public sealed record AppendResponse(string TeamId, long Version, bool Known);
    public sealed record ResolveResponse(string TeamId, long Version, bool Switched);

    private ulong Now => (ulong)clock.GetUtcNow().ToUnixTimeSeconds();

    private static ApiException Map(OmbraException e) => e.Code == "roster-conflict"
        ? ApiException.Conflict(Reasons.RosterConflict, e.Detail)
        : new ApiException(400, Reasons.RosterRejected, $"{e.Code}: {e.Detail}");

    private RosterAppendResult Verify(byte[]? chain, byte[] update)
    {
        try
        {
            return OmbraPublic.VerifyRosterAppend(chain, update, Ids.ToBytes(server.ServerId), Now);
        }
        catch (OmbraException e)
        {
            throw Map(e);
        }
    }

    public async Task<AppendResponse> CreateTeamAsync(DeviceIdentity device, byte[] genesis)
    {
        var result = Verify(null, genesis);
        var p = Parse(result);
        if (!Guid.TryParseExact(p.Owner, "N", out var owner) || owner != device.MemberId)
            throw ApiException.Forbidden(Reasons.NotOwner, "the genesis owner must be the requesting member");
        if (!p.Members.SelectMany(m => m.Devices).Any(d => Guid.ParseExact(d.DeviceId, "N") == device.DeviceId))
            throw ApiException.Forbidden(Reasons.NotOwner, "the requesting device must be in the genesis");
        var teamId = result.TeamId!;
        var now = clock.GetUtcNow();
        List<Guid> transfers = [];
        await using (var conn = await db.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            var isOwner = await conn.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM server_owners WHERE member_id = @m)", tx, ("m", device.MemberId));
            if (!isOwner) throw ApiException.Forbidden(Reasons.NotOwner, "only a ServerOwner creates teams (§2.4)");
            try
            {
                await conn.ExecAsync(
                    "INSERT INTO teams (team_id, created_at, roster_version, roster_hash, chain_state) VALUES (@t, @now, @v, @h, @c)", tx,
                    ("t", teamId), ("now", now), ("v", (long)result.Version), ("h", result.Hash), ("c", result.ChainState));
            }
            catch (PostgresException e) when (e.IsUniqueViolation())
            {
                throw ApiException.Conflict(Reasons.Exists, "team exists");
            }
            await InsertVersionAsync(conn, tx, teamId, result, null, genesis, now);
            await ApplyProjectionAsync(conn, tx, teamId, p, transfers);
            await tx.CommitAsync();
        }
        return new AppendResponse(Ids.Hex(teamId), (long)result.Version, false);
    }

    /// <param name="beforeCommit">Extra checks/effects inside the append transaction on a new version: (conn, tx,
    /// version, owner memberId hex). Used by the ownership accept (§4.3) to bind the update to the open offer.</param>
    public async Task<AppendResponse> AppendAsync(DeviceIdentity device, byte[] teamId, byte[] update,
        Func<NpgsqlConnection, NpgsqlTransaction, long, string, Task>? beforeCommit = null)
    {
        var now = clock.GetUtcNow();
        List<Guid> transfers = [];
        AppendResponse response;
        await using (var conn = await db.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            var team = await conn.SingleAsync(
                "SELECT chain_state, roster_version, roster_hash, frozen_reason FROM teams WHERE team_id = @t FOR NO KEY UPDATE", tx,
                r => new { Chain = r.IsDBNull(0) ? null : r.Bytes(0), Version = r.GetInt64(1), Hash = r.Bytes(2), Frozen = r.StringOrNull(3) },
                ("t", teamId)) ?? throw ApiException.NotFound("team");
            if (team.Frozen is not null) throw ApiException.Forbidden(Reasons.Frozen, team.Frozen);
            // Quarantine does not block roster uploads (§1 карантин п.2–3); a blocked/removed device is refused.
            await AccessService.RequireActiveAsync(conn, tx, teamId, device.DeviceId);
            if (team.Chain is null) throw ApiException.Conflict(Reasons.RosterConflict, "team has no verified chain");

            var result = Verify(team.Chain, update);
            if (result.Equivocation)
            {
                // R2-08: two valid versions with one number. Freeze until the owner decides; never pick by length.
                var reason = "equivocation";
                var known = await conn.ScalarAsync<byte[]>("SELECT roster_update FROM roster_versions WHERE team_id = @t AND version = @v", tx,
                    ("t", teamId), ("v", (long)result.Version));
                await conn.ExecAsync("UPDATE teams SET chain_state = @c, frozen_reason = @r, access_revision = access_revision + 1 WHERE team_id = @t", tx,
                    ("c", result.ChainState), ("r", reason), ("t", teamId));
                await conn.ExecAsync(
                    "INSERT INTO roster_equivocations (team_id, version, known, conflicting, detected_at) VALUES (@t, @v, @k, @u, @now)", tx,
                    ("t", teamId), ("v", (long)result.Version), ("k", known), ("u", update), ("now", now));
                await conn.ExecAsync(
                    "INSERT INTO audit (audit_id, at, actor, action, team_id, details) VALUES (@a, @now, @actor, 'roster.equivocation', @t, @d::jsonb)", tx,
                    ("a", Guid.CreateVersion7()), ("now", now), ("actor", "device:" + device.DeviceId), ("t", teamId),
                    ("d", JsonSerializer.Serialize(new { version = result.Version, update = Convert.ToHexStringLower(update) })));
                await tx.CommitAsync();
                throw ApiException.Conflict(Reasons.Equivocation, "two valid roster versions with the same number; team frozen until the owner resolves it");
            }
            if ((long)result.Version == team.Version)
            {
                if (beforeCommit is not null) throw ApiException.Conflict(Reasons.RosterConflict, "this version is already in the chain");
                response = new AppendResponse(Ids.Hex(teamId), team.Version, true);
            }
            else
            {
                if ((long)result.Version != team.Version + 1) throw ApiException.Conflict(Reasons.RosterConflict, $"head is {team.Version}");
                await InsertVersionAsync(conn, tx, teamId, result, team.Hash, update, now);
                await conn.ExecAsync("UPDATE teams SET chain_state = @c, roster_version = @v, roster_hash = @h WHERE team_id = @t", tx,
                    ("c", result.ChainState), ("v", (long)result.Version), ("h", result.Hash), ("t", teamId));
                var head = Parse(result);
                await ApplyProjectionAsync(conn, tx, teamId, head, transfers);
                if (beforeCommit is not null) await beforeCommit(conn, tx, head.Version, head.Owner);
                response = new AppendResponse(Ids.Hex(teamId), (long)result.Version, false);
            }
            await tx.CommitAsync();
        }
        foreach (var t in transfers) store.DeleteFiles(t);
        return response;
    }

    public async Task<List<SyncRoster>> ChainAsync(DeviceIdentity device, byte[] teamId, long from)
    {
        await using var conn = await db.OpenConnectionAsync();
        await AccessService.RequireActiveAsync(conn, null, teamId, device.DeviceId);
        return await conn.ListAsync("SELECT version, roster_update FROM roster_versions WHERE team_id = @t AND version >= @f ORDER BY version", null,
            r => new SyncRoster(Ids.Hex(teamId), r.GetInt64(0), r.Bytes(1)), ("t", teamId), ("f", from));
    }

    /// <summary>The open equivocation of a frozen team (both signed versions), for the owner's client to decide on.</summary>
    public async Task<RosterEquivocation> EquivocationAsync(DeviceIdentity device, byte[] teamId)
    {
        await using var conn = await db.OpenConnectionAsync();
        await AccessService.RequireActiveAsync(conn, null, teamId, device.DeviceId);
        return await OpenEquivocationAsync(conn, null, teamId) ?? throw ApiException.NotFound("equivocation");
    }

    private static Task<RosterEquivocation?> OpenEquivocationAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, byte[] teamId) =>
        conn.SingleAsync("SELECT version, known, conflicting FROM roster_equivocations WHERE team_id = @t AND resolved_at IS NULL", tx,
            r => new RosterEquivocation(Ids.Hex(teamId), r.GetInt64(0), r.Bytes(1), r.Bytes(2)), ("t", teamId));

    /// <summary>Owner resolutions applied to this team, oldest first: clients apply them to unfreeze their own chains.</summary>
    public async Task<List<byte[]>> ResolutionsAsync(DeviceIdentity device, byte[] teamId)
    {
        await using var conn = await db.OpenConnectionAsync();
        await AccessService.RequireActiveAsync(conn, null, teamId, device.DeviceId);
        return await conn.ListAsync("SELECT resolution FROM roster_equivocations WHERE team_id = @t AND resolution IS NOT NULL ORDER BY resolved_at", null,
            r => r.Bytes(0), ("t", teamId));
    }

    /// <summary>
    /// Apply the owner's signed decision on the open equivocation (R2-08, §1 карантин п.3). The signature is the authority
    /// (an owner device of the common parent, checked by ombra-core), so the uploader is either any active device of the
    /// team (<paramref name="device"/>) or the operator relaying the owner's file via <c>ombra-admin roster resolve</c>.
    /// Keeping the known version only unfreezes; switching replaces versions ≥ v by the conflicting one and re-mirrors
    /// the projection: members/devices only in the discarded branch lose access (blocked like a roster removal).
    /// </summary>
    public async Task<ResolveResponse> ResolveAsync(DeviceIdentity? device, string actor, byte[] teamId, byte[] resolution)
    {
        var now = clock.GetUtcNow();
        List<Guid> transfers = [];
        ResolveResponse response;
        await using (var conn = await db.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            var team = await conn.SingleAsync("SELECT chain_state, frozen_reason FROM teams WHERE team_id = @t FOR NO KEY UPDATE", tx,
                r => new { Chain = r.IsDBNull(0) ? null : r.Bytes(0), Frozen = r.StringOrNull(1) }, ("t", teamId)) ?? throw ApiException.NotFound("team");
            if (device is not null) await AccessService.RequireActiveAsync(conn, tx, teamId, device.DeviceId);
            var open = await OpenEquivocationAsync(conn, tx, teamId);
            if (team.Frozen != "equivocation" || open is null || team.Chain is null)
                throw ApiException.Conflict(Reasons.RosterConflict, "the team has no open roster equivocation");
            RosterResolveResult r;
            try { r = OmbraPublic.ResolveEquivocation(team.Chain, resolution, Ids.ToBytes(server.ServerId), Now); }
            catch (OmbraException e) { throw Map(e); }
            var head = r.Head;
            if (r.Switched)
            {
                var v = (long)r.ResolvedVersion;
                var prevHash = await conn.ScalarAsync<byte[]>("SELECT hash FROM roster_versions WHERE team_id = @t AND version = @v", tx, ("t", teamId), ("v", v - 1));
                await conn.ExecAsync("DELETE FROM roster_versions WHERE team_id = @t AND version >= @v", tx, ("t", teamId), ("v", v));
                await InsertVersionAsync(conn, tx, teamId, head, prevHash, open.Conflicting, now);
                // Epochs bound to a replaced roster version cannot be continued: close those groups; clients recreate
                // them as the next generation on the canonical chain (ombra-core closes them locally the same way).
                await conn.ExecAsync(
                    "UPDATE groups SET closed_at = @now WHERE team_id = @t AND closed_at IS NULL AND (roster_version IS NULL OR roster_version >= @v)", tx,
                    ("now", now), ("t", teamId), ("v", v));
            }
            await conn.ExecAsync(
                "UPDATE teams SET chain_state = @c, roster_version = @v, roster_hash = @h, frozen_reason = NULL, access_revision = access_revision + 1 WHERE team_id = @t", tx,
                ("c", head.ChainState), ("v", (long)head.Version), ("h", head.Hash), ("t", teamId));
            if (r.Switched) await ApplyProjectionAsync(conn, tx, teamId, Parse(head), transfers, branchSwitch: true);
            await conn.ExecAsync(
                "UPDATE roster_equivocations SET resolution = @r, switched = @s, resolved_at = @now, resolved_by = @by WHERE team_id = @t AND resolved_at IS NULL", tx,
                ("r", resolution), ("s", r.Switched), ("now", now), ("by", actor), ("t", teamId));
            await conn.ExecAsync(
                "INSERT INTO audit (audit_id, at, actor, action, team_id, details) VALUES (@a, @now, @actor, 'roster.equivocation.resolved', @t, @d::jsonb)", tx,
                ("a", Guid.CreateVersion7()), ("now", now), ("actor", actor), ("t", teamId),
                ("d", JsonSerializer.Serialize(new { version = r.ResolvedVersion, switched = r.Switched, head = head.Version, resolution = Convert.ToHexStringLower(resolution) })));
            await tx.CommitAsync();
            response = new ResolveResponse(Ids.Hex(teamId), (long)head.Version, r.Switched);
        }
        foreach (var t in transfers) store.DeleteFiles(t);
        return response;
    }

    private static ProjectionDto Parse(RosterAppendResult r) =>
        JsonSerializer.Deserialize<ProjectionDto>(r.ProjectionJson ?? throw new ApiException(500, Reasons.RosterRejected, "no projection"), ProjectionJson)
        ?? throw new ApiException(500, Reasons.RosterRejected, "projection");

    private static async Task InsertVersionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, byte[] teamId, RosterAppendResult r, byte[]? prevHash, byte[] update, DateTimeOffset now)
    {
        await ChangeLog.LockAsync(conn, tx);
        await conn.ExecAsync(
            "INSERT INTO roster_versions (team_id, version, hash, prev_hash, roster_update, accepted_at) VALUES (@t, @v, @h, @p, @u, @now)", tx,
            ("t", teamId), ("v", (long)r.Version), ("h", r.Hash), ("p", prevHash), ("u", update), ("now", now));
    }

    /// <summary>
    /// Mirror the verified head into roster_*; removed members / newly revoked devices are blocked and purged. After a
    /// <paramref name="branchSwitch"/> the mirror only ever reduces: devices missing from the head are revoked too, and
    /// an existing member's role is never raised (the server ACL stays reductive, §4.2).
    /// </summary>
    private async Task ApplyProjectionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, byte[] teamId, ProjectionDto p, List<Guid> transfers,
        bool branchSwitch = false)
    {
        var members = p.Members.Select(m => (Id: Guid.ParseExact(m.MemberId, "N"), m.Role, m.Devices)).ToList();
        var memberIds = members.Select(m => m.Id).ToArray();
        foreach (var (id, role, _) in members)
            await conn.ExecAsync(
                "INSERT INTO roster_members (team_id, member_id, role, added_in_version) VALUES (@t, @m, @r, @v) " +
                "ON CONFLICT (team_id, member_id) DO UPDATE SET role = CASE WHEN @switch " +
                "AND (CASE excluded.role WHEN 'owner' THEN 3 WHEN 'admin' THEN 2 ELSE 1 END) > (CASE roster_members.role WHEN 'owner' THEN 3 WHEN 'admin' THEN 2 ELSE 1 END) " +
                "THEN roster_members.role ELSE excluded.role END", tx,
                ("t", teamId), ("m", id), ("r", role), ("v", p.Version), ("switch", branchSwitch));
        var removed = await conn.ListAsync(
            "UPDATE roster_members SET removed_in_version = @v WHERE team_id = @t AND removed_in_version IS NULL AND member_id <> ALL(@ids) RETURNING member_id", tx,
            r => r.GetGuid(0), ("v", p.Version), ("t", teamId), ("ids", memberIds));

        var newlyRevoked = new List<Guid>();
        foreach (var (memberId, _, devices) in members)
        foreach (var d in devices)
        {
            var deviceId = Guid.ParseExact(d.DeviceId, "N");
            var revokedNow = await conn.ScalarAsync<Guid?>(
                "INSERT INTO roster_devices (team_id, device_id, member_id, identity_key_hash, mls_key_hash, cert_hash, added_in_version, revoked_in_version) " +
                "VALUES (@t, @d, @m, @ik, @mk, @ch, @av, @rv) " +
                "ON CONFLICT (team_id, device_id) DO UPDATE SET revoked_in_version = coalesce(roster_devices.revoked_in_version, excluded.revoked_in_version) " +
                "RETURNING CASE WHEN revoked_in_version = @v THEN device_id END", tx,
                ("t", teamId), ("d", deviceId), ("m", memberId), ("ik", Convert.FromHexString(d.IdentityKeyHash)), ("mk", Convert.FromHexString(d.MlsKeyHash)),
                ("ch", Convert.FromHexString(d.CertHash)), ("av", d.AddedInVersion), ("rv", d.Revoked?.Version), ("v", p.Version));
            if (revokedNow is { } x) newlyRevoked.Add(x);
        }
        if (branchSwitch)
        {
            // Devices of remaining members that only the discarded branch added (removed members are blocked as a whole).
            var listed = members.SelectMany(m => m.Devices).Select(d => Guid.ParseExact(d.DeviceId, "N")).ToArray();
            newlyRevoked.AddRange(await conn.ListAsync(
                "UPDATE roster_devices SET revoked_in_version = @v WHERE team_id = @t AND revoked_in_version IS NULL " +
                "AND member_id = ANY(@members) AND device_id <> ALL(@ids) RETURNING device_id", tx,
                r => r.GetGuid(0), ("v", p.Version), ("t", teamId), ("members", memberIds), ("ids", listed)));
        }

        await conn.ExecAsync("DELETE FROM roster_access WHERE team_id = @t", tx, ("t", teamId));
        foreach (var a in p.Access)
            await conn.ExecAsync("INSERT INTO roster_access (team_id, project_id, member_id, level) VALUES (@t, @p, @m, @l)", tx,
                ("t", teamId), ("p", Guid.ParseExact(a.ProjectId, "N")), ("m", Guid.ParseExact(a.MemberId, "N")), ("l", a.Level));
        await conn.ExecAsync("UPDATE teams SET access_revision = access_revision + 1 WHERE team_id = @t", tx, ("t", teamId));

        // §5.1 п.3: the same transaction blocks, purges queues/transfers, revokes sessions and closes sockets.
        foreach (var m in removed)
        {
            var (_, t) = await revocation.ApplyAsync(conn, tx,
                new RevocationRequest(RevocationKind.MemberRemove, teamId, m, "admin", "roster", $"removed in roster v{p.Version}"),
                Guid.CreateVersion7(), live: false, journal: false);
            transfers.AddRange(t);
        }
        foreach (var d in newlyRevoked)
        {
            var (_, t) = await revocation.ApplyAsync(conn, tx,
                new RevocationRequest(RevocationKind.DeviceRevoke, teamId, d, "admin", "roster", $"revoked in roster v{p.Version}"),
                Guid.CreateVersion7(), live: false, journal: false);
            transfers.AddRange(t);
        }
        // A rotation is done once the roster no longer contains the blocked member/device; joins once the member is in.
        await conn.ExecAsync(
            "UPDATE pending_rotations pr SET resolved_at = @now WHERE team_id = @t AND resolved_at IS NULL AND (" +
            "(blocked_member_id IS NOT NULL AND blocked_member_id <> ALL(@ids)) OR " +
            "(blocked_device_id IS NOT NULL AND EXISTS (SELECT 1 FROM roster_devices d WHERE d.team_id = pr.team_id AND d.device_id = pr.blocked_device_id AND d.revoked_in_version IS NOT NULL)))", tx,
            ("now", clock.GetUtcNow()), ("t", teamId), ("ids", memberIds));
        await conn.ExecAsync("UPDATE join_requests SET resolved_at = @now WHERE team_id = @t AND resolved_at IS NULL AND member_id = ANY(@ids)", tx,
            ("now", clock.GetUtcNow()), ("t", teamId), ("ids", memberIds));
        // R2-05: invites created by a device that is no longer an active owner/admin device stop working at once.
        await conn.ExecAsync(
            "UPDATE invites SET expires_at = @now WHERE team_id = @t AND expires_at > @now AND created_by_device NOT IN " +
            "(SELECT device_id FROM team_device_access WHERE team_id = @t AND role IN ('owner', 'admin'))", tx,
            ("now", clock.GetUtcNow()), ("t", teamId));
    }
}
