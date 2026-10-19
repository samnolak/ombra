using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;

namespace Ombra.Server.Streaming;

/// <summary>
/// Access of open WSS connections without relying on LISTEN/NOTIFY alone (§5.2 п.1 "ни байта после блокировки"):
/// right after a connection is registered, after the revocation listener (re)connects, and every
/// <see cref="LimitsOptions.StreamRevalidateSeconds"/>. A device without a live session or without access to any team is
/// closed (<c>removed</c>); otherwise the team list of its sockets follows <c>team_device_access</c>, so presence is only
/// exchanged inside teams the device may still see.
/// </summary>
public sealed class StreamRevalidator(NpgsqlDataSource db, StreamHub hub, TimeProvider clock, IOptions<OmbraOptions> options, ILogger<StreamRevalidator> log)
    : BackgroundService
{
    public async Task RevalidateAsync(IReadOnlyCollection<StreamHub.Connection> connections, CancellationToken ct = default)
    {
        if (connections.Count == 0) return;
        var ids = connections.Select(c => c.DeviceId).Distinct().ToArray();
        await using var conn = await db.OpenConnectionAsync(ct);
        var access = (await conn.ListAsync("SELECT device_id, team_id FROM team_device_access WHERE device_id = ANY(@d)", null,
                r => (Device: r.GetGuid(0), Team: Ids.Hex(r.Bytes(1))), ("d", ids)))
            .GroupBy(x => x.Device).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Team).ToList());
        var live = (await conn.ListAsync(
                "SELECT DISTINCT s.device_id FROM sessions s JOIN devices d ON d.device_id = s.device_id " +
                "WHERE s.device_id = ANY(@d) AND s.refresh_expires_at > @now AND d.revoked_at IS NULL", null,
                r => r.GetGuid(0), ("d", ids), ("now", clock.GetUtcNow())))
            .ToHashSet();
        foreach (var device in ids)
        {
            if (!live.Contains(device))
            {
                hub.Close([device], Reasons.Revoked);
                continue;
            }
            if (!access.TryGetValue(device, out var teams))
            {
                // No team yet (bootstrap owner, pending joiner) is a normal state, not a revocation: keep the socket,
                // exchange presence with nobody. Only a device that had teams and lost them all is closed.
                var known = await AccessService.TeamsOfDeviceAsync(conn, null, device);
                var reason = known.Count > 0 ? await AccessService.DenyReasonAsync(conn, null, known[0], device) : null;
                if (known.Count == 0) hub.Retarget(device, []);
                else hub.Close([device], reason ?? Reasons.Revoked);
                continue;
            }
            hub.Retarget(device, teams);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = options.Value.Limits.StreamRevalidateSeconds;
        if (seconds <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RevalidateAsync(hub.Connections(), stoppingToken);
                }
                catch (NpgsqlException e)
                {
                    log.LogError(e, "stream access sweep failed; retrying on the next tick");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
