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

    /// <summary>Idempotency keys (user-owned, row-level security).</summary>
    public DbSet<IdempotencyKeyRow> IdempotencyKeys => Set<IdempotencyKeyRow>();

    /// <summary>Outbox messages.</summary>
    public DbSet<OutboxMessageRow> OutboxMessages => Set<OutboxMessageRow>();

    /// <summary>Inbox messages.</summary>
    public DbSet<InboxMessageRow> InboxMessages => Set<InboxMessageRow>();

    /// <summary>Job queue.</summary>
    public DbSet<JobRow> Jobs => Set<JobRow>();

    /// <summary>Audit log (read-only to the application; written through <c>platform.append_audit</c>).</summary>
    public DbSet<AuditLogRow> AuditLog => Set<AuditLogRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<IdempotencyKeyRow>(e =>
        {
            e.ToTable("idempotency_keys", t => t.HasCheckConstraint("ck_idempotency_keys_state", "state IN ('in_progress', 'completed')"));
            e.HasKey(x => new { x.UserId, x.Method, x.Route, x.Key });
            e.Property(x => x.ResponseHeaders).HasColumnType("jsonb");
            e.Property(x => x.RequestHash).IsRequired();
            e.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<OutboxMessageRow>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.NextAttemptAt).HasFilter("processed_at IS NULL");
        });

        modelBuilder.Entity<InboxMessageRow>(e =>
        {
            e.ToTable("inbox_messages");
            e.HasKey(x => new { x.Consumer, x.MessageId });
        });

        modelBuilder.Entity<JobRow>(e =>
        {
            e.ToTable("job_queue", t => t.HasCheckConstraint("ck_job_queue_state", "state IN ('pending', 'leased', 'succeeded', 'failed', 'dead')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.JobKey).IsUnique();
            e.HasIndex(x => x.AvailableAt).HasFilter("state = 'pending'");
            e.HasIndex(x => x.LeaseExpiresAt).HasFilter("state = 'leased'");
        });

        modelBuilder.Entity<AuditLogRow>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Details).HasColumnType("jsonb");
            e.HasIndex(x => x.OccurredAt);
        });

        modelBuilder.UseSnakeCaseNames();
    }
}
