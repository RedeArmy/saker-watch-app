using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SakerWatch.BuildingBlocks.Mongo;

/// <summary>Readiness probe: the MongoDB server answers a ping. The response carries no connection details.</summary>
/// <param name="database">Database to ping.</param>
public sealed class MongoReadinessCheck(IMongoDatabase database) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("MongoDB is not reachable.");
        }
    }
}
