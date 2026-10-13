namespace SakerWatch.BuildingBlocks.Mongo.Migrations;

/// <summary>
/// The write-once document collections of the data model (docs/data-model.md section 2). Every document must carry
/// <c>schemaVersion</c> and <c>createdAt</c>; TTL indexes on <c>createdAt</c> enforce the retention windows.
/// <c>provider_payloads</c> starts at its 90-day maximum; the Retention module can tighten it with <c>collMod</c> (Phase 5).
/// </summary>
public sealed class V0001BaseCollections : MongoMigration
{
    /// <summary>Document retention in days per collection (a single source for the migration and its tests).</summary>
    public static IReadOnlyDictionary<string, int?> RetentionDays { get; } = new Dictionary<string, int?>
    {
        ["llm_interactions"] = 365,
        ["provider_payloads"] = 90,
        ["analysis_context"] = 365,
        ["job_events"] = 30,
        ["eval_runs"] = null,
    };

    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    public override string Name => "base-collections";

    /// <inheritdoc />
    protected override IReadOnlyList<CollectionSpec> Collections { get; } =
        [.. RetentionDays.Select(pair => new CollectionSpec(
            pair.Key,
            pair.Value is int days
                ? [new IndexSpec("ttl_created_at", [("createdAt", 1)], ExpireAfter: TimeSpan.FromDays(days))]
                : []))];
}

/// <summary>Every Mongo migration of the product, in order.</summary>
public static class MongoMigrations
{
    /// <summary>The migrations the migrator applies.</summary>
    public static IReadOnlyList<IMongoMigration> All { get; } = [new V0001BaseCollections()];
}
