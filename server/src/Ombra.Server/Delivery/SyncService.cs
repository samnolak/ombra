using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;

namespace Ombra.Server.Delivery;

/// <summary>
/// Writers of change_seq rows (roster_versions, blocks, pending_rotations, join_requests) take this transaction lock
/// before inserting, so commit order equals change_seq order and a reader never skips a lower, later-committed seq.
/// </summary>
public static class ChangeLog
{
    public const long LockKey = 0x4F4D4252_41000002;

    public static Task LockAsync(NpgsqlConnection conn, NpgsqlTransaction tx) =>
        conn.ExecAsync("SELECT pg_advisory_xact_lock(@k)", tx, ("k", LockKey));
}

/// <summary>
/// GET /v1/sync (§6.1, N7): only queue rows where the token's device is pending, roster versions, blocks and pending
/// rotations of teams where the device is active. Cursor = <c>v1.&lt;cursorEpoch&gt;.&lt;deliverySeq&gt;.&lt;changeSeq&gt;</c>;
/// a cursor of another cursor epoch (restore) or a malformed one gets 410 with a snapshot.
/// </summary>
public sealed class SyncService(NpgsqlDataSource db, IOptions<OmbraOptions> options)
{
    private readonly LimitsOptions _limits = options.Value.Limits;

    private sealed record Cursor(Guid Epoch, long DeliverySeq, long ChangeSeq);

    public async Task<(int Status, SyncResponse Body)> SyncAsync(DeviceIdentity device, string? cursorText)
    {
        await using var conn = await db.OpenConnectionAsync();
        var cursorEpoch = Guid.Parse((await conn.ScalarAsync<string>("SELECT value FROM server_state WHERE key = 'cursor_epoch'", null))!);
        var cursor = Parse(cursorText);
        var snapshot = cursorText is not null && (cursor is null || cursor.Epoch != cursorEpoch);
        if (cursor is null || cursor.Epoch != cursorEpoch) cursor = new Cursor(cursorEpoch, 0, 0);

        var teams = await conn.ListAsync(
            "SELECT a.team_id, t.quarantine, t.frozen_reason IS NOT NULL FROM team_device_access a JOIN teams t ON t.team_id = a.team_id WHERE a.device_id = @d", null,
            r => (Team: r.Bytes(0), Quarantine: r.GetBoolean(1), Frozen: r.GetBoolean(2)), ("d", device.DeviceId));
        if (teams.Count == 0)
        {
            var known = await AccessService.TeamsOfDeviceAsync(conn, null, device.DeviceId);
            if (known.Count > 0)
                throw ApiException.Forbidden(await AccessService.DenyReasonAsync(conn, null, known[0], device.DeviceId) ?? Reasons.Forbidden);
        }
        var all = teams.Select(t => t.Team).ToArray();
        // Content only for teams that are neither in quarantine (§1) nor frozen (equivocation, R2-08); roster/blocks flow anyway.
        var content = teams.Where(t => !t.Quarantine && !t.Frozen).Select(t => t.Team).ToArray();

        var ops = new List<SyncOp>();
        long bytes = 0;
        var more = false;
        await using (var cmd = conn.Cmd(
            "SELECT seq, group_id, sender_device_id, op_id, epoch, ciphertext, kind FROM delivery " +
            "WHERE pending_device_ids @> ARRAY[@d]::uuid[] AND seq > @after AND team_id = ANY(@teams) ORDER BY seq LIMIT @n", null,
            ("d", device.DeviceId), ("after", cursor.DeliverySeq), ("teams", content), ("n", _limits.SyncMaxOps + 1)))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var ct = r.Bytes(5);
                if (ops.Count == _limits.SyncMaxOps || (ops.Count > 0 && bytes + ct.Length > _limits.SyncMaxBytes)) { more = true; break; }
                ops.Add(new SyncOp(r.GetInt64(0), Ids.Hex(r.Bytes(1)), r.GetGuid(2), r.GetGuid(3), (ulong)r.GetInt64(4), ct, r.GetString(6)));
                bytes += ct.Length;
            }
        }

        var roster = await conn.ListAsync(
            "SELECT team_id, version, roster_update, change_seq FROM roster_versions WHERE team_id = ANY(@teams) AND change_seq > @c ORDER BY change_seq", null,
            r => (Row: new SyncRoster(Ids.Hex(r.Bytes(0)), r.GetInt64(1), r.Bytes(2)), Seq: r.GetInt64(3)), ("teams", all), ("c", cursor.ChangeSeq));
        var blocks = await conn.ListAsync(
            "SELECT team_id, member_id, device_id, by_actor, at, audit_id, change_seq FROM blocks WHERE team_id = ANY(@teams) AND change_seq > @c ORDER BY change_seq", null,
            r => (Row: new SyncBlock(Ids.Hex(r.Bytes(0)), r.GuidOrNull(1), r.GuidOrNull(2), r.GetString(3), r.Time(4), r.GetGuid(5)), Seq: r.GetInt64(6)),
            ("teams", all), ("c", cursor.ChangeSeq));
        var rotations = await conn.ListAsync(
            "SELECT rotation_id, team_id, blocked_member_id, blocked_device_id, audit_id, group_ids, created_at, change_seq FROM pending_rotations " +
            "WHERE team_id = ANY(@teams) AND resolved_at IS NULL AND change_seq > @c ORDER BY change_seq", null,
            r => (Row: new SyncPendingRotation(r.GetGuid(0), Ids.Hex(r.Bytes(1)), r.GuidOrNull(2), r.GuidOrNull(3), r.GetGuid(4),
                ((byte[][])r[5]).Select(g => Ids.Hex(g)).ToList(), r.Time(6)), Seq: r.GetInt64(7)),
            ("teams", all), ("c", cursor.ChangeSeq));

        var maxChange = new[] { cursor.ChangeSeq }
            .Concat(roster.Select(x => x.Seq)).Concat(blocks.Select(x => x.Seq)).Concat(rotations.Select(x => x.Seq)).Max();
        var next = new Cursor(cursorEpoch, more ? ops[^1].Seq : 0, maxChange);
        var body = new SyncResponse(Format(next), more, snapshot || cursorText is null,
            ops, roster.Select(x => x.Row).ToList(), blocks.Select(x => x.Row).ToList(), rotations.Select(x => x.Row).ToList(),
            teams.Where(t => t.Quarantine).Select(t => Ids.Hex(t.Team)).ToList(),
            teams.Where(t => t.Frozen).Select(t => Ids.Hex(t.Team)).ToList());
        return (snapshot ? 410 : 200, body);
    }

    private static string Format(Cursor c) => $"v1.{c.Epoch:N}.{c.DeliverySeq}.{c.ChangeSeq}";

    private static Cursor? Parse(string? s)
    {
        if (s is null) return null;
        var parts = s.Split('.');
        if (parts.Length != 4 || parts[0] != "v1") return null;
        if (!Guid.TryParseExact(parts[1], "N", out var epoch)) return null;
        if (!long.TryParse(parts[2], out var d) || d < 0) return null;
        if (!long.TryParse(parts[3], out var c) || c < 0) return null;
        return new Cursor(epoch, d, c);
    }
}
