using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Core.Interop;
using Ombra.Server.Contracts;
using Ombra.Server.Data;

namespace Ombra.Server.Auth;

/// <summary>
/// challenge → hello (§2.2) and the one-time bootstrap of the instance owner (§2.4, SD-13).
/// The hello signature input is defined by ombra-core (<see cref="OmbraPublic.VerifyHello"/>).
/// </summary>
public sealed class SessionService(NpgsqlDataSource db, TimeProvider clock, IOptions<OmbraOptions> options, TokenService tokens, ServerIdentity server)
{
    private readonly LimitsOptions _limits = options.Value.Limits;

    /// <summary>
    /// One-time hello nonce. Unauthenticated, so bounded: at most <see cref="LimitsOptions.ChallengesPerDevice"/> unexpired
    /// challenges per deviceId and <see cref="LimitsOptions.ChallengesMax"/> in total (429 beyond; plus the per-address rate limit).
    /// </summary>
    public async Task<ChallengeResponse> ChallengeAsync(ChallengeRequest req)
    {
        await server.RequireNotRestoringAsync();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var now = clock.GetUtcNow();
        var exp = now.AddSeconds(_limits.ChallengeSeconds);
        await using var conn = await db.OpenConnectionAsync();
        var inserted = await conn.ExecAsync(
            "INSERT INTO hello_challenges (nonce, device_id, expires_at) SELECT @n, @d, @e " +
            "WHERE (SELECT count(*) FROM hello_challenges WHERE device_id = @d AND expires_at > @now) < @per " +
            "AND (SELECT count(*) FROM hello_challenges WHERE expires_at > @now) < @max", null,
            ("n", nonce), ("d", req.DeviceId), ("e", exp), ("now", now), ("per", (long)_limits.ChallengesPerDevice), ("max", (long)_limits.ChallengesMax));
        if (inserted == 0)
            throw new ApiException(429, Reasons.RateLimited, "too many outstanding challenges") { RetryAfter = TimeSpan.FromSeconds(_limits.ChallengeSeconds) };
        return new ChallengeResponse(server.ServerId, nonce, exp);
    }

    private static void RequireProtocol(int version)
    {
        if (version < Protocol.Minimum || version > Protocol.Current)
            throw new ApiException(426, Reasons.UpgradeRequired, $"supported protocol versions {Protocol.Minimum}..{Protocol.Current}");
    }

    private async Task ConsumeChallengeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid deviceId, byte[] nonce, ulong time)
    {
        if (nonce is not { Length: 32 }) throw new ApiException(401, Reasons.Unauthorized, "bad nonce");
        var now = clock.GetUtcNow();
        var consumed = await conn.ExecAsync("DELETE FROM hello_challenges WHERE nonce = @n AND device_id = @d AND expires_at > @now", tx,
            ("n", nonce), ("d", deviceId), ("now", now));
        if (consumed != 1) throw new ApiException(401, Reasons.Unauthorized, "unknown or expired challenge");
        var skew = Math.Abs((long)time - now.ToUnixTimeSeconds());
        if (skew > _limits.ClockSkewSeconds) throw new ApiException(401, Reasons.Unauthorized, "clock skew");
    }

    private void VerifyHelloSignature(Guid deviceId, byte[] nonce, ulong time, byte[] signature, byte[] identityKey)
    {
        if (signature is not { Length: 64 } || identityKey is not { Length: 32 }) throw new ApiException(401, Reasons.BadSignature);
        if (!OmbraPublic.VerifyHello(Ids.ToBytes(server.ServerId), nonce, Ids.ToBytes(deviceId), time, signature, identityKey))
            throw new ApiException(401, Reasons.BadSignature);
    }

    public async Task<HelloResponse> HelloAsync(HelloRequest req)
    {
        RequireProtocol(req.ProtocolVersion);
        await server.RequireNotRestoringAsync();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await ConsumeChallengeAsync(conn, tx, req.DeviceId, req.Nonce, req.Time);
        var revoked = await conn.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM devices WHERE device_id = @d AND revoked_at IS NOT NULL)", tx, ("d", req.DeviceId));
        if (revoked) throw ApiException.Forbidden(Reasons.Revoked);
        var device = await AccessService.DeviceAsync(conn, tx, req.DeviceId) ?? throw new ApiException(401, Reasons.Unauthorized, "unknown device");
        VerifyHelloSignature(req.DeviceId, req.Nonce, req.Time, req.Signature, device.IdentityKey);

        var teams = await TeamStatusesAsync(conn, tx, device.DeviceId);
        // §5.3: a device known to teams but denied in all of them gets 403 removed/revoked/blocked on hello, with the
        // admin-signed RemovalNotices of its member (§5.1 п.6) so the client can verify them and wipe local data.
        if (teams.Count > 0 && teams.All(t => t.AccessDenied is not null))
        {
            await tx.CommitAsync(); // keep the challenge consumed
            throw new ApiException(403, teams[0].AccessDenied!)
            {
                RemovalNotices = teams.Where(t => t.RemovalNotice is not null).Select(t => t.RemovalNotice!).ToList(),
            };
        }
        var owner = await conn.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM server_owners WHERE member_id = @m)", tx, ("m", device.MemberId));
        var issued = await tokens.IssueAsync(conn, tx, device.DeviceId);
        await tx.CommitAsync();
        return new HelloResponse(server.ServerId, device.MemberId, owner, issued, teams);
    }

    public static async Task<List<TeamStatus>> TeamStatusesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid deviceId)
    {
        var result = new List<TeamStatus>();
        foreach (var teamId in await AccessService.TeamsOfDeviceAsync(conn, tx, deviceId))
        {
            var t = (await AccessService.TeamAsync(conn, tx, teamId))!;
            var deny = await AccessService.DenyReasonAsync(conn, tx, teamId, deviceId);
            var notice = deny == Reasons.Removed
                ? (await Teams.TeamSignalsService.NoticesForMemberAsync(conn, tx, (await AccessService.MemberOfDeviceAsync(conn, tx, deviceId))!.Value, teamId)).FirstOrDefault()
                : null;
            result.Add(new TeamStatus(Ids.Hex(teamId), t.AccessRevision, t.RosterVersion, t.Quarantine, t.QuarantineReleasedBy, t.Frozen, deny, notice));
        }
        return result;
    }

    public async Task<HelloResponse> BootstrapAsync(BootstrapRequest req)
    {
        RequireProtocol(req.ProtocolVersion);
        await server.RequireNotRestoringAsync();
        if (req.Product is not ("gui" or "browser")) throw ApiException.Bad("product must be gui|browser");
        if (req.IdentityKey is not { Length: 32 } || req.MlsSignatureKey is not { Length: 32 }) throw ApiException.Bad("keys must be 32 bytes");
        var now = clock.GetUtcNow();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var codeHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(BootstrapCodes.Normalize(req.Code)));
        var code = await conn.ListAsync(
            "SELECT spki_pin FROM bootstrap_codes WHERE code_hash = @h AND used_at IS NULL AND expires_at > @now FOR UPDATE", tx,
            r => r.StringOrNull(0), ("h", codeHash), ("now", now));
        if (code.Count == 0) throw ApiException.Forbidden(Reasons.BootstrapInvalid);
        // SD-13: the code is issued together with the SPKI pin; a client that did not pin (or saw another key) is refused.
        if (code[0] is { } pin && !string.Equals(pin, req.SpkiPin, StringComparison.OrdinalIgnoreCase))
            throw ApiException.Forbidden(Reasons.PinMismatch);
        await ConsumeChallengeAsync(conn, tx, req.DeviceId, req.Nonce, req.Time);
        VerifyHelloSignature(req.DeviceId, req.Nonce, req.Time, req.Signature, req.IdentityKey);

        await UsedIds.ClaimAsync(conn, tx, req.MemberId, "member", "bootstrap", now);
        await UsedIds.ClaimAsync(conn, tx, req.DeviceId, "device", "bootstrap", now);
        await conn.ExecAsync("INSERT INTO members (member_id, created_at) VALUES (@m, @now)", tx, ("m", req.MemberId), ("now", now));
        await conn.ExecAsync(
            "INSERT INTO devices (device_id, member_id, product, identity_key, mls_signature_key, created_at) VALUES (@d, @m, @p, @ik, @mk, @now)", tx,
            ("d", req.DeviceId), ("m", req.MemberId), ("p", req.Product), ("ik", req.IdentityKey), ("mk", req.MlsSignatureKey), ("now", now));
        await conn.ExecAsync("INSERT INTO server_owners (member_id, granted_at) VALUES (@m, @now)", tx, ("m", req.MemberId), ("now", now));
        await conn.ExecAsync("UPDATE bootstrap_codes SET used_at = @now, used_by_device = @d WHERE code_hash = @h", tx,
            ("now", now), ("d", req.DeviceId), ("h", codeHash));
        await conn.ExecAsync("INSERT INTO audit (audit_id, at, actor, action, subject) VALUES (@a, @now, @actor, 'bootstrap', @s)", tx,
            ("a", Guid.CreateVersion7()), ("now", now), ("actor", "device:" + req.DeviceId), ("s", req.MemberId.ToString()));
        var issued = await tokens.IssueAsync(conn, tx, req.DeviceId);
        await tx.CommitAsync();
        return new HelloResponse(server.ServerId, req.MemberId, true, issued, []);
    }
}

/// <summary>deviceId/memberId are consumed forever on first use (R5-06, N2b).</summary>
public static class UsedIds
{
    public static async Task ClaimAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string kind, string source, DateTimeOffset now)
    {
        var n = await conn.ExecAsync("INSERT INTO used_ids (id, kind, source, at) VALUES (@id, @k, @s, @now) ON CONFLICT (id) DO NOTHING", tx,
            ("id", id), ("k", kind), ("s", source), ("now", now));
        if (n == 0) throw ApiException.Conflict(kind == "device" ? Reasons.DeviceIdReused : Reasons.MemberIdReused);
    }
}

/// <summary>Bootstrap code format: 26 base32 chars (130 bits) in groups of 5; only SHA-256 of the normalized code is stored.</summary>
public static class BootstrapCodes
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Generate()
    {
        var chars = new char[26];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var s = new string(chars);
        return string.Join("-", Enumerable.Range(0, 6).Select(i => s.Substring(i * 5, Math.Min(5, s.Length - i * 5))));
    }

    public static string Normalize(string? code) =>
        new((code ?? "").ToUpperInvariant().Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray());

    public static async Task<string> IssueAsync(NpgsqlDataSource db, string? spkiPin, TimeSpan ttl, DateTimeOffset now)
    {
        var code = Generate();
        await using var conn = await db.OpenConnectionAsync();
        await conn.ExecAsync("INSERT INTO bootstrap_codes (code_hash, spki_pin, created_at, expires_at) VALUES (@h, @p, @now, @exp)", null,
            ("h", SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Normalize(code)))), ("p", spkiPin), ("now", now), ("exp", now + ttl));
        return code;
    }
}
