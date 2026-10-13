using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SakerWatch.Hosts.Migrator;

/// <summary>Applies the pending EF Core migrations of one module context with the admin (migrator) connection.</summary>
/// <typeparam name="TContext">The module's DbContext.</typeparam>
/// <param name="name">Step name for logs.</param>
/// <param name="connectionString">Admin connection string; never the runtime login.</param>
/// <param name="historySchema">Schema that holds the context's <c>__ef_migrations_history</c> table.</param>
/// <param name="factory">Creates the context from options.</param>
public sealed class EfMigrationStep<TContext>(
    string name,
    string connectionString,
    string historySchema,
    Func<DbContextOptions<TContext>, TContext> factory) : IMigrationStep
    where TContext : DbContext
{
    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public Task<int> RunAsync(CancellationToken cancellationToken) => ApplyAsync(targetMigration: null, cancellationToken);

    /// <summary>
    /// Migrates up to the migration with the given name (for example <c>CreateUsers</c>), for upgrade-path tests that start
    /// from a previous schema version. The timestamp prefix of the migration id is resolved automatically.
    /// </summary>
    public async Task<int> MigrateToAsync(string migrationName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationName);

        await using TContext context = factory(BuildOptions());
        string target = context.GetService<IMigrationsAssembly>().Migrations.Keys
            .Single(id => id.EndsWith($"_{migrationName}", StringComparison.Ordinal));
        return await ApplyAsync(target, cancellationToken);
    }

    private async Task<int> ApplyAsync(string? targetMigration, CancellationToken cancellationToken)
    {
        await using TContext context = factory(BuildOptions());
        int pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        if (pending == 0)
        {
            return 0;
        }

        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration, cancellationToken);
        return pending;
    }

    private DbContextOptions<TContext> BuildOptions() =>
        new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", historySchema))
            .Options;
}
