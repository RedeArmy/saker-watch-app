using Npgsql;
using Testcontainers.PostgreSql;

namespace SakerWatch.Integration.Tests.Infrastructure;

/// <summary>
/// Starts a throwaway PostgreSQL + TimescaleDB container using the exact image declared in
/// <c>deploy/docker-compose.yml</c>, so integration tests run against the real engine instead of an in-memory substitute.
/// </summary>
public sealed class TimescalePostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder(ComposeImages.For("timescale/timescaledb")).Build();

    /// <summary>Connection string of the running container (superuser; tests create their own roles).</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <inheritdoc />
    public Task InitializeAsync() => _container.StartAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database so each test class migrates from scratch, and returns its superuser connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        string name = $"t_{Guid.CreateVersion7():N}";
        await using NpgsqlDataSource server = NpgsqlDataSource.Create(ConnectionString);
        await using NpgsqlCommand create = server.CreateCommand($"CREATE DATABASE {name}");
        await create.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }
}
