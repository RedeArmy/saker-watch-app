using AwesomeAssertions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class SnapshotReadersTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static async Task<StockId> SeedStockAsync(string admin, string ticker, string market = "XNAS", string assetType = "equity")
    {
        var id = StockId.Create(Guid.CreateVersion7());
        await admin.ExecuteAsync(
            "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (@id, @market, @ticker, @name, 'USD', 'America/New_York', @type, true, now())",
            ("id", id.Value), ("market", market), ("ticker", ticker), ("name", ticker + " Inc."), ("type", assetType));
        return id;
    }

    private static async Task<T> InSessionAsync<T>(string connection, Func<DbSession, Task<T>> action)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = connection }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        T result = await action(session);
        await session.CommitAsync(CancellationToken.None);
        return result;
    }

    [Fact]
    public async Task FindMany_ReturnsTheListingsTheCatalogHas_WithEveryDetail_AndIgnoresTheRest()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId aapl = await SeedStockAsync(db.Admin, "AAPL");
        StockId spy = await SeedStockAsync(db.Admin, "SPY", "ARCX", "etf");

        IReadOnlyList<CatalogListing> found = await InSessionAsync(db.Api, session =>
            new StockCatalog(session).FindManyAsync([aapl, spy, StockId.Create(Guid.CreateVersion7()), aapl], CancellationToken.None));

        found.Should().HaveCount(2);
        found.Single(l => l.Id == spy).Should().Be(new CatalogListing(spy, SakerWatch.MarketData.Domain.Markets.Mic.Parse("ARCX"), Ticker.Parse("SPY"), "SPY Inc.", CurrencyCode.Parse("USD"), AssetType.Etf, true));
        found.Single(l => l.Id == aapl).AssetType.Should().Be(AssetType.Equity);
    }

    [Fact]
    public async Task FindMany_WithNothingAsked_ReadsNothing_AndManyIdsAreSplitIntoBatches()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId one = await SeedStockAsync(db.Admin, "ONE");
        StockId[] many = [.. Enumerable.Range(0, 2500).Select(_ => StockId.Create(Guid.CreateVersion7())), one];

        IReadOnlyList<CatalogListing> none = await InSessionAsync(db.Api, session => new StockCatalog(session).FindManyAsync([], CancellationToken.None));
        IReadOnlyList<CatalogListing> found = await InSessionAsync(db.Api, session => new StockCatalog(session).FindManyAsync(many, CancellationToken.None));

        none.Should().BeEmpty();
        found.Should().ContainSingle().Which.Id.Should().Be(one);
    }

    [Fact]
    public async Task LatestBars_ReturnsTheRawCloseDateAndFinality_OfTheNewestRowOnly_AndOmitsStocksWithoutPrices()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId withPrices = await SeedStockAsync(db.Admin, "AAA");
        StockId without = await SeedStockAsync(db.Admin, "BBB");
        await db.Admin.ExecuteAsync(
            """
            INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at) VALUES
              (@id, '2026-10-14', 100, 101, 99, 100, 25, 5, 'twelvedata', true, now()),
              (@id, '2026-10-15', 104, 106, 103, 105.123456, 26.28, 5, 'twelvedata', true, now()),
              (@id, '2026-10-16', 110, 111, 109, 110.5, 27.625, 5, 'twelvedata', false, now())
            """,
            ("id", withPrices.Value));

        IReadOnlyDictionary<StockId, LatestBar> bars = await InSessionAsync(db.Api, session => new LatestBars(session).ReadAsync([withPrices, without], CancellationToken.None));

        bars.Keys.Should().Equal(withPrices);
        bars[withPrices].Should().Be(new LatestBar(new DateOnly(2026, 10, 16), 110.5m, IsFinal: false), "the raw close of the newest day, not the adjusted one");
    }

    [Fact]
    public async Task LatestBars_ManyStocks_AreReadInBatches_EachWithItsOwnNewestRow()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var ids = new List<StockId>();
        for (int i = 0; i < 12; i++)
        {
            ids.Add(await SeedStockAsync(db.Admin, $"S{i:D2}"));
        }

        await db.Admin.ExecuteAsync(
            """
            INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at)
            SELECT s.id, d::date, 10 + extract(day FROM d), 11 + extract(day FROM d), 9 + extract(day FROM d), 10 + extract(day FROM d), 10, 1, 'twelvedata', true, now()
            FROM marketdata.stocks s CROSS JOIN generate_series('2026-10-01'::date, '2026-10-16'::date, interval '1 day') AS g(d)
            """);
        StockId[] asked = [.. ids, .. Enumerable.Range(0, 700).Select(_ => StockId.Create(Guid.CreateVersion7()))];

        IReadOnlyDictionary<StockId, LatestBar> bars = await InSessionAsync(db.Api, session => new LatestBars(session).ReadAsync(asked, CancellationToken.None));

        bars.Should().HaveCount(12);
        bars.Values.Should().OnlyContain(bar => bar.Date == new DateOnly(2026, 10, 16) && bar.Close == 26m && bar.IsFinal);
    }

    [Fact]
    public async Task FailedIngestions_ListsOnlyTheStocksWhoseLoadFailedForGood()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId failed = await SeedStockAsync(db.Admin, "BAD");
        StockId pending = await SeedStockAsync(db.Admin, "NEW");
        StockId ok = await SeedStockAsync(db.Admin, "OK");
        StockId untracked = await SeedStockAsync(db.Admin, "NONE");
        await db.Admin.ExecuteAsync("INSERT INTO marketdata.ingestion_state (stock_id, status, failure_code) VALUES (@a, 'failed', 'marketdata.provider.symbol_not_found'), (@b, 'pending', NULL), (@c, 'ok', NULL)",
            ("a", failed.Value), ("b", pending.Value), ("c", ok.Value));

        IReadOnlySet<StockId> found = await InSessionAsync(db.Api, session => new FailedIngestions(session).FailedAsync([failed, pending, ok, untracked], CancellationToken.None));

        found.Should().BeEquivalentTo([failed]);
    }
}
