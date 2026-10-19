using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Ombra.ClientCore.Teams;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Ombra.Teams.Tests.Infra.ServerStack))]

namespace Ombra.Teams.Tests.Infra;

/// <summary>
/// Once per run: a Docker network, Postgres 17 on it (alias <c>pg</c>) and the team server image built from the
/// current sources with the production Dockerfile (server/deploy/Dockerfile: Linux, OpenSSL TLS 1.3, ombra-core .so).
/// Kestrel on macOS cannot serve TLS 1.3, so the real server runs in a container even on a Mac (colima's socket is
/// found automatically).
/// </summary>
public sealed class ServerStack : IAsyncLifetime
{
    /// <summary>Unique per test run: parallel runs (other agents, CI shards) must not delete each other's image.</summary>
    public static readonly string Image = "ombra-server:teams-test-" + Guid.NewGuid().ToString("N")[..12];
    private INetwork? _network;
    private PostgreSqlContainer? _pg;

    public INetwork Network => _network!;
    public string AdminConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var colima = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".colima", "default", "docker.sock");
        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is null && File.Exists(colima))
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", "unix://" + colima);
            Environment.SetEnvironmentVariable("TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE", "/var/run/docker.sock");
        }
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();
        _pg = new PostgreSqlBuilder("postgres:17-alpine").WithNetwork(_network).WithNetworkAliases("pg").WithCommand("-c", "max_connections=300").Build();
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(ServerDir())
            .WithDockerfile("deploy/Dockerfile")
            .WithName(Image)
            .WithCleanUp(true)
            .WithDeleteIfExists(true) // this run's own tag (several test runs in parallel); Docker's layer cache keeps it fast
            .Build();
        await Task.WhenAll(_pg.StartAsync(), image.CreateAsync());
        AdminConnectionString = _pg.GetConnectionString();
    }

    private static string ServerDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "server", "deploy", "Dockerfile"))) return Path.Combine(d.FullName, "server");
        throw new InvalidOperationException("server/deploy/Dockerfile not found above the test output");
    }

    public async Task<(string Host, string Inner)> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N")[..16];
        await using (var conn = new NpgsqlConnection(AdminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var host = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name, MaxPoolSize = 20 };
        var inner = new NpgsqlConnectionStringBuilder(host.ConnectionString) { Host = "pg", Port = 5432 };
        return (host.ConnectionString, inner.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_pg is not null) await _pg.DisposeAsync();
        if (_network is not null) await _network.DisposeAsync();
    }
}

/// <summary>
/// One team server as install.sh runs it in IP mode: the production image, TLS 1.3 with a fresh self-signed key whose
/// SPKI SHA-256 is the pin, its own Postgres database, token key and serverId. The test process reaches the
/// database directly (bootstrap code, tampering by a "malicious server", dumps).
/// </summary>
public sealed class TeamServerHost : IAsyncDisposable
{
    private readonly IContainer _container;

    private TeamServerHost(IContainer container, string root, string pin, NpgsqlDataSource db)
    {
        _container = container;
        Root = root;
        Pin = pin;
        Db = db;
    }

    public string Root { get; }
    public string Pin { get; }
    public NpgsqlDataSource Db { get; }
    public string Address => $"https://127.0.0.1:{_container.GetMappedPublicPort(8443)}";

    public static async Task<TeamServerHost> StartAsync(ServerStack stack)
    {
        var root = Path.Combine(Path.GetTempPath(), "ombra-teams-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pin = ServerEndpoint.SpkiPin(cert);
        var (hostDb, innerDb) = await stack.CreateDatabaseAsync();
        const uint Readable = 0x1A4; // 0644: the image runs as user app
        var container = new ContainerBuilder(ServerStack.Image)
            .WithNetwork(stack.Network)
            .WithEnvironment("Ombra__ConnectionString", innerDb)
            .WithEnvironment("Ombra__Mode", "ip")
            .WithEnvironment("Ombra__ListenUrl", "https://0.0.0.0:8443")
            .WithEnvironment("Ombra__TlsCertFile", "/etc/ombra/tls/current.crt")
            .WithEnvironment("Ombra__TlsKeyFile", "/etc/ombra/tls/current.key")
            .WithEnvironment("Ombra__TokenKeyFile", "/etc/ombra/token.key")
            .WithEnvironment("Ombra__ServerIdFile", "/etc/ombra/server-id")
            .WithEnvironment("Ombra__DataDir", "/var/lib/ombra")
            .WithEnvironment("Ombra__AgePath", "/usr/bin/age")
            .WithEnvironment("Ombra__Limits__PreAuthRequestsPerMinute", "100000")
            .WithResourceMapping(System.Text.Encoding.ASCII.GetBytes(cert.ExportCertificatePem()), "/etc/ombra/tls/current.crt", Readable)
            .WithResourceMapping(System.Text.Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem()), "/etc/ombra/tls/current.key", Readable)
            .WithResourceMapping(System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n"), "/etc/ombra/token.key", Readable)
            .WithResourceMapping(System.Text.Encoding.ASCII.GetBytes(Guid.NewGuid() + "\n"), "/etc/ombra/server-id", Readable)
            .WithPortBinding(8443, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening"))
            .Build();
        await container.StartAsync();
        return new TeamServerHost(container, root, pin, NpgsqlDataSource.Create(hostDb));
    }

    /// <summary>Server logs (stdout/stderr) — searched for secrets in the SD-01 test.</summary>
    public async Task<string> LogsAsync()
    {
        var (o, e) = await _container.GetLogsAsync();
        return o + e;
    }

    /// <summary>
    /// A one-time bootstrap code bound to this server's pin, written as <c>ombra-admin bootstrap issue --pin</c> does:
    /// only SHA-256 of the normalised code is stored.
    /// </summary>
    public async Task<string> BootstrapCodeAsync()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var code = new string(Enumerable.Range(0, 26).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
        await ExecAsync("INSERT INTO bootstrap_codes (code_hash, spki_pin, created_at, expires_at) VALUES (@h, @p, now(), now() + interval '1 hour')",
            ("h", SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code))), ("p", Pin));
        return string.Join("-", code.Chunk(5).Select(c => new string(c)));
    }

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

    /// <summary>Text dump of every table (bytea as \x hex): what an attacker reading the database sees.</summary>
    public async Task<string> DumpAsync()
    {
        await using var conn = await Db.OpenConnectionAsync();
        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) tables.Add(r.GetString(0));
        var sb = new System.Text.StringBuilder();
        foreach (var t in tables)
        {
            await using var cmd = new NpgsqlCommand($"SELECT x::text FROM \"{t}\" x", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) sb.AppendLine(r.GetString(0));
        }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        await Db.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

/// <summary>In-memory key store for tests that are not about the Keychain itself.</summary>
public sealed class MemoryKeyStore : ITeamsKeyStore
{
    private readonly Dictionary<string, byte[]> _items = [];
    public IReadOnlyCollection<string> Accounts => _items.Keys;
    public byte[]? Read(string account) => _items.TryGetValue(account, out var v) ? v.ToArray() : null;
    public void Write(string account, ReadOnlySpan<byte> secret) => _items[account] = secret.ToArray();
    public void Delete(string account) => _items.Remove(account);
}

/// <summary>An OS key store that fails every call (locked/missing keychain, no entitlement).</summary>
public sealed class FailingKeyStore : ITeamsKeyStore
{
    public byte[]? Read(string account) => throw new KeyStoreUnavailableException("keychain unavailable", -25308);
    public void Write(string account, ReadOnlySpan<byte> secret) => throw new KeyStoreUnavailableException("keychain unavailable", -25308);
    public void Delete(string account) => throw new KeyStoreUnavailableException("keychain unavailable", -25308);
}

public sealed class FixedMachine(string id) : IMachineIdentity
{
    public byte[] MachineId() => System.Text.Encoding.UTF8.GetBytes(id);
}

/// <summary>One client device (its own profile dir, key store and server session) driving the real Teams module.</summary>
public sealed class Client : IDisposable
{
    public Client(TeamServerHost host, string name, ITeamsKeyStore? keys = null, IMachineIdentity? machine = null, string? root = null)
    {
        Root = root ?? Path.Combine(host.Root, "clients", name);
        Keys = keys ?? new MemoryKeyStore();
        Machine = machine ?? new FixedMachine("machine-" + name);
        Device = TeamsDevice.Open(Root, "server", Keys, Machine);
        Server = new TeamsServer(ServerEndpoint.Parse(host.Address, host.Pin));
        Server.Attach(Device);
        Teams = new TeamClient(Server, Device);
    }

    public string Root { get; }
    public ITeamsKeyStore Keys { get; }
    public IMachineIdentity Machine { get; }
    public TeamsDevice Device { get; }
    public TeamsServer Server { get; }
    public TeamClient Teams { get; }
    public Ombra.ClientCore.Teams.DeviceIdentity Id => Device.Identity!;

    public void InitFresh() => Device.Initialize(Guid.CreateVersion7(), Guid.CreateVersion7());

    public void Dispose()
    {
        Server.Dispose();
        Device.Dispose();
    }
}
