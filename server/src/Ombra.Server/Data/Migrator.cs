using System.Reflection;
using System.Security.Cryptography;
using Npgsql;

namespace Ombra.Server.Data;

public sealed record Migration(string Version, string Sql, byte[] Sha256);

/// <summary>
/// Forward-only migrations from server/migrations (embedded). Each file runs in its own transaction under a
/// session advisory lock. An applied migration whose checksum differs is a hard error (history was edited).
/// Applied versions unknown to this binary are tolerated so that an image rollback (install.sh --upgrade) can
/// start against a schema that is one step ahead — migrations must stay compatible with the N-1 binary.
/// </summary>
public static class Migrator
{
    private const long LockKey = 0x4F4D4252_41000001; // "OMBRA" + 1

    public static IReadOnlyList<Migration> All { get; } = Load();

    public static string Latest => All[^1].Version;

    private static List<Migration> Load()
    {
        var asm = typeof(Migrator).Assembly;
        var list = new List<Migration>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("migrations/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            var version = Path.GetFileNameWithoutExtension(name["migrations/".Length..]);
            list.Add(new Migration(version, System.Text.Encoding.UTF8.GetString(bytes), SHA256.HashData(bytes)));
        }
        if (list.Count == 0) throw new InvalidOperationException("no embedded migrations");
        return list;
    }

    /// <summary>Applies pending migrations up to and including <paramref name="upTo"/> (default: all).</summary>
    public static async Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource db, string? upTo = null, CancellationToken ct = default)
    {
        if (upTo is not null && All.All(m => m.Version != upTo))
            throw new InvalidOperationException($"unknown migration {upTo}");

        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn))
        {
            lockCmd.Parameters.AddWithValue("k", LockKey);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await using (var create = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, sha256 bytea NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", conn))
                await create.ExecuteNonQueryAsync(ct);

            var applied = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            await using (var q = new NpgsqlCommand("SELECT version, sha256 FROM schema_migrations", conn))
            await using (var r = await q.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) applied[r.GetString(0)] = (byte[])r[1];

            foreach (var m in All)
                if (applied.TryGetValue(m.Version, out var sha) && !sha.AsSpan().SequenceEqual(m.Sha256))
                    throw new InvalidOperationException($"migration {m.Version} was modified after it was applied");

            var done = new List<string>();
            foreach (var m in All)
            {
                if (applied.ContainsKey(m.Version)) { if (m.Version == upTo) break; continue; }
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var cmd = new NpgsqlCommand(m.Sql, conn, tx)) await cmd.ExecuteNonQueryAsync(ct);
                await using (var ins = new NpgsqlCommand("INSERT INTO schema_migrations (version, sha256) VALUES (@v, @s)", conn, tx))
                {
                    ins.Parameters.AddWithValue("v", m.Version);
                    ins.Parameters.AddWithValue("s", m.Sha256);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                done.Add(m.Version);
                if (m.Version == upTo) break;
            }
            return done;
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
            unlock.Parameters.AddWithValue("k", LockKey);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    public static async Task<string?> CurrentVersionAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT CASE WHEN to_regclass('schema_migrations') IS NULL THEN NULL ELSE (SELECT max(version) FROM schema_migrations) END", conn);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
