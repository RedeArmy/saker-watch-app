using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.MarketData.Infrastructure.Persistence.Migrations;

/// <summary>
/// The API's one door to the shared catalog (ADR-0032): <c>marketdata.register_stock</c> inserts a stock, the provider's name for it and its
/// pending ingestion state in one call, and returns the id of whichever row holds <c>(market_mic, ticker)</c>. It never updates a stock that
/// already exists, so it cannot be used to rewrite a name, a currency or a time zone. It is a security-definer function with a fixed
/// <c>search_path</c>; the checks of the tables (market code, ticker, currency, name, asset type) still apply to everything it inserts.
/// </summary>
/// <inheritdoc />
public partial class RegisterStock : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            CREATE FUNCTION marketdata.register_stock(
                p_id uuid, p_market_mic text, p_ticker text, p_name text, p_currency text, p_time_zone text, p_asset_type text,
                p_provider text, p_provider_mic text, p_provider_symbol text, p_now timestamptz)
            RETURNS uuid
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, marketdata, pg_temp
            AS $$
            DECLARE
                v_id uuid;
            BEGIN
                INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at)
                VALUES (p_id, p_market_mic, p_ticker, p_name, p_currency, p_time_zone, p_asset_type, true, p_now)
                ON CONFLICT (market_mic, ticker) DO NOTHING;

                SELECT id INTO v_id FROM marketdata.stocks WHERE market_mic = p_market_mic AND ticker = p_ticker;

                INSERT INTO marketdata.symbol_aliases (provider, provider_mic, provider_symbol, stock_id, created_at)
                VALUES (p_provider, p_provider_mic, p_provider_symbol, v_id, p_now)
                ON CONFLICT DO NOTHING;

                INSERT INTO marketdata.ingestion_state (stock_id, status)
                VALUES (v_id, 'pending')
                ON CONFLICT (stock_id) DO NOTHING;

                RETURN v_id;
            END
            $$;

            REVOKE ALL ON FUNCTION marketdata.register_stock(uuid, text, text, text, text, text, text, text, text, text, timestamptz) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION marketdata.register_stock(uuid, text, text, text, text, text, text, text, text, text, timestamptz) TO api_role;
            """);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
