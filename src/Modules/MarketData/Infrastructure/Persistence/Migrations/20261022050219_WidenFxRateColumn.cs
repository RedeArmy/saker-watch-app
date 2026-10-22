using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.MarketData.Infrastructure.Persistence.Migrations;

/// <summary>
/// <c>marketdata.fx_rates.rate</c> goes from <c>numeric(20,10)</c> to <c>numeric(30,12)</c>, the width of <c>fx_rates_rejected</c> and of the whole
/// range of rates the domain accepts (1e-12 to 1e12). With the old width a rate of 1e10 or more overflowed the column (22003) and one below 5e-11
/// rounded to zero and broke the check, failing the whole batch of a refresh; no rate of a real pair gets near either end, but the column must not
/// be narrower than what the domain lets in. It is a compatible change (every value that fitted still fits) on a table of a few thousand rows, so the
/// rewrite is instant; the previous version of the application keeps working on the new column.
/// </summary>
/// <inheritdoc />
public partial class WidenFxRateColumn : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AlterColumn<decimal>(
            name: "rate",
            schema: "marketdata",
            table: "fx_rates",
            type: "numeric(30,12)",
            precision: 30,
            scale: 12,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(20,10)",
            oldPrecision: 20,
            oldScale: 10);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
