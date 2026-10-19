using Npgsql;
using Ombra.Server.Contracts;

namespace Ombra.Server.Auth;

/// <summary>Bearer access token → device. The device id of every request comes only from here, never from a body.</summary>
public sealed class DeviceAuth(TokenService tokens, NpgsqlDataSource db)
{
    private const string ItemKey = "ombra.device";

    public async Task<DeviceIdentity> RequireAsync(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(ItemKey, out var cached) && cached is DeviceIdentity d) return d;
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) throw new ApiException(401, Reasons.Unauthorized);
        var deviceId = await tokens.ValidateAccessAsync(header["Bearer ".Length..].Trim())
                       ?? throw new ApiException(401, Reasons.Unauthorized);
        await using var conn = await db.OpenConnectionAsync();
        var device = await AccessService.DeviceAsync(conn, null, deviceId) ?? throw new ApiException(401, Reasons.Unauthorized);
        ctx.Items[ItemKey] = device;
        return device;
    }
}
