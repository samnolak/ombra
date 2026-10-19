using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ombra.Server.Contracts;
using Ombra.Server.Retention;
using Ombra.Server.Revocation;
using Ombra.Server.Tests.Infra;

namespace Ombra.Server.Tests;

/// <summary>S0 / N7 / R5-06 / N8: delivery queue contract tests (§3.0, §6.1).</summary>
public sealed class DeliveryTests(PostgresFixture pg)
{
    private sealed record World(ServerHarness H, byte[] Team, TestDevice Admin, TestDevice A1, TestDevice A2, TestDevice B, byte[] Group);

    private async Task<World> WorldAsync(Action<Dictionary<string, string?>>? tweak = null)
    {
        var h = await ServerHarness.StartAsync(pg, tweak);
        var team = await h.SeedTeamAsync();
        var admin = await h.AddMemberAsync(team, "admin");
        var a1 = await h.AddMemberAsync(team);
        var a2 = await h.AddDeviceAsync(team, a1.MemberId);
        var b = await h.AddMemberAsync(team);
        var group = await h.AddGroupAsync(team, "team", [admin, a1, a2, b], epoch: 3);
        foreach (var d in new[] { admin, a1, a2, b }) await h.LoginAsync(d);
        return new World(h, team, admin, a1, a2, b, group);
    }

    [Fact]
    public async Task Op_is_pending_for_unblocked_leaves_except_sender_and_sync_shows_only_own_rows()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        var r = await w.H.PutOpAsync(w.A1, w.Group, 3, [1, 2, 3]);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var put = (await r.Content.ReadFromJsonAsync<OpPutResponse>())!;
        Assert.Equal(3, put.Recipients); // admin, A2 (own other device), B — not the sending device

        var pending = await w.H.ScalarAsync<Guid[]>("SELECT pending_device_ids FROM delivery");
        Assert.Equal(new[] { w.Admin.DeviceId, w.A2.DeviceId, w.B.DeviceId }.Order(), pending.Order());

        var syncB = await w.H.SyncAsync(w.B);
        Assert.Single(syncB.Ops);
        Assert.Equal(new byte[] { 1, 2, 3 }, syncB.Ops[0].Ciphertext);
        var syncA1 = await w.H.SyncAsync(w.A1);
        Assert.Empty(syncA1.Ops); // the sender is not pending
    }

    [Fact]
    public async Task Wrong_epoch_is_409_and_closed_generation_is_409_closed_generation()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        var r = await w.H.PutOpAsync(w.A1, w.Group, 2, [1]);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(Reasons.EpochConflict, await ServerHarness.ReasonAsync(r));

        var pair = await w.H.AddGroupAsync(w.Team, "pair", [w.A1, w.A2, w.B], epoch: 1, memberA: w.A1.MemberId, memberB: w.B.MemberId);
        await w.H.ExecAsync("UPDATE groups SET closed_at = now() WHERE group_id = @g", ("g", pair));
        r = await w.H.PutOpAsync(w.A1, pair, 1, [1]);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(Reasons.ClosedGeneration, await ServerHarness.ReasonAsync(r));
        Assert.Equal(0L, await w.H.ScalarAsync<long>("SELECT count(*) FROM delivery"));
    }

    [Fact]
    public async Task Non_leaf_device_cannot_post_and_replays_are_deduplicated_by_sender_and_op_id()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        var outsider = await w.H.AddMemberAsync(w.Team);
        await w.H.LoginAsync(outsider);
        var r = await w.H.PutOpAsync(outsider, w.Group, 3, [9]);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(Reasons.NotMember, await ServerHarness.ReasonAsync(r));

        var op = Guid.CreateVersion7();
        var first = (await (await w.H.PutOpAsync(w.A1, w.Group, 3, [7], op)).Content.ReadFromJsonAsync<OpPutResponse>())!;
        var again = (await (await w.H.PutOpAsync(w.A1, w.Group, 3, [7], op)).Content.ReadFromJsonAsync<OpPutResponse>())!;
        Assert.False(first.Duplicate);
        Assert.True(again.Duplicate);
        Assert.Equal(first.Seq, again.Seq);
        r = await w.H.PutOpAsync(w.A1, w.Group, 3, [8], op); // same opId, other content
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(Reasons.OpIdReused, await ServerHarness.ReasonAsync(r));

        // Another sender may reuse the same opId: the key is (groupId, senderDeviceId, opId).
        Assert.Equal(HttpStatusCode.OK, (await w.H.PutOpAsync(w.B, w.Group, 3, [7], op)).StatusCode);
        Assert.Equal(2L, await w.H.ScalarAsync<long>("SELECT count(*) FROM delivery"));
    }

    [Fact]
    public async Task Ack_uses_only_the_token_device_and_row_disappears_after_all_acks()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        var op = Guid.CreateVersion7();
        await w.H.PutOpAsync(w.A1, w.Group, 3, [5], op);
        var url = $"/v1/groups/{Ids.Hex(w.Group)}/ops/ack";
        var ack = new AckRequest([new AckItem(w.A1.DeviceId, op)]);

        // The sender itself is not pending: its ack is a no-op and cannot "extinguish" the row for others.
        using (var c = w.H.Client(w.A1))
            Assert.Equal(0, (await (await c.PostAsJsonAsync(url, ack)).Content.ReadFromJsonAsync<AckResponse>())!.Acked);
        using (var c = w.H.Client())
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync(url, ack)).StatusCode);

        foreach (var d in new[] { w.Admin, w.A2 })
        {
            using var c = w.H.Client(d);
            Assert.Equal(1, (await (await c.PostAsJsonAsync(url, ack)).Content.ReadFromJsonAsync<AckResponse>())!.Acked);
        }
        var pending = await w.H.ScalarAsync<Guid[]>("SELECT pending_device_ids FROM delivery");
        Assert.Equal([w.B.DeviceId], pending);
        Assert.Single((await w.H.SyncAsync(w.B)).Ops);
        Assert.Empty((await w.H.SyncAsync(w.A2)).Ops);

        using (var c = w.H.Client(w.B))
        {
            var res = (await (await c.PostAsJsonAsync(url, ack)).Content.ReadFromJsonAsync<AckResponse>())!;
            Assert.Equal(1, res.Deleted);
        }
        Assert.Equal(0L, await w.H.ScalarAsync<long>("SELECT count(*) FROM delivery"));
    }

    [Fact]
    public async Task Sync_cursor_pages_and_unacked_rows_are_redelivered_and_foreign_cursor_is_410()
    {
        var w = await WorldAsync(s => s["Ombra:Limits:SyncMaxOps"] = "2");
        await using var _ = w.H;
        for (var i = 0; i < 5; i++) await w.H.PutOpAsync(w.A1, w.Group, 3, [(byte)i]);
        var p1 = await w.H.SyncAsync(w.B);
        Assert.True(p1.More);
        var p2 = await w.H.SyncAsync(w.B, p1.Cursor);
        var p3 = await w.H.SyncAsync(w.B, p2.Cursor);
        Assert.False(p3.More);
        Assert.Equal(5, p1.Ops.Count + p2.Ops.Count + p3.Ops.Count);
        var again = await w.H.SyncAsync(w.B, p3.Cursor); // nothing acked → next pass starts over
        Assert.Equal(p1.Ops.Select(o => o.Seq), again.Ops.Select(o => o.Seq));

        using var c = w.H.Client(w.B);
        var foreign = await c.GetAsync("/v1/sync?cursor=v1." + Guid.NewGuid().ToString("N") + ".0.0");
        Assert.Equal(HttpStatusCode.Gone, foreign.StatusCode);
        Assert.True((await foreign.Content.ReadFromJsonAsync<SyncResponse>())!.Snapshot);
    }

    [Fact]
    public async Task Queue_rows_expire_after_30_days_without_ack()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        await w.H.PutOpAsync(w.A1, w.Group, 3, [1]);
        var retention = w.H.Service<RetentionService>();
        await retention.RunOnceAsync(w.H.Clock.GetUtcNow().AddDays(29));
        Assert.Equal(1L, await w.H.ScalarAsync<long>("SELECT count(*) FROM delivery"));
        await retention.RunOnceAsync(w.H.Clock.GetUtcNow().AddDays(30).AddSeconds(1));
        Assert.Equal(0L, await w.H.ScalarAsync<long>("SELECT count(*) FROM delivery"));
    }

    [Fact]
    public async Task Blocked_member_is_purged_from_pending_and_gets_403_removed()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        await w.H.PutOpAsync(w.A1, w.Group, 3, [1]);
        var second = Guid.CreateVersion7();
        await w.H.PutOpAsync(w.A1, w.Group, 3, [2], second);
        foreach (var d in new[] { w.Admin, w.A2 }) // the second row now waits only for B
        {
            using var c0 = w.H.Client(d);
            await c0.PostAsJsonAsync($"/v1/groups/{Ids.Hex(w.Group)}/ops/ack", new AckRequest([new AckItem(w.A1.DeviceId, second)]));
        }
        var revocation = w.H.Service<RevocationService>();
        var res = await revocation.BlockAsync(new RevocationRequest(RevocationKind.MemberRemove, w.Team, w.B.MemberId, "operator", "test", null));
        Assert.Equal([w.B.DeviceId], res.Devices);
        Assert.Equal(1, res.DeliveryRowsDeleted);
        Assert.Equal(1L, await w.H.ScalarAsync<long>("SELECT count(*) FROM delivery"));
        var pendings = await w.H.ScalarAsync<long>("SELECT count(*) FROM delivery WHERE pending_device_ids @> ARRAY[@d]::uuid[]", ("d", w.B.DeviceId));
        Assert.Equal(0L, pendings);
        using var c = w.H.Client(w.B);
        var sync = await c.GetAsync("/v1/sync");
        Assert.Equal(HttpStatusCode.Unauthorized, sync.StatusCode); // sessions were revoked with the block
        var hello = await w.H.HelloAsync(w.B);
        Assert.Equal(HttpStatusCode.Forbidden, hello.StatusCode);
        Assert.Equal(Reasons.Removed, await ServerHarness.ReasonAsync(hello));
        var r = await w.H.PutOpAsync(w.A1, w.Group, 3, [3]);
        Assert.Equal(2, (await r.Content.ReadFromJsonAsync<OpPutResponse>())!.Recipients);
    }

    /// <summary>R5-06: parallel POST /ops and device block, 1000 repetitions — never a blocked device in pending.</summary>
    [Fact]
    public async Task Parallel_ops_and_device_block_never_leave_blocked_device_pending_1000x()
    {
        var w = await WorldAsync(s => s["Ombra:Limits:DeviceOpsBurst"] = "1000000");
        await using var _ = w.H;
        var revocation = w.H.Service<RevocationService>();
        using var sender = w.H.Client(w.A1);
        var violations = 0;
        for (var i = 0; i < 1000; i++)
        {
            var victim = await w.H.AddDeviceAsync(w.Team, w.B.MemberId);
            await w.H.AddLeafAsync(w.Group, victim);
            var ops = Enumerable.Range(0, 3).Select(_ => w.H.PutOpAsync(w.A1, w.Group, 3, RandomNumberGenerator.GetBytes(16), client: sender)).ToList();
            var block = revocation.BlockAsync(new RevocationRequest(RevocationKind.DeviceRevoke, w.Team, victim.DeviceId, "admin", "test", null));
            await Task.WhenAll(ops.Cast<Task>().Append(block));
            foreach (var o in ops) Assert.Equal(HttpStatusCode.OK, (await o).StatusCode);
            violations += (int)await w.H.ScalarAsync<long>("SELECT count(*) FROM delivery WHERE pending_device_ids @> ARRAY[@d]::uuid[]", ("d", victim.DeviceId));
        }
        Assert.Equal(0, violations);
        Assert.Equal(3000L, await w.H.ScalarAsync<long>("SELECT count(*) FROM delivery"));
    }

    [Fact]
    public async Task Sender_quota_429_only_for_that_member_and_evicts_only_its_own_oldest_ops()
    {
        // share = max(MemberQueueMinBytes, TeamQueueBytes / members) = max(1000, 2000/4) = 1000 bytes per member
        var w = await WorldAsync(s =>
        {
            s["Ombra:Limits:TeamQueueBytes"] = "2000";
            s["Ombra:Limits:MemberQueueMinBytes"] = "1000";
        });
        await using var _ = w.H;
        var a3 = await w.H.AddDeviceAsync(w.Team, w.A1.MemberId);
        await w.H.AddLeafAsync(w.Group, a3);
        await w.H.LoginAsync(a3);
        var other = await w.H.PutOpAsync(w.B, w.Group, 3, new byte[300]); // another member's op must survive
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        var devices = new[] { w.A1, w.A2, a3 };
        var seqs = new List<long>();
        foreach (var d in devices) // 3 × 300 = 900 ≤ 1000
        {
            var r = await w.H.PutOpAsync(d, w.Group, 3, new byte[300]);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            seqs.Add((await r.Content.ReadFromJsonAsync<OpPutResponse>())!.Seq);
        }
        var fourth = Guid.CreateVersion7();
        var over = await w.H.PutOpAsync(w.A1, w.Group, 3, new byte[300], fourth); // 1200 > 1000
        Assert.Equal((HttpStatusCode)429, over.StatusCode);
        Assert.Equal(Reasons.SenderQuota, await ServerHarness.ReasonAsync(over));

        var left = await w.H.ScalarAsync<long[]>("SELECT coalesce(array_agg(seq ORDER BY seq), '{}') FROM delivery WHERE sender_member_id = @m", ("m", w.A1.MemberId));
        Assert.Equal(seqs.Skip(1), left); // only its own oldest op was evicted
        Assert.Equal(1L, await w.H.ScalarAsync<long>("SELECT count(*) FROM delivery WHERE sender_member_id = @m", ("m", w.B.MemberId)));

        Assert.Equal(HttpStatusCode.OK, (await w.H.PutOpAsync(w.B, w.Group, 3, new byte[300])).StatusCode); // others unaffected
        Assert.Equal(HttpStatusCode.OK, (await w.H.PutOpAsync(w.A1, w.Group, 3, new byte[300], fourth)).StatusCode); // retry fits now
    }

    [Fact]
    public async Task Device_rate_limit_answers_429_with_retry_after()
    {
        var w = await WorldAsync(s => s["Ombra:Limits:DeviceOpsBurst"] = "3");
        await using var _ = w.H;
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await w.H.PutOpAsync(w.A1, w.Group, 3, [1])).StatusCode);
        var r = await w.H.PutOpAsync(w.A1, w.Group, 3, [1]);
        Assert.Equal((HttpStatusCode)429, r.StatusCode);
        Assert.Equal(Reasons.RateLimited, await ServerHarness.ReasonAsync(r));
        Assert.True(r.Headers.RetryAfter is not null);
        Assert.Equal(HttpStatusCode.OK, (await w.H.PutOpAsync(w.B, w.Group, 3, [1])).StatusCode);
    }

    [Fact]
    public async Task Oversized_op_is_413_and_ephemeral_op_is_not_stored()
    {
        var w = await WorldAsync();
        await using var _ = w.H;
        var big = await w.H.PutOpAsync(w.A1, w.Group, 3, new byte[64 * 1024 + 1]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        var eph = await w.H.PutOpAsync(w.A1, w.Group, 3, [1, 2], ephemeral: true);
        Assert.Equal(HttpStatusCode.OK, eph.StatusCode);
        Assert.Equal(0L, await w.H.ScalarAsync<long>("SELECT count(*) FROM delivery"));
    }
}
