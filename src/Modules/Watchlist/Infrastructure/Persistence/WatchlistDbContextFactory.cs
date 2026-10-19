using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SakerWatch.Watchlist.Infrastructure.Persistence;

/// <summary>Design-time factory for <c>dotnet ef migrations add</c>; it never connects to a database.</summary>
public sealed class WatchlistDbContextFactory : IDesignTimeDbContextFactory<WatchlistDbContext>
{
    /// <inheritdoc />
    public WatchlistDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WatchlistDbContext>()
            .UseNpgsql(
                "Host=design-time;Database=design-time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", WatchlistDbContext.Schema))
            .Options;
        return new WatchlistDbContext(options);
    }
}
