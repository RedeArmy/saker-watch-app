using Microsoft.EntityFrameworkCore;
using SakerWatch.BuildingBlocks.Persistence.Conventions;

namespace SakerWatch.BuildingBlocks.Persistence.Platform;

/// <summary>
/// Owns the <c>platform</c> schema: cross-cutting tables used by every module (idempotency keys, outbox, inbox,
/// job queue, audit log). Its migrations also create the database roles and enable TimescaleDB.
/// </summary>
/// <param name="options">Context options.</param>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    /// <summary>Schema that holds the platform tables and the migrations history.</summary>
    public const string Schema = "platform";

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.UseSnakeCaseNames();
    }
}
