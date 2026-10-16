using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace SakerWatch.BuildingBlocks.Redis;

/// <summary>Readiness probe: Redis answers a ping. The response carries no connection details.</summary>
/// <param name="connection">Shared multiplexer.</param>
public sealed class RedisReadinessCheck(IConnectionMultiplexer connection) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await connection.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(ex is OperationCanceledException ? "Redis did not answer in time." : "Redis is not reachable.", ex);
        }
    }
}
