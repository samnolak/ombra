using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;

namespace Ombra.Server.Data;

/// <summary>
/// serverId and process-wide state checks. Startup invariants (<see cref="InitializeAsync"/>):
/// the DB belongs to this serverId; a token key different from the recorded one invalidates every session
/// (restore rotates the key — §1 карантин п.1); cursor_epoch exists.
/// </summary>
public sealed class ServerIdentity(NpgsqlDataSource db, Guid serverId)
{
    public Guid ServerId { get; } = serverId;

    public static Guid ReadServerId(string path) => Guid.Parse(File.ReadAllText(path).Trim());

    public static async Task InitializeAsync(NpgsqlDataSource db, Guid serverId, TokenKey key)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var stored = await conn.ScalarAsync<string>("SELECT value FROM server_state WHERE key = 'server_id' FOR UPDATE", tx);
        if (stored is null)
            await conn.ExecAsync("INSERT INTO server_state (key, value) VALUES ('server_id', @v)", tx, ("v", serverId.ToString()));
        else if (Guid.Parse(stored) != serverId)
            throw new InvalidOperationException($"database belongs to server {stored}, configured serverId is {serverId}");

        await conn.ExecAsync("INSERT INTO server_state (key, value) VALUES ('cursor_epoch', @v) ON CONFLICT (key) DO NOTHING", tx,
            ("v", Guid.NewGuid().ToString("N")));

        var keyId = await conn.ScalarAsync<string>("SELECT value FROM server_state WHERE key = 'token_key_id'", tx);
        if (keyId != key.Id)
        {
            await conn.ExecAsync("DELETE FROM sessions", tx);
            await conn.ExecAsync("INSERT INTO server_state (key, value) VALUES ('token_key_id', @v) ON CONFLICT (key) DO UPDATE SET value = excluded.value", tx,
                ("v", key.Id));
        }
        await tx.CommitAsync();
    }

    public async Task<bool> RestoreInProgressAsync()
    {
        await using var conn = await db.OpenConnectionAsync();
        return await conn.ScalarAsync<string>("SELECT value FROM server_state WHERE key = 'restore_in_progress'", null) == "1";
    }

    /// <summary>No hello/bootstrap/challenge while a restore is incomplete (§1: logs are applied before any hello).</summary>
    public async Task RequireNotRestoringAsync()
    {
        if (await RestoreInProgressAsync()) throw new ApiException(503, Reasons.RestoreInProgress);
    }
}
