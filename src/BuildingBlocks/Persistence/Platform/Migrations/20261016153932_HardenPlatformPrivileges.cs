using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.BuildingBlocks.Persistence.Platform.Migrations;

/// <summary>
/// Closes three gaps that the Phase 1 security review left as hardening. Every security-definer function of the <c>platform</c> schema gets
/// <c>pg_temp</c> last in its <c>search_path</c> (without it PostgreSQL searches the temporary schema first for relations, so a temporary
/// table could shadow a platform one inside code that runs with the owner's rights). Functions created from now on by the migrating role are
/// not executable by <c>PUBLIC</c> unless a migration grants them explicitly, so a forgotten <c>REVOKE</c> cannot expose a new function.
/// And <c>PUBLIC</c> loses <c>TEMPORARY</c> on the database: no application login needs temporary tables.
/// </summary>
/// <inheritdoc />
public partial class HardenPlatformPrivileges : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            DO $$
            DECLARE
                r record;
            BEGIN
                FOR r IN
                    SELECT p.oid::regprocedure AS signature, substring(c FROM 'search_path=(.*)') AS path
                    FROM pg_proc p
                    JOIN pg_namespace n ON n.oid = p.pronamespace
                    CROSS JOIN LATERAL unnest(p.proconfig) AS c
                    WHERE n.nspname = 'platform' AND p.prosecdef AND c LIKE 'search_path=%' AND c NOT LIKE '%pg_temp%'
                LOOP
                    EXECUTE format('ALTER FUNCTION %s SET search_path = %s, pg_temp', r.signature, r.path);
                END LOOP;

                EXECUTE format('REVOKE TEMPORARY ON DATABASE %I FROM PUBLIC', current_database());
            END
            $$;

            ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
            """);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
