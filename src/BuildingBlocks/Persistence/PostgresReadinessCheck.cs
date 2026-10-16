using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;

namespace SakerWatch.BuildingBlocks.Persistence;

/// <summary>Readiness probe: the runtime role can connect and run a trivial query. The response carries no connection details.</summary>
/// <param name="options">Validated connection settings.</param>
public sealed class PostgresReadinessCheck(IOptions<PostgresOptions> options) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(options.Value.Postgres);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(ex is OperationCanceledException ? "PostgreSQL did not answer in time." : "PostgreSQL is not reachable.", ex);
        }
    }
}
