using Npgsql;
using SakerWatch.Hosts.Migrator;
using Testcontainers.PostgreSql;

namespace SakerWatch.Integration.Tests.Infrastructure;

/// <summary>A freshly migrated database with one login per runtime role.</summary>
/// <param name="Admin">Superuser connection string (migrations, seeding, assertions).</param>
/// <param name="Api">Connection string of a login that is a member of <c>api_role</c>.</param>
/// <param name="Worker">Connection string of a login that is a member of <c>worker_role</c>.</param>
internal sealed record MigratedDatabase(string Admin, string Api, string Worker);

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

    /// <summary>Creates a database, applies every migration (platform, then identity) and creates one login per runtime role.</summary>
    internal async Task<MigratedDatabase> CreateMigratedDatabaseAsync()
    {
        string admin = await CreateDatabaseAsync();
        foreach (IMigrationStep step in MigrationSteps.Create(admin))
        {
            await step.RunAsync(CancellationToken.None);
        }

        return new MigratedDatabase(
            admin,
            await CreateLoginAsync(admin, "api_role"),
            await CreateLoginAsync(admin, "worker_role"));
    }

    /// <summary>Creates a login that is a member of a runtime role on an already prepared database (upgrade-path tests).</summary>
    internal static Task<string> CreateRoleLoginAsync(string admin, string role) => CreateLoginAsync(admin, role);

    private static async Task<string> CreateLoginAsync(string admin, string role)
    {
        string login = $"l_{role}_{Guid.CreateVersion7():N}";
        string password = Guid.CreateVersion7().ToString("N");
        await admin.ExecuteAsync($"CREATE ROLE {login} LOGIN PASSWORD '{password}' IN ROLE {role}");
        return admin.ForLogin(login, password);
    }
}
