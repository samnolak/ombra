using System.Text.Json.Nodes;
using Ombra.Core.Interop;
using static Ombra.Core.Interop.Tests.H;

namespace Ombra.Core.Interop.Tests;

/// <summary>
/// Owner O, admin X, member A (devices A1, A2), members B and C. Team group with everyone; pairs A–B and A–C
/// (created by A1); optionally project P (created by O) with access for O, A and B.
/// </summary>
public sealed class ItemWorld : IDisposable
{
    public readonly World W;
    public readonly Dev O, X, A1, A2, B, C;
    public readonly string Team, PairAB = "", PairAC = "", Project = "", ProjectId = "";

    public ItemWorld(bool pairs = true, bool project = false)
    {
        W = new World();
        O = W.Device("O"); X = W.Device("X"); A1 = W.Device("A1"); A2 = W.Device("A2", A1.MemberId); B = W.Device("B"); C = W.Device("C");
        W.CreateTeam(O);
        W.AddMember(O, X, "admin");
        W.AddMember(O, A1, "member", A2);
        W.AddMember(O, B);
        W.AddMember(O, C);
        Team = W.CreateGroup(O, W.Team());
        W.Add(O, Team, X, A1, A2, B, C);
        if (pairs)
        {
            PairAB = W.CreateGroup(A1, W.Pair(A1, B));
            W.Add(A1, PairAB, A2, B);
            PairAC = W.CreateGroup(A1, W.Pair(A1, C));
            W.Add(A1, PairAC, A2, C);
        }
        if (project)
        {
            ProjectId = Rand(16);
            W.Update(O, new() { ["grantAccess"] = new JsonArray(
                new JsonObject { ["projectId"] = ProjectId, ["memberId"] = O.MemberId },
                new JsonObject { ["projectId"] = ProjectId, ["memberId"] = A1.MemberId },
                new JsonObject { ["projectId"] = ProjectId, ["memberId"] = B.MemberId }) });
            Project = W.CreateGroup(O, W.Project(ProjectId));
            W.Add(O, Project, A1, A2, B);
        }
    }

    public string TeamId => W.TeamId;

    /// <summary>Deliver an application message to every other member of the group; results by device.</summary>
    public Dictionary<Dev, JsonObject> Fan(Dev from, string gid, string msg) =>
        W.Groups[gid].Where(d => d != from).ToDictionary(d => d, d => d.Process(gid, msg));

    /// <summary>Send through the honest client and deliver to everyone; every receiver must accept.</summary>
    public JsonNode Post(Dev d, string gid, string opType, JsonObject payload, string? itemId = null, string? author = null, string? recipient = null,
        ulong? seq = null, string? opId = null, ulong? rev = null)
    {
        var r = d.Send(gid, opType, payload, itemId: itemId, author: author, recipient: recipient, seq: seq, opId: opId, rev: rev);
        foreach (var (dev, res) in Fan(d, gid, r["message"].S()))
            Assert.True(res["ok"].B(), $"{dev} rejected {opType}: {res["code"]} {res["error"]}");
        return r;
    }

    /// <summary>Error code of an honest send ("ok" if the sender's own validator accepts it).</summary>
    public static string TrySend(Dev d, string gid, string opType, JsonObject payload, string? itemId = null, string? author = null,
        string? recipient = null, ulong? seq = null, string? opId = null)
    {
        var a = new JsonObject { ["groupId"] = gid, ["opType"] = opType, ["payload"] = payload, ["now"] = Now };
        if (itemId != null) a["itemId"] = itemId;
        if (author != null) a["author"] = author;
        if (recipient != null) a["recipient"] = recipient;
        if (seq != null) a["seq"] = seq;
        if (opId != null) a["opId"] = opId;
        return d.Try("op.send", a);
    }

    /// <summary>Signed header for an op forged by <paramref name="d"/> at the group's current epoch and roster version.</summary>
    public JsonObject Header(Dev d, string gid, int opType, string author, string itemId, ulong rev, ulong seq, string? recipient = null)
    {
        var gi = d.Call("group.info", new() { ["groupId"] = gid });
        var v = gi["rosterVersion"].U();
        var h = new JsonObject
        {
            ["teamId"] = TeamId, ["groupDescriptorHash"] = gid, ["epoch"] = gi["epoch"].U(), ["rosterVersion"] = v,
            ["rosterHash"] = d.Call("roster.head", new() { ["teamId"] = TeamId, ["version"] = v })["hash"].S(),
            ["authorMemberId"] = author, ["signerMemberId"] = d.MemberId, ["signerDeviceId"] = d.DeviceId, ["opType"] = opType,
            ["itemId"] = itemId, ["rev"] = rev, ["seq"] = seq, ["seqHead"] = seq,
        };
        if (recipient != null) h["recipientMemberId"] = recipient;
        return h;
    }

    /// <summary>An item op signed by <paramref name="d"/> without its own validator, encrypted into the group by d's leaf.</summary>
    public static string Forge(Dev d, string gid, JsonObject header, JsonObject payload, string? opId = null, bool badSig = false)
    {
        var a = new JsonObject { ["header"] = header, ["payload"] = payload, ["badSig"] = badSig };
        if (opId != null) a["opId"] = opId;
        var op = d.Call("attack.sign_op", a)["op"].S();
        return d.Call("attack.encrypt", new() { ["groupId"] = gid, ["plaintext"] = op })["message"].S();
    }

    public static string Code(JsonObject r) => r["ok"].B() ? "ok" : r["code"].S();

    /// <summary><c>link</c> create payload addressed to <paramref name="recipient"/> (travels in the author–recipient pair).</summary>
    public static JsonObject ToMember(Dev recipient, string url) => new()
    {
        ["kind"] = "link", ["audience"] = new JsonObject { ["member"] = "hex:" + recipient.MemberId }, ["createdAt"] = Now,
        ["body"] = new JsonObject { ["url"] = url },
    };

    public static JsonObject Mark(string scope, bool anchor = true)
    {
        var p = new JsonObject { ["kind"] = "mark", ["audience"] = new JsonObject { ["space"] = "hex:" + scope }, ["createdAt"] = Now };
        if (anchor) p["anchor"] = new JsonObject { ["web"] = new JsonObject { ["url"] = "https://example.com/a", ["selector"] = "#x", ["textQuote"] = "quote" } };
        return p;
    }

    public static JsonObject Collection(string scope, params (string author, string item)[] keys) => new()
    {
        ["kind"] = "collection", ["audience"] = new JsonObject { ["space"] = "hex:" + scope }, ["createdAt"] = Now,
        ["body"] = new JsonObject { ["itemKeys"] = Keys(keys) },
    };

    public static JsonArray Keys(params (string author, string item)[] keys) =>
        new(keys.Select(k => (JsonNode)new JsonArray("hex:" + k.author, "hex:" + k.item)).ToArray());

    public static JsonObject Edit(ulong baseRev, string text) => new()
    {
        ["baseRev"] = baseRev, ["delta"] = new JsonObject { ["context"] = new JsonObject { ["text"] = text } },
    };

    public static JsonObject Status(string status, string? note = null)
    {
        var s = new JsonObject { ["status"] = status };
        if (note != null) s["note"] = note;
        return s;
    }

    public void Dispose() => W.Dispose();
}

/// <summary>§3.0 operation table and validator (R5-03/04/05/08/10/12, N4, N5), each rule checked on the receiver
/// with an op forged by an insider that bypasses its own client, and where meaningful on the honest sender too.</summary>
public sealed class ItemOpTests
{
    private const int Create = 1, EditT = 2, Delete = 3, CollAdd = 4, StatusT = 6, SeqSkip = 9;

    [Fact]
    public void Unknown_operation_types_reply_thread_rejected()
    {
        using var t = new ItemWorld(pairs: false);
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.A1, t.Team, "item.reply", Link(t.TeamId, "x")));
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.A1, t.Team, "thread.create", Link(t.TeamId, "x")));
        foreach (var ty in new[] { 0, 11, 200 })
        {
            var m = ItemWorld.Forge(t.A1, t.Team, t.Header(t.A1, t.Team, ty, t.A1.MemberId, Rand(16), 1, 1), Link(t.TeamId, "reply"));
            var r = t.B.Process(t.Team, m);
            Assert.Equal("op-rejected", ItemWorld.Code(r));
            Assert.Contains("unknown operation type", r["error"].S());
        }
        // Unknown item kind and unknown payload field are rejected by the closed schema as well.
        var k = Link(t.TeamId, "x"); k["kind"] = "reply";
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.A1, t.Team, "item.create", k));
        var f = Link(t.TeamId, "x"); f["thread"] = "t1";
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.A1, t.Team, "item.create", f));
    }

    [Fact]
    public void Edit_only_by_author()
    {
        using var t = new ItemWorld(pairs: false);
        var item = Rand(16);
        t.Post(t.A1, t.Team, "item.create", Link(t.TeamId, "a"), itemId: item);
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.B, t.Team, "item.edit", ItemWorld.Edit(1, "b"), itemId: item, author: t.A1.MemberId));
        var forged = ItemWorld.Forge(t.B, t.Team, t.Header(t.B, t.Team, EditT, t.A1.MemberId, item, 2, 1), ItemWorld.Edit(1, "b"));
        foreach (var d in new[] { t.A1, t.C, t.O }) Assert.Equal("op-rejected", ItemWorld.Code(d.Process(t.Team, forged)));
        Assert.Equal(1UL, t.C.Item(t.A1.MemberId, item)!["rev"].U());
        // The author's other device may edit (signer member = author).
        t.Post(t.A2, t.Team, "item.edit", ItemWorld.Edit(1, "a2"), itemId: item, author: t.A1.MemberId);
        Assert.Equal("a2", t.C.Item(t.A1.MemberId, item)!["context"]!["text"].S());
    }

    [Fact]
    public void Delete_by_author_or_admin_for_non_admin_authors_only()
    {
        using var t = new ItemWorld(pairs: false);
        var byA = Rand(16); var byO = Rand(16); var byA2 = Rand(16);
        t.Post(t.A1, t.Team, "item.create", Link(t.TeamId, "a"), itemId: byA);
        t.Post(t.A1, t.Team, "item.create", Link(t.TeamId, "a2"), itemId: byA2);
        t.Post(t.O, t.Team, "item.create", Link(t.TeamId, "o"), itemId: byO);
        // Non-author non-admin: refused by its own client and by every receiver.
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.B, t.Team, "item.delete", new JsonObject(), itemId: byA, author: t.A1.MemberId));
        var forged = ItemWorld.Forge(t.B, t.Team, t.Header(t.B, t.Team, Delete, t.A1.MemberId, byA, 2, 1), new JsonObject());
        Assert.Equal("op-rejected", ItemWorld.Code(t.C.Process(t.Team, forged)));
        // Admin X deleting an item of the owner: refused (author is owner/admin).
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.X, t.Team, "item.delete", new JsonObject(), itemId: byO, author: t.O.MemberId));
        var forgedX = ItemWorld.Forge(t.X, t.Team, t.Header(t.X, t.Team, Delete, t.O.MemberId, byO, 2, 1), new JsonObject());
        Assert.Equal("op-rejected", ItemWorld.Code(t.C.Process(t.Team, forgedX)));
        Assert.False(t.C.Item(t.O.MemberId, byO)!["deleted"].B());
        // Admin X deleting a member's space item, and the author deleting its own: accepted.
        t.Post(t.X, t.Team, "item.delete", new JsonObject(), itemId: byA, author: t.A1.MemberId);
        t.Post(t.A2, t.Team, "item.delete", new JsonObject(), itemId: byA2, author: t.A1.MemberId);
        Assert.True(t.C.Item(t.A1.MemberId, byA)!["deleted"].B());
        Assert.True(t.C.Item(t.A1.MemberId, byA2)!["deleted"].B());
    }

    [Fact]
    public void Collection_add_remove_only_by_collection_author()
    {
        using var t = new ItemWorld(pairs: false);
        var coll = Rand(16); var x = Rand(16);
        t.Post(t.A1, t.Team, "item.create", ItemWorld.Collection(t.TeamId), itemId: coll);
        t.Post(t.B, t.Team, "item.create", Link(t.TeamId, "x"), itemId: x);
        var add = new JsonObject { ["itemKey"] = ItemWorld.Keys((t.B.MemberId, x))[0]!.DeepClone() };
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.B, t.Team, "collection.add", (JsonObject)add.DeepClone(), itemId: coll, author: t.A1.MemberId));
        var forged = ItemWorld.Forge(t.B, t.Team, t.Header(t.B, t.Team, CollAdd, t.A1.MemberId, coll, 2, 2), (JsonObject)add.DeepClone());
        Assert.Equal("op-rejected", ItemWorld.Code(t.C.Process(t.Team, forged)));
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.B, t.Team, "collection.remove", (JsonObject)add.DeepClone(), itemId: coll, author: t.A1.MemberId));
        t.Post(t.A1, t.Team, "collection.add", (JsonObject)add.DeepClone(), itemId: coll);
        Assert.Single((JsonArray)t.C.Item(t.A1.MemberId, coll)!["body"]!["itemKeys"]!);
        t.Post(t.A1, t.Team, "collection.remove", (JsonObject)add.DeepClone(), itemId: coll);
        Assert.Empty((JsonArray)t.C.Item(t.A1.MemberId, coll)!["body"]!["itemKeys"]!);
    }

    [Fact]
    public void Create_with_known_key_rejected_same_itemId_by_other_author_is_another_item()
    {
        using var t = new ItemWorld(pairs: false);
        var item = Rand(16);
        t.Post(t.A1, t.Team, "item.create", Link(t.TeamId, "by A"), itemId: item);
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.A2, t.Team, "item.create", Link(t.TeamId, "again"), itemId: item));
        var dup = ItemWorld.Forge(t.A2, t.Team, t.Header(t.A2, t.Team, Create, t.A1.MemberId, item, 1, 50), Link(t.TeamId, "again"));
        var r = t.C.Process(t.Team, dup);
        Assert.Equal("op-rejected", ItemWorld.Code(r));
        Assert.Contains("already known key", r["error"].S());
        // M (= B) creates its own item with the same itemId: a different item; A's authorship is unchanged.
        t.Post(t.B, t.Team, "item.create", Link(t.TeamId, "by B"), itemId: item);
        Assert.Equal(t.A1.MemberId, t.C.Item(t.A1.MemberId, item)!["author"].S());
        Assert.Equal("by A", t.C.Item(t.A1.MemberId, item)!["body"]!["url"].S());
        Assert.Equal("by B", t.C.Item(t.B.MemberId, item)!["body"]!["url"].S());
        // A create claiming author A but signed by B is refused.
        var steal = ItemWorld.Forge(t.B, t.Team, t.Header(t.B, t.Team, Create, t.A1.MemberId, Rand(16), 1, 9), Link(t.TeamId, "stolen"));
        Assert.Equal("op-rejected", ItemWorld.Code(t.C.Process(t.Team, steal)));
        t.Post(t.A1, t.Team, "item.edit", ItemWorld.Edit(1, "edited by A"), itemId: item);
        Assert.Equal(2UL, t.C.Item(t.A1.MemberId, item)!["rev"].U());
        Assert.Equal(1UL, t.C.Item(t.B.MemberId, item)!["rev"].U());
    }

    [Fact]
    public void Mark_without_anchor_and_limits_rejected()
    {
        using var t = new ItemWorld();
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.A1, t.Team, "item.create", ItemWorld.Mark(t.TeamId, anchor: false)));
        var forged = ItemWorld.Forge(t.A1, t.Team, t.Header(t.A1, t.Team, Create, t.A1.MemberId, Rand(16), 1, 1), ItemWorld.Mark(t.TeamId, anchor: false));
        var r = t.B.Process(t.Team, forged);
        Assert.Equal("op-rejected", ItemWorld.Code(r));
        Assert.Contains("anchor", r["error"].S());
        Assert.Equal("ok", ItemWorld.TrySend(t.A1, t.Team, "item.create", ItemWorld.Mark(t.TeamId)));
        var longCtx = Link(t.TeamId, "x"); longCtx["context"] = new JsonObject { ["text"] = new string('ж', 2001) };
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.A1, t.Team, "item.create", longCtx));
        var big = Link(t.TeamId, "x"); big["context"] = new JsonObject { ["selection"] = new string('a', 70 * 1024) };
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.A1, t.Team, "item.create", big));
        // Note > 280 characters on a status.
        var it = t.A1.Call("item.send_addressed", new() { ["teamId"] = t.TeamId, ["kind"] = "link", ["recipients"] = Arr(t.B.MemberId), ["now"] = Now,
            ["body"] = new JsonObject { ["url"] = "https://e/1" } });
        t.Fan(t.A1, t.PairAB, it["copies"]![0]!["message"].S());
        var id = it["copies"]![0]!["itemId"].S();
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.B, t.PairAB, "item.status", ItemWorld.Status("accepted", new string('я', 281)), itemId: id, author: t.A1.MemberId, recipient: t.B.MemberId));
        var forgedNote = ItemWorld.Forge(t.B, t.PairAB, t.Header(t.B, t.PairAB, StatusT, t.A1.MemberId, id, 0, 1, t.B.MemberId), ItemWorld.Status("accepted", new string('я', 281)));
        Assert.Equal("op-rejected", ItemWorld.Code(t.A1.Process(t.PairAB, forgedNote)));
        Assert.Equal("ok", ItemWorld.TrySend(t.B, t.PairAB, "item.status", ItemWorld.Status("accepted", new string('я', 280)), itemId: id, author: t.A1.MemberId, recipient: t.B.MemberId));
    }

    [Fact]
    public void Status_only_by_recipient_about_itself_in_the_pair_and_with_current_access()
    {
        using var t = new ItemWorld(project: true);
        var it = t.A1.Call("item.send_addressed", new() { ["teamId"] = t.TeamId, ["kind"] = "link", ["recipients"] = Arr(t.B.MemberId), ["now"] = Now,
            ["body"] = new JsonObject { ["url"] = "https://e/2" } });
        t.Fan(t.A1, t.PairAB, it["copies"]![0]!["message"].S());
        var id = it["copies"]![0]!["itemId"].S();
        // About another member (recipientMemberId = C) — refused by sender and receiver.
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.B, t.PairAB, "item.status", ItemWorld.Status("seen"), itemId: id, author: t.A1.MemberId, recipient: t.C.MemberId));
        var about = ItemWorld.Forge(t.B, t.PairAB, t.Header(t.B, t.PairAB, StatusT, t.A1.MemberId, id, 0, 1, t.C.MemberId), ItemWorld.Status("seen"));
        Assert.Equal("op-rejected", ItemWorld.Code(t.A1.Process(t.PairAB, about)));
        // From a non-recipient: the author's own other device.
        var self = ItemWorld.Forge(t.A2, t.PairAB, t.Header(t.A2, t.PairAB, StatusT, t.A1.MemberId, id, 0, 1, t.A2.MemberId), ItemWorld.Status("seen"));
        Assert.Equal("op-rejected", ItemWorld.Code(t.B.Process(t.PairAB, self)));
        // Sent into the scope (team) group instead of the pair.
        var space = Rand(16);
        t.Post(t.A1, t.Team, "item.create", Link(t.TeamId, "space"), itemId: space);
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.B, t.Team, "item.status", ItemWorld.Status("seen"), itemId: space, author: t.A1.MemberId, recipient: t.B.MemberId));
        var inScope = ItemWorld.Forge(t.B, t.Team, t.Header(t.B, t.Team, StatusT, t.A1.MemberId, space, 0, 1, t.B.MemberId), ItemWorld.Status("seen"));
        Assert.Equal("op-rejected", ItemWorld.Code(t.C.Process(t.Team, inScope)));
        // Space item of project P: C (no access) cannot post a status in pair A–C; B (access) can in A–B.
        var pItem = Rand(16);
        t.Post(t.A1, t.Project, "item.create", Link(t.ProjectId, "project"), itemId: pItem);
        foreach (var d in new[] { t.A1, t.A2 }) Assert.NotNull(d.Item(t.A1.MemberId, pItem));
        var noAccess = ItemWorld.Forge(t.C, t.PairAC, t.Header(t.C, t.PairAC, StatusT, t.A1.MemberId, pItem, 0, 1, t.C.MemberId), ItemWorld.Status("seen"));
        var r = t.A1.Process(t.PairAC, noAccess);
        Assert.Equal("op-rejected", ItemWorld.Code(r));
        Assert.Contains("without access", r["error"].S());
        t.Post(t.B, t.PairAB, "item.status", ItemWorld.Status("accepted", "ok"), itemId: pItem, author: t.A1.MemberId, recipient: t.B.MemberId);
        t.Post(t.B, t.PairAB, "item.status", ItemWorld.Status("seen"), itemId: id, author: t.A1.MemberId, recipient: t.B.MemberId);
        var st = (JsonArray)t.A2.Item(t.A1.MemberId, id)!["statuses"]!;
        Assert.NotNull(st.Single()!["seen"]);
        Assert.Null(st.Single()!["status"]);
        Assert.Equal(t.B.MemberId, st.Single()!["recipient"].S());
    }

    [Fact]
    public void Replayed_opId_rejected()
    {
        using var t = new ItemWorld(pairs: false);
        var opId = Rand(16);
        t.Post(t.A1, t.Team, "item.create", Link(t.TeamId, "1"), opId: opId);
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.A1, t.Team, "item.create", Link(t.TeamId, "2"), opId: opId));
        var replay = ItemWorld.Forge(t.A1, t.Team, t.Header(t.A1, t.Team, Create, t.A1.MemberId, Rand(16), 1, 77), Link(t.TeamId, "2"), opId: opId);
        var r = t.B.Process(t.Team, replay);
        Assert.Equal("op-rejected", ItemWorld.Code(r));
        Assert.Contains("replayed opId", r["error"].S());
        // Same (signerDeviceId, seq) under a fresh opId is a replay too.
        var seqReplay = ItemWorld.Forge(t.A1, t.Team, t.Header(t.A1, t.Team, Create, t.A1.MemberId, Rand(16), 1, 1), Link(t.TeamId, "3"));
        Assert.Equal("op-rejected", ItemWorld.Code(t.B.Process(t.Team, seqReplay)));
    }

    [Fact]
    public void Live_delivery_binding_sender_epoch_descriptor_version_signature()
    {
        using var t = new ItemWorld();
        var ok = t.Header(t.A1, t.Team, Create, t.A1.MemberId, Rand(16), 1, 1);
        string Try(JsonObject h, bool badSig = false) => ItemWorld.Code(t.B.Process(t.Team, ItemWorld.Forge(t.A1, t.Team, h, Link(t.TeamId, "x"), badSig: badSig)));
        var wrongDevice = (JsonObject)ok.DeepClone(); wrongDevice["signerDeviceId"] = t.A2.DeviceId;          // MLS sender != signerDeviceId
        Assert.Equal("op-rejected", Try(wrongDevice));
        var wrongEpoch = (JsonObject)ok.DeepClone(); wrongEpoch["epoch"] = ok["epoch"].U() - 1;
        Assert.Equal("op-rejected", Try(wrongEpoch));
        var wrongGroup = (JsonObject)ok.DeepClone(); wrongGroup["groupDescriptorHash"] = t.PairAB;
        Assert.Equal("op-rejected", Try(wrongGroup));
        Assert.Equal("bad-signature", Try((JsonObject)ok.DeepClone(), badSig: true));
        // N4: rosterVersion must be exactly the version of this epoch's GroupContext — older (V-1) and newer both refused.
        var v = ok["rosterVersion"].U();
        var older = (JsonObject)ok.DeepClone();
        older["rosterVersion"] = v - 1;
        older["rosterHash"] = t.A1.Call("roster.head", new() { ["teamId"] = t.TeamId, ["version"] = v - 1 })["hash"].S();
        var r = t.B.Process(t.Team, ItemWorld.Forge(t.A1, t.Team, older, Link(t.TeamId, "x")));
        Assert.Equal("op-rejected", ItemWorld.Code(r));
        Assert.Contains("N4", r["error"].S());
        t.W.Update(t.O, new() { ["pins"] = Arr(Rand(32)) });
        var newer = (JsonObject)ok.DeepClone();
        newer["rosterVersion"] = t.W.Head(t.O).version;
        newer["rosterHash"] = t.W.Head(t.O).hash;
        Assert.Equal("op-rejected", Try(newer));
        // After the admin moves the group to the head, ops signed at the old version are refused, at the new accepted.
        t.W.Deliver(t.Team, t.O.Call("group.update", new() { ["groupId"] = t.Team })["commit"].S(), t.O);
        var stale = t.Header(t.A1, t.Team, Create, t.A1.MemberId, Rand(16), 1, 2);
        stale["rosterVersion"] = v; stale["rosterHash"] = ok["rosterHash"].S();
        Assert.Equal("op-rejected", Try(stale));
        Assert.Equal("ok", Try(t.Header(t.A1, t.Team, Create, t.A1.MemberId, Rand(16), 1, 3)));
    }

    [Fact]
    public void R2_10_device_revoked_by_cascade_cannot_sign_even_at_a_version_where_it_was_listed()
    {
        using var t = new ItemWorld(pairs: false);
        var a3 = t.W.Device("A3", t.A1.MemberId);
        t.W.AddDevice(t.A2, a3);                                   // A3 issued by A2
        var added = t.W.Head(t.O).version;
        t.W.Add(t.O, t.Team, a3);
        // A2 compromised since A3 was added: A2 and (cascade) A3 revoked. A3 never sees the update.
        t.W.Update(t.A1, new() { ["revokeDevices"] = new JsonArray(
            new JsonObject { ["deviceId"] = t.A2.DeviceId, ["compromisedSince"] = added },
            new JsonObject { ["deviceId"] = a3.DeviceId }) }, a3);
        var gv = a3.Call("group.info", new() { ["groupId"] = t.Team })["rosterVersion"].U();
        Assert.True(gv >= added);
        var m = a3.Send(t.Team, "item.create", Link(t.TeamId, "from stolen device"))["message"].S();
        var r = t.B.Process(t.Team, m);
        Assert.Equal("op-rejected", ItemWorld.Code(r));
        Assert.Contains("compromised", r["error"].S());
        // A plain revocation (lost device, compromisedSince = revocation version) keeps earlier signatures valid:
        // A1's ops at the group's version are still accepted before rotation.
        var ok = ItemWorld.Forge(t.A1, t.Team, t.Header(t.A1, t.Team, Create, t.A1.MemberId, Rand(16), 1, 1), Link(t.TeamId, "fine"));
        Assert.Equal("ok", ItemWorld.Code(t.B.Process(t.Team, ok)));
    }

    [Fact]
    public void Ops_for_unknown_item_keys_are_ignored_and_create_nothing()
    {
        using var t = new ItemWorld();
        var ghost = Rand(16);
        foreach (var (op, payload) in new[] { ("item.edit", ItemWorld.Edit(1, "x")), ("item.delete", new JsonObject()) })
        {
            var r = t.A1.Send(t.Team, op, payload, itemId: ghost);
            foreach (var (_, res) in t.Fan(t.A1, t.Team, r["message"].S()))
                Assert.Contains("ignored", res["result"]!["outcome"]!.ToJsonString());
        }
        var add = t.A1.Send(t.Team, "collection.add", new JsonObject { ["itemKey"] = ItemWorld.Keys((t.A1.MemberId, Rand(16)))[0]!.DeepClone() }, itemId: ghost);
        t.Fan(t.A1, t.Team, add["message"].S());
        var st = t.B.Send(t.PairAB, "item.status", ItemWorld.Status("done"), itemId: ghost, author: t.A1.MemberId, recipient: t.B.MemberId);
        Assert.Contains("ignored", t.A1.Process(t.PairAB, st["message"].S())["result"]!["outcome"]!.ToJsonString());
        foreach (var d in new[] { t.A1, t.B, t.C, t.O }) Assert.Null(d.Item(t.A1.MemberId, ghost));
    }

    [Fact]
    public void Statuses_reordered_by_the_server_give_the_same_result_by_seq()
    {
        using var t = new ItemWorld();
        var it = t.A1.Call("item.send_addressed", new() { ["teamId"] = t.TeamId, ["kind"] = "link", ["recipients"] = Arr(t.B.MemberId), ["now"] = Now,
            ["body"] = new JsonObject { ["url"] = "https://e/3" } });
        t.Fan(t.A1, t.PairAB, it["copies"]![0]!["message"].S());
        var id = it["copies"]![0]!["itemId"].S();
        // Resource (rev 10): seen once before the final status; the first final status (accepted) never changes.
        var msgs = new[] { "seen", "accepted" }.Select(s =>
            t.B.Send(t.PairAB, "item.status", ItemWorld.Status(s), itemId: id, author: t.A1.MemberId, recipient: t.B.MemberId)["message"].S()).ToList();
        foreach (var m in msgs) Assert.True(t.A1.Process(t.PairAB, m)["ok"].B());
        foreach (var m in Enumerable.Reverse(msgs)) Assert.True(t.A2.Process(t.PairAB, m)["ok"].B());
        foreach (var d in new[] { t.A1, t.A2 })
        {
            var s = ((JsonArray)d.Item(t.A1.MemberId, id)!["statuses"]!).Single()!;
            Assert.Equal("accepted", s["status"].S());
            Assert.NotNull(s["seen"]);
        }
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.B, t.PairAB, "item.status", ItemWorld.Status("declined"), itemId: id, author: t.A1.MemberId, recipient: t.B.MemberId));
    }

    private static JsonArray Gaps(Dev viewer, string gid, Dev of) =>
        (JsonArray)viewer.Call("group.info", new() { ["groupId"] = gid })["seq"]![of.MemberId]!["gaps"]!;

    [Fact]
    public void N5_seq_gaps_are_shown_closed_by_seq_skip_and_revealed_by_seq_head()
    {
        using var t = new ItemWorld();
        string Op(ulong? seq = null) => t.A1.Send(t.PairAB, "item.create", ItemWorld.ToMember(t.B, "s"), recipient: t.B.MemberId, seq: seq)["message"].S();
        Assert.Equal("ok", ItemWorld.Code(t.B.Process(t.PairAB, Op())));        // seq 1
        _ = Op();                                                                  // seq 2 dropped by the server
        Assert.Equal("ok", ItemWorld.Code(t.B.Process(t.PairAB, Op())));        // seq 3
        var g = Gaps(t.B, t.PairAB, t.A1);
        Assert.Equal(2UL, g.Single()!["from"].U());
        // A reserves 4 without sending, sends 5; the gap at 4 is closed by a signed seq.skip.
        Assert.Equal("ok", ItemWorld.Code(t.B.Process(t.PairAB, Op(5))));
        Assert.Equal(2, Gaps(t.B, t.PairAB, t.A1).Count);
        var skip = new JsonObject { ["from"] = 4, ["to"] = 4, ["deviceId"] = "hex:" + t.A1.DeviceId };
        Assert.Equal("ok", ItemWorld.Code(t.B.Process(t.PairAB, t.A1.Send(t.PairAB, "seq.skip", skip)["message"].S())));
        Assert.Equal(2UL, Gaps(t.B, t.PairAB, t.A1).Single()!["from"].U());
        // seq.skip only about oneself: B cannot close A's range; a skip naming another member's device is refused.
        var foreign = ItemWorld.Forge(t.B, t.PairAB, t.Header(t.B, t.PairAB, SeqSkip, t.A1.MemberId, Rand(16), 0, 0),
            new JsonObject { ["from"] = 2, ["to"] = 2, ["deviceId"] = "hex:" + t.A1.DeviceId });
        Assert.Equal("op-rejected", ItemWorld.Code(t.A2.Process(t.PairAB, foreign)));
        Assert.Equal("op-rejected", ItemWorld.TrySend(t.B, t.PairAB, "seq.skip", new JsonObject { ["from"] = 1, ["to"] = 1, ["deviceId"] = "hex:" + t.A1.DeviceId }));
        // A2 reserved 6 and is then revoked: A1 publishes seq.skip for A2's range after rotating the pair.
        var seven = Op(7);
        Assert.Equal("ok", ItemWorld.Code(t.B.Process(t.PairAB, seven)));
        Assert.Contains(Gaps(t.B, t.PairAB, t.A1), x => x!["from"].U() == 6);
        t.W.Update(t.A1, new() { ["revokeDevices"] = new JsonArray(new JsonObject { ["deviceId"] = t.A2.DeviceId }) });
        t.W.Deliver(t.PairAB, t.A1.Call("group.rotate", new() { ["groupId"] = t.PairAB })["commit"].S(), t.A1);
        var skipA2 = new JsonObject { ["from"] = 6, ["to"] = 6, ["deviceId"] = "hex:" + t.A2.DeviceId };
        Assert.Equal("ok", ItemWorld.Code(t.B.Process(t.PairAB, t.A1.Send(t.PairAB, "seq.skip", skipA2)["message"].S())));
        Assert.DoesNotContain(Gaps(t.B, t.PairAB, t.A1), x => x!["from"].U() == 6);
        // The last op (8) is dropped; the next seq.head beacon reveals it.
        _ = Op();
        Assert.DoesNotContain(Gaps(t.B, t.PairAB, t.A1), x => x!["from"].U() == 8);
        Assert.Equal("ok", ItemWorld.Code(t.B.Process(t.PairAB, t.A1.Send(t.PairAB, "seq.head", new JsonObject())["message"].S())));
        Assert.Contains(Gaps(t.B, t.PairAB, t.A1), x => x!["from"].U() == 8 && x["to"].U() == 8);
    }

    [Fact]
    public void N5_seq_head_is_not_in_authenticated_data_and_new_members_see_no_false_gaps()
    {
        using var t = new ItemWorld(pairs: false);
        for (var i = 0; i < 3; i++) t.Post(t.A1, t.Team, "item.create", Link(t.TeamId, $"before {i}"));
        var m = t.A1.Send(t.Team, "item.create", Link(t.TeamId, "probe"))["message"].S();
        t.Fan(t.A1, t.Team, m);
        var info = OmbraPublic.InspectMessage(Bytes(m));
        Assert.Equal("PrivateMessage", info["wireFormat"].S());
        var aad = info["aad"].S();
        var gi = t.A1.Call("group.info", new() { ["groupId"] = t.Team });
        var v = gi["rosterVersion"].U();
        // aad = rosterVersion u64 ‖ rosterHash[32] ‖ epoch u64 ‖ epoch_authenticator[32] — nothing else (no seq head).
        Assert.Equal((8 + 32 + 8 + 32) * 2, aad.Length);
        Assert.Equal(v.ToString("x16"), aad[..16]);
        Assert.Equal(t.A1.Call("roster.head", new() { ["teamId"] = t.TeamId, ["version"] = v })["hash"].S(), aad[16..80]);
        Assert.Equal(gi["epoch"].U().ToString("x16"), aad[80..96]);
        // New member D joins after A's seq 1..4: no gap below its base. First a beacon, then an op.
        var d = t.W.Device("D");
        t.W.AddMember(t.O, d);
        t.W.Add(t.O, t.Team, d);
        t.Fan(t.A1, t.Team, t.A1.Send(t.Team, "seq.head", new JsonObject())["message"].S());
        Assert.Empty(Gaps(d, t.Team, t.A1));
        t.Fan(t.A1, t.Team, t.A1.Send(t.Team, "item.create", Link(t.TeamId, "after"))["message"].S());
        Assert.Empty(Gaps(d, t.Team, t.A1));
        // A new device of A sees no false gaps for B either.
        t.Post(t.B, t.Team, "item.create", Link(t.TeamId, "b1"));
        t.Post(t.B, t.Team, "item.create", Link(t.TeamId, "b2"));
        var a3 = t.W.Device("A3", t.A1.MemberId);
        t.W.AddDevice(t.A1, a3);
        t.W.Add(t.O, t.Team, a3);
        t.Post(t.B, t.Team, "item.create", Link(t.TeamId, "b3"));
        Assert.Empty(Gaps(a3, t.Team, t.B));
    }

    [Fact]
    public void Tombstone_erases_payload_context_attachment_keys_and_signed_ops()
    {
        using var t = new ItemWorld(pairs: false);
        var id = Rand(16);
        var p = Link(t.TeamId, "secret-url");
        p["context"] = new JsonObject { ["text"] = "secret context" };
        p["attachments"] = new JsonArray(new JsonObject { ["blobRef"] = "blob-1", ["key"] = "hex:" + Rand(32) });
        t.Post(t.A1, t.Team, "item.create", p, itemId: id);
        t.Post(t.A1, t.Team, "item.edit", ItemWorld.Edit(1, "secret edit"), itemId: id);
        var editAfter = t.A1.Send(t.Team, "item.edit", ItemWorld.Edit(2, "late"), itemId: id)["message"].S();   // delivered after the tombstone
        t.Post(t.A1, t.Team, "item.delete", new JsonObject(), itemId: id, rev: 4);
        foreach (var d in new[] { t.B, t.C, t.O })
        {
            var it = d.Item(t.A1.MemberId, id)!;
            Assert.True(it["deleted"].B());
            Assert.Null(it["body"]); Assert.Null(it["context"]); Assert.Null(it["attachments"]);
            Assert.Equal(0UL, it["storedOps"].U());
            Assert.DoesNotContain("secret", it.ToJsonString());
        }
        Assert.Contains("ignored", t.B.Process(t.Team, editAfter)["result"]!["outcome"]!.ToJsonString());
        var resurrect = ItemWorld.Forge(t.A1, t.Team, t.Header(t.A1, t.Team, Create, t.A1.MemberId, id, 1, 90), Link(t.TeamId, "again"));
        Assert.Equal("op-rejected", ItemWorld.Code(t.B.Process(t.Team, resurrect)));
        t.B.Restart();
        Assert.DoesNotContain("secret", t.B.Item(t.A1.MemberId, id)!.ToJsonString());
    }

    [Fact]
    public void R5_10_addressed_copies_carry_only_their_recipient_and_its_visible_keys()
    {
        using var t = new ItemWorld();
        var space = Rand(16);
        t.Post(t.A1, t.Team, "item.create", Link(t.TeamId, "space"), itemId: space);
        string Addressed(Dev to, string gid, string url)
        {
            var r = t.A1.Call("item.send_addressed", new() { ["teamId"] = t.TeamId, ["kind"] = "link", ["recipients"] = Arr(to.MemberId), ["now"] = Now,
                ["body"] = new JsonObject { ["url"] = url } });
            t.Fan(t.A1, gid, r["copies"]![0]!["message"].S());
            return r["copies"]![0]!["itemId"].S();
        }
        var forB = Addressed(t.B, t.PairAB, "https://e/b");
        var forC = Addressed(t.C, t.PairAC, "https://e/c");
        var coll = t.A1.Call("item.send_addressed", new() { ["teamId"] = t.TeamId, ["kind"] = "collection", ["recipients"] = Arr(t.B.MemberId, t.C.MemberId),
            ["now"] = Now, ["body"] = new JsonObject { ["itemKeys"] = ItemWorld.Keys((t.A1.MemberId, space), (t.A1.MemberId, forC), (t.A1.MemberId, forB)) } });
        var copies = (JsonArray)coll["copies"]!;
        Assert.Equal(2, copies.Count);
        Assert.NotEqual(copies[0]!["opId"].S(), copies[1]!["opId"].S());
        var toB = copies.Single(c => c!["recipient"].S() == t.B.MemberId)!;
        var toC = copies.Single(c => c!["recipient"].S() == t.C.MemberId)!;
        Assert.Equal(t.PairAB, toB["groupId"].S());
        var rb = t.B.Process(t.PairAB, toB["message"].S());
        Assert.True(rb["ok"].B());
        Assert.True(t.C.Process(t.PairAC, toC["message"].S())["ok"].B());
        var idB = toB["itemId"].S(); var idC = toC["itemId"].S();
        Assert.NotEqual(idB, idC);
        var bView = t.B.Item(t.A1.MemberId, idB)!;
        var keysB = ((JsonArray)bView["body"]!["itemKeys"]!).Select(k => k![1].S()).ToList();
        Assert.Equal(new[] { space, forB }.OrderBy(x => x), keysB.OrderBy(x => x));
        Assert.Equal(t.B.MemberId, bView["recipient"].S());
        Assert.DoesNotContain(t.C.MemberId, bView.ToJsonString() + rb.ToJsonString());
        Assert.DoesNotContain(forC, bView.ToJsonString());
        var keysC = ((JsonArray)t.C.Item(t.A1.MemberId, idC)!["body"]!["itemKeys"]!).Select(k => k![1].S()).ToList();
        Assert.Null(t.B.Item(t.A1.MemberId, idC));
        Assert.Equal(new[] { space, forC }.OrderBy(x => x), keysC.OrderBy(x => x));
        // A member item must travel in the author–recipient pair: addressed to C but sent into pair A–B is refused.
        var wrong = ItemWorld.Forge(t.A1, t.PairAB, t.Header(t.A1, t.PairAB, Create, t.A1.MemberId, Rand(16), 1, 40, t.C.MemberId), new JsonObject
        {
            ["kind"] = "link", ["audience"] = new JsonObject { ["member"] = "hex:" + t.C.MemberId }, ["createdAt"] = Now, ["body"] = new JsonObject { ["url"] = "x" },
        });
        Assert.Equal("op-rejected", ItemWorld.Code(t.B.Process(t.PairAB, wrong)));
    }
}

/// <summary>N3 `share` (§3.0, §3.2): where and by whom, receiver-side admin check in its current roster,
/// archive ops verified at their own signed roster version (N4).</summary>
public sealed class ShareTests
{
    private static JsonObject SharePayload(string teamId, Dev target, string shareId) => new()
    {
        ["shareId"] = "hex:" + shareId, ["targetMemberId"] = "hex:" + target.MemberId, ["targetDeviceId"] = "hex:" + target.DeviceId,
        ["scopes"] = new JsonArray("hex:" + teamId), ["count"] = 0, ["sha256"] = "hex:" + Rand(32),
    };

    [Fact]
    public void Share_only_by_admin_in_team_group_or_to_self_in_personal_group()
    {
        using var t = new ItemWorld(project: true);
        string Share(Dev d, string gid, Dev target) { var s = Rand(16); return ItemWorld.TrySend(d, gid, "share", SharePayload(t.TeamId, target, s), itemId: s); }
        Assert.Equal("op-rejected", Share(t.A1, t.PairAB, t.B));         // pair
        Assert.Equal("op-rejected", Share(t.O, t.Project, t.B));         // project, even by the owner
        Assert.Equal("op-rejected", Share(t.B, t.Team, t.C));            // non-admin in team group
        var s1 = Rand(16);
        var forged = ItemWorld.Forge(t.B, t.Team, t.Header(t.B, t.Team, 7, t.B.MemberId, s1, 0, 1), SharePayload(t.TeamId, t.C, s1));
        Assert.Equal("op-rejected", ItemWorld.Code(t.C.Process(t.Team, forged)));
        var personal = t.W.CreateGroup(t.A1, t.W.Personal(t.A1));
        t.W.Add(t.A1, personal, t.A2);
        Assert.Equal("op-rejected", Share(t.A1, personal, t.B));         // personal, target != self
        Assert.Equal("ok", Share(t.A1, personal, t.A2));                 // personal, to own device
        Assert.Equal("ok", Share(t.X, t.Team, t.C));                     // admin in team group
    }

    /// <summary>C writes a space item and is removed; D joins later; admin X shares the team scope to D.</summary>
    private static (ItemWorld t, Dev d, string archive, string itemC, string itemA) Setup()
    {
        var t = new ItemWorld(pairs: false);
        var itemC = Rand(16); var itemA = Rand(16);
        t.Post(t.C, t.Team, "item.create", Link(t.TeamId, "by C"), itemId: itemC);
        t.Post(t.A1, t.Team, "item.create", Link(t.TeamId, "by A"), itemId: itemA);
        t.W.Update(t.O, new() { ["removeMembers"] = Arr(t.C.MemberId) });
        t.W.Deliver(t.Team, t.O.Call("group.rotate", new() { ["groupId"] = t.Team })["commit"].S(), t.O);
        var d = t.W.Device("D");
        t.W.AddMember(t.O, d);
        t.W.Add(t.O, t.Team, d);
        Assert.Null(d.Item(t.C.MemberId, itemC));                         // D-4: nothing from before its epoch
        var shareId = Rand(16);
        var b = t.X.Call("share.build", new() { ["teamId"] = t.TeamId, ["targetMemberId"] = d.MemberId, ["targetDeviceId"] = d.DeviceId,
            ["scopes"] = Arr(t.TeamId), ["shareId"] = shareId });
        Assert.Equal(2UL, b["count"].U());
        t.Post(t.X, t.Team, "share", (JsonObject)b["payload"]!.DeepClone(), itemId: shareId);
        return (t, d, b["archive"].S(), itemC, itemA);
    }

    [Fact]
    public void Archive_ops_verified_at_their_own_roster_version_without_trusting_the_admin()
    {
        var (t, d, archive, itemC, itemA) = Setup();
        using var _ = t;
        var r = d.Call("share.accept", new() { ["teamId"] = t.TeamId, ["archive"] = archive });
        Assert.Equal(2UL, r["accepted"].U());
        Assert.Empty((JsonArray)r["rejected"]!);
        Assert.Equal("by C", d.Item(t.C.MemberId, itemC)!["body"]!["url"].S());   // C removed since, but signed while active
        Assert.NotNull(d.Item(t.A1.MemberId, itemA));
        Assert.Equal("op-rejected", d.Try("share.accept", new() { ["teamId"] = t.TeamId, ["archive"] = archive }));   // consumed
    }

    [Fact]
    public void Archive_from_admin_demoted_before_acceptance_rejected()
    {
        var (t, d, archive, _, _) = Setup();
        using var _t = t;
        t.W.Update(t.O, new() { ["setRoles"] = new JsonArray(new JsonObject { ["memberId"] = t.X.MemberId, ["role"] = "member" }) });
        var r = d.Raw("share.accept", new() { ["teamId"] = t.TeamId, ["archive"] = archive });
        Assert.Equal("op-rejected", ItemWorld.Code(r));
        Assert.Contains("no longer an admin", r["error"].S());
        // An archive without a matching signed share event is refused as a whole.
        var other = t.O.Call("share.build", new() { ["teamId"] = t.TeamId, ["targetMemberId"] = d.MemberId, ["targetDeviceId"] = d.DeviceId,
            ["scopes"] = Arr(t.TeamId), ["shareId"] = Rand(16) })["archive"].S();
        Assert.Equal("op-rejected", d.Try("share.accept", new() { ["teamId"] = t.TeamId, ["archive"] = other }));
    }
}

/// <summary>Heartbeat acceptance (§4.1, R2-02): owner counts alone, admins by quorum k_hb, removed admin is one vote at most,
/// foreign epochProof is not counted.</summary>
public sealed class HeartbeatTests
{
    private sealed class T : IDisposable
    {
        public readonly World W = new();
        public readonly Dev O, X, Y, K;
        public readonly string G;
        public T()
        {
            O = W.Device("O"); X = W.Device("X"); Y = W.Device("Y"); K = W.Device("K");
            W.CreateTeam(O, quorum: 2);
            W.AddMember(O, X, "admin"); W.AddMember(O, Y, "admin"); W.AddMember(O, K);
            G = W.CreateGroup(O, W.Team());
            W.Add(O, G, X, Y, K);
        }
        public string Hb(Dev d, ulong? time = null) => d.Call("heartbeat.create", new() { ["teamId"] = W.TeamId, ["time"] = time ?? Now })["heartbeat"].S();
        public JsonNode Accept(Dev at, string hb) => at.Call("heartbeat.accept", new() { ["heartbeat"] = hb });
        public JsonNode Status(Dev at) => at.Call("team.status", new() { ["teamId"] = W.TeamId, ["now"] = Now })["confirmation"]!;
        public void Dispose() => W.Dispose();
    }

    [Fact]
    public void Owner_heartbeat_confirms_alone()
    {
        using var t = new T();
        Assert.False(t.Status(t.K)["confirmed"].B());
        Assert.True(t.Accept(t.K, t.Hb(t.O))["counted"].B());
        var s = t.Status(t.K);
        Assert.True(s["confirmed"].B() && s["byOwner"].B());
        // A member cannot publish heartbeats; a stale one (outside T) does not confirm.
        Assert.Equal("invalid-input", t.K.Try("heartbeat.create", new() { ["teamId"] = t.W.TeamId, ["time"] = Now }));
        using var t2 = new T();
        Assert.True(t2.Accept(t2.K, t2.Hb(t2.O, Now - 80 * 3600))["counted"].B());
        Assert.False(t2.Status(t2.K)["confirmed"].B());
    }

    [Fact]
    public void Removed_admin_alone_does_not_confirm_with_quorum_two()
    {
        using var t = new T();
        // X is removed; K is isolated by the server (never gets the new version), X keeps signing at the old one.
        t.W.Update(t.O, new() { ["removeMembers"] = Arr(t.X.MemberId) }, t.K, t.X);
        var hbX = t.Hb(t.X);
        Assert.True(t.Accept(t.K, hbX)["counted"].B());
        var s = t.Status(t.K);
        Assert.False(s["confirmed"].B());
        Assert.Equal(1UL, s["adminVotes"].U());
        Assert.Equal(2UL, s["required"].U());
        // At a client that has the removal, X's heartbeat is not counted at all.
        var atY = t.Accept(t.Y, hbX);
        Assert.False(atY["counted"].B());
        // An honest admin's heartbeat names the newer version: the isolated client learns it must fetch the roster.
        Assert.Equal("need-roster", t.K.Try("heartbeat.accept", new() { ["heartbeat"] = t.Hb(t.Y) }));
    }

    [Fact]
    public void Heartbeat_with_foreign_epochProof_is_not_counted()
    {
        using var t = new T();
        // The server forks the group: O's commit reaches X, Y's commit reaches K (same epoch number, other secrets).
        var cO = t.O.Call("group.update", new() { ["groupId"] = t.G })["commit"].S();
        var cY = t.Y.Call("group.update", new() { ["groupId"] = t.G })["commit"].S();
        Assert.True(t.X.Process(t.G, cO)["ok"].B());
        Assert.True(t.K.Process(t.G, cY)["ok"].B());
        Assert.Equal(t.X.Epoch(t.G), t.K.Epoch(t.G));
        var r = t.Accept(t.K, t.Hb(t.X));
        Assert.False(r["counted"].B());
        Assert.Contains("epochProof", r["reason"].S());
        Assert.True(t.Accept(t.K, t.Hb(t.Y))["counted"].B());               // same branch: counted
    }
}
