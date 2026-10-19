using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Tests.Infra;

namespace Ombra.Server.Tests;

/// <summary>
/// Implementation review hardening: request body limits and pre-auth quotas (challenges, rate limit, pending WS
/// handshakes); WSS re-validation of access without relying on NOTIFY; presence change rate per connection.
/// </summary>
public sealed class HardeningTests(PostgresFixture pg)
{
    private static async Task<WebSocket> StreamAsync(ServerHarness h, TestDevice d)
    {
        var ws = await h.Factory.Server.CreateWebSocketClient().ConnectAsync(new Uri(h.Factory.Server.BaseAddress, "v1/stream"), CancellationToken.None);
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new StreamAuth("auth", d.AccessToken!), JsonDefaults.Options), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal(StreamEventTypes.Ready, (await ReceiveAsync(ws))!.Type);
        return ws;
    }

    /// <summary>Next event; null when the server closed the socket; throws on timeout.</summary>
    private static async Task<StreamEvent?> ReceiveAsync(WebSocket ws, int seconds = 10)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var buf = new byte[8192];
        var r = await ws.ReceiveAsync(buf, cts.Token);
        return r.MessageType == WebSocketMessageType.Close ? null : JsonSerializer.Deserialize<StreamEvent>(buf.AsSpan(0, r.Count), JsonDefaults.Options);
    }

    private static Task SendActivityAsync(WebSocket ws, string state) =>
        ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"type\":\"activity\",\"state\":\"{state}\"}}"), WebSocketMessageType.Text, true, CancellationToken.None);

    [Fact]
    public async Task Request_bodies_are_capped_per_route_before_buffering()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var x = await h.AddMemberAsync(team);
        var group = await h.AddGroupAsync(team, "team", [x]);
        await h.LoginAsync(x);
        var pad = new string('a', 1 << 20);
        using var anon = h.Client();
        // Unauthenticated JSON endpoints: 1 MiB is refused with 413 instead of being buffered.
        foreach (var path in new[] { "/v1/session/challenge", "/v1/session/hello", "/v1/session/refresh", "/v1/bootstrap" })
        {
            var r = await anon.PostAsync(path, new StringContent($"{{\"deviceId\":\"{Guid.NewGuid()}\",\"pad\":\"{pad}\"}}", Encoding.UTF8, "application/json"));
            Assert.True(r.StatusCode == HttpStatusCode.RequestEntityTooLarge, $"{path}: {r.StatusCode}");
        }
        // Authenticated small-JSON endpoint (ack) is capped too; handshake JSON keeps a larger, bounded limit.
        using var c = h.Client(x);
        var ack = await c.PostAsync($"/v1/groups/{Ids.Hex(group)}/ops/ack", new StringContent($"{{\"ops\":[],\"pad\":\"{pad}\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, ack.StatusCode);
        var hugeCommit = await c.PostAsync($"/v1/groups/{Ids.Hex(group)}/commit", new StringContent($"{{\"commit\":\"{new string('A', 8 << 20)}\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, hugeCommit.StatusCode);
        // The streaming blob upload keeps its own (large) limit.
        Assert.Equal(HttpStatusCode.OK, (await BlobTests.PutBlobAsync(h, x, group, Guid.CreateVersion7(), new byte[1 << 20])).StatusCode);
    }

    [Fact]
    public async Task Hello_challenges_are_capped_per_device()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        using var c = h.Client();
        var device = Guid.NewGuid();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 20; i++) codes.Add((await c.PostAsJsonAsync("/v1/session/challenge", new ChallengeRequest(device))).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.InRange(await h.ScalarAsync<long>("SELECT count(*) FROM hello_challenges WHERE device_id = @d", ("d", device)), 1L, 8L);
        // Another device is not affected by that quota.
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/v1/session/challenge", new ChallengeRequest(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_endpoints_are_rate_limited_per_client_address()
    {
        await using var h = await ServerHarness.StartAsync(pg, s => s["Ombra:Limits:PreAuthRequestsPerMinute"] = "5");
        using var c = h.Client();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++) statuses.Add((await c.PostAsJsonAsync("/v1/session/challenge", new ChallengeRequest(Guid.NewGuid()))).StatusCode);
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.GetAsync($"/v1/invites/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/v1/health")).StatusCode); // health checks are not throttled
    }

    [Fact]
    public async Task Pending_unauthenticated_websockets_are_capped()
    {
        await using var h = await ServerHarness.StartAsync(pg, s => s["Ombra:Limits:PreAuthStreamsMax"] = "2");
        var uri = new Uri(h.Factory.Server.BaseAddress, "v1/stream");
        var held = new List<WebSocket>();
        for (var i = 0; i < 2; i++) held.Add(await h.Factory.Server.CreateWebSocketClient().ConnectAsync(uri, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => h.Factory.Server.CreateWebSocketClient().ConnectAsync(uri, CancellationToken.None));
        foreach (var ws in held) ws.Abort();
    }

    [Fact]
    public async Task Stream_access_is_revalidated_without_notify_and_a_blocked_device_is_closed()
    {
        await using var h = await ServerHarness.StartAsync(pg, s => s["Ombra:Limits:StreamRevalidateSeconds"] = "1");
        var team = await h.SeedTeamAsync();
        var admin = await h.AddMemberAsync(team, "admin");
        var victim = await h.AddMemberAsync(team);
        await h.LoginAsync(admin);
        await h.LoginAsync(victim);
        var adminWs = await StreamAsync(h, admin);
        var ws = await StreamAsync(h, victim);
        Assert.Equal(StreamEventTypes.Presence, (await ReceiveAsync(adminWs))!.Type);
        // A block lands while the NOTIFY is lost (listener reconnecting): no pg_notify here.
        await h.ExecAsync("INSERT INTO blocks (team_id, member_id, by_actor, at, audit_id) VALUES (@t, @m, 'operator', now(), gen_random_uuid())",
            ("t", team), ("m", victim.MemberId));
        var evt = await ReceiveAsync(ws, seconds: 8);
        Assert.Equal(StreamEventTypes.Removed, evt!.Type);
        Assert.Null(await ReceiveAsync(ws, seconds: 8)); // closed by the server
        Assert.Equal("offline", (await ReceiveAsync(adminWs, seconds: 8))!.State);
    }

    [Fact]
    public async Task A_device_without_any_team_yet_keeps_its_stream_through_revalidation()
    {
        // Bootstrap owner / pending joiner: registered, signed in, not in any roster yet — never "removed".
        await using var h = await ServerHarness.StartAsync(pg, s => s["Ombra:Limits:StreamRevalidateSeconds"] = "1");
        var d = new TestDevice(Guid.CreateVersion7());
        await h.ExecAsync(
            "INSERT INTO members (member_id, created_at) VALUES (@m, now()); " +
            "INSERT INTO devices (device_id, member_id, product, identity_key, mls_signature_key, created_at) VALUES (@d, @m, 'gui', @ik, @mk, now())",
            ("m", d.MemberId), ("d", d.DeviceId), ("ik", d.IdentityKey), ("mk", d.MlsSignatureKey));
        await h.LoginAsync(d);
        var ws = await StreamAsync(h, d);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReceiveAsync(ws, seconds: 3)); // nothing, in particular no "removed"
        Assert.Equal(WebSocketState.Open, ws.State);
    }

    [Fact]
    public async Task Presence_changes_are_rate_limited_per_connection()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var watcher = await h.AddMemberAsync(team);
        var spammer = await h.AddMemberAsync(team);
        await h.LoginAsync(watcher);
        await h.LoginAsync(spammer);
        var watcherWs = await StreamAsync(h, watcher);
        var ws = await StreamAsync(h, spammer);
        Assert.Equal("online", (await ReceiveAsync(watcherWs))!.State);
        for (var i = 0; i < 40; i++)
        {
            try { await SendActivityAsync(ws, i % 2 == 0 ? "inactive" : "online"); }
            catch (WebSocketException) { break; }
        }
        // The spammer is cut off …
        StreamEvent? e;
        do { e = await ReceiveAsync(ws); } while (e is not null);
        // … and the watcher saw only a bounded number of its presence changes (then "offline").
        var changes = 0;
        while (true)
        {
            var p = await ReceiveAsync(watcherWs);
            Assert.NotNull(p);
            if (p.State == "offline") break;
            changes++;
        }
        Assert.InRange(changes, 1, 6);
    }
}
