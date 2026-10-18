using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.Watchlist.Infrastructure.Persistence.Migrations;

/// <summary>
/// Puts the temporary schema last in the <c>search_path</c> of the watchlist functions, as every other function of the project does: a schema
/// searched before the catalog or the module's own lets a caller who can create objects there shadow what a privileged function uses.
/// <c>WatchlistSecurity</c> pinned the path without <c>pg_temp</c>; this fixes it forward.
/// </summary>
/// <inheritdoc />
public partial class PinWatchlistSearchPaths : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            ALTER FUNCTION watchlist.tracked_stock_ids() SET search_path = pg_catalog, watchlist, pg_temp;
            ALTER FUNCTION watchlist.enforce_item_limit() SET search_path = pg_catalog, watchlist, pg_temp;
            ALTER FUNCTION watchlist.enforce_portfolio_limit() SET search_path = pg_catalog, watchlist, pg_temp;
            ALTER FUNCTION watchlist.enforce_portfolio_item_limit() SET search_path = pg_catalog, watchlist, pg_temp;
            """);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
