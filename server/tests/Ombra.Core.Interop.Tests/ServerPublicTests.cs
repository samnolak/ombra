using System.Text.Json.Nodes;
using Ombra.Core.Interop;
using static Ombra.Core.Interop.Tests.H;

namespace Ombra.Core.Interop.Tests;

/// <summary>Server "public verification" mode: roster chain, KeyPackage inspection, PublicGroup commit policy (N2).</summary>
public sealed class ServerPublicTests
{
    private static byte[] Chain(World w)
    {
        byte[]? st = null;
        foreach (var u in w.Updates)
            st = OmbraPublic.VerifyRosterAppend(st, Bytes(u), Bytes(w.ServerId), Now).ChainState;
        return st!;
    }

    [Fact]
    public void Roster_append_tracks_head_and_rejects_forgery()
    {
        using var w = new World();
        var a = w.Device("a"); var b = w.Device("b");
        w.CreateTeam(a);
        w.AddMember(a, b);
        byte[]? st = null;
        RosterAppendResult? r = null;
        foreach (var u in w.Updates) { r = OmbraPublic.VerifyRosterAppend(st, Bytes(u), Bytes(w.ServerId), Now); st = r.ChainState; }
        Assert.Equal(2UL, r!.Version);
        Assert.Equal(w.TeamId, Hex(r.TeamId!));
        // member b tries to promote itself to admin
        var forged = b.Call("roster.update", new() { ["teamId"] = w.TeamId, ["now"] = Now, ["setRoles"] = new JsonArray(new JsonObject { ["memberId"] = b.MemberId, ["role"] = "admin" }) })["update"].S();
        var ex = Assert.Throws<OmbraException>(() => OmbraPublic.VerifyRosterAppend(st, Bytes(forged), Bytes(w.ServerId), Now));
        Assert.Equal("roster-rejected", ex.Code);
    }

    [Fact]
    public void KeyPackage_inspection_reports_identity_lifetime_and_ref()
    {
        using var w = new World();
        var a = w.Device("a");
        var kp = OmbraPublic.InspectKeyPackage(Bytes(a.KeyPackage(lifetime: 3600)));
        Assert.Equal(a.DeviceId, Hex(kp.DeviceId));
        Assert.Equal(a.MlsKey, Hex(kp.MlsSignatureKey));
        Assert.False(kp.LastResort);
        Assert.InRange(kp.NotAfter - kp.NotBefore, 3600UL, 3600UL + 3600);
        Assert.True(OmbraPublic.InspectKeyPackage(Bytes(a.KeyPackage(lastResort: true))).LastResort);
    }

    [Fact]
    public void N2_server_accepts_pair_only_from_descriptor_members()
    {
        using var w = new World();
        var admin = w.Device("admin"); var a = w.Device("A"); var b = w.Device("B"); var c = w.Device("C");
        w.CreateTeam(admin);
        w.AddMember(admin, a); w.AddMember(admin, b); w.AddMember(admin, c);
        var chain = Chain(w);
        var pair = w.Pair(a, b);
        var gid = w.CreateGroup(a, pair);
        var init = OmbraPublic.PublicGroupInit(chain, Bytes(a.Call("group.public_info", new() { ["groupId"] = gid })["groupInfo"].S()));
        Assert.Equal(gid, Hex(init.GroupId));
        var add = a.Call("group.add", new() { ["groupId"] = gid, ["keyPackages"] = Arr(b.KeyPackage()), ["now"] = Now });
        var after = OmbraPublic.PublicGroupCommit(chain, init.GroupState, Bytes(add["commit"].S()));
        Assert.Equal(1UL, after.Epoch);
        Assert.Equal(b.DeviceId, Hex(after.Added.Single().DeviceId));
        Assert.Equal(2, after.Leaves.Count);

        // C "occupies" the A–B groupId: the server refuses to track it.
        var c2 = w.Device("C-attacker", c.MemberId, c.DeviceId);
        _ = c2;
        c.Call("attack.create_group", new() { ["groupId"] = gid, ["descriptor"] = World.DescBytes(pair), ["rosterHash"] = w.Head(a).hash });
        var cInfo = Bytes(c.Call("group.public_info", new() { ["groupId"] = gid })["groupInfo"].S());
        Assert.Equal("commit-rejected", Assert.Throws<OmbraException>(() => OmbraPublic.PublicGroupInit(chain, cInfo)).Code);

        // A member of the pair tries to add C: rejected by the same policy on the server.
        var bad = a.Call("attack.commit", new() { ["groupId"] = gid, ["adds"] = Arr(c.KeyPackage()) })["commit"].S();
        Assert.Equal("commit-rejected", Assert.Throws<OmbraException>(() => OmbraPublic.PublicGroupCommit(chain, after.GroupState, Bytes(bad))).Code);
    }

    [Fact]
    public void N2_team_and_project_groups_are_registered_only_by_an_admin_creator_at_epoch_0()
    {
        using var w = new World();
        var admin = w.Device("admin"); var m = w.Device("M");
        w.CreateTeam(admin);
        w.AddMember(admin, m);
        var project = Rand(16);
        w.Update(admin, new() { ["grantAccess"] = new JsonArray(new JsonObject { ["projectId"] = project, ["memberId"] = m.MemberId }) });
        var chain = Chain(w);

        // A member squats the deterministic team/project groupIds (client-side admin rule bypassed): refused.
        foreach (var desc in new[] { w.Team(), w.Project(project) })
        {
            var gid = World.Gid(desc);
            m.Call("attack.create_group", new() { ["groupId"] = gid, ["descriptor"] = World.DescBytes(desc), ["rosterHash"] = w.Head(m).hash });
            var squat = Bytes(m.Call("group.public_info", new() { ["groupId"] = gid })["groupInfo"].S());
            var ex = Assert.Throws<OmbraException>(() => OmbraPublic.PublicGroupInit(chain, squat));
            Assert.Equal("commit-rejected", ex.Code);
            Assert.Contains("admin", ex.Detail);
        }

        // The admin's genuine team group: the epoch-0 GroupInfo registers it and names the creator …
        using var w2 = new World();
        var a2 = w2.Device("admin"); var m2 = w2.Device("M");
        w2.CreateTeam(a2);
        w2.AddMember(a2, m2);
        var chain2 = Chain(w2);
        var g = w2.CreateGroup(a2, w2.Team());
        var info0 = Bytes(a2.Call("group.public_info", new() { ["groupId"] = g })["groupInfo"].S());
        var init = OmbraPublic.PublicGroupInit(chain2, info0);
        Assert.Equal(a2.DeviceId, Hex(init.Committer!));
        // … while a later-epoch GroupInfo is not an initial registration.
        w2.Add(a2, g, m2);
        var info1 = Bytes(a2.Call("group.public_info", new() { ["groupId"] = g })["groupInfo"].S());
        Assert.Equal("commit-rejected", Assert.Throws<OmbraException>(() => OmbraPublic.PublicGroupInit(chain2, info1)).Code);
    }
}
