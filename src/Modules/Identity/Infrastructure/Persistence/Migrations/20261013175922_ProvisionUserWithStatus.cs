using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.Identity.Infrastructure.Persistence.Migrations;

/// <summary>
/// Just-in-time provisioning needs more than an id: the caller must know whether the account is usable and whether it was
/// created by this call (to audit it). The original <c>identity.provision_user</c> returned only the id, so it is replaced
/// by a function returning <c>(user_id, user_status, was_created)</c>. Profile data (email, display name) is refreshed only
/// for active accounts, so a pending-deletion account cannot be silently edited by a login.
/// </summary>
/// <inheritdoc />
public partial class ProvisionUserWithStatus : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP FUNCTION identity.provision_user(uuid, text, text, text, text, text, timestamptz);

            CREATE FUNCTION identity.provision_user(
                p_new_id uuid, p_issuer text, p_subject text, p_email text, p_display_name text, p_locale text, p_now timestamptz)
            RETURNS TABLE (user_id uuid, user_status text, was_created boolean)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, identity
            AS $$
            BEGIN
                RETURN QUERY
                INSERT INTO identity.users AS u
                    (id, external_issuer, external_subject, email, display_name, locale, status, created_at, updated_at)
                VALUES
                    (p_new_id, p_issuer, p_subject, p_email, p_display_name, coalesce(p_locale, 'es-GT'), 'active', p_now, p_now)
                ON CONFLICT (external_issuer, external_subject) DO UPDATE
                    SET email = CASE WHEN u.status = 'active' THEN coalesce(EXCLUDED.email, u.email) ELSE u.email END,
                        display_name = CASE WHEN u.status = 'active' THEN coalesce(EXCLUDED.display_name, u.display_name) ELSE u.display_name END,
                        updated_at = CASE WHEN u.status = 'active' THEN EXCLUDED.updated_at ELSE u.updated_at END
                RETURNING u.id, u.status, (u.xmax = 0);
            END
            $$;

            REVOKE ALL ON FUNCTION identity.provision_user(uuid, text, text, text, text, text, timestamptz) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION identity.provision_user(uuid, text, text, text, text, text, timestamptz) TO api_role;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
