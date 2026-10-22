using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SakerWatch.MarketData.Infrastructure.Persistence.Migrations;

/// <summary>
/// <c>marketdata.register_stock</c> no longer hides a conflict of the provider's listing. The alias (provider, provider market code, provider symbol)
/// is the provider's own name for one listing; when it already belongs to another stock, the function used to insert nothing for it and still return
/// the new stock, which was then left with no alias and could never be loaded (its first load failed later with <c>no_alias</c>). Now it raises a unique
/// violation of <c>pk_symbol_aliases</c> and the caller's unit of work rolls the whole registration back; the application answers <c>409
/// marketdata.stock.provider_listing_taken</c>. The same stock registering its own alias again stays idempotent. The check comes after the insert, so
/// two parallel registrations of the same alias for different stocks are told apart too (the second insert waits for the first transaction).
/// The function keeps its owner, its grants (<c>api_role</c> only) and its pinned <c>search_path</c> (<c>CREATE OR REPLACE</c> keeps the privileges).
/// </summary>
/// <inheritdoc />
public partial class RegisterStockProviderListingTaken : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION marketdata.register_stock(
                p_id uuid, p_market_mic text, p_ticker text, p_name text, p_currency text, p_time_zone text, p_asset_type text,
                p_provider text, p_provider_mic text, p_provider_symbol text, p_now timestamptz)
            RETURNS uuid
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, marketdata, pg_temp
            AS $$
            DECLARE
                v_id uuid;
                v_owner uuid;
            BEGIN
                INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at)
                VALUES (p_id, p_market_mic, p_ticker, p_name, p_currency, p_time_zone, p_asset_type, true, p_now)
                ON CONFLICT (market_mic, ticker) DO NOTHING;

                SELECT id INTO v_id FROM marketdata.stocks WHERE market_mic = p_market_mic AND ticker = p_ticker;

                INSERT INTO marketdata.symbol_aliases (provider, provider_mic, provider_symbol, stock_id, created_at)
                VALUES (p_provider, p_provider_mic, p_provider_symbol, v_id, p_now)
                ON CONFLICT DO NOTHING;

                SELECT stock_id INTO v_owner FROM marketdata.symbol_aliases
                WHERE provider = p_provider AND provider_mic = p_provider_mic AND provider_symbol = p_provider_symbol;
                IF v_owner IS DISTINCT FROM v_id THEN
                    RAISE EXCEPTION 'The provider listing is already the alias of another stock.'
                        USING ERRCODE = '23505', CONSTRAINT = 'pk_symbol_aliases';
                END IF;

                INSERT INTO marketdata.ingestion_state (stock_id, status)
                VALUES (v_id, 'pending')
                ON CONFLICT (stock_id) DO NOTHING;

                RETURN v_id;
            END
            $$;
            """);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Migrations are forward-only (ADR-0004); fix forward with a new migration.");
}
