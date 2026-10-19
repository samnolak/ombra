using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;
using Ombra.Server.Delivery;
using Ombra.Server.Logs;
using Ombra.Server.Transfers;

namespace Ombra.Server.Blobs;

internal sealed record BlobRow(Guid BlobId, byte[] TeamId, byte[] GroupId, string GroupKind, Guid UploaderMemberId, Guid UploaderDeviceId, long Size, byte[] Sha256);

/// <summary>
/// Attachment ciphertext (§3.3, R5-05, N6, N11).
/// <list type="bullet">
/// <item>PUT: signed envelope with scope = groupId; uploader must be an unblocked leaf of that group.</item>
/// <item>GET: device token; the device must be an unblocked leaf of the blob's group at request time.</item>
/// <item>DELETE: signed envelope; the uploading member (any of its devices), or an admin/owner of the team for a
/// <c>space</c> (team/project) blob whose uploader is not currently owner/admin (N6). Every deletion — DELETE and
/// TTL — is appended to blob-deletions.log (fsync) before the row and file are removed.</item>
/// </list>
/// </summary>
public sealed class BlobService(NpgsqlDataSource db, TimeProvider clock, IOptions<OmbraOptions> options, TransferStore store, ILogger<BlobService> log)
{
    private readonly OmbraOptions _o = options.Value;

    public HashChainLog DeletionLog => new(_o.BlobDeletionsLogPath, _o.BlobDeletionsSink,
        e => log.LogError(e, "blob-deletions.log sink write failed ({Sink})", _o.BlobDeletionsSink));

    private static Task<BlobRow?> RowAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id) =>
        conn.SingleAsync(
            "SELECT b.blob_id, b.team_id, b.group_id, g.kind, b.uploader_member_id, b.uploader_device_id, b.size, b.sha256 " +
            "FROM blobs b JOIN groups g ON g.group_id = b.group_id WHERE b.blob_id = @id", tx,
            r => new BlobRow(r.GetGuid(0), r.Bytes(1), r.Bytes(2), r.GetString(3), r.GetGuid(4), r.GetGuid(5), r.GetInt64(6), r.Bytes(7)), ("id", id));

    private static async Task RequireLeafAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, byte[] groupId, Guid device)
    {
        var leaf = await conn.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM group_leaves WHERE group_id = @g AND device_id = @d)", tx,
            ("g", groupId), ("d", device));
        if (!leaf) throw ApiException.Forbidden(Reasons.NotMember, "device is not a member of the blob's group");
    }

    public async Task<BlobPutResponse> PutAsync(DeviceIdentity device, Guid blobId, VerifiedEnvelope env, HttpRequest request)
    {
        if (env.Envelope.Scope.Length != 32) throw ApiException.Bad("scope must be the groupId");
        BlobPutBody body;
        try { body = BlobPutBody.Parse(env.Envelope.BodyMap()); }
        catch (WireFormatException e) { throw ApiException.Bad(e.Message); }
        if (body.BlobId != blobId) throw ApiException.Bad("blobId mismatch");
        if (body.Size == 0 || body.Size > (ulong)_o.Limits.BlobMaxBytes) throw new ApiException(413, Reasons.TooLarge, $"blob > {_o.Limits.BlobMaxBytes} bytes");
        var groupId = env.Envelope.Scope;

        await using (var conn = await db.OpenConnectionAsync())
        {
            var g = await DeliveryService.GroupAsync(conn, null, groupId, false) ?? throw ApiException.NotFound("group");
            await AccessService.RequireContentAsync(conn, null, g.TeamId, device.DeviceId);
            if (g.Closed) throw ApiException.Conflict(Reasons.ClosedGeneration);
            await RequireLeafAsync(conn, null, groupId, device.DeviceId);
            var existing = await RowAsync(conn, null, blobId);
            if (existing is not null)
            {
                if (existing.UploaderDeviceId == device.DeviceId && existing.Sha256.AsSpan().SequenceEqual(body.Sha256))
                    return new BlobPutResponse(blobId, existing.Size, true);
                throw ApiException.Conflict(Reasons.Exists);
            }
        }

        var (tmp, length, sha) = await TransferStore.SpoolAsync(request.Body, _o.BlobDir, (long)body.Size, request.HttpContext.RequestAborted);
        try
        {
            if ((ulong)length != body.Size || !sha.AsSpan().SequenceEqual(body.Sha256))
                throw ApiException.Bad("body does not match the signed size/sha256");
            var now = clock.GetUtcNow();
            await using var conn = await db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var g = await DeliveryService.GroupAsync(conn, tx, groupId, forUpdate: true) ?? throw ApiException.NotFound("group");
            var (_, access) = await AccessService.RequireContentAsync(conn, tx, g.TeamId, device.DeviceId);
            await RequireLeafAsync(conn, tx, groupId, device.DeviceId);
            await conn.ExecAsync("SELECT pg_advisory_xact_lock(hashtextextended(encode(@t, 'hex'), 13))", tx, ("t", g.TeamId));
            var used = await conn.ScalarAsync<long>("SELECT coalesce(sum(size), 0)::bigint FROM blobs WHERE team_id = @t", tx, ("t", g.TeamId));
            if (used + length > _o.Limits.TeamBlobQuotaBytes) throw new ApiException(413, Reasons.TeamQuota);
            var stored = await EnvelopeDedupe.ClaimAsync(conn, tx, env, now);
            if (stored is not null) return EnvelopeDedupe.Read<BlobPutResponse>(stored) with { Duplicate = true };
            try
            {
                await conn.ExecAsync(
                    "INSERT INTO blobs (blob_id, team_id, group_id, uploader_member_id, uploader_device_id, size, sha256, created_at, last_access_at) " +
                    "VALUES (@id, @t, @g, @m, @d, @n, @sha, @now, @now)", tx,
                    ("id", blobId), ("t", g.TeamId), ("g", groupId), ("m", access.MemberId), ("d", device.DeviceId), ("n", length), ("sha", sha), ("now", now));
            }
            catch (PostgresException e) when (e.IsUniqueViolation())
            {
                throw ApiException.Conflict(Reasons.Exists);
            }
            var response = new BlobPutResponse(blobId, length, false);
            await EnvelopeDedupe.StoreAsync(conn, tx, env, 200, response);
            // File in place before the row commits: a committed row always has its file; a crash in between leaves
            // only an orphan file that the retention job removes.
            File.Move(tmp, store.BlobPath(blobId), overwrite: true);
            await tx.CommitAsync();
            return response;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>Returns the file path after the access check; 404 is decided before access (N11: deleted ⇒ 404).</summary>
    public async Task<(string Path, long Size)> GetAsync(DeviceIdentity device, Guid blobId)
    {
        await using var conn = await db.OpenConnectionAsync();
        var b = await RowAsync(conn, null, blobId) ?? throw ApiException.NotFound("blob");
        await AccessService.RequireContentAsync(conn, null, b.TeamId, device.DeviceId);
        await RequireLeafAsync(conn, null, b.GroupId, device.DeviceId);
        await conn.ExecAsync("UPDATE blobs SET last_access_at = @now WHERE blob_id = @id", null, ("now", clock.GetUtcNow()), ("id", blobId));
        var path = store.BlobPath(blobId);
        if (!File.Exists(path)) throw ApiException.NotFound("blob file");
        return (path, b.Size);
    }

    public async Task DeleteAsync(DeviceIdentity device, Guid blobId, VerifiedEnvelope env)
    {
        BlobDeleteBody body;
        try { body = BlobDeleteBody.Parse(env.Envelope.BodyMap()); }
        catch (WireFormatException e) { throw ApiException.Bad(e.Message); }
        if (body.BlobId != blobId) throw ApiException.Bad("blobId mismatch");

        await using (var conn = await db.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            var b = await RowAsync(conn, tx, blobId) ?? throw ApiException.NotFound("blob");
            if (!env.Envelope.Scope.AsSpan().SequenceEqual(b.GroupId)) throw ApiException.Bad("scope must be the blob's groupId");
            var (_, access) = await AccessService.RequireContentAsync(conn, tx, b.TeamId, device.DeviceId);
            if (access.MemberId != b.UploaderMemberId)
            {
                if (!access.IsAdmin) throw ApiException.Forbidden(Reasons.NotAdmin);
                if (b.GroupKind is not ("team" or "project"))
                    throw ApiException.Forbidden(Reasons.Forbidden, "admins may delete only space blobs");
                var uploaderPrivileged = await conn.ScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM roster_members WHERE team_id = @t AND member_id = @m AND role IN ('owner', 'admin') AND removed_in_version IS NULL)", tx,
                    ("t", b.TeamId), ("m", b.UploaderMemberId));
                if (uploaderPrivileged) throw ApiException.Forbidden(Reasons.Forbidden, "blob of an owner/admin (N6)");
            }
            var deleted = await conn.ScalarAsync<Guid?>("DELETE FROM blobs WHERE blob_id = @id RETURNING blob_id", tx, ("id", blobId));
            if (deleted is null) throw ApiException.NotFound("blob");
            // Journal first (fsync), then commit: a committed deletion is always in blob-deletions.log.
            DeletionLog.Append(new JsonObject
            {
                ["blobId"] = blobId.ToString(),
                ["by"] = $"member:{access.MemberId}/device:{device.DeviceId}",
            }, clock.GetUtcNow());
            await tx.CommitAsync();
        }
        store.DeleteBlob(blobId);
    }

    /// <summary>TTL deletion (retention job): journaled like DELETE with <c>by = "ttl"</c>.</summary>
    public async Task<int> ExpireIdleAsync(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-_o.Limits.BlobIdleTtlDays);
        var count = 0;
        while (true)
        {
            await using var conn = await db.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var ids = await conn.ListAsync(
                "SELECT blob_id FROM blobs WHERE last_access_at < @c ORDER BY last_access_at LIMIT 200 FOR UPDATE SKIP LOCKED", tx,
                r => r.GetGuid(0), ("c", cutoff));
            if (ids.Count == 0) return count;
            foreach (var id in ids)
                DeletionLog.Append(new JsonObject { ["blobId"] = id.ToString(), ["by"] = "ttl" }, now);
            await conn.ExecAsync("DELETE FROM blobs WHERE blob_id = ANY(@ids)", tx, ("ids", ids.ToArray()));
            await tx.CommitAsync();
            foreach (var id in ids) store.DeleteBlob(id);
            count += ids.Count;
        }
    }

    /// <summary>
    /// After restore (N11): delete every restored blob that the journal says was deleted after the backup.
    /// Local and off-host copies are reconciled like revocations.log.
    /// </summary>
    public async Task<int> ApplyDeletionLogAsync(string? offHostCopy)
    {
        var chain = HashChainLog.ReadVerified(_o.BlobDeletionsLogPath);
        if (offHostCopy is not null)
        {
            var merged = HashChainLog.Reconcile(chain, HashChainLog.ReadVerified(offHostCopy));
            if (!ReferenceEquals(merged, chain))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_o.BlobDeletionsLogPath))!);
                File.Copy(offHostCopy, _o.BlobDeletionsLogPath, overwrite: true);
                chain = merged;
            }
        }
        var ids = chain.Select(e => Guid.Parse(e.Entry["blobId"]!.GetValue<string>())).Distinct().ToArray();
        if (ids.Length == 0) return 0;
        await using var conn = await db.OpenConnectionAsync();
        var removed = await conn.ListAsync("DELETE FROM blobs WHERE blob_id = ANY(@ids) RETURNING blob_id", null, r => r.GetGuid(0), ("ids", ids));
        foreach (var id in ids) store.DeleteBlob(id);
        return removed.Count;
    }
}
