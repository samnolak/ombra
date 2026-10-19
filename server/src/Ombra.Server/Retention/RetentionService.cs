using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Blobs;
using Ombra.Server.Data;
using Ombra.Server.Transfers;

namespace Ombra.Server.Retention;

public sealed record RetentionReport(int DeliveryExpired, int TransfersExpired, int BlobsExpired, int EnvelopeResults, int Challenges,
    int Sessions, int Audit, int TransferRate, int BootstrapCodes, int OrphanFiles);

/// <summary>
/// TTL cleanup (§3.0, §3.3, §8 п.1): queue rows after QueueTtlDays without ack, transfers after TransferTtlHours,
/// blobs after BlobIdleTtlDays without access (journaled), audit after AuditRetentionDays, plus housekeeping.
/// </summary>
public sealed class RetentionService(NpgsqlDataSource db, IOptions<OmbraOptions> options, TransferStore store, BlobService blobs)
{
    private readonly OmbraOptions _o = options.Value;

    public async Task<RetentionReport> RunOnceAsync(DateTimeOffset now)
    {
        int delivery, envelopes, challenges, sessions, audit, rate, codes;
        List<Guid> transfers;
        await using (var conn = await db.OpenConnectionAsync())
        {
            delivery = await conn.ExecAsync("DELETE FROM delivery WHERE expires_at <= @now", null, ("now", now));
            transfers = await conn.ListAsync("DELETE FROM transfers WHERE expires_at <= @now RETURNING transfer_id", null, r => r.GetGuid(0), ("now", now));
            envelopes = await conn.ExecAsync("DELETE FROM envelope_results WHERE created_at < @c", null, ("c", now.AddHours(-_o.Limits.EnvelopeResultHours)));
            challenges = await conn.ExecAsync("DELETE FROM hello_challenges WHERE expires_at <= @now", null, ("now", now));
            sessions = await conn.ExecAsync("DELETE FROM sessions WHERE refresh_expires_at <= @now", null, ("now", now));
            audit = await conn.ExecAsync("DELETE FROM audit WHERE at < @c AND NOT EXISTS (SELECT 1 FROM pending_rotations p WHERE p.audit_id = audit.audit_id)", null,
                ("c", now.AddDays(-_o.Limits.AuditRetentionDays)));
            rate = await conn.ExecAsync("DELETE FROM transfer_rate WHERE at < @c", null, ("c", now.AddHours(-2)));
            codes = await conn.ExecAsync("DELETE FROM bootstrap_codes WHERE used_at IS NULL AND expires_at <= @now", null, ("now", now));
            await conn.ExecAsync("DELETE FROM pairing_sessions WHERE expires_at <= @now", null, ("now", now));
        }
        foreach (var t in transfers) store.DeleteFiles(t);
        var blobCount = await blobs.ExpireIdleAsync(now);
        var orphans = await DeleteOrphanFilesAsync(now);
        return new RetentionReport(delivery, transfers.Count, blobCount, envelopes, challenges, sessions, audit, rate, codes, orphans);
    }

    /// <summary>Files without a row (crash between file move and commit, or between commit and delete), older than 1 h.</summary>
    private async Task<int> DeleteOrphanFilesAsync(DateTimeOffset now)
    {
        var cutoff = now.UtcDateTime.AddHours(-1);
        var count = 0;
        await using var conn = await db.OpenConnectionAsync();
        if (Directory.Exists(_o.BlobDir))
        {
            var files = new DirectoryInfo(_o.BlobDir).EnumerateFiles().Where(f => f.LastWriteTimeUtc < cutoff).ToList();
            var ids = files.Select(f => Guid.TryParseExact(f.Name, "N", out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToArray();
            var live = (await conn.ListAsync("SELECT blob_id FROM blobs WHERE blob_id = ANY(@ids)", null, r => r.GetGuid(0), ("ids", ids))).ToHashSet();
            foreach (var f in files)
            {
                var isLive = Guid.TryParseExact(f.Name, "N", out var id) && live.Contains(id);
                if (!isLive) { f.Delete(); count++; }
            }
        }
        if (Directory.Exists(_o.TransferDir))
        {
            var dirs = new DirectoryInfo(_o.TransferDir).EnumerateDirectories().Where(d => d.LastWriteTimeUtc < cutoff).ToList();
            var ids = dirs.Select(d => Guid.TryParseExact(d.Name, "N", out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToArray();
            var live = (await conn.ListAsync("SELECT transfer_id FROM transfers WHERE transfer_id = ANY(@ids)", null, r => r.GetGuid(0), ("ids", ids))).ToHashSet();
            foreach (var d in dirs)
            {
                var isLive = Guid.TryParseExact(d.Name, "N", out var id) && live.Contains(id);
                if (!isLive) { d.Delete(recursive: true); count++; }
            }
        }
        return count;
    }
}

public sealed class RetentionHostedService(RetentionService retention, TimeProvider clock, IOptions<OmbraOptions> options, ILogger<RetentionHostedService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // RetentionIntervalSeconds <= 0 disables the loop (tests drive RunOnceAsync with a fake clock).
        if (options.Value.Limits.RetentionIntervalSeconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.Limits.RetentionIntervalSeconds), clock);
        do
        {
            try
            {
                var r = await retention.RunOnceAsync(clock.GetUtcNow());
                log.LogInformation("retention: {Report}", r);
            }
            catch (Exception e) when (e is NpgsqlException or IOException)
            {
                log.LogError(e, "retention run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
