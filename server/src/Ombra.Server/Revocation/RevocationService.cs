using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;
using Ombra.Server.Delivery;
using Ombra.Server.Logs;
using Ombra.Server.Transfers;

namespace Ombra.Server.Revocation;

public enum RevocationKind { MemberRemove, DeviceRevoke }

public sealed record RevocationRequest(RevocationKind Kind, byte[] TeamId, Guid Subject, string By, string Actor, string? Reason);

public sealed record RevocationResult(Guid AuditId, IReadOnlyList<Guid> Devices, int DeliveryRowsTouched, int DeliveryRowsDeleted,
    int TransfersDeleted, int SessionsRevoked, Guid? RotationId, bool AlreadyBlocked);

/// <summary>
/// Block a member or device of a team (§4.2, §5.2 п.1–2, §3.0 "Отзыв доступа", N3). One transaction:
/// lock all group rows of the team (ordered) → audit → block → purge the devices from every queue row of the team
/// (rows waiting only for them are deleted) → delete transfers where they are sender or target → revoke their
/// sessions → bump access revision → PendingRotation (operator) → NOTIFY (sockets closed by the server on commit).
/// The revocations.log line is appended (fsync) before COMMIT, so a committed block is always in the journal.
/// A block is never lifted (§5.2 п.5).
/// </summary>
public sealed class RevocationService(NpgsqlDataSource db, TimeProvider clock, IOptions<OmbraOptions> options, TransferStore transferStore,
    ILogger<RevocationService> log)
{
    public const string NotifyChannel = "ombra_revoked";

    private readonly OmbraOptions _options = options.Value;

    public HashChainLog Journal => new(_options.RevocationsLogPath, _options.RevocationsSink,
        e => log.LogError(e, "revocations.log sink write failed ({Sink})", _options.RevocationsSink));

    public Task<RevocationResult> BlockAsync(RevocationRequest req) => BlockCoreAsync(req, auditIdForReplay: null, live: true);

    /// <param name="live">Live call (CLI/app): team and subject must exist. Replay: the block is recorded even for
    /// teams/members the restored DB does not know yet.</param>
    private async Task<RevocationResult> BlockCoreAsync(RevocationRequest req, Guid? auditIdForReplay, bool live)
    {
        RevocationResult result;
        List<Guid> transfers;
        await using (var conn = await db.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            (result, transfers) = await ApplyAsync(conn, tx, req, auditIdForReplay ?? Guid.CreateVersion7(), live, journal: live && req.By == "operator");
            await tx.CommitAsync();
        }
        foreach (var t in transfers) transferStore.DeleteFiles(t);
        return result;
    }

    /// <summary>
    /// The block inside the caller's transaction (also used by the roster path when a RosterUpdate removes a member or
    /// revokes a device, §5.1 п.3). Lock order: team row, then all group rows of the team by group_id. The caller
    /// deletes the returned transfers' files after COMMIT.
    /// </summary>
    internal async Task<(RevocationResult Result, List<Guid> Transfers)> ApplyAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        RevocationRequest req, Guid auditId, bool live, bool journal)
    {
        if (req.By is not ("operator" or "admin")) throw new ArgumentException("by must be operator|admin");
        var now = clock.GetUtcNow();
        var teamExists = await conn.ScalarAsync<byte[]>("SELECT team_id FROM teams WHERE team_id = @t FOR NO KEY UPDATE", tx, ("t", req.TeamId)) is not null;
        if (live && !teamExists) throw new InvalidOperationException("unknown team " + Ids.Hex(req.TeamId));
        await conn.ExecAsync("SELECT group_id FROM groups WHERE team_id = @t ORDER BY group_id FOR UPDATE", tx, ("t", req.TeamId));

        Guid? memberId = null, deviceId = null;
        List<Guid> devices;
        if (req.Kind == RevocationKind.MemberRemove)
        {
            memberId = req.Subject;
            var known = await conn.ScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM roster_members WHERE team_id = @t AND member_id = @m)", tx, ("t", req.TeamId), ("m", memberId));
            if (live && !known) throw new InvalidOperationException($"member {memberId} is not in team {Ids.Hex(req.TeamId)}");
            devices = await conn.ListAsync(
                "SELECT device_id FROM roster_devices WHERE team_id = @t AND member_id = @m " +
                "UNION SELECT device_id FROM devices WHERE member_id = @m ORDER BY 1", tx,
                r => r.GetGuid(0), ("t", req.TeamId), ("m", memberId));
        }
        else
        {
            deviceId = req.Subject;
            var known = await conn.ScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM roster_devices WHERE team_id = @t AND device_id = @d)", tx, ("t", req.TeamId), ("d", deviceId));
            if (live && !known) throw new InvalidOperationException($"device {deviceId} is not in team {Ids.Hex(req.TeamId)}");
            devices = [deviceId.Value];
        }
        var devArray = devices.ToArray();

        await conn.ExecAsync(
            "INSERT INTO audit (audit_id, at, actor, action, team_id, subject, details) VALUES (@a, @now, @actor, @action, @t, @s, @d::jsonb) " +
            "ON CONFLICT (audit_id) DO NOTHING", tx,
            ("a", auditId), ("now", now), ("actor", req.Actor), ("action", Action(req.Kind)), ("t", req.TeamId), ("s", req.Subject.ToString()),
            ("d", JsonSerializer.Serialize(new { by = req.By, reason = req.Reason, devices })));

        await ChangeLog.LockAsync(conn, tx);
        var inserted = await conn.ExecAsync(
            "INSERT INTO blocks (team_id, member_id, device_id, by_actor, at, reason, audit_id) VALUES (@t, @m, @d, @by, @now, @r, @a) ON CONFLICT DO NOTHING", tx,
            ("t", req.TeamId), ("m", memberId), ("d", deviceId), ("by", req.By), ("now", now), ("r", req.Reason), ("a", auditId));

        var touched = await conn.ExecAsync(
            "UPDATE delivery SET pending_device_ids = ARRAY(SELECT x FROM unnest(pending_device_ids) x WHERE x <> ALL(@devs)) " +
            "WHERE team_id = @t AND pending_device_ids && @devs", tx, ("t", req.TeamId), ("devs", devArray));
        var deleted = await conn.ExecAsync(
            "DELETE FROM delivery WHERE team_id = @t AND cardinality(pending_device_ids) = 0", tx, ("t", req.TeamId));

        var transfers = await conn.ListAsync(
            "DELETE FROM transfers WHERE team_id = @t AND (sender_device_id = ANY(@devs) OR target_device_id = ANY(@devs) " +
            "OR sender_member_id = @m OR target_member_id = @m) RETURNING transfer_id", tx,
            r => r.GetGuid(0), ("t", req.TeamId), ("devs", devArray), ("m", memberId));

        var sessions = await TokenService.RevokeDevicesAsync(conn, tx, devices);
        await conn.ExecAsync("UPDATE teams SET access_revision = access_revision + 1 WHERE team_id = @t", tx, ("t", req.TeamId));

        Guid? rotationId = null;
        if (req.By == "operator" && inserted == 1 && teamExists)
        {
            var groups = await conn.ListAsync(
                "SELECT DISTINCT g.group_id FROM groups g JOIN group_leaves gl ON gl.group_id = g.group_id " +
                "WHERE g.team_id = @t AND g.kind IN ('team', 'project') AND g.closed_at IS NULL AND gl.device_id = ANY(@devs) ORDER BY 1", tx,
                r => r.Bytes(0), ("t", req.TeamId), ("devs", devArray));
            rotationId = Guid.CreateVersion7();
            await conn.ExecAsync(
                "INSERT INTO pending_rotations (rotation_id, team_id, group_ids, blocked_member_id, blocked_device_id, audit_id, created_at) " +
                "VALUES (@id, @t, @g, @m, @d, @a, @now)", tx,
                ("id", rotationId), ("t", req.TeamId), ("g", groups.ToArray()), ("m", memberId), ("d", deviceId), ("a", auditId), ("now", now));
        }

        var payload = JsonSerializer.Serialize(new RevokedNotice(Ids.Hex(req.TeamId), devices,
            req.Kind == RevocationKind.MemberRemove ? Reasons.Removed : Reasons.Blocked, rotationId is not null));
        await conn.ExecAsync("SELECT pg_notify(@c, @p)", tx, ("c", NotifyChannel), ("p", payload));

        // §1: CLI deletions go to revocations.log (fsync) before COMMIT, so a committed block is always journaled.
        // App removals are in the signed roster chain instead, which clients re-upload after a restore.
        if (journal && inserted == 1)
        {
            Journal.Append(new JsonObject
            {
                ["kind"] = Action(req.Kind),
                ["teamId"] = Ids.Hex(req.TeamId),
                ["subject"] = req.Subject.ToString(),
                ["by"] = req.By,
                ["reason"] = req.Reason,
                ["auditId"] = auditId.ToString(),
            }, now);
        }
        return (new RevocationResult(auditId, devices, touched, deleted, transfers.Count, sessions, rotationId, inserted == 0), transfers);
    }

    /// <summary>Kills sessions only (no block): <c>ombra-admin sessions revoke</c>.</summary>
    public async Task<int> RevokeSessionsAsync(Guid deviceId, string actor)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var n = await TokenService.RevokeDevicesAsync(conn, tx, [deviceId]);
        await conn.ExecAsync(
            "INSERT INTO audit (audit_id, at, actor, action, subject) VALUES (@a, @now, @actor, 'sessions.revoke', @s)", tx,
            ("a", Guid.CreateVersion7()), ("now", clock.GetUtcNow()), ("actor", actor), ("s", deviceId.ToString()));
        await conn.ExecAsync("SELECT pg_notify(@c, @p)", tx, ("c", NotifyChannel),
            ("p", JsonSerializer.Serialize(new RevokedNotice(null, [deviceId], Reasons.Revoked, false))));
        await tx.CommitAsync();
        return n;
    }

    /// <summary>
    /// After restore (§1 R2-08): verify the chain(s) and re-apply every block. Local and off-host copies must agree
    /// (one a prefix of the other; the longer one becomes the local journal). Idempotent: returns how many blocks
    /// were missing from the restored DB and re-created, and how many were already present.
    /// </summary>
    public async Task<(int Restored, int AlreadyPresent, IReadOnlyList<ChainEntry> Chain)> ReplayAsync(string? offHostCopy)
    {
        var chain = HashChainLog.ReadVerified(_options.RevocationsLogPath);
        if (offHostCopy is not null)
        {
            var other = HashChainLog.ReadVerified(offHostCopy);
            var merged = HashChainLog.Reconcile(chain, other);
            if (!ReferenceEquals(merged, chain))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.RevocationsLogPath))!);
                File.Copy(offHostCopy, _options.RevocationsLogPath, overwrite: true);
                chain = merged;
            }
        }
        var restored = 0;
        var present = 0;
        foreach (var e in chain)
        {
            var kind = e.Entry["kind"]!.GetValue<string>() switch
            {
                "member.remove" => RevocationKind.MemberRemove,
                "device.revoke" => RevocationKind.DeviceRevoke,
                var other => throw new ChainBrokenException("unknown revocation kind " + other),
            };
            if (!Ids.TryParseHash(e.Entry["teamId"]!.GetValue<string>(), out var team)) throw new ChainBrokenException("bad teamId");
            var req = new RevocationRequest(kind, team, Guid.Parse(e.Entry["subject"]!.GetValue<string>()),
                e.Entry["by"]!.GetValue<string>(), "replay-revocations", e.Entry["reason"]?.GetValue<string>());
            var r = await BlockCoreAsync(req, Guid.Parse(e.Entry["auditId"]!.GetValue<string>()), live: false);
            if (r.AlreadyBlocked) present++; else restored++;
        }
        return (restored, present, chain);
    }

    private static string Action(RevocationKind k) => k == RevocationKind.MemberRemove ? "member.remove" : "device.revoke";
}

public sealed record RevokedNotice(string? TeamId, IReadOnlyList<Guid> Devices, string Reason, bool PendingRotation);
