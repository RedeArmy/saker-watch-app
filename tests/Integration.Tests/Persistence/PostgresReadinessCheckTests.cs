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
    public async Task ReadinessCheck_WhenTheServerAcceptsButNeverAnswers_AndTheCheckIsCancelled_ReportsUnhealthyInsteadOfThrowing()
    {
        // A server that takes the connection and says nothing, on this machine: the check really is waiting for an answer when it is cancelled
        // (an address that nothing routes to may fail at once, and then the cancellation is never what ends the check).
        using var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        silent.Start();
        int port = ((System.Net.IPEndPoint)silent.LocalEndpoint).Port;
        var check = CheckFor($"Host=127.0.0.1;Port={port};Username=x;Password=x;Database=x;Timeout=60;Command Timeout=60");
        using var cancel = new CancellationTokenSource();

        Task<HealthCheckResult> running = check.CheckHealthAsync(new HealthCheckContext(), cancel.Token);
        using System.Net.Sockets.TcpClient accepted = await silent.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await cancel.CancelAsync();
        HealthCheckResult result = await running.WaitAsync(TimeSpan.FromSeconds(30));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("PostgreSQL did not answer in time.", "it is the cancellation that ended the check, not an unreachable address");
    }

    private static PostgresReadinessCheck CheckFor(string connectionString) =>
        new(Options.Create(new PostgresOptions { Postgres = connectionString }));
}
