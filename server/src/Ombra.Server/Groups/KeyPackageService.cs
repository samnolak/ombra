using Npgsql;
using Ombra.Core.Interop;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;

namespace Ombra.Server.Groups;

/// <summary>
/// KeyPackages (§6.1, SD-11, N1).
/// <list type="bullet">
/// <item>Upload: ombra-core verifies the KP (signature, credential); it must be the token device's own KP with the
/// MLS key registered for it, lifetime ≤ 30 days.</item>
/// <item>Issue only to those who may Add: an admin/owner of the team, the device's own member, or the peer of an
/// open pair group of the team (else 403).</item>
/// <item>Limits: one successfully used KP per (requester, target) in 24 h (429) — "used" means a commit with its Add
/// was accepted (<see cref="GroupService"/>), so after a 409/rejected commit a new one is issued; 30 issues per hour
/// per requester (429). Last-resort KPs go only to admins; a non-admin with an empty stock gets 404.</item>
/// </list>
/// </summary>
public sealed class KeyPackageService(NpgsqlDataSource db, TimeProvider clock)
{
    public const int MaxStockPerDevice = 200;
    public const int IssuesPerHour = 30;
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30) + TimeSpan.FromHours(1);

    public async Task<KeyPackageUploadResponse> UploadAsync(DeviceIdentity device, KeyPackageUpload req)
    {
        if (req.KeyPackages is not { Count: > 0 and <= 100 }) throw ApiException.Bad("1..100 key packages per upload");
        var now = clock.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync();
        var mlsKey = await conn.ScalarAsync<byte[]>("SELECT mls_signature_key FROM devices WHERE device_id = @d", null, ("d", device.DeviceId));
        var stored = 0;
        foreach (var kp in req.KeyPackages)
        {
            KeyPackageInfo info;
            try { info = OmbraPublic.InspectKeyPackage(kp); }
            catch (OmbraException e) { throw ApiException.Bad($"{e.Code}: {e.Detail}"); }
            if (Ids.FromBytes(info.DeviceId) != device.DeviceId) throw ApiException.Forbidden(Reasons.Forbidden, "KeyPackage of another device");
            if (mlsKey is null || !info.MlsSignatureKey.AsSpan().SequenceEqual(mlsKey)) throw ApiException.Forbidden(Reasons.Forbidden, "MLS key differs from the registered one");
            var notAfter = DateTimeOffset.FromUnixTimeSeconds((long)info.NotAfter);
            if (notAfter <= now || notAfter > now + MaxLifetime) throw ApiException.Bad("KeyPackage lifetime must end within 30 days");
            stored += await conn.ExecAsync(
                "INSERT INTO key_packages (kp_ref, device_id, key_package, last_resort, not_after, uploaded_at) " +
                "SELECT @r, @d, @kp, @lr, @na, @now WHERE (SELECT count(*) FROM key_packages WHERE device_id = @d AND issued_at IS NULL) < @max " +
                "ON CONFLICT (kp_ref) DO NOTHING", null,
                ("r", info.KpRef), ("d", device.DeviceId), ("kp", kp), ("lr", info.LastResort), ("na", notAfter), ("now", now), ("max", (long)MaxStockPerDevice));
        }
        return new KeyPackageUploadResponse(stored);
    }

    /// <param name="groupId">Optional: the group the Add is for. The N1 24 h limit then counts only Adds of this
    /// device into that group, so one admin can add a device to several groups the same day.</param>
    public async Task<KeyPackageResponse> IssueAsync(DeviceIdentity requester, Guid targetDevice, byte[] teamId, byte[]? groupId = null)
    {
        var now = clock.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var r = await AccessService.RequireActiveAsync(conn, tx, teamId, requester.DeviceId);
        var t = await AccessService.ActiveAsync(conn, tx, teamId, targetDevice) ?? throw ApiException.Forbidden(Reasons.Forbidden, "target device is not active in the team");
        var allowed = r.MemberId == t.MemberId || r.IsAdmin || await conn.ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM groups WHERE team_id = @t AND kind = 'pair' AND closed_at IS NULL AND " +
            "((member_a = @x AND member_b = @y) OR (member_a = @y AND member_b = @x)))", tx, ("t", teamId), ("x", r.MemberId), ("y", t.MemberId));
        if (!allowed) throw ApiException.Forbidden(Reasons.Forbidden, "no right to Add this device (N1)");

        await conn.ExecAsync("SELECT pg_advisory_xact_lock(hashtextextended(@d::text, 17))", tx, ("d", requester.DeviceId));
        var lastHour = await conn.ScalarAsync<long>("SELECT count(*) FROM kp_issuance WHERE requester_device_id = @r AND issued_at > @since", tx,
            ("r", requester.DeviceId), ("since", now.AddHours(-1)));
        if (lastHour >= IssuesPerHour) throw new ApiException(429, Reasons.RateLimited, $"at most {IssuesPerHour} KeyPackages per hour") { RetryAfter = TimeSpan.FromMinutes(5) };
        var usedRecently = await conn.ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM kp_issuance WHERE requester_device_id = @r AND target_device_id = @d AND consumed_at > @since " +
            "AND (@g::bytea IS NULL OR consumed_group = @g))", tx,
            ("r", requester.DeviceId), ("d", targetDevice), ("since", now.AddHours(-24)), ("g", groupId));
        if (usedRecently) throw new ApiException(429, Reasons.RateLimited, "a KeyPackage of this device was already used by you in the last 24 h") { RetryAfter = TimeSpan.FromHours(1) };

        var kp = await conn.SingleAsync(
            "UPDATE key_packages SET issued_at = @now WHERE kp_ref = (SELECT kp_ref FROM key_packages WHERE device_id = @d AND issued_at IS NULL " +
            "AND NOT last_resort AND not_after > @now ORDER BY uploaded_at LIMIT 1 FOR UPDATE SKIP LOCKED) RETURNING kp_ref, key_package", tx,
            x => new { Ref = x.Bytes(0), Kp = x.Bytes(1) }, ("now", now), ("d", targetDevice));
        var lastResort = false;
        if (kp is null)
        {
            if (!r.IsAdmin) throw ApiException.NotFound("no KeyPackage in stock; wait for the device to replenish");
            kp = await conn.SingleAsync(
                "SELECT kp_ref, key_package FROM key_packages WHERE device_id = @d AND last_resort AND not_after > @now ORDER BY uploaded_at DESC LIMIT 1", tx,
                x => new { Ref = x.Bytes(0), Kp = x.Bytes(1) }, ("d", targetDevice), ("now", now)) ?? throw ApiException.NotFound("no KeyPackage");
            lastResort = true;
        }
        await conn.ExecAsync(
            "INSERT INTO kp_issuance (kp_ref, requester_device_id, target_device_id, issued_at) VALUES (@k, @r, @d, @now) " +
            "ON CONFLICT (kp_ref, requester_device_id) DO UPDATE SET issued_at = excluded.issued_at", tx,
            ("k", kp.Ref), ("r", requester.DeviceId), ("d", targetDevice), ("now", now));
        await tx.CommitAsync();
        return new KeyPackageResponse(targetDevice, kp.Kp, lastResort);
    }
}
