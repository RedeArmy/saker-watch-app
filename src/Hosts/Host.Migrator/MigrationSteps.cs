using SakerWatch.BuildingBlocks.Persistence.Platform;
using SakerWatch.Identity.Infrastructure.Persistence;
using SakerWatch.MarketData.Infrastructure.Persistence;

namespace SakerWatch.Hosts.Migrator;

/// <summary>The ordered list of migration steps. Order matters: platform creates the roles and extensions first.</summary>
public static class MigrationSteps
{
    /// <summary>Builds the steps for an admin PostgreSQL connection string.</summary>
    public static IReadOnlyList<IMigrationStep> Create(string postgresAdminConnectionString) =>
    [
        PlatformStep(postgresAdminConnectionString),
        IdentityStep(postgresAdminConnectionString),
        MarketDataStep(postgresAdminConnectionString),
    ];

    /// <summary>The platform step on its own (tests migrate it to a specific version).</summary>
    public static EfMigrationStep<PlatformDbContext> PlatformStep(string postgresAdminConnectionString) =>
        new("platform", postgresAdminConnectionString, PlatformDbContext.Schema, options => new PlatformDbContext(options));

    /// <summary>The identity step on its own (tests migrate it to a specific version).</summary>
    public static EfMigrationStep<IdentityDbContext> IdentityStep(string postgresAdminConnectionString) =>
        new("identity", postgresAdminConnectionString, IdentityDbContext.Schema, options => new IdentityDbContext(options));

    /// <summary>The market data step on its own (tests migrate it to a specific version).</summary>
    public static EfMigrationStep<MarketDataDbContext> MarketDataStep(string postgresAdminConnectionString) =>
        new("marketdata", postgresAdminConnectionString, MarketDataDbContext.Schema, options => new MarketDataDbContext(options));
}
