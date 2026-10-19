using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;
using Ombra.Server.Streaming;

namespace Ombra.Server.Delivery;

public sealed record GroupRow(byte[] GroupId, byte[] TeamId, string Kind, long Epoch, bool Closed);

/// <summary>
/// Delivery queue (§3.0, R5-06, N7, N8).
/// <para>Invariant R5-06: a row's pending list is computed while holding <c>SELECT … FOR UPDATE</c> on the group row,
/// from <c>team_device_access</c> (roster ∧ no block). The revoke transaction takes the same row locks (all groups of
/// the team, ordered) before purging, so either the insert commits first and the purge removes the device, or the
/// block commits first and the insert does not see the device.</para>
/// </summary>
public sealed class DeliveryService(NpgsqlDataSource db, TimeProvider clock, IOptions<OmbraOptions> options, StreamHub hub, OpRateLimiter rate)
{
    private readonly LimitsOptions _limits = options.Value.Limits;

    public static Task<GroupRow?> GroupAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, byte[] groupId, bool forUpdate) =>
        conn.SingleAsync(
            "SELECT group_id, team_id, kind, epoch, closed_at IS NOT NULL FROM groups WHERE group_id = @g" + (forUpdate ? " FOR UPDATE" : ""), tx,
            r => new GroupRow(r.Bytes(0), r.Bytes(1), r.GetString(2), r.GetInt64(3), r.GetBoolean(4)), ("g", groupId));

    /// <summary>Unblocked leaves of the current epoch except the sender.</summary>
    private static Task<List<Guid>> RecipientsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, GroupRow g, Guid sender) =>
        conn.ListAsync(
            "SELECT gl.device_id FROM group_leaves gl " +
            "JOIN team_device_access a ON a.team_id = @t AND a.device_id = gl.device_id " +
            "WHERE gl.group_id = @g AND gl.device_id <> @s ORDER BY gl.device_id", tx,
            r => r.GetGuid(0), ("t", g.TeamId), ("g", g.GroupId), ("s", sender));

    private static async Task RequireLeafAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, GroupRow g, Guid device)
    {
        var leaf = await conn.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM group_leaves WHERE group_id = @g AND device_id = @d)", tx,
            ("g", g.GroupId), ("d", device));
        if (!leaf) throw ApiException.Forbidden(Reasons.NotMember, "device is not a leaf of the group");
    }

    public async Task<OpPutResponse> PutAsync(DeviceIdentity sender, byte[] groupId, VerifiedEnvelope env)
    {
        if (!env.Envelope.Scope.AsSpan().SequenceEqual(groupId)) throw ApiException.Bad("envelope scope must be the groupId");
        OpPutBody body;
        try { body = OpPutBody.Parse(env.Envelope.BodyMap()); }
        catch (WireFormatException e) { throw ApiException.Bad(e.Message); }
        if (body.Ciphertext.Length == 0) throw ApiException.Bad("empty ciphertext");
        if (body.Ciphertext.Length > _limits.OpMaxBytes) throw new ApiException(413, Reasons.TooLarge, $"operation > {_limits.OpMaxBytes} bytes");
        if (!rate.TryTake(sender.DeviceId, out var retry))
            throw new ApiException(429, Reasons.RateLimited) { RetryAfter = retry };

        return body.Ephemeral ? await EphemeralAsync(sender, groupId, body) : await PersistAsync(sender, groupId, env, body);
    }

    /// <summary>Progress P3: relay to online unblocked leaves only, nothing stored (§3.0 Presence).</summary>
    private async Task<OpPutResponse> EphemeralAsync(DeviceIdentity sender, byte[] groupId, OpPutBody body)
    {
        await using var conn = await db.OpenConnectionAsync();
        var g = await GroupAsync(conn, null, groupId, false) ?? throw ApiException.NotFound("group");
        await AccessService.RequireContentAsync(conn, null, g.TeamId, sender.DeviceId);
        if (g.Closed) throw ApiException.Conflict(Reasons.ClosedGeneration);
        if ((long)body.Epoch != g.Epoch) throw ApiException.Conflict(Reasons.EpochConflict, $"current epoch {g.Epoch}");
        await RequireLeafAsync(conn, null, g, sender.DeviceId);
        var online = (await RecipientsAsync(conn, null, g, sender.DeviceId)).Where(hub.IsOnline).ToList();
        hub.Send(online, new StreamEvent(StreamEventTypes.Ephemeral, TeamId: Ids.Hex(g.TeamId), GroupId: Ids.Hex(g.GroupId),
            SenderDeviceId: sender.DeviceId, Ciphertext: body.Ciphertext));
        return new OpPutResponse(0, false, online.Count);
    }

    private async Task<OpPutResponse> PersistAsync(DeviceIdentity sender, byte[] groupId, VerifiedEnvelope env, OpPutBody body)
    {
        var now = clock.GetUtcNow();
        List<Guid> pending;
        OpPutResponse response;
        GroupRow g;
        await using (var conn = await db.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            g = await GroupAsync(conn, tx, groupId, forUpdate: true) ?? throw ApiException.NotFound("group");
            var (_, access) = await AccessService.RequireContentAsync(conn, tx, g.TeamId, sender.DeviceId);
            var stored = await EnvelopeDedupe.ClaimAsync(conn, tx, env, now);
            if (stored is not null)
            {
                await tx.RollbackAsync();
                return EnvelopeDedupe.Read<OpPutResponse>(stored) with { Duplicate = true };
            }
            if (g.Closed) throw ApiException.Conflict(Reasons.ClosedGeneration);
            if ((long)body.Epoch != g.Epoch) throw ApiException.Conflict(Reasons.EpochConflict, $"current epoch {g.Epoch}");
            await RequireLeafAsync(conn, tx, g, sender.DeviceId);

            pending = await RecipientsAsync(conn, tx, g, sender.DeviceId);
            if (pending.Count == 0)
            {
                response = new OpPutResponse(0, false, 0);
            }
            else
            {
                var size = body.Ciphertext.Length;
                var evicted = await EnforceSenderQuotaAsync(conn, tx, g.TeamId, access.MemberId, size);
                if (evicted >= 0)
                {
                    // N8: only this sender is refused; its own oldest operations were evicted to make room for a retry.
                    await conn.ExecAsync("DELETE FROM envelope_results WHERE device_id = @d AND op_id = @o", tx,
                        ("d", env.Envelope.DeviceId), ("o", env.Envelope.OpId));
                    await tx.CommitAsync();
                    throw new ApiException(429, Reasons.SenderQuota, $"evicted {evicted} own operations") { RetryAfter = TimeSpan.FromSeconds(1) };
                }
                var seq = await conn.ScalarAsync<long>(
                    "INSERT INTO delivery (group_id, sender_device_id, op_id, team_id, sender_member_id, epoch, ciphertext, size, " +
                    "pending_device_ids, created_at, expires_at) VALUES (@g, @sd, @o, @t, @sm, @e, @c, @n, @p, @now, @exp) RETURNING seq", tx,
                    ("g", g.GroupId), ("sd", sender.DeviceId), ("o", env.Envelope.OpId), ("t", g.TeamId), ("sm", access.MemberId),
                    ("e", g.Epoch), ("c", body.Ciphertext), ("n", size), ("p", pending.ToArray()), ("now", now),
                    ("exp", now.AddDays(_limits.QueueTtlDays)));
                response = new OpPutResponse(seq, false, pending.Count);
            }
            await EnvelopeDedupe.StoreAsync(conn, tx, env, 200, response);
            await tx.CommitAsync();
        }
        if (pending.Count > 0)
            hub.Send(pending, new StreamEvent(StreamEventTypes.Revision, TeamId: Ids.Hex(g.TeamId), GroupId: Ids.Hex(g.GroupId)));
        return response;
    }

    /// <summary>
    /// N8: share = max(MemberQueueMinBytes, TeamQueueBytes / active members). Returns -1 when the new operation fits;
    /// otherwise evicts the sender member's own oldest rows until it would fit and returns how many were evicted.
    /// Serialized per (team, member) by a transaction advisory lock.
    /// </summary>
    private async Task<int> EnforceSenderQuotaAsync(NpgsqlConnection conn, NpgsqlTransaction tx, byte[] teamId, Guid member, int size)
    {
        await conn.ExecAsync("SELECT pg_advisory_xact_lock(hashtextextended(encode(@t, 'hex') || @m::text, 7))", tx, ("t", teamId), ("m", member));
        var members = await conn.ScalarAsync<long>(
            "SELECT count(*) FROM roster_members WHERE team_id = @t AND removed_in_version IS NULL", tx, ("t", teamId));
        var share = Math.Max(_limits.MemberQueueMinBytes, _limits.TeamQueueBytes / Math.Max(1, members));
        var used = await conn.ScalarAsync<long>(
            "SELECT coalesce(sum(size), 0)::bigint FROM delivery WHERE team_id = @t AND sender_member_id = @m", tx, ("t", teamId), ("m", member));
        if (used + size <= share) return -1;

        var own = await conn.ListAsync(
            "SELECT seq, size FROM delivery WHERE team_id = @t AND sender_member_id = @m ORDER BY seq", tx,
            r => (Seq: r.GetInt64(0), Size: r.GetInt32(1)), ("t", teamId), ("m", member));
        var victims = new List<long>();
        foreach (var (seq, rowSize) in own)
        {
            if (used + size <= share) break;
            victims.Add(seq);
            used -= rowSize;
        }
        await conn.ExecAsync("DELETE FROM delivery WHERE seq = ANY(@s)", tx, ("s", victims.ToArray()));
        return victims.Count;
    }

    /// <summary>
    /// N7: the acknowledging device comes only from the token; acking a row where it is not pending is a no-op.
    /// Rows whose pending list becomes empty are deleted in the same transaction.
    /// </summary>
    public async Task<AckResponse> AckAsync(DeviceIdentity device, byte[] groupId, AckRequest request)
    {
        if (request.Ops is null || request.Ops.Count == 0) return new AckResponse(0, 0);
        if (request.Ops.Count > 1000) throw ApiException.Bad("at most 1000 ops per ack");
        var senders = request.Ops.Select(o => o.SenderDeviceId).ToArray();
        var ops = request.Ops.Select(o => o.OpId).ToArray();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var acked = await conn.ExecAsync(
            "UPDATE delivery SET pending_device_ids = array_remove(pending_device_ids, @dev) " +
            "WHERE group_id = @g AND pending_device_ids @> ARRAY[@dev]::uuid[] " +
            "AND (sender_device_id, op_id) IN (SELECT * FROM unnest(@s::uuid[], @o::uuid[]))", tx,
            ("dev", device.DeviceId), ("g", groupId), ("s", senders), ("o", ops));
        var deleted = await conn.ExecAsync(
            "DELETE FROM delivery WHERE group_id = @g AND cardinality(pending_device_ids) = 0", tx, ("g", groupId));
        await tx.CommitAsync();
        return new AckResponse(acked, deleted);
    }
}
