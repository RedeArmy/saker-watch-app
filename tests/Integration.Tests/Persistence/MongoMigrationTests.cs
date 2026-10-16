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

    private sealed class SlowMigration(TimeSpan duration) : IMongoMigration
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public int Version => 1;

        public string Name => "slow";

        public string Checksum => "slow-v1";

        public async Task UpAsync(IMongoDatabase database, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            await Task.Delay(duration, cancellationToken);
        }
    }

    [Fact]
    public async Task AMigrationThatOutlastsTheLockLifetime_KeepsTheLockWhileItRuns()
    {
        IMongoDatabase db = mongo.NewDatabase();
        // Real timers, so the margins are wide: the lock is renewed every second, would expire after three, and the second migrator
        // arrives after four (a loaded CI machine can delay a renewal by a good fraction of a second without changing the outcome).
        var slow = new SlowMigration(TimeSpan.FromSeconds(7));
        var shortLock = new MongoMigrationLockOptions(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(60));

        Task<int> first = RunnerFor(db).RunAsync([slow], shortLock, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(4));
        Task<int> second = RunnerFor(db).RunAsync([slow], shortLock, CancellationToken.None);

        (await first).Should().Be(1);
        (await second).Should().Be(0, "the second migrator waited for the lock and found the migration applied");
        slow.Runs.Should().Be(1, "without renewal the lock would have expired after 3 s and the second runner would have started it again");
    }

    [Fact]
    public async Task TheLockOwnerId_IsStampedWithTheInjectedClock_NotTheSystemOne()
    {
        IMongoDatabase db = mongo.NewDatabase();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2031, 3, 4, 5, 6, 7, TimeSpan.Zero));
        var runner = new MongoMigrationRunner(db, clock, NullLogger<MongoMigrationRunner>.Instance);
        var slow = new SlowMigration(TimeSpan.FromSeconds(3));

        Task<int> run = runner.RunAsync([slow], new MongoMigrationLockOptions(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30)), CancellationToken.None);
        IMongoCollection<BsonDocument> locks = db.GetCollection<BsonDocument>("_migration_lock");
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        BsonDocument? held;
        while ((held = await locks.Find(new BsonDocument("_id", "migrations")).FirstOrDefaultAsync(bound.Token)) is null)
        {
            await Task.Delay(50, bound.Token);
        }

        string owner = held["owner"].AsString;
        var id = Guid.ParseExact(owner[(owner.LastIndexOf(':') + 1)..], "N");
        byte[] bytes = id.ToByteArray(bigEndian: true);
        long unixMilliseconds = (long)bytes[0] << 40 | (long)bytes[1] << 32 | (long)bytes[2] << 24 | (long)bytes[3] << 16 | (long)bytes[4] << 8 | bytes[5];
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).Should().Be(clock.GetUtcNow().AddTicks(-(clock.GetUtcNow().Ticks % TimeSpan.TicksPerMillisecond)));
    }

    [Fact]
    public async Task WhenTheLockIsLostMidRun_TheRunStopsInsteadOfInterleaving()
    {
        IMongoDatabase db = mongo.NewDatabase();
        var slow = new SlowMigration(TimeSpan.FromSeconds(30));
        var shortLock = new MongoMigrationLockOptions(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));

        Task<int> run = RunnerFor(db).RunAsync([slow], shortLock, CancellationToken.None);

        // Remove the lock only once the runner really holds it (on a slow machine the first connection alone can take longer than any
        // fixed pause); deleting it earlier would remove nothing and the migration would simply run its 30 seconds.
        IMongoCollection<BsonDocument> locks = db.GetCollection<BsonDocument>("_migration_lock");
        using var waitForLock = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await locks.CountDocumentsAsync(new BsonDocument("_id", "migrations"), cancellationToken: waitForLock.Token) == 0)
        {
            await Task.Delay(50, waitForLock.Token);
        }

        await locks.DeleteOneAsync(new BsonDocument("_id", "migrations"));

        Func<Task> act = () => run.WaitAsync(TimeSpan.FromSeconds(30));

        await act.Should().ThrowAsync<MongoMigrationLockLostException>();
        (await db.GetCollection<BsonDocument>("_migrations").CountDocumentsAsync(new BsonDocument())).Should().Be(0, "the interrupted migration is not recorded as applied");
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
