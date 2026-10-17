using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.MarketData.Infrastructure.Persistence.Migrations;

/// <summary>
/// Daily prices: <c>marketdata.price_daily</c>, one row per listing and trading day with the raw bar, the adjusted close, the source and whether the
/// day is final, as a Timescale hypertable. Checks mirror the domain's <c>DailyBar</c> (positive prices, a consistent range, a non-negative volume).
/// </summary>
/// <inheritdoc />
public partial class CreatePriceDaily : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "price_daily",
            schema: "marketdata",
            columns: table => new
            {
                stock_id = table.Column<Guid>(type: "uuid", nullable: false),
                trade_date = table.Column<DateOnly>(type: "date", nullable: false),
                open = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                high = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                low = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                close = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                adjusted_close = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: false),
                volume = table.Column<long>(type: "bigint", nullable: false),
                source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                is_final = table.Column<bool>(type: "boolean", nullable: false),
                ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_price_daily", x => new { x.stock_id, x.trade_date });
                table.CheckConstraint("ck_price_daily_prices", "open > 0 AND high > 0 AND low > 0 AND close > 0 AND adjusted_close > 0");
                table.CheckConstraint("ck_price_daily_range", "high >= greatest(open, close, low) AND low <= least(open, close, high)");
                table.CheckConstraint("ck_price_daily_source", "source ~ '^[a-z0-9_]{1,32}$'");
                table.CheckConstraint("ck_price_daily_volume", "volume >= 0");
                table.ForeignKey(
                    name: "FK_price_daily_stocks_stock_id",
                    column: x => x.stock_id,
                    principalSchema: "marketdata",
                    principalTable: "stocks",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        // A hypertable partitioned by trade date, one chunk a year (about 250 rows a year per listing, so a chunk stays small however many
        // listings there are). The primary key already includes the partition column, as Timescale requires of a unique index. It is created
        // while the table is empty. No compression: the adjusted close of old rows is rewritten whenever a dividend is paid (ADR-0029), and
        // a compressed chunk is costly to update; revisit with real volumes in Phase 5. The catalog is shared, so there is no row-level
        // security: the API only reads, the worker writes, and nobody deletes.
        migrationBuilder.Sql("""
            SELECT create_hypertable('marketdata.price_daily', 'trade_date', chunk_time_interval => INTERVAL '1 year', if_not_exists => TRUE);

            GRANT SELECT ON marketdata.price_daily TO api_role;
            GRANT SELECT, INSERT, UPDATE ON marketdata.price_daily TO worker_role;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
