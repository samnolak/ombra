using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Blobs;
using Ombra.Server.Contracts;
using Ombra.Server.Data;
using Ombra.Server.Delivery;
using Ombra.Server.Streaming;
using Ombra.Server.Teams;
using Ombra.Server.Transfers;

namespace Ombra.Server;

/// <summary>HTTP surface of §6.1 that does not need the ombra-core commit/roster validator.</summary>
public static class Endpoints
{
    private static byte[] Hash32(string s) => Ids.TryParseHash(s, out var v) ? v : throw ApiException.Bad("id must be 64 hex chars");

    public static void Map(WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapGet("/health", async (NpgsqlDataSource db, ServerIdentity server) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            await conn.ScalarAsync<int>("SELECT 1", null);
            var restoring = await server.RestoreInProgressAsync();
            return Results.Json(new HealthResponse(restoring ? "restoring" : "ok", server.ServerId, Protocol.Current, Protocol.Minimum, restoring),
                JsonDefaults.Options, statusCode: restoring ? 503 : 200);
        });

        // Reachable without a token: per-address rate limit (RequestGuards), small body limits.
        v1.MapPost("/session/challenge", (ChallengeRequest req, SessionService s) => s.ChallengeAsync(req)).RequireRateLimiting(RequestGuards.PreAuthPolicy);
        v1.MapPost("/session/hello", (HelloRequest req, SessionService s) => s.HelloAsync(req)).RequireRateLimiting(RequestGuards.PreAuthPolicy);
        v1.MapPost("/session/refresh", async (RefreshRequest req, TokenService tokens) =>
            await tokens.RefreshAsync(req.RefreshToken) ?? throw new ApiException(401, Reasons.Unauthorized)).RequireRateLimiting(RequestGuards.PreAuthPolicy);
        v1.MapPost("/bootstrap", (BootstrapRequest req, SessionService s) => s.BootstrapAsync(req)).RequireRateLimiting(RequestGuards.PreAuthPolicy);

        // §3.0 delivery queue
        v1.MapPost("/groups/{gid}/ops", async (string gid, HttpContext ctx, DeviceAuth auth, Envelopes envs, DeliveryService delivery) =>
        {
            var device = await auth.RequireAsync(ctx);
            var env = await envs.FromBodyAsync(ctx.Request, device, EnvelopeKinds.OpPut);
            return await delivery.PutAsync(device, Hash32(gid), env);
        });
        v1.MapPost("/groups/{gid}/ops/ack", async (string gid, AckRequest req, HttpContext ctx, DeviceAuth auth, DeliveryService delivery) =>
            await delivery.AckAsync(await auth.RequireAsync(ctx), Hash32(gid), req));
        v1.MapGet("/sync", async (string? cursor, HttpContext ctx, DeviceAuth auth, SyncService sync) =>
        {
            var (status, body) = await sync.SyncAsync(await auth.RequireAsync(ctx), cursor);
            return Results.Json(body, JsonDefaults.Options, statusCode: status);
        });

        // §3.3 transfers
        v1.MapPut("/transfers/{id:guid}", async (Guid id, HttpContext ctx, DeviceAuth auth, Envelopes envs, TransferService transfers) =>
        {
            var device = await auth.RequireAsync(ctx);
            var env = await envs.FromBodyAsync(ctx.Request, device, EnvelopeKinds.TransferCreate);
            return await transfers.CreateAsync(device, id, env);
        });
        v1.MapPut("/transfers/{id:guid}/chunks/{index:int}", async (Guid id, int index, HttpContext ctx, DeviceAuth auth, TransferService transfers) =>
        {
            await transfers.PutChunkAsync(await auth.RequireAsync(ctx), id, index, ctx.Request);
            return Results.NoContent();
        });
        v1.MapGet("/transfers/{id:guid}", async (Guid id, HttpContext ctx, DeviceAuth auth, TransferService transfers) =>
            await transfers.InfoAsync(await auth.RequireAsync(ctx), id));
        v1.MapGet("/transfers/{id:guid}/chunks/{index:int}", async (Guid id, int index, HttpContext ctx, DeviceAuth auth, TransferService transfers) =>
            Results.File(await transfers.ChunkPathForTargetAsync(await auth.RequireAsync(ctx), id, index), Headers.MlsContentType));
        v1.MapPost("/transfers/{id:guid}/ack", async (Guid id, HttpContext ctx, DeviceAuth auth, TransferService transfers) =>
        {
            await transfers.AckAsync(await auth.RequireAsync(ctx), id);
            return Results.NoContent();
        });

        // §3.3 blobs
        v1.MapPut("/blobs/{id:guid}", async (Guid id, HttpContext ctx, DeviceAuth auth, Envelopes envs, BlobService blobs) =>
        {
            var device = await auth.RequireAsync(ctx);
            var env = envs.FromHeader(ctx.Request, device, EnvelopeKinds.BlobPut);
            return await blobs.PutAsync(device, id, env, ctx.Request);
        });
        v1.MapGet("/blobs/{id:guid}", async (Guid id, HttpContext ctx, DeviceAuth auth, BlobService blobs) =>
        {
            var (path, _) = await blobs.GetAsync(await auth.RequireAsync(ctx), id);
            return Results.File(path, "application/octet-stream");
        });
        v1.MapDelete("/blobs/{id:guid}", async (Guid id, HttpContext ctx, DeviceAuth auth, Envelopes envs, BlobService blobs) =>
        {
            var device = await auth.RequireAsync(ctx);
            var env = await envs.FromBodyAsync(ctx.Request, device, EnvelopeKinds.BlobDelete);
            await blobs.DeleteAsync(device, id, env);
            return Results.NoContent();
        });

        // §4.1 roster chain (verified by ombra-core): genesis, linear append (CAS), chain download
        v1.MapPost("/teams", async (HttpContext ctx, DeviceAuth auth, RosterService roster) =>
        {
            var device = await auth.RequireAsync(ctx);
            return await roster.CreateTeamAsync(device, await Envelopes.ReadBodyAsync(ctx.Request, 1 << 20));
        });
        v1.MapPost("/teams/{teamId}/roster", async (string teamId, HttpContext ctx, DeviceAuth auth, RosterService roster) =>
        {
            var device = await auth.RequireAsync(ctx);
            return await roster.AppendAsync(device, Hash32(teamId), await Envelopes.ReadBodyAsync(ctx.Request, 1 << 20));
        });
        v1.MapGet("/teams/{teamId}/roster", async (string teamId, long? from, HttpContext ctx, DeviceAuth auth, RosterService roster) =>
            await roster.ChainAsync(await auth.RequireAsync(ctx), Hash32(teamId), from ?? 1));
        // R2-08: evidence of an open equivocation, the owner's signed decision, and the decisions applied so far.
        v1.MapGet("/teams/{teamId}/roster/equivocation", async (string teamId, HttpContext ctx, DeviceAuth auth, RosterService roster) =>
            await roster.EquivocationAsync(await auth.RequireAsync(ctx), Hash32(teamId)));
        v1.MapPost("/teams/{teamId}/roster/resolution", async (string teamId, HttpContext ctx, DeviceAuth auth, RosterService roster) =>
        {
            var device = await auth.RequireAsync(ctx);
            return await roster.ResolveAsync(device, "device:" + device.DeviceId, Hash32(teamId), await Envelopes.ReadBodyAsync(ctx.Request, 4096));
        });
        v1.MapGet("/teams/{teamId}/roster/resolutions", async (string teamId, HttpContext ctx, DeviceAuth auth, RosterService roster) =>
            await roster.ResolutionsAsync(await auth.RequireAsync(ctx), Hash32(teamId)));

        // §3.1 / §6.1 MLS handshake (ombra-core public verification) and KeyPackages (N1)
        v1.MapPost("/teams/{teamId}/groups", async (string teamId, HttpContext ctx, DeviceAuth auth, Groups.GroupService groups) =>
        {
            var device = await auth.RequireAsync(ctx);
            return await groups.InitAsync(device, Hash32(teamId), await Envelopes.ReadBodyAsync(ctx.Request, Groups.GroupService.MaxHandshakeBytes));
        });
        v1.MapPost("/groups/{gid}/commit", async (string gid, CommitRequest req, HttpContext ctx, DeviceAuth auth, Groups.GroupService groups) =>
            await groups.CommitAsync(await auth.RequireAsync(ctx), Hash32(gid), req));
        v1.MapPost("/keypackages", async (KeyPackageUpload req, HttpContext ctx, DeviceAuth auth, Groups.KeyPackageService kps) =>
            await kps.UploadAsync(await auth.RequireAsync(ctx), req));
        v1.MapGet("/keypackages/{deviceId:guid}", async (Guid deviceId, string team, string? group, HttpContext ctx, DeviceAuth auth, Groups.KeyPackageService kps) =>
            await kps.IssueAsync(await auth.RequireAsync(ctx), deviceId, Hash32(team), group is null ? null : Hash32(group)));

        // §3.4 invites (server side), admin pending, quarantine
        v1.MapPost("/teams/{teamId}/invites", async (string teamId, HttpContext ctx, DeviceAuth auth, Envelopes envs, TeamService teams) =>
        {
            var device = await auth.RequireAsync(ctx);
            var env = await envs.FromBodyAsync(ctx.Request, device, EnvelopeKinds.InviteCreate);
            return await teams.CreateInviteAsync(device, Hash32(teamId), env);
        });
        v1.MapGet("/invites/{id:guid}", (Guid id, TeamService teams) => teams.GetInviteAsync(id)).RequireRateLimiting(RequestGuards.PreAuthPolicy);
        v1.MapPost("/invites/{id:guid}/join", (Guid id, HttpContext ctx, TeamService teams) => teams.JoinAsync(id, ctx.Request))
            .RequireRateLimiting(RequestGuards.PreAuthPolicy);
        v1.MapGet("/admin/pending", async (HttpContext ctx, DeviceAuth auth, TeamService teams) =>
            await teams.PendingAsync(await auth.RequireAsync(ctx)));
        v1.MapPost("/teams/{teamId}/quarantine/release", async (string teamId, HttpContext ctx, DeviceAuth auth, Envelopes envs, TeamService teams) =>
        {
            var device = await auth.RequireAsync(ctx);
            var env = await envs.FromBodyAsync(ctx.Request, device, EnvelopeKinds.QuarantineRelease);
            await teams.ReleaseQuarantineAsync(device, Hash32(teamId), env);
            return Results.NoContent();
        });

        // §2.3 pairing channel (SAS commit-then-reveal; the new device is unauthenticated), DeviceCertificate, DeviceRevocation
        v1.MapPost("/devices/pair", async (PairOpenRequest req, HttpContext ctx, DeviceAuth auth, Devices.DeviceService devices) =>
            await devices.OpenPairAsync(await auth.RequireAsync(ctx), req));
        v1.MapGet("/devices/pair/{code}", (string code, Devices.DeviceService devices) => devices.GetPairAsync(code))
            .RequireRateLimiting(RequestGuards.PreAuthPolicy);
        v1.MapPost("/devices/pair/{code}/join", (string code, PairJoinRequest req, Devices.DeviceService devices) => devices.JoinPairAsync(code, req))
            .RequireRateLimiting(RequestGuards.PreAuthPolicy);
        v1.MapPost("/devices/pair/{code}/reveal", async (string code, PairRevealRequest req, HttpContext ctx, DeviceAuth auth, Devices.DeviceService devices) =>
            await devices.RevealPairAsync(await auth.RequireAsync(ctx), code, req));
        v1.MapPost("/devices/certificate", async (DeviceCertificateRequest req, HttpContext ctx, DeviceAuth auth, Envelopes envs, Devices.DeviceService devices) =>
            await devices.RegisterAsync(await auth.RequireAsync(ctx), req, envs));
        v1.MapGet("/devices/{deviceId:guid}/certificate", async (Guid deviceId, HttpContext ctx, DeviceAuth auth, Devices.DeviceService devices) =>
            await devices.RecordAsync(await auth.RequireAsync(ctx), deviceId));
        v1.MapPost("/devices/{deviceId:guid}/grant", async (Guid deviceId, DeviceGrantRequest req, HttpContext ctx, DeviceAuth auth, Envelopes envs, Devices.DeviceService devices) =>
        {
            await devices.UpdateGrantAsync(await auth.RequireAsync(ctx), deviceId, req, envs);
            return Results.NoContent();
        });
        v1.MapGet("/devices", async (HttpContext ctx, DeviceAuth auth, Devices.DeviceService devices) =>
            await devices.OwnDevicesAsync(await auth.RequireAsync(ctx)));
        v1.MapPost("/devices/revoke", async (HttpContext ctx, DeviceAuth auth, Envelopes envs, Devices.DeviceService devices) =>
        {
            var device = await auth.RequireAsync(ctx);
            return await devices.RevokeAsync(device, await envs.FromBodyAsync(ctx.Request, device, EnvelopeKinds.DeviceRevoke));
        });

        // §4.1 heartbeat, §4.3 ownership, §5.1 п.6 RemovalNotice
        v1.MapPost("/teams/{teamId}/heartbeat", async (string teamId, HttpContext ctx, DeviceAuth auth, TeamSignalsService signals) =>
        {
            var device = await auth.RequireAsync(ctx);
            await signals.PutHeartbeatAsync(device, Hash32(teamId), await Envelopes.ReadBodyAsync(ctx.Request, TeamSignalsService.MaxHeartbeatBytes));
            return Results.NoContent();
        });
        v1.MapGet("/teams/{teamId}/heartbeats", async (string teamId, HttpContext ctx, DeviceAuth auth, TeamSignalsService signals) =>
            await signals.HeartbeatsAsync(await auth.RequireAsync(ctx), Hash32(teamId)));
        v1.MapPost("/teams/{teamId}/ownership/offer", async (string teamId, OwnershipOfferRequest req, HttpContext ctx, DeviceAuth auth, TeamSignalsService signals) =>
            await signals.OfferAsync(await auth.RequireAsync(ctx), Hash32(teamId), req));
        v1.MapGet("/teams/{teamId}/ownership", async (string teamId, HttpContext ctx, DeviceAuth auth, TeamSignalsService signals) =>
            await signals.OpenOfferAsync(await auth.RequireAsync(ctx), Hash32(teamId)));
        v1.MapPost("/teams/{teamId}/ownership/accept", async (string teamId, HttpContext ctx, DeviceAuth auth, TeamSignalsService signals) =>
        {
            var device = await auth.RequireAsync(ctx);
            return await signals.AcceptAsync(device, Hash32(teamId), await Envelopes.ReadBodyAsync(ctx.Request, 1 << 20));
        });
        v1.MapPost("/teams/{teamId}/removal-notices", async (string teamId, HttpContext ctx, DeviceAuth auth, TeamSignalsService signals) =>
        {
            var device = await auth.RequireAsync(ctx);
            await signals.PutRemovalNoticeAsync(device, Hash32(teamId), await Envelopes.ReadBodyAsync(ctx.Request, 4096));
            return Results.NoContent();
        });
        v1.Map("/stream", StreamEndpoint.HandleAsync).RequireRateLimiting(RequestGuards.PreAuthPolicy);
    }
}
