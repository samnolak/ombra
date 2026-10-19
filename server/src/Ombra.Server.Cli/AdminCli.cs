using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Backup;
using Ombra.Server.Blobs;
using Ombra.Server.Contracts;
using Ombra.Server.Data;
using Ombra.Server.Logs;
using Ombra.Server.Retention;
using Ombra.Server.Revocation;
using Ombra.Server.Teams;

namespace Ombra.Server.Cli;

/// <summary>
/// ombra-admin (§1, §5.2): operator commands may only REDUCE access (block member/device, revoke sessions) or do
/// maintenance (migrate, backup/verify/restore, replay journals, retention, TLS key rotation, bootstrap codes).
/// There is deliberately no command that unblocks, adds members or changes roles (§5.2 п.5). <c>roster resolve</c> only
/// relays a decision whose authority is the owner device's signature (R2-08), verified exactly as on the API.
/// </summary>
public static class AdminCli
{
    public const string Usage = """
        ombra-admin <command> [options]

          migrate
          bootstrap issue [--pin <spki-sha256-hex>] [--ttl-minutes 60]
          bootstrap reset [--pin <spki-sha256-hex>]          new owner code; existing teams are unaffected
          member remove --team <teamId> --member <memberId> [--reason <text>]
          device revoke --team <teamId> --device <deviceId> [--reason <text>]
          sessions revoke --device <deviceId>
          quarantine release --team <teamId> --confirm       operator override, recorded in audit
          roster equivocation --team <teamId>                the two signed versions of an open equivocation (hex), for the owner
          roster resolve --team <teamId> --resolution <file> relay the owner-signed EquivocationResolution (hex or binary file)
          replay-revocations [--log <off-host copy>]
          backup --out <file> (--recipient <age-recipient> | --recipients-file <file>)...
          backup verify --in <file> --identity <age-identity-file>
          restore --in <file> --identity <age-identity-file> [--revocations-log <copy>] [--blob-deletions-log <copy>] [--no-external-log] [--force]
                    (copies default to Ombra:RevocationsSink / Ombra:BlobDeletionsSink; without any, restore is refused
                     unless --no-external-log, which is audited)
          logs verify
          retention run
          tls fingerprints [--dir /etc/ombra/tls]
          tls rotate --to-next [--dir /etc/ombra/tls]
        """;

    public static async Task<int> RunAsync(string[] args, IConfiguration config, TextWriter stdout, TextWriter stderr)
    {
        var a = new Args(args);
        try
        {
            switch (a.Command)
            {
                case "tls fingerprints": return Tls.Fingerprints(a.Opt("--dir") ?? "/etc/ombra/tls", stdout);
                case "tls rotate": return Tls.RotateToNext(a.Opt("--dir") ?? "/etc/ombra/tls", a.Flag("--to-next"), stdout);
                case "help" or "--help" or "-h" or "":
                    stdout.WriteLine(Usage);
                    return a.Command == "" ? 2 : 0;
            }

            await using var sp = BuildServices(config);
            var db = sp.GetRequiredService<NpgsqlDataSource>();
            var clock = sp.GetRequiredService<TimeProvider>();
            var actor = "operator:" + Environment.UserName;
            switch (a.Command)
            {
                case "migrate":
                {
                    var applied = await Migrator.MigrateAsync(db);
                    stdout.WriteLine(applied.Count == 0 ? $"schema up to date ({Migrator.Latest})" : "applied: " + string.Join(", ", applied));
                    return 0;
                }
                case "bootstrap issue" or "bootstrap reset":
                {
                    var ttl = TimeSpan.FromMinutes(int.Parse(a.Opt("--ttl-minutes") ?? "60", System.Globalization.CultureInfo.InvariantCulture));
                    var pin = a.Opt("--pin")?.ToLowerInvariant();
                    if (pin is not null && !Ids.TryParseHash(pin, out _)) throw new ArgumentException("--pin must be 64 hex chars");
                    var code = await BootstrapCodes.IssueAsync(db, pin, ttl, clock.GetUtcNow());
                    await Audit(db, clock, actor, a.Command.Replace(' ', '.'), null, pin ?? "");
                    stdout.WriteLine($"bootstrap code: {code}");
                    if (pin is not null) stdout.WriteLine($"pin (SPKI SHA-256): {pin}");
                    stdout.WriteLine($"valid until: {clock.GetUtcNow() + ttl:u} (one use)");
                    return 0;
                }
                case "member remove":
                case "device revoke":
                {
                    var team = Team(a);
                    var kind = a.Command == "member remove" ? RevocationKind.MemberRemove : RevocationKind.DeviceRevoke;
                    var subject = Guid.Parse(a.Required(kind == RevocationKind.MemberRemove ? "--member" : "--device"));
                    var r = await sp.GetRequiredService<RevocationService>().BlockAsync(
                        new RevocationRequest(kind, team, subject, "operator", actor, a.Opt("--reason")));
                    stdout.WriteLine(JsonSerializer.Serialize(r, JsonDefaults.Options));
                    return 0;
                }
                case "sessions revoke":
                {
                    var n = await sp.GetRequiredService<RevocationService>().RevokeSessionsAsync(Guid.Parse(a.Required("--device")), actor);
                    stdout.WriteLine($"sessions revoked: {n}");
                    return 0;
                }
                case "quarantine release":
                {
                    if (!a.Flag("--confirm")) throw new ArgumentException("operator release skips the owner device check; pass --confirm");
                    var team = Team(a);
                    await using var conn = await db.OpenConnectionAsync();
                    await using var tx = await conn.BeginTransactionAsync();
                    await Quarantine.ReleaseAsync(conn, tx, team, "operator", actor, clock.GetUtcNow());
                    await tx.CommitAsync();
                    stderr.WriteLine("WARNING: quarantine released by the operator; clients show this in a banner and it is in the audit log.");
                    return 0;
                }
                case "roster equivocation":
                {
                    var team = Team(a);
                    await using var conn = await db.OpenConnectionAsync();
                    var ev = await conn.SingleAsync("SELECT version, known, conflicting FROM roster_equivocations WHERE team_id = @t AND resolved_at IS NULL", null,
                        r => new { Version = r.GetInt64(0), Known = Convert.ToHexStringLower(r.Bytes(1)), Conflicting = Convert.ToHexStringLower(r.Bytes(2)) }, ("t", team));
                    if (ev is null) throw new InvalidOperationException("the team has no open roster equivocation");
                    stdout.WriteLine(JsonSerializer.Serialize(ev, JsonDefaults.Options));
                    return 0;
                }
                case "roster resolve":
                {
                    var team = Team(a);
                    var raw = await File.ReadAllBytesAsync(a.Required("--resolution"));
                    var text = System.Text.Encoding.ASCII.GetString(raw).Trim();
                    var resolution = text.Length > 0 && text.All(Uri.IsHexDigit) ? Convert.FromHexString(text) : raw;
                    var r = await sp.GetRequiredService<RosterService>().ResolveAsync(null, actor, team, resolution);
                    stdout.WriteLine(JsonSerializer.Serialize(r, JsonDefaults.Options));
                    stderr.WriteLine("Owner resolution applied: the team is unfrozen" + (r.Switched ? " on the conflicting branch." : " on the known branch."));
                    return 0;
                }
                case "replay-revocations":
                {
                    var (restored, present, chain) = await sp.GetRequiredService<RevocationService>().ReplayAsync(a.Opt("--log"));
                    stdout.WriteLine($"revocations.log: {chain.Count} entries verified; blocks re-created {restored}, already present {present}");
                    return 0;
                }
                case "backup":
                {
                    var recipients = new List<string>();
                    foreach (var r in a.All("--recipient")) { recipients.Add("--recipient"); recipients.Add(r); }
                    foreach (var f in a.All("--recipients-file")) { recipients.Add("--recipients-file"); recipients.Add(f); }
                    if (recipients.Count == 0) throw new ArgumentException("at least one --recipient or --recipients-file is required");
                    var m = await sp.GetRequiredService<BackupService>().BackupAsync(a.Required("--out"), recipients);
                    stdout.WriteLine(JsonSerializer.Serialize(m, JsonDefaults.Options));
                    return 0;
                }
                case "backup verify":
                {
                    var r = await sp.GetRequiredService<BackupService>().VerifyAsync(a.Required("--in"), a.Required("--identity"));
                    foreach (var (t, (e, act)) in r.Tables) stdout.WriteLine($"{t,-20} expected {e,8} restored {act,8}");
                    stdout.WriteLine($"blobs checked: {r.BlobsChecked}");
                    foreach (var p in r.Problems) stderr.WriteLine("PROBLEM: " + p);
                    stdout.WriteLine(r.Ok ? "backup verify: OK" : "backup verify: FAILED");
                    return r.Ok ? 0 : 1;
                }
                case "restore":
                {
                    var r = await sp.GetRequiredService<BackupService>().RestoreAsync(a.Required("--in"), a.Required("--identity"),
                        a.Opt("--revocations-log"), a.Opt("--blob-deletions-log"), a.Flag("--force"), a.Flag("--no-external-log"));
                    stdout.WriteLine(JsonSerializer.Serialize(r, JsonDefaults.Options));
                    if (!r.ExternalJournalsApplied)
                        stderr.WriteLine("WARNING: restored WITHOUT the off-host revocations.log/blob-deletions.log: blocks and blob deletions made " +
                                         "after the backup were not re-applied (recorded in the audit log).");
                    stderr.WriteLine("Restored. All teams are in QUARANTINE; the token key was rotated (every device must hello again). " +
                                     "Start the service; an owner device releases quarantine.");
                    return 0;
                }
                case "logs verify":
                {
                    var o = sp.GetRequiredService<IOptions<OmbraOptions>>().Value;
                    var ok = VerifyLog(o.RevocationsLogPath, o.RevocationsSink, stdout, stderr) & VerifyLog(o.BlobDeletionsLogPath, o.BlobDeletionsSink, stdout, stderr);
                    return ok ? 0 : 1;
                }
                case "retention run":
                {
                    var r = await sp.GetRequiredService<RetentionService>().RunOnceAsync(clock.GetUtcNow());
                    stdout.WriteLine(JsonSerializer.Serialize(r, JsonDefaults.Options));
                    return 0;
                }
                default:
                    stderr.WriteLine("unknown command: " + a.Command);
                    stderr.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or ChainBrokenException
                                       or InvalidDataException or IOException or NpgsqlException or OptionsValidationException or ApiException)
        {
            stderr.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    private static bool VerifyLog(string path, string? sink, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var local = HashChainLog.ReadVerified(path);
            stdout.WriteLine($"{Path.GetFileName(path)}: {local.Count} entries, chain OK");
            if (sink is null) return true;
            var remote = HashChainLog.ReadVerified(sink);
            if (remote.Count != local.Count || (local.Count > 0 && remote[^1].Hash != local[^1].Hash))
            {
                HashChainLog.Reconcile(local, remote);
                stderr.WriteLine($"{Path.GetFileName(path)}: sink has {remote.Count} entries, local {local.Count} (sink lagging)");
                return false;
            }
            stdout.WriteLine($"{Path.GetFileName(path)}: sink in sync");
            return true;
        }
        catch (ChainBrokenException e)
        {
            stderr.WriteLine($"{Path.GetFileName(path)}: BROKEN: {e.Message}");
            return false;
        }
    }

    private static byte[] Team(Args a) =>
        Ids.TryParseHash(a.Required("--team"), out var t) ? t : throw new ArgumentException("--team must be 64 hex chars");

    private static async Task Audit(NpgsqlDataSource db, TimeProvider clock, string actor, string action, byte[]? team, string subject)
    {
        await using var conn = await db.OpenConnectionAsync();
        await conn.ExecAsync("INSERT INTO audit (audit_id, at, actor, action, team_id, subject) VALUES (@a, @now, @actor, @action, @t, @s)", null,
            ("a", Guid.CreateVersion7()), ("now", clock.GetUtcNow()), ("actor", actor), ("action", action), ("t", team), ("s", subject));
    }

    public static ServiceProvider BuildServices(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging(l => l.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        services.AddOmbraCore(config, "ombra-admin");
        return services.BuildServiceProvider();
    }

    /// <summary>Environment (Ombra__*) plus the KEY=VALUE env file written by install.sh.</summary>
    public static IConfiguration LoadConfiguration(string? envFile)
    {
        var values = new Dictionary<string, string?>();
        if (envFile is not null && File.Exists(envFile))
        {
            foreach (var line in File.ReadAllLines(envFile))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                var eq = t.IndexOf('=');
                if (eq <= 0) continue;
                values[t[..eq].Trim().Replace("__", ":")] = t[(eq + 1)..].Trim().Trim('"');
            }
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).AddEnvironmentVariables().Build();
    }

    private sealed class Args
    {
        private readonly List<string> _rest = [];

        public Args(string[] args)
        {
            var words = new List<string>();
            var i = 0;
            for (; i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal) && words.Count < 2; i++) words.Add(args[i]);
            Command = string.Join(' ', words);
            if (words.Count == 2 && !KnownTwoWord.Contains(Command))
            {
                Command = words[0];
                _rest.Add(words[1]);
            }
            for (; i < args.Length; i++) _rest.Add(args[i]);
        }

        private static readonly HashSet<string> KnownTwoWord =
        [
            "bootstrap issue", "bootstrap reset", "member remove", "device revoke", "sessions revoke", "quarantine release",
            "backup verify", "logs verify", "retention run", "tls fingerprints", "tls rotate", "roster equivocation", "roster resolve",
        ];

        public string Command { get; }

        public string? Opt(string name)
        {
            var i = _rest.IndexOf(name);
            return i >= 0 && i + 1 < _rest.Count ? _rest[i + 1] : null;
        }

        public IEnumerable<string> All(string name)
        {
            for (var i = 0; i < _rest.Count - 1; i++)
                if (_rest[i] == name) yield return _rest[i + 1];
        }

        public bool Flag(string name) => _rest.Contains(name);

        public string Required(string name) => Opt(name) ?? throw new ArgumentException(name + " is required");
    }
}

/// <summary>§6.2 TLS keys in IP mode: SPKI pins and rotation current ← next, new next.</summary>
public static class Tls
{
    public static string SpkiSha256Hex(AsymmetricAlgorithm key) =>
        Convert.ToHexStringLower(SHA256.HashData(key switch
        {
            ECDsa ec => ec.ExportSubjectPublicKeyInfo(),
            RSA rsa => rsa.ExportSubjectPublicKeyInfo(),
            _ => throw new NotSupportedException(key.GetType().Name),
        }));

    private static ECDsa LoadKey(string path)
    {
        var k = ECDsa.Create();
        k.ImportFromPem(File.ReadAllText(path));
        return k;
    }

    public static int Fingerprints(string dir, TextWriter stdout)
    {
        foreach (var name in new[] { "current", "next" })
        {
            var p = Path.Combine(dir, name + ".key");
            if (!File.Exists(p)) { stdout.WriteLine($"{name}: (no key on this host)"); continue; }
            using var k = LoadKey(p);
            stdout.WriteLine($"{name}: {SpkiSha256Hex(k)}");
        }
        return 0;
    }

    /// <summary>
    /// current ← next (keeps the SAN/CN of the current certificate, re-issues a self-signed cert for the next key),
    /// then generates a new next key. The old current pair is kept as retired-&lt;timestamp&gt;.*; restart the server.
    /// </summary>
    public static int RotateToNext(string dir, bool confirmed, TextWriter stdout)
    {
        if (!confirmed) throw new ArgumentException("pass --to-next");
        var nextKeyPath = Path.Combine(dir, "next.key");
        if (!File.Exists(nextKeyPath)) throw new InvalidOperationException($"{nextKeyPath} not found: copy the offline next key back to this host first");
        using var currentCert = X509Certificate2.CreateFromPemFile(Path.Combine(dir, "current.crt"), Path.Combine(dir, "current.key"));
        using var nextKey = LoadKey(nextKeyPath);
        var req = new CertificateRequest(currentCert.SubjectName, nextKey, HashAlgorithmName.SHA256);
        foreach (var ext in currentCert.Extensions)
            if (ext.Oid?.Value == "2.5.29.17") req.CertificateExtensions.Add(ext); // subjectAltName
        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

        var stamp = now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        File.Move(Path.Combine(dir, "current.key"), Path.Combine(dir, $"retired-{stamp}.key"));
        File.Move(Path.Combine(dir, "current.crt"), Path.Combine(dir, $"retired-{stamp}.crt"));
        WritePrivate(Path.Combine(dir, "current.key"), nextKey.ExportPkcs8PrivateKeyPem());
        WritePrivate(Path.Combine(dir, "current.crt"), cert.ExportCertificatePem());
        File.Delete(nextKeyPath);
        using var newNext = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WritePrivate(nextKeyPath, newNext.ExportPkcs8PrivateKeyPem());
        stdout.WriteLine($"current (was next): {SpkiSha256Hex(nextKey)}");
        stdout.WriteLine($"new next:           {SpkiSha256Hex(newNext)}");
        stdout.WriteLine("Restart ombra-server. An admin publishes a RosterUpdate with pins {current, new next}; move next.key offline.");
        return 0;
    }

    private static void WritePrivate(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
