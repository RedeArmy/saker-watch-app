using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.MarketData.Infrastructure.Persistence.Migrations;

/// <summary>
/// The market data catalog: <c>stocks</c> (one row per listing, unique on market and ticker), <c>symbol_aliases</c> (how a provider names a
/// listing) and <c>corporate_actions</c> (splits, dividends, symbol changes, delistings). Shared by every user, so no row-level security:
/// the API reads, the worker writes, nobody deletes (a delisted stock is marked inactive and its history kept until retention removes it).
/// </summary>
/// <inheritdoc />
public partial class CreateMarketData : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "marketdata");

        migrationBuilder.CreateTable(
            name: "stocks",
            schema: "marketdata",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                market_mic = table.Column<string>(type: "character(4)", fixedLength: true, maxLength: 4, nullable: false),
                ticker = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                asset_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                listed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_stocks", x => x.id);
                table.CheckConstraint("ck_stocks_asset_type", "asset_type IN ('equity', 'etf', 'other')");
                table.CheckConstraint("ck_stocks_currency", "currency ~ '^[A-Z]{3}$'");
                table.CheckConstraint("ck_stocks_market_mic", "market_mic ~ '^[A-Z0-9]{4}$'");
                table.CheckConstraint("ck_stocks_name", "char_length(name) BETWEEN 1 AND 200");
                table.CheckConstraint("ck_stocks_ticker", "ticker ~ '^[A-Z0-9][A-Z0-9._-]{0,15}$'");
            });

        migrationBuilder.CreateTable(
            name: "corporate_actions",
            schema: "marketdata",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                stock_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                split_ratio = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                cash_amount = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_corporate_actions", x => x.id);
                table.CheckConstraint("ck_corporate_actions_currency", "currency IS NULL OR currency ~ '^[A-Z]{3}$'");
                table.CheckConstraint("ck_corporate_actions_dividend", "kind <> 'dividend' OR (cash_amount IS NOT NULL AND cash_amount >= 0 AND currency IS NOT NULL)");
                table.CheckConstraint("ck_corporate_actions_kind", "kind IN ('split', 'dividend', 'symbol_change', 'delisting')");
                table.CheckConstraint("ck_corporate_actions_split", "kind <> 'split' OR (split_ratio IS NOT NULL AND split_ratio > 0)");
                table.ForeignKey(
                    name: "FK_corporate_actions_stocks_stock_id",
                    column: x => x.stock_id,
                    principalSchema: "marketdata",
                    principalTable: "stocks",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "symbol_aliases",
            schema: "marketdata",
            columns: table => new
            {
                provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                provider_mic = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                provider_symbol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                stock_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_symbol_aliases", x => new { x.provider, x.provider_mic, x.provider_symbol });
                table.ForeignKey(
                    name: "FK_symbol_aliases_stocks_stock_id",
                    column: x => x.stock_id,
                    principalSchema: "marketdata",
                    principalTable: "stocks",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ux_corporate_actions_stock_id_kind_effective_date",
            schema: "marketdata",
            table: "corporate_actions",
            columns: new[] { "stock_id", "kind", "effective_date" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_stocks_market_mic_ticker",
            schema: "marketdata",
            table: "stocks",
            columns: new[] { "market_mic", "ticker" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_symbol_aliases_stock_id",
            schema: "marketdata",
            table: "symbol_aliases",
            column: "stock_id");

        // The catalog is shared, not user-owned, so there is no row-level security. The API only reads it; the worker maintains it. Nothing
        // is granted to PUBLIC, and registering a stock on demand from a request will go through a security-definer function (Phase 2.6).
        migrationBuilder.Sql("""
            REVOKE ALL ON ALL TABLES IN SCHEMA marketdata FROM PUBLIC, api_role, worker_role;
            GRANT USAGE ON SCHEMA marketdata TO api_role, worker_role;
            GRANT SELECT ON marketdata.stocks, marketdata.symbol_aliases, marketdata.corporate_actions TO api_role;
            GRANT SELECT, INSERT, UPDATE ON marketdata.stocks, marketdata.symbol_aliases, marketdata.corporate_actions TO worker_role;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
