using AwesomeAssertions;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Tests;

public sealed class UnitScopedStockCatalogTests
{
    private sealed class Unit : IUnitOfWork
    {
        public int Begun { get; private set; }

        public int Committed { get; private set; }

        public int RolledBack { get; private set; }

        public Task BeginAsync(CancellationToken cancellationToken)
        {
            Begun++;
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Committed++;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            RolledBack++;
            return Task.CompletedTask;
        }
    }

    private sealed class Catalog(bool fail = false) : IStockCatalog
    {
        public Task<CatalogStock?> FindAsync(StockId id, CancellationToken cancellationToken) => Answer<CatalogStock?>(null);

        public Task<CatalogListing?> FindByKeyAsync(Mic market, Ticker ticker, CancellationToken cancellationToken) =>
            Answer<CatalogListing?>(new CatalogListing(StockId.Create(Guid.CreateVersion7()), market, ticker, "Apple Inc.", CurrencyCode.Parse("USD"), AssetType.Equity, true));

        public Task<IReadOnlyList<CatalogListing>> FindManyAsync(IReadOnlyCollection<StockId> ids, CancellationToken cancellationToken) => Answer<IReadOnlyList<CatalogListing>>([]);

        public Task<IReadOnlyList<StockId>> ListIngestableAsync(Mic market, CancellationToken cancellationToken) => Answer<IReadOnlyList<StockId>>([]);

        private Task<T> Answer<T>(T value) => fail ? throw new InvalidOperationException("the database is down") : Task.FromResult(value);
    }

    [Fact]
    public async Task EveryRead_IsItsOwnShortUnitOfWork_ThatIsCommitted()
    {
        var unit = new Unit();
        var catalog = new UnitScopedStockCatalog(new Catalog(), unit);

        await catalog.FindAsync(StockId.Create(Guid.CreateVersion7()), CancellationToken.None);
        await catalog.FindByKeyAsync(Mic.Parse("XNAS"), Ticker.Parse("AAPL"), CancellationToken.None);
        await catalog.FindManyAsync([], CancellationToken.None);
        await catalog.ListIngestableAsync(Mic.Parse("XNAS"), CancellationToken.None);

        (unit.Begun, unit.Committed, unit.RolledBack).Should().Be((4, 4, 0));
    }

    [Fact]
    public async Task AReadThatThrows_RollsTheUnitBack_AndTheExceptionReachesTheCaller()
    {
        var unit = new Unit();
        var catalog = new UnitScopedStockCatalog(new Catalog(fail: true), unit);

        Func<Task> act = () => catalog.FindByKeyAsync(Mic.Parse("XNAS"), Ticker.Parse("AAPL"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (unit.Committed, unit.RolledBack).Should().Be((0, 1));
    }

    [Fact]
    public async Task TheAnswerOfTheInnerCatalog_IsReturnedUnchanged()
    {
        var catalog = new UnitScopedStockCatalog(new Catalog(), new Unit());

        CatalogListing? found = await catalog.FindByKeyAsync(Mic.Parse("XNAS"), Ticker.Parse("AAPL"), CancellationToken.None);

        found!.Ticker.Value.Should().Be("AAPL");
    }
}
