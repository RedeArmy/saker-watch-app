using AwesomeAssertions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class PriceSeriesReaderTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static async Task<StockId> SeedStockAsync(string admin, string ticker)
    {
        var id = StockId.Create(Guid.CreateVersion7());
        await admin.ExecuteAsync(
            "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (@id, 'XNAS', @ticker, @name, 'USD', 'America/New_York', 'equity', true, now())",
            ("id", id.Value), ("ticker", ticker), ("name", ticker + " Inc."));
        return id;
    }

    // Weekdays from 2026-09-28 to 2026-10-16: the close as traded is twice the adjusted one, and the adjusted one is the day of the month, so every
    // row can be told apart. The 14th is provisional.
    private static Task<int> SeedDaysAsync(string admin, StockId stock, string from = "2026-09-28", string to = "2026-10-16") =>
        admin.ExecuteAsync(
            """
            INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at)
            SELECT @id, d::date, extract(day FROM d) * 2, extract(day FROM d) * 2 + 1, extract(day FROM d) * 2 - 1, extract(day FROM d) * 2, extract(day FROM d), 1000, 'twelvedata', d::date <> '2026-10-14', now()
            FROM generate_series(@from::date, @to::date, interval '1 day') AS g(d)
            WHERE extract(isodow FROM d) < 6
            """,
            ("id", stock.Value), ("from", from), ("to", to));

    private static async Task<T> InSessionAsync<T>(string connection, Func<DbSession, Task<T>> action)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = connection }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        T result = await action(session);
        await session.CommitAsync(CancellationToken.None);
        return result;
    }

    private static string[] Dates(IEnumerable<SeriesBar> bars) => [.. bars.Select(bar => bar.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))];

    [Fact]
    public async Task Days_AreReturnedOldestFirst_WithBothClosesAndFinality_FromTheDayAsked()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId stock = await SeedStockAsync(db.Admin, "AAA");
        await SeedDaysAsync(db.Admin, stock);

        IReadOnlyList<SeriesBar> all = await InSessionAsync(db.Api, s => new PriceSeriesReader(s).ReadAsync(stock, null, SeriesBucket.Day, CancellationToken.None));
        IReadOnlyList<SeriesBar> since = await InSessionAsync(db.Api, s => new PriceSeriesReader(s).ReadAsync(stock, new DateOnly(2026, 10, 12), SeriesBucket.Day, CancellationToken.None));

        all.Should().HaveCount(15);
        Dates(all).Should().BeInAscendingOrder();
        all[0].Should().Be(new SeriesBar(new DateOnly(2026, 9, 28), 56m, 28m, true));
        all.Single(bar => bar.Date == new DateOnly(2026, 10, 14)).IsFinal.Should().BeFalse();
        Dates(since).Should().Equal("2026-10-12", "2026-10-13", "2026-10-14", "2026-10-15", "2026-10-16");
    }

    [Fact]
    public async Task Weeks_AreTheirLastTradingDay_AndOnlyTheDaysFromTheStartCount()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId stock = await SeedStockAsync(db.Admin, "BBB");
        await SeedDaysAsync(db.Admin, stock);

        IReadOnlyList<SeriesBar> weeks = await InSessionAsync(db.Api, s => new PriceSeriesReader(s).ReadAsync(stock, null, SeriesBucket.Week, CancellationToken.None));
        IReadOnlyList<SeriesBar> fromTheSaturday = await InSessionAsync(db.Api, s => new PriceSeriesReader(s).ReadAsync(stock, new DateOnly(2026, 10, 10), SeriesBucket.Week, CancellationToken.None));
        IReadOnlyList<SeriesBar> fromTheTuesday = await InSessionAsync(db.Api, s => new PriceSeriesReader(s).ReadAsync(stock, new DateOnly(2026, 10, 6), SeriesBucket.Week, CancellationToken.None));

        Dates(weeks).Should().Equal("2026-10-02", "2026-10-09", "2026-10-16");
        weeks.Select(bar => bar.AdjustedClose).Should().Equal(2m, 9m, 16m);
        Dates(fromTheSaturday).Should().Equal("2026-10-16");
        Dates(fromTheTuesday).Should().Equal("2026-10-09", "2026-10-16");
    }

    [Fact]
    public async Task Months_AreTheirLastTradingDay_AndAPartialMonthShowsItsNewestDay()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId stock = await SeedStockAsync(db.Admin, "CCC");
        await SeedDaysAsync(db.Admin, stock);

        IReadOnlyList<SeriesBar> months = await InSessionAsync(db.Api, s => new PriceSeriesReader(s).ReadAsync(stock, null, SeriesBucket.Month, CancellationToken.None));

        Dates(months).Should().Equal("2026-09-30", "2026-10-16");
    }

    [Fact]
    public async Task ARangeWithNoDays_IsEmpty_AndOtherStocksNeverLeakIn()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId stock = await SeedStockAsync(db.Admin, "DDD");
        StockId other = await SeedStockAsync(db.Admin, "EEE");
        await SeedDaysAsync(db.Admin, other);

        foreach (SeriesBucket bucket in Enum.GetValues<SeriesBucket>())
        {
            (await InSessionAsync(db.Api, s => new PriceSeriesReader(s).ReadAsync(stock, null, bucket, CancellationToken.None))).Should().BeEmpty(bucket.ToString());
        }
    }

    [Fact]
    public async Task TheRecentCloses_AreTheLastDaysOfEachStock_OldestFirst_AndAStockWithoutPricesIsLeftOut()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId many = await SeedStockAsync(db.Admin, "FFF");
        StockId few = await SeedStockAsync(db.Admin, "GGG");
        StockId none = await SeedStockAsync(db.Admin, "HHH");
        await SeedDaysAsync(db.Admin, many);
        await SeedDaysAsync(db.Admin, few, "2026-10-14", "2026-10-16");

        IReadOnlyDictionary<StockId, IReadOnlyList<decimal>> recent = await InSessionAsync(db.Api, s => new PriceSeriesReader(s).RecentAdjustedAsync([many, few, none, many], 5, CancellationToken.None));

        recent.Keys.Should().BeEquivalentTo([many, few]);
        recent[many].Should().Equal(12m, 13m, 14m, 15m, 16m);
        recent[few].Should().Equal(14m, 15m, 16m);
    }

    [Fact]
    public async Task TheRecentCloses_OfManyStocks_AreReadInBatches()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId one = await SeedStockAsync(db.Admin, "III");
        await SeedDaysAsync(db.Admin, one);
        StockId[] asked = [one, .. Enumerable.Range(0, 1200).Select(_ => StockId.Create(Guid.CreateVersion7()))];

        IReadOnlyDictionary<StockId, IReadOnlyList<decimal>> recent = await InSessionAsync(db.Api, s => new PriceSeriesReader(s).RecentAdjustedAsync(asked, 3, CancellationToken.None));

        recent.Should().ContainSingle().Which.Key.Should().Be(one);
    }
}
