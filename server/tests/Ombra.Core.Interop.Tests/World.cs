using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ombra.Core.Interop;

namespace Ombra.Core.Interop.Tests;

public static class H
{
    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);
    public static byte[] Bytes(string hex) => Convert.FromHexString(hex);
    public static string Rand(int n) => Hex(RandomNumberGenerator.GetBytes(n));
    public static ulong Now => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public static string S(this JsonNode? n) => n!.GetValue<string>();
    public static ulong U(this JsonNode? n) => n!.GetValue<ulong>();
    public static bool B(this JsonNode? n) => n!.GetValue<bool>();
    public static JsonArray Arr(params string[] xs) => new(xs.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
    public static string Code(Action a)
    {
        try { a(); return "ok"; }
        catch (OmbraException e) { return e.Code; }
    }

    /// <summary><c>link</c> create payload in a space scope (teamId or projectId).</summary>
    public static JsonObject Link(string scope, string url) => new()
    {
        ["kind"] = "link", ["audience"] = new JsonObject { ["space"] = "hex:" + scope }, ["createdAt"] = Now, ["body"] = new JsonObject { ["url"] = url },
    };
}

/// <summary>One device: its own encrypted state file. With <see cref="RestartEachCall"/> every call
/// first closes and reopens the state from disk (the "restart between steps" requirement).</summary>
public sealed class Dev : IDisposable
{
    public readonly string Name;
    public readonly string DeviceId;
    public readonly string MemberId;
    public readonly World W;
    private readonly string _path;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _install = RandomNumberGenerator.GetBytes(16);
    private OmbraState _s;
    public bool RestartEachCall;
    public readonly JsonNode Info;
    public int Restarts;

    public Dev(World w, string name, string memberId, string? deviceId = null)
    {
        W = w; Name = name; MemberId = memberId; DeviceId = deviceId ?? H.Rand(16);
        _path = Path.Combine(w.Dir, $"{name}-{Guid.NewGuid():N}.state");
        _s = OmbraState.Open(_path, _key, _install);
        Info = _s.Call("device.init", new() { ["deviceId"] = DeviceId, ["memberId"] = MemberId });
        RestartEachCall = w.RestartEachCall;
    }

    public string IdentityKey => Info["identityKey"].S();
    public string MlsKey => Info["mlsSignatureKey"].S();
    public string Fingerprint => Info["identityFingerprint"].S();
    public string StatePath => _path;
    public byte[] Key => _key;
    public byte[] InstallId => _install;

    public void Restart()
    {
        _s.Dispose();
        _s = OmbraState.Open(_path, _key, _install);
        foreach (var (team, key) in TeamKeys) OmbraJson.Unwrap(_s.CallRaw("team.unlock", new() { ["teamId"] = team, ["key"] = key }));
        Restarts++;
    }

    /// <summary>Per-team row keys returned by team.create/team.import (the app keeps them in the OS key store).</summary>
    public readonly Dictionary<string, string> TeamKeys = [];

    public JsonObject Raw(string op, JsonObject? a = null)
    {
        if (RestartEachCall) Restart();
        var r = _s.CallRaw(op, a);
        if (r["ok"].B() && r["result"]?["teamKey"] is { } k) TeamKeys[r["result"]!["teamId"].S()] = k.S();
        if (op == "team.forget" && r["ok"].B()) TeamKeys.Remove(a!["teamId"].S());
        return r;
    }

    public JsonNode Call(string op, JsonObject? a = null) => OmbraJson.Unwrap(Raw(op, a));

    /// <summary>Error code of a call ("ok" if it succeeded).</summary>
    public string Try(string op, JsonObject? a = null)
    {
        var r = Raw(op, a);
        return r["ok"].B() ? "ok" : r["code"].S();
    }

    public string SelfCert() => Call("device.certificate", new() { ["self"] = true, ["serverId"] = W.ServerId, ["issuedAt"] = H.Now, ["pairingNonce"] = H.Rand(16) })["cert"].S();

    public string CertFor(Dev d) => Call("device.certificate", new()
    {
        ["serverId"] = W.ServerId, ["memberId"] = d.MemberId, ["deviceId"] = d.DeviceId, ["identityKey"] = d.IdentityKey,
        ["mlsSignatureKey"] = d.MlsKey, ["issuedAt"] = H.Now, ["pairingNonce"] = H.Rand(16),
    })["cert"].S();

    public string KeyPackage(ulong? lifetime = null, bool lastResort = false)
    {
        var a = new JsonObject { ["lastResort"] = lastResort };
        if (lifetime is { } l) a["lifetime"] = l;
        return Call("key_package", a)["keyPackage"].S();
    }

    public long Epoch(string gid) => (long)Call("group.info", new() { ["groupId"] = gid })["epoch"].U();
    public int Members(string gid) => ((JsonArray)Call("group.info", new() { ["groupId"] = gid })["members"]!).Count;
    public int TreeState(string gid) => (int)Call("group.tree_state", new() { ["groupId"] = gid })["state"].U();
    public string Export(string gid, string label = "ombra/attachment-wrap") => Call("group.export", new() { ["groupId"] = gid, ["label"] = label })["secret"].S();

    public JsonObject Process(string gid, string msg) => Raw("group.process", new() { ["groupId"] = gid, ["message"] = msg, ["now"] = H.Now });

    public JsonNode Send(string gid, string opType, JsonObject payload, string? itemId = null, ulong? rev = null, string? recipient = null,
        string? author = null, string? opId = null, ulong? seq = null)
    {
        var a = new JsonObject { ["groupId"] = gid, ["opType"] = opType, ["payload"] = payload, ["now"] = H.Now };
        if (rev != null) a["rev"] = rev;
        if (itemId != null) a["itemId"] = itemId;
        if (recipient != null) a["recipient"] = recipient;
        if (author != null) a["author"] = author;
        if (opId != null) a["opId"] = opId;
        if (seq != null) a["seq"] = seq;
        return Call("op.send", a);
    }

    /// <summary>Replica view of an item, or null if the replica has no such key.</summary>
    public JsonNode? Item(string author, string itemId)
    {
        var r = Raw("item.get", new() { ["teamId"] = W.TeamId, ["author"] = author, ["itemId"] = itemId });
        OmbraJson.Unwrap(r);
        return r["result"];
    }

    public override string ToString() => Name;
    public void Dispose() => _s.Dispose();
}

/// <summary>A simulated team: the "server" is just this object relaying roster updates and MLS bytes.</summary>
public sealed class World : IDisposable
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "ombra-core-tests", Guid.NewGuid().ToString("N"));
    public readonly string ServerId;
    public readonly List<Dev> Devs = [];
    public readonly List<string> Updates = [];
    public readonly HashSet<Dev> HasTeam = [];
    public readonly Dictionary<string, List<Dev>> Groups = [];
    public readonly bool RestartEachCall;
    public string TeamId = "";
    public string TeamGroup = "";
    public readonly string RecoverySk;
    public readonly string RecoveryPk;

    public World(bool restartEachCall = false, string? serverId = null)
    {
        RestartEachCall = restartEachCall;
        ServerId = serverId ?? H.Rand(16);
        Directory.CreateDirectory(Dir);
        var k = OmbraPublic.Call("ed25519.keygen");
        RecoverySk = k["secretKey"].S();
        RecoveryPk = k["publicKey"].S();
    }

    public Dev Device(string name, string? memberId = null, string? deviceId = null)
    {
        var d = new Dev(this, name, memberId ?? H.Rand(16), deviceId);
        Devs.Add(d);
        return d;
    }

    public Dev CreateTeam(Dev owner, ulong quorum = 2, ulong timeLock = 72 * 3600)
    {
        var r = owner.Call("team.create", new()
        {
            ["serverId"] = ServerId, ["recoveryKey"] = RecoveryPk, ["issuedAt"] = H.Now, ["heartbeatQuorum"] = quorum, ["timeLock"] = timeLock,
        });
        TeamId = r["teamId"].S();
        Updates.Add(r["update"].S());
        HasTeam.Add(owner);
        return owner;
    }

    public (ulong version, string hash) Head(Dev d)
    {
        var h = d.Call("roster.head", new() { ["teamId"] = TeamId });
        return (h["version"].U(), h["hash"].S());
    }

    /// <summary>Sign an update with <paramref name="by"/>, apply it everywhere (the signer included).</summary>
    public string Update(Dev by, JsonObject edits, params Dev[] skip)
    {
        edits["teamId"] = TeamId;
        edits["now"] = H.Now;
        var u = by.Call("roster.update", edits)["update"].S();
        Publish(u, skip);
        return u;
    }

    public void Publish(string update, params Dev[] skip)
    {
        Updates.Add(update);
        foreach (var d in HasTeam.Where(d => !skip.Contains(d)))
            d.Call("roster.apply", new() { ["teamId"] = TeamId, ["update"] = update, ["now"] = H.Now });
    }

    /// <summary>Invitee/new device imports the chain from the "server", pinned to the current head.</summary>
    public void Import(Dev d, Dev inviter, int? upto = null)
    {
        var ups = upto is { } n ? Updates.Take(n).ToList() : Updates;
        var head = upto is { } m ? (ulong)m : (ulong)Updates.Count;
        var hash = OmbraPublic.Call("roster.parse", new() { ["update"] = ups[^1] })["hash"].S();
        d.Call("team.import", new()
        {
            ["teamId"] = TeamId, ["serverId"] = ServerId, ["updates"] = H.Arr(ups.ToArray()), ["now"] = H.Now,
            ["rosterHead"] = new JsonObject { ["version"] = head, ["hash"] = hash }, ["inviterFingerprint"] = inviter.Fingerprint,
        });
        HasTeam.Add(d);
    }

    public void AddMember(Dev admin, Dev newcomer, string role = "member", params Dev[] extraDevices)
    {
        var certs = new List<string> { newcomer.SelfCert() };
        foreach (var x in extraDevices) certs.Add(newcomer.CertFor(x));
        Update(admin, new() { ["addMembers"] = new JsonArray(new JsonObject { ["memberId"] = newcomer.MemberId, ["role"] = role, ["certs"] = H.Arr(certs.ToArray()) }) });
        Import(newcomer, admin);
        foreach (var x in extraDevices) Import(x, admin);
    }

    /// <summary>Pair a new device of an existing member: certificate issued by <paramref name="issuer"/>.</summary>
    public void AddDevice(Dev issuer, Dev newDev)
    {
        Update(issuer, new() { ["addDevices"] = H.Arr(issuer.CertFor(newDev)) });
        Import(newDev, issuer);
    }

    public JsonObject Team() => new() { ["type"] = "team", ["teamId"] = TeamId, ["generation"] = 0 };
    public JsonObject Project(string projectId) => new() { ["type"] = "project", ["teamId"] = TeamId, ["projectId"] = projectId, ["generation"] = 0 };
    public JsonObject Pair(Dev a, Dev b, uint gen = 0)
    {
        var ms = new[] { a.MemberId, b.MemberId }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return new() { ["type"] = "pair", ["teamId"] = TeamId, ["members"] = H.Arr(ms), ["generation"] = gen };
    }
    public JsonObject Personal(Dev o) => new() { ["type"] = "personal", ["teamId"] = TeamId, ["owner"] = o.MemberId, ["generation"] = 0 };
    public static string Gid(JsonObject desc) => OmbraPublic.Call("descriptor.group_id", new() { ["descriptor"] = desc.DeepClone() })["groupId"].S();
    public static string DescBytes(JsonObject desc) => OmbraPublic.Call("descriptor.group_id", new() { ["descriptor"] = desc.DeepClone() })["canonical"].S();

    public string CreateGroup(Dev creator, JsonObject desc)
    {
        var gid = creator.Call("group.create", new() { ["descriptor"] = desc })["groupId"].S();
        Groups[gid] = [creator];
        if (desc["type"].S() == "team") TeamGroup = gid;
        return gid;
    }

    /// <summary>Committer adds each joiner (one commit per device); every current member processes; joiner joins.</summary>
    public void Add(Dev committer, string gid, params Dev[] joiners)
    {
        foreach (var j in joiners)
        {
            var r = committer.Call("group.add", new() { ["groupId"] = gid, ["keyPackages"] = H.Arr(j.KeyPackage()), ["now"] = H.Now });
            Deliver(gid, r["commit"].S(), committer);
            j.Call("group.join", new() { ["welcome"] = r["welcome"].S() });
            Groups[gid].Add(j);
        }
    }

    /// <summary>Deliver a commit to all group members except the sender; each must accept.</summary>
    public void Deliver(string gid, string commit, params Dev[] except)
    {
        foreach (var d in Groups[gid].Where(d => !except.Contains(d)).ToList())
        {
            var r = d.Process(gid, commit);
            if (!r["ok"].B()) throw new Xunit.Sdk.XunitException($"{d} rejected commit: {r["code"]} {r["error"]}");
            if (r["result"]!["kind"].S() == "removed") Groups[gid].Remove(d);
        }
    }

    public void Dispose()
    {
        foreach (var d in Devs) d.Dispose();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}
