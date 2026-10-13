using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SakerWatch.Identity.Infrastructure.Persistence;

/// <summary>Design-time factory for <c>dotnet ef migrations add</c>; it never connects to a database.</summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    /// <inheritdoc />
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(
                "Host=design-time;Database=design-time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.Schema))
            .Options;
        return new IdentityDbContext(options);
    }
}
