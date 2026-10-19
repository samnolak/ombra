using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ombra.Core.Interop;
using Ombra.Server.Contracts;

namespace Ombra.ClientCore.Teams;

/// <summary>A team this paired device took over from its member's signed grant.</summary>
public sealed record GrantedTeam(string TeamId, ulong Version);

public sealed partial class TeamClient
{
    // ---------------- R2-05: the admin group and invite secrets ----------------

    public string? AdminGroupId(string teamId) =>
        Device.Call("group.list")["groups"]!.AsArray()
            .Where(g => S(g!["teamId"]!) == teamId && S(g["type"]!) == "admin" && !g["closed"]!.GetValue<bool>() && !g["evicted"]!.GetValue<bool>())
            .Select(g => S(g!["groupId"]!)).FirstOrDefault();

    /// <summary>
    /// The owner's device creates the team's admin group (owner/admin devices only) once; other admins join it by Welcome.
    /// Returns its groupId, or null on a non-owner device that has not been added yet.
    /// </summary>
    public async Task<string?> EnsureAdminGroupAsync(string teamId, CancellationToken ct = default)
    {
        if (AdminGroupId(teamId) is { } existing) return existing;
        var me = Hex(Me.MemberId);
        var role = Head(teamId)["projection"]!["members"]!.AsArray().FirstOrDefault(m => S(m!["memberId"]!) == me)?["role"]?.GetValue<string>();
        if (role != "owner") return null;
        var gid = S(Device.Call("group.create", new() { ["descriptor"] = new JsonObject { ["type"] = "admin", ["teamId"] = teamId, ["generation"] = 0 } })["groupId"]!);
        try
        {
            await Server.PostBytesAsync<GroupResponse>($"v1/teams/{teamId}/groups", B(Device.Call("group.public_info", new() { ["groupId"] = gid })["groupInfo"]!),
                Headers.MlsContentType, ct);
        }
        catch (TeamsServerException e) when (e.Reason == Reasons.Exists)
        {
            Device.Call("group.discard", new() { ["groupId"] = gid }); // another owner device won; its Welcome follows
            return null;
        }
        return gid;
    }

    /// <summary>
    /// Admin side of R2-05, run after roster changes: remove leaves that are no longer owner/admin devices (demoted,
    /// removed, revoked), add the admin devices missing from the admin group, then send this device's invite secrets
    /// to the group. Returns the number of devices added.
    /// </summary>
    public async Task<int> SyncAdminGroupAsync(string teamId, CancellationToken ct = default)
    {
        if (!IsAdmin(teamId) || await EnsureAdminGroupAsync(teamId, ct) is not { } gid) return 0;
        var admins = Head(teamId)["projection"]!["members"]!.AsArray()
            .Where(m => S(m!["role"]!) is "owner" or "admin")
            .SelectMany(m => m!["devices"]!.AsArray())
            .Where(d => d!["revoked"] is null).Select(d => S(d!["deviceId"]!)).ToHashSet();
        var inTree = Device.Call("group.info", new() { ["groupId"] = gid })["members"]!.AsArray().Select(x => S(x!)).ToHashSet();
        if (inTree.Any(d => !admins.Contains(d)))
        {
            // A demoted/removed admin or a revoked device: its leaf leaves the admin group (new epoch, no more secrets).
            await CommitAsync(gid, Device.Call("group.rotate", new() { ["groupId"] = gid }), ct);
            inTree = Device.Call("group.info", new() { ["groupId"] = gid })["members"]!.AsArray().Select(x => S(x!)).ToHashSet();
        }
        var missing = admins.Where(d => !inTree.Contains(d)).ToList();
        var kps = new List<JsonNode>();
        foreach (var d in missing)
        {
            try { kps.Add(Hex((await Server.GetAsync<KeyPackageResponse>($"v1/keypackages/{TeamsHex.ToGuid(d)}?team={teamId}&group={gid}", ct)).KeyPackage)); }
            catch (TeamsServerException e) when (e.Status is 404 or 429) { } // not published yet / already used today: next round
        }
        if (kps.Count > 0)
        {
            var add = Device.Call("group.add", new() { ["groupId"] = gid, ["keyPackages"] = new JsonArray(kps.ToArray()), ["now"] = Now });
            await CommitAsync(gid, add, ct);
        }
        await ShareInviteKeysAsync(teamId, ct);
        return kps.Count;
    }

    /// <summary>R2-05: this device's unexpired invite secrets (k_adm) to the team's admin group — never to any other group.</summary>
    public async Task ShareInviteKeysAsync(string teamId, CancellationToken ct = default)
    {
        if (AdminGroupId(teamId) is not { } gid) return;
        var res = Device.RequireState().CallRaw("adminkeys.seal", new() { ["groupId"] = gid, ["now"] = Now });
        if (!res["ok"]!.GetValue<bool>()) { OmbraJson.Unwrap(res); return; }
        if (res["result"]!["invites"]!.GetValue<int>() == 0) return;
        var epoch = Device.Call("group.info", new() { ["groupId"] = gid })["epoch"]!.GetValue<ulong>();
        await Server.PostEnvelopeAsync<OpPutResponse>($"v1/groups/{gid}/ops", EnvelopeKinds.OpPut, Convert.FromHexString(gid),
            new OpPutBody(epoch, B(res["result"]!["message"]!), false).ToCbor(), ct: ct);
    }

    // ---------------- §2.3 п.4: paired devices follow their member into new teams ----------------

    /// <summary>
    /// Member side: list this member's paired devices (certified, not revoked) in <paramref name="teamId"/>'s roster
    /// when they are missing there, and refresh each paired device's signed grant to every team that now lists it.
    /// A device whose certificate issuer is not an active device of the head is skipped (the validator would refuse it).
    /// Returns the deviceIds added to the roster.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> SyncOwnDevicesAsync(string teamId, CancellationToken ct = default)
    {
        await SyncRosterAsync(teamId, ct);
        var own = await Server.GetAsync<List<OwnDevice>>("v1/devices", ct);
        var head = Head(teamId)["projection"]!;
        var me = Hex(Me.MemberId);
        var mine = head["members"]!.AsArray().FirstOrDefault(m => S(m!["memberId"]!) == me);
        if (mine is null) return [];
        var listed = mine["devices"]!.AsArray().Select(d => S(d!["deviceId"]!)).ToHashSet();
        var active = mine["devices"]!.AsArray().Where(d => d!["revoked"] is null).Select(d => S(d!["deviceId"]!)).ToHashSet();
        var add = own.Where(d => !d.Revoked && d.Certificate is not null && d.IssuerDeviceId is { } i && active.Contains(Hex(i)) && !listed.Contains(Hex(d.DeviceId)))
            .ToList();
        if (add.Count > 0)
        {
            var u = Device.Call("roster.update", new()
            {
                ["teamId"] = teamId, ["now"] = Now, ["apply"] = true,
                ["addDevices"] = new JsonArray(add.Select(d => (JsonNode)Hex(d.Certificate!)).ToArray()),
            });
            await PostRosterAsync(teamId, B(u["update"]!), ct);
        }
        foreach (var d in own.Where(d => !d.Revoked && d.Certificate is not null && d.DeviceId != Me.DeviceId))
            await RefreshGrantAsync(d.DeviceId, ct);
        return add.Select(d => d.DeviceId).ToList();
    }

    /// <summary>The paired device's grant becomes every locally verified team whose head lists it as an active device.</summary>
    private async Task RefreshGrantAsync(Guid deviceId, CancellationToken ct)
    {
        var target = Hex(deviceId);
        var teams = Device.Call("group.list")["groups"]!.AsArray().Select(g => S(g!["teamId"]!)).Distinct()
            .Where(t => Head(t)["projection"]!["members"]!.AsArray().SelectMany(m => m!["devices"]!.AsArray())
                .Any(d => S(d!["deviceId"]!) == target && d["revoked"] is null))
            .ToList();
        if (teams.Count == 0) return;
        var (env, sig) = Server.SignEnvelope(EnvelopeKinds.DeviceGrant, deviceId.ToByteArray(bigEndian: true),
            new DeviceGrantBody(teams.Select(Convert.FromHexString).ToList()).ToCbor());
        await Server.PostJsonAsync<NoContent>($"v1/devices/{deviceId:D}/grant", new DeviceGrantRequest(env, sig), ct);
    }

    /// <summary>
    /// Paired-device side: take over the teams of the member's current signed grant that this device does not know
    /// yet. The grant must be signed by the device that certified this one (its key was confirmed at pairing) or by
    /// a sibling listed as an active device of this member in a team this device already verified. Each new team is
    /// imported against its chain (this device must be listed in the head), recorded as a pending join so the admin's
    /// Welcome is accepted, and a key package is published for that Add.
    /// </summary>
    public async Task<IReadOnlyList<GrantedTeam>> JoinGrantedTeamsAsync(CancellationToken ct = default)
    {
        var rec = await Server.GetAsync<DeviceRecord>($"v1/devices/{Me.DeviceId:D}/certificate", ct);
        if (!OmbraPublic.VerifyEnvelope(rec.GrantEnvelope, rec.GrantSignature, rec.GrantSignerIdentityKey))
            throw new PairingTamperedException("the team grant signature does not verify");
        var env = Envelope.Decode(rec.GrantEnvelope);
        if (env.Kind != EnvelopeKinds.DeviceGrant || env.DeviceId != rec.GrantSignerDeviceId || !env.Scope.AsSpan().SequenceEqual(Me.DeviceId.ToByteArray(bigEndian: true)))
            throw new PairingTamperedException("the team grant is for another device");
        if (!GrantSignerTrusted(rec)) throw new PairingTamperedException("the team grant is signed by a device this device cannot vouch for");
        var granted = DeviceGrantBody.Parse(env.BodyMap()).TeamIds.Select(t => TeamsHex.Of(t)).ToList();
        var sid = Hex(await Server.ServerIdAsync(ct));
        var fingerprint = Hex(SHA256.HashData(rec.GrantSignerIdentityKey));
        var result = new List<GrantedTeam>();
        foreach (var teamId in granted)
        {
            if (Device.RequireState().CallRaw("team.status", new() { ["teamId"] = teamId, ["now"] = Now })["ok"]!.GetValue<bool>()) continue;
            var chain = await Server.GetAsync<List<SyncRoster>>($"v1/teams/{teamId}/roster?from=1", ct);
            if (chain.Count == 0) continue;
            var last = chain.MaxBy(c => c.Version)!;
            var hash = S(OmbraPublic.Call("roster.parse", new() { ["update"] = Hex(last.RosterUpdate) })["hash"]!);
            Device.Call("team.import", new()
            {
                ["teamId"] = teamId, ["serverId"] = sid, ["now"] = Now,
                ["updates"] = new JsonArray(chain.OrderBy(c => c.Version).Select(c => (JsonNode)Hex(c.RosterUpdate)).ToArray()),
                ["rosterHead"] = new JsonObject { ["version"] = (ulong)last.Version, ["hash"] = hash }, ["inviterFingerprint"] = fingerprint,
            });
            var me = Hex(Me.DeviceId);
            var listed = Head(teamId)["projection"]!["members"]!.AsArray()
                .Any(m => S(m!["memberId"]!) == Hex(Me.MemberId) && m["devices"]!.AsArray().Any(d => S(d!["deviceId"]!) == me && d["revoked"] is null));
            if (!listed)
            {
                Device.Call("team.forget", new() { ["teamId"] = teamId });
                continue; // granted before the roster update reached the server: next round
            }
            result.Add(new GrantedTeam(teamId, (ulong)last.Version));
        }
        // One KeyPackage per team group the admin will Add this device to (each Add consumes one).
        foreach (var _ in result) await UploadKeyPackageAsync(ct);
        return result;
    }

    private bool GrantSignerTrusted(DeviceRecord rec)
    {
        if (rec.GrantSignerDeviceId == rec.IssuerDeviceId && rec.GrantSignerIdentityKey.AsSpan().SequenceEqual(rec.IssuerIdentityKey)) return true;
        var signer = Hex(rec.GrantSignerDeviceId);
        var signerKeyHash = Hex(SHA256.HashData(rec.GrantSignerIdentityKey));
        var me = Hex(Me.MemberId);
        foreach (var teamId in Device.Call("group.list")["groups"]!.AsArray().Select(g => S(g!["teamId"]!)).Distinct())
        {
            var mine = Head(teamId)["projection"]!["members"]!.AsArray().FirstOrDefault(m => S(m!["memberId"]!) == me);
            if (mine?["devices"]!.AsArray().Any(d => S(d!["deviceId"]!) == signer && d["revoked"] is null && S(d["identityKeyHash"]!) == signerKeyHash) == true)
                return true;
        }
        return false;
    }
}
