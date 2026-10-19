using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Ombra.Server.Contracts;
using Ombra.Server.Logs;
using Ombra.Server.Retention;
using Ombra.Server.Tests.Infra;

namespace Ombra.Server.Tests;

/// <summary>§3.3 R5-05 / N6 / N11: blob upload, membership-checked download, DELETE rules and blob-deletions.log.</summary>
public sealed class BlobTests(PostgresFixture pg)
{
    private sealed record World(ServerHarness H, byte[] Team, TestDevice Owner, TestDevice Admin, TestDevice Admin2, TestDevice M1, TestDevice M2, byte[] Space, byte[] Pair);

    private async Task<World> WorldAsync()
    {
        var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var owner = await h.AddMemberAsync(team, "owner");
        var admin = await h.AddMemberAsync(team, "admin");
        var admin2 = await h.AddMemberAsync(team, "admin");
        var m1 = await h.AddMemberAsync(team);
        var m2 = await h.AddMemberAsync(team);
        var space = await h.AddGroupAsync(team, "team", [owner, admin, admin2, m1, m2]);
        var pair = await h.AddGroupAsync(team, "pair", [m1, m2], memberA: m1.MemberId, memberB: m2.MemberId);
        foreach (var d in new[] { owner, admin, admin2, m1, m2 }) await h.LoginAsync(d);
        return new World(h, team, owner, admin, admin2, m1, m2, space, pair);
    }

    public static async Task<HttpResponseMessage> PutBlobAsync(ServerHarness h, TestDevice d, byte[] group, Guid id, byte[] data)
    {
        var (encoded, sig) = d.Envelope(EnvelopeKinds.BlobPut, group, new BlobPutBody(id, (ulong)data.Length, SHA256.HashData(data)).ToCbor(), h.Clock.GetUtcNow());
        var req = new HttpRequestMessage(HttpMethod.Put, $"/v1/blobs/{id}") { Content = new ByteArrayContent(data) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Headers.Add(Headers.Envelope, TestDevice.Base64Url(encoded));
        req.Headers.Add(Headers.Signature, sig);
        using var c = h.Client(d);
        return await c.SendAsync(req);
    }

    public static Task<HttpResponseMessage> DeleteBlobAsync(ServerHarness h, TestDevice d, byte[] group, Guid id) =>
        h.SendEnvelopeAsync(d, HttpMethod.Delete, $"/v1/blobs/{id}", EnvelopeKinds.BlobDelete, group, new BlobDeleteBody(id).ToCbor());

    [Fact]
    public async Task Upload_checks_signed_size_and_hash_and_download_checks_group_membership()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        var data = RandomNumberGenerator.GetBytes(5000);
        var id = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.OK, (await PutBlobAsync(w.H, w.M1, w.Pair, id, data)).StatusCode);
        Assert.Equal(data, await w.H.Client(w.M2).GetByteArrayAsync($"/v1/blobs/{id}"));
        // R5-05: a blob of a member-addressed item lives in the pair; other team members (even admins) get 403.
        var admin = await w.H.Client(w.Admin).GetAsync($"/v1/blobs/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        Assert.Equal(Reasons.NotMember, await ServerHarness.ReasonAsync(admin));

        // Body that does not match the signed sha256 is refused and leaves nothing behind.
        var other = Guid.CreateVersion7();
        var (encoded, sig) = w.M1.Envelope(EnvelopeKinds.BlobPut, w.Space, new BlobPutBody(other, 100, new byte[32]).ToCbor(), w.H.Clock.GetUtcNow());
        var req = new HttpRequestMessage(HttpMethod.Put, $"/v1/blobs/{other}") { Content = new ByteArrayContent(new byte[100]) };
        req.Headers.Add(Headers.Envelope, TestDevice.Base64Url(encoded));
        req.Headers.Add(Headers.Signature, sig);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.H.Client(w.M1).SendAsync(req)).StatusCode);
        Assert.Equal(1L, await w.H.ScalarAsync<long>("SELECT count(*) FROM blobs"));
        Assert.Single(Directory.GetFiles(Path.Combine(w.H.DataDir, "blobs")));
    }

    [Fact]
    public async Task N6_admin_cannot_delete_owner_or_admin_blobs_but_can_delete_member_space_blobs()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        var ofOwner = Guid.CreateVersion7();
        var ofAdmin2 = Guid.CreateVersion7();
        var ofMember = Guid.CreateVersion7();
        var inPair = Guid.CreateVersion7();
        await PutBlobAsync(w.H, w.Owner, w.Space, ofOwner, [1]);
        await PutBlobAsync(w.H, w.Admin2, w.Space, ofAdmin2, [2]);
        await PutBlobAsync(w.H, w.M1, w.Space, ofMember, [3]);
        await PutBlobAsync(w.H, w.M1, w.Pair, inPair, [4]);

        var r = await DeleteBlobAsync(w.H, w.Admin, w.Space, ofOwner);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteBlobAsync(w.H, w.Admin, w.Space, ofAdmin2)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteBlobAsync(w.H, w.M2, w.Space, ofMember)).StatusCode); // a member cannot delete others'
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteBlobAsync(w.H, w.Admin, w.Space, ofMember)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeleteBlobAsync(w.H, w.Owner, w.Pair, inPair)).StatusCode); // not a space blob
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteBlobAsync(w.H, w.Owner, w.Space, ofOwner)).StatusCode); // uploader itself

        // the uploader's other device may delete too
        var m1b = await w.H.AddDeviceAsync(w.Team, w.M1.MemberId);
        await w.H.AddLeafAsync(w.Pair, m1b);
        await w.H.LoginAsync(m1b);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteBlobAsync(w.H, m1b, w.Pair, inPair)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.H.Client(w.M2).GetAsync($"/v1/blobs/{inPair}")).StatusCode);

        var log = HashChainLog.ReadVerified(Path.Combine(w.H.DataDir, "blob-deletions.log"));
        Assert.Equal([ofMember, ofOwner, inPair], log.Select(e => Guid.Parse(e.Entry["blobId"]!.GetValue<string>())));
        Assert.Contains($"member:{w.Admin.MemberId}", log[0].Entry["by"]!.GetValue<string>());
    }

    [Fact]
    public async Task Idle_blobs_expire_after_180_days_and_are_journaled()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        var id = Guid.CreateVersion7();
        await PutBlobAsync(w.H, w.M1, w.Space, id, [9, 9]);
        await w.H.Service<RetentionService>().RunOnceAsync(w.H.Clock.GetUtcNow().AddDays(179));
        Assert.Equal(1L, await w.H.ScalarAsync<long>("SELECT count(*) FROM blobs"));
        await w.H.Service<RetentionService>().RunOnceAsync(w.H.Clock.GetUtcNow().AddDays(181));
        Assert.Equal(0L, await w.H.ScalarAsync<long>("SELECT count(*) FROM blobs"));
        var log = HashChainLog.ReadVerified(Path.Combine(w.H.DataDir, "blob-deletions.log"));
        Assert.Equal("ttl", log.Single().Entry["by"]!.GetValue<string>());
    }
}
