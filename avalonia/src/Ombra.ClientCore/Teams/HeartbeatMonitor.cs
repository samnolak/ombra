namespace Ombra.ClientCore.Teams;

/// <summary>
/// Heartbeat duties of one team (§4.1, SD-06): an owner/admin device publishes a signed heartbeat every
/// <see cref="PublishInterval"/> (and right after every removal, see <see cref="TeamClient.RemoveMemberAsync"/>); every
/// device checks the team's heartbeats through ombra-core. Without confirmation for T (72 h) the team is
/// "not confirmed" and <see cref="MaySend"/> turns false: the client stops sending new content.
/// </summary>
public sealed class HeartbeatMonitor(TeamClient client, string teamId)
{
    public static readonly TimeSpan PublishInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    private DateTimeOffset _lastPublished = DateTimeOffset.MinValue;

    public string TeamId { get; } = teamId;
    public TeamConfirmation? Last { get; private set; }
    /// <summary>False when the team state is not confirmed (or not checked yet).</summary>
    public bool MaySend => Last?.Confirmed == true;

    public event Action<TeamConfirmation>? Changed;

    /// <summary>One round: publish if due (admins/owner), then fetch, verify and count the team's heartbeats.</summary>
    public async Task<TeamConfirmation> TickAsync(CancellationToken ct = default)
    {
        var now = client.Server.Clock.GetUtcNow();
        if (client.IsAdmin(TeamId) && now - _lastPublished >= PublishInterval)
        {
            await client.PublishHeartbeatAsync(TeamId, ct);
            _lastPublished = now;
        }
        var c = await client.CheckConfirmationAsync(TeamId, ct);
        var changed = Last != c;
        Last = c;
        if (changed) Changed?.Invoke(c);
        return c;
    }

    /// <summary>Runs <see cref="TickAsync"/> every <see cref="CheckInterval"/> until cancelled; failures leave the last state in place.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(CheckInterval, client.Server.Clock);
        do
        {
            try { await TickAsync(ct); }
            catch (TeamsServerException) { }
            catch (HttpRequestException) { }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
