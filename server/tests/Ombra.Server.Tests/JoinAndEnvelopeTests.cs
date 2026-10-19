using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ombra.Server.Contracts;
using Ombra.Server.Tests.Infra;

namespace Ombra.Server.Tests;

/// <summary>§3.4 server side of invites/JoinRequest (deviceId never reused, R5-06) and §6.1 envelope rules.</summary>
public sealed class JoinAndEnvelopeTests(PostgresFixture pg)
{
    private static async Task<(ServerHarness H, byte[] Team, TestDevice Admin, Guid InviteId, byte[] KSrv)> InviteAsync(PostgresFixture pg, uint maxUses = 5)
    {
        var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var admin = await h.AddMemberAsync(team, "admin");
        await h.LoginAsync(admin);
        var kSrv = RandomNumberGenerator.GetBytes(32);
        var inviteId = Guid.CreateVersion7();
        var head = await h.ScalarAsync<byte[]>("SELECT hash FROM roster_versions WHERE team_id = @t AND version = 1", ("t", team));
        var body = new InviteCreateBody(inviteId, RandomNumberGenerator.GetBytes(80), 1, head, "member",
            (ulong)h.Clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds(), maxUses, SHA256.HashData(kSrv));
        var r = await h.SendEnvelopeAsync(admin, HttpMethod.Post, $"/v1/teams/{Ids.Hex(team)}/invites", EnvelopeKinds.InviteCreate, team, body.ToCbor(), baseRevision: 0);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (h, team, admin, inviteId, kSrv);
    }

    private static async Task<HttpResponseMessage> JoinAsync(ServerHarness h, byte[] team, Guid inviteId, byte[] proof, Guid memberId, params TestDevice[] devices)
    {
        var now = (ulong)h.Clock.GetUtcNow().ToUnixTimeSeconds();
        var body = new JoinRequestBody(inviteId, memberId,
            devices.Select(d => new JoinDevice(d.DeviceId, "gui", d.IdentityKey, d.MlsSignatureKey, [1, 2, 3], d.Certificate(h.ServerId, memberId, now))).ToList(),
            proof, new byte[32]);
        var (encoded, sig) = devices[0].Envelope(EnvelopeKinds.JoinRequest, team, body.ToCbor(), h.Clock.GetUtcNow());
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/invites/{inviteId}/join") { Content = new ByteArrayContent(encoded) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(Headers.EnvelopeContentType);
        req.Headers.Add(Headers.Signature, sig);
        using var c = h.Client();
        return await c.SendAsync(req);
    }

    [Fact]
    public async Task Join_checks_proof_and_never_accepts_a_reused_device_or_member_id()
    {
        var (h, team, admin, inviteId, kSrv) = await InviteAsync(pg);
        await using var _ = h;
        using (var anon = h.Client())
        {
            var invite = (await anon.GetFromJsonAsync<InviteResponse>($"/v1/invites/{inviteId}"))!;
            Assert.Equal(Ids.Hex(team), invite.TeamId);
            Assert.Single(invite.Chain);
        }

        var m = Guid.CreateVersion7();
        var d = new TestDevice(m);
        var bad = await JoinAsync(h, team, inviteId, RandomNumberGenerator.GetBytes(32), m, d);
        Assert.Equal(Reasons.InviteInvalid, await ServerHarness.ReasonAsync(bad));
        Assert.Equal(HttpStatusCode.OK, (await JoinAsync(h, team, inviteId, kSrv, m, d)).StatusCode);

        var reuseDevice = await JoinAsync(h, team, inviteId, kSrv, Guid.CreateVersion7(), new TestDevice(Guid.Empty, d.DeviceId));
        Assert.Equal(HttpStatusCode.Conflict, reuseDevice.StatusCode);
        Assert.Equal(Reasons.DeviceIdReused, await ServerHarness.ReasonAsync(reuseDevice));
        var reuseAdminDevice = await JoinAsync(h, team, inviteId, kSrv, Guid.CreateVersion7(), new TestDevice(Guid.Empty, admin.DeviceId));
        Assert.Equal(Reasons.DeviceIdReused, await ServerHarness.ReasonAsync(reuseAdminDevice)); // cannot "inherit" an existing device
        var reuseMember = await JoinAsync(h, team, inviteId, kSrv, admin.MemberId, new TestDevice(admin.MemberId));
        Assert.Equal(Reasons.MemberIdReused, await ServerHarness.ReasonAsync(reuseMember));

        var pending = (await h.Client(admin).GetFromJsonAsync<PendingResponse>("/v1/admin/pending"))!;
        Assert.Equal(m, pending.Joins.Single().MemberId);
        Assert.Equal(HttpStatusCode.OK, (await h.HelloAsync(d)).StatusCode); // registered device can hello (no team yet)
    }

    [Fact]
    public async Task Invite_creation_needs_admin_and_current_revision_and_max_uses_is_enforced()
    {
        var (h, team, admin, inviteId, kSrv) = await InviteAsync(pg, maxUses: 1);
        await using var _ = h;
        var member = await h.AddMemberAsync(team);
        await h.LoginAsync(member);
        var head = await h.ScalarAsync<byte[]>("SELECT hash FROM roster_versions WHERE team_id = @t", ("t", team));
        var body = new InviteCreateBody(Guid.CreateVersion7(), [1], 1, head, "member", (ulong)h.Clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds(), 1, new byte[32]);
        var notAdmin = await h.SendEnvelopeAsync(member, HttpMethod.Post, $"/v1/teams/{Ids.Hex(team)}/invites", EnvelopeKinds.InviteCreate, team, body.ToCbor(), baseRevision: 1);
        Assert.Equal(Reasons.NotAdmin, await ServerHarness.ReasonAsync(notAdmin));
        var stale = await h.SendEnvelopeAsync(admin, HttpMethod.Post, $"/v1/teams/{Ids.Hex(team)}/invites", EnvelopeKinds.InviteCreate, team, body.ToCbor(), baseRevision: 0);
        Assert.Equal(Reasons.Revision, await ServerHarness.ReasonAsync(stale));

        var m = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.OK, (await JoinAsync(h, team, inviteId, kSrv, m, new TestDevice(m))).StatusCode);
        var m2 = Guid.CreateVersion7();
        Assert.Equal(Reasons.InviteInvalid, await ServerHarness.ReasonAsync(await JoinAsync(h, team, inviteId, kSrv, m2, new TestDevice(m2))));
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client().GetAsync($"/v1/invites/{inviteId}")).StatusCode);
    }

    [Fact]
    public async Task Envelope_rules_signer_timestamp_canonical_cbor()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var a = await h.AddMemberAsync(team);
        var b = await h.AddMemberAsync(team);
        var group = await h.AddGroupAsync(team, "team", [a, b]);
        await h.LoginAsync(a);
        await h.LoginAsync(b);
        var url = $"/v1/groups/{Ids.Hex(group)}/ops";

        async Task<HttpResponseMessage> Send(byte[] encoded, string sig, TestDevice tokenOf)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(encoded) };
            req.Headers.Add(Headers.Signature, sig);
            using var c = h.Client(tokenOf);
            return await c.SendAsync(req);
        }

        // signed by A, sent with B's token → the envelope device must be the token device
        var (enc, sig) = a.Envelope(EnvelopeKinds.OpPut, group, new OpPutBody(1, [1], false).ToCbor(), h.Clock.GetUtcNow());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(enc, sig, b)).StatusCode);
        // signature by another key
        var forged = new TestDevice(a.MemberId, a.DeviceId).Envelope(EnvelopeKinds.OpPut, group, new OpPutBody(1, [1], false).ToCbor(), h.Clock.GetUtcNow());
        Assert.Equal(Reasons.BadSignature, await ServerHarness.ReasonAsync(await Send(forged.Encoded, forged.Sig, a)));
        // stale timestamp
        var old = a.Envelope(EnvelopeKinds.OpPut, group, new OpPutBody(1, [1], false).ToCbor(), h.Clock.GetUtcNow().AddMinutes(-6));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(old.Encoded, old.Sig, a)).StatusCode);
        // non-canonical CBOR (keys not sorted) signed correctly → rejected by ombra-core before any field is used
        var nonCanonical = NonCanonicalEnvelope(a, group, h.Clock.GetUtcNow());
        var ncResp = await Send(nonCanonical, TestDevice.Base64Url(a.Sign(TestDevice.EnvelopeSigningInput(nonCanonical))), a);
        Assert.Equal(HttpStatusCode.Unauthorized, ncResp.StatusCode);
        // and the good one passes
        Assert.Equal(HttpStatusCode.OK, (await Send(enc, sig, a)).StatusCode);
    }

    private static byte[] NonCanonicalEnvelope(TestDevice d, byte[] scope, DateTimeOffset now)
    {
        var w = new System.Formats.Cbor.CborWriter(System.Formats.Cbor.CborConformanceMode.Lax);
        w.WriteStartMap(8);
        w.WriteTextString("ts"); w.WriteUInt64((ulong)now.ToUnixTimeMilliseconds());
        w.WriteTextString("v"); w.WriteUInt32(1);
        w.WriteTextString("opId"); w.WriteByteString(Ids.ToBytes(Guid.CreateVersion7()));
        w.WriteTextString("deviceId"); w.WriteByteString(Ids.ToBytes(d.DeviceId));
        w.WriteTextString("scope"); w.WriteByteString(scope);
        w.WriteTextString("baseRevision"); w.WriteUInt64(0);
        w.WriteTextString("kind"); w.WriteTextString(EnvelopeKinds.OpPut);
        w.WriteTextString("body"); w.WriteEncodedValue(new OpPutBody(1, [1], false).ToCbor().Encode());
        w.WriteEndMap();
        return w.Encode();
    }
}
