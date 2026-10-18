using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.Watchlist.Infrastructure.Persistence.Migrations;

/// <summary>
/// Security of the watchlist schema (ADR-0010, ADR-0031): every table is visible and writable only by its owner through row-level security,
/// the API role gets the four data privileges and the worker none, portfolio names are unique per user ignoring letter case, the database
/// ceilings on what a user may keep are enforced by triggers that count under a lock (parallel requests cannot slip past them), and the
/// worker learns which stocks are tracked only through one security-definer function that returns no user.
/// </summary>
/// <inheritdoc />
public partial class WatchlistSecurity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE UNIQUE INDEX ux_portfolios_user_id_lower_name ON watchlist.portfolios (user_id, lower(name));

            ALTER TABLE watchlist.watchlist_items ENABLE ROW LEVEL SECURITY;
            ALTER TABLE watchlist.portfolios ENABLE ROW LEVEL SECURITY;
            ALTER TABLE watchlist.portfolio_items ENABLE ROW LEVEL SECURITY;

            CREATE POLICY watchlist_items_owner ON watchlist.watchlist_items
                FOR ALL TO api_role
                USING (user_id = NULLIF(current_setting('app.current_user_id', true), '')::uuid)
                WITH CHECK (user_id = NULLIF(current_setting('app.current_user_id', true), '')::uuid);
            CREATE POLICY portfolios_owner ON watchlist.portfolios
                FOR ALL TO api_role
                USING (user_id = NULLIF(current_setting('app.current_user_id', true), '')::uuid)
                WITH CHECK (user_id = NULLIF(current_setting('app.current_user_id', true), '')::uuid);
            CREATE POLICY portfolio_items_owner ON watchlist.portfolio_items
                FOR ALL TO api_role
                USING (user_id = NULLIF(current_setting('app.current_user_id', true), '')::uuid)
                WITH CHECK (user_id = NULLIF(current_setting('app.current_user_id', true), '')::uuid);

            REVOKE ALL ON ALL TABLES IN SCHEMA watchlist FROM PUBLIC, api_role, worker_role;
            GRANT USAGE ON SCHEMA watchlist TO api_role, worker_role;
            GRANT SELECT, INSERT, UPDATE, DELETE ON watchlist.watchlist_items, watchlist.portfolios, watchlist.portfolio_items TO api_role;
            """);

        // The ceilings repeat WatchlistLimits.Ceilings. Each trigger takes a transaction-level advisory lock for the owner (or the portfolio) before
        // it counts, so two parallel inserts are counted one after the other. The trigger runs as the caller, so the count sees only the caller's
        // rows, which is exactly the scope of the limit. The constraint name is what the application maps to a friendly error.
        migrationBuilder.Sql("""
            CREATE FUNCTION watchlist.enforce_item_limit() RETURNS trigger
            LANGUAGE plpgsql
            SET search_path = pg_catalog, watchlist
            AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(hashtextextended('watchlist.items:' || NEW.user_id::text, 0));
                IF (SELECT count(*) FROM watchlist.watchlist_items WHERE user_id = NEW.user_id) >= 500 THEN
                    RAISE EXCEPTION 'The watchlist already holds 500 stocks.' USING ERRCODE = '23514', CONSTRAINT = 'ck_watchlist_items_max_per_user';
                END IF;
                RETURN NEW;
            END
            $$;

            CREATE FUNCTION watchlist.enforce_portfolio_limit() RETURNS trigger
            LANGUAGE plpgsql
            SET search_path = pg_catalog, watchlist
            AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(hashtextextended('watchlist.portfolios:' || NEW.user_id::text, 0));
                IF (SELECT count(*) FROM watchlist.portfolios WHERE user_id = NEW.user_id) >= 20 THEN
                    RAISE EXCEPTION 'A user keeps at most 20 portfolios.' USING ERRCODE = '23514', CONSTRAINT = 'ck_portfolios_max_per_user';
                END IF;
                RETURN NEW;
            END
            $$;

            CREATE FUNCTION watchlist.enforce_portfolio_item_limit() RETURNS trigger
            LANGUAGE plpgsql
            SET search_path = pg_catalog, watchlist
            AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock(hashtextextended('watchlist.portfolio_items:' || NEW.portfolio_id::text, 0));
                IF (SELECT count(*) FROM watchlist.portfolio_items WHERE portfolio_id = NEW.portfolio_id) >= 100 THEN
                    RAISE EXCEPTION 'A portfolio holds at most 100 stocks.' USING ERRCODE = '23514', CONSTRAINT = 'ck_portfolio_items_max_per_portfolio';
                END IF;
                RETURN NEW;
            END
            $$;

            REVOKE ALL ON FUNCTION watchlist.enforce_item_limit(), watchlist.enforce_portfolio_limit(), watchlist.enforce_portfolio_item_limit() FROM PUBLIC;

            CREATE TRIGGER watchlist_items_limit BEFORE INSERT ON watchlist.watchlist_items
                FOR EACH ROW EXECUTE FUNCTION watchlist.enforce_item_limit();
            CREATE TRIGGER portfolios_limit BEFORE INSERT ON watchlist.portfolios
                FOR EACH ROW EXECUTE FUNCTION watchlist.enforce_portfolio_limit();
            CREATE TRIGGER portfolio_items_limit BEFORE INSERT ON watchlist.portfolio_items
                FOR EACH ROW EXECUTE FUNCTION watchlist.enforce_portfolio_item_limit();
            """);

        // The only cross-user read the worker has (ADR-0010): which stocks anyone tracks, so ingestion and the analysis scheduler know what to
        // keep fresh. It returns no user id and no note. It runs as the owner of the tables, which is not subject to the policies.
        migrationBuilder.Sql("""
            CREATE FUNCTION watchlist.tracked_stock_ids() RETURNS SETOF uuid
            LANGUAGE sql
            STABLE
            SECURITY DEFINER
            SET search_path = pg_catalog, watchlist
            AS $$ SELECT DISTINCT stock_id FROM watchlist.watchlist_items $$;

            REVOKE ALL ON FUNCTION watchlist.tracked_stock_ids() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION watchlist.tracked_stock_ids() TO worker_role;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
