using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using SakerWatch.BuildingBlocks.Mongo.Migrations;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Infrastructure.Providers;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class ProviderPayloadStoreTests(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private static readonly DateTimeOffset _fetched = new(2026, 10, 16, 21, 5, 0, TimeSpan.Zero);

    private static ProviderPayload Payload(string body = "{\"values\":[]}", string symbol = "AAPL", DateTimeOffset? at = null) =>
        new("twelvedata", "daily", new ProviderListingKey("XNGS", symbol), "2024-06-01..2024-06-20", true, body, at ?? _fetched);

    private static async Task<IMongoDatabase> MigratedAsync(MongoFixture mongo)
    {
        IMongoDatabase db = mongo.NewDatabase();
        await new MongoMigrationRunner(db, TimeProvider.System, NullLogger<MongoMigrationRunner>.Instance).RunAsync(MongoMigrations.All, null, CancellationToken.None);
        return db;
    }

    [Fact]
    public async Task Store_WritesAValidatedDocument_WithTheRawBodyAndItsHash()
    {
        IMongoDatabase db = await MigratedAsync(mongo);
        const string body = "{ \"values\": [ {\"datetime\":\"2024-06-03\"} ],  \"note\":\"$where is just text\" }";

        await new MongoProviderPayloadStore(db).StoreAsync(Payload(body), CancellationToken.None);

        BsonDocument doc = await db.GetCollection<BsonDocument>("provider_payloads").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        doc["schemaVersion"].AsInt32.Should().Be(1);
        doc["createdAt"].ToUniversalTime().Should().Be(_fetched.UtcDateTime);
        doc["provider"].AsString.Should().Be("twelvedata");
        doc["operation"].AsString.Should().Be("daily");
        doc["request"]["mic"].AsString.Should().Be("XNGS");
        doc["request"]["symbol"].AsString.Should().Be("AAPL");
        doc["request"]["adjusted"].AsBoolean.Should().BeTrue();
        doc["body"].AsString.Should().Be(body, "the body is kept as text, never as nested documents");
        doc["bodyBytes"].AsInt32.Should().Be(System.Text.Encoding.UTF8.GetByteCount(body));
        doc["bodySha256"].AsString.Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))));
    }

    [Fact]
    public async Task TheSameRequestOnTheSameDay_IsOneDocument_ReplacedByTheLatest()
    {
        IMongoDatabase db = await MigratedAsync(mongo);
        var store = new MongoProviderPayloadStore(db);

        await store.StoreAsync(Payload("{\"first\":1}"), CancellationToken.None);
        await store.StoreAsync(Payload("{\"second\":2}", at: _fetched.AddHours(1)), CancellationToken.None);

        List<BsonDocument> docs = await db.GetCollection<BsonDocument>("provider_payloads").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        docs.Should().ContainSingle().Which["body"].AsString.Should().Be("{\"second\":2}");
    }

    [Fact]
    public async Task ADifferentSymbolOrADifferentDay_IsAnotherDocument()
    {
        IMongoDatabase db = await MigratedAsync(mongo);
        var store = new MongoProviderPayloadStore(db);

        await store.StoreAsync(Payload(), CancellationToken.None);
        await store.StoreAsync(Payload(symbol: "MSFT"), CancellationToken.None);
        await store.StoreAsync(Payload(at: _fetched.AddDays(1)), CancellationToken.None);

        (await db.GetCollection<BsonDocument>("provider_payloads").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).Should().Be(3);
    }

    [Fact]
    public void TheId_IsDeterministic_AndBuiltFromAHashNotFromTheCallersText()
    {
        string id = MongoProviderPayloadStore.IdFor(Payload(symbol: "A.B"));

        id.Should().Be(MongoProviderPayloadStore.IdFor(Payload(symbol: "A.B")));
        id.Should().StartWith("twelvedata:daily:20261016:").And.NotContain("A.B");
        id.Should().NotBe(MongoProviderPayloadStore.IdFor(Payload(symbol: "A.C")));
    }

    [Fact]
    public async Task Migrate_AddsTheLookupIndex_AndKeepsTheRetentionOne()
    {
        IMongoDatabase db = await MigratedAsync(mongo);

        List<BsonDocument> indexes = await (await db.GetCollection<BsonDocument>("provider_payloads").Indexes.ListAsync()).ToListAsync();

        indexes.Select(i => i["name"].AsString).Should().Contain(["ttl_created_at", "ix_provider_request"]);
        indexes.Single(i => i["name"] == "ix_provider_request")["key"].AsBsonDocument.Names.Should().Equal("provider", "request.mic", "request.symbol", "createdAt");
    }

    [Fact]
    public async Task TheValidator_RefusesAPayloadWithoutTheStandardFields()
    {
        IMongoDatabase db = await MigratedAsync(mongo);

        Func<Task> act = () => db.GetCollection<BsonDocument>("provider_payloads").InsertOneAsync(new BsonDocument { { "_id", "x" }, { "provider", "p" } });

        await act.Should().ThrowAsync<MongoWriteException>();
    }

    [Fact]
    public async Task Store_WhenMongoIsUnreachable_Throws_SoTheProviderCanLogAndMoveOn()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:1");
        settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(300);
        var store = new MongoProviderPayloadStore(new MongoClient(settings).GetDatabase("down"));

        Func<Task> act = () => store.StoreAsync(Payload(), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }
}
