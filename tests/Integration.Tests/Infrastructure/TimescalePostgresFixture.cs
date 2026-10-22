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

    public TimescalePostgresFixture() => _template = new Lazy<Task<string>>(BuildTemplateAsync);

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
        await using var server = NpgsqlDataSource.Create(ConnectionString);
        await using NpgsqlCommand create = server.CreateCommand($"CREATE DATABASE {name}");
        await create.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    private readonly Lazy<Task<string>> _template;

    /// <summary>
    /// Creates a database with every migration applied (platform, identity, marketdata...) and one login per runtime role. The migrations run once
    /// per fixture, into a template database, and each call copies it (<c>CREATE DATABASE ... TEMPLATE</c>, a copy of files in a fraction of the
    /// time of running every migration again): a test pays for a copy and not for the whole schema.
    /// </summary>
    internal async Task<MigratedDatabase> CreateMigratedDatabaseAsync()
    {
        string template = await _template.Value;
        string name = $"t_{Guid.CreateVersion7():N}";
        await using (var server = NpgsqlDataSource.Create(ConnectionString))
        {
            await using NpgsqlCommand create = server.CreateCommand($"CREATE DATABASE {name} TEMPLATE {template}");
            await create.ExecuteNonQueryAsync();
        }

        // A copy keeps what is inside the database but not the privileges on the database itself, which a migration can set (HardenPlatformPrivileges
        // revokes TEMPORARY from PUBLIC). They are set again here, and the copy is compared with the template so that a migration that sets another one
        // makes every test fail with a clear message instead of leaving the copies weaker than a database that was migrated.
        await using (var server = NpgsqlDataSource.Create(ConnectionString))
        {
            await using NpgsqlCommand revoke = server.CreateCommand($"REVOKE TEMPORARY ON DATABASE {name} FROM PUBLIC");
            await revoke.ExecuteNonQueryAsync();
            await using NpgsqlCommand compare = server.CreateCommand(
                "SELECT (SELECT coalesce(datacl::text, '') FROM pg_database WHERE datname = @template) = (SELECT coalesce(datacl::text, '') FROM pg_database WHERE datname = @copy)");
            compare.Parameters.AddWithValue("template", template);
            compare.Parameters.AddWithValue("copy", name);
            if (!(bool)(await compare.ExecuteScalarAsync())!)
            {
                throw new InvalidOperationException("The privileges on the database of a copy differ from those of the migrated template: a migration sets one that CreateMigratedDatabaseAsync does not repeat after the copy.");
            }
        }

        string admin = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
        return new MigratedDatabase(
            admin,
            await CreateLoginAsync(admin, "api_role"),
            await CreateLoginAsync(admin, "worker_role"));
    }

    // The template is migrated over connections that are not pooled: CREATE DATABASE ... TEMPLATE needs nobody connected to it. Lazy runs it once, whoever asks first.
    private async Task<string> BuildTemplateAsync()
    {
        string admin = await CreateDatabaseAsync();
        string unpooled = new NpgsqlConnectionStringBuilder(admin) { Pooling = false }.ConnectionString;
        foreach (IMigrationStep step in MigrationSteps.Create(unpooled))
        {
            await step.RunAsync(CancellationToken.None);
        }

        // Nobody may be connected to a template while it is copied, and TimescaleDB's own background workers connect to every database that has the
        // extension. Closing the door to new connections and then ending the sessions that are there leaves it quiet, for good.
        string name = new NpgsqlConnectionStringBuilder(admin).Database!;
        await using var server = NpgsqlDataSource.Create(ConnectionString);
        await using (NpgsqlCommand close = server.CreateCommand($"ALTER DATABASE {name} WITH ALLOW_CONNECTIONS false"))
        {
            await close.ExecuteNonQueryAsync();
        }

        await using NpgsqlCommand end = server.CreateCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid()");
        end.Parameters.AddWithValue("name", name);
        await end.ExecuteNonQueryAsync();
        return name;
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
