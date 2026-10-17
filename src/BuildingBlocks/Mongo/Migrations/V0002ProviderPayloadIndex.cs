namespace SakerWatch.BuildingBlocks.Mongo.Migrations;

/// <summary>
/// Lets a stored provider response be found by who sent it and what was asked, newest first, without scanning the collection
/// (<c>provider_payloads</c>, milestone 2.2). The TTL index of <see cref="V0001BaseCollections"/> stays as it is.
/// </summary>
public sealed class V0002ProviderPayloadIndex : MongoMigration
{
    /// <inheritdoc />
    public override int Version => 2;

    /// <inheritdoc />
    public override string Name => "provider-payload-index";

    /// <inheritdoc />
    protected override IReadOnlyList<CollectionSpec> Collections { get; } =
    [
        new CollectionSpec(
            "provider_payloads",
            [new IndexSpec("ix_provider_request", [("provider", 1), ("request.mic", 1), ("request.symbol", 1), ("createdAt", -1)])]),
    ];
}
