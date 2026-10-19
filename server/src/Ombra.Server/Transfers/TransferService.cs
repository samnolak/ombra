using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;

namespace Ombra.Server.Transfers;

internal sealed record TransferRow(Guid TransferId, byte[] TeamId, Guid SenderDeviceId, Guid SenderMemberId, Guid TargetDeviceId,
    long Size, byte[] Sha256, int TotalChunks, DateTimeOffset ExpiresAt);

/// <summary>
/// One-shot transfers (§3.3 R5-09, N3).
/// <list type="bullet">
/// <item>Create: signed envelope; admin → active device of a team member, member → own other device; else 403.</item>
/// <item>≤ 1 active transfer per target device (unique index), ≤ TransfersPerHour per sender member, size ≤ limit.</item>
/// <item>Chunks are uploaded only by the sender device; GET/ack only by the target device's token.</item>
/// <item>Deleted on ack, after TTL (24 h), or when the sender or target is blocked (RevocationService).</item>
/// </list>
/// </summary>
public sealed class TransferService(NpgsqlDataSource db, TimeProvider clock, IOptions<OmbraOptions> options, TransferStore store)
{
    private readonly LimitsOptions _limits = options.Value.Limits;

    public async Task<TransferInfo> CreateAsync(DeviceIdentity sender, Guid transferId, VerifiedEnvelope env)
    {
        if (env.Envelope.Scope.Length != 32) throw ApiException.Bad("scope must be the teamId");
        TransferCreateBody body;
        try { body = TransferCreateBody.Parse(env.Envelope.BodyMap()); }
        catch (WireFormatException e) { throw ApiException.Bad(e.Message); }
        if (body.TransferId != transferId) throw ApiException.Bad("transferId mismatch");
        var max = Math.Min(_limits.TransferMaxBytes, _limits.TeamBlobQuotaBytes);
        if (body.Size == 0 || body.Size > (ulong)max) throw new ApiException(413, Reasons.TooLarge, $"size must be 1..{max}");
        var expectedChunks = (body.Size + (ulong)_limits.TransferChunkPlainBytes - 1) / (ulong)_limits.TransferChunkPlainBytes;
        if (body.TotalChunks != expectedChunks) throw ApiException.Bad($"totalChunks must be {expectedChunks} for size {body.Size}");
        if (body.TargetDeviceId == sender.DeviceId) throw ApiException.Bad("target must be another device");

        var now = clock.GetUtcNow();
        var teamId = env.Envelope.Scope;
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var (_, access) = await AccessService.RequireContentAsync(conn, tx, teamId, sender.DeviceId);
        var stored = await EnvelopeDedupe.ClaimAsync(conn, tx, env, now);
        if (stored is not null) return EnvelopeDedupe.Read<TransferInfo>(stored);

        var target = await AccessService.ActiveAsync(conn, tx, teamId, body.TargetDeviceId)
                     ?? throw ApiException.Forbidden(Reasons.Forbidden, "target device is not active in the team");
        if (target.MemberId != access.MemberId && !access.IsAdmin)
            throw ApiException.Forbidden(Reasons.NotAdmin, "members may transfer only to their own devices");

        await conn.ExecAsync("SELECT pg_advisory_xact_lock(hashtextextended(@m::text, 11))", tx, ("m", access.MemberId));
        var recent = await conn.ScalarAsync<long>(
            "SELECT count(*) FROM transfer_rate WHERE sender_member_id = @m AND at > @since", tx,
            ("m", access.MemberId), ("since", now.AddHours(-1)));
        if (recent >= _limits.TransfersPerHour)
            throw new ApiException(429, Reasons.RateLimited, $"at most {_limits.TransfersPerHour} transfers per hour") { RetryAfter = TimeSpan.FromMinutes(10) };

        var expires = now.AddHours(_limits.TransferTtlHours);
        // A unique violation aborts the transaction; the ApiException below rolls it back.
        try
        {
            await conn.ExecAsync(
                "INSERT INTO transfers (transfer_id, team_id, sender_device_id, sender_member_id, target_device_id, target_member_id, size, sha256, " +
                "total_chunks, created_at, expires_at) VALUES (@id, @t, @sd, @sm, @td, @tm, @size, @sha, @n, @now, @exp)", tx,
                ("id", transferId), ("t", teamId), ("sd", sender.DeviceId), ("sm", access.MemberId), ("td", target.DeviceId),
                ("tm", target.MemberId), ("size", (long)body.Size), ("sha", body.Sha256), ("n", (int)body.TotalChunks), ("now", now), ("exp", expires));
        }
        catch (PostgresException e) when (e.IsUniqueViolation())
        {
            throw ApiException.Conflict(e.ConstraintName == "transfers_one_per_target" ? Reasons.TransferActive : Reasons.Exists);
        }
        await conn.ExecAsync("INSERT INTO transfer_rate (sender_member_id, at) VALUES (@m, @now)", tx, ("m", access.MemberId), ("now", now));
        var info = new TransferInfo(transferId, sender.DeviceId, target.DeviceId, (long)body.Size, body.Sha256, (int)body.TotalChunks, 0, expires);
        await EnvelopeDedupe.StoreAsync(conn, tx, env, 200, info);
        await tx.CommitAsync();
        return info;
    }

    private static Task<TransferRow?> RowAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, DateTimeOffset now) =>
        conn.SingleAsync(
            "SELECT transfer_id, team_id, sender_device_id, sender_member_id, target_device_id, size, sha256, total_chunks, expires_at " +
            "FROM transfers WHERE transfer_id = @id AND expires_at > @now", tx,
            r => new TransferRow(r.GetGuid(0), r.Bytes(1), r.GetGuid(2), r.GetGuid(3), r.GetGuid(4), r.GetInt64(5), r.Bytes(6), r.GetInt32(7), r.Time(8)),
            ("id", id), ("now", now));

    public async Task PutChunkAsync(DeviceIdentity sender, Guid id, int index, HttpRequest request)
    {
        TransferRow t;
        await using (var conn = await db.OpenConnectionAsync())
        {
            t = await RowAsync(conn, null, id, clock.GetUtcNow()) ?? throw ApiException.NotFound("transfer");
            if (t.SenderDeviceId != sender.DeviceId) throw ApiException.Forbidden(Reasons.Forbidden, "only the sender device uploads chunks");
            await AccessService.RequireContentAsync(conn, null, t.TeamId, sender.DeviceId);
            if (index < 0 || index >= t.TotalChunks) throw ApiException.Bad("chunk index out of range");
        }
        var maxChunk = _limits.TransferChunkPlainBytes + _limits.ChunkOverheadBytes;
        var (tmp, length, _) = await TransferStore.SpoolAsync(request.Body, store.TransferDir(id), maxChunk, request.HttpContext.RequestAborted);
        try
        {
            if (length == 0) throw ApiException.Bad("empty chunk");
            await using var conn = await db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            // Re-check under the row lock: the transfer may have been revoked/acked/expired while the body was streaming.
            var live = await conn.ScalarAsync<Guid?>(
                "SELECT transfer_id FROM transfers WHERE transfer_id = @id AND expires_at > @now FOR UPDATE", tx, ("id", id), ("now", clock.GetUtcNow()));
            if (live is null) throw ApiException.NotFound("transfer");
            var uploaded = await conn.ScalarAsync<long>("SELECT coalesce(sum(size), 0)::bigint FROM transfer_chunks WHERE transfer_id = @id", tx, ("id", id));
            if (uploaded + length > t.Size + (long)t.TotalChunks * _limits.ChunkOverheadBytes)
                throw new ApiException(413, Reasons.TooLarge, "chunks exceed the declared size");
            try
            {
                await conn.ExecAsync("INSERT INTO transfer_chunks (transfer_id, idx, size, is_final) VALUES (@id, @i, @n, @f)", tx,
                    ("id", id), ("i", index), ("n", (int)length), ("f", index == t.TotalChunks - 1));
            }
            catch (PostgresException e) when (e.IsUniqueViolation())
            {
                throw ApiException.Conflict(Reasons.Exists, "chunk already uploaded");
            }
            File.Move(tmp, store.ChunkPath(id, index), overwrite: false);
            await tx.CommitAsync();
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private async Task<TransferRow> RequireTargetAsync(NpgsqlConnection conn, DeviceIdentity device, Guid id)
    {
        var t = await RowAsync(conn, null, id, clock.GetUtcNow()) ?? throw ApiException.NotFound("transfer");
        if (t.TargetDeviceId != device.DeviceId) throw ApiException.Forbidden(Reasons.NotTarget);
        await AccessService.RequireContentAsync(conn, null, t.TeamId, device.DeviceId);
        return t;
    }

    public async Task<TransferInfo> InfoAsync(DeviceIdentity device, Guid id)
    {
        await using var conn = await db.OpenConnectionAsync();
        var t = await RequireTargetAsync(conn, device, id);
        var uploaded = await conn.ScalarAsync<long>("SELECT count(*) FROM transfer_chunks WHERE transfer_id = @id", null, ("id", id));
        return new TransferInfo(t.TransferId, t.SenderDeviceId, t.TargetDeviceId, t.Size, t.Sha256, t.TotalChunks, (int)uploaded, t.ExpiresAt);
    }

    /// <summary>Target only; repeatable (resume) until ack.</summary>
    public async Task<string> ChunkPathForTargetAsync(DeviceIdentity device, Guid id, int index)
    {
        await using var conn = await db.OpenConnectionAsync();
        await RequireTargetAsync(conn, device, id);
        var exists = await conn.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM transfer_chunks WHERE transfer_id = @id AND idx = @i)", null,
            ("id", id), ("i", index));
        if (!exists) throw ApiException.NotFound("chunk");
        return store.ChunkPath(id, index);
    }

    /// <summary>Target only: deletes the transfer and its ciphertext.</summary>
    public async Task AckAsync(DeviceIdentity device, Guid id)
    {
        await using (var conn = await db.OpenConnectionAsync())
        {
            await RequireTargetAsync(conn, device, id);
            await conn.ExecAsync("DELETE FROM transfers WHERE transfer_id = @id AND target_device_id = @d", null, ("id", id), ("d", device.DeviceId));
        }
        store.DeleteFiles(id);
    }
}
