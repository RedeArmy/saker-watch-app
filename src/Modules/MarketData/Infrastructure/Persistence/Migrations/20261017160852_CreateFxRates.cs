using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.MarketData.Infrastructure.Persistence.Migrations;

/// <summary>
/// Daily exchange rates: <c>marketdata.fx_rates</c> holds one row per currency pair, day and source with the rate (units of the quote currency for
/// one of the base). Checks keep two different three-letter currencies, a positive rate and a well-formed source (ADR-0028).
/// </summary>
/// <inheritdoc />
public partial class CreateFxRates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "fx_rates",
            schema: "marketdata",
            columns: table => new
            {
                @base = table.Column<string>(name: "base", type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                quote = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                as_of_date = table.Column<DateOnly>(type: "date", nullable: false),
                source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                rate = table.Column<decimal>(type: "numeric(20,10)", precision: 20, scale: 10, nullable: false),
                ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_fx_rates", x => new { x.@base, x.quote, x.as_of_date, x.source });
                table.CheckConstraint("ck_fx_rates_currencies", "base ~ '^[A-Z]{3}$' AND quote ~ '^[A-Z]{3}$' AND base <> quote");
                table.CheckConstraint("ck_fx_rates_rate", "rate > 0");
                table.CheckConstraint("ck_fx_rates_source", "source ~ '^[a-z0-9_]{1,32}$'");
            });

        // Shared, not user-owned, so no row-level security: the API reads, the worker writes, nobody deletes. The primary key leads with the pair
        // and the day, which is the order of the "latest rate on or before a day" lookup.
        migrationBuilder.Sql("""
            GRANT SELECT ON marketdata.fx_rates TO api_role;
            GRANT SELECT, INSERT, UPDATE ON marketdata.fx_rates TO worker_role;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
