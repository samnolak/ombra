using Npgsql;
using Ombra.Server.Data;
using Ombra.Server.Tests.Infra;

namespace Ombra.Server.Tests;

/// <summary>Forward-only migrations (§1): idempotent, history tamper detection, N-1 binary tolerance, upgrade N→N+1.</summary>
public sealed class MigrationTests(PostgresFixture pg)
{
    [Fact]
    public async Task Migrations_are_idempotent_and_an_edited_applied_migration_is_refused()
    {
        await using var db = NpgsqlDataSource.Create(await pg.CreateDatabaseAsync());
        var first = await Migrator.MigrateAsync(db);
        Assert.Equal(Migrator.All.Select(m => m.Version), first);
        Assert.Empty(await Migrator.MigrateAsync(db));
        Assert.Equal(Migrator.Latest, await Migrator.CurrentVersionAsync(db));

        await using (var conn = await db.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand("UPDATE schema_migrations SET sha256 = '\\x00' WHERE version = @v", conn))
        {
            cmd.Parameters.AddWithValue("v", Migrator.All[0].Version);
            await cmd.ExecuteNonQueryAsync();
        }
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Migrator.MigrateAsync(db));
        Assert.Contains("modified", e.Message);
    }

    [Fact]
    public async Task A_schema_one_step_ahead_is_tolerated_for_image_rollback()
    {
        await using var db = NpgsqlDataSource.Create(await pg.CreateDatabaseAsync());
        await Migrator.MigrateAsync(db);
        await using (var conn = await db.OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand("INSERT INTO schema_migrations (version, sha256) VALUES ('9999_future', '\\x01')", conn))
            await cmd.ExecuteNonQueryAsync();
        Assert.Empty(await Migrator.MigrateAsync(db));
    }

    /// <summary>S0 "upgrade N→N+1 без потерь": data written at schema N survives the step to N+1 (needs ≥ 2 migrations).</summary>
    [Fact]
    public async Task Upgrade_from_previous_schema_keeps_data()
    {
        Assert.SkipWhen(Migrator.All.Count < 2, "only one migration exists; the step N→N+1 appears with the next migration");
        var (root, settings) = await ServerHarness.CreateHostAsync(pg);
        var team = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await using (var db = NpgsqlDataSource.Create(settings["Ombra:ConnectionString"]!))
        {
            await Migrator.MigrateAsync(db, Migrator.All[^2].Version);
            await using var conn = await db.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO teams (team_id, created_at, roster_version, roster_hash) VALUES (@t, now(), 1, @t);" +
                "INSERT INTO blocks (team_id, member_id, by_actor, at, audit_id) VALUES (@t, gen_random_uuid(), 'operator', now(), gen_random_uuid());", conn);
            cmd.Parameters.AddWithValue("t", team);
            await cmd.ExecuteNonQueryAsync();
        }
        await using var h = ServerHarness.Start(pg, root, settings); // server start applies N+1
        Assert.Equal(Migrator.Latest, await Migrator.CurrentVersionAsync(h.Db));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM teams WHERE team_id = @t", ("t", team)));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM blocks WHERE team_id = @t", ("t", team)));
    }
}
