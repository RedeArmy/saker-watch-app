using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.Watchlist.Infrastructure.Persistence.Migrations;

/// <summary>
/// The user's stocks and portfolios (ADR-0026, ADR-0031): <c>watchlist.watchlist_items</c> (unique per user and stock), <c>watchlist.portfolios</c>
/// and <c>watchlist.portfolio_items</c>. The membership table carries the user in both foreign keys, which target <c>(id, user_id)</c> of the
/// parent, so a portfolio can never hold another user's item. There is no foreign key to <c>marketdata.stocks</c>: the schemas stay independent.
/// </summary>
/// <inheritdoc />
public partial class CreateWatchlist : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "watchlist");

        migrationBuilder.CreateTable(
            name: "portfolios",
            schema: "watchlist",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                position = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_portfolios", x => x.id);
                table.UniqueConstraint("uq_portfolios_id_user_id", x => new { x.id, x.user_id });
                table.CheckConstraint("ck_portfolios_description", "description IS NULL OR char_length(description) BETWEEN 1 AND 500");
                table.CheckConstraint("ck_portfolios_name", "name = btrim(name) AND char_length(name) BETWEEN 1 AND 60");
                table.CheckConstraint("ck_portfolios_position", "position >= 0");
            });

        migrationBuilder.CreateTable(
            name: "watchlist_items",
            schema: "watchlist",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                stock_id = table.Column<Guid>(type: "uuid", nullable: false),
                notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                target_price = table.Column<decimal>(type: "numeric(20,8)", precision: 20, scale: 8, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_watchlist_items", x => x.id);
                table.UniqueConstraint("uq_watchlist_items_id_user_id", x => new { x.id, x.user_id });
                table.CheckConstraint("ck_watchlist_items_notes", "notes IS NULL OR char_length(notes) BETWEEN 1 AND 2000");
                table.CheckConstraint("ck_watchlist_items_target_price", "target_price IS NULL OR target_price > 0");
            });

        migrationBuilder.CreateTable(
            name: "portfolio_items",
            schema: "watchlist",
            columns: table => new
            {
                portfolio_id = table.Column<Guid>(type: "uuid", nullable: false),
                watchlist_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                position = table.Column<int>(type: "integer", nullable: false),
                added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_portfolio_items", x => new { x.portfolio_id, x.watchlist_item_id });
                table.CheckConstraint("ck_portfolio_items_position", "position >= 0");
                table.ForeignKey(
                    name: "FK_portfolio_items_portfolios_portfolio_id_user_id",
                    columns: x => new { x.portfolio_id, x.user_id },
                    principalSchema: "watchlist",
                    principalTable: "portfolios",
                    principalColumns: new[] { "id", "user_id" },
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_portfolio_items_watchlist_items_watchlist_item_id_user_id",
                    columns: x => new { x.watchlist_item_id, x.user_id },
                    principalSchema: "watchlist",
                    principalTable: "watchlist_items",
                    principalColumns: new[] { "id", "user_id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_portfolio_items_portfolio_id_user_id",
            schema: "watchlist",
            table: "portfolio_items",
            columns: new[] { "portfolio_id", "user_id" });

        migrationBuilder.CreateIndex(
            name: "ix_portfolio_items_watchlist_item_id",
            schema: "watchlist",
            table: "portfolio_items",
            column: "watchlist_item_id");

        migrationBuilder.CreateIndex(
            name: "ix_portfolio_items_watchlist_item_id_user_id",
            schema: "watchlist",
            table: "portfolio_items",
            columns: new[] { "watchlist_item_id", "user_id" });

        migrationBuilder.CreateIndex(
            name: "ix_portfolios_user_id_position",
            schema: "watchlist",
            table: "portfolios",
            columns: new[] { "user_id", "position" });

        migrationBuilder.CreateIndex(
            name: "ix_watchlist_items_stock_id",
            schema: "watchlist",
            table: "watchlist_items",
            column: "stock_id");

        migrationBuilder.CreateIndex(
            name: "ux_watchlist_items_user_id_stock_id",
            schema: "watchlist",
            table: "watchlist_items",
            columns: new[] { "user_id", "stock_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
