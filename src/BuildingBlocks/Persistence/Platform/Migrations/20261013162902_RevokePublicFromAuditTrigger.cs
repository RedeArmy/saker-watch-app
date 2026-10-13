using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.BuildingBlocks.Persistence.Platform.Migrations;

/// <summary>
/// Least privilege for the audit log's trigger function: functions are executable by PUBLIC by default, and the
/// <c>PlatformSecurity</c> migration revoked that for every function except this one. A trigger function is only ever run by
/// its trigger, so no role needs EXECUTE on it. The search path is pinned as for the other functions of the schema.
/// </summary>
/// <inheritdoc />
public partial class RevokePublicFromAuditTrigger : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            REVOKE ALL ON FUNCTION platform.audit_log_reject_change() FROM PUBLIC;
            ALTER FUNCTION platform.audit_log_reject_change() SET search_path = pg_catalog;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
