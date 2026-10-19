using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ombra.Server.Contracts;
using Ombra.Server.Tests.Infra;

namespace Ombra.Server.Tests;

/// <summary>
/// S0 rows N1 (KeyPackage issuance and limits counted on accepted Add), N2 (server side: first group wins, uploader
/// must be a leaf, newer generation closes older → 409 closed-generation) and /commit ordering (409 epoch-conflict),
/// with groups, commits and KeyPackages produced by real ombra-core client state.
/// </summary>
public sealed class GroupCommitTests(PostgresFixture pg)
{
    private sealed record World(ServerHarness H, string TeamId, CoreDevice A, CoreDevice B, CoreDevice C) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            A.Dispose(); B.Dispose(); C.Dispose();
            await H.DisposeAsync();
        }
    }

    private static ulong Now(ServerHarness h) => (ulong)h.Clock.GetUtcNow().ToUnixTimeSeconds();
    private static string Hex(Guid id) => CoreDevice.Hex(Ids.ToBytes(id));

    private static HttpClient Client(ServerHarness h, CoreDevice d)
    {
        var c = h.Client();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", d.AccessToken);
        return c;
    }

    private static async Task<HttpResponseMessage> PostBytesAsync(ServerHarness h, CoreDevice d, string url, string hex)
    {
        using var c = Client(h, d);
        var content = new ByteArrayContent(Convert.FromHexString(hex));
        content.Headers.ContentType = new MediaTypeHeaderValue(Headers.MlsContentType);
        return await c.PostAsync(url, content);
    }

    /// <summary>Team: A owner (ServerOwner), B and C members; every client has the verified chain.</summary>
    private async Task<World> WorldAsync()
    {
        var h = await ServerHarness.StartAsync(pg);
        var (a, b, c) = (new CoreDevice(), new CoreDevice(), new CoreDevice());
        await a.RegisterAsync(h, serverOwner: true);
        await b.RegisterAsync(h);
        await c.RegisterAsync(h);
        var sid = Hex(h.ServerId);
        var created = a.Call("team.create", new() { ["serverId"] = sid, ["recoveryKey"] = CoreDevice.Hex(RandomNumberGenerator.GetBytes(32)), ["issuedAt"] = Now(h) });
        var teamId = created["teamId"]!.GetValue<string>();
        var v1 = created["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(h, a, "/v1/teams", v1)).StatusCode);
        var v2 = a.Call("roster.update", new()
        {
            ["teamId"] = teamId, ["apply"] = true, ["now"] = Now(h),
            ["addMembers"] = new JsonArray(
                new JsonObject { ["memberId"] = Hex(b.MemberId), ["certs"] = new JsonArray(b.Certificate(h.ServerId, Now(h))) },
                new JsonObject { ["memberId"] = Hex(c.MemberId), ["certs"] = new JsonArray(c.Certificate(h.ServerId, Now(h))) }),
        })["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(h, a, $"/v1/teams/{teamId}/roster", v2)).StatusCode);
        foreach (var d in new[] { b, c })
            d.Call("team.import", new() { ["teamId"] = teamId, ["updates"] = new JsonArray(v1, v2), ["serverId"] = sid, ["now"] = Now(h) });
        return new World(h, teamId, a, b, c);
    }

    private static JsonObject PairDescriptor(World w, CoreDevice x, CoreDevice y, int generation)
    {
        var members = new[] { Hex(x.MemberId), Hex(y.MemberId) }.Order(StringComparer.Ordinal).ToArray();
        return new JsonObject { ["type"] = "pair", ["teamId"] = w.TeamId, ["members"] = new JsonArray(members[0], members[1]), ["generation"] = generation };
    }

    private static (string GroupId, string GroupInfo) CreateGroup(CoreDevice d, JsonObject descriptor)
    {
        var gid = d.Call("group.create", new() { ["descriptor"] = descriptor })["groupId"]!.GetValue<string>();
        return (gid, d.Call("group.public_info", new() { ["groupId"] = gid })["groupInfo"]!.GetValue<string>());
    }

    private static async Task UploadKeyPackagesAsync(ServerHarness h, CoreDevice d, int count, bool lastResort = false)
    {
        var kps = Enumerable.Range(0, count).Select(_ => Convert.FromHexString(d.Call("key_package", new() { ["lastResort"] = lastResort })["keyPackage"]!.GetValue<string>())).ToList();
        using var c = Client(h, d);
        var r = await c.PostAsJsonAsync("/v1/keypackages", new KeyPackageUpload(kps));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(count, (await r.Content.ReadFromJsonAsync<KeyPackageUploadResponse>())!.Stored);
    }

    private static async Task<HttpResponseMessage> FetchKpAsync(World w, CoreDevice requester, CoreDevice target)
    {
        using var c = Client(w.H, requester);
        return await c.GetAsync($"/v1/keypackages/{target.DeviceId}?team={w.TeamId}");
    }

    private static async Task<HttpResponseMessage> CommitAsync(World w, CoreDevice d, string gid, string commitHex, string? welcomeHex)
    {
        using var c = Client(w.H, d);
        return await c.PostAsJsonAsync($"/v1/groups/{gid}/commit",
            new CommitRequest(Convert.FromHexString(commitHex), welcomeHex is null ? null : Convert.FromHexString(welcomeHex)));
    }

    [Fact]
    public async Task N1_N2_pair_creation_keypackages_and_commit_ordering()
    {
        await using var w = await WorldAsync();
        var h = w.H;
        var (gid, gi) = CreateGroup(w.A, PairDescriptor(w, w.A, w.B, 0));
        await UploadKeyPackagesAsync(h, w.B, 3);

        // N1: no pair yet → nobody but admins/own member may fetch B's KeyPackages
        Assert.Equal(HttpStatusCode.Forbidden, (await FetchKpAsync(w, w.C, w.B)).StatusCode);
        // N2: C cannot occupy the A–B groupId with a tree it is not part of
        Assert.Equal(Reasons.NotMember, await ServerHarness.ReasonAsync(await PostBytesAsync(h, w.C, $"/v1/teams/{w.TeamId}/groups", gi)));
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(h, w.A, $"/v1/teams/{w.TeamId}/groups", gi)).StatusCode);
        // N2: B created the same pair concurrently → one wins, the second gets 409 and waits for the Welcome
        var (gidB, giB) = CreateGroup(w.B, PairDescriptor(w, w.A, w.B, 0));
        Assert.Equal(gid, gidB);
        var race = await PostBytesAsync(h, w.B, $"/v1/teams/{w.TeamId}/groups", giB);
        Assert.Equal(HttpStatusCode.Conflict, race.StatusCode);
        Assert.Equal(Reasons.Exists, await ServerHarness.ReasonAsync(race));

        // C still has no pair with B → 403; A (pair peer) gets one; not consumed until an Add is accepted
        Assert.Equal(HttpStatusCode.Forbidden, (await FetchKpAsync(w, w.C, w.B)).StatusCode);
        var kp1 = (await (await FetchKpAsync(w, w.A, w.B)).Content.ReadFromJsonAsync<KeyPackageResponse>())!;
        var unused = w.A.Call("group.add", new() { ["groupId"] = gid, ["keyPackages"] = new JsonArray(CoreDevice.Hex(kp1.KeyPackage)), ["now"] = Now(h) });
        _ = unused;
        // the commit built on kp1 is never accepted (A's client moved on); a replay of a stale epoch is 409 below.
        var kp2Resp = await FetchKpAsync(w, w.A, w.B);
        Assert.Equal(HttpStatusCode.OK, kp2Resp.StatusCode); // no accepted Add yet → issued again
        var kp2 = (await kp2Resp.Content.ReadFromJsonAsync<KeyPackageResponse>())!;
        Assert.False(kp2.LastResort);

        // A fresh pair generation 1 (A's client state already advanced the gen-0 group locally).
        var (gid1, gi1) = CreateGroup(w.A, PairDescriptor(w, w.A, w.B, 1));
        Assert.Equal(HttpStatusCode.OK, (await PostBytesAsync(h, w.A, $"/v1/teams/{w.TeamId}/groups", gi1)).StatusCode);
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM groups WHERE group_id = @g AND closed_at IS NOT NULL", ("g", Convert.FromHexString(gid))));
        var stale = await CommitAsync(w, w.A, gid, unused["commit"]!.GetValue<string>(), unused["welcome"]!.GetValue<string>());
        Assert.Equal(Reasons.ClosedGeneration, await ServerHarness.ReasonAsync(stale)); // N2a: closed generation

        var add = w.A.Call("group.add", new() { ["groupId"] = gid1, ["keyPackages"] = new JsonArray(CoreDevice.Hex(kp2.KeyPackage)), ["now"] = Now(h) });
        var ok = await CommitAsync(w, w.A, gid1, add["commit"]!.GetValue<string>(), add["welcome"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var g = (await ok.Content.ReadFromJsonAsync<GroupResponse>())!;
        Assert.Equal(1, g.Epoch);
        Assert.Contains(w.B.DeviceId, g.Leaves);

        // replaying the same commit: its epoch is no longer current → 409 epoch-conflict
        var replay = await CommitAsync(w, w.A, gid1, add["commit"]!.GetValue<string>(), add["welcome"]!.GetValue<string>());
        Assert.Equal(Reasons.EpochConflict, await ServerHarness.ReasonAsync(replay));
        // N1: the Add was accepted → the second KP of the same device pair within 24 h is 429
        Assert.Equal((HttpStatusCode)429, (await FetchKpAsync(w, w.A, w.B)).StatusCode);

        // The Welcome waits in B's queue (kind = welcome) and B can join with it.
        using (var cb = Client(h, w.B))
        {
            var sync = (await cb.GetFromJsonAsync<SyncResponse>("/v1/sync"))!;
            var welcome = sync.Ops.Single(o => o.Kind == "welcome");
            Assert.Equal(gid1, welcome.GroupId);
            w.B.Call("group.join", new() { ["welcome"] = CoreDevice.Hex(welcome.Ciphertext) });
        }

        // last resort: only for admins; a non-admin with an empty stock gets 404
        await UploadKeyPackagesAsync(h, w.A, 1, lastResort: true);
        Assert.Equal(HttpStatusCode.NotFound, (await FetchKpAsync(w, w.B, w.A)).StatusCode); // B is A's pair peer, but A has only last resort
        await UploadKeyPackagesAsync(h, w.C, 1, lastResort: true);
        var lr = (await (await FetchKpAsync(w, w.A, w.C)).Content.ReadFromJsonAsync<KeyPackageResponse>())!; // A is owner
        Assert.True(lr.LastResort);
    }

    [Fact]
    public async Task KeyPackage_upload_accepts_only_own_valid_packages()
    {
        await using var w = await WorldAsync();
        var foreign = Convert.FromHexString(w.B.Call("key_package", new())["keyPackage"]!.GetValue<string>());
        using var c = Client(w.H, w.A);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/v1/keypackages", new KeyPackageUpload([foreign]))).StatusCode);
        foreign[^3] ^= 1;
        using var cb = Client(w.H, w.B);
        Assert.Equal(HttpStatusCode.BadRequest, (await cb.PostAsJsonAsync("/v1/keypackages", new KeyPackageUpload([foreign]))).StatusCode);
    }
}
