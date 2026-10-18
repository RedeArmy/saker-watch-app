using AwesomeAssertions;
using FluentValidation.Results;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.MarketData.Contracts;
using SakerWatch.Watchlist.Application;
using SakerWatch.Watchlist.Application.Features.PutWatchlistItem;
using SakerWatch.Watchlist.Application.Features.RemoveWatchlistItem;
using SakerWatch.Watchlist.Domain;
using SakerWatch.Watchlist.Domain.Items;

namespace SakerWatch.Watchlist.Tests;

public sealed class WatchlistUseCaseTests
{
    private static readonly Guid _user = Guid.CreateVersion7();
    private static readonly DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly StockSummary _apple = new(Guid.CreateVersion7(), "XNAS", "AAPL", "Apple Inc.", "USD", "equity");

    private sealed class User(Guid? id) : ICurrentUser
    {
        public Guid? UserId => id;
    }

    private sealed class Stocks(Result<StockSummary>? ensure = null, Result<StockSummary>? find = null) : IStockDirectory
    {
        public List<(string Market, string Ticker)> Ensured { get; } = [];

        public List<(string Market, string Ticker)> Found { get; } = [];

        public Task<Result<IReadOnlyList<StockCandidate>>> SearchAsync(string query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<StockSummary>> FindAsync(string market, string ticker, CancellationToken cancellationToken)
        {
            Found.Add((market, ticker));
            return Task.FromResult(find ?? _apple);
        }

        public Task<Result<StockSummary>> EnsureAsync(string market, string ticker, CancellationToken cancellationToken)
        {
            Ensured.Add((market, ticker));
            return Task.FromResult(ensure ?? _apple);
        }
    }

    private sealed class Repository : IWatchlistRepository
    {
        public Dictionary<(Guid User, Guid Stock), WatchlistItem> Items { get; } = [];

        public AddOutcome? ForcedOutcome { get; set; }

        public bool RaceWinnerOnAdd { get; set; }

        public int Updates { get; private set; }

        public Task<WatchlistItem?> FindByStockAsync(Guid userId, Guid stockId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.GetValueOrDefault((userId, stockId)));

        public Task<int> CountAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(Items.Keys.Count(key => key.User == userId));

        public Task<AddOutcome> AddAsync(WatchlistItem item, CancellationToken cancellationToken)
        {
            if (RaceWinnerOnAdd)
            {
                Items[(item.UserId, item.StockId)] = WatchlistItem.Restore(WatchlistItemId.Create(Guid.CreateVersion7()), item.UserId, item.StockId, "winner", null, _now, _now);
                return Task.FromResult(AddOutcome.AlreadyThere);
            }

            if (ForcedOutcome is { } forced)
            {
                return Task.FromResult(forced);
            }

            Items[(item.UserId, item.StockId)] = item;
            return Task.FromResult(AddOutcome.Added);
        }

        public Task UpdateAsync(WatchlistItem item, CancellationToken cancellationToken)
        {
            Updates++;
            Items[(item.UserId, item.StockId)] = item;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveByStockAsync(Guid userId, Guid stockId, CancellationToken cancellationToken) => Task.FromResult(Items.Remove((userId, stockId)));
    }

    private static PutWatchlistItemHandler Put(Repository repository, Stocks stocks, Guid? user = null, int maxItems = 500, FakeTimeProvider? clock = null)
    {
        FakeTimeProvider time = clock ?? new FakeTimeProvider(_now);
        return new PutWatchlistItemHandler(
            new User(user ?? _user), stocks, repository, new TimeProviderIdGenerator(time), Options.Create(new WatchlistOptions { MaxItems = maxItems }), time);
    }

    private static ValueTask<Result<PutWatchlistItemResult>> PutAsync(PutWatchlistItemHandler handler, string market = "XNAS", string ticker = "AAPL", string? notes = null, decimal? target = null) =>
        handler.Handle(new PutWatchlistItemCommand(market, ticker, notes, target), CancellationToken.None);

    [Fact]
    public async Task Put_AStockNotOnTheWatchlist_AddsIt_WithTheNotesAndTheTarget()
    {
        var repository = new Repository();
        var stocks = new Stocks();

        Result<PutWatchlistItemResult> result = await PutAsync(Put(repository, stocks), notes: "  Esperar resultados  ", target: 180.5m);

        PutWatchlistItemResult put = result.Value;
        put.Created.Should().BeTrue();
        (put.Item.Stock, put.Item.Notes, put.Item.TargetPrice, put.Item.CreatedAt, put.Item.UpdatedAt).Should().Be((_apple, "Esperar resultados", 180.5m, _now, _now));
        put.Item.Id.Should().NotBeEmpty();
        repository.Items.Should().ContainSingle().Which.Value.UserId.Should().Be(_user);
        stocks.Ensured.Should().Equal(("XNAS", "AAPL"));
    }

    [Fact]
    public async Task Put_AStockAlreadyThere_ReplacesTheNotesAndTheTarget_AndKeepsTheItem()
    {
        var repository = new Repository();
        PutWatchlistItemHandler handler = Put(repository, new Stocks());
        Guid id = (await PutAsync(handler, notes: "antes", target: 100m)).Value.Item.Id;
        var later = new FakeTimeProvider(_now.AddHours(3));

        Result<PutWatchlistItemResult> result = await PutAsync(Put(repository, new Stocks(), clock: later), notes: "después", target: null);

        PutWatchlistItemResult put = result.Value;
        (put.Created, put.Item.Id, put.Item.Notes, put.Item.TargetPrice, put.Item.CreatedAt, put.Item.UpdatedAt).Should().Be((false, id, "después", null, _now, _now.AddHours(3)));
        repository.Updates.Should().Be(1);
    }

    [Fact]
    public async Task Put_TheSameRequestTwice_LeavesTheSameState_AndWritesNothingTheSecondTime()
    {
        var repository = new Repository();
        PutWatchlistItemHandler handler = Put(repository, new Stocks());
        await PutAsync(handler, notes: "nota", target: 10m);
        var later = new FakeTimeProvider(_now.AddDays(1));

        Result<PutWatchlistItemResult> again = await PutAsync(Put(repository, new Stocks(), clock: later), notes: " nota ", target: 10m);

        again.Value.Created.Should().BeFalse();
        again.Value.Item.UpdatedAt.Should().Be(_now, "nothing changed, so the item keeps its date");
        repository.Updates.Should().Be(0);
        repository.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Put_ByAnAnonymousCaller_IsRefusedBeforeAnythingIsLookedUp()
    {
        var repository = new Repository();
        var stocks = new Stocks();
        var handler = new PutWatchlistItemHandler(
            new User(null), stocks, repository, new TimeProviderIdGenerator(TimeProvider.System), Options.Create(new WatchlistOptions()), TimeProvider.System);

        Result<PutWatchlistItemResult> result = await PutAsync(handler);

        result.Error!.Type.Should().Be(ErrorType.Unauthorized);
        stocks.Ensured.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_AnInvalidNoteOrTarget_IsRefusedBeforeTheStockIsLookedUp()
    {
        var repository = new Repository();
        var stocks = new Stocks();
        PutWatchlistItemHandler handler = Put(repository, stocks);

        Result<PutWatchlistItemResult> longNote = await PutAsync(handler, notes: new string('x', WatchlistItem.MaxNotesLength + 1));
        Result<PutWatchlistItemResult> badTarget = await PutAsync(handler, target: 0m);
        Result<PutWatchlistItemResult> tooPrecise = await PutAsync(handler, target: 1.123456789m);

        longNote.Error!.Code.Should().Be("watchlist.item.notes_too_long");
        badTarget.Error!.Code.Should().Be("watchlist.item.target_price_invalid");
        tooPrecise.Error!.Code.Should().Be("watchlist.item.target_price_precision");
        stocks.Ensured.Should().BeEmpty("a bad note must not cost a provider search or a catalog row");
        repository.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_WhenTheDirectoryRefuses_PassesItsErrorThrough_AndStoresNothing()
    {
        var repository = new Repository();
        var unavailable = new Stocks(ensure: Error.Unavailable("marketdata.search.provider_unavailable", "down", 30));
        var notFound = new Stocks(ensure: Error.NotFound("marketdata.stock.not_found", "nope"));

        Error down = (await PutAsync(Put(repository, unavailable))).Error!;
        Error missing = (await PutAsync(Put(repository, notFound))).Error!;

        (down.Type, missing.Type).Should().Be((ErrorType.Unavailable, ErrorType.NotFound));
        repository.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Put_OnAFullWatchlist_IsAConflict_ButAStockAlreadyThereCanStillBeEdited()
    {
        var repository = new Repository();
        var other = new StockSummary(Guid.CreateVersion7(), "XNYS", "KO", "Coca-Cola", "USD", "equity");
        await PutAsync(Put(repository, new Stocks(ensure: other), maxItems: 2));
        await PutAsync(Put(repository, new Stocks(), maxItems: 2));

        Result<PutWatchlistItemResult> third = await PutAsync(Put(repository, new Stocks(ensure: new StockSummary(Guid.CreateVersion7(), "XNAS", "MSFT", "Microsoft", "USD", "equity")), maxItems: 2), ticker: "MSFT");
        Result<PutWatchlistItemResult> edit = await PutAsync(Put(repository, new Stocks(), maxItems: 2), notes: "still fine");

        third.Error!.Should().Be(Error.Conflict(WatchlistErrors.LimitReached, "The watchlist is full. Remove a stock before adding another."));
        edit.Value.Item.Notes.Should().Be("still fine");
        repository.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Put_WhenTheDatabaseCeilingStopsAParallelInsert_IsTheSameConflict()
    {
        var repository = new Repository { ForcedOutcome = AddOutcome.LimitReached };

        Result<PutWatchlistItemResult> result = await PutAsync(Put(repository, new Stocks()));

        result.Error!.Code.Should().Be(WatchlistErrors.LimitReached);
        result.Error.Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public async Task Put_WhenAParallelRequestAddedTheSameStockFirst_UpdatesThatItem()
    {
        var repository = new Repository { RaceWinnerOnAdd = true };

        Result<PutWatchlistItemResult> result = await PutAsync(Put(repository, new Stocks()), notes: "mine", target: 5m);

        PutWatchlistItemResult put = result.Value;
        (put.Created, put.Item.Notes, put.Item.TargetPrice).Should().Be((false, "mine", 5m));
        repository.Updates.Should().Be(1);
    }

    [Fact]
    public async Task Put_ForTwoUsers_KeepsTheirItemsApart()
    {
        var repository = new Repository();
        var other = Guid.CreateVersion7();

        await PutAsync(Put(repository, new Stocks()), notes: "mine");
        Result<PutWatchlistItemResult> theirs = await PutAsync(Put(repository, new Stocks(), user: other), notes: "theirs");

        theirs.Value.Created.Should().BeTrue();
        repository.Items.Values.Select(i => i.Notes).Should().BeEquivalentTo("mine", "theirs");
    }

    [Fact]
    public async Task Remove_TakesTheStockOff_AndANeverAddedStockIsNotAnError()
    {
        var repository = new Repository();
        await PutAsync(Put(repository, new Stocks()));
        var handler = new RemoveWatchlistItemHandler(new User(_user), new Stocks(), repository);

        Result first = await handler.Handle(new RemoveWatchlistItemCommand("XNAS", "AAPL"), CancellationToken.None);
        Result again = await handler.Handle(new RemoveWatchlistItemCommand("XNAS", "AAPL"), CancellationToken.None);

        (first.IsSuccess, again.IsSuccess).Should().Be((true, true));
        repository.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_NeverRegistersAStock_AndAStockOutsideTheCatalogHasNothingToRemove()
    {
        var repository = new Repository();
        var stocks = new Stocks(find: Error.NotFound("marketdata.stock.not_found", "not in the catalog"));
        var handler = new RemoveWatchlistItemHandler(new User(_user), stocks, repository);

        Result result = await handler.Handle(new RemoveWatchlistItemCommand("XNAS", "ZZZZ"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        stocks.Ensured.Should().BeEmpty();
        stocks.Found.Should().Equal(("XNAS", "ZZZZ"));
    }

    [Fact]
    public async Task Remove_AMalformedKey_IsStillRefused_AndAnAnonymousCallerIsNot_Served()
    {
        var repository = new Repository();
        var malformed = new Stocks(find: Error.Validation("marketdata.ticker.invalid", "bad"));

        Result bad = await new RemoveWatchlistItemHandler(new User(_user), malformed, repository).Handle(new RemoveWatchlistItemCommand("XNAS", "a b"), CancellationToken.None);
        Result anonymous = await new RemoveWatchlistItemHandler(new User(null), new Stocks(), repository).Handle(new RemoveWatchlistItemCommand("XNAS", "AAPL"), CancellationToken.None);

        (bad.Error!.Type, anonymous.Error!.Type).Should().Be((ErrorType.Validation, ErrorType.Unauthorized));
    }

    [Fact]
    public async Task Remove_OnlyTouchesTheCallersItem()
    {
        var repository = new Repository();
        var other = Guid.CreateVersion7();
        await PutAsync(Put(repository, new Stocks()));
        await PutAsync(Put(repository, new Stocks(), user: other));

        await new RemoveWatchlistItemHandler(new User(_user), new Stocks(), repository).Handle(new RemoveWatchlistItemCommand("XNAS", "AAPL"), CancellationToken.None);

        repository.Items.Keys.Should().ContainSingle().Which.User.Should().Be(other);
    }

    [Theory]
    [InlineData("", "AAPL", "Market")]
    [InlineData("XNASX", "AAPL", "Market")]
    [InlineData("XNAS", "", "Ticker")]
    [InlineData("XNAS", "12345678901234567", "Ticker")]
    public void ThePutValidator_NamesTheFieldThatIsWrong(string market, string ticker, string field)
    {
        ValidationResult result = new PutWatchlistItemValidator().Validate(new PutWatchlistItemCommand(market, ticker, null, null));

        result.Errors.Select(e => e.PropertyName).Should().Contain(field);
        new RemoveWatchlistItemValidator().Validate(new RemoveWatchlistItemCommand(market, ticker)).Errors.Select(e => e.PropertyName).Should().Contain(field);
    }

    [Fact]
    public void ThePutValidator_AcceptsAFullRequest_AndRefusesNotesAndTargetsOutOfRange()
    {
        var validator = new PutWatchlistItemValidator();

        validator.Validate(new PutWatchlistItemCommand("XNAS", "BRK.B", "nota", 10m)).IsValid.Should().BeTrue();
        validator.Validate(new PutWatchlistItemCommand("XNAS", "AAPL", new string('x', 2001), null)).Errors.Select(e => e.PropertyName).Should().Contain("Notes");
        validator.Validate(new PutWatchlistItemCommand("XNAS", "AAPL", null, -1m)).Errors.Select(e => e.PropertyName).Should().Contain("TargetPrice");
        validator.Validate(new PutWatchlistItemCommand("XNAS", "AAPL", null, WatchlistItem.MaxTargetPrice + 1)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void TheOptions_DefaultToTheDocumentedLimits_AndConvertToLimits()
    {
        var options = new WatchlistOptions();

        options.ToLimits().Should().Be(WatchlistLimits.Defaults);
        new WatchlistOptions { MaxItems = 10, MaxPortfolios = 2, MaxItemsPerPortfolio = 5 }.ToLimits().IsWithinCeilings.Should().BeTrue();
    }
}
