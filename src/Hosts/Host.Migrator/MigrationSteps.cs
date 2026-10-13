using SakerWatch.BuildingBlocks.Persistence.Platform;

namespace SakerWatch.Hosts.Migrator;

/// <summary>The ordered list of migration steps. Order matters: platform creates the roles and extensions first.</summary>
public static class MigrationSteps
{
    /// <summary>Builds the steps for an admin PostgreSQL connection string.</summary>
    public static IReadOnlyList<IMigrationStep> Create(string postgresAdminConnectionString) =>
    [
        PlatformStep(postgresAdminConnectionString),
    ];

    /// <summary>The platform step on its own (tests migrate it to a specific version).</summary>
    public static EfMigrationStep<PlatformDbContext> PlatformStep(string postgresAdminConnectionString) =>
        new("platform", postgresAdminConnectionString, PlatformDbContext.Schema, options => new PlatformDbContext(options));
}
