using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ombra.Server.Contracts;
using Ombra.Server.Teams;
using Ombra.Server.Tests.Infra;

namespace Ombra.Server.Tests;

/// <summary>
/// §4.1 roster chain on the server with genuinely signed RosterUpdates (ombra-core client state), app removal (§5.1 п.3)
/// and R2-08: after a restore a removed admin uploads a longer alternative branch → not taken by length, team frozen.
/// </summary>
public sealed class RosterTests(PostgresFixture pg)
{
    private static HttpClient Client(ServerHarness h, CoreDevice d)
    {
        var c = h.Client();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", d.AccessToken);
        return c;
    }

    private static async Task<HttpResponseMessage> PostRosterAsync(ServerHarness h, CoreDevice d, string url, string updateHex)
    {
        using var c = Client(h, d);
        var content = new ByteArrayContent(Convert.FromHexString(updateHex));
        content.Headers.ContentType = new MediaTypeHeaderValue(RosterService.ContentType);
        return await c.PostAsync(url, content);
    }

    private static ulong Now(ServerHarness h) => (ulong)h.Clock.GetUtcNow().ToUnixTimeSeconds();

    private static JsonObject Update(string teamId, ulong now, Action<JsonObject> edit)
    {
        var o = new JsonObject { ["teamId"] = teamId, ["apply"] = true, ["now"] = now };
        edit(o);
        return o;
    }

    [Fact]
    public async Task Genesis_needs_server_owner_append_mirrors_projection_and_app_removal_blocks()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        using var owner = new CoreDevice();
        using var admin = new CoreDevice();
        using var stranger = new CoreDevice();
        await owner.RegisterAsync(h, serverOwner: true);
        await admin.RegisterAsync(h);
        await stranger.RegisterAsync(h);

        var created = owner.Call("team.create", new() { ["serverId"] = CoreDevice.Hex(Ids.ToBytes(h.ServerId)), ["recoveryKey"] = CoreDevice.Hex(RandomNumberGenerator.GetBytes(32)), ["issuedAt"] = Now(h) });
        var teamId = created["teamId"]!.GetValue<string>();
        var genesis = created["update"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.Forbidden, (await PostRosterAsync(h, stranger, "/v1/teams", genesis)).StatusCode); // not ServerOwner / not owner
        var tampered = Convert.FromHexString(genesis);
        tampered[^5] ^= 1;
        var bad = await PostRosterAsync(h, owner, "/v1/teams", Convert.ToHexString(tampered));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(Reasons.RosterRejected, await ServerHarness.ReasonAsync(bad));
        var ok = await PostRosterAsync(h, owner, "/v1/teams", genesis);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(teamId, (await ok.Content.ReadFromJsonAsync<RosterService.AppendResponse>())!.TeamId);

        var v2 = owner.Call("roster.update", Update(teamId, Now(h), o => o["addMembers"] = new JsonArray(new JsonObject
        {
            ["memberId"] = CoreDevice.Hex(Ids.ToBytes(admin.MemberId)), ["role"] = "admin", ["certs"] = new JsonArray(admin.Certificate(h.ServerId, Now(h))),
        })))["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(h, owner, $"/v1/teams/{teamId}/roster", v2)).StatusCode);
        var again = await (await PostRosterAsync(h, owner, $"/v1/teams/{teamId}/roster", v2)).Content.ReadFromJsonAsync<RosterService.AppendResponse>();
        Assert.True(again!.Known); // idempotent re-upload
        Assert.Equal("admin", await h.ScalarAsync<string>("SELECT role FROM team_device_access WHERE device_id = @d", ("d", admin.DeviceId)));

        using (var c = Client(h, admin))
            Assert.Equal(2, (await c.GetFromJsonAsync<List<SyncRoster>>($"/v1/teams/{teamId}/roster"))!.Count);

        // A stranger (not in the roster) cannot append, even a validly signed update by the owner.
        var v3 = owner.Call("roster.update", Update(teamId, Now(h), o => o["removeMembers"] = new JsonArray(CoreDevice.Hex(Ids.ToBytes(admin.MemberId)))))["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostRosterAsync(h, stranger, $"/v1/teams/{teamId}/roster", v3)).StatusCode);
        // §5.1 п.3: the removal is applied with block, session revocation and purge in one transaction.
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(h, owner, $"/v1/teams/{teamId}/roster", v3)).StatusCode);
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM blocks WHERE member_id = @m AND by_actor = 'admin'", ("m", admin.MemberId)));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM sessions WHERE device_id = @d", ("d", admin.DeviceId)));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM team_device_access WHERE device_id = @d", ("d", admin.DeviceId)));
    }

    [Fact]
    public async Task R2_08_removed_admin_long_branch_after_restore_freezes_the_team()
    {
        // ---- host A: v1 (owner) → v2 (+admin) → backup → v3 (admin removed) ----
        var a = await ServerHarness.StartAsync(pg);
        await using var _a = a;
        using var owner = new CoreDevice();
        using var admin = new CoreDevice();
        await owner.RegisterAsync(a, serverOwner: true);
        await admin.RegisterAsync(a);
        var created = owner.Call("team.create", new() { ["serverId"] = CoreDevice.Hex(Ids.ToBytes(a.ServerId)), ["recoveryKey"] = CoreDevice.Hex(RandomNumberGenerator.GetBytes(32)), ["issuedAt"] = Now(a) });
        var teamId = created["teamId"]!.GetValue<string>();
        var v1 = created["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(a, owner, "/v1/teams", v1)).StatusCode);
        var v2 = owner.Call("roster.update", Update(teamId, Now(a), o => o["addMembers"] = new JsonArray(new JsonObject
        {
            ["memberId"] = CoreDevice.Hex(Ids.ToBytes(admin.MemberId)), ["role"] = "admin", ["certs"] = new JsonArray(admin.Certificate(a.ServerId, Now(a))),
        })))["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(a, owner, $"/v1/teams/{teamId}/roster", v2)).StatusCode);
        admin.Call("team.import", new() { ["teamId"] = teamId, ["updates"] = new JsonArray(v1, v2), ["serverId"] = CoreDevice.Hex(Ids.ToBytes(a.ServerId)), ["now"] = Now(a) });

        var keys = Path.Combine(a.Root, "operator");
        Directory.CreateDirectory(keys);
        var keygen = Path.Combine(Path.GetDirectoryName(ServerHarness.AgePath())!, "age-keygen");
        using (var p = System.Diagnostics.Process.Start(keygen, ["-o", Path.Combine(keys, "k")])) p.WaitForExit();
        var recipient = File.ReadAllLines(Path.Combine(keys, "k")).First(l => l.StartsWith("# public key: ", StringComparison.Ordinal))[14..];
        var archive = Path.Combine(keys, "b.age");
        Assert.Equal(0, (await RevocationCliTests.CliAsync(a, "backup", "--out", archive, "--recipient", recipient)).Code);

        var v3 = owner.Call("roster.update", Update(teamId, Now(a), o => o["removeMembers"] = new JsonArray(CoreDevice.Hex(Ids.ToBytes(admin.MemberId)))))["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(a, owner, $"/v1/teams/{teamId}/roster", v3)).StatusCode);

        // ---- the removed admin pre-signs a longer alternative branch from v2 ----
        using var m1 = new CoreDevice();
        using var m2 = new CoreDevice();
        var alt3 = admin.Call("roster.update", Update(teamId, Now(a), o => o["addMembers"] = new JsonArray(new JsonObject
        {
            ["memberId"] = CoreDevice.Hex(Ids.ToBytes(m1.MemberId)), ["certs"] = new JsonArray(m1.Certificate(a.ServerId, Now(a))),
        })))["update"]!.GetValue<string>();
        var alt4 = admin.Call("roster.update", Update(teamId, Now(a), o => o["addMembers"] = new JsonArray(new JsonObject
        {
            ["memberId"] = CoreDevice.Hex(Ids.ToBytes(m2.MemberId)), ["certs"] = new JsonArray(m2.Certificate(a.ServerId, Now(a))),
        })))["update"]!.GetValue<string>();

        // ---- host B: restore of the v2 backup → quarantine ----
        var (rootB, settingsB) = await ServerHarness.CreateHostAsync(pg);
        // Host A never journaled a CLI removal or blob deletion: there is no off-host journal to apply.
        var restore = await new ServerHarnessSettings(settingsB).CliAsync("restore", "--in", archive, "--identity", Path.Combine(keys, "k"), "--no-external-log");
        Assert.True(restore.Code == 0, restore.Err);
        await using var b = ServerHarness.Start(pg, rootB, settingsB);
        await owner.LoginAsync(b);
        await admin.LoginAsync(b);
        Assert.Equal(2L, await b.ScalarAsync<long>("SELECT roster_version FROM teams"));

        // the restored server extends linearly: the removed admin's branch is accepted first …
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(b, admin, $"/v1/teams/{teamId}/roster", alt3)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(b, admin, $"/v1/teams/{teamId}/roster", alt4)).StatusCode);
        // … until the owner's client re-uploads its v3: two valid versions 3 → equivocation, frozen, no length rule.
        var conflict = await PostRosterAsync(b, owner, $"/v1/teams/{teamId}/roster", v3);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(Reasons.Equivocation, await ServerHarness.ReasonAsync(conflict));
        Assert.Equal("equivocation", await b.ScalarAsync<string>("SELECT frozen_reason FROM teams"));

        // Frozen: further roster uploads and content are refused even after quarantine is released.
        Assert.Equal(Reasons.Frozen, await ServerHarness.ReasonAsync(await PostRosterAsync(b, admin, $"/v1/teams/{teamId}/roster", alt4)));
        Assert.Equal(0, (await new ServerHarnessSettings(settingsB).CliAsync("quarantine", "release", "--team", teamId, "--confirm")).Code);
        var group = RandomNumberGenerator.GetBytes(32);
        var olderGroup = RandomNumberGenerator.GetBytes(32);
        var team = Convert.FromHexString(teamId);
        await b.ExecAsync(
            "INSERT INTO groups (group_id, team_id, kind, epoch, roster_version, created_at) VALUES (@g, @t, 'team', 1, 3, now()), (@g2, @t, 'team', 1, 2, now()); " +
            "INSERT INTO delivery (group_id, sender_device_id, op_id, team_id, sender_member_id, epoch, ciphertext, size, pending_device_ids, created_at, expires_at) " +
            "VALUES (@g, @s, gen_random_uuid(), @t, @sm, 1, '\\x01', 1, ARRAY[@d]::uuid[], now(), now() + interval '1 day')",
            ("g", group), ("g2", olderGroup), ("t", team), ("s", admin.DeviceId), ("sm", admin.MemberId), ("d", owner.DeviceId));
        using var c = Client(b, owner);
        var sync = (await c.GetFromJsonAsync<SyncResponse>("/v1/sync"))!;
        Assert.Empty(sync.Ops); // no content from a frozen team …
        Assert.Equal([teamId], sync.FrozenTeams);
        Assert.Equal(4, sync.Roster.Count); // … but the roster (incl. the conflicting branch head) still flows

        // ---- the owner decides (R2-08, §1 карантин п.3): it fetches both signed versions 3 and keeps its own ----
        JsonObject ev;
        using (var oc = Client(b, owner))
            ev = (await oc.GetFromJsonAsync<JsonObject>($"/v1/teams/{teamId}/roster/equivocation"))!;
        Assert.Equal(3, ev["version"]!.GetValue<long>());
        Assert.Equal(Convert.FromHexString(v3), Convert.FromBase64String(ev["conflicting"]!.GetValue<string>()));
        // Locally the removed admin's alt3 is only a conflict (the admin is gone at the owner's head), so the owner signs
        // its decision from the server's evidence.
        Assert.Equal("roster-conflict", Assert.Throws<Ombra.Core.Interop.OmbraException>(() =>
            owner.Call("roster.apply", new() { ["teamId"] = teamId, ["update"] = alt3, ["now"] = Now(b) })).Code);
        string Hex64(string k) => Convert.ToHexStringLower(Convert.FromBase64String(ev[k]!.GetValue<string>()));
        var resolution = owner.Call("roster.resolution_create", new()
        {
            ["teamId"] = teamId, ["chosen"] = "conflicting",
            ["evidence"] = new JsonObject { ["version"] = 3, ["known"] = Hex64("known"), ["conflicting"] = Hex64("conflicting") },
        })["resolution"]!.GetValue<string>();
        var resolved = await PostRosterAsync(b, owner, $"/v1/teams/{teamId}/roster/resolution", resolution);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.True((await resolved.Content.ReadFromJsonAsync<JsonObject>())!["switched"]!.GetValue<bool>());
        Assert.True(await b.ScalarAsync<bool>("SELECT frozen_reason IS NULL FROM teams"));
        Assert.Equal(3L, await b.ScalarAsync<long>("SELECT roster_version FROM teams"));
        Assert.Equal(3L, await b.ScalarAsync<long>("SELECT count(*) FROM roster_versions"));
        Assert.Equal(Convert.FromHexString(v3), await b.ScalarAsync<byte[]>("SELECT roster_update FROM roster_versions WHERE version = 3"));
        // The canonical branch removed the admin; the members of the discarded branch are out of the roster mirror.
        Assert.Equal(0L, await b.ScalarAsync<long>("SELECT count(*) FROM team_device_access WHERE device_id = @d", ("d", admin.DeviceId)));
        Assert.Equal(0L, await b.ScalarAsync<long>("SELECT count(*) FROM roster_members WHERE member_id = ANY(@m) AND removed_in_version IS NULL",
            ("m", new[] { m1.MemberId, m2.MemberId })));
        // Groups whose epoch references a replaced version (≥ 3) are closed: a new generation is created on the canonical chain.
        Assert.True(await b.ScalarAsync<bool>("SELECT closed_at IS NOT NULL FROM groups WHERE group_id = @g", ("g", group)));
        Assert.True(await b.ScalarAsync<bool>("SELECT closed_at IS NULL FROM groups WHERE group_id = @g", ("g", olderGroup)));
        Assert.Equal(1L, await b.ScalarAsync<long>("SELECT count(*) FROM audit WHERE action = 'roster.equivocation.resolved'"));
        // The rejected branch is a conflict from now on, never a new freeze; other clients fetch the decision.
        var again = await PostRosterAsync(b, owner, $"/v1/teams/{teamId}/roster", alt3);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(Reasons.RosterConflict, await ServerHarness.ReasonAsync(again));
        Assert.True(await b.ScalarAsync<bool>("SELECT frozen_reason IS NULL FROM teams"));
        using (var oc = Client(b, owner))
            Assert.Equal(Convert.FromHexString(resolution), (await oc.GetFromJsonAsync<List<byte[]>>($"/v1/teams/{teamId}/roster/resolutions"))!.Single());
        Assert.Empty((await c.GetFromJsonAsync<SyncResponse>("/v1/sync"))!.FrozenTeams);
    }

    [Fact]
    public async Task Member_signed_alternative_version_is_a_conflict_and_does_not_freeze_the_team()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        using var owner = new CoreDevice();
        using var member = new CoreDevice();
        await owner.RegisterAsync(h, serverOwner: true);
        await member.RegisterAsync(h);
        var created = owner.Call("team.create", new() { ["serverId"] = CoreDevice.Hex(Ids.ToBytes(h.ServerId)), ["recoveryKey"] = CoreDevice.Hex(RandomNumberGenerator.GetBytes(32)), ["issuedAt"] = Now(h) });
        var teamId = created["teamId"]!.GetValue<string>();
        var v1 = created["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(h, owner, "/v1/teams", v1)).StatusCode);
        var v2 = owner.Call("roster.update", Update(teamId, Now(h), o => o["addMembers"] = new JsonArray(new JsonObject
        {
            ["memberId"] = CoreDevice.Hex(Ids.ToBytes(member.MemberId)), ["certs"] = new JsonArray(member.Certificate(h.ServerId, Now(h))),
        })))["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(h, owner, $"/v1/teams/{teamId}/roster", v2)).StatusCode);
        member.Call("team.import", new() { ["teamId"] = teamId, ["updates"] = new JsonArray(v1, v2), ["serverId"] = CoreDevice.Hex(Ids.ToBytes(h.ServerId)), ["now"] = Now(h) });
        var v3 = owner.Call("roster.update", Update(teamId, Now(h), o => o["pins"] = new JsonArray(CoreDevice.Hex(RandomNumberGenerator.GetBytes(32)))))["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(h, owner, $"/v1/teams/{teamId}/roster", v3)).StatusCode);

        // The member signs its own alternative v3 (only privateCommit): a conflict, not equivocation.
        var alt = member.Call("roster.update", new() { ["teamId"] = teamId, ["now"] = Now(h), ["privateCommit"] = CoreDevice.Hex(RandomNumberGenerator.GetBytes(32)) })["update"]!.GetValue<string>();
        var r = await PostRosterAsync(h, member, $"/v1/teams/{teamId}/roster", alt);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(Reasons.RosterConflict, await ServerHarness.ReasonAsync(r));
        Assert.True(await h.ScalarAsync<bool>("SELECT frozen_reason IS NULL FROM teams"));
        Assert.Equal(0L, await h.ScalarAsync<long>("SELECT count(*) FROM audit WHERE action = 'roster.equivocation'"));
        // The team keeps working.
        var v4 = owner.Call("roster.update", Update(teamId, Now(h), o => o["pins"] = new JsonArray(CoreDevice.Hex(RandomNumberGenerator.GetBytes(32)))))["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(h, owner, $"/v1/teams/{teamId}/roster", v4)).StatusCode);
    }

    [Fact]
    public async Task Equivocation_between_admins_is_resolved_by_an_owner_signed_decision_relayed_by_the_cli()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        using var owner = new CoreDevice();
        using var x = new CoreDevice();
        using var y = new CoreDevice();
        await owner.RegisterAsync(h, serverOwner: true);
        await x.RegisterAsync(h);
        await y.RegisterAsync(h);
        var sid = CoreDevice.Hex(Ids.ToBytes(h.ServerId));
        var created = owner.Call("team.create", new() { ["serverId"] = sid, ["recoveryKey"] = CoreDevice.Hex(RandomNumberGenerator.GetBytes(32)), ["issuedAt"] = Now(h) });
        var teamId = created["teamId"]!.GetValue<string>();
        var chain = new JsonArray(created["update"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(h, owner, "/v1/teams", chain[0]!.GetValue<string>())).StatusCode);
        foreach (var admin in new[] { x, y })
        {
            var u = owner.Call("roster.update", Update(teamId, Now(h), o => o["addMembers"] = new JsonArray(new JsonObject
            {
                ["memberId"] = CoreDevice.Hex(Ids.ToBytes(admin.MemberId)), ["role"] = "admin", ["certs"] = new JsonArray(admin.Certificate(h.ServerId, Now(h))),
            })))["update"]!.GetValue<string>();
            Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(h, owner, $"/v1/teams/{teamId}/roster", u)).StatusCode);
            chain.Add(u);
        }
        foreach (var admin in new[] { x, y })
            admin.Call("team.import", new() { ["teamId"] = teamId, ["updates"] = chain.DeepClone(), ["serverId"] = sid, ["now"] = Now(h) });
        JsonObject Pins() => new() { ["teamId"] = teamId, ["now"] = Now(h), ["pins"] = new JsonArray(CoreDevice.Hex(RandomNumberGenerator.GetBytes(32))) };
        var ux = x.Call("roster.update", Pins())["update"]!.GetValue<string>();
        var uy = y.Call("roster.update", Pins())["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(h, x, $"/v1/teams/{teamId}/roster", ux)).StatusCode);
        Assert.Equal(Reasons.Equivocation, await ServerHarness.ReasonAsync(await PostRosterAsync(h, y, $"/v1/teams/{teamId}/roster", uy)));

        // The owner's client takes both versions from the server, freezes locally and decides: keep X's version.
        using (var oc = Client(h, owner))
        {
            var ev = (await oc.GetFromJsonAsync<JsonObject>($"/v1/teams/{teamId}/roster/equivocation"))!;
            string B(string k) => Convert.ToHexStringLower(Convert.FromBase64String(ev[k]!.GetValue<string>()));
            owner.Call("roster.apply", new() { ["teamId"] = teamId, ["update"] = B("known"), ["now"] = Now(h) });
            Assert.Throws<Ombra.Core.Interop.OmbraException>(() =>
                owner.Call("roster.apply", new() { ["teamId"] = teamId, ["update"] = B("conflicting"), ["now"] = Now(h) }));
        }
        var dir = Path.Combine(h.Root, "resolution");
        Directory.CreateDirectory(dir);
        // An admin-signed "decision" is refused by the CLI as well.
        x.Call("roster.apply", new() { ["teamId"] = teamId, ["update"] = ux, ["now"] = Now(h) });
        Assert.Throws<Ombra.Core.Interop.OmbraException>(() => x.Call("roster.apply", new() { ["teamId"] = teamId, ["update"] = uy, ["now"] = Now(h) }));
        await File.WriteAllTextAsync(Path.Combine(dir, "admin.hex"), x.Call("roster.resolution_create", new() { ["teamId"] = teamId, ["chosen"] = "known" })["resolution"]!.GetValue<string>());
        Assert.Equal(1, (await RevocationCliTests.CliAsync(h, "roster", "resolve", "--team", teamId, "--resolution", Path.Combine(dir, "admin.hex"))).Code);
        Assert.Equal("equivocation", await h.ScalarAsync<string>("SELECT frozen_reason FROM teams"));

        await File.WriteAllTextAsync(Path.Combine(dir, "owner.hex"), owner.Call("roster.resolution_create", new() { ["teamId"] = teamId, ["chosen"] = "known" })["resolution"]!.GetValue<string>());
        var ok = await RevocationCliTests.CliAsync(h, "roster", "resolve", "--team", teamId, "--resolution", Path.Combine(dir, "owner.hex"));
        Assert.True(ok.Code == 0, ok.Err);
        Assert.True(await h.ScalarAsync<bool>("SELECT frozen_reason IS NULL FROM teams"));
        Assert.Equal(4L, await h.ScalarAsync<long>("SELECT roster_version FROM teams"));
        Assert.Equal(Convert.FromHexString(ux), await h.ScalarAsync<byte[]>("SELECT roster_update FROM roster_versions WHERE version = 4"));
        Assert.Equal(1L, await h.ScalarAsync<long>("SELECT count(*) FROM audit WHERE action = 'roster.equivocation.resolved' AND actor LIKE 'operator:%'"));
        Assert.Equal(Reasons.RosterConflict, await ServerHarness.ReasonAsync(await PostRosterAsync(h, y, $"/v1/teams/{teamId}/roster", uy)));
    }

    [Fact]
    public async Task Switching_branch_on_the_owners_decision_never_raises_a_role_on_the_server()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        using var owner = new CoreDevice();
        using var x = new CoreDevice();
        using var y = new CoreDevice();
        await owner.RegisterAsync(h, serverOwner: true);
        await x.RegisterAsync(h);
        await y.RegisterAsync(h);
        var sid = CoreDevice.Hex(Ids.ToBytes(h.ServerId));
        var created = owner.Call("team.create", new() { ["serverId"] = sid, ["recoveryKey"] = CoreDevice.Hex(RandomNumberGenerator.GetBytes(32)), ["issuedAt"] = Now(h) });
        var teamId = created["teamId"]!.GetValue<string>();
        var chain = new JsonArray(created["update"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(h, owner, "/v1/teams", chain[0]!.GetValue<string>())).StatusCode);
        foreach (var admin in new[] { x, y })
        {
            var u = owner.Call("roster.update", Update(teamId, Now(h), o => o["addMembers"] = new JsonArray(new JsonObject
            {
                ["memberId"] = CoreDevice.Hex(Ids.ToBytes(admin.MemberId)), ["role"] = "admin", ["certs"] = new JsonArray(admin.Certificate(h.ServerId, Now(h))),
            })))["update"]!.GetValue<string>();
            Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(h, owner, $"/v1/teams/{teamId}/roster", u)).StatusCode);
            chain.Add(u);
        }
        y.Call("team.import", new() { ["teamId"] = teamId, ["updates"] = chain.DeepClone(), ["serverId"] = sid, ["now"] = Now(h) });
        // Known v4: the owner demotes X. Conflicting v4: Y changes pins (X still admin there).
        var demote = owner.Call("roster.update", Update(teamId, Now(h), o => o["setRoles"] = new JsonArray(new JsonObject
        {
            ["memberId"] = CoreDevice.Hex(Ids.ToBytes(x.MemberId)), ["role"] = "member",
        })))["update"]!.GetValue<string>();
        var pins = y.Call("roster.update", new() { ["teamId"] = teamId, ["now"] = Now(h), ["pins"] = new JsonArray(CoreDevice.Hex(RandomNumberGenerator.GetBytes(32))) })["update"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await PostRosterAsync(h, owner, $"/v1/teams/{teamId}/roster", demote)).StatusCode);
        Assert.Equal(Reasons.Equivocation, await ServerHarness.ReasonAsync(await PostRosterAsync(h, y, $"/v1/teams/{teamId}/roster", pins)));
        Assert.Equal("member", await h.ScalarAsync<string>("SELECT role FROM roster_members WHERE member_id = @m", ("m", x.MemberId)));

        Assert.Throws<Ombra.Core.Interop.OmbraException>(() => owner.Call("roster.apply", new() { ["teamId"] = teamId, ["update"] = pins, ["now"] = Now(h) }));
        var resolution = owner.Call("roster.resolution_create", new() { ["teamId"] = teamId, ["chosen"] = "conflicting" })["resolution"]!.GetValue<string>();
        var r = await PostRosterAsync(h, owner, $"/v1/teams/{teamId}/roster/resolution", resolution);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True((await r.Content.ReadFromJsonAsync<JsonObject>())!["switched"]!.GetValue<bool>());
        // The chosen branch lists X as admin, but the server mirror only ever reduces: X stays a member on the server.
        Assert.Equal("member", await h.ScalarAsync<string>("SELECT role FROM roster_members WHERE member_id = @m", ("m", x.MemberId)));
        Assert.Equal("member", await h.ScalarAsync<string>("SELECT role FROM team_device_access WHERE device_id = @d", ("d", x.DeviceId)));
    }
}
