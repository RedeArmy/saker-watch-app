using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Contracts;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Performance;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Tests;

public sealed class MarketSnapshotsTests
{
    // Saturday 2026-10-17 12:00 UTC. The Friday session (the 16th) settled at 23:00 UTC that day.
    private static readonly DateTimeOffset _now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly IExchangeCalendar _calendar = new UsEquityCalendar(Mic.Parse("XNYS"));

    private sealed class Catalog(params CatalogListing[] listings) : IStockCatalog
    {
        public int Calls { get; private set; }

        public Task<CatalogStock?> FindAsync(StockId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CatalogListing?> FindByKeyAsync(Mic market, Ticker ticker, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CatalogListing>> FindManyAsync(IReadOnlyCollection<StockId> ids, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<CatalogListing>>([.. listings.Where(l => ids.Contains(l.Id))]);
        }

        public Task<IReadOnlyList<StockId>> ListIngestableAsync(Mic market, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Performance(Dictionary<StockId, PerformanceReport> reports) : IPerformanceReader
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyDictionary<StockId, PerformanceReport>> ReadAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<StockId, PerformanceReport>>(stocks.ToDictionary(id => id, id => reports.GetValueOrDefault(id) ?? PerformanceCalculator.NoData()));
        }
    }

    private sealed class Bars(Dictionary<StockId, LatestBar> bars) : ILatestBars
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyDictionary<StockId, LatestBar>> ReadAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<StockId, LatestBar>>(bars.Where(pair => stocks.Contains(pair.Key)).ToDictionary());
        }
    }

    private sealed class Failed(params StockId[] ids) : IIngestionStatuses
    {
        public Task<IReadOnlySet<StockId>> FailedAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<StockId>>(ids.Where(stocks.Contains).ToHashSet());
    }

    private static CatalogListing Listing(string ticker = "KO", string market = "XNYS", AssetType type = AssetType.Equity) =>
        new(StockId.Create(Guid.CreateVersion7()), Mic.Parse(market), Ticker.Parse(ticker), $"{ticker} Inc.", CurrencyCode.Parse("USD"), type, true);

    private static PerformanceReport Report(decimal latestClose, DateOnly latestDate, params (PerformanceWindow Window, decimal Baseline)[] baselines)
    {
        PriceSample latest = PriceSample.Create(latestDate, latestClose).Value;
        IReadOnlyDictionary<PerformanceWindow, DateOnly> targets = PerformanceCalculator.TargetDates(latestDate, _calendar);
        var map = new Dictionary<DateOnly, PriceSample?>();
        foreach ((PerformanceWindow window, decimal baseline) in baselines)
        {
            map[targets[window]] = PriceSample.Create(targets[window], baseline).Value;
        }

        return PerformanceCalculator.Calculate(latest, map, _calendar);
    }

    private static MarketSnapshots Build(Catalog catalog, Performance performance, Bars bars, Failed? failed = null) =>
        new(catalog, performance, bars, failed ?? new Failed(), ExchangeCalendars.Default(), Options.Create(new IngestionOptions()), new FakeTimeProvider(_now));

    [Fact]
    public async Task ACurrentStock_ShowsTheRawClose_ItsStatus_AndTheWindowsFromTheCalculator()
    {
        CatalogListing ko = Listing();
        var friday = new DateOnly(2026, 10, 16);
        var catalog = new Catalog(ko);
        var performance = new Performance(new() { [ko.Id] = Report(110m, friday, (PerformanceWindow.OneDay, 100m), (PerformanceWindow.OneWeek, 110m)) });
        var bars = new Bars(new() { [ko.Id] = new LatestBar(friday, 440m, IsFinal: true) });

        StockSnapshot snapshot = (await Build(catalog, performance, bars).SnapshotsAsync([ko.Id.Value], CancellationToken.None))[ko.Id.Value];

        snapshot.Stock.Should().Be(new StockSummary(ko.Id.Value, "XNYS", "KO", "KO Inc.", "USD", "equity"));
        snapshot.PriceStatus.Should().Be(PriceStatuses.Current);
        snapshot.Price.Should().Be(new PriceSnapshot(440m, friday, true), "the price shown is the close as traded, not the adjusted one the windows use");
        snapshot.StaleSince.Should().BeNull();
        snapshot.Windows.Select(w => w.Window).Should().Equal("1D", "1W", "1M", "3M", "6M", "YTD", "1Y", "5Y");
        snapshot.Windows[0].Should().Be(new WindowSnapshot("1D", 0.1m, new DateOnly(2026, 10, 15), null));
        snapshot.Windows[1].Should().Be(new WindowSnapshot("1W", 0m, new DateOnly(2026, 10, 9), null));
        snapshot.Windows.Skip(2).Should().OnlyContain(w => w.Change == null && w.BaselineDate == null && w.Reason == "insufficient_history");
    }

    [Fact]
    public async Task AStockMissingASessionThatSettled_IsStale_SinceThatSessionSettled_AndKeepsItsLastPrice()
    {
        CatalogListing ko = Listing();
        var thursday = new DateOnly(2026, 10, 15);
        var snapshots = Build(
            new Catalog(ko),
            new Performance(new() { [ko.Id] = Report(110m, thursday, (PerformanceWindow.OneDay, 100m)) }),
            new Bars(new() { [ko.Id] = new LatestBar(thursday, 440m, true) }));

        StockSnapshot snapshot = (await snapshots.SnapshotsAsync([ko.Id.Value], CancellationToken.None))[ko.Id.Value];

        snapshot.PriceStatus.Should().Be(PriceStatuses.Stale);
        snapshot.StaleSince.Should().Be(new DateTimeOffset(2026, 10, 16, 23, 0, 0, TimeSpan.Zero), "Friday's session closed at 20:00 UTC and settled three hours later");
        snapshot.Price!.Value.Should().Be(440m);
    }

    [Fact]
    public async Task AStockWithNoPrice_IsPending_OrUnavailableOnceLoadingFailedForGood_WithEveryWindowSayingNoData()
    {
        CatalogListing fresh = Listing("NEW");
        CatalogListing broken = Listing("BAD");
        var snapshots = Build(new Catalog(fresh, broken), new Performance([]), new Bars([]), new Failed(broken.Id));

        IReadOnlyDictionary<Guid, StockSnapshot> found = await snapshots.SnapshotsAsync([fresh.Id.Value, broken.Id.Value], CancellationToken.None);

        found[fresh.Id.Value].PriceStatus.Should().Be(PriceStatuses.Pending);
        found[broken.Id.Value].PriceStatus.Should().Be(PriceStatuses.Unavailable);
        found.Values.Should().OnlyContain(s => s.Price == null && s.StaleSince == null && s.Windows.Count == 8 && s.Windows.All(w => w.Reason == "no_data" && w.Change == null));
    }

    [Fact]
    public async Task AStockThatHasAPrice_IsNeverUnavailable_EvenIfALaterLoadFailed()
    {
        CatalogListing ko = Listing();
        var thursday = new DateOnly(2026, 10, 15);
        var snapshots = Build(
            new Catalog(ko),
            new Performance(new() { [ko.Id] = Report(110m, thursday) }),
            new Bars(new() { [ko.Id] = new LatestBar(thursday, 440m, true) }),
            new Failed(ko.Id));

        (await snapshots.SnapshotsAsync([ko.Id.Value], CancellationToken.None))[ko.Id.Value].PriceStatus.Should().Be(PriceStatuses.Stale);
    }

    [Fact]
    public async Task EveryReason_IsMappedToItsStableText()
    {
        CatalogListing ko = Listing();
        var friday = new DateOnly(2026, 10, 16);
        // 1D baseline two sessions back: a hole. 1W has one. The rest are younger than the history.
        var report = Report(110m, friday, (PerformanceWindow.OneWeek, 100m));
        var snapshots = Build(new Catalog(ko), new Performance(new() { [ko.Id] = report }), new Bars(new() { [ko.Id] = new LatestBar(friday, 110m, false) }));

        StockSnapshot snapshot = (await snapshots.SnapshotsAsync([ko.Id.Value], CancellationToken.None))[ko.Id.Value];

        snapshot.Price!.IsFinal.Should().BeFalse();
        snapshot.Windows.Single(w => w.Window == "1D").Reason.Should().Be("insufficient_history");
        snapshot.Windows.Single(w => w.Window == "1W").Change.Should().Be(0.1m);
    }

    [Fact]
    public async Task AHoleAndAMissingCalendar_AreMappedToTheirReasons()
    {
        CatalogListing ko = Listing();
        var friday = new DateOnly(2026, 10, 16);
        PriceSample latest = PriceSample.Create(friday, 110m).Value;
        IReadOnlyDictionary<PerformanceWindow, DateOnly> targets = PerformanceCalculator.TargetDates(friday, calendar: null);
        // The close nearest the one-week target is twelve days before it: a hole, not a short history.
        var map = new Dictionary<DateOnly, PriceSample?> { [targets[PerformanceWindow.OneWeek]] = PriceSample.Create(targets[PerformanceWindow.OneWeek].AddDays(-12), 100m).Value };
        PerformanceReport report = PerformanceCalculator.Calculate(latest, map, calendar: null);
        var snapshots = Build(new Catalog(ko), new Performance(new() { [ko.Id] = report }), new Bars(new() { [ko.Id] = new LatestBar(friday, 110m, true) }));

        StockSnapshot snapshot = (await snapshots.SnapshotsAsync([ko.Id.Value], CancellationToken.None))[ko.Id.Value];

        snapshot.Windows.Single(w => w.Window == "1D").Reason.Should().Be("calendar_unavailable");
        snapshot.Windows.Single(w => w.Window == "1W").Reason.Should().Be("history_gap");
    }

    [Fact]
    public async Task AllTheStocksAreReadInOneCallToEachSource_NoMatterHowMany()
    {
        CatalogListing[] listings = [.. Enumerable.Range(0, 40).Select(i => Listing($"T{i:D2}"))];
        var catalog = new Catalog(listings);
        var performance = new Performance([]);
        var bars = new Bars([]);

        IReadOnlyDictionary<Guid, StockSnapshot> found = await Build(catalog, performance, bars).SnapshotsAsync([.. listings.Select(l => l.Id.Value), .. listings.Select(l => l.Id.Value)], CancellationToken.None);

        found.Should().HaveCount(40);
        (catalog.Calls, performance.Calls, bars.Calls).Should().Be((1, 1, 1));
    }

    [Fact]
    public async Task AnIdTheCatalogDoesNotHave_IsLeftOut_AndAnEmptyAskReadsNothingElse()
    {
        CatalogListing ko = Listing();
        var performance = new Performance([]);
        MarketSnapshots snapshots = Build(new Catalog(ko), performance, new Bars([]));

        IReadOnlyDictionary<Guid, StockSnapshot> found = await snapshots.SnapshotsAsync([ko.Id.Value, Guid.CreateVersion7()], CancellationToken.None);
        IReadOnlyDictionary<Guid, StockSnapshot> none = await snapshots.SnapshotsAsync([Guid.CreateVersion7()], CancellationToken.None);

        found.Keys.Should().Equal(ko.Id.Value);
        none.Should().BeEmpty();
        performance.Calls.Should().Be(1, "the second ask found nothing in the catalog, so nothing else was read");
    }

    [Fact]
    public async Task FindMany_ReturnsOnlyTheCatalogDetails_WithTheAssetTypeAsText()
    {
        CatalogListing etf = Listing("SPY", "ARCX", AssetType.Etf);
        CatalogListing other = Listing("ADR", "XNYS", AssetType.Other);
        var performance = new Performance([]);
        MarketSnapshots snapshots = Build(new Catalog(etf, other), performance, new Bars([]));

        IReadOnlyDictionary<Guid, StockSummary> found = await snapshots.FindManyAsync([etf.Id.Value, other.Id.Value], CancellationToken.None);

        found[etf.Id.Value].AssetType.Should().Be("etf");
        found[other.Id.Value].AssetType.Should().Be("other");
        performance.Calls.Should().Be(0, "the catalog alone is enough to list names");
    }
}
