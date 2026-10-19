using System.Net;
using System.Net.Http.Json;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Tests.Infra;

namespace Ombra.Server.Tests;

/// <summary>§2.2 hello, §2.4 bootstrap with code + pin (SD-13), health, protocol versions.</summary>
public sealed class SessionTests(PostgresFixture pg)
{
    private static async Task<HttpResponseMessage> BootstrapAsync(ServerHarness h, TestDevice d, string code, string? pin)
    {
        using var c = h.Client();
        var ch = (await (await c.PostAsJsonAsync("/v1/session/challenge", new ChallengeRequest(d.DeviceId))).Content.ReadFromJsonAsync<ChallengeResponse>())!;
        var time = (ulong)h.Clock.GetUtcNow().ToUnixTimeSeconds();
        return await c.PostAsJsonAsync("/v1/bootstrap", new BootstrapRequest(code, pin, d.MemberId, d.DeviceId, "gui", d.IdentityKey, d.MlsSignatureKey,
            ch.Nonce, time, d.SignHello(ch.ServerId, ch.Nonce, time), Protocol.Current));
    }

    [Fact]
    public async Task Health_reports_server_id_and_protocol()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        using var c = h.Client();
        var health = (await c.GetFromJsonAsync<HealthResponse>("/v1/health"))!;
        Assert.Equal("ok", health.Status);
        Assert.Equal(h.ServerId, health.ServerId);
        Assert.Equal(Protocol.Current, health.ProtocolCurrent);
    }

    [Fact]
    public async Task Bootstrap_requires_the_code_and_the_pin_and_is_single_use()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        const string pin = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
        var code = await BootstrapCodes.IssueAsync(h.Db, pin, TimeSpan.FromHours(1), h.Clock.GetUtcNow());
        var owner = new TestDevice(Guid.CreateVersion7());

        var noPin = await BootstrapAsync(h, owner, code, null);
        Assert.Equal(HttpStatusCode.Forbidden, noPin.StatusCode);
        Assert.Equal(Reasons.PinMismatch, await ServerHarness.ReasonAsync(noPin));
        var wrongCode = await BootstrapAsync(h, owner, "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE-F", pin);
        Assert.Equal(Reasons.BootstrapInvalid, await ServerHarness.ReasonAsync(wrongCode));

        var ok = await BootstrapAsync(h, owner, code.ToLowerInvariant(), pin); // dashes/case are not significant
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var hello = (await ok.Content.ReadFromJsonAsync<HelloResponse>())!;
        Assert.True(hello.ServerOwner);
        Assert.Equal(owner.MemberId, hello.MemberId);

        var again = await BootstrapAsync(h, new TestDevice(Guid.CreateVersion7()), code, pin);
        Assert.Equal(Reasons.BootstrapInvalid, await ServerHarness.ReasonAsync(again));

        // deviceId is unique forever, also across bootstrap codes
        var code2 = await BootstrapCodes.IssueAsync(h.Db, null, TimeSpan.FromHours(1), h.Clock.GetUtcNow());
        var reuse = await BootstrapAsync(h, new TestDevice(Guid.CreateVersion7(), owner.DeviceId), code2, null);
        Assert.Equal(HttpStatusCode.Conflict, reuse.StatusCode);
        Assert.Equal(Reasons.DeviceIdReused, await ServerHarness.ReasonAsync(reuse));
    }

    [Fact]
    public async Task Bootstrap_code_expires_after_its_ttl()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        var code = await BootstrapCodes.IssueAsync(h.Db, null, TimeSpan.FromHours(1), h.Clock.GetUtcNow());
        h.Clock.Advance(TimeSpan.FromMinutes(61));
        var r = await BootstrapAsync(h, new TestDevice(Guid.CreateVersion7()), code, null);
        Assert.Equal(Reasons.BootstrapInvalid, await ServerHarness.ReasonAsync(r));
    }

    [Fact]
    public async Task Hello_checks_challenge_signature_protocol_and_refresh_rotates()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var d = await h.AddMemberAsync(team);

        var old = await h.HelloAsync(d, protocol: 99);
        Assert.Equal((HttpStatusCode)426, old.StatusCode);

        using (var c = h.Client())
        {
            var ch = (await (await c.PostAsJsonAsync("/v1/session/challenge", new ChallengeRequest(d.DeviceId))).Content.ReadFromJsonAsync<ChallengeResponse>())!;
            var time = (ulong)h.Clock.GetUtcNow().ToUnixTimeSeconds();
            var forged = new TestDevice(d.MemberId, d.DeviceId).SignHello(ch.ServerId, ch.Nonce, time); // other key, same deviceId
            var bad = await c.PostAsJsonAsync("/v1/session/hello", new HelloRequest(Protocol.Current, d.DeviceId, ch.Nonce, time, forged));
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
            Assert.Equal(Reasons.BadSignature, await ServerHarness.ReasonAsync(bad));
            var good = await c.PostAsJsonAsync("/v1/session/hello", new HelloRequest(Protocol.Current, d.DeviceId, ch.Nonce, time, d.SignHello(ch.ServerId, ch.Nonce, time)));
            Assert.Equal(HttpStatusCode.OK, good.StatusCode);
            var replay = await c.PostAsJsonAsync("/v1/session/hello", new HelloRequest(Protocol.Current, d.DeviceId, ch.Nonce, time, d.SignHello(ch.ServerId, ch.Nonce, time)));
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode); // challenge is single use
        }

        await h.LoginAsync(d);
        Assert.Equal(HttpStatusCode.OK, (await h.Client(d).GetAsync("/v1/sync")).StatusCode);
        h.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client(d).GetAsync("/v1/sync")).StatusCode); // access token 15 min
        using var rc = h.Client();
        var refreshed = await rc.PostAsJsonAsync("/v1/session/refresh", new RefreshRequest(d.RefreshToken!));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var reused = await rc.PostAsJsonAsync("/v1/session/refresh", new RefreshRequest(d.RefreshToken!));
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode); // refresh is single use
    }
}
