using AwesomeAssertions;
using SakerWatch.Hosts.Migrator;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Persistence;

/// <summary>
/// Upgrades from an earlier version of the schema with rows in it (the rule of data and migrations: every migration is tested on an empty database and
/// on one seeded at the previous version). An empty database proves a migration applies; these prove it applies over data and keeps it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MigrationUpgradeTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private const string _insertStock =
        "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (@id, 'XNAS', @ticker, 'Apple Inc.', 'USD', @zone, 'equity', true, now())";

    private async Task<(string Admin, EfMigrationStep<SakerWatch.MarketData.Infrastructure.Persistence.MarketDataDbContext> Step)> MarketDataAtAsync(string migration)
    {
        string admin = await postgres.CreateDatabaseAsync();
        await MigrationSteps.PlatformStep(admin).RunAsync(CancellationToken.None);
        var step = MigrationSteps.MarketDataStep(admin);
        await step.MigrateToAsync(migration, CancellationToken.None);
        return (admin, step);
    }

    [Fact]
    public async Task FromTheIngestionState_KeepsTheCatalogPricesAndState_AndAddsTheRatesAndTheRejectedRates()
    {
        (string admin, var step) = await MarketDataAtAsync("CreateIngestionState");
        var id = Guid.CreateVersion7();
        await admin.ExecuteAsync(_insertStock, ("id", id), ("ticker", "AAPL"), ("zone", "America/New_York"));
        await admin.ExecuteAsync("INSERT INTO marketdata.ingestion_state (stock_id, status, last_success_at) VALUES (@id, 'ok', now())", ("id", id));
        await admin.ExecuteAsync(
            "INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at) VALUES (@id, '2026-10-14', 10, 11, 9, 10, 10, 5, 'twelvedata', true, now())",
            ("id", id));

        await step.RunAsync(CancellationToken.None);

        (await admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks")).Should().Be(1);
        (await admin.ScalarAsync<string>("SELECT status FROM marketdata.ingestion_state")).Should().Be("ok");
        (await admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily")).Should().Be(1);
        (await admin.ScalarAsync<bool>("SELECT to_regclass('marketdata.fx_rates') IS NOT NULL AND to_regclass('marketdata.fx_rates_rejected') IS NOT NULL")).Should().BeTrue();
    }

    [Fact]
    public async Task FromTheRates_EveryStoredRateKeepsItsExactValue_WhenTheColumnIsWidened()
    {
        (string admin, var step) = await MarketDataAtAsync("CreateFxRates");
        decimal[] rates = [7.6414m, 7.64572m, 7.7350000001m, 0.0000000001m, 9_999_999_999.9999999999m];
        for (int i = 0; i < rates.Length; i++)
        {
            await admin.ExecuteAsync(
                "INSERT INTO marketdata.fx_rates (base, quote, as_of_date, source, rate, ingested_at) VALUES ('USD', 'GTQ', @day::date, 'banguat', @rate, now())",
                ("day", $"2026-10-{10 + i:00}"), ("rate", rates[i]));
        }

        await step.RunAsync(CancellationToken.None);

        (await admin.ScalarAsync<string>("SELECT string_agg(rate::text, ',' ORDER BY as_of_date) FROM marketdata.fx_rates"))
            .Should().Be("7.641400000000,7.645720000000,7.735000000100,0.000000000100,9999999999.999999999900", "the values are the same, with the two more decimals of the new scale");
        (await admin.ScalarAsync<string>("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'marketdata.fx_rates'::regclass AND attname = 'rate'")).Should().Be("numeric(30,12)");
    }

    [Fact]
    public async Task FromTheRegisteredStocks_RowsThatFitTheNewTextChecksStay_AndTheAliasesAreIntact()
    {
        (string admin, var step) = await MarketDataAtAsync("RegisterStock");
        var id = Guid.CreateVersion7();
        await admin.ExecuteAsync(_insertStock, ("id", id), ("ticker", "BRKB"), ("zone", "America/New_York"));
        await admin.ExecuteAsync("INSERT INTO marketdata.symbol_aliases (provider, provider_mic, provider_symbol, stock_id, created_at) VALUES ('twelvedata', 'XNYS', 'BRK.B', @id, now())", ("id", id));

        await step.RunAsync(CancellationToken.None);

        (await admin.ScalarAsync<string>("SELECT provider_symbol FROM marketdata.symbol_aliases")).Should().Be("BRK.B");
        (await admin.ScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname IN ('ck_stocks_time_zone', 'ck_symbol_aliases_provider', 'ck_symbol_aliases_provider_mic', 'ck_symbol_aliases_provider_symbol')")).Should().Be(4);
    }

    [Fact]
    public async Task FromTheRegisteredStocks_ARowThatDoesNotFitTheTextChecksStopsTheUpgrade_InsteadOfStayingUnchecked()
    {
        (string admin, var step) = await MarketDataAtAsync("RegisterStock");
        var id = Guid.CreateVersion7();
        await admin.ExecuteAsync(_insertStock, ("id", id), ("ticker", "EVIL"), ("zone", "Not A Zone"));

        Func<Task> upgrade = () => step.RunAsync(CancellationToken.None);

        await upgrade.Should().ThrowAsync<Exception>("a catalog row that the checks refuse must stop the deploy, so someone looks at it");
    }

    [Fact]
    public async Task FromTheSecuritySteps_AWatchlistAtItsCeiling_KeepsEveryRow_AndTheNewTriggersAreInPlace()
    {
        string admin = await postgres.CreateDatabaseAsync();
        await MigrationSteps.PlatformStep(admin).RunAsync(CancellationToken.None);
        var step = MigrationSteps.WatchlistStep(admin);
        await step.MigrateToAsync("PinWatchlistSearchPaths", CancellationToken.None);
        var user = Guid.CreateVersion7();
        await admin.ExecuteAsync("INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, created_at, updated_at) SELECT gen_random_uuid(), @user, gen_random_uuid(), now(), now() FROM generate_series(1, 500)", ("user", user));
        await admin.ExecuteAsync("INSERT INTO watchlist.portfolios (id, user_id, name, position, created_at, updated_at) VALUES (gen_random_uuid(), @user, 'Tecnología', 0, now(), now())", ("user", user));

        await step.RunAsync(CancellationToken.None);

        (await admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(500);
        (await admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.portfolios")).Should().Be(1);
        (await admin.ScalarAsync<bool>("SELECT bool_and(p.proconfig::text LIKE '%pg_temp%') FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'watchlist' AND p.proname LIKE 'enforce_%'"))
            .Should().BeTrue("the replaced functions keep a search path that ends in pg_temp");
    }
}
