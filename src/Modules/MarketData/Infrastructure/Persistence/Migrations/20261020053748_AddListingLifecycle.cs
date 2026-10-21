using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.MarketData.Infrastructure.Persistence.Migrations;

/// <summary>
/// The life of a listing (ADR-0039): <c>stocks.delisted_at</c> for a stock that stopped trading (a delisted stock is also inactive),
/// <c>ticker_history</c> for the tickers a stock used to have (a lookup by a retired ticker finds the stock, so watchlists survive a symbol
/// change), and a count of the days in a row the provider did not know the symbol, which delists a stock when it reaches the configured limit.
/// </summary>
/// <inheritdoc />
public partial class AddListingLifecycle : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "delisted_at",
            schema: "marketdata",
            table: "stocks",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateOnly>(
            name: "last_not_found_on",
            schema: "marketdata",
            table: "ingestion_state",
            type: "date",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "not_found_days",
            schema: "marketdata",
            table: "ingestion_state",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "ticker_history",
            schema: "marketdata",
            columns: table => new
            {
                market_mic = table.Column<string>(type: "character(4)", fixedLength: true, maxLength: 4, nullable: false),
                ticker = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                stock_id = table.Column<Guid>(type: "uuid", nullable: false),
                changed_on = table.Column<DateOnly>(type: "date", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_ticker_history", x => new { x.market_mic, x.ticker });
                table.CheckConstraint("ck_ticker_history_ticker", "ticker ~ '^[A-Z0-9][A-Z0-9._-]{0,15}$'");
                table.ForeignKey(
                    name: "FK_ticker_history_stocks_stock_id",
                    column: x => x.stock_id,
                    principalSchema: "marketdata",
                    principalTable: "stocks",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.AddCheckConstraint(
            name: "ck_stocks_delisted",
            schema: "marketdata",
            table: "stocks",
            sql: "delisted_at IS NULL OR NOT is_active");

        migrationBuilder.AddCheckConstraint(
            name: "ck_ingestion_state_not_found_days",
            schema: "marketdata",
            table: "ingestion_state",
            sql: "not_found_days >= 0");

        migrationBuilder.CreateIndex(
            name: "ix_ticker_history_stock_id",
            schema: "marketdata",
            table: "ticker_history",
            column: "stock_id");

        // Shared, not user-owned, so no row-level security: the API reads the history to resolve a retired ticker, the worker writes it (a ticker that is in use
        // again leaves the history). The new columns of stocks and ingestion_state are covered by the privileges those tables already have.
        migrationBuilder.Sql("""
            GRANT SELECT ON marketdata.ticker_history TO api_role;
            GRANT SELECT, INSERT, UPDATE, DELETE ON marketdata.ticker_history TO worker_role;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
