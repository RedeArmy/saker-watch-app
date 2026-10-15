using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.BuildingBlocks.Persistence.Platform.Migrations;

/// <summary>
/// <c>platform.idempotency_keys.response_body</c> stores the first response of a retried command, which can hold profile data (email,
/// display name). Expired keys leave through retention, but account deletion (Phase 6) must not wait for that, so the worker gets a
/// security-definer function that removes every key of one user (row-level security hides the table from it). Nothing calls it yet.
/// </summary>
/// <inheritdoc />
public partial class PurgeUserIdempotencyKeys : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE FUNCTION platform.purge_user_idempotency_keys(p_user_id uuid)
            RETURNS integer
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, platform
            AS $$
            DECLARE
                v_count integer;
            BEGIN
                DELETE FROM platform.idempotency_keys WHERE user_id = p_user_id;
                GET DIAGNOSTICS v_count = ROW_COUNT;
                RETURN v_count;
            END
            $$;

            REVOKE ALL ON FUNCTION platform.purge_user_idempotency_keys(uuid) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION platform.purge_user_idempotency_keys(uuid) TO worker_role;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
