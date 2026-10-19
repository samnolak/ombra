using System.Text.Json.Nodes;
using static Ombra.Core.Interop.Tests.H;

namespace Ombra.Core.Interop.Tests;

/// <summary>
/// SD-16 / R2-07 (names.rs): member and device names travel only in the team MLS group; a received privatePart is
/// kept only when it matches a verified roster's privateCommit; nobody renames another member.
/// </summary>
public sealed class NamesTests
{
    private sealed class NamesWorld : IDisposable
    {
        public readonly World W = new();
        public readonly Dev O, A, A2;
        public readonly string G;

        public NamesWorld()
        {
            O = W.Device("O"); A = W.Device("A"); A2 = W.Device("A2", A.MemberId);
            W.CreateTeam(O);
            W.AddMember(O, A, "member", A2);
            G = W.CreateGroup(O, W.Team());
            W.Add(O, G, A, A2);
        }

        public JsonNode Names(Dev d) => d.Call("names.get", new() { ["teamId"] = W.TeamId });

        /// <summary>The UI's order: names.set → roster.update with privateCommit → names.seal → the group gets it.</summary>
        public void Rename(Dev d, string? name, JsonObject? devices = null, params Dev[] receivers)
        {
            var a = new JsonObject { ["teamId"] = W.TeamId };
            if (name is not null) a["name"] = name;
            if (devices is not null) a["devices"] = devices;
            var commit = d.Call("names.set", a)["privateCommit"].S();
            W.Update(d, new() { ["privateCommit"] = commit });
            var msg = d.Call("names.seal", new() { ["groupId"] = G })["message"].S();
            foreach (var r in receivers)
            {
                var res = r.Process(G, msg);
                Assert.True(res["ok"].B(), res.ToJsonString());
                Assert.Equal("names", res["result"]!["kind"].S());
            }
        }

        public void Dispose() => W.Dispose();
    }

    [Fact]
    public void Names_reach_every_member_confirmed_and_merge_per_member()
    {
        using var t = new NamesWorld();
        Assert.True(t.Names(t.A)["confirmed"].B()); // genesis commits to no names
        t.Rename(t.O, "Ольга", null, t.A, t.A2);
        foreach (var d in new[] { t.O, t.A, t.A2 })
        {
            var n = t.Names(d);
            Assert.True(n["confirmed"].B());
            Assert.Equal("Ольга", n["names"]![t.O.MemberId].S());
        }
        t.Rename(t.A, "Max", new JsonObject { [t.A2.DeviceId] = "MacBook" }, t.O, t.A2);
        var o = t.Names(t.O);
        Assert.Equal("Ольга", o["names"]![t.O.MemberId].S());
        Assert.Equal("Max", o["names"]![t.A.MemberId].S());
        Assert.Equal("MacBook", o["devices"]![t.A2.DeviceId].S());
    }

    [Fact]
    public void A_part_not_matching_any_verified_roster_is_not_kept_and_nobody_renames_others()
    {
        using var t = new NamesWorld();
        // A sets a name but the roster update never reaches O: the sealed part matches nothing O verified.
        t.A.Call("names.set", new() { ["teamId"] = t.W.TeamId, ["name"] = "Max" });
        var msg = t.A.Call("names.seal", new() { ["groupId"] = t.G })["message"].S();
        var r = t.O.Process(t.G, msg);
        Assert.False(r["ok"].B());
        Assert.Equal("need-roster", r["code"].S());
        Assert.Empty(t.Names(t.O)["names"]!.AsObject());
        // A's local part does not match the head: its names are unconfirmed and further changes wait.
        Assert.False(t.Names(t.A)["confirmed"].B());
        Assert.Equal("frozen", t.A.Try("names.set", new() { ["teamId"] = t.W.TeamId, ["name"] = "Max 2" }));

        // Device names only for one's own devices; names are checked.
        Assert.Equal("invalid-input", t.O.Try("names.set", new() { ["teamId"] = t.W.TeamId, ["devices"] = new JsonObject { [t.A2.DeviceId] = "x" } }));
        Assert.Equal("invalid-input", t.O.Try("names.set", new() { ["teamId"] = t.W.TeamId, ["name"] = " padded" }));
        Assert.Equal("invalid-input", t.O.Try("names.set", new() { ["teamId"] = t.W.TeamId, ["name"] = new string('x', 65) }));
    }

    [Fact]
    public void Names_are_wiped_with_the_team()
    {
        using var t = new NamesWorld();
        t.Rename(t.O, "Ольга", null, t.A, t.A2);
        t.A.Call("team.forget", new() { ["teamId"] = t.W.TeamId });
        Assert.Equal("not-found", t.A.Try("names.get", new() { ["teamId"] = t.W.TeamId }));
    }
}
