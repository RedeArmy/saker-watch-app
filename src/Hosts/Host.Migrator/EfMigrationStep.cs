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

    /// <summary>Migrates up to <paramref name="targetMigration"/> (a migration id), for upgrade-path tests from a previous schema version.</summary>
    public Task<int> MigrateToAsync(string targetMigration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMigration);
        return ApplyAsync(targetMigration, cancellationToken);
    }

    private async Task<int> ApplyAsync(string? targetMigration, CancellationToken cancellationToken)
    {
        DbContextOptions<TContext> options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", historySchema))
            .Options;

        await using TContext context = factory(options);
        int pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        if (pending == 0)
        {
            return 0;
        }

        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration, cancellationToken);
        return pending;
    }
}
