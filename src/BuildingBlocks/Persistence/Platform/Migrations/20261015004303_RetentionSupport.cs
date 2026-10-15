using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.BuildingBlocks.Persistence.Platform.Migrations;

/// <summary>
/// Support for the Retention module (data-model.md section 5). The worker has no table access to <c>platform.idempotency_keys</c>
/// (row-level security keeps it per user), so a dry run counts expired keys through a security-definer function that mirrors
/// <c>purge_expired_idempotency_keys</c>. Partial indexes let each purge find its candidates in order without scanning the whole
/// table; they only cover rows that are already finished, so they stay small. The tables are small at this stage, so a plain
/// (locking) CREATE INDEX inside the migration is acceptable; a large table would need CONCURRENTLY outside a transaction.
/// </summary>
/// <inheritdoc />
public partial class RetentionSupport : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE FUNCTION platform.count_expired_idempotency_keys(p_before timestamptz)
            RETURNS bigint
            LANGUAGE sql
            STABLE
            SECURITY DEFINER
            SET search_path = pg_catalog, platform
            AS $$
                SELECT count(*) FROM platform.idempotency_keys WHERE expires_at < p_before;
            $$;

            REVOKE ALL ON FUNCTION platform.count_expired_idempotency_keys(timestamptz) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION platform.count_expired_idempotency_keys(timestamptz) TO worker_role;

            CREATE INDEX ix_outbox_messages_processed_at ON platform.outbox_messages (processed_at) WHERE processed_at IS NOT NULL;
            CREATE INDEX ix_inbox_messages_processed_at ON platform.inbox_messages (processed_at);
            CREATE INDEX ix_job_queue_finished ON platform.job_queue (state, finished_at) WHERE finished_at IS NOT NULL;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
