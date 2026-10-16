using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.Identity.Infrastructure.Persistence.Migrations;

/// <summary>
/// Narrows what the API login can do to <c>identity.users</c> and closes a name-resolution gap in the module's security-definer code.
/// The API only ever updates the locale (and the update time) of its own row, so the table-wide <c>UPDATE</c> grant becomes a grant on those
/// two columns: a compromised API session can no longer rewrite the status, the identity-provider pair or the email directly (profile data
/// still arrives through <c>provision_user</c>). Every security-definer function of the schema gets <c>pg_temp</c> last in its
/// <c>search_path</c>; without it PostgreSQL searches the session's temporary schema first for relations, so a temporary table could shadow a real one.
/// </summary>
/// <inheritdoc />
public partial class HardenIdentityPrivileges : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            REVOKE UPDATE ON identity.users FROM api_role;
            GRANT UPDATE (locale, updated_at) ON identity.users TO api_role;

            DO $$
            DECLARE
                r record;
            BEGIN
                FOR r IN
                    SELECT p.oid::regprocedure AS signature, substring(c FROM 'search_path=(.*)') AS path
                    FROM pg_proc p
                    JOIN pg_namespace n ON n.oid = p.pronamespace
                    CROSS JOIN LATERAL unnest(p.proconfig) AS c
                    WHERE n.nspname = 'identity' AND p.prosecdef AND c LIKE 'search_path=%' AND c NOT LIKE '%pg_temp%'
                LOOP
                    EXECUTE format('ALTER FUNCTION %s SET search_path = %s, pg_temp', r.signature, r.path);
                END LOOP;
            END
            $$;
            """);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
