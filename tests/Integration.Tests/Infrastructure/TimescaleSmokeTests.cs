using AwesomeAssertions;
using Npgsql;

namespace SakerWatch.Integration.Tests.Infrastructure;

[Trait("Category", "Integration")]
public sealed class TimescaleSmokeTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    [Fact]
    public async Task Container_WithTimescaleExtension_ReportsInstalledVersion()
    {
        await using var dataSource = NpgsqlDataSource.Create(postgres.ConnectionString);
        await using NpgsqlCommand create = dataSource.CreateCommand("CREATE EXTENSION IF NOT EXISTS timescaledb");
        await create.ExecuteNonQueryAsync();

        await using NpgsqlCommand query = dataSource.CreateCommand(
            "SELECT extversion FROM pg_extension WHERE extname = 'timescaledb'");
        object? version = await query.ExecuteScalarAsync();

        version.Should().BeOfType<string>().Which.Should().NotBeNullOrWhiteSpace();
    }
}
