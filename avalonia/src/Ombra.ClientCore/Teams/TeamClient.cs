using System.Text.Json;
using System.Text.Json.Nodes;
using Ombra.Core.Interop;
using Ombra.Server.Contracts;

namespace Ombra.ClientCore.Teams;

public sealed record TeamCreated(string TeamId, byte[] RecoverySecretKey, string TeamGroupId)
{
    /// <summary>D-3: the owner recovery key, printable (52 base32 characters in groups of 4); never stored by Ombra.</summary>
    public string RecoveryKeyText => Base32Groups(RecoverySecretKey);

    internal static string Base32Groups(byte[] b)
    {
        const string A = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var sb = new System.Text.StringBuilder();
        int bits = 0, value = 0, n = 0;
        foreach (var x in b)
        {
            value = (value << 8) | x;
            bits += 8;
            while (bits >= 5)
            {
                if (n > 0 && n % 4 == 0) sb.Append('-');
                sb.Append(A[(value >> (bits - 5)) & 31]);
                bits -= 5;
                n++;
            }
        }
        if (bits > 0) { if (n % 4 == 0) sb.Append('-'); sb.Append(A[(value << (5 - bits)) & 31]); }
        return sb.ToString();
    }
}

public sealed record JoinPending(Guid RequestId, string TeamId, Guid MemberId);

/// <summary>A JoinRequest as the admin device judges it (§3.4 п.4–5).</summary>
public sealed record JoinReview(Guid RequestId, Guid InviteId, string TeamId, Guid MemberId, IReadOnlyList<JoinDevice> Devices, bool Valid, bool AutoAccept,
    string? Role, string? Problem);

public sealed record RosterSyncResult(string TeamId, ulong Version, bool Frozen, bool RemovedSelf, int Applied);

public sealed record SyncResult(int Processed, int Welcomes, IReadOnlyList<string> RemovedFromTeams, IReadOnlyList<string> Rejected);

/// <summary>R2-08 evidence and what each choice undoes (§7: shown and confirmed before signing).</summary>
public sealed record EquivocationReview(string TeamId, ulong Version, byte[] Known, byte[] Conflicting, IReadOnlyList<string> KnownSigners,
    IReadOnlyList<string> ConflictingSigners, IReadOnlyList<JsonObject> UndoneIfSwitch, IReadOnlyList<JsonObject> UndoneIfKeep);

public sealed record TeamConfirmation(bool Confirmed, bool ByOwner, int AdminVotes, int Required);

/// <summary>
/// Team operations of one device on one server (§3.4, §4, §5): genesis, invites, join and approval, roster sync and
/// validation, removal and revocation with cascade, RemovalNotice and local wipe (D-5), heartbeats, ownership
/// transfer, equivocation resolution. Every rule is checked by ombra-core in <see cref="TeamsDevice"/>; this class
/// moves verified bytes between it and <see cref="TeamsServer"/>.
/// </summary>
public sealed partial class TeamClient(TeamsServer server, TeamsDevice device)
{
    public const string RosterContentType = "application/ombra-roster+cbor";
    public static readonly TimeSpan ConfirmationWindow = TimeSpan.FromHours(72);

    public TeamsServer Server { get; } = server;
    public TeamsDevice Device { get; } = device;

    /// <summary>D-5: a team's local data was erased (removed member, verified notice or 403 removed).</summary>
    public event Action<string>? TeamWiped;

    private ulong Now => Server.Now;
    private DeviceIdentity Me => Device.Identity ?? throw new InvalidOperationException("device not initialised");
    private static string Hex(ReadOnlySpan<byte> b) => TeamsHex.Of(b);
    private static string Hex(Guid g) => TeamsHex.Of(g);
    private static byte[] B(JsonNode n) => TeamsHex.Bytes(n);
    private static string S(JsonNode n) => n.GetValue<string>();

    private async Task<string> ServerIdHexAsync(CancellationToken ct) => Hex(await Server.ServerIdAsync(ct));

    public JsonNode Status(string teamId) => Device.Call("team.status", new() { ["teamId"] = teamId, ["now"] = Now, ["window"] = (ulong)ConfirmationWindow.TotalSeconds });

    public JsonNode Head(string teamId) => Device.Call("roster.head", new() { ["teamId"] = teamId });

    public string? TeamGroupId(string teamId) =>
        Device.Call("group.list")["groups"]!.AsArray()
            .Where(g => S(g!["teamId"]!) == teamId && S(g["type"]!) == "team" && !g["closed"]!.GetValue<bool>() && !g["evicted"]!.GetValue<bool>())
            .Select(g => S(g!["groupId"]!)).FirstOrDefault();

    // ---------------- genesis (§2.4, §4.1) ----------------

    /// <summary>
    /// A new team owned by this (ServerOwner) device: genesis signed here with a fresh owner recovery key (only its
    /// public half goes into the roster; the secret is returned once for printing), the team MLS group registered.
    /// In IP mode the current pin is put into the roster.
    /// </summary>
    public async Task<TeamCreated> CreateTeamAsync(CancellationToken ct = default)
    {
        var sid = await ServerIdHexAsync(ct);
        var rk = OmbraPublic.Call("ed25519.keygen");
        var pins = new JsonArray(Server.Endpoint.Pins.Select(p => (JsonNode)p).ToArray());
        var created = Device.Call("team.create", new() { ["serverId"] = sid, ["recoveryKey"] = rk["publicKey"]!.DeepClone(), ["issuedAt"] = Now, ["pins"] = pins });
        var teamId = S(created["teamId"]!);
        await Server.PostBytesAsync<RosterAppend>("v1/teams", B(created["update"]!), RosterContentType, ct);
        var gid = await CreateTeamGroupAsync(teamId, ct);
        await EnsureAdminGroupAsync(teamId, ct); // R2-05: the channel for invite secrets
        return new TeamCreated(teamId, B(rk["secretKey"]!), gid);
    }

    private async Task<string> CreateTeamGroupAsync(string teamId, CancellationToken ct)
    {
        var gid = S(Device.Call("group.create", new() { ["descriptor"] = new JsonObject { ["type"] = "team", ["teamId"] = teamId, ["generation"] = 0 } })["groupId"]!);
        var gi = B(Device.Call("group.public_info", new() { ["groupId"] = gid })["groupInfo"]!);
        await Server.PostBytesAsync<GroupResponse>($"v1/teams/{teamId}/groups", gi, Headers.MlsContentType, ct);
        return gid;
    }

    // ---------------- invites (§3.4) ----------------

    private async Task<long> AccessRevisionAsync(string teamId, bool refresh, CancellationToken ct)
    {
        if (refresh || Server.LastHello is null || Server.LastHello.Teams.All(t => t.TeamId != teamId)) await Server.HelloAsync(ct);
        return Server.LastHello!.Teams.FirstOrDefault(t => t.TeamId == teamId)?.AccessRevision
               ?? throw new TeamsServerException(403, Reasons.NotMember, "this device is not in the team");
    }

    /// <summary>Invite link with <c>k</c> only in the fragment; the server gets <c>verifier</c>, <c>k_adm</c> stays on this device.</summary>
    public async Task<InviteLink> CreateInviteAsync(string teamId, string role = "member", TimeSpan? ttl = null, uint maxUses = 1, CancellationToken ct = default)
    {
        var expires = Now + (ulong)(ttl ?? TimeSpan.FromDays(7)).TotalSeconds;
        var inv = Device.Call("invite.create", new() { ["teamId"] = teamId, ["role"] = role, ["maxUses"] = maxUses, ["expiresAt"] = expires });
        var inviteId = TeamsHex.ToGuid(inv["inviteId"]!);
        var head = inv["rosterHead"]!;
        var body = new InviteCreateBody(inviteId, B(inv["signedInvite"]!), head["version"]!.GetValue<ulong>(), B(head["hash"]!), role, expires, maxUses, B(inv["verifier"]!));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var rev = await AccessRevisionAsync(teamId, refresh: attempt > 0, ct);
                await Server.PostEnvelopeAsync<InviteResponse>($"v1/teams/{teamId}/invites", EnvelopeKinds.InviteCreate, Convert.FromHexString(teamId), body.ToCbor(), (ulong)rev, ct: ct);
                break;
            }
            catch (TeamsServerException e) when (e.Reason == Reasons.Revision && attempt == 0) { }
        }
        await ShareInviteKeysAsync(teamId, ct); // R2-05: k_adm to the other admins (admin group only)
        var pin = Server.Endpoint.IpMode ? Server.Endpoint.Pins.First() : null;
        return new InviteLink(Server.Endpoint.ToString(), pin, teamId, head["version"]!.GetValue<ulong>(), S(head["hash"]!), S(inv["inviterFingerprint"]!), inviteId,
            B(inv["k"]!));
    }

    /// <summary>
    /// Invitee (§3.4 п.2–3): verify the Invite and the chain against the link (genesis <c>t</c>, rosterHead <c>h</c>,
    /// inviter fingerprint <c>a</c>) in ombra-core, then send the JoinRequest with <c>proof_srv</c> and <c>bind</c>.
    /// This device must be fresh (its own memberId, not in any team).
    /// </summary>
    public async Task<JoinPending> JoinAsync(InviteLink link, CancellationToken ct = default)
    {
        var sid = await ServerIdHexAsync(ct);
        var invite = await Server.GetAnonymousAsync<InviteResponse>($"v1/invites/{link.InviteId:D}", ct);
        if (invite.TeamId != link.TeamId) throw new TeamsServerException(409, Reasons.RosterConflict, "the server returned another team than the link names");
        Device.Call("team.import", new()
        {
            ["teamId"] = link.TeamId, ["serverId"] = sid, ["now"] = Now,
            ["updates"] = new JsonArray(invite.Chain.OrderBy(c => c.Version).Select(c => (JsonNode)Hex(c.RosterUpdate)).ToArray()),
            ["rosterHead"] = new JsonObject { ["version"] = link.HeadVersion, ["hash"] = link.HeadHash },
            ["inviterFingerprint"] = link.InviterFingerprint, ["signedInvite"] = Hex(invite.SignedInvite),
        });
        var keys = OmbraPublic.Call("invite.keys", new() { ["k"] = Hex(link.Secret), ["inviteId"] = Hex(link.InviteId) });
        var me = Me;
        var kp = B(Device.Call("key_package")["keyPackage"]!);
        var cert = B(Device.Call("device.certificate", new() { ["self"] = true, ["serverId"] = sid, ["issuedAt"] = Now })["cert"]!);
        var bind = B(OmbraPublic.Call("invite.bind", new()
        {
            ["inviteId"] = Hex(link.InviteId), ["memberId"] = Hex(me.MemberId), ["kAdm"] = keys["kAdm"]!.DeepClone(),
            ["devices"] = new JsonArray(new JsonObject
            {
                ["deviceId"] = Hex(me.DeviceId), ["identityKey"] = Hex(me.IdentityKey), ["mlsSignatureKey"] = Hex(me.MlsSignatureKey), ["keyPackage"] = Hex(kp),
            }),
        })["bind"]!);
        var body = new JoinRequestBody(link.InviteId, me.MemberId, [new JoinDevice(me.DeviceId, "gui", me.IdentityKey, me.MlsSignatureKey, kp, cert)], B(keys["kSrv"]!), bind);
        var resp = await Server.PostEnvelopeAsync<JoinResponse>($"v1/invites/{link.InviteId:D}/join", EnvelopeKinds.JoinRequest,
            Convert.FromHexString(link.TeamId), body.ToCbor(), anonymous: true, ct: ct);
        return new JoinPending(resp.RequestId, link.TeamId, me.MemberId);
    }

    /// <summary>Admin: pending JoinRequests with the device's own verdict (envelope signature, bind with k_adm, certificates).</summary>
    public async Task<IReadOnlyList<JoinReview>> PendingJoinsAsync(CancellationToken ct = default)
    {
        var pending = await Server.GetAsync<PendingResponse>("v1/admin/pending", ct);
        var list = new List<JoinReview>();
        foreach (var j in pending.Joins)
        {
            JoinRequestBody body;
            try { body = JoinRequestBody.Parse(Envelope.Decode(j.JoinEnvelope).BodyMap()); }
            catch (WireFormatException e)
            {
                list.Add(new JoinReview(j.RequestId, j.InviteId, j.TeamId, j.MemberId, [], false, false, null, "malformed request: " + e.Message));
                continue;
            }
            if (!OmbraPublic.VerifyEnvelope(j.JoinEnvelope, j.JoinSignature, body.Devices[0].IdentityKey) || body.MemberId != j.MemberId || body.InviteId != j.InviteId)
            {
                list.Add(new JoinReview(j.RequestId, j.InviteId, j.TeamId, j.MemberId, body.Devices, false, false, null, "request signature or routing does not match"));
                continue;
            }
            var v = Device.Call("invite.verify_join", new()
            {
                ["teamId"] = j.TeamId, ["inviteId"] = Hex(j.InviteId), ["memberId"] = Hex(j.MemberId), ["bind"] = Hex(body.Bind), ["now"] = Now,
                ["devices"] = new JsonArray(body.Devices.Select(d => (JsonNode)new JsonObject
                {
                    ["deviceId"] = Hex(d.DeviceId), ["identityKey"] = Hex(d.IdentityKey), ["mlsSignatureKey"] = Hex(d.MlsSignatureKey),
                    ["keyPackage"] = Hex(d.KeyPackage), ["certificate"] = Hex(d.Certificate),
                }).ToArray()),
            });
            list.Add(new JoinReview(j.RequestId, j.InviteId, j.TeamId, j.MemberId, body.Devices, v["valid"]!.GetValue<bool>(), v["autoAccept"]!.GetValue<bool>(),
                v["role"]?.GetValue<string>(), v["reason"]?.GetValue<string>()));
        }
        return list;
    }

    /// <summary>
    /// Admin: add the member (RosterUpdate) and its devices to the team group (Add + Welcome). Refused when <c>bind</c>
    /// does not verify ("request changed on the way"); a multi-use invite needs <paramref name="safetyNumberConfirmed"/>.
    /// </summary>
    public async Task ApproveJoinAsync(JoinReview join, bool safetyNumberConfirmed = false, CancellationToken ct = default)
    {
        if (!join.Valid) throw new InvalidOperationException("join request rejected: " + join.Problem);
        if (!join.AutoAccept && !safetyNumberConfirmed) throw new InvalidOperationException("multi-use invite: compare the safety number with the invitee first");
        await SyncAsync(ct); // roster and team-group epoch must be current before the commit
        await SyncRosterAsync(join.TeamId, ct);
        var u = Device.Call("roster.update", new()
        {
            ["teamId"] = join.TeamId, ["now"] = Now, ["apply"] = true,
            ["addMembers"] = new JsonArray(new JsonObject
            {
                ["memberId"] = Hex(join.MemberId), ["role"] = join.Role ?? "member",
                ["certs"] = new JsonArray(join.Devices.Select(d => (JsonNode)Hex(d.Certificate)).ToArray()),
            }),
        });
        await PostRosterAsync(join.TeamId, B(u["update"]!), ct);
        if (TeamGroupId(join.TeamId) is { } gid)
        {
            var add = Device.Call("group.add", new() { ["groupId"] = gid, ["keyPackages"] = new JsonArray(join.Devices.Select(d => (JsonNode)Hex(d.KeyPackage)).ToArray()), ["now"] = Now });
            await CommitAsync(gid, add, ct);
        }
    }

    private Task<GroupResponse> CommitAsync(string gid, JsonNode commit, CancellationToken ct) =>
        Server.PostJsonAsync<GroupResponse>($"v1/groups/{gid}/commit",
            new CommitRequest(B(commit["commit"]!), commit["welcome"] is { } w ? B(w) : null), ct);

    private Task<RosterAppend> PostRosterAsync(string teamId, byte[] update, CancellationToken ct) =>
        Server.PostBytesAsync<RosterAppend>($"v1/teams/{teamId}/roster", update, RosterContentType, ct);

    // ---------------- roster sync and validation (§4.1) ----------------

    /// <summary>
    /// Fetch roster versions after the local head and apply them through the validator. Equivocation freezes the
    /// team locally (<see cref="RosterSyncResult.Frozen"/>); IP-mode pins of the head are trusted for the connection;
    /// a head without this member means it was removed (the caller wipes, D-5).
    /// </summary>
    public async Task<RosterSyncResult> SyncRosterAsync(string teamId, CancellationToken ct = default)
    {
        var local = Head(teamId)["version"]!.GetValue<ulong>();
        var chain = await Server.GetAsync<List<SyncRoster>>($"v1/teams/{teamId}/roster?from={local + 1}", ct);
        var applied = 0;
        var frozen = false;
        foreach (var r in chain.OrderBy(c => c.Version))
        {
            var res = Device.RequireState().CallRaw("roster.apply", new() { ["teamId"] = teamId, ["update"] = Hex(r.RosterUpdate), ["now"] = Now });
            if (res["ok"]!.GetValue<bool>()) { applied++; continue; }
            if (res["code"]?.GetValue<string>() == "equivocation") { frozen = true; break; }
            OmbraJson.Unwrap(res);
        }
        var status = Status(teamId);
        frozen |= status["frozen"]!.GetValue<bool>();
        var head = Head(teamId);
        var projection = head["projection"]!;
        Server.Endpoint.TrustRosterPins(teamId, projection["pins"]!.AsArray().Select(p => S(p!)));
        var memberHex = Hex(Me.MemberId);
        var deviceHex = Hex(Me.DeviceId);
        var mine = projection["members"]!.AsArray().FirstOrDefault(m => S(m!["memberId"]!) == memberHex);
        var removed = mine is null
            ? WasMember(teamId)
            : mine["devices"]!.AsArray().Any(d => S(d!["deviceId"]!) == deviceHex && d["revoked"] is not null);
        return new RosterSyncResult(teamId, head["version"]!.GetValue<ulong>(), frozen, removed, applied);
    }

    private bool WasMember(string teamId)
    {
        var updates = Device.Call("roster.updates", new() { ["teamId"] = teamId })["updates"]!.AsArray();
        var me = Hex(Me.MemberId);
        return updates.Any(u => OmbraPublic.Call("roster.parse", new() { ["update"] = u!.DeepClone() })["projection"]!["members"]!.AsArray().Any(m => S(m!["memberId"]!) == me));
    }

    /// <summary>
    /// Delta sync (§6.1 /v1/sync): roster versions, Welcomes (join), commits and ops of this device's groups. The team
    /// is wiped (D-5) only when the verified roster no longer lists this member or revoked this device — a Remove
    /// from a single group (e.g. a project) leaves the rest of the team untouched.
    /// </summary>
    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var processed = 0;
        var welcomes = 0;
        var removedTeams = new List<string>();
        var evicted = new List<string>();
        var rejected = new List<string>();
        var itemTeams = new HashSet<string>(); // group ids that delivered item ops
        // design-sync-decision §4: the cursor lives in state.db between calls; the loop runs until More=false; an op is
        // acked only after its commit (or a final rejection, which core logs in the security log); transient failures
        // (storage, need-roster, locked team) are not acked and come back on the next sync.
        var cursor = Device.Call("sync.cursor")["cursor"]?.GetValue<string>();
        while (true)
        {
            var sync = await Server.GetAsync<SyncResponse>("v1/sync" + (cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor)), ct);
            foreach (var team in sync.Roster.Select(r => r.TeamId).Distinct())
            {
                if (removedTeams.Contains(team)) continue;
                var known = Device.RequireState().CallRaw("team.status", new() { ["teamId"] = team, ["now"] = Now })["ok"]!.GetValue<bool>();
                if (!known) continue;
                var r = await SyncRosterAsync(team, ct);
                if (r.RemovedSelf) removedTeams.Add(team);
            }
            var acks = new Dictionary<string, List<AckItem>>();
            foreach (var op in sync.Ops.OrderBy(o => o.Seq))
            {
                JsonObject res = op.Kind == "welcome"
                    ? Device.RequireState().CallRaw("group.join", new() { ["welcome"] = Hex(op.Ciphertext) })
                    : Device.RequireState().CallRaw("group.process", new() { ["groupId"] = op.GroupId, ["message"] = Hex(op.Ciphertext), ["now"] = Now });
                var ok = res["ok"]!.GetValue<bool>();
                var ack = op.Kind == "welcome" ? ok || res["code"]?.GetValue<string>() is not ("need-roster" or "storage" or "internal" or "team-locked")
                    : res["ack"]?.GetValue<bool>() == true;
                if (ok)
                {
                    processed++;
                    if (op.Kind == "welcome") welcomes++;
                    if (res["result"]?["kind"]?.GetValue<string>() == "removed")
                    {
                        // A Remove from one group (e.g. a project after its access was revoked) is not a team removal:
                        // only the verified roster decides whether the whole team is wiped (below).
                        var team = Device.Call("group.list")["groups"]!.AsArray().FirstOrDefault(g => S(g!["groupId"]!) == op.GroupId)?["teamId"];
                        if (team is not null && !evicted.Contains(S(team))) evicted.Add(S(team));
                    }
                    if (res["result"]?["kind"]?.GetValue<string>() == "op") itemTeams.Add(op.GroupId);
                }
                else if (ack)
                {
                    rejected.Add($"{op.Kind} {op.OpId}: {res["code"]} {res["error"]}");
                }
                if (ack) (acks.TryGetValue(op.GroupId, out var l) ? l : acks[op.GroupId] = []).Add(new AckItem(op.SenderDeviceId, op.OpId));
            }
            foreach (var (gid, items) in acks) await Server.PostJsonAsync<AckResponse>($"v1/groups/{gid}/ops/ack", new AckRequest(items), ct);
            cursor = sync.Cursor;
            Device.Call("sync.set_cursor", new() { ["cursor"] = cursor, ["now"] = Now });
            if (!sync.More) break;
        }
        foreach (var t in evicted.Where(t => !removedTeams.Contains(t)))
        {
            var r = await SyncRosterAsync(t, ct);
            if (r.RemovedSelf) removedTeams.Add(t);
        }
        foreach (var t in removedTeams) WipeTeam(t);
        // Each Welcome may have consumed this device's key package: publish a fresh one so peers can open pair groups.
        if (welcomes > 0) await UploadKeyPackageAsync(ct);
        if (itemTeams.Count > 0) OpsReceived?.Invoke(itemTeams.ToList());
        return new SyncResult(processed, welcomes, removedTeams, rejected);
    }

    // ---------------- paired devices of this member (§2.3 п.4) ----------------

    /// <summary>Lists a device this member paired (its certificate issued here) in a team's roster and the team group.</summary>
    public async Task AddOwnDeviceAsync(string teamId, byte[] certificate, CancellationToken ct = default)
    {
        await SyncRosterAsync(teamId, ct);
        var u = Device.Call("roster.update", new() { ["teamId"] = teamId, ["now"] = Now, ["apply"] = true, ["addDevices"] = new JsonArray(Hex(certificate)) });
        await PostRosterAsync(teamId, B(u["update"]!), ct);
    }

    /// <summary>A paired device imports a team its member vouched for in the signed grant; it must find itself in the head.</summary>
    public async Task ImportOwnTeamAsync(string teamId, CancellationToken ct = default)
    {
        var sid = await ServerIdHexAsync(ct);
        var chain = await Server.GetAsync<List<SyncRoster>>($"v1/teams/{teamId}/roster?from=1", ct);
        Device.Call("team.import", new()
        {
            ["teamId"] = teamId, ["serverId"] = sid, ["now"] = Now,
            ["updates"] = new JsonArray(chain.OrderBy(c => c.Version).Select(c => (JsonNode)Hex(c.RosterUpdate)).ToArray()),
        });
        var me = Hex(Me.DeviceId);
        var listed = Head(teamId)["projection"]!["members"]!.AsArray()
            .Any(m => S(m!["memberId"]!) == Hex(Me.MemberId) && m["devices"]!.AsArray().Any(d => S(d!["deviceId"]!) == me && d["revoked"] is null));
        if (!listed)
        {
            Device.Call("team.forget", new() { ["teamId"] = teamId });
            throw new TeamsServerException(409, Reasons.RosterConflict, "this device is not listed in the team's roster");
        }
    }

    // ---------------- removal and revocation (§5) ----------------

    /// <summary>
    /// §5.1: RosterUpdate without the member, Remove of its leaves from the team group, the signed RemovalNotice for
    /// its devices, and an immediate heartbeat.
    /// </summary>
    public async Task RemoveMemberAsync(string teamId, Guid memberId, CancellationToken ct = default)
    {
        await SyncAsync(ct);
        await SyncRosterAsync(teamId, ct);
        var u = Device.Call("roster.update", new()
        {
            ["teamId"] = teamId, ["now"] = Now, ["apply"] = true, ["removeMembers"] = new JsonArray(Hex(memberId)),
        });
        await PostRosterAsync(teamId, B(u["update"]!), ct);
        await RotateTeamGroupAsync(teamId, ct);
        await SyncAdminGroupAsync(teamId, ct); // a removed admin leaves the admin group; its invites are void
        var notice = B(Device.Call("removal.create", new() { ["teamId"] = teamId, ["memberId"] = Hex(memberId), ["time"] = Now })["notice"]!);
        await Server.PostBytesAsync<NoContent>($"v1/teams/{teamId}/removal-notices", notice, ct: ct);
        await PublishHeartbeatAsync(teamId, ct);
    }

    private async Task RotateTeamGroupAsync(string teamId, CancellationToken ct)
    {
        if (TeamGroupId(teamId) is not { } gid) return;
        var res = Device.RequireState().CallRaw("group.rotate", new() { ["groupId"] = gid });
        if (res["ok"]!.GetValue<bool>()) await CommitAsync(gid, res["result"]!, ct);
    }

    /// <summary>
    /// §5.4 / §2.3 п.5: revoke a device of this member — in every team's roster (DeviceRevocation; devices it issued
    /// are cascaded by ombra-core) and server-wide (tokens, sockets, cascade by registration time). <paramref name="compromised"/>:
    /// everything since the device was added is suspect (its certificates too); otherwise only from now on.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RevokeDeviceAsync(Guid deviceId, bool compromised, IEnumerable<string> teams, CancellationToken ct = default)
    {
        var target = Hex(deviceId);
        await SyncAsync(ct);
        foreach (var teamId in teams)
        {
            await SyncRosterAsync(teamId, ct);
            var entry = Head(teamId)["projection"]!["members"]!.AsArray().SelectMany(m => m!["devices"]!.AsArray()).FirstOrDefault(d => S(d!["deviceId"]!) == target);
            if (entry is null || entry["revoked"] is not null) continue;
            var rev = new JsonObject { ["deviceId"] = target };
            if (compromised) rev["compromisedSince"] = entry["addedInVersion"]!.GetValue<ulong>();
            var u = Device.Call("roster.update", new() { ["teamId"] = teamId, ["now"] = Now, ["apply"] = true, ["revokeDevices"] = new JsonArray(rev) });
            await PostRosterAsync(teamId, B(u["update"]!), ct);
            var admin = IsAdmin(teamId);
            if (admin) await RotateTeamGroupAsync(teamId, ct);
            else if (TeamGroupId(teamId) is { } gid)
            {
                // SD-15: a member removes its own revoked leaves itself, without waiting for an admin.
                var cascaded = u["cascaded"]!.AsArray().Select(x => S(x!)).Append(target).ToArray();
                var res = Device.RequireState().CallRaw("group.remove", new() { ["groupId"] = gid, ["deviceIds"] = new JsonArray(cascaded.Select(x => (JsonNode)x).ToArray()) });
                if (res["ok"]!.GetValue<bool>()) await CommitAsync(gid, res["result"]!, ct);
            }
        }
        var since = compromised ? 0UL : Now;
        var r = await Server.PostEnvelopeAsync<DeviceRevokeResponse>("v1/devices/revoke", EnvelopeKinds.DeviceRevoke, Me.MemberId.ToByteArray(bigEndian: true),
            new DeviceRevokeBody(deviceId, since).ToCbor(), ct: ct);
        return r.Revoked;
    }

    public bool IsAdmin(string teamId)
    {
        var me = Hex(Me.MemberId);
        var role = Head(teamId)["projection"]!["members"]!.AsArray().FirstOrDefault(m => S(m!["memberId"]!) == me)?["role"]?.GetValue<string>();
        return role is "owner" or "admin";
    }

    /// <summary>A RemovalNotice from any path: wipe only when ombra-core accepts it (admin-signed, member gone in that version) and it is about this member.</summary>
    public bool ProcessRemovalNotice(byte[] notice)
    {
        var res = Device.RequireState().CallRaw("removal.verify", new() { ["notice"] = Hex(notice) });
        if (!res["ok"]!.GetValue<bool>()) return false;
        var v = res["result"]!;
        if (!v["accepted"]!.GetValue<bool>() || !v["aboutMe"]!.GetValue<bool>()) return false;
        var teamId = S(OmbraPublic.Call("removal.inspect", new() { ["notice"] = Hex(notice) })["teamId"]!);
        WipeTeam(teamId);
        return true;
    }

    /// <summary>
    /// §5.3: <c>403 removed</c> from the team's server (on hello or any request). Verifiable notices are checked first;
    /// an honest client then wipes every team the server reports as removed, notice or not (D-5).
    /// </summary>
    public IReadOnlyList<string> HandleRemoved(TeamsServerException e, IEnumerable<string>? removedTeams = null)
    {
        var wiped = new List<string>();
        foreach (var n in e.RemovalNotices)
            if (ProcessRemovalNotice(n)) wiped.Add(S(OmbraPublic.Call("removal.inspect", new() { ["notice"] = Hex(n) })["teamId"]!));
        foreach (var t in removedTeams ?? [])
            if (!wiped.Contains(t)) { WipeTeam(t); wiped.Add(t); }
        return wiped;
    }

    /// <summary>D-5 local wipe of one team: MLS state, group keys, replica and chain leave the encrypted state; team files are deleted.</summary>
    public void WipeTeam(string teamId)
    {
        var res = Device.RequireState().CallRaw("team.forget", new() { ["teamId"] = teamId });
        if (!res["ok"]!.GetValue<bool>() && res["code"]?.GetValue<string>() != "not-found") OmbraJson.Unwrap(res);
        Device.ForgetTeamKey(teamId);
        var dir = Path.Combine(Device.Directory, "teams", teamId);
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
        // The old state key could still open earlier copies of state.db that contain this team: destroy it.
        Device.RotateStateKey();
        TeamWiped?.Invoke(teamId);
    }

    // ---------------- heartbeat (§4.1) ----------------

    public async Task PublishHeartbeatAsync(string teamId, CancellationToken ct = default)
    {
        var hb = B(Device.Call("heartbeat.create", new() { ["teamId"] = teamId, ["time"] = Now })["heartbeat"]!);
        await Server.PostBytesAsync<NoContent>($"v1/teams/{teamId}/heartbeat", hb, ct: ct);
    }

    /// <summary>Fetch the team's heartbeats, count those ombra-core accepts, and report the confirmation state.</summary>
    public async Task<TeamConfirmation> CheckConfirmationAsync(string teamId, CancellationToken ct = default)
    {
        var hbs = await Server.GetAsync<List<HeartbeatRecord>>($"v1/teams/{teamId}/heartbeats", ct);
        foreach (var hb in hbs)
        {
            var res = Device.RequireState().CallRaw("heartbeat.accept", new() { ["heartbeat"] = Hex(hb.Heartbeat) });
            if (res["ok"]!.GetValue<bool>() || res["code"]?.GetValue<string>() != "need-roster") continue;
            await SyncRosterAsync(teamId, ct);
            Device.RequireState().CallRaw("heartbeat.accept", new() { ["heartbeat"] = Hex(hb.Heartbeat) });
        }
        var c = Status(teamId)["confirmation"]!;
        return new TeamConfirmation(c["confirmed"]!.GetValue<bool>(), c["byOwner"]!.GetValue<bool>(), c["adminVotes"]!.GetValue<int>(), c["required"]!.GetValue<int>());
    }

    // ---------------- ownership (§4.3) ----------------

    public async Task<OwnershipOfferResponse> OfferOwnershipAsync(string teamId, Guid to, string newRoleOfFrom = "admin", TimeSpan? ttl = null, CancellationToken ct = default)
    {
        await SyncRosterAsync(teamId, ct);
        var made = Device.Call("ownership.offer", new()
        {
            ["teamId"] = teamId, ["to"] = Hex(to), ["newRoleOfFrom"] = newRoleOfFrom, ["expiresAt"] = Now + (ulong)(ttl ?? TimeSpan.FromDays(3)).TotalSeconds,
        });
        return await Server.PostJsonAsync<OwnershipOfferResponse>($"v1/teams/{teamId}/ownership/offer", new OwnershipOfferRequest(
            made["offer"]!.Deserialize<OwnershipOffer>(TeamsServer.Json)!, made["offerSig"]!.Deserialize<OwnershipSig>(TeamsServer.Json)!), ct);
    }

    public async Task<OwnershipOfferResponse?> OpenOwnershipOfferAsync(string teamId, CancellationToken ct = default)
    {
        try { return await Server.GetAsync<OwnershipOfferResponse>($"v1/teams/{teamId}/ownership", ct); }
        catch (TeamsServerException e) when (e.Status == 404) { return null; }
    }

    /// <summary>Recipient: sign Accept and publish the RosterUpdate carrying offer + both signatures (validated locally first).</summary>
    public async Task AcceptOwnershipAsync(string teamId, CancellationToken ct = default)
    {
        await SyncRosterAsync(teamId, ct);
        var offer = await OpenOwnershipOfferAsync(teamId, ct) ?? throw new InvalidOperationException("no open ownership offer");
        if (offer.Offer.To != Hex(Me.MemberId)) throw new InvalidOperationException("the offer is for another member");
        var offerJson = JsonSerializer.SerializeToNode(offer.Offer, TeamsServer.Json)!;
        var acceptSig = Device.Call("ownership.accept", new() { ["offer"] = offerJson.DeepClone() })["acceptSig"]!;
        var ownership = new JsonObject { ["offer"] = offerJson, ["offerSig"] = JsonSerializer.SerializeToNode(offer.OfferSig, TeamsServer.Json), ["acceptSig"] = acceptSig.DeepClone() };
        var u = Device.Call("roster.update", new() { ["teamId"] = teamId, ["now"] = Now, ["apply"] = true, ["ownership"] = ownership });
        await Server.PostBytesAsync<RosterAppend>($"v1/teams/{teamId}/ownership/accept", B(u["update"]!), RosterContentType, ct);
    }

    // ---------------- equivocation (§4.1 R2-08) ----------------

    public async Task<EquivocationReview?> EquivocationAsync(string teamId, CancellationToken ct = default)
    {
        RosterEquivocation ev;
        try { ev = await Server.GetAsync<RosterEquivocation>($"v1/teams/{teamId}/roster/equivocation", ct); }
        catch (TeamsServerException e) when (e.Status == 404) { return null; }
        var evidence = new JsonObject { ["version"] = (ulong)ev.Version, ["known"] = Hex(ev.Known), ["conflicting"] = Hex(ev.Conflicting) };
        var diff = Device.Call("roster.equivocation_diff", new() { ["teamId"] = teamId, ["evidence"] = evidence });
        IReadOnlyList<string> Signers(byte[] u) => OmbraPublic.Call("roster.parse", new() { ["update"] = Hex(u) })["signers"]!.AsArray().Select(x => S(x!)).ToList();
        return new EquivocationReview(teamId, (ulong)ev.Version, ev.Known, ev.Conflicting, Signers(ev.Known), Signers(ev.Conflicting),
            diff["undoneIfSwitch"]!.AsArray().Select(x => x!.AsObject()).ToList(), diff["undoneIfKeep"]!.AsArray().Select(x => x!.AsObject()).ToList());
    }

    /// <summary>
    /// Owner: sign the EquivocationResolution. Choosing the conflicting branch when it undoes revocations, removals,
    /// demotions, pins or access needs <paramref name="undoneConfirmed"/> (§7: explicit confirmation of that list).
    /// </summary>
    public async Task ResolveEquivocationAsync(EquivocationReview review, bool keepKnown, bool undoneConfirmed, CancellationToken ct = default)
    {
        var undone = keepKnown ? review.UndoneIfKeep : review.UndoneIfSwitch;
        if (undone.Count > 0 && !undoneConfirmed) throw new InvalidOperationException("confirm the list of changes this choice undoes first");
        var evidence = new JsonObject { ["version"] = review.Version, ["known"] = Hex(review.Known), ["conflicting"] = Hex(review.Conflicting) };
        var r = Device.Call("roster.resolution_create", new() { ["teamId"] = review.TeamId, ["chosen"] = keepKnown ? "known" : "conflicting", ["evidence"] = evidence });
        var resolution = B(r["resolution"]!);
        await Server.PostBytesAsync<JsonObject>($"v1/teams/{review.TeamId}/roster/resolution", resolution, ct: ct);
        await ApplyResolutionsAsync(review.TeamId, ct);
    }

    /// <summary>Every client: apply the owner's published resolutions to its own chain, then catch up.</summary>
    public async Task ApplyResolutionsAsync(string teamId, CancellationToken ct = default)
    {
        var list = await Server.GetAsync<List<byte[]>>($"v1/teams/{teamId}/roster/resolutions", ct);
        foreach (var r in list)
        {
            if (!Status(teamId)["frozen"]!.GetValue<bool>()) break;
            Device.RequireState().CallRaw("roster.resolve", new() { ["teamId"] = teamId, ["resolution"] = Hex(r), ["now"] = Now });
        }
        await SyncRosterAsync(teamId, ct);
    }

    /// <summary>Safety number of a member (60 digits) from the verified roster (§2.1).</summary>
    public string SafetyNumber(string teamId, Guid memberId) =>
        S(Device.Call("roster.safety_number", new() { ["teamId"] = teamId, ["memberId"] = Hex(memberId) })["safetyNumber"]!);
}

/// <summary>Server answer to a roster append.</summary>
public sealed record RosterAppend(string TeamId, long Version, bool Known);
