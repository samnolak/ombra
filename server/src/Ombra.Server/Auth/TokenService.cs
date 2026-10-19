using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Contracts;
using Ombra.Server.Data;

namespace Ombra.Server.Auth;

/// <summary>
/// Opaque bearer tokens (§2.2): access 15 min, refresh bound to the device. The DB keeps only
/// HMAC-SHA256(tokenKey, token), so a DB dump neither reveals nor lets anyone test tokens offline without the key.
/// Restore rotates the key and truncates sessions (§1 карантин п.1).
/// </summary>
public sealed class TokenService(NpgsqlDataSource db, TokenKey key, TimeProvider clock, IOptions<OmbraOptions> options)
{
    private readonly LimitsOptions _limits = options.Value.Limits;

    public async Task<TokenResponse> IssueAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid deviceId)
    {
        var now = clock.GetUtcNow();
        var access = NewToken();
        var refresh = NewToken();
        var accessExp = now.AddMinutes(_limits.AccessTokenMinutes);
        var refreshExp = now.AddDays(_limits.RefreshTokenDays);
        await conn.ExecAsync(
            "INSERT INTO sessions (session_id, device_id, access_hash, access_expires_at, refresh_hash, refresh_expires_at, created_at) " +
            "VALUES (@id, @d, @ah, @ae, @rh, @re, @now)", tx,
            ("id", Guid.CreateVersion7()), ("d", deviceId), ("ah", key.Hash(access)), ("ae", accessExp),
            ("rh", key.Hash(refresh)), ("re", refreshExp), ("now", now));
        return new TokenResponse(access, accessExp, refresh, refreshExp);
    }

    /// <summary>Resolves an access token to its device; null if unknown/expired or the device was revoked server-wide.</summary>
    public async Task<Guid?> ValidateAccessAsync(string token)
    {
        if (!LooksLikeToken(token)) return null;
        await using var conn = await db.OpenConnectionAsync();
        return await conn.ScalarAsync<Guid?>(
            "SELECT s.device_id FROM sessions s JOIN devices d ON d.device_id = s.device_id " +
            "WHERE s.access_hash = @h AND s.access_expires_at > @now AND d.revoked_at IS NULL", null,
            ("h", key.Hash(token)), ("now", clock.GetUtcNow()));
    }

    /// <summary>Rotates both tokens of a session (refresh is single-use).</summary>
    public async Task<TokenResponse?> RefreshAsync(string refreshToken)
    {
        if (!LooksLikeToken(refreshToken)) return null;
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var device = await conn.ScalarAsync<Guid?>(
            "DELETE FROM sessions s USING devices d WHERE d.device_id = s.device_id AND s.refresh_hash = @h " +
            "AND s.refresh_expires_at > @now AND d.revoked_at IS NULL RETURNING s.device_id", tx,
            ("h", key.Hash(refreshToken)), ("now", clock.GetUtcNow()));
        if (device is null) return null;
        var tokens = await IssueAsync(conn, tx, device.Value);
        await tx.CommitAsync();
        return tokens;
    }

    public static async Task<int> RevokeDevicesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, IReadOnlyCollection<Guid> devices) =>
        await conn.ExecAsync("DELETE FROM sessions WHERE device_id = ANY(@d)", tx, ("d", devices.ToArray()));

    private static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static bool LooksLikeToken(string token) => token.Length is >= 40 and <= 64;

    internal static string Base64Url(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>HMAC key for token hashing, loaded from <see cref="OmbraOptions.TokenKeyFile"/>.</summary>
public sealed class TokenKey
{
    private readonly byte[] _key;

    public TokenKey(byte[] key)
    {
        if (key.Length < 32) throw new InvalidOperationException("token key must be at least 32 bytes");
        _key = key;
        Id = Convert.ToHexStringLower(SHA256.HashData(key).AsSpan(0, 8));
    }

    /// <summary>Public id of the key (first 8 bytes of SHA-256), recorded in server_state.token_key_id.</summary>
    public string Id { get; }

    public byte[] Hash(string token) => HMACSHA256.HashData(_key, System.Text.Encoding.ASCII.GetBytes(token));

    public static TokenKey Load(string path)
    {
        var text = File.ReadAllText(path).Trim();
        return new TokenKey(Convert.FromBase64String(text));
    }

    public static void Generate(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}
