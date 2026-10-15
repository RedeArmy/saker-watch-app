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
            await connection.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable.", ex);
        }
    }
}
