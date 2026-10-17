using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using SakerWatch.MarketData.Application.Providers;

namespace SakerWatch.MarketData.Infrastructure.Providers;

/// <summary>
/// <see cref="IProviderPayloadStore"/> over the <c>provider_payloads</c> collection. The document id is derived from the provider, the
/// operation, the day and a hash of what was asked, so the same request on the same day is one document (replaced, not duplicated) and
/// no caller-supplied text becomes a key. The body is stored as a string, never as nested BSON, so nothing in it can be read as an operator
/// or a field name. The collection and its TTL index come from the Mongo migrations.
/// </summary>
/// <param name="database">The application database.</param>
public sealed class MongoProviderPayloadStore(IMongoDatabase database) : IProviderPayloadStore
{
    /// <summary>Name of the collection (created by migration <c>V0001</c>).</summary>
    public const string CollectionName = "provider_payloads";

    /// <summary>The document schema version stored in every payload.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The deterministic id of a payload.</summary>
    public static string IdFor(ProviderPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        string asked = $"{payload.Key?.ProviderMic}|{payload.Key?.Symbol}|{payload.Query}|{payload.Adjusted}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(asked)))[..32];
        return $"{payload.Provider}:{payload.Operation}:{payload.FetchedAt.UtcDateTime:yyyyMMdd}:{hash}";
    }

    /// <inheritdoc />
    public async Task StoreAsync(ProviderPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var request = new BsonDocument { { "query", payload.Query } };
        if (payload.Key is { } key)
        {
            request.Add("mic", key.ProviderMic);
            request.Add("symbol", key.Symbol);
        }

        if (payload.Adjusted is { } adjusted)
        {
            request.Add("adjusted", adjusted);
        }

        byte[] bytes = Encoding.UTF8.GetBytes(payload.Body);
        var document = new BsonDocument
        {
            { "_id", IdFor(payload) },
            { "schemaVersion", SchemaVersion },
            { "createdAt", payload.FetchedAt.UtcDateTime },
            { "provider", payload.Provider },
            { "operation", payload.Operation },
            { "request", request },
            { "bodySha256", Convert.ToHexString(SHA256.HashData(bytes)) },
            { "bodyBytes", bytes.Length },
            { "body", payload.Body },
        };

        await database.GetCollection<BsonDocument>(CollectionName).ReplaceOneAsync(
            new BsonDocument("_id", document["_id"]),
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }
}
