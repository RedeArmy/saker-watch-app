using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class PostgresReadinessCheckTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    [Fact]
    public async Task ReadinessCheck_ReportsHealthyForAReachableServer()
    {
        var check = CheckFor(postgres.ConnectionString);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task ReadinessCheck_ReportsUnhealthyWithoutDetailsWhenTheServerIsDown()
    {
        var check = CheckFor("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=2");

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().NotContain("127.0.0.1");
    }

    [Fact]
    public async Task ReadinessCheck_WhenTheTimeoutCancelsTheCheck_ReportsUnhealthyInsteadOfThrowing()
    {
        var check = CheckFor("Host=10.255.255.1;Port=5432;Username=x;Password=x;Database=x;Timeout=30");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), timeout.Token);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    private static PostgresReadinessCheck CheckFor(string connectionString) =>
        new(Options.Create(new PostgresOptions { Postgres = connectionString }));
}
