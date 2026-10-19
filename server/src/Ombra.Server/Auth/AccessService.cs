using Npgsql;
using Ombra.Server.Contracts;
using Ombra.Server.Data;

namespace Ombra.Server.Auth;

public sealed record DeviceIdentity(Guid DeviceId, Guid MemberId, byte[] IdentityKey);

public sealed record TeamAccess(byte[] TeamId, Guid DeviceId, Guid MemberId, string Role)
{
    public bool IsAdmin => Role is "admin" or "owner";
}

public sealed record TeamRow(byte[] TeamId, long AccessRevision, long RosterVersion, bool Quarantine, string? QuarantineReleasedBy, string? Frozen);

/// <summary>
/// Server ACL (§4.2): right = roster projection allows AND no block. Every check reads <c>team_device_access</c>,
/// so a block committed by a revoke transaction is visible to every later transaction.
/// </summary>
public static class AccessService
{
    public static Task<DeviceIdentity?> DeviceAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid deviceId) =>
        conn.SingleAsync(
            "SELECT device_id, member_id, identity_key FROM devices WHERE device_id = @d AND revoked_at IS NULL", tx,
            r => new DeviceIdentity(r.GetGuid(0), r.GetGuid(1), r.Bytes(2)), ("d", deviceId));

    /// <summary>Member of a device, including revoked ones.</summary>
    public static Task<Guid?> MemberOfDeviceAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid deviceId) =>
        conn.ScalarAsync<Guid?>("SELECT member_id FROM devices WHERE device_id = @d", tx, ("d", deviceId));

    public static Task<TeamRow?> TeamAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, byte[] teamId) =>
        conn.SingleAsync(
            "SELECT team_id, access_revision, roster_version, quarantine, quarantine_released_by, frozen_reason FROM teams WHERE team_id = @t", tx,
            r => new TeamRow(r.Bytes(0), r.GetInt64(1), r.GetInt64(2), r.GetBoolean(3), r.StringOrNull(4), r.StringOrNull(5)), ("t", teamId));

    public static Task<TeamAccess?> ActiveAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, byte[] teamId, Guid deviceId) =>
        conn.SingleAsync(
            "SELECT team_id, device_id, member_id, role FROM team_device_access WHERE team_id = @t AND device_id = @d", tx,
            r => new TeamAccess(r.Bytes(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3)), ("t", teamId), ("d", deviceId));

    /// <summary>Why a device has no access to a team (403 reason), or null if it has.</summary>
    public static async Task<string?> DenyReasonAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, byte[] teamId, Guid deviceId)
    {
        if (await ActiveAsync(conn, tx, teamId, deviceId) is not null) return null;
        var row = await conn.SingleAsync(
            """
            SELECT d.member_id,
                   EXISTS (SELECT 1 FROM blocks b WHERE b.team_id = @t AND b.member_id = d.member_id) AS member_blocked,
                   EXISTS (SELECT 1 FROM roster_members m WHERE m.team_id = @t AND m.member_id = d.member_id AND m.removed_in_version IS NOT NULL) AS member_removed,
                   EXISTS (SELECT 1 FROM blocks b WHERE b.team_id = @t AND b.device_id = d.device_id) AS device_blocked,
                   d.revoked_at IS NOT NULL OR EXISTS (SELECT 1 FROM roster_devices rd WHERE rd.team_id = @t AND rd.device_id = d.device_id AND rd.revoked_in_version IS NOT NULL) AS device_revoked
            FROM devices d WHERE d.device_id = @d
            """, tx,
            r => new[] { r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4) }, ("t", teamId), ("d", deviceId));
        if (row is null) return Reasons.NotMember;
        if (row[0] || row[1]) return Reasons.Removed;
        if (row[2]) return Reasons.Blocked;
        if (row[3]) return Reasons.Revoked;
        return Reasons.NotMember;
    }

    /// <summary>Access for content operations: active device, team not in quarantine and not frozen.</summary>
    public static async Task<(TeamRow Team, TeamAccess Access)> RequireContentAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, byte[] teamId, Guid deviceId)
    {
        var team = await TeamAsync(conn, tx, teamId) ?? throw ApiException.NotFound("team");
        var access = await RequireActiveAsync(conn, tx, teamId, deviceId);
        if (team.Quarantine) throw ApiException.Forbidden(Reasons.Quarantine);
        if (team.Frozen is not null) throw ApiException.Forbidden(Reasons.Frozen, team.Frozen);
        return (team, access);
    }

    public static async Task<TeamAccess> RequireActiveAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, byte[] teamId, Guid deviceId)
    {
        var access = await ActiveAsync(conn, tx, teamId, deviceId);
        if (access is not null) return access;
        throw ApiException.Forbidden(await DenyReasonAsync(conn, tx, teamId, deviceId) ?? Reasons.Forbidden);
    }

    /// <summary>All teams where the device appears in the roster projection.</summary>
    public static Task<List<byte[]>> TeamsOfDeviceAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid deviceId) =>
        conn.ListAsync("SELECT DISTINCT team_id FROM roster_devices WHERE device_id = @d", tx, r => r.Bytes(0), ("d", deviceId));
}
