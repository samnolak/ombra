using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Ombra.Server.Auth;
using Ombra.Server.Contracts;

namespace Ombra.Server.Tests.Infra;

/// <summary>
/// The real server (WebApplicationFactory, TestServer) over a fresh Postgres database, a temp data dir and a fake
/// clock. Roster projection / groups are seeded with SQL: in production they are written by the roster and commit
/// paths that go through the ombra-core validator, which is not part of these tests.
/// </summary>
public sealed class ServerHarness : IAsyncDisposable
{
    private ServerHarness(PostgresFixture pg, string connectionString, string root, Dictionary<string, string?> settings, FakeTimeProvider clock)
    {
        Pg = pg;
        ConnectionString = connectionString;
        Root = root;
        Settings = settings;
        Clock = clock;
        Db = NpgsqlDataSource.Create(connectionString);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in settings) b.UseSetting(k, v);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(clock);
            });
        });
    }

    public PostgresFixture Pg { get; }
    public string ConnectionString { get; }
    public string Root { get; }
    public Dictionary<string, string?> Settings { get; }
    public FakeTimeProvider Clock { get; }
    public NpgsqlDataSource Db { get; }
    public WebApplicationFactory<Program> Factory { get; }
    public string DataDir => Settings["Ombra:DataDir"]!;
    public Guid ServerId => Guid.Parse(File.ReadAllText(Settings["Ombra:ServerIdFile"]!).Trim());

    public static string AgePath()
    {
        var env = Environment.GetEnvironmentVariable("OMBRA_TEST_AGE");
        if (env is not null) return env;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (File.Exists(Path.Combine(dir, "age"))) return Path.Combine(dir, "age");
        throw new InvalidOperationException("age binary not found: set OMBRA_TEST_AGE or put age on PATH");
    }

    /// <summary>Settings of a fresh "host": temp /etc (token key, server-id), data dir, fresh database.</summary>
    public static async Task<(string Root, Dictionary<string, string?> Settings)> CreateHostAsync(PostgresFixture pg,
        Action<Dictionary<string, string?>>? tweak = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "ombra-server-tests", Guid.NewGuid().ToString("N"));
        var etc = Path.Combine(root, "etc");
        Directory.CreateDirectory(etc);
        TokenKey.Generate(Path.Combine(etc, "token.key"));
        await File.WriteAllTextAsync(Path.Combine(etc, "server-id"), Guid.NewGuid() + "\n");
        var settings = new Dictionary<string, string?>
        {
            ["Ombra:ConnectionString"] = await pg.CreateDatabaseAsync(),
            ["Ombra:DataDir"] = Path.Combine(root, "data"),
            ["Ombra:TokenKeyFile"] = Path.Combine(etc, "token.key"),
            ["Ombra:ServerIdFile"] = Path.Combine(etc, "server-id"),
            ["Ombra:Mode"] = "domain",
            ["Ombra:ListenUrl"] = "http://127.0.0.1:0",
            ["Ombra:AgePath"] = AgePath(),
            ["Ombra:Limits:RetentionIntervalSeconds"] = "0",
            // All test clients share one (null) remote address; tests of the per-address limit lower it explicitly.
            ["Ombra:Limits:PreAuthRequestsPerMinute"] = "100000",
        };
        tweak?.Invoke(settings);
        return (root, settings);
    }

    public static async Task<ServerHarness> StartAsync(PostgresFixture pg, Action<Dictionary<string, string?>>? tweak = null)
    {
        var (root, settings) = await CreateHostAsync(pg, tweak);
        return Start(pg, root, settings);
    }

    public static ServerHarness Start(PostgresFixture pg, string root, Dictionary<string, string?> settings)
    {
        var h = new ServerHarness(pg, settings["Ombra:ConnectionString"]!, root, settings, new FakeTimeProvider(DateTimeOffset.UtcNow));
        _ = h.Factory.Server; // start: runs migrations and startup invariants
        return h;
    }

    public T Service<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

    public HttpClient Client(TestDevice? device = null)
    {
        var c = Factory.CreateClient();
        if (device?.AccessToken is { } t) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t);
        return c;
    }

    // ---- seeding of the roster projection / groups (written by the ombra-core validated paths in production) ----

    public async Task<byte[]> SeedTeamAsync()
    {
        var team = RandomNumberGenerator.GetBytes(32);
        await using var conn = await Db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO teams (team_id, created_at, roster_version, roster_hash) VALUES (@t, now(), 1, @h);" +
            "INSERT INTO roster_versions (team_id, version, hash, prev_hash, roster_update, accepted_at) VALUES (@t, 1, @h, NULL, @u, now());", conn);
        cmd.Parameters.AddWithValue("t", team);
        cmd.Parameters.AddWithValue("h", RandomNumberGenerator.GetBytes(32));
        cmd.Parameters.AddWithValue("u", RandomNumberGenerator.GetBytes(64));
        await cmd.ExecuteNonQueryAsync();
        return team;
    }

    /// <summary>New member with one device in the roster projection.</summary>
    public async Task<TestDevice> AddMemberAsync(byte[] team, string role = "member")
    {
        var member = Guid.CreateVersion7();
        await ExecAsync("INSERT INTO members (member_id, created_at) VALUES (@m, now()); " +
                        "INSERT INTO used_ids (id, kind, source, at) VALUES (@m, 'member', 'test', now()); " +
                        "INSERT INTO roster_members (team_id, member_id, role, added_in_version) VALUES (@t, @m, @r, 1)",
            ("m", member), ("t", team), ("r", role));
        return await AddDeviceAsync(team, member);
    }

    public async Task<TestDevice> AddDeviceAsync(byte[] team, Guid member)
    {
        var d = new TestDevice(member);
        await ExecAsync(
            "INSERT INTO devices (device_id, member_id, product, identity_key, mls_signature_key, created_at) VALUES (@d, @m, 'gui', @ik, @mk, now()); " +
            "INSERT INTO used_ids (id, kind, source, at) VALUES (@d, 'device', 'test', now()); " +
            "INSERT INTO roster_devices (team_id, device_id, member_id, identity_key_hash, mls_key_hash, cert_hash, added_in_version) " +
            "VALUES (@t, @d, @m, sha256(@ik), sha256(@mk), sha256(@ik), 1)",
            ("d", d.DeviceId), ("m", member), ("ik", d.IdentityKey), ("mk", d.MlsSignatureKey), ("t", team));
        return d;
    }

    public async Task<byte[]> AddGroupAsync(byte[] team, string kind, IEnumerable<TestDevice> leaves, long epoch = 1, Guid? memberA = null, Guid? memberB = null)
    {
        var group = RandomNumberGenerator.GetBytes(32);
        await ExecAsync("INSERT INTO groups (group_id, team_id, kind, member_a, member_b, epoch, created_at) VALUES (@g, @t, @k, @a, @b, @e, now())",
            ("g", group), ("t", team), ("k", kind), ("a", memberA), ("b", memberB), ("e", epoch));
        foreach (var d in leaves) await AddLeafAsync(group, d);
        return group;
    }

    public Task AddLeafAsync(byte[] group, TestDevice d) =>
        ExecAsync("INSERT INTO group_leaves (group_id, device_id) VALUES (@g, @d)", ("g", group), ("d", d.DeviceId));

    public async Task ExecAsync(string sql, params (string, object?)[] args)
    {
        await using var conn = await Db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string, object?)[] args)
    {
        await using var conn = await Db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    // ---- protocol helpers ----

    public async Task<HttpResponseMessage> HelloAsync(TestDevice d, int protocol = Protocol.Current)
    {
        using var c = Client();
        var ch = await (await c.PostAsJsonAsync("/v1/session/challenge", new ChallengeRequest(d.DeviceId))).Content.ReadFromJsonAsync<ChallengeResponse>();
        var time = (ulong)Clock.GetUtcNow().ToUnixTimeSeconds();
        return await c.PostAsJsonAsync("/v1/session/hello", new HelloRequest(protocol, d.DeviceId, ch!.Nonce, time, d.SignHello(ch.ServerId, ch.Nonce, time)));
    }

    public async Task LoginAsync(TestDevice d)
    {
        var r = await HelloAsync(d);
        r.EnsureSuccessStatusCode();
        var hello = (await r.Content.ReadFromJsonAsync<HelloResponse>())!;
        d.AccessToken = hello.Tokens.AccessToken;
        d.RefreshToken = hello.Tokens.RefreshToken;
    }

    public async Task<HttpResponseMessage> SendEnvelopeAsync(TestDevice d, HttpMethod method, string url, string kind, byte[] scope, CborMapWriter body,
        Guid? opId = null, ulong baseRevision = 0, HttpClient? client = null)
    {
        var (encoded, sig) = d.Envelope(kind, scope, body, Clock.GetUtcNow(), opId, baseRevision);
        var req = new HttpRequestMessage(method, url) { Content = new ByteArrayContent(encoded) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(Headers.EnvelopeContentType);
        req.Headers.Add(Headers.Signature, sig);
        if (client is not null) return await client.SendAsync(req);
        using var c = Client(d);
        return await c.SendAsync(req);
    }

    public Task<HttpResponseMessage> PutOpAsync(TestDevice d, byte[] group, long epoch, byte[] ciphertext, Guid? opId = null, bool ephemeral = false, HttpClient? client = null) =>
        SendEnvelopeAsync(d, HttpMethod.Post, $"/v1/groups/{Ids.Hex(group)}/ops", EnvelopeKinds.OpPut, group,
            new OpPutBody((ulong)epoch, ciphertext, ephemeral).ToCbor(), opId, client: client);

    public async Task<SyncResponse> SyncAsync(TestDevice d, string? cursor = null)
    {
        using var c = Client(d);
        var r = await c.GetAsync("/v1/sync" + (cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor)));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<SyncResponse>())!;
    }

    public static async Task<string?> ReasonAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<ErrorResponse>())?.Reason;

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
