using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SakerWatch.MarketData.Infrastructure.Persistence;

/// <summary>Design-time factory for <c>dotnet ef migrations add</c>; it never connects to a database.</summary>
public sealed class MarketDataDbContextFactory : IDesignTimeDbContextFactory<MarketDataDbContext>
{
    /// <inheritdoc />
    public MarketDataDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MarketDataDbContext>()
            .UseNpgsql(
                "Host=design-time;Database=design-time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", MarketDataDbContext.Schema))
            .Options;
        return new MarketDataDbContext(options);
    }
}
