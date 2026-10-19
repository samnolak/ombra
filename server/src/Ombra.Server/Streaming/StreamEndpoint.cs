using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;
using Ombra.Server.Delivery;
using Ombra.Server.Revocation;

namespace Ombra.Server.Streaming;

public static class StreamEndpoint
{
    private const int MaxFrame = 4096;

    /// <summary>
    /// WSS /v1/stream. First client frame within 10 s: <c>{"type":"auth","token":"…"}</c> (browsers cannot set headers on
    /// WebSocket). Then client frames <c>{"type":"activity","state":"online|inactive"}</c>; everything else closes, and so
    /// does changing the state faster than the per-connection presence budget. At most
    /// <see cref="LimitsOptions.PreAuthStreamsMax"/> handshakes may be waiting for their auth frame (503 beyond).
    /// </summary>
    public static async Task HandleAsync(HttpContext ctx, StreamHub hub, TokenService tokens, NpgsqlDataSource db, StreamRevalidator revalidator,
        IOptions<OmbraOptions> options, TimeProvider clock, ILoggerFactory lf)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var limits = options.Value.Limits;
        if (!hub.TryEnterPreAuth(limits.PreAuthStreamsMax))
            throw new ApiException(503, Reasons.RateLimited, "too many unauthenticated stream handshakes") { RetryAfter = TimeSpan.FromSeconds(5) };
        WebSocket socket;
        StreamAuth? auth;
        var buf = new byte[MaxFrame];
        try
        {
            socket = await ctx.WebSockets.AcceptWebSocketAsync();
            using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            authTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var first = await ReadFrameAsync(socket, buf, authTimeout.Token);
                auth = first is null ? null : JsonSerializer.Deserialize<StreamAuth>(first.Value.Span, JsonDefaults.Options);
            }
            catch (Exception e) when (e is OperationCanceledException or JsonException or WebSocketException)
            {
                auth = null;
            }
        }
        finally
        {
            hub.ExitPreAuth();
        }
        using var _ = socket;
        var deviceId = auth is { Type: "auth", Token: { } t } ? await tokens.ValidateAccessAsync(t) : null;
        if (deviceId is null)
        {
            await CloseQuietly(socket, WebSocketCloseStatus.PolicyViolation, Reasons.Unauthorized);
            return;
        }

        DeviceIdentity? device;
        List<string> teams;
        string? deny = null;
        await using (var conn = await db.OpenConnectionAsync())
        {
            device = await AccessService.DeviceAsync(conn, null, deviceId.Value);
            teams = await conn.ListAsync("SELECT team_id FROM team_device_access WHERE device_id = @d", null, r => Ids.Hex(r.Bytes(0)), ("d", deviceId.Value));
            if (device is not null && teams.Count == 0)
            {
                var known = await AccessService.TeamsOfDeviceAsync(conn, null, device.DeviceId);
                if (known.Count > 0) deny = await AccessService.DenyReasonAsync(conn, null, known[0], device.DeviceId);
            }
        }
        if (device is null || deny is not null)
        {
            await CloseQuietly(socket, WebSocketCloseStatus.PolicyViolation, deny ?? Reasons.Revoked);
            return;
        }

        var c = new StreamHub.Connection(device.DeviceId, device.MemberId, teams, socket);
        hub.Add(c);
        c.Outbox.Writer.TryWrite(new StreamEvent(StreamEventTypes.Ready));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, c.Closing.Token);
        var pump = hub.PumpAsync(c, cts.Token);
        var refill = TimeSpan.FromSeconds(limits.PresenceRefillSeconds);
        var closeReason = "bye";
        var closeStatus = WebSocketCloseStatus.NormalClosure;
        try
        {
            // A revocation whose NOTIFY was handled between the access query above and hub.Add found no socket to close:
            // re-check now that the connection is registered (later changes: NOTIFY and the periodic sweep).
            await revalidator.RevalidateAsync([c], cts.Token);
            while (!cts.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(socket, buf, cts.Token);
                if (frame is null) break;
                var msg = JsonSerializer.Deserialize<ActivityFrame>(frame.Value.Span, JsonDefaults.Options);
                if (msg is not { Type: "activity", State: { } state }) break;
                if (!hub.TrySetState(c, state, limits.PresenceBurst, refill, clock.GetUtcNow().UtcTicks))
                {
                    (closeStatus, closeReason) = (WebSocketCloseStatus.PolicyViolation, Reasons.RateLimited);
                    break;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or JsonException or WebSocketException or NpgsqlException)
        {
            lf.CreateLogger("stream").LogDebug("stream {Device} closed: {Message}", c.DeviceId, e.Message);
        }
        finally
        {
            hub.Remove(c);
            c.Outbox.Writer.TryComplete();
            await cts.CancelAsync();
            await pump;
            await CloseQuietly(socket, closeStatus, closeReason);
        }
    }

    private sealed record ActivityFrame(string Type, string? State);

    private static async Task<ReadOnlyMemory<byte>?> ReadFrameAsync(WebSocket socket, byte[] buf, CancellationToken ct)
    {
        var count = 0;
        while (true)
        {
            if (count == buf.Length) throw new WebSocketException("frame too large");
            var r = await socket.ReceiveAsync(buf.AsMemory(count), ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            count += r.Count;
            if (r.EndOfMessage) return buf.AsMemory(0, count);
        }
    }

    private static async Task CloseQuietly(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(status, reason, CancellationToken.None);
        }
        catch (WebSocketException) { }
    }
}

/// <summary>
/// LISTEN ombra_revoked: revocations committed by this process or by ombra-admin close the sockets of the affected
/// devices and drop their rate-limit state (§5.2 п.1 "закрытие сокетов").
/// </summary>
public sealed class RevocationListener(NpgsqlDataSource db, StreamHub hub, OpRateLimiter rate, StreamRevalidator revalidator, ILogger<RevocationListener> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(stoppingToken);
                conn.Notification += (_, e) =>
                {
                    try
                    {
                        var n = JsonSerializer.Deserialize<RevokedNotice>(e.Payload)!;
                        hub.Close(n.Devices, n.Reason);
                        rate.Forget(n.Devices);
                    }
                    catch (JsonException ex)
                    {
                        log.LogError(ex, "bad revocation notice");
                    }
                };
                await conn.ExecAsync("LISTEN " + RevocationService.NotifyChannel, null);
                // Notices sent while the listener was (re)connecting are lost: re-check every open socket now.
                await revalidator.RevalidateAsync(hub.Connections(), stoppingToken);
                while (!stoppingToken.IsCancellationRequested) await conn.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (NpgsqlException e)
            {
                log.LogError(e, "revocation listener lost its connection; reconnecting");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
