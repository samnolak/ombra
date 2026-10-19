using System.Text.Json.Nodes;
using Ombra.Core.Interop;
using static Ombra.Core.Interop.Tests.H;

namespace Ombra.Core.Interop.Tests;

/// <summary>Owner O (admin), A (A1, A2), B (B1, B2), C — all in the roster; pair A–B with all four devices.</summary>
public sealed class PairWorld : IDisposable
{
    public readonly World W;
    public readonly Dev O, A1, A2, B1, B2, C;
    public readonly string Pair;

    public PairWorld(bool restart = false, bool withPair = true)
    {
        W = new World(restart);
        O = W.Device("O"); A1 = W.Device("A1"); A2 = W.Device("A2", A1.MemberId); B1 = W.Device("B1"); B2 = W.Device("B2", B1.MemberId); C = W.Device("C");
        W.CreateTeam(O);
        W.AddMember(O, A1, "member", A2);
        W.AddMember(O, B1, "member", B2);
        W.AddMember(O, C);
        Pair = withPair ? MakePair(A1, B1, 0, A2, B1, B2) : "";
    }

    public string MakePair(Dev creator, Dev peer, uint gen, params Dev[] joiners)
    {
        var g = W.CreateGroup(creator, W.Pair(creator, peer, gen));
        W.Add(creator, g, joiners);
        return g;
    }

    public void Dispose() => W.Dispose();
}

public sealed class GroupPolicyTests
{
    private static string Rejected(JsonObject r) => r["ok"].B() ? "ok" : r["code"].S();

    [Fact]
    public void R5_01_admin_outside_pair_cannot_be_added_to_pair()
    {
        using var p = new PairWorld();
        // A's own client refuses; a compromised A1 that bypasses it is rejected by every other leaf.
        Assert.Equal("add-rejected", p.A1.Try("group.add", new() { ["groupId"] = p.Pair, ["keyPackages"] = Arr(p.O.KeyPackage()), ["now"] = Now }));
        var bad = p.A1.Call("attack.commit", new() { ["groupId"] = p.Pair, ["adds"] = Arr(p.O.KeyPackage()) })["commit"].S();
        foreach (var d in new[] { p.A2, p.B1, p.B2 }) Assert.Equal("commit-rejected", Rejected(d.Process(p.Pair, bad)));
    }

    [Fact]
    public void R5_01_third_member_cannot_be_added_to_personal_group()
    {
        using var p = new PairWorld(withPair: false);
        var g = p.W.CreateGroup(p.A1, p.W.Personal(p.A1));
        p.W.Add(p.A1, g, p.A2);
        Assert.Equal("add-rejected", p.A1.Try("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(p.C.KeyPackage()), ["now"] = Now }));
        var bad = p.A1.Call("attack.commit", new() { ["groupId"] = g, ["adds"] = Arr(p.C.KeyPackage()) })["commit"].S();
        Assert.Equal("commit-rejected", Rejected(p.A2.Process(g, bad)));
    }

    [Fact]
    public void R5_01_GroupContextExtensions_changing_descriptor_rejected()
    {
        using var p = new PairWorld();
        var other = World.DescBytes(p.W.Pair(p.A1, p.B1, 7));
        var bad = p.A1.Call("attack.commit", new() { ["groupId"] = p.Pair, ["descriptor"] = other })["commit"].S();
        var r = p.B1.Process(p.Pair, bad);
        Assert.Equal("commit-rejected", Rejected(r));
        Assert.Contains("descriptor", r["error"].S());
    }

    [Fact]
    public void R5_01_N2_N10_welcome_into_pair_from_non_member_rejected()
    {
        using var p = new PairWorld(withPair: false);
        // C builds a group under the A–B pair groupId and invites B: B never accepts it.
        var desc = p.W.Pair(p.A1, p.B1);
        var gid = World.Gid(desc);
        p.C.Call("attack.create_group", new() { ["groupId"] = gid, ["descriptor"] = World.DescBytes(desc), ["rosterHash"] = p.W.Head(p.O).hash });
        var w = p.C.Call("attack.commit", new() { ["groupId"] = gid, ["adds"] = Arr(p.B1.KeyPackage()) })["welcome"].S();
        var r = p.B1.Raw("group.join", new() { ["welcome"] = w });
        Assert.Equal("welcome-rejected", Rejected(r));
    }

    [Fact]
    public void R5_02_after_B_removed_A_removes_B_leaves_without_admins()
    {
        using var p = new PairWorld();
        p.W.Update(p.O, new() { ["removeMembers"] = Arr(p.B1.MemberId) });
        // O (only admin) is offline from here on: A rotates the pair itself.
        Assert.Equal(2, p.A2.TreeState(p.Pair));
        var rot = p.A1.Call("group.rotate", new() { ["groupId"] = p.Pair })["commit"].S();
        Assert.Equal("ok", Rejected(p.A2.Process(p.Pair, rot)));
        Assert.Equal("removed", p.B1.Process(p.Pair, rot)["result"]!["kind"].S());
        Assert.Equal(0, p.A2.TreeState(p.Pair));
        Assert.Equal(2, p.A2.Members(p.Pair));
    }

    [Fact]
    public void R5_02_remove_of_active_pair_leaf_rejected()
    {
        using var p = new PairWorld();
        Assert.Equal("commit-rejected", p.A1.Try("group.remove", new() { ["groupId"] = p.Pair, ["deviceIds"] = Arr(p.B2.DeviceId) }));
        var bad = p.A1.Call("attack.commit", new() { ["groupId"] = p.Pair, ["removeDevices"] = Arr(p.B2.DeviceId) })["commit"].S();
        Assert.Equal("commit-rejected", Rejected(p.A2.Process(p.Pair, bad)));
        Assert.Equal("commit-rejected", Rejected(p.B1.Process(p.Pair, bad)));
    }

    [Fact]
    public void N2_simultaneous_pair_creation_loser_discards_own_group_and_joins_by_welcome()
    {
        using var p = new PairWorld(withPair: false);
        var desc = p.W.Pair(p.A1, p.B1);
        var g = p.W.CreateGroup(p.A1, desc);
        Assert.Equal(g, p.B1.Call("group.create", new() { ["descriptor"] = desc.DeepClone() })["groupId"].S());
        // The server took A1's first commit; B1 got 409, so it drops its own group and accepts A1's Welcome.
        var add = p.A1.Call("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(p.B1.KeyPackage()), ["now"] = Now });
        Assert.Equal("welcome-rejected", p.B1.Try("group.join", new() { ["welcome"] = add["welcome"].S() }));
        p.B1.Call("group.discard", new() { ["groupId"] = g });
        Assert.Equal("ok", p.B1.Try("group.join", new() { ["welcome"] = add["welcome"].S() }));
        Assert.Equal(p.A1.Export(g), p.B1.Export(g));
        // A discarded group cannot be re-created under the same groupId, and an old Welcome stays refused.
        Assert.Equal("welcome-rejected", p.B1.Try("group.join", new() { ["welcome"] = add["welcome"].S() }));
    }

    [Fact]
    public void N2_generation_g_plus_1_closes_generation_g_for_both()
    {
        using var p = new PairWorld();
        var g0 = p.Pair;
        var fromB = p.B1.Send(g0, "seq.head", new JsonObject())["message"].S();
        var g1 = p.MakePair(p.A1, p.B1, 1, p.B1);
        Assert.NotEqual(g0, g1);
        var groups = (JsonArray)p.B1.Call("group.list")["groups"]!;
        Assert.True(groups.Single(x => x!["groupId"].S() == g0)!["closed"].B());
        Assert.False(groups.Single(x => x!["groupId"].S() == g1)!["closed"].B());
        Assert.True(((JsonArray)p.A1.Call("group.list")["groups"]!).Single(x => x!["groupId"].S() == g0)!["closed"].B());
        // Operation into the closed generation: sender refuses, receiver refuses.
        Assert.Equal("closed-generation", p.A1.Try("op.send", new() { ["groupId"] = g0, ["opType"] = "seq.head", ["payload"] = new JsonObject(), ["now"] = Now }));
        Assert.Equal("closed-generation", Rejected(p.A1.Process(g0, fromB)));
    }

    [Fact]
    public void N9_N10_welcome_from_frozen_peer_rejected_from_own_other_device_accepted()
    {
        using var p = new PairWorld(withPair: false);
        p.B1.Call("pair.freeze", new() { ["teamId"] = p.W.TeamId, ["memberId"] = p.A1.MemberId });
        var g = p.W.CreateGroup(p.A1, p.W.Pair(p.A1, p.B1));
        var add = p.A1.Call("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(p.B1.KeyPackage()), ["now"] = Now });
        Assert.Equal("welcome-rejected", p.B1.Try("group.join", new() { ["welcome"] = add["welcome"].S() }));
        // B1 creates the pair and adds its own other device B2: accepted (N10).
        p.B1.Call("pair.unfreeze", new() { ["teamId"] = p.W.TeamId, ["memberId"] = p.A1.MemberId });
        using var q = new PairWorld(withPair: false);
        var g2 = q.W.CreateGroup(q.B1, q.W.Pair(q.A1, q.B1));
        var w2 = q.B1.Call("group.add", new() { ["groupId"] = g2, ["keyPackages"] = Arr(q.B2.KeyPackage()), ["now"] = Now })["welcome"].S();
        Assert.Equal("ok", q.B2.Try("group.join", new() { ["welcome"] = w2 }));
        // Frozen peer: no new devices of A are added by B into the pair (N9), and no ops are sent.
        q.B1.Call("pair.freeze", new() { ["teamId"] = q.W.TeamId, ["memberId"] = q.A1.MemberId });
        Assert.Equal("add-rejected", q.B1.Try("group.add", new() { ["groupId"] = g2, ["keyPackages"] = Arr(q.A2.KeyPackage()), ["now"] = Now }));
        Assert.Equal("frozen", q.B1.Try("op.send", new() { ["groupId"] = g2, ["opType"] = "seq.head", ["payload"] = new JsonObject(), ["now"] = Now }));
    }

    [Fact]
    public void R2_14_psk_and_reinit_proposals_rejected()
    {
        using var p = new PairWorld();
        var psk = Rand(16);
        var secret = Rand(32);
        foreach (var d in new[] { p.A1, p.B1 }) d.Call("attack.store_psk", new() { ["pskId"] = psk, ["secret"] = secret });
        var c = p.A1.Call("attack.commit", new() { ["groupId"] = p.Pair, ["pskId"] = psk })["commit"].S();
        var r = p.B1.Process(p.Pair, c);
        Assert.Equal("commit-rejected", Rejected(r));
        Assert.Contains("PreSharedKey", r["error"].S());

        // ReInit: OpenMLS 0.9.1 drops ReInit from its own commit builder (proposal_store.rs, TODO #751), so the
        // insider forges the commit with the frankenstein codec, signed by its real leaf key. The server's public
        // verification (no group secrets, so no membership/confirmation tag check) reaches the proposal whitelist;
        // members refuse it already at the MLS layer (dummy membership tag) and stay on their epoch.
        using var q = new PairWorld(withPair: false);
        byte[]? chain = null;
        foreach (var u in q.W.Updates) chain = OmbraPublic.VerifyRosterAppend(chain, Bytes(u), Bytes(q.W.ServerId), Now).ChainState;
        var gid = q.W.CreateGroup(q.A1, q.W.Pair(q.A1, q.B1));
        var pg = OmbraPublic.PublicGroupInit(chain!, Bytes(q.A1.Call("group.public_info", new() { ["groupId"] = gid })["groupInfo"].S()));
        var add = q.A1.Call("group.add", new() { ["groupId"] = gid, ["keyPackages"] = Arr(q.B1.KeyPackage()), ["now"] = Now });
        pg = OmbraPublic.PublicGroupCommit(chain!, pg.GroupState, Bytes(add["commit"].S()));
        q.B1.Call("group.join", new() { ["welcome"] = add["welcome"].S() });
        var reinit = q.A1.Call("attack.reinit_commit", new() { ["groupId"] = gid })["commit"].S();
        Assert.Equal("PublicMessage", OmbraPublic.InspectMessage(Bytes(reinit))["wireFormat"].S());
        var srv = Assert.Throws<OmbraException>(() => OmbraPublic.PublicGroupCommit(chain!, pg.GroupState, Bytes(reinit)));
        Assert.Equal("commit-rejected", srv.Code);
        Assert.Contains("Reinit", srv.Detail);
        var e0 = q.B1.Epoch(gid);
        Assert.NotEqual("ok", Rejected(q.B1.Process(gid, reinit)));
        Assert.Equal(e0, q.B1.Epoch(gid));
    }

    [Fact]
    public void R2_14_standalone_update_proposal_with_foreign_credential_rejected()
    {
        using var p = new PairWorld();
        var prop = p.B2.Call("attack.propose_update", new() { ["groupId"] = p.Pair, ["newId"] = p.A1.DeviceId })["proposal"].S();
        Assert.Equal("commit-rejected", Rejected(p.A1.Process(p.Pair, prop)));
    }

    [Fact]
    public void R2_14_key_package_lifetime_and_last_resort()
    {
        using var p = new PairWorld(withPair: false);
        var g = p.W.CreateGroup(p.O, p.W.Team());
        Assert.Equal("add-rejected", p.O.Try("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(p.C.KeyPackage(lifetime: 60UL * 24 * 3600)), ["now"] = Now }));
        Assert.Equal("add-rejected", p.O.Try("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(p.C.KeyPackage()), ["now"] = Now + 31UL * 24 * 3600 }));
        var lr = p.C.KeyPackage(lastResort: true);
        Assert.Equal("add-rejected", p.O.Try("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(lr), ["now"] = Now }));
        var add = p.O.Call("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(lr), ["now"] = Now, ["allowLastResort"] = true });
        p.C.Call("group.join", new() { ["welcome"] = add["welcome"].S() });
        Assert.Contains(p.C.DeviceId, ((JsonArray)p.O.Call("group.info", new() { ["groupId"] = g })["weakForwardSecrecy"]!).Select(x => x.S()));
        p.O.Process(g, p.C.Call("group.update", new() { ["groupId"] = g })["commit"].S());
        Assert.Empty((JsonArray)p.O.Call("group.info", new() { ["groupId"] = g })["weakForwardSecrecy"]!);
    }

    [Fact]
    public void R2_14_replayed_old_welcome_rejected()
    {
        using var p = new PairWorld(withPair: false);
        var g = p.W.CreateGroup(p.O, p.W.Team());
        var add = p.O.Call("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(p.C.KeyPackage()), ["now"] = Now });
        Assert.Equal("ok", p.C.Try("group.join", new() { ["welcome"] = add["welcome"].S() }));
        Assert.Equal("welcome-rejected", p.C.Try("group.join", new() { ["welcome"] = add["welcome"].S() }));
    }

    [Fact]
    public void R2_01_chain_without_rosterHead_is_refused_on_import()
    {
        using var w = new World();
        var o = w.Device("o"); var ivy = w.Device("ivy");
        w.CreateTeam(o);
        w.Update(o, new() { ["addMembers"] = new JsonArray(new JsonObject { ["memberId"] = ivy.MemberId, ["certs"] = Arr(ivy.SelfCert()) }) });
        w.Update(o, new() { ["pins"] = Arr(Rand(32)) });
        var head = w.Head(o);
        // The server shows only the prefix up to v2 while the invite link pins v3.
        var r = ivy.Raw("team.import", new()
        {
            ["teamId"] = w.TeamId, ["serverId"] = w.ServerId, ["updates"] = Arr(w.Updates.Take(2).ToArray()), ["now"] = Now,
            ["rosterHead"] = new JsonObject { ["version"] = head.version, ["hash"] = head.hash }, ["inviterFingerprint"] = o.Fingerprint,
        });
        Assert.Equal("roster-rejected", Rejected(r));
        // Wrong genesis (server-made team) for the invite's teamId `t`.
        using var other = new World(serverId: w.ServerId);
        var op = other.Device("op");
        other.CreateTeam(op);
        var r2 = ivy.Raw("team.import", new()
        {
            ["teamId"] = w.TeamId, ["serverId"] = w.ServerId, ["updates"] = Arr(other.Updates.ToArray()), ["now"] = Now,
        });
        Assert.Equal("roster-rejected", Rejected(r2));
    }

    [Fact]
    public void R2_12_leaf_outside_admissible_set_after_merge_rejected()
    {
        // Compromised admin moves the GroupContext to the version that revoked B2 without removing it.
        using var p = new PairWorld(withPair: false);
        var g = p.W.CreateGroup(p.O, p.W.Team());
        p.W.Add(p.O, g, p.B1, p.B2, p.C);
        p.W.Update(p.B1, new() { ["revokeDevices"] = new JsonArray(new JsonObject { ["deviceId"] = p.B2.DeviceId }) });
        var bad = p.O.Call("attack.commit", new() { ["groupId"] = g, ["rosterHash"] = p.W.Head(p.O).hash })["commit"].S();
        var r = p.C.Process(g, bad);
        Assert.Equal("commit-rejected", Rejected(r));
        Assert.Contains("not admissible", r["error"].S());
    }

    [Fact]
    public void Handshake_must_be_public_message()
    {
        using var p = new PairWorld();
        var info = OmbraPublic.InspectMessage(Bytes(p.A1.Call("group.update", new() { ["groupId"] = p.Pair })["commit"].S()));
        Assert.Equal("PublicMessage", info["wireFormat"].S());
    }
}
