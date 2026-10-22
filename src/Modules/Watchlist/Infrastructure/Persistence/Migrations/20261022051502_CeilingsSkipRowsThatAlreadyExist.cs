using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.Watchlist.Infrastructure.Persistence.Migrations;

/// <summary>
/// The three ceiling triggers (500 stocks, 20 portfolios, 100 stocks per portfolio) run before <c>ON CONFLICT DO NOTHING</c> is resolved, so a user at
/// a ceiling who added again something they already had got the ceiling error instead of the quiet "already there" the statement means (a retry, or a
/// parallel duplicate, at the limit). The functions now let a row that already exists through (the insert then does nothing, as written), after taking the
/// same advisory lock, and count only for a row that would be new. They are still not security definer: they run as the caller, so the existence check
/// sees only the caller's rows, which is the scope of every limit. <c>CREATE OR REPLACE</c> keeps the grants (no <c>EXECUTE</c> for <c>PUBLIC</c>) and the
/// search path stays pinned with <c>pg_temp</c> last.
/// </summary>
/// <inheritdoc />
public partial class CeilingsSkipRowsThatAlreadyExist : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION watchlist.enforce_item_limit() RETURNS trigger
            LANGUAGE plpgsql
            SET search_path = pg_catalog, watchlist, pg_temp
            AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(hashtextextended('watchlist.items:' || NEW.user_id::text, 0));
                IF EXISTS (SELECT 1 FROM watchlist.watchlist_items WHERE user_id = NEW.user_id AND stock_id = NEW.stock_id) THEN
                    RETURN NEW;
                END IF;
                IF (SELECT count(*) FROM watchlist.watchlist_items WHERE user_id = NEW.user_id) >= 500 THEN
                    RAISE EXCEPTION 'The watchlist already holds 500 stocks.' USING ERRCODE = '23514', CONSTRAINT = 'ck_watchlist_items_max_per_user';
                END IF;
                RETURN NEW;
            END
            $$;

            CREATE OR REPLACE FUNCTION watchlist.enforce_portfolio_limit() RETURNS trigger
            LANGUAGE plpgsql
            SET search_path = pg_catalog, watchlist, pg_temp
            AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(hashtextextended('watchlist.portfolios:' || NEW.user_id::text, 0));
                IF EXISTS (SELECT 1 FROM watchlist.portfolios WHERE user_id = NEW.user_id AND lower(name) = lower(NEW.name)) THEN
                    RETURN NEW;
                END IF;
                IF (SELECT count(*) FROM watchlist.portfolios WHERE user_id = NEW.user_id) >= 20 THEN
                    RAISE EXCEPTION 'A user keeps at most 20 portfolios.' USING ERRCODE = '23514', CONSTRAINT = 'ck_portfolios_max_per_user';
                END IF;
                RETURN NEW;
            END
            $$;

            CREATE OR REPLACE FUNCTION watchlist.enforce_portfolio_item_limit() RETURNS trigger
            LANGUAGE plpgsql
            SET search_path = pg_catalog, watchlist, pg_temp
            AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(hashtextextended('watchlist.portfolio_items:' || NEW.portfolio_id::text, 0));
                IF EXISTS (SELECT 1 FROM watchlist.portfolio_items WHERE portfolio_id = NEW.portfolio_id AND watchlist_item_id = NEW.watchlist_item_id) THEN
                    RETURN NEW;
                END IF;
                IF (SELECT count(*) FROM watchlist.portfolio_items WHERE portfolio_id = NEW.portfolio_id) >= 100 THEN
                    RAISE EXCEPTION 'A portfolio holds at most 100 stocks.' USING ERRCODE = '23514', CONSTRAINT = 'ck_portfolio_items_max_per_portfolio';
                END IF;
                RETURN NEW;
            END
            $$;
            """);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
