using Testcontainers.PostgreSql;

namespace SakerWatch.Integration.Tests.Infrastructure;

/// <summary>
/// Starts a throwaway PostgreSQL + TimescaleDB container, the same image family the dev compose file uses,
/// so integration tests run against the real engine instead of an in-memory substitute.
/// </summary>
public sealed class TimescalePostgresFixture : IAsyncLifetime
{
    // Keep in sync with deploy/docker-compose.yml by hand: Dependabot only bumps the compose file.
    private const string _image = "timescale/timescaledb:2.30.2-pg16";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(_image).Build();

    /// <summary>Connection string of the running container (superuser; tests create their own roles).</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <inheritdoc />
    public Task InitializeAsync() => _container.StartAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
