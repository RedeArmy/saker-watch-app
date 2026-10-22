using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.MarketData.Infrastructure.Persistence.Migrations;

/// <summary>
/// Format checks on the text of the shared catalog that the API can write through <c>register_stock</c>: the provider's name, market code and symbol of
/// a stock (<c>symbol_aliases</c>) and its time zone (<c>stocks.time_zone</c>). They had only a length, so a compromised API login could have planted a
/// row whose symbol or time zone is hostile or unparseable, and the worker's calendar would have faulted on it. The patterns are the narrowest that
/// still hold what providers send (a symbol such as <c>BRK.B</c> or <c>BF-B</c>, a zone such as <c>America/Argentina/Buenos_Aires</c>). The checks are
/// plain and not <c>NOT VALID</c> because the tables are small catalogs and a row that does not fit must stop the deploy, not stay unchecked.
/// </summary>
/// <inheritdoc />
public partial class AddCatalogTextChecks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddCheckConstraint(
            name: "ck_symbol_aliases_provider",
            schema: "marketdata",
            table: "symbol_aliases",
            sql: "provider ~ '^[a-z0-9_]{1,32}$'");

        migrationBuilder.AddCheckConstraint(
            name: "ck_symbol_aliases_provider_mic",
            schema: "marketdata",
            table: "symbol_aliases",
            sql: "provider_mic ~ '^[A-Za-z0-9]{1,16}$'");

        migrationBuilder.AddCheckConstraint(
            name: "ck_symbol_aliases_provider_symbol",
            schema: "marketdata",
            table: "symbol_aliases",
            sql: "provider_symbol ~ '^[A-Za-z0-9][A-Za-z0-9._/-]{0,31}$'");

        migrationBuilder.AddCheckConstraint(
            name: "ck_stocks_time_zone",
            schema: "marketdata",
            table: "stocks",
            sql: "time_zone ~ '^[A-Za-z0-9_+-]+(/[A-Za-z0-9_+-]+){0,2}$'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
