using System.Text.Json.Nodes;
using Ombra.Server.Contracts;

namespace Ombra.ClientCore.Teams;

/// <summary>A device of a member as the verified roster lists it, with its name from the team's names (SD-16).</summary>
public sealed record TeamDevice(Guid DeviceId, string? Name, bool Revoked, bool IsThisDevice);

/// <summary>A member of a team: role from the verified roster, name from the names confirmed against it.</summary>
public sealed record TeamMember(Guid MemberId, string Role, string? Name, IReadOnlyList<TeamDevice> Devices, bool IsMe, string RootFingerprint)
{
    public bool IsOwner => Role == "owner";
}

/// <summary>The roster head of one team as the UI shows it.</summary>
public sealed record TeamRoster(string TeamId, ulong Version, Guid Owner, IReadOnlyList<TeamMember> Members, bool NamesConfirmed);

public sealed partial class TeamClient
{
    /// <summary>Members and devices of the verified head, names from the kept privatePart (names.rs).</summary>
    public TeamRoster Roster(string teamId)
    {
        var head = Head(teamId);
        var p = head["projection"]!;
        var names = Device.Call("names.get", new() { ["teamId"] = teamId });
        var memberNames = names["names"]!.AsObject();
        var deviceNames = names["devices"]!.AsObject();
        var me = Hex(Me.MemberId);
        var thisDevice = Hex(Me.DeviceId);
        var members = p["members"]!.AsArray().Select(m =>
        {
            var id = S(m!["memberId"]!);
            var devices = m["devices"]!.AsArray().Select(d =>
            {
                var did = S(d!["deviceId"]!);
                return new TeamDevice(TeamsHex.ToGuid(did), deviceNames[did]?.GetValue<string>(), d["revoked"] is not null, did == thisDevice);
            }).ToList();
            var root = S(Device.Call("roster.safety_number", new() { ["teamId"] = teamId, ["memberId"] = id })["rootFingerprint"]!);
            return new TeamMember(TeamsHex.ToGuid(id), S(m["role"]!), memberNames[id]?.GetValue<string>(), devices, id == me, root);
        }).ToList();
        return new TeamRoster(teamId, head["version"]!.GetValue<ulong>(), TeamsHex.ToGuid(p["owner"]!), members, names["confirmed"]!.GetValue<bool>());
    }

    /// <summary>
    /// SD-16: this member's name (and its own devices' names) for the team: ombra-core builds the new privatePart, the
    /// roster carries its commitment, the part itself goes to the team group under MLS (the server never sees a name).
    /// </summary>
    public async Task SetNamesAsync(string teamId, string? name, IReadOnlyDictionary<Guid, string?>? devices = null, CancellationToken ct = default)
    {
        await SyncAsync(ct);
        await SyncRosterAsync(teamId, ct);
        var args = new JsonObject { ["teamId"] = teamId };
        if (name is not null) args["name"] = name;
        if (devices is not null) args["devices"] = new JsonObject(devices.Select(d => KeyValuePair.Create(Hex(d.Key), (JsonNode?)d.Value)));
        var commit = S(Device.Call("names.set", args)["privateCommit"]!);
        var u = Device.Call("roster.update", new() { ["teamId"] = teamId, ["now"] = Now, ["apply"] = true, ["privateCommit"] = commit });
        await PostRosterAsync(teamId, B(u["update"]!), ct);
        await SendNamesAsync(teamId, ct);
    }

    /// <summary>The kept names to the team group (after a name change, and to a newcomer after its Welcome).</summary>
    public async Task SendNamesAsync(string teamId, CancellationToken ct = default)
    {
        if (TeamGroupId(teamId) is not { } gid) return;
        var res = Device.RequireState().CallRaw("names.seal", new() { ["groupId"] = gid });
        if (!res["ok"]!.GetValue<bool>())
        {
            if (res["code"]?.GetValue<string>() == "not-found") return; // no names in this team yet
            Ombra.Core.Interop.OmbraJson.Unwrap(res);
        }
        var epoch = Device.Call("group.info", new() { ["groupId"] = gid })["epoch"]!.GetValue<ulong>();
        await Server.PostEnvelopeAsync<OpPutResponse>($"v1/groups/{gid}/ops", EnvelopeKinds.OpPut, Convert.FromHexString(gid),
            new OpPutBody(epoch, B(res["result"]!["message"]!), false).ToCbor(), ct: ct);
    }

    /// <summary>Publish key packages so an admin can add this device to the team group (paired devices, §2.3 п.4).</summary>
    public async Task UploadKeyPackageAsync(CancellationToken ct = default)
    {
        var kp = B(Device.Call("key_package")["keyPackage"]!);
        await Server.PostJsonAsync<KeyPackageUploadResponse>("v1/keypackages", new KeyPackageUpload([kp]), ct);
    }

    /// <summary>
    /// R2-12 converged state, admin side: devices the roster admits but the team group lacks (a member's newly paired
    /// device) are added with their published key package; the names follow the Welcome. Returns how many were added.
    /// </summary>
    public async Task<int> AddMissingDevicesAsync(string teamId, CancellationToken ct = default)
    {
        if (!IsAdmin(teamId) || TeamGroupId(teamId) is not { } gid) return 0;
        if (Device.Call("group.tree_state", new() { ["groupId"] = gid })["state"]!.GetValue<int>() != 1) return 0;
        var inTree = Device.Call("group.info", new() { ["groupId"] = gid })["members"]!.AsArray().Select(x => S(x!)).ToHashSet();
        var missing = Head(teamId)["projection"]!["members"]!.AsArray().SelectMany(m => m!["devices"]!.AsArray())
            .Where(d => d!["revoked"] is null && !inTree.Contains(S(d["deviceId"]!))).Select(d => S(d!["deviceId"]!)).ToList();
        var kps = new List<JsonNode>();
        foreach (var d in missing)
        {
            try { kps.Add(Hex((await Server.GetAsync<KeyPackageResponse>($"v1/keypackages/{TeamsHex.ToGuid(d)}?team={teamId}&group={gid}", ct)).KeyPackage)); }
            catch (TeamsServerException e) when (e.Status == 404) { } // that device has not published one yet: next round
        }
        if (kps.Count == 0) return 0;
        var add = Device.Call("group.add", new() { ["groupId"] = gid, ["keyPackages"] = new JsonArray(kps.ToArray()), ["now"] = Now });
        await CommitAsync(gid, add, ct);
        await SendNamesAsync(teamId, ct);
        return kps.Count;
    }

    /// <summary>N9: freeze or release the pair with a member whose root key changed, until its safety number is compared.</summary>
    public void SetPeerFrozen(string teamId, Guid memberId, bool frozen) =>
        Device.Call(frozen ? "pair.freeze" : "pair.unfreeze", new() { ["teamId"] = teamId, ["memberId"] = Hex(memberId) });
}
