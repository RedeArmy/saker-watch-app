using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using SakerWatch.BuildingBlocks.Mongo.Migrations;

namespace SakerWatch.Hosts.Migrator;

/// <summary>Applies the MongoDB migrations with the administrative connection, after PostgreSQL.</summary>
/// <param name="connectionString">Admin connection string (root or <c>dbOwner</c> user); never the runtime user.</param>
/// <param name="database">Database name.</param>
/// <param name="timeProvider">Clock for the history timestamps and the lock.</param>
/// <param name="loggerFactory">Creates the runner logger.</param>
public sealed class MongoMigrationStep(string connectionString, string database, TimeProvider timeProvider, ILoggerFactory loggerFactory) : IMigrationStep
{
    /// <inheritdoc />
    public string Name => "mongo";

    /// <inheritdoc />
    public Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var client = new MongoClient(connectionString);
        var runner = new MongoMigrationRunner(client.GetDatabase(database), timeProvider, loggerFactory.CreateLogger<MongoMigrationRunner>());
        return runner.RunAsync(MongoMigrations.All, lockOptions: null, cancellationToken);
    }
}
