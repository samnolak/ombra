using System.Text.Json.Nodes;
using static Ombra.Core.Interop.Tests.H;

namespace Ombra.Core.Interop.Tests;

/// <summary>
/// design-sync-decision §4–§5 through the C ABI: same-rev edits from two devices of the author converge on the
/// (seq, deviceId) winner in every delivery order and the losing device keeps a copy; redelivery is a duplicate (acked);
/// a final rejection is acked and logged; the same opId with other content is a security event.
/// </summary>
public sealed class SyncRulesTests
{
    private static string Ctx(JsonNode item) => item["context"]!["text"].S();

    [Fact]
    public void Same_rev_from_two_devices_of_the_author_converges_and_the_loser_keeps_a_copy()
    {
        using var t = new ItemWorld(pairs: false);
        var item = Rand(16);
        t.Post(t.A1, t.Team, "item.create", Link(t.TeamId, "v1"), itemId: item);
        var m1 = t.A1.Send(t.Team, "item.edit", ItemWorld.Edit(1, "from A1"), itemId: item)["message"].S();
        var m2 = t.A2.Send(t.Team, "item.edit", ItemWorld.Edit(1, "from A2"), itemId: item, author: t.A1.MemberId)["message"].S();
        // Opposite orders on different receivers; each author device gets the other's edit.
        foreach (var m in new[] { m1, m2 }) Assert.True(t.B.Process(t.Team, m)["ok"].B());
        foreach (var m in new[] { m2, m1 }) Assert.True(t.O.Process(t.Team, m)["ok"].B());
        Assert.True(t.A1.Process(t.Team, m2)["ok"].B());
        Assert.True(t.A2.Process(t.Team, m1)["ok"].B());
        // Equal seq: the smaller deviceId wins.
        var (win, lose) = string.CompareOrdinal(t.A1.DeviceId, t.A2.DeviceId) < 0 ? (t.A1, t.A2) : (t.A2, t.A1);
        var expected = win == t.A1 ? "from A1" : "from A2";
        foreach (var d in new[] { t.B, t.O, t.A1, t.A2 })
        {
            var it = d.Item(t.A1.MemberId, item)!;
            Assert.Equal(2UL, it["rev"].U());
            Assert.Equal(expected, Ctx(it));
        }
        var replaced = lose.Item(t.A1.MemberId, item)!["replacedByOtherDevice"]!;
        Assert.Equal(lose == t.A1 ? "from A1" : "from A2", replaced["context"]!["text"].S());
        Assert.Equal(win.DeviceId, replaced["byDevice"].S());
        Assert.Null(win.Item(t.A1.MemberId, item)!["replacedByOtherDevice"]);
    }

    [Fact]
    public void Redelivery_is_a_duplicate_final_rejection_is_acked_and_logged()
    {
        using var t = new ItemWorld(pairs: false);
        var m = t.A1.Send(t.Team, "item.create", Link(t.TeamId, "x"), itemId: Rand(16))["message"].S();
        var r1 = t.B.Process(t.Team, m);
        Assert.True(r1["ack"].B());
        var r2 = t.B.Process(t.Team, m);
        Assert.Equal("duplicate", r2["result"]!["kind"].S());
        Assert.True(r2["ack"].B());

        // A forged op the validator refuses for good: acked (no endless redelivery) and written to the security log.
        var forged = ItemWorld.Forge(t.A1, t.Team, t.Header(t.A1, t.Team, 200, t.A1.MemberId, Rand(16), 1, 50), Link(t.TeamId, "bad"));
        var r3 = t.B.Process(t.Team, forged);
        Assert.False(r3["ok"].B());
        Assert.True(r3["ack"].B());
        var log = (JsonArray)t.B.Call("seclog.list", new() { ["teamId"] = t.TeamId })["entries"]!;
        Assert.Contains(log, e => e!["event"].S() == "delivery-rejected" && e["code"].S() == "op-rejected");
        // The same rejected message again is a duplicate, not a second log entry.
        Assert.Equal("duplicate", t.B.Process(t.Team, forged)["result"]!["kind"].S());
    }

    [Fact]
    public void Same_opId_with_other_content_is_a_security_event()
    {
        using var t = new ItemWorld(pairs: false);
        var opId = Rand(16);
        t.Post(t.A1, t.Team, "item.create", Link(t.TeamId, "1"), opId: opId);
        var other = ItemWorld.Forge(t.A1, t.Team, t.Header(t.A1, t.Team, 1, t.A1.MemberId, Rand(16), 1, 9), Link(t.TeamId, "2"), opId: opId);
        var r = t.B.Process(t.Team, other);
        Assert.Equal("op-rejected", r["code"].S());
        Assert.Contains("security", r["error"].S());
        Assert.True(r["ack"].B());
        Assert.Contains((JsonArray)t.B.Call("seclog.list", new() { ["teamId"] = t.TeamId })["entries"]!, e => e!["error"].S().Contains("other content"));
    }
}
