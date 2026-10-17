using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.MarketData.Infrastructure.Persistence.Migrations;

/// <summary>
/// Where ingestion stands for each listing: <c>marketdata.ingestion_state</c> holds its status (pending, ok or failed), the last attempt and success,
/// and the stable code of a permanent failure, so a listing that cannot be ingested is not retried blindly and the reason can be read.
/// </summary>
/// <inheritdoc />
public partial class CreateIngestionState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ingestion_state",
            schema: "marketdata",
            columns: table => new
            {
                stock_id = table.Column<Guid>(type: "uuid", nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_ingestion_state", x => x.stock_id);
                table.CheckConstraint("ck_ingestion_state_failure", "(status = 'failed') = (failure_code IS NOT NULL)");
                table.CheckConstraint("ck_ingestion_state_status", "status IN ('pending', 'ok', 'failed')");
                table.ForeignKey(
                    name: "FK_ingestion_state_stocks_stock_id",
                    column: x => x.stock_id,
                    principalSchema: "marketdata",
                    principalTable: "stocks",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        // Shared, not user-owned, so no row-level security: the API reads, the worker writes, nobody deletes.
        migrationBuilder.Sql("""
            GRANT SELECT ON marketdata.ingestion_state TO api_role;
            GRANT SELECT, INSERT, UPDATE ON marketdata.ingestion_state TO worker_role;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
