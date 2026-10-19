using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Ombra.Server.Auth;
using Ombra.Server.Cli;
using Ombra.Server.Contracts;
using Ombra.Server.Logs;
using Ombra.Server.Tests.Infra;

namespace Ombra.Server.Tests;

/// <summary>§5.2 CLI removal (SD-07), revocations.log, sockets closed, CLI can only reduce access.</summary>
public sealed class RevocationCliTests(PostgresFixture pg)
{
    public static async Task<(int Code, string Out, string Err)> CliAsync(ServerHarness h, params string[] args)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(h.Settings).Build();
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await AdminCli.RunAsync(args, config, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private static async Task<WebSocket> StreamAsync(ServerHarness h, TestDevice d)
    {
        var ws = await h.Factory.Server.CreateWebSocketClient().ConnectAsync(new Uri(h.Factory.Server.BaseAddress, "v1/stream"), CancellationToken.None);
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new StreamAuth("auth", d.AccessToken!), JsonDefaults.Options), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal(StreamEventTypes.Ready, (await ReceiveAsync(ws))!.Type);
        return ws;
    }

    private static async Task<StreamEvent?> ReceiveAsync(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buf = new byte[8192];
        var r = await ws.ReceiveAsync(buf, cts.Token);
        return r.MessageType == WebSocketMessageType.Close ? null : JsonSerializer.Deserialize<StreamEvent>(buf.AsSpan(0, r.Count), JsonDefaults.Options);
    }

    [Fact]
    public async Task Cli_member_remove_blocks_closes_socket_journals_and_creates_pending_rotation()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var admin = await h.AddMemberAsync(team, "admin");
        var victim = await h.AddMemberAsync(team);
        var group = await h.AddGroupAsync(team, "team", [admin, victim]);
        await h.LoginAsync(admin);
        await h.LoginAsync(victim);
        var adminWs = await StreamAsync(h, admin);
        var ws = await StreamAsync(h, victim);
        var presence = await ReceiveAsync(adminWs);
        Assert.Equal((StreamEventTypes.Presence, victim.DeviceId, "online"), (presence!.Type, presence.DeviceId!.Value, presence.State!));

        await h.PutOpAsync(admin, group, 1, [1]);
        Assert.Equal(StreamEventTypes.Revision, (await ReceiveAsync(ws))!.Type);

        var (code, stdout, stderr) = await CliAsync(h, "member", "remove", "--team", Ids.Hex(team), "--member", victim.MemberId.ToString(), "--reason", "left company");
        Assert.True(code == 0, stderr);
        Assert.Contains("\"rotationId\"", stdout);

        var removed = await ReceiveAsync(ws);
        Assert.Equal(StreamEventTypes.Removed, removed!.Type);
        Assert.Null(await ReceiveAsync(ws)); // closed by the server
        Assert.Equal("offline", (await ReceiveAsync(adminWs))!.State); // teammates see the removed device go offline
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM delivery"));

        var hello = await h.HelloAsync(victim);
        Assert.Equal(HttpStatusCode.Forbidden, hello.StatusCode);
        Assert.Equal(Reasons.Removed, await ServerHarness.ReasonAsync(hello));

        var journal = HashChainLog.ReadVerified(Path.Combine(h.DataDir, "revocations.log"));
        Assert.Equal("member.remove", journal.Single().Entry["kind"]!.GetValue<string>());
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM pending_rotations WHERE blocked_member_id = @m AND cardinality(group_ids) = 1", ("m", victim.MemberId)));

        var pending = await h.Client(admin).GetFromJsonAsync<PendingResponse>("/v1/admin/pending");
        Assert.Single(pending!.Rotations);
        var sync = await h.SyncAsync(admin);
        Assert.Single(sync.Blocks);
        Assert.Equal("operator", sync.Blocks[0].By);

        // idempotent; there is no command that lifts a block or adds access
        Assert.Equal(0, (await CliAsync(h, "member", "remove", "--team", Ids.Hex(team), "--member", victim.MemberId.ToString())).Code);
        Assert.Single(HashChainLog.ReadVerified(Path.Combine(h.DataDir, "revocations.log")));
        Assert.Equal(2, (await CliAsync(h, "member", "add", "--team", Ids.Hex(team))).Code);
        Assert.Equal(2, (await CliAsync(h, "unblock", "--team", Ids.Hex(team))).Code);
    }

    [Fact]
    public async Task Cli_device_revoke_and_sessions_revoke_and_unknown_subject_fails()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var m = await h.AddMemberAsync(team);
        var other = await h.AddDeviceAsync(team, m.MemberId);
        await h.LoginAsync(m);
        await h.LoginAsync(other);

        Assert.Equal(0, (await CliAsync(h, "sessions", "revoke", "--device", other.DeviceId.ToString())).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client(other).GetAsync("/v1/sync")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.HelloAsync(other)).StatusCode); // sessions only, not a block

        Assert.Equal(0, (await CliAsync(h, "device", "revoke", "--team", Ids.Hex(team), "--device", other.DeviceId.ToString())).Code);
        var hello = await h.HelloAsync(other);
        Assert.Equal(Reasons.Blocked, await ServerHarness.ReasonAsync(hello));
        Assert.Equal(HttpStatusCode.OK, (await h.Client(m).GetAsync("/v1/sync")).StatusCode); // the member's other device keeps working

        var unknown = await CliAsync(h, "device", "revoke", "--team", Ids.Hex(team), "--device", Guid.NewGuid().ToString());
        Assert.Equal(1, unknown.Code);
    }

    [Fact]
    public async Task Logs_verify_detects_a_tampered_journal()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var a = await h.AddMemberAsync(team);
        var b = await h.AddMemberAsync(team);
        await CliAsync(h, "member", "remove", "--team", Ids.Hex(team), "--member", a.MemberId.ToString());
        await CliAsync(h, "member", "remove", "--team", Ids.Hex(team), "--member", b.MemberId.ToString());
        Assert.Equal(0, (await CliAsync(h, "logs", "verify")).Code);

        var path = Path.Combine(h.DataDir, "revocations.log");
        var lines = await File.ReadAllLinesAsync(path);
        lines[0] = lines[0].Replace(a.MemberId.ToString(), Guid.NewGuid().ToString());
        await File.WriteAllLinesAsync(path, lines);
        var (code, _, err) = await CliAsync(h, "logs", "verify");
        Assert.Equal(1, code);
        Assert.Contains("BROKEN", err);
    }

    [Fact]
    public async Task Cli_bootstrap_issue_prints_code_and_pin()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        const string pin = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
        var (code, stdout, _) = await CliAsync(h, "bootstrap", "issue", "--pin", pin);
        Assert.Equal(0, code);
        Assert.Contains("bootstrap code: ", stdout);
        Assert.Contains(pin, stdout);
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM bootstrap_codes WHERE spki_pin = @p", ("p", pin)));
        Assert.Equal(1, (await CliAsync(h, "bootstrap", "issue", "--pin", "zz")).Code);
    }
}
