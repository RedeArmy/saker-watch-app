using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lifecycle;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Tests;

/// <summary>What the operator gets back from the command: an exit code for every way it can end, and nothing left half done (ADR-0039).</summary>
public sealed class LifecycleCommandLineTests
{
    private static readonly StockId _stock = StockId.Create(Guid.NewGuid());

    private sealed class Catalog : IStockCatalog
    {
        public Task<CatalogStock?> FindAsync(StockId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CatalogListing?> FindByKeyAsync(Mic market, Ticker ticker, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<CatalogListing?>(ticker.Value == "ABC" ? new CatalogListing(_stock, market, ticker, "Abc Inc.", CurrencyCode.Parse("USD"), AssetType.Equity, true) : null);
        }

        public Task<IReadOnlyList<CatalogListing>> FindManyAsync(IReadOnlyCollection<StockId> ids, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<StockId>> ListIngestableAsync(Mic market, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Store(bool breaks) : IListingLifecycleStore
    {
        public Task<bool> DelistAsync(StockId stock, DateTimeOffset at, DateOnly on, string source, CancellationToken cancellationToken) =>
            breaks ? throw new InvalidOperationException("the database went away") : Task.FromResult(true);

        public Task<bool> RelistAsync(StockId stock, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<RenameOutcome> RenameAsync(TickerChange change, DateOnly on, string providerSymbol, DateTimeOffset at, CancellationToken cancellationToken) =>
            Task.FromResult(RenameOutcome.Done);

        public Task<int> NoteSymbolNotFoundAsync(StockId stock, DateOnly day, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(1);

        public Task<NotFoundShare> NotFoundShareAsync(StockId stock, DateOnly day, CancellationToken cancellationToken) => Task.FromResult(new NotFoundShare(0, 0));
    }

    private sealed class Unit : IUnitOfWork
    {
        public int Commits { get; private set; }

        public int Rollbacks { get; private set; }

        public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Commits++;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            Rollbacks++;
            return Task.CompletedTask;
        }
    }

    private static (LifecycleCommandLine Line, Unit Unit) Build(bool breaks = false)
    {
        var unit = new Unit();
        var lifecycle = new ListingLifecycle(new Catalog(), new Store(breaks), Options.Create(new IngestionOptions()), new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)), NullLogger<ListingLifecycle>.Instance);
        return (new LifecycleCommandLine(new LifecycleCommandRunner(lifecycle, unit), NullLogger<LifecycleCommandLine>.Instance), unit);
    }

    [Theory]
    [InlineData("delist", "XNAS", "ABC")]
    [InlineData("relist", "XNAS", "ABC")]
    [InlineData("rename", "XNAS", "ABC", "NEWT")]
    public async Task AChangeThatIsMade_ExitsWithZero_AndCommits(params string[] arguments)
    {
        (LifecycleCommandLine line, Unit unit) = Build();

        int exit = await line.RunAsync(arguments, CancellationToken.None);

        (exit, unit.Commits, unit.Rollbacks).Should().Be((LifecycleCommandLine.Done, 1, 0));
    }

    [Fact]
    public async Task AnUnknownStock_ExitsWithOne_AndRollsBack()
    {
        (LifecycleCommandLine line, Unit unit) = Build();

        int exit = await line.RunAsync(["delist", "XNAS", "ZZZ"], CancellationToken.None);

        (exit, unit.Commits, unit.Rollbacks).Should().Be((LifecycleCommandLine.Refused, 0, 1));
    }

    [Theory]
    [InlineData]
    [InlineData("explode", "XNAS", "ABC")]
    [InlineData("delist", "XNAS")]
    public async Task ARequestThatCannotBeRead_ExitsWithTwo_AndTouchesNothing(params string[] arguments)
    {
        (LifecycleCommandLine line, Unit unit) = Build();

        int exit = await line.RunAsync(arguments, CancellationToken.None);

        (exit, unit.Commits, unit.Rollbacks).Should().Be((LifecycleCommandLine.Unreadable, 0, 0));
    }

    [Fact]
    public async Task SomethingThatBreaks_ExitsWithThree_RollsBack_AndNeverLeavesTheProcessWithAnException()
    {
        (LifecycleCommandLine line, Unit unit) = Build(breaks: true);

        int exit = await line.RunAsync(["delist", "XNAS", "ABC"], CancellationToken.None);

        (exit, unit.Commits, unit.Rollbacks).Should().Be((LifecycleCommandLine.Failed, 0, 1));
    }

    [Fact]
    public async Task ACancellation_IsNotAnOutcome()
    {
        (LifecycleCommandLine line, _) = Build();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Func<Task> act = () => line.RunAsync(["delist", "XNAS", "ZZZ"], cancelled.Token);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeAssignableTo<OperationCanceledException>();
    }
}
