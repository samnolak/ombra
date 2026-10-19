using System.Data;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Blobs;
using Ombra.Server.Data;
using Ombra.Server.Revocation;

namespace Ombra.Server.Backup;

public sealed record BackupManifest(
    int Format, DateTimeOffset CreatedAt, Guid ServerId, string SchemaVersion,
    Dictionary<string, long> Tables, List<BackupBlob> Blobs, List<Guid> MissingBlobs,
    long RevocationsSeq, long BlobDeletionsSeq);

public sealed record BackupBlob(Guid Id, long Size);

public sealed record VerifyReport(bool Ok, Dictionary<string, (long Expected, long Actual)> Tables, int BlobsChecked, List<string> Problems);

public sealed record RestoreReport(string SchemaVersion, Dictionary<string, long> Tables, int BlobsRestored, int RevocationsRestored,
    int RevocationsAlreadyPresent, int BlobsDeletedByLog, int TeamsQuarantined, bool ExternalJournalsApplied);

/// <summary>
/// ombra-admin backup / backup verify / restore (§1 SD-08, R2-08, R5-11, N11).
/// <para>Backup = per-table binary COPY of an explicit allowlist (one REPEATABLE READ snapshot) + blob files + manifest,
/// tar-streamed into <c>age</c>. Tables of the delivery queue and transfers are never in the allowlist (D-7, R5-11);
/// <see cref="Excluded"/> lists every other table on purpose, and a test fails if a table is in neither list.</para>
/// <para>Restore (service stopped): wipe schema → migrate to the backup's schema version → COPY → migrate to latest →
/// empty queue/transfers/sessions → blobs → replay revocations.log → apply blob-deletions.log → new token key,
/// new cursor epoch, serverId from the backup → every team in quarantine. <c>restore_in_progress</c> stays set until
/// the last step, and hello is refused while it is set.</para>
/// </summary>
public sealed class BackupService(NpgsqlDataSource db, IOptions<OmbraOptions> options, TimeProvider clock, RevocationService revocations,
    BlobService blobs, ILogger<BackupService> log)
{
    public const int Format = 1;

    /// <summary>In FK order. A table added by a later migration is simply absent from older archives (and their manifest).</summary>
    public static readonly string[] Included =
    [
        "members", "devices", "server_owners", "teams", "roster_versions", "roster_members", "roster_devices", "roster_access",
        "blocks", "audit", "pending_rotations", "groups", "group_leaves", "blobs", "invites", "join_requests", "used_ids",
        "roster_equivocations", "heartbeats", "ownership_offers", "removal_notices",
    ];

    /// <summary>Never backed up: queue and transfers (D-7), secrets/sessions, per-instance state.</summary>
    public static readonly string[] Excluded =
    [
        "delivery", "transfers", "transfer_chunks", "transfer_rate", "envelope_results", "sessions", "hello_challenges",
        "bootstrap_codes", "server_state", "schema_migrations", "key_packages", "kp_issuance", "pairing_sessions",
    ];

    private readonly OmbraOptions _o = options.Value;

    public async Task<BackupManifest> BackupAsync(string outPath, IReadOnlyList<string> recipientArgs)
    {
        var work = PrivateTempDir();
        try
        {
            var manifest = await DumpAsync(work);
            await new AgeCli(_o.AgePath).EncryptAsync(outPath, recipientArgs, async stdin =>
            {
                await using var tar = new TarWriter(stdin, TarEntryFormat.Pax, leaveOpen: true);
                await tar.WriteEntryAsync(Path.Combine(work, "manifest.json"), "manifest.json");
                foreach (var t in Included) await tar.WriteEntryAsync(Path.Combine(work, "db", t + ".copy"), $"db/{t}.copy");
                foreach (var b in manifest.Blobs) await tar.WriteEntryAsync(BlobFile(b.Id), $"blobs/{b.Id:N}");
            });
            return manifest;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private async Task<BackupManifest> DumpAsync(string work)
    {
        Directory.CreateDirectory(Path.Combine(work, "db"));
        var counts = new Dictionary<string, long>();
        string schema;
        Guid serverId;
        List<(Guid Id, long Size)> blobRows;
        await using (var conn = await db.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead);
            schema = (await conn.ScalarAsync<string>("SELECT max(version) FROM schema_migrations", tx))!;
            serverId = Guid.Parse((await conn.ScalarAsync<string>("SELECT value FROM server_state WHERE key = 'server_id'", tx))!);
            foreach (var t in Included)
            {
                counts[t] = await conn.ScalarAsync<long>($"SELECT count(*) FROM {t}", tx);
                await using var src = await conn.BeginRawBinaryCopyAsync($"COPY {t} TO STDOUT (FORMAT binary)");
                await using var dst = File.Create(Path.Combine(work, "db", t + ".copy"));
                await src.CopyToAsync(dst);
            }
            blobRows = await conn.ListAsync("SELECT blob_id, size FROM blobs ORDER BY blob_id", tx, r => (r.GetGuid(0), r.GetInt64(1)));
            await tx.CommitAsync();
        }
        var present = blobRows.Where(b => File.Exists(BlobFile(b.Id))).Select(b => new BackupBlob(b.Id, b.Size)).ToList();
        var missing = blobRows.Where(b => !File.Exists(BlobFile(b.Id))).Select(b => b.Id).ToList();
        var manifest = new BackupManifest(Format, clock.GetUtcNow(), serverId, schema, counts, present, missing,
            Logs.HashChainLog.ReadVerified(_o.RevocationsLogPath).Count, Logs.HashChainLog.ReadVerified(_o.BlobDeletionsLogPath).Count);
        await File.WriteAllTextAsync(Path.Combine(work, "manifest.json"), JsonSerializer.Serialize(manifest, JsonDefaults.Options));
        return manifest;
    }

    private string BlobFile(Guid id) => Path.Combine(_o.BlobDir, id.ToString("N"));

    private string PrivateTempDir()
    {
        var root = Path.Combine(_o.DataDir, ".work");
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }

    /// <summary>Decrypts and extracts into a private temp dir; rejects unexpected archive members.</summary>
    private async Task<(string Dir, BackupManifest Manifest)> ExtractAsync(string inPath, string identityPath)
    {
        var work = PrivateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(work, "db"));
            Directory.CreateDirectory(Path.Combine(work, "blobs"));
            await new AgeCli(_o.AgePath).DecryptAsync(inPath, identityPath, async stdout =>
            {
                await using var tar = new TarReader(stdout);
                while (await tar.GetNextEntryAsync() is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new InvalidDataException("unexpected entry type");
                    var name = entry.Name;
                    string target;
                    if (name == "manifest.json") target = Path.Combine(work, name);
                    else if (name.StartsWith("db/", StringComparison.Ordinal) && Included.Contains(name[3..^5]) && name.EndsWith(".copy", StringComparison.Ordinal))
                        target = Path.Combine(work, "db", name[3..]);
                    else if (name.StartsWith("blobs/", StringComparison.Ordinal) && Guid.TryParseExact(name[6..], "N", out var bid))
                        target = Path.Combine(work, "blobs", bid.ToString("N"));
                    else throw new InvalidDataException("unexpected archive member " + name);
                    await entry.ExtractToFileAsync(target, overwrite: false);
                }
            });
            var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(Path.Combine(work, "manifest.json")), JsonDefaults.Options)
                           ?? throw new InvalidDataException("manifest");
            if (manifest.Format != Format) throw new InvalidDataException($"unsupported backup format {manifest.Format}");
            if (Migrator.All.All(m => m.Version != manifest.SchemaVersion))
                throw new InvalidDataException($"backup schema {manifest.SchemaVersion} is unknown to this binary (latest {Migrator.Latest})");
            if (manifest.Tables.Keys.Any(k => !Included.Contains(k))) throw new InvalidDataException("manifest lists a table outside the allowlist");
            foreach (var t in manifest.Tables.Keys)
                if (!File.Exists(Path.Combine(work, "db", t + ".copy"))) throw new InvalidDataException($"missing table {t}");
            return (work, manifest);
        }
        catch
        {
            Directory.Delete(work, recursive: true);
            throw;
        }
    }

    private static async Task LoadTablesAsync(NpgsqlDataSource target, string work)
    {
        await using var conn = await target.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        foreach (var t in Included)
        {
            var file = Path.Combine(work, "db", t + ".copy");
            if (!File.Exists(file)) continue; // table newer than the backup's schema
            await using (var dst = await conn.BeginRawBinaryCopyAsync($"COPY {t} FROM STDIN (FORMAT binary)"))
            await using (var src = File.OpenRead(file))
                await src.CopyToAsync(dst);
        }
        await conn.ExecAsync(
            """
            SELECT setval('change_seq', greatest(1,
                (SELECT coalesce(max(change_seq), 0) FROM roster_versions),
                (SELECT coalesce(max(change_seq), 0) FROM blocks),
                (SELECT coalesce(max(change_seq), 0) FROM pending_rotations),
                (SELECT coalesce(max(change_seq), 0) FROM join_requests)));
            SELECT setval(pg_get_serial_sequence('blocks', 'block_id'), greatest(1, (SELECT coalesce(max(block_id), 0) FROM blocks)));
            """, tx);
        await tx.CommitAsync();
    }

    private static async Task<Dictionary<string, long>> CountAsync(NpgsqlDataSource target, IEnumerable<string>? tables = null)
    {
        await using var conn = await target.OpenConnectionAsync();
        var d = new Dictionary<string, long>();
        foreach (var t in tables ?? Included) d[t] = await conn.ScalarAsync<long>($"SELECT count(*) FROM {t}", null);
        return d;
    }

    /// <summary><c>backup verify</c>: restore into a throw-away database and compare counts and blob hashes.</summary>
    public async Task<VerifyReport> VerifyAsync(string inPath, string identityPath)
    {
        var (work, manifest) = await ExtractAsync(inPath, identityPath);
        var csb = new NpgsqlConnectionStringBuilder(_o.ConnectionString);
        var tempDb = "ombra_verify_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await using (var conn = await db.OpenConnectionAsync())
                await conn.ExecAsync($"CREATE DATABASE {tempDb}", null);
            csb.Database = tempDb;
            csb.Pooling = false;
            await using var tdb = NpgsqlDataSource.Create(csb.ConnectionString);
            await Migrator.MigrateAsync(tdb, manifest.SchemaVersion);
            await LoadTablesAsync(tdb, work);
            var listed = Included.Where(manifest.Tables.ContainsKey).ToList();
            var actual = await CountAsync(tdb, listed);
            var problems = new List<string>();
            var tables = new Dictionary<string, (long, long)>();
            foreach (var t in listed)
            {
                var expected = manifest.Tables.GetValueOrDefault(t, -1);
                tables[t] = (expected, actual[t]);
                if (expected != actual[t]) problems.Add($"{t}: expected {expected}, restored {actual[t]}");
            }
            await using var c2 = await tdb.OpenConnectionAsync();
            var rows = await c2.ListAsync("SELECT blob_id, sha256 FROM blobs", null, r => (r.GetGuid(0), r.Bytes(1)));
            var checkedBlobs = 0;
            foreach (var (id, sha) in rows)
            {
                if (manifest.MissingBlobs.Contains(id)) continue;
                var f = Path.Combine(work, "blobs", id.ToString("N"));
                if (!File.Exists(f)) { problems.Add($"blob {id} missing from archive"); continue; }
                await using var fs = File.OpenRead(f);
                if (!(await SHA256.HashDataAsync(fs)).AsSpan().SequenceEqual(sha)) problems.Add($"blob {id} sha256 mismatch");
                checkedBlobs++;
            }
            await c2.CloseAsync();
            return new VerifyReport(problems.Count == 0, tables, checkedBlobs, problems);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using (var conn = await db.OpenConnectionAsync())
                await conn.ExecAsync($"DROP DATABASE IF EXISTS {tempDb} WITH (FORCE)", null);
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>
    /// Restore (§1 SD-08, R2-08, N11). Before anything is wiped, the off-host journals are located — the explicit copies,
    /// else the configured <c>RevocationsSink</c>/<c>BlobDeletionsSink</c> — verified, and checked to be at least as long as
    /// the journals recorded in the backup (a shorter copy is a wrong or truncated one). Without them every block and
    /// blob deletion after the backup would silently come back, so restore is refused unless <paramref name="noExternalLog"/>
    /// explicitly accepts that (audited as <c>restore.no-external-log</c>).
    /// </summary>
    public async Task<RestoreReport> RestoreAsync(string inPath, string identityPath, string? revocationsCopy, string? blobDeletionsCopy, bool force,
        bool noExternalLog = false)
    {
        await using (var conn = await db.OpenConnectionAsync())
        {
            var live = await conn.ScalarAsync<long>(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND application_name = 'ombra-server' AND pid <> pg_backend_pid()", null);
            if (live > 0 && !force) throw new InvalidOperationException("ombra-server is connected to the database; stop the service before restore");
        }
        // An explicit copy or a configured sink whose file is missing (unmounted share, typo) is never read as "no events".
        string? Pick(string? explicitCopy, string? sink, string name)
        {
            var path = explicitCopy ?? sink;
            if (path is null || File.Exists(path)) return path;
            if (explicitCopy is null && noExternalLog) return null;
            throw new InvalidOperationException($"{name} copy {path} not found" + (explicitCopy is null
                ? " (configured sink not mounted?); pass the copy explicitly, or --no-external-log if none exists"
                : ""));
        }
        var revCopy = Pick(revocationsCopy, _o.RevocationsSink, "revocations.log");
        var blobCopy = Pick(blobDeletionsCopy, _o.BlobDeletionsSink, "blob-deletions.log");
        var external = revCopy is not null && blobCopy is not null;
        if (!external && !noExternalLog)
            throw new InvalidOperationException(
                "restore needs the off-host copies of revocations.log and blob-deletions.log: configure Ombra:RevocationsSink/Ombra:BlobDeletionsSink " +
                "or pass --revocations-log/--blob-deletions-log. Without them blocks and blob deletions made after the backup are lost; " +
                "to accept that explicitly pass --no-external-log (recorded in the audit log).");
        var (work, manifest) = await ExtractAsync(inPath, identityPath);
        try
        {
            RequireNotShorter(revCopy, manifest.RevocationsSeq, "revocations.log");
            RequireNotShorter(blobCopy, manifest.BlobDeletionsSeq, "blob-deletions.log");
            log.LogWarning("restore: wiping schema and loading backup of {Created} (schema {Schema})", manifest.CreatedAt, manifest.SchemaVersion);
            await using (var conn = await db.OpenConnectionAsync())
                await conn.ExecAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", null);
            await Migrator.MigrateAsync(db, manifest.SchemaVersion);
            await using (var conn = await db.OpenConnectionAsync())
                await conn.ExecAsync(
                    "INSERT INTO server_state (key, value) VALUES ('restore_in_progress', '1'), ('server_id', @s), ('cursor_epoch', @c)", null,
                    ("s", manifest.ServerId.ToString()), ("c", Guid.NewGuid().ToString("N")));
            await LoadTablesAsync(db, work);
            await Migrator.MigrateAsync(db);
            await using (var conn = await db.OpenConnectionAsync())
                await conn.ExecAsync(
                    "TRUNCATE delivery, transfer_chunks, transfers, transfer_rate, envelope_results, sessions, hello_challenges, bootstrap_codes, key_packages, kp_issuance", null);

            // Blobs from the backup replace local ones; transfers ciphertext is dropped (never restored).
            if (Directory.Exists(_o.BlobDir)) Directory.Delete(_o.BlobDir, recursive: true);
            Directory.Move(Path.Combine(work, "blobs"), _o.BlobDir);
            if (Directory.Exists(_o.TransferDir)) Directory.Delete(_o.TransferDir, recursive: true);
            Directory.CreateDirectory(_o.TransferDir);

            var (restored, present, _) = await revocations.ReplayAsync(revCopy);
            var blobsDeleted = await blobs.ApplyDeletionLogAsync(blobCopy);

            TokenKey.Generate(_o.TokenKeyFile);
            var key = TokenKey.Load(_o.TokenKeyFile);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_o.ServerIdFile))!);
            await File.WriteAllTextAsync(_o.ServerIdFile, manifest.ServerId + "\n");

            int quarantined;
            await using (var conn = await db.OpenConnectionAsync())
            {
                await using var tx = await conn.BeginTransactionAsync();
                await conn.ExecAsync("INSERT INTO server_state (key, value) VALUES ('token_key_id', @v) ON CONFLICT (key) DO UPDATE SET value = excluded.value", tx,
                    ("v", key.Id));
                quarantined = await conn.ExecAsync("UPDATE teams SET quarantine = true, quarantine_released_by = NULL", tx);
                await conn.ExecAsync("INSERT INTO audit (audit_id, at, actor, action, details) VALUES (@a, @now, 'ombra-admin', 'restore', @d::jsonb)", tx,
                    ("a", Guid.CreateVersion7()), ("now", clock.GetUtcNow()),
                    ("d", JsonSerializer.Serialize(new { manifest.CreatedAt, manifest.SchemaVersion, restored, present, blobsDeleted, revocationsLog = revCopy, blobDeletionsLog = blobCopy })));
                if (!external)
                    await conn.ExecAsync("INSERT INTO audit (audit_id, at, actor, action, details) VALUES (@a, @now, 'ombra-admin', 'restore.no-external-log', @d::jsonb)", tx,
                        ("a", Guid.CreateVersion7()), ("now", clock.GetUtcNow()),
                        ("d", JsonSerializer.Serialize(new { warning = "restored without off-host journals: blocks and blob deletions after the backup are not re-applied", revocationsLog = revCopy, blobDeletionsLog = blobCopy })));
                await conn.ExecAsync("DELETE FROM server_state WHERE key = 'restore_in_progress'", tx);
                await tx.CommitAsync();
            }
            return new RestoreReport(manifest.SchemaVersion, await CountAsync(db), manifest.Blobs.Count, restored, present, blobsDeleted, quarantined, external);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    private static void RequireNotShorter(string? copy, long recorded, string name)
    {
        if (copy is null) return;
        var n = Logs.HashChainLog.ReadVerified(copy).Count;
        if (n < recorded)
            throw new InvalidOperationException($"{name} copy {copy} has {n} entries, shorter than the {recorded} recorded in the backup (wrong or truncated copy)");
    }
}
