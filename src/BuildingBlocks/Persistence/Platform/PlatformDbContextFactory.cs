using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SakerWatch.BuildingBlocks.Persistence.Platform;

/// <summary>Design-time factory for <c>dotnet ef migrations add</c>; it never connects to a database.</summary>
public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    /// <inheritdoc />
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(
                "Host=design-time;Database=design-time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.Schema))
            .Options;
        return new PlatformDbContext(options);
    }
}
