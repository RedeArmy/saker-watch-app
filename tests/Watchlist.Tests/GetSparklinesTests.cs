using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.MarketData.Contracts;
using SakerWatch.Watchlist.Application;
using SakerWatch.Watchlist.Application.Features.GetSparklines;
using SakerWatch.Watchlist.Application.Features.Portfolios;
using SakerWatch.Watchlist.Domain.Items;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Tests;

/// <summary>The sparklines of the watchlist over fakes of the repositories and of the market data (ADR-0038).</summary>
public sealed class GetSparklinesTests
{
    private static readonly Guid _user = Guid.CreateVersion7();
    private static readonly DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly StockSummary _apple = new(Guid.CreateVersion7(), "XNAS", "AAPL", "Apple Inc.", "USD", "equity");
    private static readonly StockSummary _coke = new(Guid.CreateVersion7(), "XNYS", "KO", "Coca-Cola", "USD", "equity");
    private static readonly StockSummary _pending = new(Guid.CreateVersion7(), "XNAS", "MSFT", "Microsoft", "USD", "equity");

    private sealed class User(Guid? id) : ICurrentUser
    {
        public Guid? UserId => id;
    }

    private sealed class Items(params StockSummary[] stocks) : IWatchlistRepository
    {
        public Task<WatchlistItem?> FindByStockAsync(Guid userId, Guid stockId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<WatchlistItem>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WatchlistItem>>([.. stocks.Select(stock => WatchlistItem.Restore(WatchlistItemId.Create(Guid.CreateVersion7()), userId, stock.Id, null, null, _now, _now))]);

        public Task<int> CountAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AddOutcome> AddAsync(WatchlistItem item, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UpdateAsync(WatchlistItem item, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RemoveByStockAsync(Guid userId, Guid stockId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Portfolios(Guid? known = null, params Guid[] members) : IPortfolioRepository
    {
        public Task LockOrderAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PortfolioSummary>> ListAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PortfolioSummary?> FindAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken) =>
            Task.FromResult(known == id.Value ? new PortfolioSummary(Portfolio.Create(id, userId, "P", null, 0, _now).Value, members.Length) : null);

        public Task<PortfolioWrite> AddAsync(Portfolio portfolio, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PortfolioWrite> UpdateAsync(Portfolio portfolio, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ReorderAsync(Guid userId, IReadOnlyList<PortfolioId> ordered, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PortfolioMember>> MembersAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PortfolioMember>>([.. members.Select((stock, position) => new PortfolioMember(stock, position, _now))]);

        public Task<MemberAdd> AddMemberAsync(Guid userId, PortfolioId id, Guid stockId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> RemoveMemberAsync(Guid userId, PortfolioId id, Guid stockId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ReorderMembersAsync(Guid userId, PortfolioId id, IReadOnlyList<Guid> orderedStocks, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DeletionPreview> PreviewDeletionAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid userId, PortfolioId id, bool removeOrphans, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Market : IMarketSnapshots
    {
        private readonly StockSummary[] _known = [_apple, _coke, _pending];

        public Task<IReadOnlyDictionary<Guid, StockSummary>> FindManyAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, StockSummary>>(_known.Where(stock => stockIds.Contains(stock.Id)).ToDictionary(stock => stock.Id));

        public Task<IReadOnlyDictionary<Guid, StockSnapshot>> SnapshotsAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class History : IPriceHistory
    {
        public int Days { get; private set; }

        public int Asked { get; private set; }

        public Task<Result<PriceSeries>> SeriesAsync(string market, string ticker, string range, string interval, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<decimal>>> RecentAdjustedClosesAsync(IReadOnlyCollection<Guid> stockIds, int days, CancellationToken cancellationToken)
        {
            (Days, Asked) = (days, stockIds.Count);
            return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<decimal>>>(new Dictionary<Guid, IReadOnlyList<decimal>>
            {
                [_apple.Id] = [10m, 11m, 12m],
                [_coke.Id] = [50m, 49.5m],
            });
        }
    }

    private static GetSparklinesHandler Handler(Items items, Portfolios? portfolios = null, History? history = null, Guid? user = null) =>
        new(new User(user ?? _user), items, portfolios ?? new Portfolios(), new Market(), history ?? new History());

    [Fact]
    public async Task EveryStockOfTheWatchlist_HasALine_OrderedByTicker_AndOneWithoutPricesIsEmpty()
    {
        var history = new History();

        SparklinesView view = (await Handler(new Items(_coke, _pending, _apple), history: history).Handle(new GetSparklinesQuery(30), CancellationToken.None)).Value;

        view.Days.Should().Be(30);
        view.Items.Select(line => line.Stock.Ticker).Should().Equal("AAPL", "KO", "MSFT");
        view.Items.Select(line => line.Points).Should().SatisfyRespectively(
            apple => apple.Should().Equal(10m, 11m, 12m),
            coke => coke.Should().Equal(50m, 49.5m),
            pending => pending.Should().BeEmpty("the first load of the stock is still on the way"));
        (history.Days, history.Asked).Should().Be((30, 3), "one read for all the stocks, however many");
    }

    [Fact]
    public async Task ThePortfolioFilter_KeepsOnlyItsStocks_AndAnotherUsersPortfolioIsNotFound()
    {
        Guid portfolio = Guid.CreateVersion7();
        var items = new Items(_apple, _coke, _pending);
        var history = new History();

        SparklinesView view = (await Handler(items, new Portfolios(portfolio, _coke.Id), history).Handle(new GetSparklinesQuery(30, portfolio), CancellationToken.None)).Value;
        Result<SparklinesView> foreign = await Handler(items, new Portfolios(portfolio, _coke.Id)).Handle(new GetSparklinesQuery(30, Guid.CreateVersion7()), CancellationToken.None);

        view.Items.Select(line => line.Stock.Ticker).Should().Equal("KO");
        history.Asked.Should().Be(1);
        (foreign.Error!.Type, foreign.Error.Code).Should().Be((ErrorType.NotFound, PortfolioErrors.NotFound));
    }

    [Fact]
    public async Task AnEmptyWatchlist_IsAnEmptyAnswer()
    {
        SparklinesView view = (await Handler(new Items()).Handle(new GetSparklinesQuery(30), CancellationToken.None)).Value;

        view.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAUser_ItIsUnauthorized()
    {
        var handler = new GetSparklinesHandler(new User(null), new Items(_apple), new Portfolios(), new Market(), new History());

        Result<SparklinesView> result = await handler.Handle(new GetSparklinesQuery(30), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(30, true)]
    [InlineData(250, true)]
    [InlineData(251, false)]
    public void TheNumberOfDays_IsFiveToTwoHundredAndFifty(int days, bool valid) =>
        new GetSparklinesValidator().Validate(new GetSparklinesQuery(days)).IsValid.Should().Be(valid);
}
