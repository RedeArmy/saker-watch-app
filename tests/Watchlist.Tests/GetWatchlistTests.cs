using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.MarketData.Contracts;
using SakerWatch.Watchlist.Application;
using SakerWatch.Watchlist.Application.Features.GetWatchlist;
using SakerWatch.Watchlist.Application.Features.Portfolios;
using SakerWatch.Watchlist.Domain.Items;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Tests;

public sealed class GetWatchlistTests
{
    private static readonly Guid _user = Guid.CreateVersion7();
    private static readonly DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class User(Guid? id) : ICurrentUser
    {
        public Guid? UserId => id;
    }

    private sealed class Items(params WatchlistItem[] items) : IWatchlistRepository
    {
        public Task<WatchlistItem?> FindByStockAsync(Guid userId, Guid stockId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<WatchlistItem>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WatchlistItem>>([.. items.Where(i => i.UserId == userId)]);

        public Task<int> CountAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AddOutcome> AddAsync(WatchlistItem item, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdateAsync(WatchlistItem item, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RemoveByStockAsync(Guid userId, Guid stockId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // The portfolios the list may be filtered by: id to the stocks it holds. Everything else is outside what the list reads.
    private sealed class Portfolios(Dictionary<Guid, Guid[]>? held = null) : IPortfolioRepository
    {
        public Task<IReadOnlyList<PortfolioSummary>> ListAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task LockOrderAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PortfolioSummary?> FindAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken) =>
            Task.FromResult(held is not null && held.TryGetValue(id.Value, out Guid[]? stocks)
                ? new PortfolioSummary(Portfolio.Create(id, userId, "P", null, 0, _now).Value, stocks.Length)
                : null);

        public Task<PortfolioWrite> AddAsync(Portfolio portfolio, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PortfolioWrite> UpdateAsync(Portfolio portfolio, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ReorderAsync(Guid userId, IReadOnlyList<PortfolioId> ordered, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PortfolioMember>> MembersAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PortfolioMember>>([.. held![id.Value].Select((stock, position) => new PortfolioMember(stock, position, _now))]);

        public Task<MemberAdd> AddMemberAsync(Guid userId, PortfolioId id, Guid stockId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RemoveMemberAsync(Guid userId, PortfolioId id, Guid stockId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ReorderMembersAsync(Guid userId, PortfolioId id, IReadOnlyList<Guid> orderedStocks, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DeletionPreview> PreviewDeletionAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid userId, PortfolioId id, bool removeOrphans, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Market(IEnumerable<StockSnapshot> snapshots) : IMarketSnapshots
    {
        private readonly Dictionary<Guid, StockSnapshot> _byId = snapshots.ToDictionary(s => s.Stock.Id);

        public List<int> SnapshotCalls { get; } = [];

        public Task<IReadOnlyDictionary<Guid, StockSummary>> FindManyAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, StockSummary>>(_byId.Where(pair => stockIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value.Stock));

        public Task<IReadOnlyDictionary<Guid, StockSnapshot>> SnapshotsAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken)
        {
            SnapshotCalls.Add(stockIds.Count);
            return Task.FromResult<IReadOnlyDictionary<Guid, StockSnapshot>>(_byId.Where(pair => stockIds.Contains(pair.Key)).ToDictionary());
        }
    }

    private static StockSnapshot Snapshot(
        string ticker, string name, decimal? price, decimal? oneDay = null, decimal? ytd = null, string status = PriceStatuses.Current, string market = "XNAS", string type = "equity")
    {
        var stock = new StockSummary(Guid.CreateVersion7(), market, ticker, name, "USD", type);
        WindowSnapshot[] windows =
        [
            new("1D", oneDay, null, oneDay is null ? "no_data" : null),
            new("1W", null, null, "insufficient_history"),
            new("1M", null, null, "insufficient_history"),
            new("3M", null, null, "insufficient_history"),
            new("6M", null, null, "insufficient_history"),
            new("YTD", ytd, null, ytd is null ? "insufficient_history" : null),
            new("1Y", null, null, "insufficient_history"),
            new("5Y", null, null, "insufficient_history"),
        ];
        return new StockSnapshot(stock, status, price is null ? null : new PriceSnapshot(price.Value, new DateOnly(2026, 10, 16), true), null, windows);
    }

    private static WatchlistItem Item(StockSnapshot snapshot, int minutesAfter = 0) =>
        WatchlistItem.Restore(WatchlistItemId.Create(Guid.CreateVersion7()), _user, snapshot.Stock.Id, null, null, _now.AddMinutes(minutesAfter), _now.AddMinutes(minutesAfter));

    private static GetWatchlistQuery Query(
        string? sort = null, string? order = null, string? market = null, string? assetType = null, string? search = null, string? priceStatus = null, int limit = 50, int offset = 0) =>
        new(sort, order, market, assetType, search, priceStatus, limit, offset);

    private static (GetWatchlistHandler Handler, Market Market) Build(StockSnapshot[] snapshots, Guid? user = null, Func<StockSnapshot, WatchlistItem>? item = null)
    {
        var market = new Market(snapshots);
        Func<StockSnapshot, WatchlistItem> make = item ?? (s => Item(s));
        return (new GetWatchlistHandler(new User(user ?? _user), new Items([.. snapshots.Select(make)]), new Portfolios(), market), market);
    }

    private static async Task<WatchlistPage> RunAsync(GetWatchlistHandler handler, GetWatchlistQuery query) => (await handler.Handle(query, CancellationToken.None)).Value;

    private static readonly StockSnapshot[] _four =
    [
        Snapshot("KO", "Coca-Cola Co.", 60m, oneDay: 0.01m, ytd: 0.10m),
        Snapshot("AAPL", "Apple Inc.", 150m, oneDay: -0.02m, ytd: 0.30m),
        Snapshot("MSFT", "Microsoft", 400m, oneDay: 0.03m),
        Snapshot("NEW", "Newly Listed", null, status: PriceStatuses.Pending),
    ];

    [Fact]
    public async Task ByDefault_ListsEverythingByTickerAscending_WithPriceAndWindows()
    {
        (GetWatchlistHandler handler, _) = Build(_four);

        WatchlistPage page = await RunAsync(handler, Query());

        page.Items.Select(e => e.Stock.Ticker).Should().Equal("AAPL", "KO", "MSFT", "NEW");
        (page.Total, page.Limit, page.Offset).Should().Be((4, 50, 0));
        page.Items[0].Price!.Value.Should().Be(150m);
        page.Items[0].Windows.Should().HaveCount(8);
        page.Items[0].PriceStatus.Should().Be(PriceStatuses.Current);
        page.Items[3].PriceStatus.Should().Be(PriceStatuses.Pending);
    }

    [Theory]
    [InlineData("name", "asc", new[] { "AAPL", "KO", "MSFT", "NEW" })]
    [InlineData("name", "desc", new[] { "NEW", "MSFT", "KO", "AAPL" })]
    [InlineData("ticker", "desc", new[] { "NEW", "MSFT", "KO", "AAPL" })]
    public async Task ByNameOrTicker_FollowsTheOrder(string sort, string order, string[] expected)
    {
        (GetWatchlistHandler handler, _) = Build(_four);

        WatchlistPage page = await RunAsync(handler, Query(sort, order));

        page.Items.Select(e => e.Stock.Ticker).Should().Equal(expected);
    }

    [Fact]
    public async Task ByCreationDate_ListsTheOldestFirstOrTheNewestFirst()
    {
        int minute = 0;
        (GetWatchlistHandler handler, _) = Build(_four, item: s => Item(s, minutesAfter: minute++));

        WatchlistPage oldest = await RunAsync(handler, Query("createdAt", "asc"));
        WatchlistPage newest = await RunAsync(handler, Query("createdAt", "desc"));

        oldest.Items.Select(e => e.Stock.Ticker).Should().Equal("KO", "AAPL", "MSFT", "NEW");
        newest.Items.Select(e => e.Stock.Ticker).Should().Equal("NEW", "MSFT", "AAPL", "KO");
    }

    [Theory]
    [InlineData("asc", new[] { "KO", "AAPL", "MSFT", "NEW" })]
    [InlineData("desc", new[] { "MSFT", "AAPL", "KO", "NEW" })]
    public async Task ByPrice_PutsTheStockWithoutAPriceLast_InEitherOrder(string order, string[] expected)
    {
        (GetWatchlistHandler handler, _) = Build(_four);

        WatchlistPage page = await RunAsync(handler, Query("price", order));

        page.Items.Select(e => e.Stock.Ticker).Should().Equal(expected);
    }

    [Theory]
    [InlineData("1d", "desc", new[] { "MSFT", "KO", "AAPL", "NEW" })]
    [InlineData("1d", "asc", new[] { "AAPL", "KO", "MSFT", "NEW" })]
    [InlineData("ytd", "desc", new[] { "AAPL", "KO", "MSFT", "NEW" })]
    [InlineData("YTD", "asc", new[] { "KO", "AAPL", "MSFT", "NEW" })]
    public async Task ByAWindow_PutsTheStocksWithoutThatWindowLast(string sort, string order, string[] expected)
    {
        // Sort keys are case-sensitive by contract; the lower case ones are the documented spelling.
        (GetWatchlistHandler handler, _) = Build(_four);

        Result<WatchlistPage> result = await handler.Handle(Query(sort.ToLowerInvariant(), order), CancellationToken.None);

        result.Value.Items.Select(e => e.Stock.Ticker).Should().Equal(expected);
    }

    [Fact]
    public async Task EqualValues_BreakTiesByTickerAndMarket_SoThePageIsTheSameEveryTime()
    {
        StockSnapshot[] same =
        [
            Snapshot("ZZZ", "Z", 10m),
            Snapshot("AAA", "A", 10m, market: "XNYS"),
            Snapshot("AAA", "A", 10m),
            Snapshot("MMM", "M", 10m),
        ];
        (GetWatchlistHandler handler, _) = Build(same);

        WatchlistPage page = await RunAsync(handler, Query("price", "desc"));

        page.Items.Select(e => (e.Stock.Ticker, e.Stock.Market)).Should().Equal(("AAA", "XNAS"), ("AAA", "XNYS"), ("MMM", "XNAS"), ("ZZZ", "XNAS"));
    }

    [Fact]
    public async Task Paging_ReturnsTheSliceAndTheTotalOfAllPages()
    {
        (GetWatchlistHandler handler, _) = Build(_four);

        WatchlistPage second = await RunAsync(handler, Query(limit: 2, offset: 2));
        WatchlistPage beyond = await RunAsync(handler, Query(limit: 2, offset: 10));

        second.Items.Select(e => e.Stock.Ticker).Should().Equal("MSFT", "NEW");
        (second.Total, second.Limit, second.Offset).Should().Be((4, 2, 2));
        (beyond.Items, beyond.Total).Should().BeEquivalentTo((Array.Empty<WatchlistEntry>(), 4));
    }

    [Fact]
    public async Task Filters_NarrowTheListAndTheTotal()
    {
        StockSnapshot[] mixed =
        [
            Snapshot("KO", "Coca-Cola Co.", 60m, market: "XNYS"),
            Snapshot("AAPL", "Apple Inc.", 150m),
            Snapshot("SPY", "SPDR S&P 500", 500m, market: "ARCX", type: "etf"),
            Snapshot("AAL", "American Airlines", 12m),
        ];
        (GetWatchlistHandler handler, _) = Build(mixed);

        WatchlistPage nyse = await RunAsync(handler, Query(market: "xnyse".Remove(4)));
        WatchlistPage etf = await RunAsync(handler, Query(assetType: "etf"));
        WatchlistPage text = await RunAsync(handler, Query(search: "  AMERICAN  "));
        WatchlistPage byTicker = await RunAsync(handler, Query(search: "aa"));
        WatchlistPage none = await RunAsync(handler, Query(search: "zzz"));

        nyse.Items.Select(e => e.Stock.Ticker).Should().Equal("KO");
        etf.Items.Select(e => e.Stock.Ticker).Should().Equal("SPY");
        text.Items.Select(e => e.Stock.Ticker).Should().Equal("AAL");
        byTicker.Items.Select(e => e.Stock.Ticker).Should().Equal("AAL", "AAPL");
        (none.Items, none.Total).Should().BeEquivalentTo((Array.Empty<WatchlistEntry>(), 0));
        etf.Total.Should().Be(1);
    }

    [Fact]
    public async Task ThePriceStatusFilter_KeepsOnlyThoseStocks_AndCountsThemInTheTotal()
    {
        StockSnapshot[] stocks =
        [
            Snapshot("AAA", "A", 1m),
            Snapshot("BBB", "B", 2m, status: PriceStatuses.Stale),
            Snapshot("CCC", "C", null, status: PriceStatuses.Pending),
            Snapshot("DDD", "D", 4m, status: PriceStatuses.Stale),
        ];
        (GetWatchlistHandler handler, _) = Build(stocks);

        WatchlistPage page = await RunAsync(handler, Query(priceStatus: PriceStatuses.Stale));

        page.Items.Select(e => e.Stock.Ticker).Should().Equal("BBB", "DDD");
        page.Total.Should().Be(2);
    }

    [Fact]
    public async Task TheDelistedFilter_ListsOnlyTheStocksThatNoLongerTrade_WithTheMomentTheyStopped()
    {
        var delistedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        StockSnapshot[] stocks =
        [
            Snapshot("AAA", "A", 1m),
            Snapshot("BBB", "B", 2m, status: PriceStatuses.Delisted) with { DelistedAt = delistedAt },
        ];
        (GetWatchlistHandler handler, _) = Build(stocks);

        WatchlistPage page = await RunAsync(handler, Query(priceStatus: PriceStatuses.Delisted));

        page.Items.Select(e => (e.Stock.Ticker, e.PriceStatus, e.DelistedAt, e.StaleSince)).Should().Equal(("BBB", PriceStatuses.Delisted, (DateTimeOffset?)delistedAt, (DateTimeOffset?)null));
        page.Items[0].Price.Should().NotBeNull("the last known price is still shown");
        new GetWatchlistValidator().Validate(Query(priceStatus: PriceStatuses.Delisted)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ASortByNameReadsTheMarketDataOnlyForThePage()
    {
        (GetWatchlistHandler handler, Market market) = Build(_four);

        await RunAsync(handler, Query("name", limit: 2));

        market.SnapshotCalls.Should().Equal(2);
    }

    [Fact]
    public async Task ASortByAWindowOrAPriceStatusFilter_ReadsTheMarketDataOfEveryMatchingStockOnce()
    {
        (GetWatchlistHandler handler, Market market) = Build(_four);

        await RunAsync(handler, Query("1w", limit: 1));
        await RunAsync(handler, Query(priceStatus: PriceStatuses.Current, limit: 1));

        market.SnapshotCalls.Should().Equal(4, 4);
    }

    [Fact]
    public async Task TheCallersWatchlist_NeverIncludesAnotherUsersStocks()
    {
        StockSnapshot mine = Snapshot("MINE", "Mine", 1m);
        StockSnapshot theirs = Snapshot("THEIRS", "Theirs", 1m);
        var other = Guid.CreateVersion7();
        var market = new Market([mine, theirs]);
        var items = new Items(Item(mine), WatchlistItem.Restore(WatchlistItemId.Create(Guid.CreateVersion7()), other, theirs.Stock.Id, null, null, _now, _now));
        var handler = new GetWatchlistHandler(new User(_user), items, new Portfolios(), market);

        WatchlistPage page = await RunAsync(handler, Query());

        page.Items.Select(e => e.Stock.Ticker).Should().Equal("MINE");
    }

    [Fact]
    public async Task AnItemWhoseStockTheCatalogCannotFind_IsLeftOut()
    {
        StockSnapshot known = Snapshot("KNOWN", "Known", 1m);
        StockSnapshot lost = Snapshot("LOST", "Lost", 1m);
        var market = new Market([known]);
        var handler = new GetWatchlistHandler(new User(_user), new Items(Item(known), Item(lost)), new Portfolios(), market);

        WatchlistPage page = await RunAsync(handler, Query());

        page.Items.Select(e => e.Stock.Ticker).Should().Equal("KNOWN");
        page.Total.Should().Be(1);
    }

    [Fact]
    public async Task AnEntry_CarriesTheItemsOwnFields()
    {
        StockSnapshot ko = Snapshot("KO", "Coca-Cola Co.", 60m);
        var item = WatchlistItem.Restore(WatchlistItemId.Create(Guid.CreateVersion7()), _user, ko.Stock.Id, "Esperar", 70.5m, _now, _now.AddHours(1));
        var handler = new GetWatchlistHandler(new User(_user), new Items(item), new Portfolios(), new Market([ko]));

        WatchlistEntry entry = (await RunAsync(handler, Query())).Items.Single();

        (entry.Id, entry.Notes, entry.TargetPrice, entry.CreatedAt, entry.UpdatedAt).Should().Be((item.Id.Value, "Esperar", 70.5m, _now, _now.AddHours(1)));
    }

    [Fact]
    public async Task ByPortfolio_ListsOnlyItsStocks_AndTheTotalCountsOnlyThose()
    {
        var market = new Market(_four);
        var portfolioId = Guid.CreateVersion7();
        var handler = new GetWatchlistHandler(
            new User(_user), new Items([.. _four.Select(s => Item(s))]), new Portfolios(new() { [portfolioId] = [_four[1].Stock.Id, _four[2].Stock.Id] }), market);

        WatchlistPage page = await RunAsync(handler, Query() with { PortfolioId = portfolioId });
        WatchlistPage filtered = await RunAsync(handler, Query(search: "micro") with { PortfolioId = portfolioId });

        page.Items.Select(e => e.Stock.Ticker).Should().Equal("AAPL", "MSFT");
        page.Total.Should().Be(2);
        filtered.Items.Select(e => e.Stock.Ticker).Should().Equal("MSFT");
        market.SnapshotCalls.Should().Equal(2, 1);
    }

    [Fact]
    public async Task ByAPortfolioThatIsNotTheCallers_Is404_AndNothingIsRead()
    {
        var market = new Market(_four);
        var handler = new GetWatchlistHandler(new User(_user), new Items([.. _four.Select(s => Item(s))]), new Portfolios(), market);

        Result<WatchlistPage> result = await handler.Handle(Query() with { PortfolioId = Guid.CreateVersion7() }, CancellationToken.None);

        result.Error!.Code.Should().Be(PortfolioErrors.NotFound);
        market.SnapshotCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEmptyWatchlist_IsAnEmptyPage_AndAnAnonymousCallerIsRefused()
    {
        var market = new Market([]);
        var empty = new GetWatchlistHandler(new User(_user), new Items(), new Portfolios(), market);
        var anonymous = new GetWatchlistHandler(new User(null), new Items(), new Portfolios(), market);

        WatchlistPage page = await RunAsync(empty, Query());
        Result<WatchlistPage> refused = await anonymous.Handle(Query(), CancellationToken.None);

        (page.Items, page.Total).Should().BeEquivalentTo((Array.Empty<WatchlistEntry>(), 0));
        refused.Error!.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Theory]
    [InlineData(0, 0, "Limit")]
    [InlineData(101, 0, "Limit")]
    [InlineData(10, -1, "Offset")]
    public void TheValidator_RefusesAPageOutOfRange(int limit, int offset, string field) =>
        new GetWatchlistValidator().Validate(Query(limit: limit, offset: offset)).Errors.Select(e => e.PropertyName).Should().Contain(field);

    [Theory]
    [InlineData("signal")]
    [InlineData("TICKER")]
    [InlineData("price; drop")]
    public void TheValidator_RefusesASortItDoesNotOffer_IncludingTheSignalUntilTheAnalysisExists(string sort) =>
        new GetWatchlistValidator().Validate(Query(sort)).Errors.Select(e => e.PropertyName).Should().Contain("Sort");

    [Fact]
    public void TheValidator_AcceptsEveryDocumentedSortAndFilter_AndRefusesTheRest()
    {
        var validator = new GetWatchlistValidator();

        foreach (string sort in WatchlistSort.Keys)
        {
            validator.Validate(Query(sort, "desc", "XNAS", "etf", "apple", PriceStatuses.Stale, 100, 5)).IsValid.Should().BeTrue(sort);
        }

        validator.Validate(Query(order: "up")).Errors.Select(e => e.PropertyName).Should().Contain("Order");
        validator.Validate(Query(assetType: "stock")).Errors.Select(e => e.PropertyName).Should().Contain("AssetType");
        validator.Validate(Query(priceStatus: "fresh")).Errors.Select(e => e.PropertyName).Should().Contain("PriceStatus");
        validator.Validate(Query(market: "XNASX")).Errors.Select(e => e.PropertyName).Should().Contain("Market");
        validator.Validate(Query(search: new string('x', 41))).Errors.Select(e => e.PropertyName).Should().Contain("Search");
    }
}
