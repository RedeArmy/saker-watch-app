using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.Identity.Infrastructure.Persistence.Migrations;

/// <summary>
/// <c>identity.provision_user</c> ran on every cache miss (each active user, every few minutes, per API instance) and always
/// executed an <c>UPDATE</c> through <c>ON CONFLICT DO UPDATE</c>, which rewrites the row, takes its lock and writes WAL even when
/// nothing changed. The function now inserts, and for an existing active account updates only when the profile data really
/// differs; an unchanged login is a plain read. <c>updated_at</c> therefore means "profile changed", not "last seen". The signature,
/// result, owner, grants and pinned search path are unchanged (<c>CREATE OR REPLACE</c>).
/// </summary>
/// <inheritdoc />
public partial class ProvisionUserSkipsNoopUpdates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION identity.provision_user(
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
                ON CONFLICT (external_issuer, external_subject) DO NOTHING
                RETURNING u.id, u.status, true;
                IF FOUND THEN
                    RETURN;
                END IF;

                -- Known account. Profile data is refreshed only for active accounts and only when it differs.
                UPDATE identity.users AS u
                SET email = coalesce(p_email, u.email),
                    display_name = coalesce(p_display_name, u.display_name),
                    updated_at = p_now
                WHERE u.external_issuer = p_issuer AND u.external_subject = p_subject AND u.status = 'active'
                  AND (u.email IS DISTINCT FROM coalesce(p_email, u.email)
                       OR u.display_name IS DISTINCT FROM coalesce(p_display_name, u.display_name));

                RETURN QUERY
                SELECT u.id, u.status, false
                FROM identity.users AS u
                WHERE u.external_issuer = p_issuer AND u.external_subject = p_subject;
            END
            $$;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
