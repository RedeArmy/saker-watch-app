using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SakerWatch.BuildingBlocks.Mongo.Migrations;

/// <summary>Index definition of a collection. A non-null <see cref="ExpireAfter"/> makes it a TTL index on that single field.</summary>
/// <param name="Name">Index name; stable, because creating the same name with a different definition fails.</param>
/// <param name="Keys">Ordered fields with direction 1 (ascending) or -1 (descending).</param>
/// <param name="Unique">Whether the index enforces uniqueness.</param>
/// <param name="ExpireAfter">Time to live for TTL indexes.</param>
public sealed record IndexSpec(string Name, IReadOnlyList<(string Field, int Direction)> Keys, bool Unique = false, TimeSpan? ExpireAfter = null);

/// <summary>Collection definition: standard-field validation and indexes.</summary>
/// <param name="Name">Collection name.</param>
/// <param name="Indexes">Indexes to create.</param>
/// <param name="RequireStandardFields">Require <c>schemaVersion</c> (int) and <c>createdAt</c> (date) on every document (data model rule).</param>
public sealed record CollectionSpec(string Name, IReadOnlyList<IndexSpec> Indexes, bool RequireStandardFields = true);

/// <summary>A versioned, ordered, idempotent MongoDB migration (ADR-0004).</summary>
public interface IMongoMigration
{
    /// <summary>Strictly increasing version; migrations apply in this order.</summary>
    int Version { get; }

    /// <summary>Short human name, stored with the record.</summary>
    string Name { get; }

    /// <summary>Fingerprint of what the migration does; a different value for an applied version means an applied migration was edited.</summary>
    string Checksum { get; }

    /// <summary>Applies the migration. Must be safe to run again after a crash.</summary>
    Task UpAsync(IMongoDatabase database, CancellationToken cancellationToken);
}

/// <summary>
/// Declarative migration: collections with validators and indexes (including TTL). The checksum is derived from the
/// declaration, so editing an applied migration is detected; creating an existing collection or identical index is a no-op.
/// </summary>
public abstract class MongoMigration : IMongoMigration
{
    /// <inheritdoc />
    public abstract int Version { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>The collections this migration ensures.</summary>
    protected abstract IReadOnlyList<CollectionSpec> Collections { get; }

    /// <inheritdoc />
    public string Checksum => ComputeChecksum(Version, Name, Collections);

    /// <inheritdoc />
    public async Task UpAsync(IMongoDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        var existing = new HashSet<string>(await (await database.ListCollectionNamesAsync(cancellationToken: cancellationToken)).ToListAsync(cancellationToken));
        foreach (CollectionSpec spec in Collections)
        {
            if (!existing.Contains(spec.Name))
            {
                await database.CreateCollectionAsync(spec.Name, CreateOptions(spec), cancellationToken);
            }

            IMongoCollection<BsonDocument> collection = database.GetCollection<BsonDocument>(spec.Name);
            foreach (IndexSpec index in spec.Indexes)
            {
                await collection.Indexes.CreateOneAsync(ToModel(index), cancellationToken: cancellationToken);
            }
        }
    }

    private static CreateCollectionOptions<BsonDocument> CreateOptions(CollectionSpec spec)
    {
        var options = new CreateCollectionOptions<BsonDocument>();
        if (spec.RequireStandardFields)
        {
            options.Validator = new BsonDocumentFilterDefinition<BsonDocument>(StandardFieldsValidator());
            options.ValidationAction = DocumentValidationAction.Error;
            options.ValidationLevel = DocumentValidationLevel.Strict;
        }

        return options;
    }

    private static BsonDocument StandardFieldsValidator() => new(
        "$jsonSchema",
        new BsonDocument
        {
            { "bsonType", "object" },
            { "required", new BsonArray { "schemaVersion", "createdAt" } },
            {
                "properties", new BsonDocument
                {
                    { "schemaVersion", new BsonDocument("bsonType", "int") },
                    { "createdAt", new BsonDocument("bsonType", "date") },
                }
            },
        });

    private static CreateIndexModel<BsonDocument> ToModel(IndexSpec index)
    {
        var keys = new BsonDocument();
        foreach ((string field, int direction) in index.Keys)
        {
            keys.Add(field, direction);
        }

        var options = new CreateIndexOptions { Name = index.Name, Unique = index.Unique ? true : null, ExpireAfter = index.ExpireAfter };
        return new CreateIndexModel<BsonDocument>(new BsonDocumentIndexKeysDefinition<BsonDocument>(keys), options);
    }

    private static string ComputeChecksum(int version, string name, IReadOnlyList<CollectionSpec> collections)
    {
        var text = new StringBuilder().Append(version).Append('|').Append(name);
        foreach (CollectionSpec collection in collections)
        {
            text.Append("|c:").Append(collection.Name).Append(':').Append(collection.RequireStandardFields);
            foreach (IndexSpec index in collection.Indexes)
            {
                text.Append("|i:").Append(index.Name).Append(':').Append(index.Unique).Append(':').Append(index.ExpireAfter?.TotalSeconds)
                    .Append(':').Append(string.Join(',', index.Keys.Select(k => $"{k.Field}={k.Direction}")));
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
