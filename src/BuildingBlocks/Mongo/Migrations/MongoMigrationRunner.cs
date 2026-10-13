using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SakerWatch.BuildingBlocks.Mongo.Migrations;

/// <summary>An applied migration was edited: its stored checksum differs from the code.</summary>
public sealed class MongoMigrationChecksumException(int version, string expected, string actual)
    : InvalidOperationException($"Applied Mongo migration {version} was modified (stored checksum {expected}, code checksum {actual}); add a new migration instead.");

/// <summary>The migration lock could not be acquired in time.</summary>
public sealed class MongoMigrationLockException(TimeSpan waited)
    : TimeoutException($"Could not acquire the Mongo migration lock within {waited.TotalSeconds:0.#}s; another migrator may be running.");

/// <summary>Timing of the migration lock.</summary>
/// <param name="LockLifetime">How long a held lock stays valid; a crashed migrator's lock expires after this.</param>
/// <param name="LockWait">How long a runner waits for another migrator to finish before giving up.</param>
public sealed record MongoMigrationLockOptions(TimeSpan LockLifetime, TimeSpan LockWait)
{
    /// <summary>Defaults: a 10-minute lock and a 2-minute wait.</summary>
    public static MongoMigrationLockOptions Default { get; } = new(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2));
}

/// <summary>
/// Applies ordered <see cref="IMongoMigration"/>s to a database (ADR-0004). Applied versions are recorded in
/// <c>_migrations</c> with their checksum; a lock document in <c>_migration_lock</c> keeps concurrent migrators from
/// interleaving. Safe to re-run: applied versions are skipped and every migration is idempotent.
/// </summary>
/// <param name="database">Target database, connected with an administrative user.</param>
/// <param name="timeProvider">Clock for timestamps and lock expiry.</param>
/// <param name="logger">Runner logger.</param>
public sealed partial class MongoMigrationRunner(IMongoDatabase database, TimeProvider timeProvider, ILogger<MongoMigrationRunner> logger)
{
    private const string _historyCollection = "_migrations";
    private const string _lockCollection = "_migration_lock";
    private const string _lockId = "migrations";

    /// <summary>Applies pending migrations in version order and returns how many were applied.</summary>
    public async Task<int> RunAsync(IEnumerable<IMongoMigration> migrations, MongoMigrationLockOptions? lockOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        IMongoMigration[] ordered = [.. migrations.OrderBy(m => m.Version)];
        if (ordered.Select(m => m.Version).Distinct().Count() != ordered.Length)
        {
            throw new ArgumentException("Mongo migration versions must be unique.", nameof(migrations));
        }

        MongoMigrationLockOptions timing = lockOptions ?? MongoMigrationLockOptions.Default;
        string owner = $"{Environment.MachineName}:{Guid.CreateVersion7():N}";
        await AcquireLockAsync(owner, timing, cancellationToken);
        try
        {
            return await ApplyPendingAsync(ordered, cancellationToken);
        }
        finally
        {
            await ReleaseLockAsync(owner);
        }
    }

    private async Task<int> ApplyPendingAsync(IMongoMigration[] ordered, CancellationToken cancellationToken)
    {
        IMongoCollection<BsonDocument> history = database.GetCollection<BsonDocument>(_historyCollection);
        var applied = (await history.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(cancellationToken))
            .ToDictionary(d => d["_id"].AsInt32, d => d["checksum"].AsString);

        int count = 0;
        foreach (IMongoMigration migration in ordered)
        {
            if (applied.TryGetValue(migration.Version, out string? stored))
            {
                if (!string.Equals(stored, migration.Checksum, StringComparison.Ordinal))
                {
                    throw new MongoMigrationChecksumException(migration.Version, stored, migration.Checksum);
                }

                continue;
            }

            await migration.UpAsync(database, cancellationToken);
            await history.InsertOneAsync(
                new BsonDocument
                {
                    { "_id", migration.Version },
                    { "name", migration.Name },
                    { "checksum", migration.Checksum },
                    { "appliedAt", timeProvider.GetUtcNow().UtcDateTime },
                },
                cancellationToken: cancellationToken);
            MigrationApplied(migration.Version, migration.Name);
            count++;
        }

        return count;
    }

    private async Task AcquireLockAsync(string owner, MongoMigrationLockOptions timing, CancellationToken cancellationToken)
    {
        IMongoCollection<BsonDocument> locks = database.GetCollection<BsonDocument>(_lockCollection);
        long deadline = timeProvider.GetTimestamp() + (long)(timing.LockWait.TotalSeconds * timeProvider.TimestampFrequency);

        while (true)
        {
            DateTime now = timeProvider.GetUtcNow().UtcDateTime;
            FilterDefinition<BsonDocument> free = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id", _lockId),
                Builders<BsonDocument>.Filter.Lt("expiresAt", now));
            UpdateDefinition<BsonDocument> take = Builders<BsonDocument>.Update
                .Set("owner", owner)
                .Set("expiresAt", now + timing.LockLifetime);

            try
            {
                // Matches only an expired lock; when none exists the upsert inserts it; when a live lock exists the upsert
                // collides on _id and fails with a duplicate-key error, which means "held".
                await locks.UpdateOneAsync(free, take, new UpdateOptions { IsUpsert = true }, cancellationToken);
                return;
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                if (timeProvider.GetTimestamp() >= deadline)
                {
                    throw new MongoMigrationLockException(timing.LockWait);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), timeProvider, cancellationToken);
            }
        }
    }

    private async Task ReleaseLockAsync(string owner)
    {
        IMongoCollection<BsonDocument> locks = database.GetCollection<BsonDocument>(_lockCollection);
        await locks.DeleteOneAsync(Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("_id", _lockId),
            Builders<BsonDocument>.Filter.Eq("owner", owner)));
    }

    [LoggerMessage(EventId = 9311, Level = LogLevel.Information, Message = "Mongo migration {Version} {Name} applied")]
    private partial void MigrationApplied(int version, string name);
}
