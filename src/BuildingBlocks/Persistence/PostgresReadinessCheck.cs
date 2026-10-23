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
            // WaitAsync makes the check end when the token does: the driver can sit in the start-up of a connection to a server that accepts and then says
            // nothing until its own connect timeout, and a readiness probe must answer within the time it was given. The abandoned attempt is closed with the connection.
            await connection.OpenAsync(cancellationToken).WaitAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken).WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            // When the token ended the check, that is the cause whatever the driver made of it: it can report its own cancellation as a connection
            // failure, and "not reachable" would then send the operator after the network.
            return HealthCheckResult.Unhealthy(ex is OperationCanceledException || cancellationToken.IsCancellationRequested ? "PostgreSQL did not answer in time." : "PostgreSQL is not reachable.", ex);
        }
    }
}
