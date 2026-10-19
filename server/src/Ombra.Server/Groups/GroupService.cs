using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Core.Interop;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;
using Ombra.Server.Data;
using Ombra.Server.Delivery;
using Ombra.Server.Streaming;

namespace Ombra.Server.Groups;

internal sealed record DescriptorDto(string Type, string TeamId, string? ProjectId, List<string>? Members, string? Owner, int Generation);

/// <summary>
/// MLS handshake on the server (§3.1, §6.1 /commit, N1, N2) in "public verification" mode: ombra-core
/// (<see cref="OmbraPublic.PublicGroupInit"/>/<see cref="OmbraPublic.PublicGroupCommit"/>) applies the same commit
/// policy as clients against the verified roster chain; the server never re-implements it. The server adds:
/// <list type="bullet">
/// <item>ordering: one commit per epoch under the group row lock; a commit for another epoch → 409 epoch-conflict;</item>
/// <item>first group of a groupId wins (N2 race → 409 exists); only the creator's epoch-0 GroupInfo registers a group
/// (team/project: creator is owner/admin, checked by ombra-core) and the uploader must be that creator;
/// a newer pair/personal generation closes older ones (operations to them → 409 closed-generation);</item>
/// <item>delivery of the commit to the remaining unblocked leaves and of the Welcome to added devices via the queue;</item>
/// <item>N1: a KeyPackage counts as used only when the commit with its Add is accepted here.</item>
/// </list>
/// </summary>
public sealed class GroupService(NpgsqlDataSource db, TimeProvider clock, StreamHub hub, IOptions<OmbraOptions> options)
{
    public const int MaxHandshakeBytes = 1 << 20;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly LimitsOptions _limits = options.Value.Limits;

    private static ApiException Map(OmbraException e) => e.Code == "need-roster"
        ? ApiException.Conflict(Reasons.RosterConflict, e.Detail)
        : ApiException.Forbidden(Reasons.CommitRejected, $"{e.Code}: {e.Detail}");

    private static async Task<byte[]> ChainAsync(NpgsqlConnection conn, NpgsqlTransaction tx, byte[] teamId)
    {
        var team = await conn.SingleAsync("SELECT chain_state, frozen_reason FROM teams WHERE team_id = @t", tx,
            r => new { Chain = r.IsDBNull(0) ? null : r.Bytes(0), Frozen = r.StringOrNull(1) }, ("t", teamId)) ?? throw ApiException.NotFound("team");
        if (team.Frozen is not null) throw ApiException.Forbidden(Reasons.Frozen, team.Frozen);
        return team.Chain ?? throw ApiException.Conflict(Reasons.RosterConflict, "team has no verified chain");
    }

    public async Task<GroupResponse> InitAsync(DeviceIdentity device, byte[] teamId, byte[] groupInfo)
    {
        if (groupInfo.Length is 0 or > MaxHandshakeBytes) throw ApiException.Bad("bad GroupInfo size");
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        // Quarantine allows handshake uploads (§1 карантин п.2); blocked/removed devices are refused.
        await AccessService.RequireActiveAsync(conn, tx, teamId, device.DeviceId);
        var chain = await ChainAsync(conn, tx, teamId);
        PublicGroupResult r;
        try { r = OmbraPublic.PublicGroupInit(chain, groupInfo); }
        catch (OmbraException e) { throw Map(e); }
        var leaves = r.Leaves.Select(l => Ids.FromBytes(l)).ToList();
        if (r.Committer is null || Ids.FromBytes(r.Committer) != device.DeviceId)
            throw ApiException.Forbidden(Reasons.NotMember, "only the group's creator (the GroupInfo signer) registers it");
        var d = JsonSerializer.Deserialize<DescriptorDto>(r.DescriptorJson!, Web)!;
        Guid? P(string? hex) => hex is null ? null : Guid.ParseExact(hex, "N");
        var memberA = d.Members is { Count: 2 } ? P(d.Members[0]) : null;
        var memberB = d.Members is { Count: 2 } ? P(d.Members[1]) : null;
        var now = clock.GetUtcNow();
        var inserted = await conn.ExecAsync(
            "INSERT INTO groups (group_id, team_id, kind, project_id, member_a, member_b, owner_member_id, generation, epoch, roster_version, group_state, created_at) " +
            "VALUES (@g, @t, @k, @p, @a, @b, @o, @gen, @e, @rv, @s, @now) ON CONFLICT (group_id) DO NOTHING", tx,
            ("g", r.GroupId), ("t", teamId), ("k", d.Type), ("p", P(d.ProjectId)), ("a", memberA), ("b", memberB), ("o", P(d.Owner)),
            ("gen", d.Generation), ("e", (long)r.Epoch), ("rv", (long)r.RosterVersion), ("s", r.GroupState), ("now", now));
        if (inserted == 0) throw ApiException.Conflict(Reasons.Exists, "group already created (first one wins)");
        foreach (var l in leaves)
            await conn.ExecAsync("INSERT INTO group_leaves (group_id, device_id) VALUES (@g, @d)", tx, ("g", r.GroupId), ("d", l));
        // N2a: a newer generation of the same pair/personal group closes the older ones.
        if (d.Type == "pair")
            await conn.ExecAsync("UPDATE groups SET closed_at = @now WHERE team_id = @t AND kind = 'pair' AND member_a = @a AND member_b = @b " +
                                 "AND generation < @gen AND closed_at IS NULL", tx, ("now", now), ("t", teamId), ("a", memberA), ("b", memberB), ("gen", d.Generation));
        else if (d.Type == "personal")
            await conn.ExecAsync("UPDATE groups SET closed_at = @now WHERE team_id = @t AND kind = 'personal' AND owner_member_id = @o " +
                                 "AND generation < @gen AND closed_at IS NULL", tx, ("now", now), ("t", teamId), ("o", P(d.Owner)), ("gen", d.Generation));
        await tx.CommitAsync();
        return new GroupResponse(Ids.Hex(r.GroupId), (long)r.Epoch, leaves);
    }

    public async Task<GroupResponse> CommitAsync(DeviceIdentity device, byte[] groupId, CommitRequest req)
    {
        if (req.Commit is not { Length: > 0 and <= MaxHandshakeBytes }) throw ApiException.Bad("bad commit size");
        if (req.Welcome is { Length: > MaxHandshakeBytes }) throw ApiException.Bad("bad welcome size");
        ulong msgEpoch;
        try { msgEpoch = OmbraPublic.InspectMessage(req.Commit)["epoch"]!.GetValue<ulong>(); }
        catch (OmbraException e) { throw ApiException.Bad($"{e.Code}: {e.Detail}"); }

        var now = clock.GetUtcNow();
        List<Guid> commitRecipients, welcomeRecipients;
        GroupResponse response;
        byte[] teamId;
        await using (var conn = await db.OpenConnectionAsync())
        {
            await using var tx = await conn.BeginTransactionAsync();
            var g = await conn.SingleAsync(
                "SELECT team_id, epoch, closed_at IS NOT NULL, group_state FROM groups WHERE group_id = @g FOR UPDATE", tx,
                r => new { Team = r.Bytes(0), Epoch = r.GetInt64(1), Closed = r.GetBoolean(2), State = r.IsDBNull(3) ? null : r.Bytes(3) },
                ("g", groupId)) ?? throw ApiException.NotFound("group");
            teamId = g.Team;
            var access = await AccessService.RequireActiveAsync(conn, tx, g.Team, device.DeviceId);
            if (g.Closed) throw ApiException.Conflict(Reasons.ClosedGeneration);
            if ((long)msgEpoch != g.Epoch) throw ApiException.Conflict(Reasons.EpochConflict, $"current epoch {g.Epoch}");
            if (g.State is null) throw ApiException.Conflict(Reasons.EpochConflict, "group has no public state");
            var chain = await ChainAsync(conn, tx, g.Team);
            PublicGroupResult r;
            try { r = OmbraPublic.PublicGroupCommit(chain, g.State, req.Commit); }
            catch (OmbraException e) { throw Map(e); }
            if (r.Committer is null || Ids.FromBytes(r.Committer) != device.DeviceId)
                throw ApiException.Forbidden(Reasons.CommitRejected, "the committer must be the requesting device");

            var before = (await conn.ListAsync("SELECT device_id FROM group_leaves WHERE group_id = @g", tx, x => x.GetGuid(0), ("g", groupId))).ToHashSet();
            var after = r.Leaves.Select(l => Ids.FromBytes(l)).ToHashSet();
            var added = r.Added.Select(a => Ids.FromBytes(a.DeviceId)).ToHashSet();
            if (added.Count > 0 && req.Welcome is null) throw ApiException.Bad("a commit with Add needs its Welcome");
            if (added.Count == 0 && req.Welcome is not null) throw ApiException.Bad("Welcome without Add");

            await conn.ExecAsync("UPDATE groups SET epoch = @e, group_state = @s, roster_version = @rv WHERE group_id = @g", tx,
                ("e", (long)r.Epoch), ("s", r.GroupState), ("rv", (long)r.RosterVersion), ("g", groupId));
            await conn.ExecAsync("DELETE FROM group_leaves WHERE group_id = @g", tx, ("g", groupId));
            foreach (var l in after) await conn.ExecAsync("INSERT INTO group_leaves (group_id, device_id) VALUES (@g, @d)", tx, ("g", groupId), ("d", l));
            // N1: the KP is "used" only now that the Add is accepted.
            await conn.ExecAsync("UPDATE kp_issuance SET consumed_at = @now, consumed_group = @g WHERE kp_ref = ANY(@refs) AND requester_device_id = @d AND consumed_at IS NULL", tx,
                ("now", now), ("g", groupId), ("refs", r.Added.Select(a => a.KpRef).ToArray()), ("d", device.DeviceId));

            var active = (await conn.ListAsync("SELECT device_id FROM team_device_access WHERE team_id = @t", tx, x => x.GetGuid(0), ("t", g.Team))).ToHashSet();
            commitRecipients = before.Where(d => after.Contains(d) && d != device.DeviceId && active.Contains(d)).Order().ToList();
            welcomeRecipients = added.Where(active.Contains).Order().ToList();
            await EnqueueAsync(conn, tx, groupId, g.Team, device.DeviceId, access.MemberId, (long)r.Epoch, "commit", req.Commit, commitRecipients, now);
            if (req.Welcome is not null)
                await EnqueueAsync(conn, tx, groupId, g.Team, device.DeviceId, access.MemberId, (long)r.Epoch, "welcome", req.Welcome, welcomeRecipients, now);
            await tx.CommitAsync();
            response = new GroupResponse(Ids.Hex(groupId), (long)r.Epoch, after.Order().ToList());
        }
        var evt = new StreamEvent(StreamEventTypes.Revision, TeamId: Ids.Hex(teamId), GroupId: Ids.Hex(groupId));
        hub.Send(commitRecipients.Concat(welcomeRecipients), evt);
        return response;
    }

    private async Task EnqueueAsync(NpgsqlConnection conn, NpgsqlTransaction tx, byte[] groupId, byte[] teamId, Guid sender, Guid senderMember,
        long epoch, string kind, byte[] payload, List<Guid> pending, DateTimeOffset now)
    {
        if (pending.Count == 0) return;
        await conn.ExecAsync(
            "INSERT INTO delivery (group_id, sender_device_id, op_id, team_id, sender_member_id, epoch, ciphertext, size, pending_device_ids, created_at, expires_at, kind) " +
            "VALUES (@g, @sd, @o, @t, @sm, @e, @c, @n, @p, @now, @exp, @k)", tx,
            ("g", groupId), ("sd", sender), ("o", Guid.CreateVersion7()), ("t", teamId), ("sm", senderMember), ("e", epoch), ("c", payload),
            ("n", payload.Length), ("p", pending.ToArray()), ("now", now), ("exp", now.AddDays(_limits.QueueTtlDays)), ("k", kind));
    }
}
