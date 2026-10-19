using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ombra.Server.Contracts;
using Ombra.Server.Retention;
using Ombra.Server.Revocation;
using Ombra.Server.Tests.Infra;

namespace Ombra.Server.Tests;

/// <summary>§3.3 R5-09 / N3: one-shot transfers.</summary>
public sealed class TransferTests(PostgresFixture pg)
{
    private sealed record World(ServerHarness H, byte[] Team, TestDevice Admin, TestDevice M1, TestDevice M1b, TestDevice M2);

    private async Task<World> WorldAsync()
    {
        var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var admin = await h.AddMemberAsync(team, "admin");
        var m1 = await h.AddMemberAsync(team);
        var m1b = await h.AddDeviceAsync(team, m1.MemberId);
        var m2 = await h.AddMemberAsync(team);
        foreach (var d in new[] { admin, m1, m1b, m2 }) await h.LoginAsync(d);
        return new World(h, team, admin, m1, m1b, m2);
    }

    private static Task<HttpResponseMessage> CreateAsync(World w, TestDevice from, TestDevice to, Guid id, ulong size = 10) =>
        w.H.SendEnvelopeAsync(from, HttpMethod.Put, $"/v1/transfers/{id}", EnvelopeKinds.TransferCreate, w.Team,
            new TransferCreateBody(id, to.DeviceId, size, RandomNumberGenerator.GetBytes(32), (uint)((size + (1 << 20) - 1) >> 20)).ToCbor());

    [Fact]
    public async Task Rights_admin_to_member_and_member_to_own_device_only()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        Assert.Equal(HttpStatusCode.OK, (await CreateAsync(w, w.Admin, w.M2, Guid.CreateVersion7())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CreateAsync(w, w.M1, w.M1b, Guid.CreateVersion7())).StatusCode);
        var foreign = await CreateAsync(w, w.M1, w.Admin, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(Reasons.NotAdmin, await ServerHarness.ReasonAsync(foreign));
        var second = await CreateAsync(w, w.Admin, w.M2, Guid.CreateVersion7()); // ≤ 1 active transfer per target device
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(Reasons.TransferActive, await ServerHarness.ReasonAsync(second));
    }

    [Fact]
    public async Task Only_target_token_may_get_and_ack_and_ack_deletes()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        var id = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.OK, (await CreateAsync(w, w.Admin, w.M2, id)).StatusCode);
        using (var up = w.H.Client(w.Admin))
            Assert.Equal(HttpStatusCode.NoContent, (await up.PutAsync($"/v1/transfers/{id}/chunks/0", new ByteArrayContent(new byte[26]))).StatusCode);
        using (var notSender = w.H.Client(w.M2))
            Assert.Equal(HttpStatusCode.Forbidden, (await notSender.PutAsync($"/v1/transfers/{id}/chunks/0", new ByteArrayContent(new byte[5]))).StatusCode);

        foreach (var other in new[] { w.Admin, w.M1 })
        {
            using var c = w.H.Client(other);
            var get = await c.GetAsync($"/v1/transfers/{id}/chunks/0");
            Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
            Assert.Equal(Reasons.NotTarget, await ServerHarness.ReasonAsync(get));
            var ack = await c.PostAsync($"/v1/transfers/{id}/ack", null);
            Assert.Equal(HttpStatusCode.Forbidden, ack.StatusCode);
        }

        using var t = w.H.Client(w.M2);
        var info = (await t.GetFromJsonAsync<TransferInfo>($"/v1/transfers/{id}"))!;
        Assert.Equal(1, info.UploadedChunks);
        Assert.Equal(26, (await t.GetByteArrayAsync($"/v1/transfers/{id}/chunks/0")).Length);
        Assert.Equal(26, (await t.GetByteArrayAsync($"/v1/transfers/{id}/chunks/0")).Length); // resumable until ack
        Assert.Equal(HttpStatusCode.NoContent, (await t.PostAsync($"/v1/transfers/{id}/ack", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t.GetAsync($"/v1/transfers/{id}/chunks/0")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(w.H.DataDir, "transfers", id.ToString("N"))));
    }

    [Fact]
    public async Task Blocking_sender_or_target_deletes_transfers_and_ttl_is_24h()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        var bySender = Guid.CreateVersion7();
        var toTarget = Guid.CreateVersion7();
        var survivor = Guid.CreateVersion7();
        await CreateAsync(w, w.M1, w.M1b, bySender);
        await CreateAsync(w, w.Admin, w.M2, toTarget);
        using (var up = w.H.Client(w.M1))
            await up.PutAsync($"/v1/transfers/{bySender}/chunks/0", new ByteArrayContent(new byte[20]));

        var rev = w.H.Service<RevocationService>();
        var r1 = await rev.BlockAsync(new RevocationRequest(RevocationKind.DeviceRevoke, w.Team, w.M1.DeviceId, "operator", "test", null));
        Assert.Equal(1, r1.TransfersDeleted); // N3: the sender's active transfer is gone
        Assert.False(Directory.Exists(Path.Combine(w.H.DataDir, "transfers", bySender.ToString("N"))));
        var r2 = await rev.BlockAsync(new RevocationRequest(RevocationKind.MemberRemove, w.Team, w.M2.MemberId, "operator", "test", null));
        Assert.Equal(1, r2.TransfersDeleted);

        Assert.Equal(HttpStatusCode.OK, (await CreateAsync(w, w.Admin, w.M1b, survivor)).StatusCode);
        await w.H.Service<RetentionService>().RunOnceAsync(w.H.Clock.GetUtcNow().AddHours(23));
        Assert.Equal(1L, await w.H.ScalarAsync<long>("SELECT count(*) FROM transfers"));
        await w.H.Service<RetentionService>().RunOnceAsync(w.H.Clock.GetUtcNow().AddHours(24).AddSeconds(1));
        Assert.Equal(0L, await w.H.ScalarAsync<long>("SELECT count(*) FROM transfers"));
    }

    [Fact]
    public async Task Hourly_rate_and_size_limits()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        var tooBig = await CreateAsync(w, w.Admin, w.M2, Guid.CreateVersion7(), size: (1UL << 30) + 1);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.CreateVersion7();
            Assert.Equal(HttpStatusCode.OK, (await CreateAsync(w, w.Admin, w.M2, id)).StatusCode);
            using var t = w.H.Client(w.M2);
            await t.PostAsync($"/v1/transfers/{id}/ack", null);
        }
        var sixth = await CreateAsync(w, w.Admin, w.M2, Guid.CreateVersion7());
        Assert.Equal((HttpStatusCode)429, sixth.StatusCode);
        w.H.Clock.Advance(TimeSpan.FromMinutes(61));
        await w.H.LoginAsync(w.Admin); // access tokens live 15 min
        Assert.Equal(HttpStatusCode.OK, (await CreateAsync(w, w.Admin, w.M2, Guid.CreateVersion7())).StatusCode);
    }
}
