using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Core.Interop;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;
using Ombra.Server.Streaming;

namespace Ombra.Server.Teams;

/// <summary>
/// Signed team signals the server stores and relays but cannot forge:
/// <list type="bullet">
/// <item>heartbeats of owner/admin devices (§4.1, SD-06) — the server checks routing (team, signer = token device,
/// owner/admin role, roster version/hash in its chain, time window) and the signature; epochProofs are checked only by
/// clients, which also decide what counts;</item>
/// <item>OwnershipOffer (§4.3) — one open offer per team, signed by an owner device for the next roster version; the
/// accept is the RosterUpdate with both signatures, appended CAS-style and bound to that offer;</item>
/// <item>RemovalNotice (§5.1 п.6) — admin-signed, kept per (team, member) and served to the removed member's devices
/// with their <c>403 removed</c>.</item>
/// </list>
/// </summary>
public sealed class TeamSignalsService(NpgsqlDataSource db, TimeProvider clock, StreamHub hub, RosterService roster, IOptions<OmbraOptions> options)
{
    public const int MaxHeartbeatBytes = 64 * 1024;
    private static readonly TimeSpan HeartbeatServeWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaxOfferLifetime = TimeSpan.FromDays(30);
    private readonly LimitsOptions _limits = options.Value.Limits;

    private static JsonNode Inspect(string op, string key, byte[] bytes)
    {
        try { return OmbraPublic.Call(op, new JsonObject { [key] = OmbraPublic.Hex(bytes) }); }
        catch (OmbraException e) { throw ApiException.Bad($"{e.Code}: {e.Detail}"); }
    }

    private static byte[] H(JsonNode n) => Convert.FromHexString(n.GetValue<string>());

    private async Task NotifyTeamAsync(byte[] teamId, StreamEvent evt, Guid? except = null, Guid? onlyMember = null)
    {
        await using var conn = await db.OpenConnectionAsync();
        var devices = await conn.ListAsync("SELECT device_id FROM team_device_access WHERE team_id = @t AND (@m::uuid IS NULL OR member_id = @m)", null,
            r => r.GetGuid(0), ("t", teamId), ("m", onlyMember));
        hub.Send(devices.Where(d => d != except).ToList(), evt);
    }

    // ---------------- heartbeat ----------------

    public async Task PutHeartbeatAsync(DeviceIdentity device, byte[] teamId, byte[] heartbeat)
    {
        var hb = Inspect("heartbeat.inspect", "heartbeat", heartbeat);
        if (!H(hb["teamId"]!).AsSpan().SequenceEqual(teamId)) throw ApiException.Bad("heartbeat of another team");
        if (Guid.ParseExact(hb["signerDeviceId"]!.GetValue<string>(), "N") != device.DeviceId) throw ApiException.Bad("heartbeat signer must be the requesting device");
        if (!OmbraPublic.Ed25519Verify(device.IdentityKey, H(hb["signed"]!), H(hb["signature"]!))) throw new ApiException(401, Reasons.BadSignature, "heartbeat signature");
        var now = clock.GetUtcNow();
        var time = DateTimeOffset.FromUnixTimeSeconds((long)hb["time"]!.GetValue<ulong>());
        if ((time - now).Duration() > TimeSpan.FromSeconds(_limits.ClockSkewSeconds)) throw ApiException.Bad("heartbeat time outside the accepted window");
        var version = (long)hb["rosterVersion"]!.GetValue<ulong>();
        await using (var conn = await db.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            var access = await AccessService.RequireActiveAsync(conn, tx, teamId, device.DeviceId);
            if (!access.IsAdmin) throw ApiException.Forbidden(Reasons.NotAdmin, "heartbeats are published by owner/admin devices");
            var hash = await conn.ScalarAsync<byte[]>("SELECT hash FROM roster_versions WHERE team_id = @t AND version = @v", tx, ("t", teamId), ("v", version));
            if (hash is null || !hash.AsSpan().SequenceEqual(H(hb["rosterHash"]!)))
                throw ApiException.Conflict(Reasons.RosterConflict, "heartbeat roster version/hash is not in the chain");
            await conn.ExecAsync(
                "INSERT INTO heartbeats (team_id, device_id, roster_version, time, heartbeat, received_at) VALUES (@t, @d, @v, @time, @hb, @now) " +
                "ON CONFLICT (team_id, device_id) DO UPDATE SET roster_version = excluded.roster_version, time = excluded.time, heartbeat = excluded.heartbeat, " +
                "received_at = excluded.received_at WHERE heartbeats.time <= excluded.time", tx,
                ("t", teamId), ("d", device.DeviceId), ("v", version), ("time", time), ("hb", heartbeat), ("now", now));
            await tx.CommitAsync();
        }
        await NotifyTeamAsync(teamId, new StreamEvent(StreamEventTypes.Heartbeat, TeamId: Ids.Hex(teamId), DeviceId: device.DeviceId), except: device.DeviceId);
    }

    public async Task<List<HeartbeatRecord>> HeartbeatsAsync(DeviceIdentity device, byte[] teamId)
    {
        await using var conn = await db.OpenConnectionAsync();
        await AccessService.RequireActiveAsync(conn, null, teamId, device.DeviceId);
        return await conn.ListAsync(
            "SELECT h.device_id, h.roster_version, h.time, h.heartbeat FROM heartbeats h " +
            "JOIN team_device_access a ON a.team_id = h.team_id AND a.device_id = h.device_id " +
            "WHERE h.team_id = @t AND h.time > @since ORDER BY h.time DESC", null,
            r => new HeartbeatRecord(r.GetGuid(0), r.GetInt64(1), r.Time(2), r.Bytes(3)), ("t", teamId), ("since", clock.GetUtcNow() - HeartbeatServeWindow));
    }

    // ---------------- ownership ----------------

    public async Task<OwnershipOfferResponse> OfferAsync(DeviceIdentity device, byte[] teamId, OwnershipOfferRequest req)
    {
        var o = req.Offer;
        var now = clock.GetUtcNow();
        if (o.TeamId != Ids.Hex(teamId)) throw ApiException.Bad("offer of another team");
        if (!Guid.TryParseExact(o.From, "N", out var from) || from != device.MemberId) throw ApiException.Bad("offer.from must be the requesting member");
        if (!Guid.TryParseExact(o.To, "N", out var to) || to == from) throw ApiException.Bad("bad offer.to");
        if (o.NewRoleOfFrom is not ("admin" or "member")) throw ApiException.Bad("newRoleOfFrom must be admin|member");
        var expires = DateTimeOffset.FromUnixTimeSeconds((long)Math.Min(o.ExpiresAt, (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds()));
        if (expires <= now || expires > now + MaxOfferLifetime) throw ApiException.Bad("expiresAt must be within 30 days");
        if (!Guid.TryParseExact(req.OfferSig.DeviceId, "N", out var sigDevice) || sigDevice != device.DeviceId) throw ApiException.Bad("offerSig must be by the requesting device");
        var offerJson = JsonSerializer.SerializeToNode(o, JsonDefaults.Options)!;
        byte[] signed;
        try { signed = H(OmbraPublic.Call("ownership.bytes", new JsonObject { ["offer"] = offerJson })["offer"]!); }
        catch (OmbraException e) { throw ApiException.Bad($"{e.Code}: {e.Detail}"); }
        byte[] sig;
        try { sig = Convert.FromHexString(req.OfferSig.Sig); }
        catch (FormatException) { throw ApiException.Bad("offerSig.sig must be hex"); }
        if (!OmbraPublic.Ed25519Verify(device.IdentityKey, signed, sig)) throw new ApiException(401, Reasons.BadSignature, "offer signature");

        var id = Guid.CreateVersion7();
        await using (var conn = await db.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            var team = await conn.SingleAsync("SELECT roster_version, roster_hash, frozen_reason FROM teams WHERE team_id = @t FOR NO KEY UPDATE", tx,
                r => new { Version = r.GetInt64(0), Hash = r.Bytes(1), Frozen = r.StringOrNull(2) }, ("t", teamId)) ?? throw ApiException.NotFound("team");
            if (team.Frozen is not null) throw ApiException.Forbidden(Reasons.Frozen, team.Frozen);
            var access = await AccessService.RequireActiveAsync(conn, tx, teamId, device.DeviceId);
            if (access.Role != "owner") throw ApiException.Forbidden(Reasons.NotOwner);
            if ((long)o.RosterVersion != team.Version + 1 || o.PrevHash != Ids.Hex(team.Hash))
                throw ApiException.Conflict(Reasons.RosterConflict, $"the offer must be for version {team.Version + 1} on the current head");
            var toActive = await conn.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM team_device_access WHERE team_id = @t AND member_id = @m)", tx, ("t", teamId), ("m", to));
            if (!toActive) throw ApiException.Bad("the recipient is not an active member");
            await conn.ExecAsync("UPDATE ownership_offers SET closed_at = @now, accepted = false WHERE team_id = @t AND closed_at IS NULL", tx, ("now", now), ("t", teamId));
            await conn.ExecAsync(
                "INSERT INTO ownership_offers (offer_id, team_id, from_member, to_member, roster_version, offer, offer_sig, expires_at, created_at) " +
                "VALUES (@id, @t, @f, @to, @v, @o::jsonb, @s::jsonb, @exp, @now)", tx,
                ("id", id), ("t", teamId), ("f", from), ("to", to), ("v", (long)o.RosterVersion), ("o", JsonSerializer.Serialize(o, JsonDefaults.Options)),
                ("s", JsonSerializer.Serialize(req.OfferSig, JsonDefaults.Options)), ("exp", expires), ("now", now));
            await conn.ExecAsync("INSERT INTO audit (audit_id, at, actor, action, team_id, subject) VALUES (@a, @now, @actor, 'ownership.offer', @t, @s)", tx,
                ("a", Guid.CreateVersion7()), ("now", now), ("actor", "device:" + device.DeviceId), ("t", teamId), ("s", to.ToString()));
            await tx.CommitAsync();
        }
        await NotifyTeamAsync(teamId, new StreamEvent(StreamEventTypes.Ownership, TeamId: Ids.Hex(teamId)), onlyMember: to);
        return new OwnershipOfferResponse(id, o, req.OfferSig);
    }

    private static Task<OwnershipOfferResponse?> OpenOfferAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, byte[] teamId, DateTimeOffset now, bool forUpdate = false) =>
        conn.SingleAsync("SELECT offer_id, offer::text, offer_sig::text FROM ownership_offers WHERE team_id = @t AND closed_at IS NULL AND expires_at > @now" +
                         (forUpdate ? " FOR UPDATE" : ""), tx,
            r => new OwnershipOfferResponse(r.GetGuid(0), JsonSerializer.Deserialize<OwnershipOffer>(r.GetString(1), JsonDefaults.Options)!,
                JsonSerializer.Deserialize<OwnershipSig>(r.GetString(2), JsonDefaults.Options)!), ("t", teamId), ("now", now));

    public async Task<OwnershipOfferResponse> OpenOfferAsync(DeviceIdentity device, byte[] teamId)
    {
        await using var conn = await db.OpenConnectionAsync();
        await AccessService.RequireActiveAsync(conn, null, teamId, device.DeviceId);
        return await OpenOfferAsync(conn, null, teamId, clock.GetUtcNow()) ?? throw ApiException.NotFound("ownership offer");
    }

    /// <summary>The recipient publishes the RosterUpdate carrying offer + both signatures; it must be exactly the offered version and owner.</summary>
    public Task<RosterService.AppendResponse> AcceptAsync(DeviceIdentity device, byte[] teamId, byte[] update) =>
        roster.AppendAsync(device, teamId, update, async (conn, tx, version, ownerHex) =>
        {
            var offer = await OpenOfferAsync(conn, tx, teamId, clock.GetUtcNow(), forUpdate: true) ?? throw ApiException.NotFound("ownership offer");
            if (offer.Offer.To != Ids.Hex(Ids.ToBytes(device.MemberId))) throw ApiException.Forbidden(Reasons.Forbidden, "only the recipient accepts the offer");
            if ((long)offer.Offer.RosterVersion != version || ownerHex != offer.Offer.To)
                throw ApiException.Conflict(Reasons.RosterConflict, "the update does not carry out the open offer");
            await conn.ExecAsync("UPDATE ownership_offers SET closed_at = @now, accepted = true WHERE offer_id = @id", tx, ("now", clock.GetUtcNow()), ("id", offer.OfferId));
        });

    // ---------------- RemovalNotice ----------------

    public async Task PutRemovalNoticeAsync(DeviceIdentity device, byte[] teamId, byte[] notice)
    {
        var n = Inspect("removal.inspect", "notice", notice);
        if (!H(n["teamId"]!).AsSpan().SequenceEqual(teamId)) throw ApiException.Bad("notice of another team");
        if (Guid.ParseExact(n["signerDeviceId"]!.GetValue<string>(), "N") != device.DeviceId) throw ApiException.Bad("notice signer must be the requesting device");
        if (!OmbraPublic.Ed25519Verify(device.IdentityKey, H(n["signed"]!), H(n["signature"]!))) throw new ApiException(401, Reasons.BadSignature, "notice signature");
        var member = Guid.ParseExact(n["memberId"]!.GetValue<string>(), "N");
        var version = (long)n["rosterVersion"]!.GetValue<ulong>();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var access = await AccessService.RequireActiveAsync(conn, tx, teamId, device.DeviceId);
        if (!access.IsAdmin) throw ApiException.Forbidden(Reasons.NotAdmin);
        var removedIn = await conn.ScalarAsync<long?>("SELECT removed_in_version FROM roster_members WHERE team_id = @t AND member_id = @m", tx, ("t", teamId), ("m", member));
        if (removedIn is null || version < removedIn) throw ApiException.Conflict(Reasons.RosterConflict, "the member is not removed in that roster version");
        await conn.ExecAsync(
            "INSERT INTO removal_notices (team_id, member_id, roster_version, notice, created_at) VALUES (@t, @m, @v, @n, @now) " +
            "ON CONFLICT (team_id, member_id) DO NOTHING", tx,
            ("t", teamId), ("m", member), ("v", version), ("n", notice), ("now", clock.GetUtcNow()));
        await tx.CommitAsync();
    }

    public static Task<List<byte[]>> NoticesForMemberAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid member, byte[]? teamId = null) =>
        conn.ListAsync("SELECT notice FROM removal_notices WHERE member_id = @m AND (@t::bytea IS NULL OR team_id = @t) ORDER BY created_at", tx,
            r => r.Bytes(0), ("m", member), ("t", teamId));
}
