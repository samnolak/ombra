using System.Diagnostics;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ombra.Server.Backup;
using Ombra.Server.Contracts;
using Ombra.Server.Logs;
using Ombra.Server.Tests.Infra;

namespace Ombra.Server.Tests;

/// <summary>
/// §1 SD-08 / R2-08 / R5-11 / N11 / D-7: backup → restore on a second host (own DB, data dir, /etc), quarantine,
/// journals applied before any hello, queue/transfers never in a backup.
/// </summary>
public sealed class BackupRestoreTests(PostgresFixture pg)
{
    private static readonly string[] RosterTables = ["teams", "roster_versions", "roster_members", "roster_devices", "blocks", "devices", "members"];

    private static (string Identity, string Recipient) AgeKey(string dir)
    {
        var keygen = Path.Combine(Path.GetDirectoryName(ServerHarness.AgePath())!, "age-keygen");
        var path = Path.Combine(dir, "operator.agekey");
        using var p = Process.Start(new ProcessStartInfo(keygen, ["-o", path]) { RedirectStandardError = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        var recipient = File.ReadAllLines(path).First(l => l.StartsWith("# public key: ", StringComparison.Ordinal))["# public key: ".Length..];
        return (path, recipient);
    }

    private static async Task<Dictionary<string, long>> CountsAsync(ServerHarness h, IEnumerable<string> tables)
    {
        var d = new Dictionary<string, long>();
        foreach (var t in tables) d[t] = await h.ScalarAsync<long>($"SELECT count(*) FROM {t}");
        return d;
    }

    private static async Task<List<string>> ArchiveMembersAsync(string agePath, string archive, string identity)
    {
        var names = new List<string>();
        await new AgeCli(agePath).DecryptAsync(archive, identity, async s =>
        {
            await using var tar = new TarReader(s);
            while (await tar.GetNextEntryAsync() is { } e) names.Add(e.Name);
        });
        return names;
    }

    [Fact]
    public async Task Backup_restore_on_second_host_quarantine_journals_and_no_queue_or_transfers()
    {
        // ---- host A ----
        var a = await ServerHarness.StartAsync(pg);
        await using var _a = a;
        var team = await a.SeedTeamAsync();
        var owner = await a.AddMemberAsync(team, "owner");
        var admin = await a.AddMemberAsync(team, "admin");
        var x = await a.AddMemberAsync(team);
        var y = await a.AddMemberAsync(team);
        var group = await a.AddGroupAsync(team, "team", [owner, admin, x, y]);
        foreach (var d in new[] { owner, admin, x, y }) await a.LoginAsync(d);
        var deletedLater = Guid.CreateVersion7();
        var kept = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.OK, (await BlobTests.PutBlobAsync(a, x, group, deletedLater, [1, 2, 3])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await BlobTests.PutBlobAsync(a, x, group, kept, [4, 5, 6])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PutOpAsync(x, group, 1, [42])).StatusCode);
        var transferId = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.OK, (await a.SendEnvelopeAsync(admin, HttpMethod.Put, $"/v1/transfers/{transferId}", EnvelopeKinds.TransferCreate, team,
            new TransferCreateBody(transferId, y.DeviceId, 10, new byte[32], 1).ToCbor())).StatusCode);
        Assert.Equal(1L, await a.ScalarAsync<long>("SELECT count(*) FROM delivery"));
        Assert.Equal(1L, await a.ScalarAsync<long>("SELECT count(*) FROM transfers"));

        var keys = Path.Combine(a.Root, "operator");
        Directory.CreateDirectory(keys);
        var (identity, recipient) = AgeKey(keys);
        var archive = Path.Combine(keys, "backup.age");
        var backup = await RevocationCliTests.CliAsync(a, "backup", "--out", archive, "--recipient", recipient);
        Assert.True(backup.Code == 0, backup.Err);
        var countsAtBackup = await CountsAsync(a, RosterTables);

        // D-7 / R5-11: the archive has no queue and no transfers (checked on the decrypted archive itself).
        var members = await ArchiveMembersAsync(ServerHarness.AgePath(), archive, identity);
        Assert.Contains("db/roster_members.copy", members);
        Assert.DoesNotContain(members, m => m.Contains("delivery") || m.Contains("transfer") || m.Contains("session"));
        Assert.Contains($"blobs/{kept:N}", members);

        var verify = await RevocationCliTests.CliAsync(a, "backup", "verify", "--in", archive, "--identity", identity);
        Assert.True(verify.Code == 0, verify.Err + verify.Out);
        Assert.Contains("backup verify: OK", verify.Out);

        // After the backup: the operator removes Y, X deletes a blob. Both must survive the restore via the journals.
        Assert.Equal(0, (await RevocationCliTests.CliAsync(a, "member", "remove", "--team", Ids.Hex(team), "--member", y.MemberId.ToString())).Code);
        Assert.Equal(HttpStatusCode.NoContent, (await BlobTests.DeleteBlobAsync(a, x, group, deletedLater)).StatusCode);

        // restore refuses while the server of that host is connected
        var refused = await RevocationCliTests.CliAsync(a, "restore", "--in", archive, "--identity", identity);
        Assert.Equal(1, refused.Code);
        Assert.Contains("stop the service", refused.Err);

        // ---- host B: fresh DB, data dir and /etc; off-host copies of both journals from A ----
        var (rootB, settingsB) = await ServerHarness.CreateHostAsync(pg);
        var probeB = new ServerHarnessSettings(settingsB);
        var restore = await probeB.CliAsync("restore", "--in", archive, "--identity", identity,
            "--revocations-log", Path.Combine(a.DataDir, "revocations.log"),
            "--blob-deletions-log", Path.Combine(a.DataDir, "blob-deletions.log"));
        Assert.True(restore.Code == 0, restore.Err);
        var report = JsonSerializer.Deserialize<RestoreReport>(restore.Out, Auth.JsonDefaults.Options)!;
        Assert.Equal(1, report.RevocationsRestored);
        Assert.Equal(1, report.BlobsDeletedByLog);
        Assert.Equal(1, report.TeamsQuarantined);

        await using var b = ServerHarness.Start(pg, rootB, settingsB);
        Assert.Equal(a.ServerId, b.ServerId); // serverId comes from the backup (DeviceCertificates carry it)
        var countsB = await CountsAsync(b, RosterTables);
        foreach (var t in RosterTables.Where(t => t != "blocks")) Assert.Equal(countsAtBackup[t], countsB[t]);
        Assert.Equal(countsAtBackup["blocks"] + 1, countsB["blocks"]); // + the block replayed from revocations.log
        Assert.Equal(0L, await b.ScalarAsync<long>("SELECT count(*) FROM delivery"));
        Assert.Equal(0L, await b.ScalarAsync<long>("SELECT count(*) FROM transfers"));
        Assert.Equal(0L, await b.ScalarAsync<long>("SELECT count(*) FROM sessions"));

        // Old tokens are dead (key rotated, sessions not restored); removed member gets 403 removed on hello.
        using (var c = b.Client())
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/v1/session/refresh", new RefreshRequest(x.RefreshToken!))).StatusCode);
        Assert.Equal(Reasons.Removed, await ServerHarness.ReasonAsync(await b.HelloAsync(y)));

        // Quarantine: hello works, content does not; N11 — a blob deleted after the backup is 404 before release.
        foreach (var d in new[] { owner, admin, x }) await b.LoginAsync(d);
        var hello = (await (await b.HelloAsync(x)).Content.ReadFromJsonAsync<HelloResponse>())!;
        Assert.True(hello.Teams.Single().Quarantine);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client(x).GetAsync($"/v1/blobs/{deletedLater}")).StatusCode);
        var q = await b.Client(x).GetAsync($"/v1/blobs/{kept}");
        Assert.Equal(Reasons.Quarantine, await ServerHarness.ReasonAsync(q));
        Assert.Equal(Reasons.Quarantine, await ServerHarness.ReasonAsync(await b.PutOpAsync(x, group, 1, [1])));
        Assert.Contains(Ids.Hex(team), (await b.SyncAsync(x)).QuarantinedTeams);
        Assert.Contains(HashChainLog.ReadVerified(Path.Combine(b.DataDir, "blob-deletions.log")),
            e => e.Entry["blobId"]!.GetValue<string>() == deletedLater.ToString());

        // Only an owner device releases quarantine (not any admin).
        var byAdmin = await b.SendEnvelopeAsync(admin, HttpMethod.Post, $"/v1/teams/{Ids.Hex(team)}/quarantine/release",
            EnvelopeKinds.QuarantineRelease, team, new QuarantineReleaseBody().ToCbor());
        Assert.Equal(Reasons.NotOwner, await ServerHarness.ReasonAsync(byAdmin));
        var byOwner = await b.SendEnvelopeAsync(owner, HttpMethod.Post, $"/v1/teams/{Ids.Hex(team)}/quarantine/release",
            EnvelopeKinds.QuarantineRelease, team, new QuarantineReleaseBody().ToCbor());
        Assert.Equal(HttpStatusCode.NoContent, byOwner.StatusCode);
        Assert.Equal(new byte[] { 4, 5, 6 }, await b.Client(x).GetByteArrayAsync($"/v1/blobs/{kept}"));
        Assert.Equal(HttpStatusCode.OK, (await b.PutOpAsync(x, group, 1, [1])).StatusCode);
        var after = (await (await b.HelloAsync(x)).Content.ReadFromJsonAsync<HelloResponse>())!;
        Assert.Equal("owner", after.Teams.Single().QuarantineReleasedBy);
    }

    [Fact]
    public async Task Restore_applies_the_configured_off_host_sinks_and_refuses_without_an_external_journal()
    {
        var sinks = Path.Combine(Path.GetTempPath(), "ombra-sinks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sinks);
        void Sinks(Dictionary<string, string?> s)
        {
            s["Ombra:RevocationsSink"] = Path.Combine(sinks, "revocations.log");
            s["Ombra:BlobDeletionsSink"] = Path.Combine(sinks, "blob-deletions.log");
        }
        try
        {
            // ---- host A with off-host sinks: one removal before the backup, one removal and a blob DELETE after ----
            var a = await ServerHarness.StartAsync(pg, Sinks);
            await using var _a = a;
            var team = await a.SeedTeamAsync();
            var x = await a.AddMemberAsync(team);
            var early = await a.AddMemberAsync(team);
            var late = await a.AddMemberAsync(team);
            var group = await a.AddGroupAsync(team, "team", [x, late]);
            await a.LoginAsync(x);
            var blob = Guid.CreateVersion7();
            Assert.Equal(HttpStatusCode.OK, (await BlobTests.PutBlobAsync(a, x, group, blob, [7, 7, 7])).StatusCode);
            Assert.Equal(0, (await RevocationCliTests.CliAsync(a, "member", "remove", "--team", Ids.Hex(team), "--member", early.MemberId.ToString())).Code);

            var keys = Path.Combine(a.Root, "operator");
            Directory.CreateDirectory(keys);
            var (identity, recipient) = AgeKey(keys);
            var archive = Path.Combine(keys, "backup.age");
            Assert.Equal(0, (await RevocationCliTests.CliAsync(a, "backup", "--out", archive, "--recipient", recipient)).Code);
            Assert.Equal(0, (await RevocationCliTests.CliAsync(a, "member", "remove", "--team", Ids.Hex(team), "--member", late.MemberId.ToString())).Code);
            Assert.Equal(HttpStatusCode.NoContent, (await BlobTests.DeleteBlobAsync(a, x, group, blob)).StatusCode);

            // ---- host B: a fresh VM (empty data dir) with the same sinks configured, no explicit copies ----
            var (_, settingsB) = await ServerHarness.CreateHostAsync(pg, Sinks);
            var restored = await new ServerHarnessSettings(settingsB).CliAsync("restore", "--in", archive, "--identity", identity);
            Assert.True(restored.Code == 0, restored.Err);
            var report = JsonSerializer.Deserialize<RestoreReport>(restored.Out, Auth.JsonDefaults.Options)!;
            Assert.Equal(1, report.RevocationsRestored);          // the post-backup removal came back from the sink
            Assert.Equal(1, report.RevocationsAlreadyPresent);
            Assert.Equal(1, report.BlobsDeletedByLog);            // N11 via the sink
            await using (var dbB = Npgsql.NpgsqlDataSource.Create(settingsB["Ombra:ConnectionString"]!))
            await using (var conn = await dbB.OpenConnectionAsync())
            await using (var cmd = new Npgsql.NpgsqlCommand("SELECT count(*) FROM blocks WHERE member_id = @m", conn))
            {
                cmd.Parameters.AddWithValue("m", late.MemberId);
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
            }

            // ---- host C: no sink and no copy → refused before anything is wiped; explicit override is audited ----
            var (_, settingsC) = await ServerHarness.CreateHostAsync(pg);
            var probeC = new ServerHarnessSettings(settingsC);
            var refused = await probeC.CliAsync("restore", "--in", archive, "--identity", identity);
            Assert.Equal(1, refused.Code);
            Assert.Contains("revocations.log", refused.Err);
            // A copy shorter than the journal recorded in the backup is a wrong/truncated copy.
            var empty = Path.Combine(keys, "empty.log");
            await File.WriteAllTextAsync(empty, "");
            var shortCopy = await probeC.CliAsync("restore", "--in", archive, "--identity", identity,
                "--revocations-log", empty, "--blob-deletions-log", empty);
            Assert.Equal(1, shortCopy.Code);
            Assert.Contains("shorter", shortCopy.Err);
            var forced = await probeC.CliAsync("restore", "--in", archive, "--identity", identity, "--no-external-log");
            Assert.True(forced.Code == 0, forced.Err);
            Assert.Contains("WARNING", forced.Err);
            await using var dbC = Npgsql.NpgsqlDataSource.Create(settingsC["Ombra:ConnectionString"]!);
            await using var connC = await dbC.OpenConnectionAsync();
            await using var q = new Npgsql.NpgsqlCommand("SELECT count(*) FROM audit WHERE action = 'restore.no-external-log'", connC);
            Assert.Equal(1L, (long)(await q.ExecuteScalarAsync())!);
        }
        finally
        {
            Directory.Delete(sinks, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_refuses_a_configured_sink_whose_file_is_missing()
    {
        // A backup taken before any journaled event (both journals empty in the manifest).
        var a = await ServerHarness.StartAsync(pg);
        await using var _a = a;
        await a.SeedTeamAsync();
        var keys = Path.Combine(a.Root, "operator");
        Directory.CreateDirectory(keys);
        var (identity, recipient) = AgeKey(keys);
        var archive = Path.Combine(keys, "backup.age");
        Assert.Equal(0, (await RevocationCliTests.CliAsync(a, "backup", "--out", archive, "--recipient", recipient)).Code);

        // The sink mount is missing on the new host (unmounted share, typo): never read as "no events".
        var missing = Path.Combine(Path.GetTempPath(), "ombra-unmounted-" + Guid.NewGuid().ToString("N"));
        var (_, settings) = await ServerHarness.CreateHostAsync(pg, s =>
        {
            s["Ombra:RevocationsSink"] = Path.Combine(missing, "revocations.log");
            s["Ombra:BlobDeletionsSink"] = Path.Combine(missing, "blob-deletions.log");
        });
        var refused = await new ServerHarnessSettings(settings).CliAsync("restore", "--in", archive, "--identity", identity);
        Assert.Equal(1, refused.Code);
        Assert.Contains("not found", refused.Err);
    }

    [Fact]
    public async Task Verify_detects_a_corrupted_archive_and_every_table_is_classified()
    {
        await using var h = await ServerHarness.StartAsync(pg);
        var tables = await h.ScalarAsync<string[]>(
            "SELECT array_agg(table_name::text) FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'");
        Assert.Empty(BackupService.Included.Intersect(BackupService.Excluded));
        Assert.Empty(tables.Except(BackupService.Included).Except(BackupService.Excluded)); // a new table must be classified
        Assert.Empty(BackupService.Included.Concat(BackupService.Excluded).Except(tables));

        var keys = Path.Combine(h.Root, "operator");
        Directory.CreateDirectory(keys);
        var (identity, recipient) = AgeKey(keys);
        var archive = Path.Combine(keys, "b.age");
        Assert.Equal(0, (await RevocationCliTests.CliAsync(h, "backup", "--out", archive, "--recipient", recipient)).Code);
        var bytes = await File.ReadAllBytesAsync(archive);
        bytes[^10] ^= 0xFF;
        await File.WriteAllBytesAsync(archive, bytes);
        var (code, _, err) = await RevocationCliTests.CliAsync(h, "backup", "verify", "--in", archive, "--identity", identity);
        Assert.Equal(1, code);
        Assert.Contains("age --decrypt failed", err);
    }
}

/// <summary>Runs ombra-admin against a host whose server is not started (restore target).</summary>
public sealed class ServerHarnessSettings(Dictionary<string, string?> settings)
{
    public async Task<(int Code, string Out, string Err)> CliAsync(params string[] args)
    {
        var config = Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(new Microsoft.Extensions.Configuration.ConfigurationBuilder(), settings).Build();
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await Cli.AdminCli.RunAsync(args, config, o, e);
        return (code, o.ToString(), e.ToString());
    }
}
