using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lifecycle;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Tests;

/// <summary>The life of a listing over fakes of the catalog and the store (ADR-0039).</summary>
public sealed class ListingLifecycleTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly StockId _stock = StockId.Create(Guid.NewGuid());

    private sealed class Catalog(string current = "FB") : IStockCatalog
    {
        public Task<CatalogStock?> FindAsync(StockId id, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogStock?>(id == _stock ? new CatalogStock(_stock, Mic.Parse("XNAS"), Ticker.Parse(current), true) : null);

        public Task<CatalogListing?> FindByKeyAsync(Mic market, Ticker ticker, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogListing?>(market.Value == "XNAS" && ticker.Value is "FB" or "META"
                ? new CatalogListing(_stock, market, Ticker.Parse(current), "Meta Platforms", CurrencyCode.Parse("USD"), AssetType.Equity, true)
                : null);

        public Task<IReadOnlyList<CatalogListing>> FindManyAsync(IReadOnlyCollection<StockId> ids, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<StockId>> ListIngestableAsync(Mic market, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Store : IListingLifecycleStore
    {
        public bool DelistChanges { get; set; } = true;

        public bool RelistChanges { get; set; } = true;

        public RenameOutcome Rename { get; set; } = RenameOutcome.Done;

        public int Days { get; set; }

        public NotFoundShare Share { get; set; } = new(1, 100);

        public List<string> Calls { get; } = [];

        public Task<bool> DelistAsync(StockId stock, DateTimeOffset at, DateOnly on, string source, CancellationToken cancellationToken)
        {
            Calls.Add($"delist {on:yyyy-MM-dd} {source}");
            return Task.FromResult(DelistChanges);
        }

        public Task<bool> RelistAsync(StockId stock, DateTimeOffset at, CancellationToken cancellationToken)
        {
            Calls.Add("relist");
            return Task.FromResult(RelistChanges);
        }

        public Task<RenameOutcome> RenameAsync(TickerChange change, DateOnly on, string providerSymbol, DateTimeOffset at, CancellationToken cancellationToken)
        {
            Calls.Add($"rename {change.Old}>{change.New} {on:yyyy-MM-dd} {providerSymbol}");
            return Task.FromResult(Rename);
        }

        public Task<int> NoteSymbolNotFoundAsync(StockId stock, DateOnly day, DateTimeOffset at, CancellationToken cancellationToken)
        {
            Calls.Add($"note {day:yyyy-MM-dd}");
            return Task.FromResult(Days);
        }

        public Task<NotFoundShare> NotFoundShareAsync(StockId stock, DateOnly since, CancellationToken cancellationToken)
        {
            Calls.Add($"share since {since:yyyy-MM-dd}");
            return Task.FromResult(Share);
        }
    }

    private static ListingLifecycle Lifecycle(Store store, string ticker = "FB", int limit = 5) =>
        new(new Catalog(ticker), store, ExchangeCalendars.Default(), Options.Create(new IngestionOptions { DelistAfterNotFoundDays = limit }), new FakeTimeProvider(_now), NullLogger<ListingLifecycle>.Instance);

    [Theory]
    [InlineData(30, 100, false)]
    [InlineData(31, 100, false)]
    [InlineData(29, 100, true)]
    [InlineData(3, 10, false)]
    [InlineData(2, 10, true)]
    public async Task Ingestion_WhenAThirdOfTheMarketIsUnknownAtOnce_ItIsAProviderFaultAndNothingIsDelisted(int inStreak, int active, bool delists)
    {
        var store = new Store { Days = 5, Share = new NotFoundShare(inStreak, active) };

        bool delisted = await Lifecycle(store, limit: 5).NoteSymbolNotFoundAsync(_stock, new DateOnly(2026, 10, 19), CancellationToken.None);

        delisted.Should().Be(delists);
        store.Calls.Contains("delist 2026-10-19 ingestion").Should().Be(delists);
    }

    [Theory]
    [InlineData("2026-10-19", "2026-10-16")] // a Monday: the session before is Friday, not Sunday
    [InlineData("2026-10-20", "2026-10-19")]
    [InlineData("2026-11-27", "2026-11-25")] // the day after Thanksgiving: Wednesday, the Thursday was a holiday
    public async Task Ingestion_AStreakIsStillGoingWhenItReachedTheSessionBefore_NotTheCalendarDayBefore(string day, string since)
    {
        var store = new Store { Days = 5, Share = new NotFoundShare(0, 100) };

        await Lifecycle(store, limit: 5).NoteSymbolNotFoundAsync(_stock, DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);

        store.Calls.Should().Contain($"share since {since}");
    }

    [Fact]
    public async Task Ingestion_InAMarketTooSmallForTheShareToMean_JudgesOneListingAtATime()
    {
        // Two of three listings is 67 %, but a market of three is not evidence of a provider fault.
        var store = new Store { Days = 5, Share = new NotFoundShare(2, 3) };

        (await Lifecycle(store, limit: 5).NoteSymbolNotFoundAsync(_stock, new DateOnly(2026, 10, 19), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Ingestion_BelowTheLimit_DoesNotEvenLookAtTheMarket()
    {
        var store = new Store { Days = 3, Share = new NotFoundShare(100, 100) };

        (await Lifecycle(store, limit: 5).NoteSymbolNotFoundAsync(_stock, new DateOnly(2026, 10, 19), CancellationToken.None)).Should().BeFalse();
        store.Calls.Should().Equal("note 2026-10-19");
    }

    [Fact]
    public async Task Delist_MarksTheStockAtTheDayAsked_OrToday()
    {
        var store = new Store();

        LifecycleChange given = (await Lifecycle(store).DelistAsync("XNAS", "FB", new DateOnly(2026, 10, 1), CancellationToken.None)).Value;
        LifecycleChange today = (await Lifecycle(store).DelistAsync("xnas", "fb", null, CancellationToken.None)).Value;

        given.Should().Be(new LifecycleChange("XNAS", "FB", Changed: true));
        today.Should().Be(given);
        store.Calls.Should().Equal("delist 2026-10-01 operator", "delist 2026-10-19 operator");
    }

    [Fact]
    public async Task Delist_OfAStockAlreadyDelisted_IsNotAnErrorAndSaysNothingChanged()
    {
        var store = new Store { DelistChanges = false };

        Result<LifecycleChange> result = await Lifecycle(store).DelistAsync("XNAS", "FB", null, CancellationToken.None);

        result.Value.Changed.Should().BeFalse();
    }

    [Fact]
    public async Task Relist_BringsItBack_AndAStockThatWasNotDelistedIsUnchanged()
    {
        var store = new Store();
        LifecycleChange back = (await Lifecycle(store).RelistAsync("XNAS", "FB", CancellationToken.None)).Value;
        store.RelistChanges = false;
        LifecycleChange again = (await Lifecycle(store).RelistAsync("XNAS", "FB", CancellationToken.None)).Value;

        (back.Changed, again.Changed).Should().Be((true, false));
    }

    [Fact]
    public async Task Rename_GivesTheNewTickerAndTheProviderSymbol_AndTheDay()
    {
        var store = new Store();

        LifecycleChange change = (await Lifecycle(store).RenameAsync("XNAS", "FB", "meta", new DateOnly(2022, 6, 9), null, CancellationToken.None)).Value;
        await Lifecycle(store).RenameAsync("XNAS", "FB", "META", null, "META.O", CancellationToken.None);

        change.Should().Be(new LifecycleChange("XNAS", "META", Changed: true));
        store.Calls.Should().Equal("rename FB>META 2022-06-09 META", "rename FB>META 2026-10-19 META.O");
    }

    [Fact]
    public async Task Rename_ToTheTickerItHas_ToATakenOne_FromOneItNoLongerHas_OrOfAnUnknownStock_IsRefused()
    {
        var taken = new Store { Rename = RenameOutcome.TickerTaken };
        var gone = new Store { Rename = RenameOutcome.StockNotFound };

        Result<LifecycleChange> same = await Lifecycle(new Store(), ticker: "META").RenameAsync("XNAS", "META", "META", null, null, CancellationToken.None);
        Result<LifecycleChange> clash = await Lifecycle(taken).RenameAsync("XNAS", "FB", "META", null, null, CancellationToken.None);
        Result<LifecycleChange> lost = await Lifecycle(gone).RenameAsync("XNAS", "FB", "META", null, null, CancellationToken.None);
        Result<LifecycleChange> alreadyRenamed = await Lifecycle(new Store(), ticker: "META").RenameAsync("XNAS", "FB", "NEWT", null, null, CancellationToken.None);
        Result<LifecycleChange> unknown = await Lifecycle(new Store()).RenameAsync("XNAS", "ZZZ", "NEWT", null, null, CancellationToken.None);
        Result<LifecycleChange> badTicker = await Lifecycle(new Store()).RenameAsync("XNAS", "FB", "not a ticker", null, null, CancellationToken.None);

        (same.Error!.Code, clash.Error!.Code, lost.Error!.Code, alreadyRenamed.Error!.Code, unknown.Error!.Code, badTicker.Error!.Type)
            .Should().Be((ListingLifecycle.SameTickerCode, ListingLifecycle.TickerTakenCode, ListingLifecycle.StockNotFoundCode, ListingLifecycle.TickerChangedCode, ListingLifecycle.StockNotFoundCode, ErrorType.Validation));
    }

    [Fact]
    public async Task AnUnknownStock_OrABadMarket_IsRefusedForEveryChange()
    {
        ListingLifecycle lifecycle = Lifecycle(new Store());

        Result<LifecycleChange> delist = await lifecycle.DelistAsync("XNAS", "ZZZ", null, CancellationToken.None);
        Result<LifecycleChange> relist = await lifecycle.RelistAsync("XNAS", "ZZZ", CancellationToken.None);
        Result<LifecycleChange> market = await lifecycle.DelistAsync("??", "FB", null, CancellationToken.None);

        (delist.Error!.Code, relist.Error!.Code, market.Error!.Type).Should().Be((ListingLifecycle.StockNotFoundCode, ListingLifecycle.StockNotFoundCode, ErrorType.Validation));
    }

    [Fact]
    public async Task Ingestion_WaitsUntilTheDaysInARowReachTheLimit_ThenDelists()
    {
        var store = new Store();
        ListingLifecycle lifecycle = Lifecycle(store, limit: 5);
        var day = new DateOnly(2026, 10, 19);

        store.Days = 4;
        bool waiting = await lifecycle.NoteSymbolNotFoundAsync(_stock, day, CancellationToken.None);
        store.Days = 5;
        bool delisted = await lifecycle.NoteSymbolNotFoundAsync(_stock, day.AddDays(1), CancellationToken.None);

        (waiting, delisted).Should().Be((false, true));
        store.Calls.Should().Equal("note 2026-10-19", "note 2026-10-20", "share since 2026-10-19", "delist 2026-10-20 ingestion");
    }

    [Fact]
    public async Task Ingestion_DelistingAStockAlreadyDelisted_ChangesNothing()
    {
        var store = new Store { Days = 9, DelistChanges = false };

        bool delisted = await Lifecycle(store, limit: 5).NoteSymbolNotFoundAsync(_stock, new DateOnly(2026, 10, 19), CancellationToken.None);

        delisted.Should().BeFalse();
    }

    [Fact]
    public async Task TheRunner_CommitsWhenTheChangeIsMade_AndRollsBackWhenItIsRefused()
    {
        var unit = new CountingUnit();
        var runner = new LifecycleCommandRunner(Lifecycle(new Store()), unit);

        await runner.RunAsync(LifecycleCommand.Parse(["delist", "XNAS", "FB"]).Value, CancellationToken.None);
        await runner.RunAsync(LifecycleCommand.Parse(["delist", "XNAS", "ZZZ"]).Value, CancellationToken.None);

        (unit.Begins, unit.Commits, unit.Rollbacks).Should().Be((2, 1, 1));
    }

    [Fact]
    public async Task TheRunner_RollsBackAndRethrows_WhenSomethingBreaks()
    {
        var unit = new CountingUnit();
        var runner = new LifecycleCommandRunner(new ListingLifecycle(new BrokenCatalog(), new Store(), ExchangeCalendars.Default(), Options.Create(new IngestionOptions()), new FakeTimeProvider(_now), NullLogger<ListingLifecycle>.Instance), unit);

        Func<Task> act = () => runner.RunAsync(LifecycleCommand.Parse(["delist", "XNAS", "FB"]).Value, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (unit.Commits, unit.Rollbacks).Should().Be((0, 1));
    }

    private sealed class CountingUnit : IUnitOfWork
    {
        public int Begins { get; private set; }

        public int Commits { get; private set; }

        public int Rollbacks { get; private set; }

        public Task BeginAsync(CancellationToken cancellationToken)
        {
            Begins++;
            return Task.CompletedTask;
        }

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

    private sealed class BrokenCatalog : IStockCatalog
    {
        public Task<CatalogStock?> FindAsync(StockId id, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");

        public Task<CatalogListing?> FindByKeyAsync(Mic market, Ticker ticker, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");

        public Task<IReadOnlyList<CatalogListing>> FindManyAsync(IReadOnlyCollection<StockId> ids, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<StockId>> ListIngestableAsync(Mic market, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
