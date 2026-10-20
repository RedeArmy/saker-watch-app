using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Contracts;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Tests;

/// <summary>The price history over fakes of the catalog, the reader and the status sources (ADR-0038).</summary>
public sealed class PriceHistoryTests
{
    // Saturday 17 October 2026: Friday the 16th is the latest settled session.
    private static readonly DateTimeOffset _now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly StockId _apple = StockId.Create(Guid.NewGuid());

    private static readonly CatalogListing _listing = new(
        _apple, Mic.Parse("XNAS"), Ticker.Parse("AAPL"), "Apple Inc.", CurrencyCode.Parse("USD"), AssetType.Equity, IsActive: true);

    private sealed class Catalog(CatalogListing? listing) : IStockCatalog
    {
        public Task<CatalogStock?> FindAsync(StockId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CatalogListing?> FindByKeyAsync(Mic market, Ticker ticker, CancellationToken cancellationToken) =>
            Task.FromResult(listing is not null && listing.Market == market && listing.Ticker == ticker ? listing : null);

        public Task<IReadOnlyList<CatalogListing>> FindManyAsync(IReadOnlyCollection<StockId> ids, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<StockId>> ListIngestableAsync(Mic market, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Reader(params SeriesBar[] bars) : IPriceSeriesReader
    {
        public List<(DateOnly? From, SeriesBucket Bucket)> Reads { get; } = [];

        public Task<IReadOnlyList<SeriesBar>> ReadAsync(StockId stock, DateOnly? from, SeriesBucket bucket, CancellationToken cancellationToken)
        {
            Reads.Add((from, bucket));
            return Task.FromResult<IReadOnlyList<SeriesBar>>([.. bars.Where(bar => from is null || bar.Date >= from)]);
        }

        public Task<IReadOnlyDictionary<StockId, IReadOnlyList<decimal>>> RecentAdjustedAsync(IReadOnlyCollection<StockId> stocks, int days, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<StockId, IReadOnlyList<decimal>>>(stocks.ToDictionary(id => id, id => (IReadOnlyList<decimal>)[.. bars.TakeLast(days).Select(bar => bar.AdjustedClose)]));
    }

    private sealed class Latest(LatestBar? bar) : ILatestBars
    {
        public Task<IReadOnlyDictionary<StockId, LatestBar>> ReadAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<StockId, LatestBar>>(bar is null ? new Dictionary<StockId, LatestBar>() : new Dictionary<StockId, LatestBar> { [_apple] = bar });
    }

    private sealed class Failures(bool failed) : IIngestionStatuses
    {
        public Task<IReadOnlySet<StockId>> FailedAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<StockId>>(failed ? new HashSet<StockId> { _apple } : new HashSet<StockId>());
    }

    private static SeriesBar Bar(int month, int day, decimal close) => new(new DateOnly(2026, month, day), close * 2, close, IsFinal: true);

    private static PriceHistory History(Reader reader, LatestBar? latest, bool failed = false, CatalogListing? listing = null) =>
        new(new Catalog(listing ?? _listing), reader, new Latest(latest), new Failures(failed), ExchangeCalendars.Default(), Options.Create(new IngestionOptions()), new FakeTimeProvider(_now));

    private static LatestBar Newest(int month, int day) => new(new DateOnly(2026, month, day), 100m, IsFinal: true);

    [Fact]
    public async Task ARange_StartsWhereItsWindowDoes_AndTheSeriesCarriesTheStockTheStatusAndBothCloses()
    {
        var reader = new Reader(Bar(9, 10, 90m), Bar(9, 16, 95m), Bar(10, 14, 99m), Bar(10, 16, 100m));

        PriceSeries series = (await History(reader, Newest(10, 16)).SeriesAsync("XNAS", "AAPL", "1M", "auto", CancellationToken.None)).Value;

        series.Stock.Should().Be(new StockSummary(_apple.Value, "XNAS", "AAPL", "Apple Inc.", "USD", "equity"));
        (series.Range, series.Interval, series.From, series.To, series.PriceStatus).Should().Be(("1M", "day", (DateOnly?)new DateOnly(2026, 9, 16), (DateOnly?)new DateOnly(2026, 10, 16), PriceStatuses.Current));
        series.Points.Select(point => point.Date).Should().Equal(new DateOnly(2026, 9, 16), new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 16));
        series.Points[^1].Should().Be(new ChartPoint(new DateOnly(2026, 10, 16), 200m, 100m, IsFinal: true));
        reader.Reads.Should().Equal((new DateOnly(2026, 9, 16), SeriesBucket.Day));
    }

    [Theory]
    [InlineData("1W", "auto", "day")]
    [InlineData("1Y", "auto", "day")]
    [InlineData("5Y", "auto", "week")]
    [InlineData("MAX", "auto", "month")]
    [InlineData("1Y", "week", "week")]
    [InlineData("MAX", "day", "day")]
    [InlineData("1y", "MONTH", "month")]
    public async Task TheInterval_IsTheOneAskedFor_OrTheOneTheRangeUses(string range, string asked, string used)
    {
        var reader = new Reader(Bar(10, 16, 100m));

        PriceSeries series = (await History(reader, Newest(10, 16)).SeriesAsync("XNAS", "AAPL", range, asked, CancellationToken.None)).Value;

        series.Interval.Should().Be(used);
        reader.Reads.Single().Bucket.Should().Be(used switch { "week" => SeriesBucket.Week, "month" => SeriesBucket.Month, _ => SeriesBucket.Day });
    }

    [Fact]
    public async Task MaxReadsEverything_AndHasNoStart()
    {
        var reader = new Reader(Bar(1, 5, 50m), Bar(10, 16, 100m));

        PriceSeries series = (await History(reader, Newest(10, 16)).SeriesAsync("XNAS", "AAPL", "MAX", "auto", CancellationToken.None)).Value;

        (series.From, series.Points.Count).Should().Be(((DateOnly?)null, 2));
        reader.Reads.Single().From.Should().BeNull();
    }

    [Fact]
    public async Task ANewestPriceBehindTheLatestSettledSession_IsStale_AndSaysSince()
    {
        PriceSeries series = (await History(new Reader(Bar(10, 14, 99m)), Newest(10, 14)).SeriesAsync("XNAS", "AAPL", "1M", "auto", CancellationToken.None)).Value;

        series.PriceStatus.Should().Be(PriceStatuses.Stale);
        series.StaleSince.Should().NotBeNull();
        series.To.Should().Be(new DateOnly(2026, 10, 14));
    }

    [Theory]
    [InlineData(false, PriceStatuses.Pending)]
    [InlineData(true, PriceStatuses.Unavailable)]
    public async Task AStockWithoutPrices_IsAnEmptySeriesWithItsStatus_NotAFailure(bool failed, string status)
    {
        var reader = new Reader();

        Result<PriceSeries> result = await History(reader, latest: null, failed).SeriesAsync("XNAS", "AAPL", "1Y", "auto", CancellationToken.None);

        PriceSeries series = result.Value;
        (series.Points, series.From, series.To, series.PriceStatus).Should().Be((Array.Empty<ChartPoint>(), (DateOnly?)null, (DateOnly?)null, status));
        reader.Reads.Should().BeEmpty("there is no anchor to start a range from, so nothing is read");
    }

    [Theory]
    [InlineData("2Y", "auto", PriceHistory.RangeInvalidCode)]
    [InlineData("", "auto", PriceHistory.RangeInvalidCode)]
    [InlineData("1Y", "hourly", PriceHistory.IntervalInvalidCode)]
    public async Task ARangeOrIntervalThatIsUnknown_IsAValidationFailure(string range, string interval, string code)
    {
        Result<PriceSeries> result = await History(new Reader(), Newest(10, 16)).SeriesAsync("XNAS", "AAPL", range, interval, CancellationToken.None);

        (result.Error!.Type, result.Error.Code).Should().Be((ErrorType.Validation, code));
    }

    [Fact]
    public async Task AStockTheCatalogDoesNotHave_IsNotFound_AndABadMarketOrTickerIsAValidationFailure()
    {
        PriceHistory history = History(new Reader(), Newest(10, 16));

        Result<PriceSeries> unknown = await history.SeriesAsync("XNAS", "MSFT", "1Y", "auto", CancellationToken.None);
        Result<PriceSeries> otherMarket = await history.SeriesAsync("XNYS", "AAPL", "1Y", "auto", CancellationToken.None);
        Result<PriceSeries> badMarket = await history.SeriesAsync("??", "AAPL", "1Y", "auto", CancellationToken.None);
        Result<PriceSeries> badTicker = await history.SeriesAsync("XNAS", "A A", "1Y", "auto", CancellationToken.None);

        (unknown.Error!.Type, otherMarket.Error!.Type, badMarket.Error!.Type, badTicker.Error!.Type)
            .Should().Be((ErrorType.NotFound, ErrorType.NotFound, ErrorType.Validation, ErrorType.Validation));
    }

    [Fact]
    public async Task TheRecentCloses_AreTheLastDaysOfEachStock_AndTheNumberOfDaysIsBounded()
    {
        var reader = new Reader(Bar(10, 12, 10m), Bar(10, 13, 11m), Bar(10, 14, 12m), Bar(10, 15, 13m), Bar(10, 16, 14m));
        PriceHistory history = History(reader, Newest(10, 16));

        IReadOnlyDictionary<Guid, IReadOnlyList<decimal>> recent = await history.RecentAdjustedClosesAsync([_apple.Value, _apple.Value], 3, CancellationToken.None);

        recent.Should().ContainSingle().Which.Value.Should().Equal(12m, 13m, 14m);
        Func<Task> none = () => history.RecentAdjustedClosesAsync([_apple.Value], 0, CancellationToken.None);
        Func<Task> tooMany = () => history.RecentAdjustedClosesAsync([_apple.Value], PriceHistory.MaxRecentDays + 1, CancellationToken.None);
        await none.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await tooMany.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
