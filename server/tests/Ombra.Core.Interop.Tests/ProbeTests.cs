using System.Text.Json.Nodes;
using Ombra.Core.Interop;
using static Ombra.Core.Interop.Tests.H;

namespace Ombra.Core.Interop.Tests;

/// <summary>
/// The 38 checks of the D1 probe (.temp/D1Design/MlsProbe) on the product crate: real signed roster
/// chain instead of a roster string, every device state reopened from its encrypted file before every
/// call (<c>RestartEachCall</c>). Check numbers in comments follow the probe's output order.
/// One deviation, by design revision 6 (R5-02): removing a *revoked* device of another member is now
/// allowed to any leaf; check 30 therefore tests removal of another member's *active* device.
/// </summary>
public sealed class ProbeTests
{
    private static void Check(bool ok, string what) => Assert.True(ok, what);

    private static JsonObject Ok(JsonObject r, string what)
    {
        Assert.True(r["ok"].B(), $"{what}: {r["code"]} {r["error"]}");
        return (JsonObject)r["result"]!;
    }

    [Fact]
    public void Lifecycle_checks_01_to_15()
    {
        using var w = new World(restartEachCall: true);
        var alice = w.Device("alice"); var bob = w.Device("bob"); var charlie = w.Device("charlie"); var dave = w.Device("dave");
        w.CreateTeam(alice);
        foreach (var d in new[] { bob, charlie, dave }) w.AddMember(alice, d);
        var g = w.CreateGroup(alice, w.Team());
        w.Add(alice, g, bob);
        var add = alice.Call("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(charlie.KeyPackage()), ["now"] = Now });
        var pb = Ok(bob.Process(g, add["commit"].S()), "bob merge");
        charlie.Call("group.join", new() { ["welcome"] = add["welcome"].S() });
        w.Groups[g].Add(charlie);
        Check(pb["kind"].S() == "commit", "01 bob merges admin Add(charlie) commit");
        Check(alice.Epoch(g) == bob.Epoch(g) && bob.Epoch(g) == charlie.Epoch(g) && alice.Members(g) == 3, "02 3 members, same epoch");

        var m1 = alice.Send(g, "item.create", Link(w.TeamId, "hello team"), itemId: Rand(16))["message"].S();
        var rb = Ok(bob.Process(g, m1), "bob decrypt"); var rc = Ok(charlie.Process(g, m1), "charlie decrypt");
        Check(rb["outcome"].S() == "applied" && rc["outcome"].S() == "applied", "03 bob+charlie decrypt and apply app message");
        Check(alice.Export(g) == bob.Export(g), "04 exporter secret equal for members");
        var exA = alice.Export(g);

        w.Update(alice, new() { ["removeMembers"] = Arr(bob.MemberId) });
        var c3 = alice.Call("group.remove", new() { ["groupId"] = g, ["deviceIds"] = Arr(bob.DeviceId) })["commit"].S();
        var cc = Ok(charlie.Process(g, c3), "charlie merge remove");
        Check(cc["kind"].S() == "commit" && charlie.Epoch(g) == alice.Epoch(g) && alice.Members(g) == 2, "05 charlie merges Remove(bob)");
        var bb = Ok(bob.Process(g, c3), "bob learns");
        Check(bb["kind"].S() == "removed", "06 bob learns he was removed");
        w.Groups[g].Remove(bob);

        var m2 = alice.Send(g, "item.create", Link(w.TeamId, "after removal"), itemId: Rand(16))["message"].S();
        Check(charlie.Process(g, m2)["ok"].B(), "07 charlie decrypts post-removal message");
        var r2b = bob.Process(g, m2);
        Check(!r2b["ok"].B(), $"08 removed bob cannot decrypt: {r2b["code"]} {r2b["error"]}");
        var exA2 = alice.Export(g);

        w.Add(alice, g, dave);
        w.Update(alice, new() { ["removeMembers"] = Arr(dave.MemberId) });
        var c5 = alice.Call("group.remove", new() { ["groupId"] = g, ["deviceIds"] = Arr(dave.DeviceId) })["commit"].S();
        Check(charlie.Process(g, c5)["ok"].B(), "09 charlie merges Remove(dave)");
        var m3 = alice.Send(g, "item.create", Link(w.TeamId, "after dave removal"), itemId: Rand(16))["message"].S();
        var r3d = dave.Process(g, m3);
        Check(!r3d["ok"].B() && r3d["code"].S() == "mls", $"10 dave with stale state cannot decrypt: {r3d["error"]}");
        Check(charlie.Process(g, m3)["ok"].B(), "11 charlie still decrypts");
        Check(exA2 != exA, "12 exporter secret rotated");

        // Mallory claims alice's deviceId with its own keys and builds a look-alike group.
        var mallory = w.Device("mallory", alice.MemberId, alice.DeviceId);
        mallory.Call("attack.create_group", new() { ["groupId"] = g, ["descriptor"] = World.DescBytes(w.Team()), ["rosterHash"] = w.Head(alice).hash });
        var forged = mallory.Call("attack.encrypt", new() { ["groupId"] = g, ["plaintext"] = "00" })["message"].S();
        var rf = charlie.Process(g, forged);
        Check(!rf["ok"].B(), $"13 charlie rejects forged message from non-member keys: {rf["code"]} {rf["error"]}");

        var evil = charlie.Call("attack.commit", new() { ["groupId"] = g, ["removeDevices"] = Arr(alice.DeviceId) })["commit"].S();
        var ra = alice.Process(g, evil);
        Check(ra["code"]?.S() == "commit-rejected", $"14 alice rejects Remove commit from non-admin charlie: {ra["error"]}");
        Check(alice.Members(g) == 2, "15 alice group unchanged");
    }

    /// <summary>alice (owner), bob with two devices, charlie (member), all in the team group.</summary>
    private static (World w, Dev alice, Dev bob1, Dev bob2, Dev charlie, string g) Fixture()
    {
        var w = new World(restartEachCall: true);
        var alice = w.Device("alice"); var bob1 = w.Device("bob1"); var bob2 = w.Device("bob2", bob1.MemberId); var charlie = w.Device("charlie");
        w.CreateTeam(alice);
        w.AddMember(alice, bob1, "member", bob2);
        w.AddMember(alice, charlie);
        var g = w.CreateGroup(alice, w.Team());
        w.Add(alice, g, bob1, bob2, charlie);
        return (w, alice, bob1, bob2, charlie, g);
    }

    [Fact]
    public void SD01_server_substituted_key_package_checks_16_17()
    {
        var (w, alice, _, _, _, g) = Fixture();
        using var _w = w;
        var erin = w.Device("erin");
        var erinFake = w.Device("erin-fake", erin.MemberId, erin.DeviceId);
        w.AddMember(alice, erin);
        var rc = alice.Try("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(erinFake.KeyPackage()), ["now"] = Now });
        Check(rc == "add-rejected", $"16 admin refuses Add of server-substituted KeyPackage: {rc}");
        var ok = alice.Call("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(erin.KeyPackage()), ["now"] = Now });
        var rj = erin.Try("group.join", new() { ["welcome"] = ok["welcome"].S() });
        Check(rj == "ok", $"17 KeyPackage matching the roster key is added, invitee joins ({rj})");
    }

    [Fact]
    public void SD03_welcome_checks_18_19()
    {
        var (w, alice, _, _, charlie, g) = Fixture();
        using var _w = w;
        var eve = w.Device("eve");
        w.AddMember(alice, eve);
        // Non-admin charlie builds a Welcome (bypassing his own client's checks).
        var wEvil = charlie.Call("attack.commit", new() { ["groupId"] = g, ["adds"] = Arr(eve.KeyPackage()) })["welcome"].S();
        var rj = eve.Try("group.join", new() { ["welcome"] = wEvil });
        Check(rj == "welcome-rejected", $"18 invitee rejects Welcome from non-admin charlie: {rj}");

        // The operator runs its own team (own genesis) on the same server and lists the victim's real keys;
        // the victim trusts only the genesis from its invite link (alice's team).
        var victim = w.Device("victim");
        w.AddMember(alice, victim);
        using var ow = new World(restartEachCall: true, serverId: w.ServerId);
        var op = ow.Device("operator");
        ow.CreateTeam(op);
        ow.Update(op, new() { ["addMembers"] = new JsonArray(new JsonObject { ["memberId"] = victim.MemberId, ["certs"] = Arr(victim.SelfCert()) }) });
        var og = ow.CreateGroup(op, ow.Team());
        var fake = op.Call("group.add", new() { ["groupId"] = og, ["keyPackages"] = Arr(victim.KeyPackage()), ["now"] = Now })["welcome"].S();
        var rf = victim.Try("group.join", new() { ["welcome"] = fake });
        Check(rf == "welcome-rejected", $"19 invitee rejects Welcome into server-made team (roster/genesis != invite): {rf}");
    }

    [Fact]
    public void SD04a_external_commit_checks_20_21()
    {
        var (w, alice, _, _, charlie, g) = Fixture();
        using var _w = w;
        var mallory = w.Device("mallory");
        var gi = alice.Call("attack.group_info", new() { ["groupId"] = g })["groupInfo"].S();
        var ext = mallory.Call("attack.external_join", new() { ["groupInfo"] = gi })["commit"].S();
        var r = charlie.Process(g, ext);
        Check(r["code"]?.S() == "commit-rejected", $"20 member rejects external commit: {r["code"]} {r["error"]}");
        w.AddMember(alice, mallory);
        var r2 = alice.Process(g, ext);
        Check(!r2["ok"].B(), $"21 external commit rejected even with joiner's key in roster: {r2["code"]} {r2["error"]}");
    }

    [Fact]
    public void SD04b_group_context_extensions_checks_22_to_24()
    {
        var (w, alice, bob1, _, charlie, g) = Fixture();
        using var _w = w;
        // A verified next roster version exists; non-admin charlie moves the group to it.
        w.Update(alice, new() { ["pins"] = Arr(Rand(32)) });
        var next = w.Head(alice).hash;
        var r = alice.Process(g, charlie.Call("attack.commit", new() { ["groupId"] = g, ["rosterHash"] = next })["commit"].S());
        Check(r["code"]?.S() == "commit-rejected", $"22 GroupContextExtensions from non-admin rejected: {r["error"]}");

        // Admin GCE to a hash that is not a verified roster version: not applied (client must fetch, cannot verify).
        var forged = Rand(32);
        var e0 = bob1.Epoch(g);
        var bad = alice.Call("attack.commit", new() { ["groupId"] = g, ["rosterHash"] = forged })["commit"].S();
        var rb = bob1.Process(g, bad);
        Check(!rb["ok"].B() && rb["code"].S() == "need-roster" && bob1.Epoch(g) == e0, $"23 admin GCE with unverified hash rejected: {rb["code"]}");

        var (w3, alice3, bob3, _, _, g3) = Fixture();
        using var _w3 = w3;
        w3.Update(alice3, new() { ["pins"] = Arr(Rand(32)) });
        var c = alice3.Call("group.update", new() { ["groupId"] = g3 })["commit"].S(); // moves GC to the verified head
        var ok = bob3.Process(g3, c);
        Check(ok["ok"].B() && ok["result"]!["rosterVersion"].U() == w3.Head(alice3).version, $"24 admin GCE with verified next roster hash accepted: {ok["error"]}");
    }

    [Fact]
    public void SD04c_update_path_checks_25_to_27()
    {
        var (w, alice, _, _, charlie, g) = Fixture();
        using var _w = w;
        var ok = alice.Process(g, charlie.Call("group.update", new() { ["groupId"] = g })["commit"].S());
        Check(ok["ok"].B(), $"25 plain key refresh by member accepted: {ok["error"]}");
        var r1 = alice.Process(g, charlie.Call("attack.commit", new() { ["groupId"] = g, ["updateMode"] = 1 })["commit"].S());
        Check(r1["code"]?.S() == "commit-rejected", $"26 UpdatePath with signature key not in roster rejected: {r1["error"]}");
        var (w2, alice2, _, _, charlie2, g2) = Fixture();
        using var _w2 = w2;
        var r2 = alice2.Process(g2, charlie2.Call("attack.commit", new() { ["groupId"] = g2, ["updateMode"] = 2, ["newId"] = alice2.DeviceId })["commit"].S());
        Check(r2["code"]?.S() == "commit-rejected", $"27 UpdatePath changing credential to admin's deviceId rejected: {r2["error"]}");
    }

    [Fact]
    public void SD15_own_lost_device_checks_28_to_30()
    {
        var (w, alice, bob1, bob2, charlie, g) = Fixture();
        using var _w = w;
        w.Update(bob1, new() { ["revokeDevices"] = new JsonArray(new JsonObject { ["deviceId"] = bob2.DeviceId }) });
        var c = bob1.Call("group.remove", new() { ["groupId"] = g, ["deviceIds"] = Arr(bob2.DeviceId) })["commit"].S();
        var ra = alice.Process(g, c); var rc = charlie.Process(g, c); var rb2 = bob2.Process(g, c);
        Check(ra["ok"].B() && rc["ok"].B() && rb2["result"]?["kind"]?.S() == "removed", "28 bob1 removes own lost bob2: accepted, bob2 evicted");
        var m = alice.Send(g, "item.create", Link(w.TeamId, "after bob2 lost"), itemId: Rand(16))["message"].S();
        Check(!bob2.Process(g, m)["ok"].B() && bob1.Process(g, m)["ok"].B(), "29 lost device cannot decrypt, bob1 can");
        var (w2, alice2, bob1b, _, charlie2, g2) = Fixture();
        using var _w2 = w2;
        var r = alice2.Process(g2, bob1b.Call("attack.commit", new() { ["groupId"] = g2, ["removeDevices"] = Arr(charlie2.DeviceId) })["commit"].S());
        Check(r["code"]?.S() == "commit-rejected", $"30 member removing another member's (active) device rejected: {r["error"]}");
    }

    [Fact]
    public void R2_01_stale_roster_prefix_checks_31_to_33()
    {
        // 31: X admin in v2, ivy added in v3, X removed in v4; invite pinned to v4. X (who never applied v4)
        // builds a team group on v3 and invites ivy.
        using (var w = new World(restartEachCall: true))
        {
            var alice = w.Device("alice"); var x = w.Device("xadm"); var ivy = w.Device("ivy");
            w.CreateTeam(alice);
            w.AddMember(alice, x, "admin");
            // ivy is listed in v3, but receives her invite (and the chain) only at v4.
            w.Update(alice, new() { ["addMembers"] = new JsonArray(new JsonObject { ["memberId"] = ivy.MemberId, ["certs"] = Arr(ivy.SelfCert()) }) });
            w.Update(alice, new() { ["removeMembers"] = Arr(x.MemberId) }, skip: x);
            w.Import(ivy, alice);
            var gx = x.Call("group.create", new() { ["descriptor"] = w.Team() })["groupId"].S();
            var wx = x.Call("group.add", new() { ["groupId"] = gx, ["keyPackages"] = Arr(ivy.KeyPackage()), ["now"] = Now })["welcome"].S();
            var r1 = ivy.Try("group.join", new() { ["welcome"] = wx });
            Check(r1 == "welcome-rejected", $"31 Welcome from admin removed before the invite's rosterHead rejected: {r1}");
        }
        // 32: still-admin alice, but the Welcome is built on v2 while the invite pins v3.
        using (var w = new World(restartEachCall: true))
        {
            var alice = w.Device("alice"); var ivy = w.Device("ivy");
            w.CreateTeam(alice);
            var certs = Arr(ivy.SelfCert());
            w.Update(alice, new() { ["addMembers"] = new JsonArray(new JsonObject { ["memberId"] = ivy.MemberId, ["certs"] = certs }) });
            var g = w.CreateGroup(alice, w.Team());
            var v3 = alice.Call("roster.update", new() { ["teamId"] = w.TeamId, ["now"] = Now, ["pins"] = Arr(Rand(32)) })["update"].S();
            var stale = alice.Call("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(ivy.KeyPackage()), ["now"] = Now })["welcome"].S();
            w.Publish(v3);
            w.Import(ivy, alice);
            var r2 = ivy.Try("group.join", new() { ["welcome"] = stale });
            Check(r2 == "welcome-rejected", $"32 Welcome whose roster hash is older than rosterHead rejected: {r2}");
            var fresh = alice.Call("group.update", new() { ["groupId"] = g });
            _ = fresh;
        }
        // 33: admin at rosterHead with head hash.
        using (var w = new World(restartEachCall: true))
        {
            var alice = w.Device("alice"); var ivy = w.Device("ivy");
            w.CreateTeam(alice);
            w.AddMember(alice, ivy);
            var g = w.CreateGroup(alice, w.Team());
            var wok = alice.Call("group.add", new() { ["groupId"] = g, ["keyPackages"] = Arr(ivy.KeyPackage()), ["now"] = Now })["welcome"].S();
            var r3 = ivy.Try("group.join", new() { ["welcome"] = wok });
            Check(r3 == "ok", $"33 Welcome from admin at rosterHead accepted: {r3}");
        }
    }

    [Fact]
    public void R2_12_leaf_invariant_checks_34_to_38()
    {
        var (w, alice, bob1, _, charlie, g) = Fixture();
        using var _w = w;
        var r = charlie.Process(g, alice.Call("attack.commit", new() { ["groupId"] = g, ["removeDevices"] = Arr(bob1.DeviceId) })["commit"].S());
        Check(r["code"]?.S() == "commit-rejected", $"34 admin Remove of a device still active in roster rejected: {r["error"]}");

        using var pw = new World(restartEachCall: true);
        var pa = pw.Device("pa"); var pb = pw.Device("pb"); var pc = pw.Device("pc");
        pw.CreateTeam(pa);
        pw.AddMember(pa, pb);
        pw.AddMember(pa, pc);
        var project = Rand(16);
        pw.Update(pa, new() { ["grantAccess"] = new JsonArray(
            new JsonObject { ["projectId"] = project, ["memberId"] = pa.MemberId },
            new JsonObject { ["projectId"] = project, ["memberId"] = pc.MemberId }) });
        var pg = pw.CreateGroup(pa, pw.Project(project));
        pw.Add(pa, pg, pc);
        var rcAdm = pa.Try("group.add", new() { ["groupId"] = pg, ["keyPackages"] = Arr(pb.KeyPackage()), ["now"] = Now });
        Check(rcAdm == "add-rejected", $"35 admin client refuses Add of device without project access: {rcAdm}");
        var cBad = pa.Call("attack.commit", new() { ["groupId"] = pg, ["adds"] = Arr(pb.KeyPackage()) })["commit"].S();
        var rp = pc.Process(pg, cBad);
        Check(rp["code"]?.S() == "commit-rejected", $"36 member rejects Add into project group of device outside project scope: {rp["error"]}");

        var (w4, a4, b41, b42, c4, g4) = Fixture();
        using var _w4 = w4;
        w4.Update(b41, new() { ["revokeDevices"] = new JsonArray(new JsonObject { ["deviceId"] = b42.DeviceId }) });
        Check(c4.TreeState(g4) == 2, "37 roster revoked a device still in the tree -> pending rotation (hold)");
        var rot = a4.Call("group.rotate", new() { ["groupId"] = g4 })["commit"].S();
        var rr = c4.Process(g4, rot);
        Check(rr["ok"].B() && c4.TreeState(g4) == 0, $"38 after rotation tree == scope roster: {rr["error"]}");
    }
}
