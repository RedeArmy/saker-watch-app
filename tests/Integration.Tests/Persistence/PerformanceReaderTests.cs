using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Performance;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Persistence;
using Xunit.Abstractions;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class PerformanceReaderTests(TimescalePostgresFixture postgres, ITestOutputHelper output) : IClassFixture<TimescalePostgresFixture>
{
    private static readonly DateOnly _lastDay = new(2026, 10, 16);
    private static readonly IExchangeCalendar _calendar = new UsEquityCalendar(Mic.Parse("XNYS"));

    // Every weekday from `from` to the last day, with an adjusted close that moves every day: 100 + 0.5 x (n mod 37) + 0.01 x n. The raw close is
    // the adjusted one times `rawFactor` before `splitDay` (a 4:1 split that the adjusted series has already absorbed) and equal to it after.
    private const string _insertSeries = """
        INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at)
        SELECT @id, d::date, a * f, (a + 1) * f, (a - 1) * f, a * f, a, 1000, 'twelvedata', true, now()
        FROM (SELECT d, 100 + 0.5 * (row_number() OVER (ORDER BY d) % 37) + 0.01 * row_number() OVER (ORDER BY d) AS a,
                     CASE WHEN d::date < @split THEN @factor ELSE 1 END AS f
              FROM generate_series(@from::date, @to::date, interval '1 day') AS g(d) WHERE extract(isodow FROM d) < 6) s
        """;

    private static async Task<Guid> SeedStockAsync(string admin, string ticker)
    {
        var id = Guid.CreateVersion7();
        await admin.ExecuteAsync(
            "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (@id, 'XNAS', @ticker, @ticker, 'USD', 'America/New_York', 'equity', true, now())",
            ("id", id), ("ticker", ticker));
        return id;
    }

    private static async Task SeedSeriesAsync(string admin, Guid id, DateOnly from, DateOnly? split = null, int factor = 1) =>
        _ = await admin.ExecuteAsync(_insertSeries, ("id", id), ("from", from), ("to", _lastDay), ("split", split ?? DateOnly.MinValue), ("factor", (decimal)factor));

    private static async Task<IReadOnlyDictionary<StockId, PerformanceReport>> ReadAsync(string connection, params Guid[] ids)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = connection }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        var reader = new PerformanceReader(session, ExchangeCalendars.Default(), Options.Create(new PerformanceOptions()));
        IReadOnlyDictionary<StockId, PerformanceReport> reports = await reader.ReadAsync([.. ids.Select(StockId.Create)], CancellationToken.None);
        await session.CommitAsync(CancellationToken.None);
        return reports;
    }

    // The same windows, computed from the whole series held in memory: what the database lookups must agree with.
    private static async Task<PerformanceReport> OracleAsync(string admin, Guid id)
    {
        var series = new List<PriceSample>();
        await using var connection = new Npgsql.NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT trade_date, adjusted_close FROM marketdata.price_daily WHERE stock_id = @id ORDER BY trade_date", connection);
        command.Parameters.AddWithValue("id", id);
        await using Npgsql.NpgsqlDataReader rows = await command.ExecuteReaderAsync();
        while (await rows.ReadAsync())
        {
            series.Add(PriceSample.Create(rows.GetFieldValue<DateOnly>(0), rows.GetDecimal(1)).Value);
        }

        if (series.Count == 0)
        {
            return PerformanceCalculator.NoData();
        }

        PriceSample latest = series[^1];
        var baselines = new Dictionary<DateOnly, PriceSample?>();
        foreach (DateOnly target in PerformanceCalculator.TargetDates(latest.Date, _calendar).Values)
        {
            baselines[target] = series.LastOrDefault(sample => sample.Date <= target);
        }

        return PerformanceCalculator.Calculate(latest, baselines, _calendar);
    }

    private static void ShouldMatch(PerformanceReport actual, PerformanceReport expected)
    {
        actual.Latest.Should().Be(expected.Latest);
        actual.Windows.Should().Equal(expected.Windows);
    }

    [Fact]
    public async Task Read_SixYearsOfHistory_AgreesWithTheCalculatorOverTheWholeSeries()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid id = await SeedStockAsync(db.Admin, "AAPL");
        await SeedSeriesAsync(db.Admin, id, new DateOnly(2020, 1, 1));

        PerformanceReport report = (await ReadAsync(db.Api, id))[StockId.Create(id)];

        ShouldMatch(report, await OracleAsync(db.Admin, id));
        report.Latest!.Date.Should().Be(_lastDay);
        report.Windows.Should().OnlyContain(w => w.Change != null && w.Gap == null);
        report.Windows.Single(w => w.Window == PerformanceWindow.FiveYears).Baseline!.Date.Should().Be(new DateOnly(2021, 10, 15), "the 16th of October 2021 was a Saturday");
        report.Windows.Single(w => w.Window == PerformanceWindow.YearToDate).Baseline!.Date.Should().Be(new DateOnly(2025, 12, 31));
    }

    [Fact]
    public async Task Read_AcrossASplit_UsesTheAdjustedCloseAndNeverTheRawOne()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid id = await SeedStockAsync(db.Admin, "SPLT");
        await SeedSeriesAsync(db.Admin, id, new DateOnly(2025, 1, 1), split: new DateOnly(2026, 10, 14), factor: 4);

        PerformanceReport report = (await ReadAsync(db.Api, id))[StockId.Create(id)];

        decimal oneWeekRaw = await db.Admin.ScalarAsync<decimal>("SELECT close FROM marketdata.price_daily WHERE stock_id = @id AND trade_date = '2026-10-09'", ("id", id));
        decimal oneWeekAdjusted = await db.Admin.ScalarAsync<decimal>("SELECT adjusted_close FROM marketdata.price_daily WHERE stock_id = @id AND trade_date = '2026-10-09'", ("id", id));
        oneWeekRaw.Should().Be(oneWeekAdjusted * 4, "the fixture really has a raw close four times the adjusted one before the split");
        WindowPerformance week = report.Windows.Single(w => w.Window == PerformanceWindow.OneWeek);
        week.Baseline!.AdjustedClose.Should().Be(oneWeekAdjusted);
        week.Change.Should().BeGreaterThan(-0.2m, "a raw-close comparison would show about -75 %");
        ShouldMatch(report, await OracleAsync(db.Admin, id));
    }

    [Fact]
    public async Task Read_AYoungListing_HasInsufficientHistoryOnlyForTheLongWindows()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid id = await SeedStockAsync(db.Admin, "NEWB");
        await SeedSeriesAsync(db.Admin, id, new DateOnly(2026, 9, 28));

        PerformanceReport report = (await ReadAsync(db.Api, id))[StockId.Create(id)];

        report.Windows.Where(w => w.Window is PerformanceWindow.OneDay or PerformanceWindow.OneWeek).Should().OnlyContain(w => w.Change != null);
        report.Windows.Where(w => w.Window is not (PerformanceWindow.OneDay or PerformanceWindow.OneWeek)).Should().OnlyContain(w => w.Gap == PerformanceGap.InsufficientHistory);
        ShouldMatch(report, await OracleAsync(db.Admin, id));
    }

    [Fact]
    public async Task Read_AHoleInTheHistory_IsAGapAndNotAWrongNumber()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid id = await SeedStockAsync(db.Admin, "HOLE");
        await SeedSeriesAsync(db.Admin, id, new DateOnly(2025, 1, 1));
        // Nothing between the 5th and the 30th of September, and the previous session (the 15th of October) is missing too.
        await db.Admin.ExecuteAsync("DELETE FROM marketdata.price_daily WHERE stock_id = @id AND (trade_date BETWEEN '2026-09-05' AND '2026-09-30' OR trade_date = '2026-10-15')", ("id", id));

        PerformanceReport report = (await ReadAsync(db.Api, id))[StockId.Create(id)];

        report.Windows.Single(w => w.Window == PerformanceWindow.OneMonth).Gap.Should().Be(PerformanceGap.HistoryGap);
        report.Windows.Single(w => w.Window == PerformanceWindow.OneDay).Gap.Should().Be(PerformanceGap.HistoryGap);
        report.Windows.Single(w => w.Window == PerformanceWindow.OneWeek).Change.Should().NotBeNull();
        ShouldMatch(report, await OracleAsync(db.Admin, id));
    }

    [Fact]
    public async Task Read_AListingWithoutPricesOrOutsideTheCatalog_IsInTheAnswerWithNoData()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid empty = await SeedStockAsync(db.Admin, "NONE");
        Guid unknown = Guid.CreateVersion7();

        IReadOnlyDictionary<StockId, PerformanceReport> reports = await ReadAsync(db.Api, empty, unknown);

        reports.Keys.Should().BeEquivalentTo([StockId.Create(empty), StockId.Create(unknown)]);
        reports.Values.Should().OnlyContain(r => r.Latest == null && r.Windows.All(w => w.Gap == PerformanceGap.NoData));
    }

    [Fact]
    public async Task Read_AProvisionalLatestBar_IsTheAnchor()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid id = await SeedStockAsync(db.Admin, "PROV");
        await SeedSeriesAsync(db.Admin, id, new DateOnly(2026, 1, 1));
        await db.Admin.ExecuteAsync("UPDATE marketdata.price_daily SET is_final = false, adjusted_close = 500, close = 500, high = 501, low = 499, open = 500 WHERE stock_id = @id AND trade_date = '2026-10-16'", ("id", id));

        PerformanceReport report = (await ReadAsync(db.Api, id))[StockId.Create(id)];

        report.Latest!.AdjustedClose.Should().Be(500m);
    }

    [Fact]
    public async Task Read_ManyListingsAcrossSeveralBatches_GivesEachItsOwnAnswer()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid a = await SeedStockAsync(db.Admin, "AAA");
        Guid b = await SeedStockAsync(db.Admin, "BBB");
        await SeedSeriesAsync(db.Admin, a, new DateOnly(2025, 6, 1));
        await SeedSeriesAsync(db.Admin, b, new DateOnly(2026, 10, 1));
        Guid[] ids = [.. Enumerable.Range(0, 1200).Select(_ => Guid.CreateVersion7()), a, b];

        IReadOnlyDictionary<StockId, PerformanceReport> reports = await ReadAsync(db.Api, ids);

        reports.Should().HaveCount(1202);
        ShouldMatch(reports[StockId.Create(a)], await OracleAsync(db.Admin, a));
        ShouldMatch(reports[StockId.Create(b)], await OracleAsync(db.Admin, b));
        reports.Values.Count(r => r.Latest != null).Should().Be(2);
    }

    [Fact]
    public async Task Read_AskedWithTheSameListingTwice_AnswersOnce()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid id = await SeedStockAsync(db.Admin, "DUPE");
        await SeedSeriesAsync(db.Admin, id, new DateOnly(2026, 1, 1));

        (await ReadAsync(db.Api, id, id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Read_FiveHundredListingsWithSixYearsEach_StaysWellUnderASecond()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var ids = new List<Guid>();
        for (int i = 0; i < 500; i++)
        {
            ids.Add(await SeedStockAsync(db.Admin, $"S{i:D4}"));
        }

        // 500 x about 1 700 weekdays in one statement.
        await using (var connection = new Npgsql.NpgsqlConnection(db.Admin))
        {
            await connection.OpenAsync();
            await using var insert = new Npgsql.NpgsqlCommand(
                """
                INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at)
                SELECT s.id, x.d::date, x.a, x.a + 1, x.a - 1, x.a, x.a, 1000, 'twelvedata', true, now()
                FROM marketdata.stocks s
                CROSS JOIN LATERAL (SELECT d, 100 + 0.5 * (row_number() OVER (ORDER BY d) % 37) AS a
                                    FROM generate_series('2020-01-01'::date, '2026-10-16'::date, interval '1 day') AS g(d) WHERE extract(isodow FROM d) < 6) x
                """,
                connection)
            { CommandTimeout = 600 };
            await insert.ExecuteNonQueryAsync();
            await using var analyze = new Npgsql.NpgsqlCommand("ANALYZE marketdata.price_daily", connection) { CommandTimeout = 600 };
            await analyze.ExecuteNonQueryAsync();
        }

        await ReadAsync(db.Api, [.. ids.Take(5)]); // warm the plan and the connection

        var clock = Stopwatch.StartNew();
        IReadOnlyDictionary<StockId, PerformanceReport> reports = await ReadAsync(db.Api, [.. ids]);
        clock.Stop();
        output.WriteLine($"500 listings x ~1 700 days: {clock.ElapsedMilliseconds} ms");

        reports.Should().HaveCount(500);
        reports.Values.Should().OnlyContain(r => r.Windows.All(w => w.Change != null));
        // About 0.3 s on the test container: nine index probes per listing across seven yearly chunks. The watchlist has 300 ms (p95) for all of
        // its work at this size, so the daily numbers are cached per listing and day (milestone 2.8); this bound only catches a regression to a scan.
        clock.ElapsedMilliseconds.Should().BeLessThan(1000, $"took {clock.ElapsedMilliseconds} ms");
    }
}
