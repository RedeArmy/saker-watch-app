using Microsoft.EntityFrameworkCore;
using SakerWatch.BuildingBlocks.Persistence.Conventions;

namespace SakerWatch.Identity.Infrastructure.Persistence;

/// <summary>Owns the <c>identity</c> schema. Users are user-owned data protected by row-level security (ADR-0010).</summary>
/// <param name="options">Context options.</param>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    /// <summary>Schema that holds the identity tables and the migrations history.</summary>
    public const string Schema = "identity";

    /// <summary>Users.</summary>
    public DbSet<UserRow> Users => Set<UserRow>();

    /// <summary>Deletion tombstones.</summary>
    public DbSet<DeletionTombstoneRow> DeletionTombstones => Set<DeletionTombstoneRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<UserRow>(e =>
        {
            e.ToTable("users", t => t.HasCheckConstraint("ck_users_status", "status IN ('active', 'pending_deletion', 'deleted')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalIssuer).IsRequired();
            e.Property(x => x.ExternalSubject).IsRequired();
            e.Property(x => x.Locale).HasDefaultValue("es-GT");
            e.HasIndex(x => new { x.ExternalIssuer, x.ExternalSubject }).IsUnique();
        });

        modelBuilder.Entity<DeletionTombstoneRow>(e =>
        {
            e.ToTable("deletion_tombstones");
            e.HasKey(x => x.UserId);
        });

        modelBuilder.UseSnakeCaseNames();
    }
}
