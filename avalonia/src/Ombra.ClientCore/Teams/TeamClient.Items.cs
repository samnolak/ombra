using System.Text.Json.Nodes;
using Ombra.Server.Contracts;

namespace Ombra.ClientCore.Teams;

/// <summary>Result of one outbox flush: ops accepted by the server, ops the server finally refused.</summary>
public sealed record OutboxFlushResult(int Sent, int Dropped, int Pending);

/// <summary>Team items transport (design-sync-decision §1–§4): durable outbox in ombra-core, pair groups for addressed items.</summary>
public sealed partial class TeamClient
{
    /// <summary>Item ops arrived during <see cref="SyncAsync"/>; argument: the group ids they came in.</summary>
    public event Action<IReadOnlyList<string>>? OpsReceived;

    /// <summary>
    /// Send every queued own op. Each op is MLS-sealed once in core; its signed envelope (opId = the op's opId) is kept in
    /// the outbox so a retry is byte-identical. 409 epoch-conflict: the server rolled the op back; core drops the
    /// ciphertext, the group catches up through sync, and the op is resealed with the same opId/seq/rev. Network and
    /// 429/5xx failures leave the op queued.
    /// </summary>
    public async Task<OutboxFlushResult> FlushOutboxAsync(CancellationToken ct = default)
    {
        var sent = 0;
        var dropped = 0;
        var synced = false;
        foreach (var op in Device.Call("outbox.list")["ops"]!.AsArray().ToList())
        {
            var opId = S(op!["opId"]!);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var sealedOp = Device.Call("outbox.seal", new() { ["opId"] = opId });
                var gid = S(sealedOp["groupId"]!);
                byte[] env, sig;
                if (sealedOp["envelope"] is { } e && sealedOp["signature"] is { } s) { env = B(e); sig = B(s); }
                else
                {
                    var body = new OpPutBody(sealedOp["epoch"]!.GetValue<ulong>(), B(sealedOp["message"]!), false).ToCbor();
                    (env, sig) = Server.SignEnvelope(EnvelopeKinds.OpPut, Convert.FromHexString(gid), body, opId: TeamsHex.ToGuid(opId));
                    Device.Call("outbox.envelope", new() { ["opId"] = opId, ["envelope"] = Hex(env), ["signature"] = Hex(sig) });
                }
                try
                {
                    await Server.SendEnvelopeAsync<OpPutResponse>($"v1/groups/{gid}/ops", env, sig, anonymous: false, ct);
                    Device.Call("outbox.sent", new() { ["opId"] = opId });
                    sent++;
                    break;
                }
                catch (TeamsServerException x) when (x.Reason == Reasons.EpochConflict)
                {
                    Device.Call("outbox.conflict", new() { ["opId"] = opId });
                    if (!synced) { await SyncAsync(ct); synced = true; }
                }
                catch (TeamsServerException x) when (x.Reason == Reasons.OpIdReused)
                {
                    // The server already holds this opId (an earlier attempt landed): it is delivered.
                    Device.Call("outbox.sent", new() { ["opId"] = opId });
                    sent++;
                    break;
                }
                catch (TeamsServerException x) when (x.Status == 400 && x.Detail?.Contains("ts outside") == true)
                {
                    Device.Call("outbox.envelope", new() { ["opId"] = opId }); // stale envelope: sign a fresh one, same opId
                }
                catch (TeamsServerException x) when (x.Status is 403 or 404 or 413 || x.Reason is Reasons.ClosedGeneration or Reasons.BadRequest)
                {
                    Device.Call("outbox.drop", new() { ["opId"] = opId, ["reason"] = $"{x.Status} {x.Reason} {x.Detail}", ["now"] = Now });
                    dropped++;
                    break;
                }
            }
        }
        return new OutboxFlushResult(sent, dropped, Device.Call("outbox.list")["ops"]!.AsArray().Count);
    }

    /// <summary>Queued own ops (all teams, or one).</summary>
    public int OutboxCount(string? teamId = null) =>
        Device.Call("outbox.list", teamId is null ? null : new() { ["teamId"] = teamId })["ops"]!.AsArray().Count;

    /// <summary>The active pair group of this member with <paramref name="peer"/>, or null.</summary>
    public string? PairGroupId(string teamId, string peer)
    {
        var me = Hex(Me.MemberId);
        return Device.Call("group.list")["groups"]!.AsArray()
            .Where(g => S(g!["teamId"]!) == teamId && S(g["type"]!) == "pair" && !g["closed"]!.GetValue<bool>() && !g["evicted"]!.GetValue<bool>())
            .Where(g => g!["descriptor"]?["members"] is JsonArray m && m.Any(x => S(x!) == me) && m.Any(x => S(x!) == peer))
            .OrderByDescending(g => g!["descriptor"]!["generation"]!.GetValue<uint>())
            .Select(g => S(g!["groupId"]!)).FirstOrDefault();
    }

    /// <summary>
    /// Pair group for addressed items (R5-10): created on first use with every active device of both members that has
    /// a published key package; the peer joins from the Welcome at its next sync.
    /// </summary>
    public async Task<string> EnsurePairGroupAsync(string teamId, string peer, CancellationToken ct = default)
    {
        if (PairGroupId(teamId, peer) is { } existing) return existing;
        var me = Hex(Me.MemberId);
        var members = new[] { me, peer }.Order(StringComparer.Ordinal).Select(x => (JsonNode)x).ToArray();
        var self = Hex(Me.DeviceId);
        var devices = Head(teamId)["projection"]!["members"]!.AsArray().Where(m => S(m!["memberId"]!) is var id && (id == me || id == peer))
            .SelectMany(m => m!["devices"]!.AsArray().Select(d => (Member: S(m["memberId"]!), Device: d!)))
            .Where(x => x.Device["revoked"] is null && S(x.Device["deviceId"]!) != self).ToList();
        // Key packages first: a pair group the peer cannot join is useless (its devices publish one after their Welcome).
        var kps = new List<JsonNode>();
        var peerHasKp = false;
        foreach (var (member, d) in devices)
        {
            try
            {
                kps.Add(Hex((await Server.GetAsync<KeyPackageResponse>($"v1/keypackages/{TeamsHex.ToGuid(S(d["deviceId"]!))}?team={teamId}", ct)).KeyPackage));
                peerHasKp |= member == peer;
            }
            catch (TeamsServerException e) when (e.Status == 404) { } // added later by the converged-state check
        }
        if (!peerHasKp) throw new TeamsServerException(404, "no-key-package", "the recipient's devices have not published a key package yet; try again later");
        var gid = S(Device.Call("group.create", new()
        {
            ["descriptor"] = new JsonObject { ["type"] = "pair", ["teamId"] = teamId, ["members"] = new JsonArray(members), ["generation"] = 0 },
        })["groupId"]!);
        var gi = B(Device.Call("group.public_info", new() { ["groupId"] = gid })["groupInfo"]!);
        await Server.PostBytesAsync<GroupResponse>($"v1/teams/{teamId}/groups", gi, Headers.MlsContentType, ct);
        var add = Device.Call("group.add", new() { ["groupId"] = gid, ["keyPackages"] = new JsonArray(kps.ToArray()), ["now"] = Now });
        await CommitAsync(gid, add, ct);
        return gid;
    }
}
