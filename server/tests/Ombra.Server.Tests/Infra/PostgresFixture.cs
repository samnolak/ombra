using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Ombra.Server.Tests.Infra.PostgresFixture))]

namespace Ombra.Server.Tests.Infra;

/// <summary>
/// One real Postgres 17 container per test run (Testcontainers); every test gets its own fresh database.
/// On macOS with colima the Docker socket is discovered automatically.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string AdminConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var colima = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".colima", "default", "docker.sock");
        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is null && File.Exists(colima))
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", "unix://" + colima);
            Environment.SetEnvironmentVariable("TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE", "/var/run/docker.sock");
        }
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithCommand("-c", "max_connections=400")
            .Build();
        await _container.StartAsync();
        AdminConnectionString = _container.GetConnectionString();
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N")[..16];
        await using (var conn = new NpgsqlConnection(AdminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name, MaxPoolSize = 60 }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}
