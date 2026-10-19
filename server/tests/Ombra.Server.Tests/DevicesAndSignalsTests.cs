using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ombra.Core.Interop;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Teams;
using Ombra.Server.Tests.Infra;

namespace Ombra.Server.Tests;

/// <summary>
/// §6.1 endpoints of slices S2/S3 on the server: the SAS pairing relay (R2-09), DeviceCertificate registration,
/// DeviceRevocation with socket close and cascade (§2.3 п.5), heartbeat (§4.1), OwnershipOffer/accept (§4.3),
/// RemovalNotice storage and delivery with 403 removed (§5.1 п.6, §5.3).
/// </summary>
public sealed class DevicesAndSignalsTests(PostgresFixture pg)
{
    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);
    private static string Hex(Guid id) => Hex(Ids.ToBytes(id));
    private static ulong Now(ServerHarness h) => (ulong)h.Clock.GetUtcNow().ToUnixTimeSeconds();

    private static JsonObject Party(TestDevice d, byte[] nonce) => new()
    {
        ["deviceId"] = Hex(d.DeviceId), ["identityKey"] = Hex(d.IdentityKey), ["mlsSignatureKey"] = Hex(d.MlsSignatureKey), ["nonce"] = Hex(nonce),
    };

    private static JsonObject Party(Guid deviceId, byte[] ik, byte[] mk, byte[] nonce) => new()
    {
        ["deviceId"] = Hex(deviceId), ["identityKey"] = Hex(ik), ["mlsSignatureKey"] = Hex(mk), ["nonce"] = Hex(nonce),
    };

    private static JsonNode Sas(JsonObject existing, byte[] commitment, JsonObject @new) =>
        OmbraPublic.Call("pair.sas", new() { ["existing"] = existing, ["commitment"] = Hex(commitment), ["new"] = @new });

    private static async Task<HttpResponseMessage> RegisterAsync(ServerHarness h, TestDevice issuer, TestDevice device, byte[][] teams, string? code = null, Guid? member = null)
    {
        var cert = device.Certificate(h.ServerId, member ?? issuer.MemberId, Now(h), issuer);
        var (grant, sig) = issuer.Envelope(EnvelopeKinds.DeviceGrant, Ids.ToBytes(device.DeviceId), new DeviceGrantBody(teams).ToCbor(), h.Clock.GetUtcNow());
        using var c = h.Client(issuer);
        return await c.PostAsJsonAsync("/v1/devices/certificate",
            new DeviceCertificateRequest(cert, "gui", grant, Envelopes.FromBase64Url(sig), code), JsonDefaults.Options);
    }

    [Fact]
    public async Task Sas_relay_matches_words_and_a_substituted_key_changes_them_then_the_certificate_registers_the_device()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var existing = await h.AddMemberAsync(team);
        await h.LoginAsync(existing);
        var nonceE = RandomNumberGenerator.GetBytes(32);
        var commitment = Convert.FromHexString(OmbraPublic.Call("pair.commit", Party(existing, nonceE))["commitment"]!.GetValue<string>());

        async Task<PairSession> OpenJoinRevealAsync(TestDevice newDevice, byte[] nonceN, Func<string, Task>? tamper = null)
        {
            using var ce = h.Client(existing);
            var open = (await (await ce.PostAsJsonAsync("/v1/devices/pair", new PairOpenRequest(commitment))).Content.ReadFromJsonAsync<PairOpenResponse>())!;
            Assert.Equal(existing.MemberId, open.MemberId);
            using var anon = h.Client();
            var seen = (await anon.GetFromJsonAsync<PairSession>($"/v1/devices/pair/{open.Code.ToLowerInvariant()}"))!;
            Assert.Equal(commitment, seen.ExistingCommitment);
            var joined = await anon.PostAsJsonAsync($"/v1/devices/pair/{open.Code}/join",
                new PairJoinRequest(newDevice.DeviceId, "gui", newDevice.IdentityKey, newDevice.MlsSignatureKey, nonceN));
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
            var second = await anon.PostAsJsonAsync($"/v1/devices/pair/{open.Code}/join",
                new PairJoinRequest(Guid.CreateVersion7(), "gui", RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), nonceN));
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode); // first answer wins
            if (tamper is not null) await tamper(open.Code);
            var revealed = await ce.PostAsJsonAsync($"/v1/devices/pair/{open.Code}/reveal", new PairRevealRequest(nonceE));
            Assert.Equal(HttpStatusCode.OK, revealed.StatusCode);
            return (await anon.GetFromJsonAsync<PairSession>($"/v1/devices/pair/{open.Code}"))!;
        }

        // Honest relay: both sides compute the same six words.
        var n = new TestDevice(existing.MemberId);
        var nonceN = RandomNumberGenerator.GetBytes(32);
        var s = await OpenJoinRevealAsync(n, nonceN);
        var onExisting = Sas(Party(existing, nonceE), commitment, Party(s.NewDeviceId!.Value, s.NewIdentityKey!, s.NewMlsSignatureKey!, s.NewNonce!));
        var onNew = Sas(Party(s.ExistingDeviceId, s.ExistingIdentityKey, s.ExistingMlsSignatureKey, s.ExistingNonce!), s.ExistingCommitment, Party(n, nonceN));
        Assert.True(onNew["valid"]!.GetValue<bool>());
        Assert.Equal(6, onNew["words"]!.AsArray().Count);
        Assert.Equal(onExisting["words"]!.ToJsonString(), onNew["words"]!.ToJsonString());

        // R2-09: the server substitutes the new device's key towards the existing device → different words.
        var victim = new TestDevice(existing.MemberId);
        var nonceV = RandomNumberGenerator.GetBytes(32);
        var attacker = new TestDevice(existing.MemberId);
        var t = await OpenJoinRevealAsync(victim, nonceV, code => h.ExecAsync(
            "UPDATE pairing_sessions SET new_identity_key = @k, new_mls_key = @m WHERE code = @c", ("k", attacker.IdentityKey), ("m", attacker.MlsSignatureKey), ("c", code)));
        var existingView = Sas(Party(existing, nonceE), commitment, Party(t.NewDeviceId!.Value, t.NewIdentityKey!, t.NewMlsSignatureKey!, t.NewNonce!));
        var victimView = Sas(Party(t.ExistingDeviceId, t.ExistingIdentityKey, t.ExistingMlsSignatureKey, t.ExistingNonce!), t.ExistingCommitment, Party(victim, nonceV));
        Assert.NotEqual(existingView["words"]!.ToJsonString(), victimView["words"]!.ToJsonString());
        // ...and a nonce changed after the commitment is caught by the new device.
        var forgedReveal = Sas(Party(t.ExistingDeviceId, t.ExistingIdentityKey, t.ExistingMlsSignatureKey, RandomNumberGenerator.GetBytes(32)), t.ExistingCommitment, Party(victim, nonceV));
        Assert.False(forgedReveal["valid"]!.GetValue<bool>());

        // The certificate must certify exactly the keys of the revealed session.
        var wrongKeys = await RegisterAsync(h, existing, attacker, [team], s.Code);
        Assert.Equal(HttpStatusCode.Conflict, wrongKeys.StatusCode);
        var stranger = await h.AddMemberAsync(team);
        await h.LoginAsync(stranger);
        Assert.Equal(HttpStatusCode.Forbidden, (await RegisterAsync(h, stranger, new TestDevice(existing.MemberId), [team], member: existing.MemberId)).StatusCode);
        var ok = await RegisterAsync(h, existing, n, [team], s.Code);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await RegisterAsync(h, existing, n, [team])).StatusCode); // deviceId consumed forever

        // The paired device says hello at once and fetches its certificate, issuer key and grant.
        await h.LoginAsync(n);
        var rec = (await h.Client(n).GetFromJsonAsync<DeviceRecord>($"/v1/devices/{n.DeviceId}/certificate"))!;
        Assert.Equal(existing.DeviceId, rec.IssuerDeviceId);
        Assert.Equal(existing.IdentityKey, rec.IssuerIdentityKey);
        Assert.True(OmbraPublic.VerifyEnvelope(rec.GrantEnvelope, rec.GrantSignature, rec.IssuerIdentityKey));
        Assert.Equal(team, DeviceGrantBody.Parse(Envelope.Decode(rec.GrantEnvelope).BodyMap()).TeamIds.Single());
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client(stranger).GetAsync($"/v1/devices/{n.DeviceId}/certificate")).StatusCode);
    }

    private static async Task<WebSocket> StreamAsync(ServerHarness h, TestDevice d)
    {
        var ws = await h.Factory.Server.CreateWebSocketClient().ConnectAsync(new Uri(h.Factory.Server.BaseAddress, "v1/stream"), CancellationToken.None);
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new StreamAuth("auth", d.AccessToken!), JsonDefaults.Options), WebSocketMessageType.Text, true, CancellationToken.None);
        var buf = new byte[8192];
        var r = await ws.ReceiveAsync(buf, CancellationToken.None);
        Assert.Equal(StreamEventTypes.Ready, JsonSerializer.Deserialize<StreamEvent>(buf.AsSpan(0, r.Count), JsonDefaults.Options)!.Type);
        return ws;
    }

    [Fact]
    public async Task Revocation_closes_the_socket_within_a_second_and_cascades_by_registration_time()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var root = await h.AddMemberAsync(team);
        await h.LoginAsync(root);
        var lost = new TestDevice(root.MemberId);
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(h, root, lost, [team])).StatusCode);
        await h.ExecAsync("INSERT INTO roster_devices (team_id, device_id, member_id, identity_key_hash, mls_key_hash, cert_hash, added_in_version) " +
                          "VALUES (@t, @d, @m, sha256(@ik), sha256(@mk), sha256(@ik), 1)",
            ("t", team), ("d", lost.DeviceId), ("m", root.MemberId), ("ik", lost.IdentityKey), ("mk", lost.MlsSignatureKey));
        await h.LoginAsync(lost);
        var before = new TestDevice(root.MemberId);
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(h, lost, before, [team])).StatusCode);
        h.Clock.Advance(TimeSpan.FromHours(2));
        await h.LoginAsync(lost); // access tokens live 15 min
        await h.LoginAsync(root);
        var compromisedSince = h.Clock.GetUtcNow().AddMinutes(-30);
        var after = new TestDevice(root.MemberId);
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(h, lost, after, [team])).StatusCode);
        await h.LoginAsync(after);
        var afterChild = new TestDevice(root.MemberId);
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(h, after, afterChild, [team])).StatusCode);

        var ws = await StreamAsync(h, lost);
        var stranger = await h.AddMemberAsync(team);
        await h.LoginAsync(stranger);
        var notMine = await h.SendEnvelopeAsync(stranger, HttpMethod.Post, "/v1/devices/revoke", EnvelopeKinds.DeviceRevoke, Ids.ToBytes(stranger.MemberId),
            new DeviceRevokeBody(lost.DeviceId, 0).ToCbor());
        Assert.Equal(HttpStatusCode.Forbidden, notMine.StatusCode);

        var sw = Stopwatch.StartNew();
        var r = await h.SendEnvelopeAsync(root, HttpMethod.Post, "/v1/devices/revoke", EnvelopeKinds.DeviceRevoke, Ids.ToBytes(root.MemberId),
            new DeviceRevokeBody(lost.DeviceId, (ulong)compromisedSince.ToUnixTimeSeconds()).ToCbor());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            var buf = new byte[8192];
            WebSocketReceiveResult got;
            do got = await ws.ReceiveAsync(buf, cts.Token); while (got.MessageType != WebSocketMessageType.Close);
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"socket closed after {sw.Elapsed}");

        var revoked = (await r.Content.ReadFromJsonAsync<DeviceRevokeResponse>())!.Revoked.ToHashSet();
        Assert.Equal(new HashSet<Guid> { lost.DeviceId, after.DeviceId, afterChild.DeviceId }, revoked);
        Assert.Equal(Reasons.Revoked, await ServerHarness.ReasonAsync(await h.HelloAsync(lost)));
        Assert.Equal(Reasons.Revoked, await ServerHarness.ReasonAsync(await h.HelloAsync(afterChild)));
        Assert.Equal(HttpStatusCode.OK, (await h.HelloAsync(before)).StatusCode); // issued before compromisedSince
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.Client(lost).GetAsync("/v1/sync")).StatusCode);
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM blocks WHERE team_id = @t AND device_id = @d", ("t", team), ("d", lost.DeviceId)));
    }

    /// <summary>
    /// S2-CERTIFICATE-REVOKE-RACE: a registration whose issuer passed authentication before a concurrent revocation
    /// committed must not create an unrevoked child afterwards (the interleaving where the cascade was computed first).
    /// </summary>
    [Fact]
    public async Task Registration_authenticated_before_a_revocation_is_refused_once_the_issuer_is_revoked()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        var team = await h.SeedTeamAsync();
        var root = await h.AddMemberAsync(team);
        await h.LoginAsync(root);
        var lost = new TestDevice(root.MemberId);
        Assert.Equal(HttpStatusCode.OK, (await RegisterAsync(h, root, lost, [team])).StatusCode);
        var authenticatedLost = new DeviceIdentity(lost.DeviceId, lost.MemberId, lost.IdentityKey); // DeviceAuth passed

        var r = await h.SendEnvelopeAsync(root, HttpMethod.Post, "/v1/devices/revoke", EnvelopeKinds.DeviceRevoke, Ids.ToBytes(root.MemberId),
            new DeviceRevokeBody(lost.DeviceId, 0).ToCbor());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var child = new TestDevice(root.MemberId);
        var cert = child.Certificate(h.ServerId, root.MemberId, Now(h), lost);
        var (grant, sig) = lost.Envelope(EnvelopeKinds.DeviceGrant, Ids.ToBytes(child.DeviceId), new DeviceGrantBody([team]).ToCbor(), h.Clock.GetUtcNow());
        var e = await Assert.ThrowsAsync<ApiException>(() => h.Service<Devices.DeviceService>().RegisterAsync(authenticatedLost,
            new DeviceCertificateRequest(cert, "gui", grant, Envelopes.FromBase64Url(sig), null), h.Service<Envelopes>()));
        Assert.Equal(Reasons.Revoked, e.Reason);
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM devices WHERE device_id = @d", ("d", child.DeviceId)));
    }

    // ---------------- heartbeat / ownership / RemovalNotice with real ombra-core clients ----------------

    private static HttpClient Client(ServerHarness h, CoreDevice d)
    {
        var c = h.Client();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", d.AccessToken);
        return c;
    }

    private static async Task<HttpResponseMessage> PostBytesAsync(ServerHarness h, CoreDevice d, string url, byte[] bytes)
    {
        using var c = Client(h, d);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return await c.PostAsync(url, content);
    }

    private static Task<HttpResponseMessage> PostHexAsync(ServerHarness h, CoreDevice d, string url, string hex) => PostBytesAsync(h, d, url, Convert.FromHexString(hex));

    private static async Task<HttpResponseMessage> CoreHelloAsync(ServerHarness h, CoreDevice d)
    {
        using var c = h.Client();
        var ch = (await (await c.PostAsJsonAsync("/v1/session/challenge", new ChallengeRequest(d.DeviceId))).Content.ReadFromJsonAsync<ChallengeResponse>())!;
        var sig = d.Call("hello.sign", new() { ["serverId"] = Hex(ch.ServerId), ["nonce"] = Hex(ch.Nonce), ["time"] = Now(h) })["signature"]!.GetValue<string>();
        return await c.PostAsJsonAsync("/v1/session/hello", new HelloRequest(Protocol.Current, d.DeviceId, ch.Nonce, Now(h), Convert.FromHexString(sig)));
    }

    private sealed record World(ServerHarness H, string TeamId, CoreDevice Owner, CoreDevice Admin, CoreDevice Member) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            Admin.Dispose();
            Member.Dispose();
            await H.DisposeAsync();
        }
    }

    private async Task<World> WorldAsync()
    {
        var h = await ServerHarness.StartAsync(pg);
        var (o, a, m) = (new CoreDevice(), new CoreDevice(), new CoreDevice());
        await o.RegisterAsync(h, serverOwner: true);
        await a.RegisterAsync(h);
        await m.RegisterAsync(h);
        var sid = Hex(h.ServerId);
        var created = o.Call("team.create", new() { ["serverId"] = sid, ["recoveryKey"] = Hex(RandomNumberGenerator.GetBytes(32)), ["issuedAt"] = Now(h) });
        var teamId = created["teamId"]!.GetValue<string>();
        var v1 = created["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostHexAsync(h, o, "/v1/teams", v1)).StatusCode);
        var v2 = o.Call("roster.update", new()
        {
            ["teamId"] = teamId, ["apply"] = true, ["now"] = Now(h),
            ["addMembers"] = new JsonArray(
                new JsonObject { ["memberId"] = Hex(a.MemberId), ["role"] = "admin", ["certs"] = new JsonArray(a.Certificate(h.ServerId, Now(h))) },
                new JsonObject { ["memberId"] = Hex(m.MemberId), ["certs"] = new JsonArray(m.Certificate(h.ServerId, Now(h))) }),
        })["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostHexAsync(h, o, $"/v1/teams/{teamId}/roster", v2)).StatusCode);
        foreach (var d in new[] { a, m })
            d.Call("team.import", new() { ["teamId"] = teamId, ["updates"] = new JsonArray(v1, v2), ["serverId"] = sid, ["now"] = Now(h) });
        return new World(h, teamId, o, a, m);
    }

    [Fact]
    public async Task Heartbeat_is_stored_only_from_an_owner_or_admin_signer_with_a_known_roster_and_fresh_time()
    {
        await using var w = await WorldAsync();
        var h = w.H;
        var hb = Convert.FromHexString(w.Admin.Call("heartbeat.create", new() { ["teamId"] = w.TeamId, ["time"] = Now(h) })["heartbeat"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await PostBytesAsync(h, w.Owner, $"/v1/teams/{w.TeamId}/heartbeat", hb)).StatusCode); // signer ≠ token device
        Assert.Equal(HttpStatusCode.NoContent, (await PostBytesAsync(h, w.Admin, $"/v1/teams/{w.TeamId}/heartbeat", hb)).StatusCode);
        var tampered = (byte[])hb.Clone();
        tampered[^1] ^= 1;
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostBytesAsync(h, w.Admin, $"/v1/teams/{w.TeamId}/heartbeat", tampered)).StatusCode);

        var list = (await Client(h, w.Member).GetFromJsonAsync<List<HeartbeatRecord>>($"/v1/teams/{w.TeamId}/heartbeats"))!;
        Assert.Equal(hb, list.Single().Heartbeat);
        // A heartbeat the client accepts: same chain, signer is an admin (no groups yet → nothing verifiable, not counted).
        var verdict = w.Member.Call("heartbeat.accept", new() { ["heartbeat"] = Hex(list[0].Heartbeat) });
        Assert.False(verdict["counted"]!.GetValue<bool>());

        h.Clock.Advance(TimeSpan.FromHours(1));
        await w.Admin.LoginAsync(h);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostBytesAsync(h, w.Admin, $"/v1/teams/{w.TeamId}/heartbeat", hb)).StatusCode); // stale time
    }

    [Fact]
    public async Task Ownership_offer_by_the_owner_is_accepted_only_by_the_recipient_with_both_signatures()
    {
        await using var w = await WorldAsync();
        var h = w.H;
        var expires = Now(h) + 3600;
        var made = w.Owner.Call("ownership.offer", new() { ["teamId"] = w.TeamId, ["to"] = Hex(w.Admin.MemberId), ["expiresAt"] = expires, ["newRoleOfFrom"] = "admin" });
        var offer = made["offer"]!.Deserialize<OwnershipOffer>(JsonDefaults.Options)!;
        var offerSig = made["offerSig"]!.Deserialize<OwnershipSig>(JsonDefaults.Options)!;

        var byMember = w.Member.Call("ownership.offer", new() { ["teamId"] = w.TeamId, ["to"] = Hex(w.Admin.MemberId), ["expiresAt"] = expires });
        using (var cm = Client(h, w.Member))
        {
            var r = await cm.PostAsJsonAsync($"/v1/teams/{w.TeamId}/ownership/offer", new OwnershipOfferRequest(
                byMember["offer"]!.Deserialize<OwnershipOffer>(JsonDefaults.Options)!, byMember["offerSig"]!.Deserialize<OwnershipSig>(JsonDefaults.Options)!));
            Assert.Equal(Reasons.NotOwner, await ServerHarness.ReasonAsync(r)); // only an owner device offers
        }
        using (var co = Client(h, w.Owner))
        {
            var forged = await co.PostAsJsonAsync($"/v1/teams/{w.TeamId}/ownership/offer", new OwnershipOfferRequest(offer with { To = Hex(w.Member.MemberId) }, offerSig));
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode); // signature covers the recipient
            Assert.Equal(HttpStatusCode.OK, (await co.PostAsJsonAsync($"/v1/teams/{w.TeamId}/ownership/offer", new OwnershipOfferRequest(offer, offerSig))).StatusCode);
        }
        var open = (await Client(h, w.Admin).GetFromJsonAsync<OwnershipOfferResponse>($"/v1/teams/{w.TeamId}/ownership"))!;
        Assert.Equal(offer, open.Offer);

        var acceptSig = w.Admin.Call("ownership.accept", new() { ["offer"] = made["offer"]!.DeepClone() })["acceptSig"]!;
        var ownership = new JsonObject { ["offer"] = made["offer"]!.DeepClone(), ["offerSig"] = made["offerSig"]!.DeepClone(), ["acceptSig"] = acceptSig.DeepClone() };
        var update = w.Admin.Call("roster.update", new() { ["teamId"] = w.TeamId, ["now"] = Now(h), ["ownership"] = ownership })["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostHexAsync(h, w.Member, $"/v1/teams/{w.TeamId}/ownership/accept", update)).StatusCode);
        var accepted = await PostHexAsync(h, w.Admin, $"/v1/teams/{w.TeamId}/ownership/accept", update);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("owner", await h.ScalarAsync<string>("SELECT role FROM roster_members WHERE team_id = @t AND member_id = @m",
            ("t", Convert.FromHexString(w.TeamId)), ("m", w.Admin.MemberId)));
        Assert.Equal(HttpStatusCode.NotFound, (await Client(h, w.Admin).GetAsync($"/v1/teams/{w.TeamId}/ownership")).StatusCode);
    }

    [Fact]
    public async Task Removal_notice_is_kept_for_a_removed_member_and_served_with_403_removed_on_hello()
    {
        await using var w = await WorldAsync();
        var h = w.H;
        var early = Convert.FromHexString(w.Admin.Call("removal.create", new() { ["teamId"] = w.TeamId, ["memberId"] = Hex(w.Member.MemberId), ["time"] = Now(h) })["notice"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await PostBytesAsync(h, w.Admin, $"/v1/teams/{w.TeamId}/removal-notices", early)).StatusCode); // not removed yet

        var v3 = w.Admin.Call("roster.update", new() { ["teamId"] = w.TeamId, ["apply"] = true, ["now"] = Now(h), ["removeMembers"] = new JsonArray(Hex(w.Member.MemberId)) })["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostHexAsync(h, w.Admin, $"/v1/teams/{w.TeamId}/roster", v3)).StatusCode);
        var notice = Convert.FromHexString(w.Admin.Call("removal.create", new() { ["teamId"] = w.TeamId, ["memberId"] = Hex(w.Member.MemberId), ["time"] = Now(h) })["notice"]!.GetValue<string>());
        var forged = (byte[])notice.Clone();
        forged[^3] ^= 1;
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostBytesAsync(h, w.Admin, $"/v1/teams/{w.TeamId}/removal-notices", forged)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostBytesAsync(h, w.Admin, $"/v1/teams/{w.TeamId}/removal-notices", notice)).StatusCode);

        var hello = await CoreHelloAsync(h, w.Member);
        Assert.Equal(HttpStatusCode.Forbidden, hello.StatusCode);
        var err = (await hello.Content.ReadFromJsonAsync<ErrorResponse>(JsonDefaults.Options))!;
        Assert.Equal(Reasons.Removed, err.Reason);
        Assert.Equal(notice, err.RemovalNotices!.Single());
        // The removed client verifies it against its own chain (after applying v3) and learns it is about itself.
        w.Member.Call("roster.apply", new() { ["teamId"] = w.TeamId, ["update"] = v3, ["now"] = Now(h) });
        var verdict = w.Member.Call("removal.verify", new() { ["notice"] = Hex(err.RemovalNotices![0]) });
        Assert.True(verdict["accepted"]!.GetValue<bool>());
        Assert.True(verdict["aboutMe"]!.GetValue<bool>());
    }
}
