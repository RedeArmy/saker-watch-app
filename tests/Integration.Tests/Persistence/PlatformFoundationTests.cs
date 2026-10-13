using AwesomeAssertions;
using SakerWatch.Hosts.Migrator;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class PlatformFoundationTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    [Fact]
    public async Task Migrate_OnEmptyDatabase_EnablesTimescaleAndCreatesPlatformSchema()
    {
        string db = await postgres.CreateDatabaseAsync();

        await MigrationSteps.PlatformStep(db).RunAsync(CancellationToken.None);

        (await db.ScalarAsync<string>("SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'")).Should().NotBeNullOrEmpty();
        (await db.ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'platform')")).Should().BeTrue();
    }

    [Theory]
    [InlineData("api_role")]
    [InlineData("worker_role")]
    public async Task Migrate_CreatesRuntimeRoles_WithoutSuperuserBypassOrLogin(string role)
    {
        string db = await postgres.CreateDatabaseAsync();

        await MigrationSteps.PlatformStep(db).RunAsync(CancellationToken.None);

        (await db.ScalarAsync<bool>("SELECT rolsuper OR rolbypassrls OR rolcanlogin OR rolcreaterole OR rolcreatedb FROM pg_roles WHERE rolname = @r", ("r", role)))
            .Should().BeFalse($"{role} must be a plain least-privilege group role");
    }

    [Fact]
    public async Task Migrate_RunTwice_SecondRunAppliesNothing()
    {
        string db = await postgres.CreateDatabaseAsync();
        var step = MigrationSteps.PlatformStep(db);

        int first = await step.RunAsync(CancellationToken.None);
        int second = await step.RunAsync(CancellationToken.None);

        first.Should().BeGreaterThan(0);
        second.Should().Be(0);
    }

    [Fact]
    public async Task Migrate_OnAnotherDatabaseOfTheSameCluster_ReusesTheExistingRoles()
    {
        string first = await postgres.CreateDatabaseAsync();
        string second = await postgres.CreateDatabaseAsync();

        await MigrationSteps.PlatformStep(first).RunAsync(CancellationToken.None);
        Func<Task> act = async () => await MigrationSteps.PlatformStep(second).RunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync("roles are cluster-wide and creation is idempotent");
    }
}
