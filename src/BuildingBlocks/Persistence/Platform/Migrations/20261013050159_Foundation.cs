using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.BuildingBlocks.Persistence.Platform.Migrations;

/// <summary>
/// Cluster and database foundation: TimescaleDB, the two runtime roles and schema access. The roles are group roles
/// that cannot log in; each environment creates its own login (with a secret password) and grants it one of them,
/// which keeps credentials out of migrations (ADR-0010).
/// </summary>
/// <inheritdoc />
public partial class Foundation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS timescaledb;");

        // Idempotent so several databases on one cluster (tests, staging) can share the cluster-wide roles.
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'api_role') THEN
                    CREATE ROLE api_role NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'worker_role') THEN
                    CREATE ROLE worker_role NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
                END IF;
            END
            $$;
            """);

        migrationBuilder.Sql("GRANT USAGE ON SCHEMA platform TO api_role, worker_role;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
