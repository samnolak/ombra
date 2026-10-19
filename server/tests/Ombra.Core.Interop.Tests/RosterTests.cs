using System.Text.Json.Nodes;
using Ombra.Core.Interop;
using static Ombra.Core.Interop.Tests.H;

namespace Ombra.Core.Interop.Tests;

/// <summary>§4.1 roster validator: field rules by role, N2b, cascade, equivocation, R2-07, D-3, RemovalNotice.</summary>
public sealed class RosterTests
{
    /// <summary>Owner O, admins X and Y, members M (M1, M2) and K.</summary>
    private sealed class T : IDisposable
    {
        public readonly World W = new();
        public readonly Dev O, X, Y, M1, M2, K;
        public T()
        {
            O = W.Device("O"); X = W.Device("X"); Y = W.Device("Y"); M1 = W.Device("M1"); M2 = W.Device("M2", M1.MemberId); K = W.Device("K");
            W.CreateTeam(O);
            W.AddMember(O, X, "admin");
            W.AddMember(O, Y, "admin");
            W.AddMember(O, M1, "member", M2);
            W.AddMember(O, K);
        }
        /// <summary>Build+sign an update by <paramref name="by"/>; return the code of applying it at O.</summary>
        public string Apply(Dev by, JsonObject edits, ulong? now = null)
        {
            edits["teamId"] = W.TeamId; edits["now"] = Now;
            var u = by.Raw("roster.update", edits);
            if (!u["ok"].B()) { LastError = u["error"].S(); return u["code"].S(); }
            var r = O.Raw("roster.apply", new() { ["teamId"] = W.TeamId, ["update"] = u["result"]!["update"].S(), ["now"] = now ?? Now });
            LastError = r["ok"].B() ? "" : r["error"].S();
            return r["ok"].B() ? "ok" : r["code"].S();
        }
        public string LastError = "";
        public string Why(string code, string reason)
        {
            Assert.Contains(reason, LastError);
            return code;
        }
        public string Publish(Dev by, JsonObject edits) { W.Update(by, edits); return "ok"; }
        public void Dispose() => W.Dispose();
    }

    private static JsonObject AddMember(Dev d, string role = "member") => new()
    {
        ["addMembers"] = new JsonArray(new JsonObject { ["memberId"] = d.MemberId, ["role"] = role, ["certs"] = Arr(d.SelfCert()) }),
    };
    private static JsonObject Role(Dev d, string role) => new() { ["setRoles"] = new JsonArray(new JsonObject { ["memberId"] = d.MemberId, ["role"] = role }) };
    private static JsonObject Revoke(Dev d, ulong? since = null)
    {
        var o = new JsonObject { ["deviceId"] = d.DeviceId };
        if (since is { } s) o["compromisedSince"] = s;
        return new() { ["revokeDevices"] = new JsonArray(o) };
    }

    [Fact]
    public void Field_rules_follow_the_signers_role_in_the_previous_version()
    {
        using var t = new T();
        var n = t.W.Device("N");
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.K, AddMember(n)), "no signer whose role"));                       // member adds a member
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.X, AddMember(n, "admin")), "no signer whose role"));              // admin appoints an admin
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.X, Role(t.K, "admin")), "no signer whose role"));                 // admin promotes
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.X, Role(t.Y, "member")), "no signer whose role"));                // admin demotes an admin
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.X, new() { ["removeMembers"] = Arr(t.Y.MemberId) }), "no signer whose role")); // admin removes an admin
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.X, new() { ["heartbeatQuorum"] = 1 }), "heartbeatQuorum"));      // owner-only field
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.X, new() { ["setOwner"] = t.X.MemberId }), "owner"));            // SD-05: admin makes itself owner
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.X, new() { ["removeMembers"] = Arr(t.O.MemberId) }), "owner"));  // remove owner
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.K, Revoke(t.M2)), "revoke device"));                             // member revokes another member's device
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.K, new() { ["pins"] = Arr(Rand(32)) }), "pins"));                // member changes pins
        Assert.Equal("ok", t.Apply(t.X, new() { ["pins"] = Arr(Rand(32)) }));               // admin: pins
    }

    [Fact]
    public void Permitted_changes_by_role()
    {
        using var t = new T();
        Assert.Equal("ok", t.Publish(t.O, new() { ["heartbeatQuorum"] = 1 }));
        Assert.Equal("ok", t.Publish(t.O, Role(t.K, "admin")));
        Assert.Equal("ok", t.Publish(t.X, AddMember(t.W.Device("N"))));
        Assert.Equal("ok", t.Publish(t.M1, Revoke(t.M2)));                                   // own device
        Assert.Equal("ok", t.Publish(t.X, new() { ["grantAccess"] = new JsonArray(new JsonObject { ["projectId"] = Rand(16), ["memberId"] = t.M1.MemberId }) }));
        Assert.Equal("ok", t.Publish(t.K, new() { ["removeMembers"] = Arr(t.K.MemberId) }));  // leave (K is admin now)
    }

    [Fact]
    public void N2b_removed_member_or_revoked_device_never_comes_back()
    {
        using var t = new T();
        t.W.Update(t.O, new() { ["removeMembers"] = Arr(t.K.MemberId) });
        var k2 = t.W.Device("K-again", t.K.MemberId);
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.O, AddMember(k2)), "already appeared"));
        t.W.Update(t.M1, Revoke(t.M2));
        var m2again = t.W.Device("M2-again", t.M1.MemberId, t.M2.DeviceId);
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.M1, new() { ["addDevices"] = Arr(t.M1.CertFor(m2again)) }), "listed twice"));
        // The deviceId of the removed member K, offered for a brand-new member, is refused too.
        var stranger = t.W.Device("stranger", null, t.K.DeviceId);
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.O, AddMember(stranger)), "already appeared"));
    }

    [Fact]
    public void Certificates_bind_member_server_and_issuer()
    {
        using var t = new T();
        var newDev = t.W.Device("M3", t.M1.MemberId);
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.K, new() { ["addDevices"] = Arr(t.K.CertFor(newDev)) }), "another member"));
        using var other = new World();
        var foreign = other.Device("f", t.M1.MemberId);
        var foreignCert = foreign.SelfCert(); // other serverId, self-issued for an existing member
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.M1, new() { ["addDevices"] = Arr(foreignCert) }), "another server"));
        Assert.Equal("ok", t.Apply(t.M1, new() { ["addDevices"] = Arr(t.M1.CertFor(newDev)) }));
    }

    [Fact]
    public void R2_10_cascade_revocation_by_roster_version()
    {
        using var t = new T();
        var m3 = t.W.Device("M3", t.M1.MemberId);
        t.W.AddDevice(t.M2, m3);               // M3 issued by M2
        var addedIn = t.W.Head(t.O).version;
        // M2 compromised since before M3 appeared. roster.update cascades by itself; a hand-built update that revokes
        // M2 alone (M3 left active) is refused by the validator, the cascaded one is accepted.
        var built = t.M1.Call("roster.update", new() { ["teamId"] = t.W.TeamId, ["now"] = Now, ["sign"] = false,
            ["revokeDevices"] = new JsonArray(new JsonObject { ["deviceId"] = t.M2.DeviceId, ["compromisedSince"] = addedIn - 1 }) });
        Assert.Equal(m3.DeviceId, built["cascaded"]!.AsArray().Single()!.GetValue<string>());
        var projection = built["projection"]!.DeepClone().AsObject();
        foreach (var m in projection["members"]!.AsArray())
            foreach (var d in m!["devices"]!.AsArray())
                if (d!["deviceId"]!.GetValue<string>() == m3.DeviceId) d.AsObject().Remove("revoked");
        var partial = t.M1.Call("roster.sign", new() { ["projection"] = projection })["update"]!.GetValue<string>();
        Assert.Equal("roster-rejected", t.M1.Try("roster.apply", new() { ["teamId"] = t.W.TeamId, ["update"] = partial, ["now"] = Now }));
        Assert.Equal("ok", t.Apply(t.M1, Revoke(t.M2, addedIn - 1)));
    }

    [Fact]
    public void Equivocation_freezes_the_team_and_persists()
    {
        using var t = new T();
        var head = t.W.Head(t.O).version;
        var u1 = t.X.Call("roster.update", new() { ["teamId"] = t.W.TeamId, ["now"] = Now, ["pins"] = Arr(Rand(32)) })["update"].S();
        var u2 = t.Y.Call("roster.update", new() { ["teamId"] = t.W.TeamId, ["now"] = Now, ["pins"] = Arr(Rand(32)) })["update"].S();
        t.K.Call("roster.apply", new() { ["teamId"] = t.W.TeamId, ["update"] = u1, ["now"] = Now });
        Assert.Equal("equivocation", t.K.Try("roster.apply", new() { ["teamId"] = t.W.TeamId, ["update"] = u2, ["now"] = Now }));
        t.K.Restart();
        var st = t.K.Call("team.status", new() { ["teamId"] = t.W.TeamId, ["now"] = Now });
        Assert.True(st["frozen"].B());
        Assert.Equal(head + 1, st["equivocation"]!["version"].U());
        Assert.Equal("equivocation", t.K.Try("roster.apply", new() { ["teamId"] = t.W.TeamId, ["update"] = u1, ["now"] = Now }));
        // A conflicting version that is NOT validly signed is just rejected, no freeze.
        using var t2 = new T();
        var ok = t2.X.Call("roster.update", new() { ["teamId"] = t2.W.TeamId, ["now"] = Now, ["pins"] = Arr(Rand(32)) })["update"].S();
        var bad = t2.K.Call("roster.update", new() { ["teamId"] = t2.W.TeamId, ["now"] = Now, ["pins"] = Arr(Rand(32)) })["update"].S();
        t2.M1.Call("roster.apply", new() { ["teamId"] = t2.W.TeamId, ["update"] = ok, ["now"] = Now });
        Assert.Equal("roster-rejected", t2.M1.Try("roster.apply", new() { ["teamId"] = t2.W.TeamId, ["update"] = bad, ["now"] = Now }));
        Assert.False(t2.M1.Call("team.status", new() { ["teamId"] = t2.W.TeamId, ["now"] = Now })["frozen"].B());
    }

    private static JsonObject ApplyArgs(T t, string update) => new() { ["teamId"] = t.W.TeamId, ["update"] = update, ["now"] = Now };
    private static bool Frozen(Dev d, T t) => d.Call("team.status", new() { ["teamId"] = t.W.TeamId, ["now"] = Now })["frozen"].B();
    private static string HashOf(string update) => OmbraPublic.Call("roster.parse", new() { ["update"] = update })["hash"].S();

    [Fact]
    public void Member_signed_alternative_is_a_conflict_and_never_freezes_the_team()
    {
        using var t = new T();
        var u1 = t.X.Call("roster.update", new() { ["teamId"] = t.W.TeamId, ["now"] = Now, ["pins"] = Arr(Rand(32)) })["update"].S();
        t.K.Call("roster.apply", ApplyArgs(t, u1));
        // A member signs validly-formed alternatives of the same number: privateCommit, cause, an own device.
        var pc = t.M1.Call("roster.update", new() { ["teamId"] = t.W.TeamId, ["now"] = Now, ["privateCommit"] = Rand(32) })["update"].S();
        var cause = t.M1.Call("roster.update", new() { ["teamId"] = t.W.TeamId, ["now"] = Now,
            ["cause"] = new JsonObject { ["kind"] = "app", ["auditId"] = Rand(16) } })["update"].S();
        var m3 = t.W.Device("M3", t.M1.MemberId);
        var dev = t.M1.Call("roster.update", new() { ["teamId"] = t.W.TeamId, ["now"] = Now, ["addDevices"] = Arr(t.M1.CertFor(m3)) })["update"].S();
        foreach (var alt in new[] { pc, cause, dev })
        {
            Assert.Equal("roster-conflict", t.K.Try("roster.apply", ApplyArgs(t, alt)));
            Assert.False(Frozen(t.K, t));
        }
        // The server's stateless verifier agrees: conflict, no frozen chain state.
        byte[]? st = null;
        foreach (var u in t.W.Updates.Append(u1)) st = OmbraPublic.VerifyRosterAppend(st, Bytes(u), Bytes(t.W.ServerId), Now).ChainState;
        Assert.Equal("roster-conflict", Assert.Throws<OmbraException>(() => OmbraPublic.VerifyRosterAppend(st, Bytes(pc), Bytes(t.W.ServerId), Now)).Code);
        // The team keeps working: the next version applies.
        t.X.Call("roster.apply", ApplyArgs(t, u1));
        var next = t.X.Call("roster.update", new() { ["teamId"] = t.W.TeamId, ["now"] = Now, ["pins"] = Arr(Rand(32)), ["apply"] = true })["update"].S();
        t.K.Call("roster.apply", ApplyArgs(t, next));
    }

    [Fact]
    public void Owner_resolution_unfreezes_on_the_chosen_branch_and_the_rejected_version_never_returns()
    {
        foreach (var chosen in new[] { "conflicting", "known" })
        {
            using var t = new T();
            var tid = t.W.TeamId;
            var u1 = t.X.Call("roster.update", new() { ["teamId"] = tid, ["now"] = Now, ["pins"] = Arr(Rand(32)) })["update"].S();
            var u2 = t.Y.Call("roster.update", new() { ["teamId"] = tid, ["now"] = Now, ["pins"] = Arr(Rand(32)) })["update"].S();
            foreach (var d in new[] { t.O, t.X, t.K })
            {
                d.Call("roster.apply", ApplyArgs(t, u1));
                Assert.Equal("equivocation", d.Try("roster.apply", ApplyArgs(t, u2)));
            }
            // Only an owner device of the common parent decides; an admin's "resolution" is refused.
            var byAdmin = t.X.Call("roster.resolution_create", new() { ["teamId"] = tid, ["chosen"] = chosen })["resolution"].S();
            Assert.Equal("roster-rejected", t.K.Try("roster.resolve", new() { ["teamId"] = tid, ["resolution"] = byAdmin, ["now"] = Now }));
            Assert.True(Frozen(t.K, t));

            var res = t.O.Call("roster.resolution_create", new() { ["teamId"] = tid, ["chosen"] = chosen })["resolution"].S();
            var (keep, drop) = chosen == "known" ? (u1, u2) : (u2, u1);
            Assert.Equal(chosen == "conflicting", t.K.Call("roster.resolve", new() { ["teamId"] = tid, ["resolution"] = res, ["now"] = Now })["switched"].B());
            t.K.Restart();
            var st = t.K.Call("team.status", new() { ["teamId"] = tid, ["now"] = Now });
            Assert.False(st["frozen"].B());
            Assert.Equal(HashOf(keep), st["hash"].S());
            // The rejected version is a conflict from now on, never a new freeze.
            Assert.Equal("roster-conflict", t.K.Try("roster.apply", ApplyArgs(t, drop)));
            Assert.False(Frozen(t.K, t));
            // The owner applies its own decision and the team moves on.
            t.O.Call("roster.resolve", new() { ["teamId"] = tid, ["resolution"] = res, ["now"] = Now });
            var next = t.O.Call("roster.update", new() { ["teamId"] = tid, ["now"] = Now, ["pins"] = Arr(Rand(32)), ["apply"] = true })["update"].S();
            t.K.Call("roster.apply", ApplyArgs(t, next));

            // Server (stateless) path: the same resolution unfreezes the server's chain state.
            byte[]? cs = null;
            foreach (var u in t.W.Updates.Append(u1)) cs = OmbraPublic.VerifyRosterAppend(cs, Bytes(u), Bytes(t.W.ServerId), Now).ChainState;
            var frozen = OmbraPublic.VerifyRosterAppend(cs, Bytes(u2), Bytes(t.W.ServerId), Now);
            Assert.True(frozen.Equivocation);
            JsonObject Resolve(byte[] state, string resolution) => new()
            {
                ["chainState"] = Hex(state), ["resolution"] = resolution, ["serverId"] = t.W.ServerId, ["now"] = Now,
            };
            Assert.Equal("roster-rejected", Assert.Throws<OmbraException>(() => OmbraPublic.Call("roster.resolve", Resolve(frozen.ChainState, byAdmin))).Code);
            var resolved = OmbraPublic.Call("roster.resolve", Resolve(frozen.ChainState, res));
            Assert.Equal(HashOf(keep), resolved["hash"].S());
            Assert.Equal(chosen == "conflicting", resolved["switched"].B());
            Assert.Equal("roster-conflict", Assert.Throws<OmbraException>(() =>
                OmbraPublic.VerifyRosterAppend(Bytes(resolved["chainState"].S()), Bytes(drop), Bytes(t.W.ServerId), Now)).Code);
        }
    }

    private static JsonObject Pins(T t, bool apply = false) => new() { ["teamId"] = t.W.TeamId, ["now"] = Now, ["pins"] = Arr(Rand(32)), ["apply"] = apply };
    private static JsonObject Edit(T t, JsonObject edits) { edits["teamId"] = t.W.TeamId; edits["now"] = Now; return edits; }
    private static JsonObject Resolve(T t, string resolution) => new() { ["teamId"] = t.W.TeamId, ["resolution"] = resolution, ["now"] = Now };

    [Fact]
    public void A_demoted_admins_alternative_is_a_conflict_not_equivocation()
    {
        using var t = new T();
        // X signs an alternative while still admin; the known chain then demotes X (signed by the owner).
        var alt = t.X.Call("roster.update", Pins(t))["update"].S();
        t.W.Update(t.O, Role(t.X, "member"));
        Assert.Equal("roster-conflict", t.K.Try("roster.apply", ApplyArgs(t, alt)));
        Assert.False(Frozen(t.K, t));
        // The same holds for an admin removed later in the known chain.
        var altY = t.Y.Call("roster.update", Pins(t))["update"].S();
        t.W.Update(t.O, new() { ["removeMembers"] = Arr(t.Y.MemberId) });
        Assert.Equal("roster-conflict", t.K.Try("roster.apply", ApplyArgs(t, altY)));
        Assert.False(Frozen(t.K, t));
    }

    [Fact]
    public void A_former_owners_alternative_ownership_transfer_is_a_conflict_not_equivocation()
    {
        using var t = new T();
        JsonObject Own(Dev to, string newRole)
        {
            var o = t.O.Call("ownership.offer", new() { ["teamId"] = t.W.TeamId, ["to"] = to.MemberId, ["newRoleOfFrom"] = newRole, ["expiresAt"] = Now + 3600 });
            var acc = to.Call("ownership.accept", new() { ["offer"] = o["offer"]!.DeepClone() });
            return new JsonObject { ["offer"] = o["offer"]!.DeepClone(), ["offerSig"] = o["offerSig"]!.DeepClone(), ["acceptSig"] = acc["acceptSig"]!.DeepClone() };
        }
        // O prepares an alternative Offer/Accept to Y, then the known chain hands ownership to X and makes O a member.
        var alt = t.O.Call("roster.update", Edit(t, new() { ["ownership"] = Own(t.Y, "admin") }))["update"].S();
        t.W.Update(t.O, new() { ["ownership"] = Own(t.X, "member") });
        Assert.Equal("roster-conflict", t.K.Try("roster.apply", ApplyArgs(t, alt)));
        Assert.False(Frozen(t.K, t));
    }

    [Fact]
    public void Resolution_needs_an_active_owner_device_at_the_known_head()
    {
        using var t = new T();
        var o2 = t.W.Device("O2", t.O.MemberId);
        t.W.AddDevice(t.O, o2);
        var conflicting = t.Y.Call("roster.update", Pins(t))["update"].S();
        var known = t.X.Call("roster.update", Pins(t))["update"].S();
        foreach (var d in new[] { t.K, t.O, o2 }) d.Call("roster.apply", ApplyArgs(t, known));
        // The owner revokes its device O2 right after the version that will conflict.
        var revoke = t.O.Call("roster.update", Edit(t, Revoke(o2)))["update"].S();
        foreach (var d in new[] { t.K, t.O, o2 }) d.Call("roster.apply", ApplyArgs(t, revoke));
        foreach (var d in new[] { t.K, t.O, o2 }) Assert.Equal("equivocation", d.Try("roster.apply", ApplyArgs(t, conflicting)));
        // O2 was an owner device in the common parent but is revoked at the head: its decision is refused.
        var byRevoked = o2.Call("roster.resolution_create", new() { ["teamId"] = t.W.TeamId, ["chosen"] = "conflicting" })["resolution"].S();
        Assert.Equal("roster-rejected", t.K.Try("roster.resolve", Resolve(t, byRevoked)));
        Assert.True(Frozen(t.K, t));
        var byOwner = t.O.Call("roster.resolution_create", new() { ["teamId"] = t.W.TeamId, ["chosen"] = "conflicting" })["resolution"].S();
        Assert.True(t.K.Call("roster.resolve", Resolve(t, byOwner))["switched"].B());
    }

    [Fact]
    public void Switching_to_a_branch_where_the_resolver_is_not_owner_is_refused()
    {
        using var t = new T();
        var o = t.O.Call("ownership.offer", new() { ["teamId"] = t.W.TeamId, ["to"] = t.X.MemberId, ["newRoleOfFrom"] = "admin", ["expiresAt"] = Now + 3600 });
        var acc = t.X.Call("ownership.accept", new() { ["offer"] = o["offer"]!.DeepClone() });
        var own = new JsonObject { ["offer"] = o["offer"]!.DeepClone(), ["offerSig"] = o["offerSig"]!.DeepClone(), ["acceptSig"] = acc["acceptSig"]!.DeepClone() };
        var transfer = t.O.Call("roster.update", Edit(t, new() { ["ownership"] = own }))["update"].S();
        var known = t.Y.Call("roster.update", Pins(t))["update"].S();
        foreach (var d in new[] { t.K, t.O })
        {
            d.Call("roster.apply", ApplyArgs(t, known));
            Assert.Equal("equivocation", d.Try("roster.apply", ApplyArgs(t, transfer)));
        }
        // O may not "choose" a branch in which it gives ownership away; keeping its ownership works.
        var giveAway = t.O.Call("roster.resolution_create", new() { ["teamId"] = t.W.TeamId, ["chosen"] = "conflicting" })["resolution"].S();
        Assert.Equal("roster-rejected", t.K.Try("roster.resolve", Resolve(t, giveAway)));
        var keep = t.O.Call("roster.resolution_create", new() { ["teamId"] = t.W.TeamId, ["chosen"] = "known" })["resolution"].S();
        Assert.False(t.K.Call("roster.resolve", Resolve(t, keep))["switched"].B());
    }

    [Fact]
    public void After_a_switch_ids_of_the_discarded_branch_never_return_and_groups_on_replaced_versions_close()
    {
        using var t = new T();
        var n = t.W.Device("N");
        // K's pair with X is created before the conflicting number, its pair with M1 on the version that gets replaced.
        var early = t.W.CreateGroup(t.K, t.W.Pair(t.K, t.X));
        var addN = t.O.Call("roster.update", Edit(t, AddMember(n)))["update"].S();
        var alt = t.Y.Call("roster.update", Pins(t))["update"].S();
        foreach (var d in new[] { t.K, t.O }) d.Call("roster.apply", ApplyArgs(t, addN));
        var late = t.W.CreateGroup(t.K, t.W.Pair(t.K, t.M1));
        foreach (var d in new[] { t.K, t.O }) Assert.Equal("equivocation", d.Try("roster.apply", ApplyArgs(t, alt)));
        var res = t.O.Call("roster.resolution_create", new() { ["teamId"] = t.W.TeamId, ["chosen"] = "conflicting" })["resolution"].S();
        foreach (var d in new[] { t.K, t.O }) Assert.True(d.Call("roster.resolve", Resolve(t, res))["switched"].B());
        // N2b: N appeared only in the discarded branch, yet its memberId/deviceId stay used.
        var again = t.O.Call("roster.update", Edit(t, AddMember(n)))["update"].S();
        Assert.Equal("roster-rejected", t.K.Try("roster.apply", ApplyArgs(t, again)));
        t.K.Restart();
        Assert.Equal("roster-rejected", t.K.Try("roster.apply", ApplyArgs(t, again)));
        // The group whose epoch references the replaced version is closed (a new generation is needed); the older one stays.
        var groups = ((JsonArray)t.K.Call("group.list")["groups"]!).ToDictionary(g => g!["groupId"].S(), g => g!["closed"].B());
        Assert.True(groups[late]);
        Assert.False(groups[early]);
    }

    [Fact]
    public void R2_07_projection_signed_without_private_part_and_names_checked_against_commit()
    {
        using var t = new T();
        var salt = Rand(32);
        var names = new JsonObject { ["displayNames"] = new JsonObject { [t.O.MemberId] = "Анна", [t.K.MemberId] = "Костя" } };
        var commit = OmbraPublic.Call("roster.private_commit", new() { ["salt"] = salt, ["privatePart"] = names.DeepClone() })["privateCommit"].S();
        t.W.Update(t.O, new() { ["privateCommit"] = commit });
        // The projection (what is signed and what the server sees) carries only the commitment.
        var proj = t.K.Call("roster.head", new() { ["teamId"] = t.W.TeamId })["projection"]!.ToJsonString();
        Assert.DoesNotContain("Анна", proj);
        Assert.Contains(commit, proj);
        Assert.True(t.K.Call("roster.check_private", new() { ["teamId"] = t.W.TeamId, ["salt"] = salt, ["privatePart"] = names.DeepClone() })["confirmed"].B());
        var tampered = new JsonObject { ["displayNames"] = new JsonObject { [t.O.MemberId] = "Анна", [t.K.MemberId] = "Mallory" } };
        Assert.False(t.K.Call("roster.check_private", new() { ["teamId"] = t.W.TeamId, ["salt"] = salt, ["privatePart"] = tampered })["confirmed"].B());
    }

    [Fact]
    public void D3_admins_cannot_change_owner_with_any_number_of_signatures()
    {
        using var t = new T();
        var u = t.X.Call("roster.update", new() { ["teamId"] = t.W.TeamId, ["now"] = Now, ["setOwner"] = t.X.MemberId,
            ["setRoles"] = new JsonArray(new JsonObject { ["memberId"] = t.X.MemberId, ["role"] = "owner" }, new JsonObject { ["memberId"] = t.O.MemberId, ["role"] = "member" }) })["update"].S();
        u = t.Y.Call("roster.cosign", new() { ["update"] = u })["update"].S();
        Assert.Equal("roster-rejected", t.K.Try("roster.apply", new() { ["teamId"] = t.W.TeamId, ["update"] = u, ["now"] = Now }));
    }

    [Fact]
    public void Ownership_offer_and_accept_transfer_the_owner()
    {
        using var t = new T();
        var o = t.O.Call("ownership.offer", new() { ["teamId"] = t.W.TeamId, ["to"] = t.X.MemberId, ["newRoleOfFrom"] = "admin", ["expiresAt"] = Now + 3600 });
        var acc = t.X.Call("ownership.accept", new() { ["offer"] = o["offer"]!.DeepClone() });
        var own = new JsonObject { ["offer"] = o["offer"]!.DeepClone(), ["offerSig"] = o["offerSig"]!.DeepClone(), ["acceptSig"] = acc["acceptSig"]!.DeepClone() };
        // Without the recipient's accept signature it is refused.
        var noAccept = new JsonObject { ["offer"] = o["offer"]!.DeepClone(), ["offerSig"] = o["offerSig"]!.DeepClone(), ["acceptSig"] = o["offerSig"]!.DeepClone() };
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.O, new() { ["ownership"] = noAccept }), "ownership signature"));
        Assert.Equal("ok", t.Publish(t.O, new() { ["ownership"] = own }));
        var head = t.K.Call("roster.head", new() { ["teamId"] = t.W.TeamId })["projection"]!;
        Assert.Equal(t.X.MemberId, head["owner"].S());
    }

    private static JsonObject Proposal(T t, Dev newOwner, ulong createdAt, ulong timeLock)
    {
        var (v, h) = t.W.Head(t.O);
        return new()
        {
            ["teamId"] = t.W.TeamId, ["baseVersion"] = v, ["basePrevHash"] = h, ["newOwner"] = newOwner.MemberId,
            ["newOwnerDevice"] = newOwner.DeviceId, ["createdAt"] = createdAt, ["notBefore"] = createdAt + timeLock,
        };
    }

    private static JsonObject Pending(T t, JsonObject proposal, string sk) => new()
    {
        ["proposal"] = proposal.DeepClone(),
        ["sig"] = OmbraPublic.Call("recovery.sign_proposal", new() { ["recoverySecretKey"] = sk, ["proposal"] = proposal.DeepClone() })["sig"].S(),
    };

    [Fact]
    public void D3_recovery_proposal_time_lock_veto_and_apply_with_heartbeat_evidence()
    {
        using var t = new T();
        const ulong lockS = 72 * 3600;
        var created = Now;
        var prop = Proposal(t, t.K, created, lockS);
        // Proposal by a stranger key (stolen-key path is the real key; a wrong key is rejected outright).
        var wrongKey = OmbraPublic.Call("ed25519.keygen")["secretKey"].S();
        Assert.Equal("roster-rejected", t.Apply(t.K, new() { ["setPending"] = Pending(t, prop, wrongKey) }));
        // notBefore shorter than the genesis timeLock is rejected.
        Assert.Equal("roster-rejected", t.Apply(t.K, new() { ["setPending"] = Pending(t, Proposal(t, t.K, created, 3600), t.W.RecoverySk) }));
        t.W.Update(t.K, new() { ["setPending"] = Pending(t, prop, t.W.RecoverySk) });
        Assert.NotNull(t.M1.Call("team.status", new() { ["teamId"] = t.W.TeamId, ["now"] = Now })["pendingRecovery"]);

        var notBefore = created + lockS;
        string Hb(Dev admin, ulong time) => admin.Call("heartbeat.create", new() { ["teamId"] = t.W.TeamId, ["time"] = time })["heartbeat"].S();
        JsonObject ApplyEdits(params string[] evidence) => new() { ["applyRecovery"] = true, ["evidence"] = Arr(evidence) };
        // Before notBefore: not applicable (even with evidence).
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.M1, ApplyEdits(Hb(t.X, notBefore), Hb(t.Y, notBefore)), now: notBefore - 10), "notBefore"));
        // After notBefore, but without heartbeat evidence of the admin quorum (k=2): refused; one admin is not enough.
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.M1, ApplyEdits(), now: notBefore + 10), "heartbeat evidence"));
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.M1, ApplyEdits(Hb(t.X, notBefore)), now: notBefore + 10), "heartbeat evidence"));
        // Evidence older than notBefore does not count.
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.M1, ApplyEdits(Hb(t.X, notBefore - 5), Hb(t.Y, notBefore - 5)), now: notBefore + 10), "heartbeat evidence"));
        Assert.Equal("ok", t.Apply(t.M1, ApplyEdits(Hb(t.X, notBefore), Hb(t.Y, notBefore + 1)), now: notBefore + 10));
    }

    [Fact]
    public void D3_owner_veto_annuls_the_proposal()
    {
        using var t = new T();
        t.W.Update(t.K, new() { ["setPending"] = Pending(t, Proposal(t, t.K, Now, 72 * 3600), t.W.RecoverySk) });
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.X, new() { ["clearPending"] = true }), "pending(clear)"));   // only the owner vetoes
        t.W.Update(t.O, new() { ["clearPending"] = true });
        Assert.Null(t.M1.Call("team.status", new() { ["teamId"] = t.W.TeamId, ["now"] = Now })["pendingRecovery"]);
        Assert.Equal("invalid-input", t.M1.Try("roster.update", new() { ["teamId"] = t.W.TeamId, ["now"] = Now, ["applyRecovery"] = true }));
    }

    [Fact]
    public void D3_recovery_key_change_needs_owner_device_and_old_key_and_old_key_dies()
    {
        using var t = new T();
        var nk = OmbraPublic.Call("ed25519.keygen");
        var (v, _) = t.W.Head(t.O);
        var rec = new JsonObject { ["ownerRecoveryKey"] = nk["publicKey"].S(), ["timeLock"] = 72 * 3600, ["custodian"] = t.K.MemberId };
        string Sig(string sk) => OmbraPublic.Call("recovery.sign_key_change", new() { ["recoverySecretKey"] = sk, ["teamId"] = t.W.TeamId, ["version"] = v + 1, ["recovery"] = rec.DeepClone() })["sig"].S();
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.O, new() { ["setRecovery"] = rec.DeepClone() }), "recovery"));                                         // no old-key signature
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.O, new() { ["setRecovery"] = rec.DeepClone(), ["recoveryKeyChangeSig"] = Sig(nk["secretKey"].S()) }), "recovery")); // signed by the new key only
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.X, new() { ["setRecovery"] = rec.DeepClone(), ["recoveryKeyChangeSig"] = Sig(t.W.RecoverySk) }), "recovery"));  // not the owner device
        t.W.Update(t.O, new() { ["setRecovery"] = rec.DeepClone(), ["recoveryKeyChangeSig"] = Sig(t.W.RecoverySk) });
        // The former key (still held by the ex-owner) can no longer start a recovery.
        Assert.Equal("roster-rejected", t.Why(t.Apply(t.K, new() { ["setPending"] = Pending(t, Proposal(t, t.K, Now, 72 * 3600), t.W.RecoverySk) }), "pending(set)"));
        Assert.Equal("ok", t.Apply(t.K, new() { ["setPending"] = Pending(t, Proposal(t, t.K, Now, 72 * 3600), nk["secretKey"].S()) }));
    }

    [Fact]
    public void RemovalNotice_only_from_admin_of_that_version()
    {
        using var t = new T();
        t.W.Update(t.O, new() { ["removeMembers"] = Arr(t.K.MemberId) });
        var good = t.X.Call("removal.create", new() { ["teamId"] = t.W.TeamId, ["memberId"] = t.K.MemberId, ["time"] = Now })["notice"].S();
        var byMember = t.M1.Call("removal.create", new() { ["teamId"] = t.W.TeamId, ["memberId"] = t.K.MemberId, ["time"] = Now })["notice"].S();
        var tampered = good[..^2] + (good[^2..] == "00" ? "01" : "00");
        Assert.False(t.K.Call("removal.verify", new() { ["notice"] = byMember })["accepted"].B());
        Assert.False(t.K.Call("removal.verify", new() { ["notice"] = tampered })["accepted"].B());
        Assert.False(t.K.Call("team.status", new() { ["teamId"] = t.W.TeamId, ["now"] = Now })["removed"].B());
        var ok = t.K.Call("removal.verify", new() { ["notice"] = good });
        Assert.True(ok["accepted"].B() && ok["aboutMe"].B());
        Assert.True(t.K.Call("team.status", new() { ["teamId"] = t.W.TeamId, ["now"] = Now })["removed"].B());
        // A notice about a member still in the roster is refused.
        var lie = t.X.Call("removal.create", new() { ["teamId"] = t.W.TeamId, ["memberId"] = t.M1.MemberId, ["time"] = Now })["notice"].S();
        Assert.False(t.M1.Call("removal.verify", new() { ["notice"] = lie })["accepted"].B());
    }
}
