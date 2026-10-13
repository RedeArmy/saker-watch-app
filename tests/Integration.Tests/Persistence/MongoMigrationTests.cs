using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SakerWatch.BuildingBlocks.Mongo;
using SakerWatch.BuildingBlocks.Mongo.Migrations;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class MongoMigrationTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private sealed class TestMigration(int version, string name, params CollectionSpec[] collections) : MongoMigration
    {
        public override int Version => version;

        public override string Name => name;

        protected override IReadOnlyList<CollectionSpec> Collections => collections;
    }

    private static MongoMigrationRunner RunnerFor(IMongoDatabase database) =>
        new(database, TimeProvider.System, NullLogger<MongoMigrationRunner>.Instance);

    private static async Task<List<BsonDocument>> IndexesOf(IMongoDatabase database, string collection) =>
        await (await database.GetCollection<BsonDocument>(collection).Indexes.ListAsync()).ToListAsync();

    [Fact]
    public async Task Migrate_OnEmptyDatabase_CreatesTheBaseCollectionsWithRetentionIndexes()
    {
        IMongoDatabase db = mongo.NewDatabase();

        int applied = await RunnerFor(db).RunAsync(MongoMigrations.All, lockOptions: null, CancellationToken.None);

        applied.Should().Be(1);
        foreach ((string collection, int? days) in V0001BaseCollections.RetentionDays)
        {
            List<BsonDocument> indexes = await IndexesOf(db, collection);
            BsonDocument? ttl = indexes.SingleOrDefault(i => i["name"] == "ttl_created_at");
            if (days is int expected)
            {
                ttl.Should().NotBeNull($"{collection} expires after {expected} days");
                ttl["expireAfterSeconds"].ToInt64().Should().Be(TimeSpan.FromDays(expected).Ticks / TimeSpan.TicksPerSecond);
            }
            else
            {
                ttl.Should().BeNull($"{collection} is kept indefinitely");
            }
        }
    }

    [Fact]
    public async Task Migrate_RecordsTheAppliedVersionWithItsChecksum()
    {
        IMongoDatabase db = mongo.NewDatabase();

        await RunnerFor(db).RunAsync(MongoMigrations.All, null, CancellationToken.None);

        BsonDocument record = await db.GetCollection<BsonDocument>("_migrations").Find(new BsonDocument()).SingleAsync();
        record["_id"].AsInt32.Should().Be(1);
        record["name"].AsString.Should().Be("base-collections");
        record["checksum"].AsString.Should().Be(MongoMigrations.All[0].Checksum);
        record["appliedAt"].IsValidDateTime.Should().BeTrue();
    }

    [Fact]
    public async Task Migrate_RunTwice_SecondRunAppliesNothing()
    {
        IMongoDatabase db = mongo.NewDatabase();
        MongoMigrationRunner runner = RunnerFor(db);

        await runner.RunAsync(MongoMigrations.All, null, CancellationToken.None);
        int second = await runner.RunAsync(MongoMigrations.All, null, CancellationToken.None);

        second.Should().Be(0);
    }

    [Fact]
    public async Task Collections_RejectDocumentsWithoutTheStandardFields()
    {
        IMongoDatabase db = mongo.NewDatabase();
        await RunnerFor(db).RunAsync(MongoMigrations.All, null, CancellationToken.None);
        IMongoCollection<BsonDocument> events = db.GetCollection<BsonDocument>("job_events");

        await events.InsertOneAsync(new BsonDocument { { "schemaVersion", 1 }, { "createdAt", DateTime.UtcNow } });

        Func<Task> missingVersion = () => events.InsertOneAsync(new BsonDocument { { "createdAt", DateTime.UtcNow } });
        Func<Task> wrongDateType = () => events.InsertOneAsync(new BsonDocument { { "schemaVersion", 1 }, { "createdAt", "yesterday" } });

        (await missingVersion.Should().ThrowAsync<MongoWriteException>()).Which.WriteError.Code.Should().Be(121);
        (await wrongDateType.Should().ThrowAsync<MongoWriteException>()).Which.WriteError.Code.Should().Be(121);
    }

    [Fact]
    public async Task EditingAnAppliedMigration_IsDetectedByItsChecksum()
    {
        IMongoDatabase db = mongo.NewDatabase();
        var original = new TestMigration(1, "things", new CollectionSpec("things", [new IndexSpec("ix_a", [("a", 1)])]));
        var edited = new TestMigration(1, "things", new CollectionSpec("things", [new IndexSpec("ix_a", [("a", 1)]), new IndexSpec("ix_b", [("b", 1)])]));
        await RunnerFor(db).RunAsync([original], null, CancellationToken.None);

        Func<Task> act = () => RunnerFor(db).RunAsync([edited], null, CancellationToken.None);

        await act.Should().ThrowAsync<MongoMigrationChecksumException>().WithMessage("*modified*");
    }

    [Fact]
    public async Task UpgradeFromThePreviousVersion_KeepsDocumentsAndAddsTheNewIndex()
    {
        IMongoDatabase db = mongo.NewDatabase();
        await RunnerFor(db).RunAsync(MongoMigrations.All, null, CancellationToken.None);
        await db.GetCollection<BsonDocument>("provider_payloads")
            .InsertOneAsync(new BsonDocument { { "schemaVersion", 1 }, { "createdAt", DateTime.UtcNow }, { "source", "polygon" } });
        var next = new TestMigration(2, "payload-source-index", new CollectionSpec("provider_payloads", [new IndexSpec("ix_source", [("source", 1)])]));

        int applied = await RunnerFor(db).RunAsync([.. MongoMigrations.All, next], null, CancellationToken.None);

        applied.Should().Be(1);
        (await db.GetCollection<BsonDocument>("provider_payloads").CountDocumentsAsync(new BsonDocument())).Should().Be(1);
        (await IndexesOf(db, "provider_payloads")).Should().Contain(i => i["name"] == "ix_source");
    }

    [Fact]
    public async Task ParallelMigrators_ApplyEachMigrationExactlyOnce()
    {
        IMongoDatabase db = mongo.NewDatabase();

        int[] results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => RunnerFor(db).RunAsync(MongoMigrations.All, null, CancellationToken.None)));

        results.Sum().Should().Be(1);
        (await db.GetCollection<BsonDocument>("_migrations").CountDocumentsAsync(new BsonDocument())).Should().Be(1);
        (await db.GetCollection<BsonDocument>("_migration_lock").CountDocumentsAsync(new BsonDocument())).Should().Be(0, "the lock is released");
    }

    [Fact]
    public async Task ALiveLockHeldByAnotherMigrator_MakesTheRunnerGiveUp()
    {
        IMongoDatabase db = mongo.NewDatabase();
        await db.GetCollection<BsonDocument>("_migration_lock")
            .InsertOneAsync(new BsonDocument { { "_id", "migrations" }, { "owner", "someone-else" }, { "expiresAt", DateTime.UtcNow.AddHours(1) } });
        var quick = new MongoMigrationLockOptions(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));

        Func<Task> act = () => RunnerFor(db).RunAsync(MongoMigrations.All, quick, CancellationToken.None);

        await act.Should().ThrowAsync<MongoMigrationLockException>();
        (await db.GetCollection<BsonDocument>("_migrations").CountDocumentsAsync(new BsonDocument())).Should().Be(0);
    }

    [Fact]
    public async Task AnExpiredLockFromACrashedMigrator_IsTakenOver()
    {
        IMongoDatabase db = mongo.NewDatabase();
        await db.GetCollection<BsonDocument>("_migration_lock")
            .InsertOneAsync(new BsonDocument { { "_id", "migrations" }, { "owner", "crashed" }, { "expiresAt", DateTime.UtcNow.AddMinutes(-5) } });

        int applied = await RunnerFor(db).RunAsync(MongoMigrations.All, null, CancellationToken.None);

        applied.Should().Be(1);
    }

    [Fact]
    public async Task DuplicateVersions_AreRejectedBeforeAnythingRuns()
    {
        IMongoDatabase db = mongo.NewDatabase();
        var a = new TestMigration(1, "a", new CollectionSpec("a", []));
        var b = new TestMigration(1, "b", new CollectionSpec("b", []));

        Func<Task> act = () => RunnerFor(db).RunAsync([a, b], null, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadinessCheck_ReportsHealthyForAReachableServer()
    {
        var check = new MongoReadinessCheck(mongo.NewDatabase());

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task ReadinessCheck_ReportsUnhealthyWithoutDetailsWhenTheServerIsDown()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:1");
        settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(500);
        var check = new MongoReadinessCheck(new MongoClient(settings).GetDatabase("down"));

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().NotContain("127.0.0.1");
    }

    [Fact]
    public void Options_WithoutAConnectionString_FailValidation()
    {
        var services = new ServiceCollection();
        services.AddSakerMongo(new ConfigurationBuilder().Build());
        using ServiceProvider provider = services.BuildServiceProvider();

        Action act = () => _ = provider.GetRequiredService<IOptions<MongoOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*ConnectionString*");
    }
}
