using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.MarketData.Infrastructure.Persistence.Migrations;

/// <summary>
/// Exchange rates the guard did not believe (ADR-0042): <c>marketdata.fx_rates_rejected</c> keeps, per currency pair, day and source, the rate as the
/// provider gave it and why it was kept apart (<c>out_of_band</c>, <c>jump</c> or <c>rewrite</c>). They are never served. The rate is wider than in
/// <c>fx_rates</c> because what is refused may be absurd and keeping it must not fail for its size.
/// </summary>
/// <inheritdoc />
public partial class CreateFxRatesRejected : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "fx_rates_rejected",
            schema: "marketdata",
            columns: table => new
            {
                @base = table.Column<string>(name: "base", type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                quote = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                as_of_date = table.Column<DateOnly>(type: "date", nullable: false),
                source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                rate = table.Column<decimal>(type: "numeric(30,12)", precision: 30, scale: 12, nullable: false),
                reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_fx_rates_rejected", x => new { x.@base, x.quote, x.as_of_date, x.source });
                table.CheckConstraint("ck_fx_rates_rejected_currencies", "base ~ '^[A-Z]{3}$' AND quote ~ '^[A-Z]{3}$' AND base <> quote");
                table.CheckConstraint("ck_fx_rates_rejected_reason", "reason IN ('out_of_band', 'jump', 'rewrite')");
                table.CheckConstraint("ck_fx_rates_rejected_source", "source ~ '^[a-z0-9_]{1,32}$'");
            });

        // Shared and holds no user data, so no row-level security. Only the worker, which refreshes the rates, touches it: the API has no grant, so
        // nothing served to a user can come from here, and nobody deletes (the operator reads it and decides).
        migrationBuilder.Sql("""
            GRANT SELECT, INSERT, UPDATE ON marketdata.fx_rates_rejected TO worker_role;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
