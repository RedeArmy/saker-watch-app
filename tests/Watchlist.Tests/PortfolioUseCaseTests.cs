using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Idempotency;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.MarketData.Contracts;
using SakerWatch.Watchlist.Application;
using SakerWatch.Watchlist.Application.Features.Portfolios;
using SakerWatch.Watchlist.Domain;
using SakerWatch.Watchlist.Domain.Portfolios;

namespace SakerWatch.Watchlist.Tests;

public sealed class PortfolioUseCaseTests
{
    private static readonly Guid _user = Guid.CreateVersion7();
    private static readonly DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly StockSummary _apple = new(Guid.CreateVersion7(), "XNAS", "AAPL", "Apple Inc.", "USD", "equity");
    private static readonly StockSummary _cola = new(Guid.CreateVersion7(), "XNYS", "KO", "Coca-Cola Co.", "USD", "equity");

    private sealed class User(Guid? id) : ICurrentUser
    {
        public Guid? UserId => id;
    }

    private sealed class Directory(params StockSummary[] known) : IStockDirectory
    {
        public Task<Result<IReadOnlyList<StockCandidate>>> SearchAsync(string query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<StockSummary>> EnsureAsync(string market, string ticker, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<StockSummary>> FindAsync(string market, string ticker, CancellationToken cancellationToken) =>
            Task.FromResult(known.FirstOrDefault(s => s.Market == market && s.Ticker == ticker) is { } found
                ? Result.Success(found)
                : Error.NotFound("marketdata.stock.not_found", "nope"));
    }

    private sealed class Market(params StockSummary[] stocks) : IMarketSnapshots
    {
        public Task<IReadOnlyDictionary<Guid, StockSummary>> FindManyAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, StockSummary>>(stocks.Where(s => stockIds.Contains(s.Id)).ToDictionary(s => s.Id));

        public Task<IReadOnlyDictionary<Guid, StockSnapshot>> SnapshotsAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, StockPrice>> PricesAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Repository : IPortfolioRepository
    {
        public Dictionary<PortfolioId, Portfolio> Portfolios { get; } = [];

        public Dictionary<PortfolioId, List<PortfolioMember>> Members { get; } = [];

        public HashSet<Guid> OnWatchlist { get; } = [];

        public PortfolioWrite? ForcedAdd { get; set; }

        public MemberAdd? ForcedMember { get; set; }

        public int? MaxMembersSeen { get; private set; }

        public bool OrderAccepted { get; set; } = true;

        public int Updates { get; private set; }

        public List<(PortfolioId Id, bool RemoveOrphans)> Deleted { get; } = [];

        public DeletionPreview Preview { get; set; } = new([], 0);

        public Task<IReadOnlyList<PortfolioSummary>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PortfolioSummary>>([.. Portfolios.Values.Where(p => p.UserId == userId).OrderBy(p => p.Position).Select(p => new PortfolioSummary(p, Members.GetValueOrDefault(p.Id)?.Count ?? 0))]);

        public int OrderLocks { get; private set; }

        public Task LockOrderAsync(Guid userId, CancellationToken cancellationToken)
        {
            OrderLocks++;
            return Task.CompletedTask;
        }

        public Task<PortfolioSummary?> FindAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken) =>
            Task.FromResult(Portfolios.TryGetValue(id, out Portfolio? p) && p.UserId == userId ? new PortfolioSummary(p, Members.GetValueOrDefault(id)?.Count ?? 0) : null);

        public Task<bool> ExistsAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken) =>
            Task.FromResult(Portfolios.TryGetValue(id, out Portfolio? p) && p.UserId == userId);

        public Task<PortfolioWrite> AddAsync(Portfolio portfolio, CancellationToken cancellationToken)
        {
            if (ForcedAdd is { } forced)
            {
                return Task.FromResult(forced);
            }

            if (Portfolios.Values.Any(p => p.UserId == portfolio.UserId && p.Name == portfolio.Name))
            {
                return Task.FromResult(PortfolioWrite.NameTaken);
            }

            Portfolios[portfolio.Id] = portfolio;
            return Task.FromResult(PortfolioWrite.Done);
        }

        public Task<PortfolioWrite> UpdateAsync(Portfolio portfolio, CancellationToken cancellationToken)
        {
            Updates++;
            if (Portfolios.Values.Any(p => p.Id != portfolio.Id && p.UserId == portfolio.UserId && p.Name == portfolio.Name))
            {
                return Task.FromResult(PortfolioWrite.NameTaken);
            }

            Portfolios[portfolio.Id] = portfolio;
            return Task.FromResult(PortfolioWrite.Done);
        }

        public Task<bool> ReorderAsync(Guid userId, IReadOnlyList<PortfolioId> ordered, CancellationToken cancellationToken) => Task.FromResult(OrderAccepted);

        public Task<IReadOnlyList<PortfolioMember>> MembersAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PortfolioMember>>(Members.GetValueOrDefault(id) ?? []);

        public Task<MemberAdd> AddMemberAsync(NewMember member, int maxMembers, CancellationToken cancellationToken)
        {
            (Guid _, PortfolioId id, Guid stockId, DateTimeOffset now) = member;
            MaxMembersSeen = maxMembers;
            if (ForcedMember is { } forced)
            {
                return Task.FromResult(forced);
            }

            if (!OnWatchlist.Contains(stockId))
            {
                return Task.FromResult(MemberAdd.ItemNotOnWatchlist);
            }

            if (!Members.TryGetValue(id, out List<PortfolioMember>? list))
            {
                list = [];
                Members[id] = list;
            }

            if (list.Any(m => m.StockId == stockId))
            {
                return Task.FromResult(MemberAdd.AlreadyThere);
            }

            list.Add(new PortfolioMember(stockId, list.Count, now));
            return Task.FromResult(MemberAdd.Added);
        }

        public Task<bool> RemoveMemberAsync(Guid userId, PortfolioId id, Guid stockId, CancellationToken cancellationToken) =>
            Task.FromResult(Members.GetValueOrDefault(id)?.RemoveAll(m => m.StockId == stockId) > 0);

        public Task<bool> ReorderMembersAsync(Guid userId, PortfolioId id, IReadOnlyList<Guid> orderedStocks, CancellationToken cancellationToken) => Task.FromResult(OrderAccepted);

        public Task<DeletionPreview> PreviewDeletionAsync(Guid userId, PortfolioId id, CancellationToken cancellationToken) => Task.FromResult(Preview);

        public string? TokenSeen { get; private set; }

        public bool Changed { get; set; }

        public Task<PortfolioDelete> DeleteAsync(Guid userId, PortfolioId id, bool removeOrphans, string? expectedRemovalToken, CancellationToken cancellationToken)
        {
            TokenSeen = expectedRemovalToken;
            if (Changed)
            {
                return Task.FromResult(PortfolioDelete.RemovalChanged);
            }

            Deleted.Add((id, removeOrphans));
            return Task.FromResult(Portfolios.Remove(id) ? PortfolioDelete.Deleted : PortfolioDelete.NotFound);
        }
    }

    private static IdempotencyKeyInfo Key() => new("k-0123456789abcdef", "POST", "/v1/portfolios");

    private static (Repository Repo, FakeTimeProvider Clock) World() => (new Repository(), new FakeTimeProvider(_now));

    private static async Task<PortfolioView> CreateAsync(Repository repo, string name = "Petróleo", string? description = null, int max = 20)
    {
        var handler = new CreatePortfolioHandler(new User(_user), repo, new TimeProviderIdGenerator(TimeProvider.System), Options.Create(new WatchlistOptions { MaxPortfolios = max }), new FakeTimeProvider(_now));
        return (await handler.Handle(new CreatePortfolioCommand(name, description, Key()), CancellationToken.None)).Value;
    }

    [Fact]
    public async Task Create_TrimsTheName_PlacesItAtTheEnd_AndStartsEmpty()
    {
        (Repository repo, _) = World();

        PortfolioView first = await CreateAsync(repo, "  Petróleo  ", "  Energía  ");
        PortfolioView second = await CreateAsync(repo, "Imanes");

        (first.Name, first.Description, first.Position, first.ItemCount, first.CreatedAt).Should().Be(("Petróleo", "Energía", 0, 0, _now));
        second.Position.Should().Be(1);
        first.Id.Should().NotBeEmpty();
        repo.OrderLocks.Should().Be(2, "each creation queues behind the others of the user before it reads the last position");
    }

    [Fact]
    public async Task Create_WithABadNameOrDescription_IsRefused_AndStoresNothing()
    {
        (Repository repo, FakeTimeProvider clock) = World();
        var handler = new CreatePortfolioHandler(new User(_user), repo, new TimeProviderIdGenerator(clock), Options.Create(new WatchlistOptions()), clock);

        Result<PortfolioView> empty = await handler.Handle(new CreatePortfolioCommand("   ", null, Key()), CancellationToken.None);
        Result<PortfolioView> longName = await handler.Handle(new CreatePortfolioCommand(new string('x', 61), null, Key()), CancellationToken.None);
        Result<PortfolioView> control = await handler.Handle(new CreatePortfolioCommand("a\u0007b", null, Key()), CancellationToken.None);
        Result<PortfolioView> longDescription = await handler.Handle(new CreatePortfolioCommand("Ok", new string('d', 501), Key()), CancellationToken.None);

        new[] { empty, longName, control, longDescription }.Select(r => r.Error!.Type).Should().AllBeEquivalentTo(ErrorType.Validation);
        repo.Portfolios.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithANameTheCallerHasIgnoringCase_IsAConflict()
    {
        (Repository repo, _) = World();
        await CreateAsync(repo, "Petróleo");

        var handler = new CreatePortfolioHandler(new User(_user), repo, new TimeProviderIdGenerator(TimeProvider.System), Options.Create(new WatchlistOptions()), new FakeTimeProvider(_now));
        Result<PortfolioView> result = await handler.Handle(new CreatePortfolioCommand("PETRÓLEO", null, Key()), CancellationToken.None);

        repo.ForcedAdd = PortfolioWrite.NameTaken;
        Result<PortfolioView> race = await handler.Handle(new CreatePortfolioCommand("Otra", null, Key()), CancellationToken.None);

        race.Error.Should().Be(PortfolioErrors.PortfolioNameTaken());
        result.Error.Should().Be(PortfolioErrors.PortfolioNameTaken(), "the name value object compares ignoring letter case, as the database index does");
    }

    [Fact]
    public async Task Create_WhenTheCallerHasTheMostPortfolios_IsAConflict_AndTheDatabaseCeilingIsTheSame()
    {
        (Repository repo, _) = World();
        await CreateAsync(repo, "Uno", max: 1);
        var handler = new CreatePortfolioHandler(new User(_user), repo, new TimeProviderIdGenerator(TimeProvider.System), Options.Create(new WatchlistOptions { MaxPortfolios = 1 }), new FakeTimeProvider(_now));

        Result<PortfolioView> full = await handler.Handle(new CreatePortfolioCommand("Dos", null, Key()), CancellationToken.None);
        repo.ForcedAdd = PortfolioWrite.LimitReached;
        var roomy = new CreatePortfolioHandler(new User(_user), repo, new TimeProviderIdGenerator(TimeProvider.System), Options.Create(new WatchlistOptions { MaxPortfolios = 5 }), new FakeTimeProvider(_now));
        Result<PortfolioView> race = await roomy.Handle(new CreatePortfolioCommand("Tres", null, Key()), CancellationToken.None);

        full.Error.Should().Be(PortfolioErrors.PortfolioLimit());
        race.Error.Should().Be(PortfolioErrors.PortfolioLimit());
    }

    [Fact]
    public async Task Update_ReplacesTheNameAndTheDescription_AndTheSameRequestWritesNothingTheSecondTime()
    {
        (Repository repo, FakeTimeProvider clock) = World();
        PortfolioView created = await CreateAsync(repo, "Petróleo");
        var handler = new UpdatePortfolioHandler(new User(_user), repo, clock);

        Result<PortfolioView> first = await handler.Handle(new UpdatePortfolioCommand(created.Id, "  Crudo ", "Brent"), CancellationToken.None);
        Result<PortfolioView> again = await handler.Handle(new UpdatePortfolioCommand(created.Id, "Crudo", "Brent"), CancellationToken.None);

        (first.Value.Name, first.Value.Description).Should().Be(("Crudo", "Brent"));
        again.IsSuccess.Should().BeTrue();
        repo.Updates.Should().Be(1);
    }

    [Fact]
    public async Task Update_OfAnotherUsersOrAMissingPortfolio_IsNotFound_AndANameTakenIsAConflict()
    {
        (Repository repo, FakeTimeProvider clock) = World();
        PortfolioView one = await CreateAsync(repo, "Uno");
        await CreateAsync(repo, "Dos");

        Result<PortfolioView> theirs = await new UpdatePortfolioHandler(new User(Guid.CreateVersion7()), repo, clock).Handle(new UpdatePortfolioCommand(one.Id, "X", null), CancellationToken.None);
        Result<PortfolioView> missing = await new UpdatePortfolioHandler(new User(_user), repo, clock).Handle(new UpdatePortfolioCommand(Guid.CreateVersion7(), "X", null), CancellationToken.None);
        Result<PortfolioView> taken = await new UpdatePortfolioHandler(new User(_user), repo, clock).Handle(new UpdatePortfolioCommand(one.Id, "Dos", null), CancellationToken.None);
        Result<PortfolioView> invalid = await new UpdatePortfolioHandler(new User(_user), repo, clock).Handle(new UpdatePortfolioCommand(one.Id, " ", null), CancellationToken.None);

        theirs.Error!.Code.Should().Be(PortfolioErrors.NotFound);
        missing.Error!.Code.Should().Be(PortfolioErrors.NotFound);
        taken.Error!.Type.Should().Be(ErrorType.Conflict);
        invalid.Error!.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public async Task Reorder_IsAcceptedWhenTheListIsExact_AndRefusedWhenItIsNot()
    {
        (Repository repo, _) = World();
        var handler = new ReorderPortfoliosHandler(new User(_user), repo);

        Result accepted = await handler.Handle(new ReorderPortfoliosCommand([Guid.CreateVersion7()]), CancellationToken.None);
        repo.OrderAccepted = false;
        Result refused = await handler.Handle(new ReorderPortfoliosCommand([Guid.CreateVersion7()]), CancellationToken.None);

        accepted.IsSuccess.Should().BeTrue();
        refused.Error!.Code.Should().Be(PortfolioErrors.OrderMismatch);
    }

    [Fact]
    public async Task ListAndGet_ShowTheCallersPortfoliosWithTheirStocksByPosition_AndNotAnotherUsers()
    {
        (Repository repo, _) = World();
        PortfolioView created = await CreateAsync(repo, "Petróleo");
        repo.Members[PortfolioId.Create(created.Id)] = [new PortfolioMember(_cola.Id, 1, _now), new PortfolioMember(_apple.Id, 0, _now)];
        var market = new Market(_apple, _cola);

        IReadOnlyList<PortfolioView> list = (await new ListPortfoliosHandler(new User(_user), repo).Handle(new ListPortfoliosQuery(), CancellationToken.None)).Value;
        PortfolioDetail detail = (await new GetPortfolioHandler(new User(_user), repo, market).Handle(new GetPortfolioQuery(created.Id), CancellationToken.None)).Value;
        IReadOnlyList<PortfolioView> others = (await new ListPortfoliosHandler(new User(Guid.CreateVersion7()), repo).Handle(new ListPortfoliosQuery(), CancellationToken.None)).Value;
        Result<PortfolioDetail> theirs = await new GetPortfolioHandler(new User(Guid.CreateVersion7()), repo, market).Handle(new GetPortfolioQuery(created.Id), CancellationToken.None);

        list.Should().ContainSingle().Which.ItemCount.Should().Be(2);
        detail.Items.Select(i => i.Stock.Ticker).Should().BeEquivalentTo("AAPL", "KO");
        others.Should().BeEmpty();
        theirs.Error!.Code.Should().Be(PortfolioErrors.NotFound);
    }

    [Fact]
    public async Task PutItem_PutsAWatchlistStockAtTheEnd_AndPuttingItAgainIsNotACreation()
    {
        (Repository repo, FakeTimeProvider clock) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        repo.OnWatchlist.Add(_apple.Id);
        var handler = new PutPortfolioItemHandler(new User(_user), new Directory(_apple, _cola), repo, Options.Create(new WatchlistOptions()), clock);

        Result<PutPortfolioItemResult> first = await handler.Handle(new PutPortfolioItemCommand(portfolio.Id, "XNAS", "AAPL"), CancellationToken.None);
        Result<PutPortfolioItemResult> again = await handler.Handle(new PutPortfolioItemCommand(portfolio.Id, "XNAS", "AAPL"), CancellationToken.None);

        (first.Value.Created, first.Value.Item.Stock, first.Value.Item.Position, first.Value.Item.AddedAt).Should().Be((true, _apple, 0, _now));
        again.Value.Created.Should().BeFalse();
        repo.Members[PortfolioId.Create(portfolio.Id)].Should().ContainSingle();
    }

    [Fact]
    public async Task PutItem_WhenAParallelRequestTookTheMemberOutBeforeItWasRead_IsAConflict_NotACrash()
    {
        (Repository repo, FakeTimeProvider clock) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        repo.ForcedMember = MemberAdd.AlreadyThere; // the add found it there; the list read afterwards no longer has it
        var handler = new PutPortfolioItemHandler(new User(_user), new Directory(_apple), repo, Options.Create(new WatchlistOptions()), clock);

        Result<PutPortfolioItemResult> result = await handler.Handle(new PutPortfolioItemCommand(portfolio.Id, "XNAS", "AAPL"), CancellationToken.None);

        (result.Error!.Code, result.Error.Type).Should().Be((PortfolioErrors.ItemChanged, ErrorType.Conflict));
    }

    [Fact]
    public async Task PutItem_AsksTheRepositoryToHoldTheConfiguredLimit_NotTheCeiling()
    {
        (Repository repo, FakeTimeProvider clock) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        repo.OnWatchlist.Add(_apple.Id);
        var handler = new PutPortfolioItemHandler(new User(_user), new Directory(_apple), repo, Options.Create(new WatchlistOptions { MaxItemsPerPortfolio = 7 }), clock);

        await handler.Handle(new PutPortfolioItemCommand(portfolio.Id, "XNAS", "AAPL"), CancellationToken.None);

        repo.MaxMembersSeen.Should().Be(7, "Watchlist:Limits:MaxItemsPerPortfolio is the limit of the portfolio; the 100 of the database is only the ceiling behind it");
    }

    [Fact]
    public async Task PutItem_OfAStockNotOnTheWatchlist_OrNotInTheCatalog_IsNotFound_WithTheSameAdvice()
    {
        (Repository repo, FakeTimeProvider clock) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        var handler = new PutPortfolioItemHandler(new User(_user), new Directory(_apple), repo, Options.Create(new WatchlistOptions()), clock);

        Result<PutPortfolioItemResult> notFollowed = await handler.Handle(new PutPortfolioItemCommand(portfolio.Id, "XNAS", "AAPL"), CancellationToken.None);
        Result<PutPortfolioItemResult> unknown = await handler.Handle(new PutPortfolioItemCommand(portfolio.Id, "XNAS", "ZZZZ"), CancellationToken.None);

        new[] { notFollowed, unknown }.Select(r => r.Error!.Code).Should().AllBeEquivalentTo(PortfolioErrors.ItemNotOnWatchlist);
        repo.Members.Should().BeEmpty();
    }

    [Fact]
    public async Task PutItem_IntoAMissingPortfolio_IsNotFound_AMalformedKeyIsValidation_AndAFullPortfolioIsAConflict()
    {
        (Repository repo, FakeTimeProvider clock) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        repo.OnWatchlist.Add(_apple.Id);
        var invalid = new Directory();
        var stocks = new Directory(_apple);

        Result<PutPortfolioItemResult> missing = await new PutPortfolioItemHandler(new User(_user), stocks, repo, Options.Create(new WatchlistOptions()), clock).Handle(new PutPortfolioItemCommand(Guid.CreateVersion7(), "XNAS", "AAPL"), CancellationToken.None);
        repo.ForcedMember = MemberAdd.LimitReached;
        Result<PutPortfolioItemResult> full = await new PutPortfolioItemHandler(new User(_user), stocks, repo, Options.Create(new WatchlistOptions()), clock).Handle(new PutPortfolioItemCommand(portfolio.Id, "XNAS", "AAPL"), CancellationToken.None);
        repo.ForcedMember = MemberAdd.PortfolioNotFound;
        Result<PutPortfolioItemResult> raced = await new PutPortfolioItemHandler(new User(_user), stocks, repo, Options.Create(new WatchlistOptions()), clock).Handle(new PutPortfolioItemCommand(portfolio.Id, "XNAS", "AAPL"), CancellationToken.None);

        missing.Error!.Code.Should().Be(PortfolioErrors.NotFound);
        full.Error!.Code.Should().Be(PortfolioErrors.ItemLimitReached);
        raced.Error!.Code.Should().Be(PortfolioErrors.NotFound);
        _ = invalid;
    }

    [Fact]
    public async Task RemoveItem_TakesItOut_AndRemovingAgainOrAnUnknownStockIsNotAnError_ButAMissingPortfolioIs()
    {
        (Repository repo, _) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        repo.OnWatchlist.Add(_apple.Id);
        await new PutPortfolioItemHandler(new User(_user), new Directory(_apple), repo, Options.Create(new WatchlistOptions()), new FakeTimeProvider(_now)).Handle(new PutPortfolioItemCommand(portfolio.Id, "XNAS", "AAPL"), CancellationToken.None);
        var handler = new RemovePortfolioItemHandler(new User(_user), new Directory(_apple), repo);

        Result first = await handler.Handle(new RemovePortfolioItemCommand(portfolio.Id, "XNAS", "AAPL"), CancellationToken.None);
        Result again = await handler.Handle(new RemovePortfolioItemCommand(portfolio.Id, "XNAS", "AAPL"), CancellationToken.None);
        Result unknown = await handler.Handle(new RemovePortfolioItemCommand(portfolio.Id, "XNAS", "ZZZZ"), CancellationToken.None);
        Result missing = await handler.Handle(new RemovePortfolioItemCommand(Guid.CreateVersion7(), "XNAS", "AAPL"), CancellationToken.None);

        (first.IsSuccess, again.IsSuccess, unknown.IsSuccess).Should().Be((true, true, true));
        missing.Error!.Code.Should().Be(PortfolioErrors.NotFound);
    }

    [Fact]
    public async Task ReorderItems_IsAcceptedWhenExact_RefusedWhenNot_AndAStockTheCatalogLacksIsAMismatch()
    {
        (Repository repo, _) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        repo.Members[PortfolioId.Create(portfolio.Id)] = [new PortfolioMember(_apple.Id, 0, _now), new PortfolioMember(_cola.Id, 1, _now)];
        var handler = new ReorderPortfolioItemsHandler(new User(_user), new Market(_apple, _cola), repo);

        Result accepted = await handler.Handle(new ReorderPortfolioItemsCommand(portfolio.Id, [new StockKey("XNYS", "KO"), new StockKey("XNAS", "AAPL")]), CancellationToken.None);
        Result unknown = await handler.Handle(new ReorderPortfolioItemsCommand(portfolio.Id, [new StockKey("XNAS", "ZZZZ")]), CancellationToken.None);
        Result notInThePortfolio = await handler.Handle(new ReorderPortfolioItemsCommand(portfolio.Id, [new StockKey("XNAS", "MSFT")]), CancellationToken.None);
        repo.OrderAccepted = false;
        Result refused = await handler.Handle(new ReorderPortfolioItemsCommand(portfolio.Id, [new StockKey("XNAS", "AAPL")]), CancellationToken.None);
        Result missing = await handler.Handle(new ReorderPortfolioItemsCommand(Guid.CreateVersion7(), []), CancellationToken.None);

        accepted.IsSuccess.Should().BeTrue();
        unknown.Error!.Code.Should().Be(PortfolioErrors.OrderMismatch);
        notInThePortfolio.Error!.Code.Should().Be(PortfolioErrors.OrderMismatch);
        refused.Error!.Code.Should().Be(PortfolioErrors.OrderMismatch);
        missing.Error!.Code.Should().Be(PortfolioErrors.NotFound);
    }

    [Fact]
    public async Task Preview_ListsTheStocksThatWouldLeaveTheWatchlist_ByTicker_AndCountsTheOnesThatStay()
    {
        (Repository repo, _) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        repo.Preview = new DeletionPreview([_cola.Id, _apple.Id], StocksKept: 3);
        var handler = new PreviewPortfolioDeletionHandler(new User(_user), repo, new Market(_apple, _cola));

        PortfolioDeletionPreview preview = (await handler.Handle(new PreviewPortfolioDeletionQuery(portfolio.Id), CancellationToken.None)).Value;
        Result<PortfolioDeletionPreview> missing = await handler.Handle(new PreviewPortfolioDeletionQuery(Guid.CreateVersion7()), CancellationToken.None);

        preview.StocksToRemove.Select(s => s.Ticker).Should().Equal("AAPL", "KO");
        (preview.StocksKept, preview.Portfolio.Id).Should().Be((3, portfolio.Id));
        missing.Error!.Code.Should().Be(PortfolioErrors.NotFound);
    }

    [Fact]
    public async Task Delete_WithStocksToRemove_NeedsConfirmation_AndChangesNothingWithoutIt()
    {
        (Repository repo, _) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        repo.Preview = new DeletionPreview([_apple.Id, _cola.Id], 0);
        var handler = new DeletePortfolioHandler(new User(_user), repo, new Market(_apple, _cola));

        Result unconfirmed = await handler.Handle(new DeletePortfolioCommand(portfolio.Id, Confirm: false), CancellationToken.None);
        Result withoutToken = await handler.Handle(new DeletePortfolioCommand(portfolio.Id, Confirm: true), CancellationToken.None);
        Result confirmed = await handler.Handle(new DeletePortfolioCommand(portfolio.Id, Confirm: true, repo.Preview.Token), CancellationToken.None);

        (withoutToken.Error!.Code, withoutToken.Error.Type).Should().Be((PortfolioErrors.RemovalTokenRequired, ErrorType.Conflict), "what is confirmed is exactly what was shown");
        unconfirmed.Error!.Code.Should().Be(PortfolioErrors.DeleteNeedsConfirmation);
        unconfirmed.Error.Type.Should().Be(ErrorType.Conflict);
        // The same answer carries what would leave and the token that confirms it, so no other request is needed to learn them.
        unconfirmed.Error.Extensions!["removalToken"].Should().Be(repo.Preview.Token);
        ((StockSummary[])unconfirmed.Error.Extensions["stocksToRemove"]!).Select(stock => stock.Ticker).Should().Equal("AAPL", "KO");
        withoutToken.Error.Extensions!["removalToken"].Should().Be(repo.Preview.Token);
        unconfirmed.Error.Message.Should().Contain("2 stock(s)");
        confirmed.IsSuccess.Should().BeTrue();
        repo.Deleted.Should().ContainSingle().Which.Should().Be((PortfolioId.Create(portfolio.Id), true));
    }

    [Fact]
    public async Task Delete_WhenTheStocksToRemoveChangedSinceThePreview_IsAConflict_AndTheTokenReachesTheRepository()
    {
        (Repository repo, _) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        repo.Preview = new DeletionPreview([_apple.Id], 0);
        repo.Changed = true;
        var handler = new DeletePortfolioHandler(new User(_user), repo, new Market(_apple, _cola));

        Result result = await handler.Handle(new DeletePortfolioCommand(portfolio.Id, Confirm: true, "token-of-what-was-seen"), CancellationToken.None);

        (result.Error!.Code, result.Error.Type).Should().Be((PortfolioErrors.RemovalChanged, ErrorType.Conflict));
        repo.TokenSeen.Should().Be("token-of-what-was-seen");
        result.Error.Extensions!["removalToken"].Should().Be(repo.Preview.Token, "the answer has the token of what would leave now");
    }

    [Fact]
    public async Task Delete_ConfirmedWithNothingToRemove_StillChecksThatNothingAppeared()
    {
        (Repository repo, _) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        repo.Preview = new DeletionPreview([], StocksKept: 1);
        var handler = new DeletePortfolioHandler(new User(_user), repo, new Market(_apple, _cola));

        Result result = await handler.Handle(new DeletePortfolioCommand(portfolio.Id, Confirm: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        repo.TokenSeen.Should().Be(DeletionPreview.TokenOf([]), "the caller saw nothing to remove, so a stock that appeared since must not be removed");
    }

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF", false)]
    [InlineData("0123456789abcde", false)]
    [InlineData("0123456789abcdef0", false)]
    [InlineData("0123456789abcdeg", false)]
    [InlineData("0123456789abcdef\n", false)]
    public void TheDeleteValidator_AcceptsOnlyTheFormOfAToken(string token, bool valid) =>
        new DeletePortfolioValidator().Validate(new DeletePortfolioCommand(Guid.CreateVersion7(), true, token)).IsValid.Should().Be(valid);

    [Fact]
    public void ThePreviewToken_IsTheSameForTheSameSet_InAnyOrder_AndDifferentOtherwise()
    {
        var one = Guid.CreateVersion7();
        var two = Guid.CreateVersion7();

        DeletionPreview.TokenOf([one, two]).Should().Be(DeletionPreview.TokenOf([two, one]));
        DeletionPreview.TokenOf([one, two]).Should().NotBe(DeletionPreview.TokenOf([one]));
        DeletionPreview.TokenOf([]).Should().MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public async Task Delete_WhenNoStockWouldLeave_NeedsNoConfirmation_AndNeverRemovesOrphans()
    {
        (Repository repo, _) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        repo.Preview = new DeletionPreview([], StocksKept: 2);
        var handler = new DeletePortfolioHandler(new User(_user), repo, new Market(_apple, _cola));

        Result result = await handler.Handle(new DeletePortfolioCommand(portfolio.Id, Confirm: false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        repo.Deleted.Should().ContainSingle().Which.RemoveOrphans.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_OfAPortfolioThatIsNotThere_IsNotAnError_AndOfAnotherUsersLeavesItAlone()
    {
        (Repository repo, _) = World();
        PortfolioView portfolio = await CreateAsync(repo);

        Result missing = await new DeletePortfolioHandler(new User(_user), repo, new Market(_apple, _cola)).Handle(new DeletePortfolioCommand(Guid.CreateVersion7(), true), CancellationToken.None);
        Result theirs = await new DeletePortfolioHandler(new User(Guid.CreateVersion7()), repo, new Market(_apple, _cola)).Handle(new DeletePortfolioCommand(portfolio.Id, true), CancellationToken.None);

        (missing.IsSuccess, theirs.IsSuccess).Should().Be((true, true));
        repo.Deleted.Should().BeEmpty();
        repo.Portfolios.Should().ContainSingle();
    }

    private sealed class SnapshotMarket(params StockSnapshot[] snapshots) : IMarketSnapshots
    {
        public List<int> Calls { get; } = [];

        public Task<IReadOnlyDictionary<Guid, StockSummary>> FindManyAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, StockSnapshot>> SnapshotsAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken)
        {
            Calls.Add(stockIds.Count);
            return Task.FromResult<IReadOnlyDictionary<Guid, StockSnapshot>>(snapshots.Where(s => stockIds.Contains(s.Stock.Id)).ToDictionary(s => s.Stock.Id));
        }

        public Task<IReadOnlyDictionary<Guid, StockPrice>> PricesAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static StockSnapshot Snap(StockSummary stock, string status, DateTimeOffset? staleSince, params (string Window, decimal? Change)[] changes)
    {
        string[] codes = ["1D", "1W", "1M", "3M", "6M", "YTD", "1Y", "5Y"];
        WindowSnapshot[] windows = [.. codes.Select(code => new WindowSnapshot(code, changes.FirstOrDefault(c => c.Window == code).Change, null, changes.Any(c => c.Window == code && c.Change.HasValue) ? null : "insufficient_history"))];
        return new StockSnapshot(stock, status, status is PriceStatuses.Pending or PriceStatuses.Unavailable ? null : new PriceSnapshot(10m, new DateOnly(2026, 10, 16), true), staleSince, windows);
    }

    [Fact]
    public async Task Metrics_AggregateTheStocksOfThePortfolio_NameTheBestAndTheWorst_AndCountThePriceStates()
    {
        (Repository repo, _) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        var msft = new StockSummary(Guid.CreateVersion7(), "XNAS", "MSFT", "Microsoft", "USD", "equity");
        var nvda = new StockSummary(Guid.CreateVersion7(), "XNAS", "NVDA", "Nvidia", "USD", "equity");
        repo.Members[PortfolioId.Create(portfolio.Id)] = [new PortfolioMember(_apple.Id, 0, _now), new PortfolioMember(_cola.Id, 1, _now), new PortfolioMember(msft.Id, 2, _now), new PortfolioMember(nvda.Id, 3, _now)];
        DateTimeOffset since = _now.AddDays(-2);
        var market = new SnapshotMarket(
            Snap(_apple, PriceStatuses.Current, null, ("1D", 0.10m), ("YTD", 0.30m)),
            Snap(_cola, PriceStatuses.Stale, since, ("1D", -0.05m)),
            Snap(msft, PriceStatuses.Pending, null),
            Snap(nvda, PriceStatuses.Unavailable, null));

        PortfolioMetrics metrics = (await new GetPortfolioMetricsHandler(new User(_user), repo, market).Handle(new GetPortfolioMetricsQuery(portfolio.Id), CancellationToken.None)).Value;

        metrics.StockCount.Should().Be(4);
        metrics.PriceStatuses.Should().Be(new PriceStatusCounts(1, 1, 1, 1));
        metrics.StaleSince.Should().Be(since);
        metrics.Windows.Select(w => w.Window).Should().Equal("1D", "1W", "1M", "3M", "6M", "YTD", "1Y", "5Y");
        WindowMetrics day = metrics.Windows[0];
        (day.Count, day.Average, day.Median).Should().Be((2, 0.025m, 0.025m));
        (day.Best!.Stock, day.Best.Change, day.Worst!.Stock, day.Worst.Change).Should().Be((_apple, 0.10m, _cola, -0.05m));
        metrics.Windows[5].Should().Match<WindowMetrics>(w => w.Count == 1 && w.Average == 0.30m && w.Best!.Stock == _apple && w.Worst!.Stock == _apple);
        metrics.Windows[7].Should().Be(new WindowMetrics("5Y", 0, null, null, null, null));
        market.Calls.Should().Equal(4);
    }

    [Fact]
    public async Task Metrics_CountEveryMember_SoTheFiguresAddUp_EvenWhenTheMarketCannotAnswerForOne()
    {
        (Repository repo, _) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        repo.Members[PortfolioId.Create(portfolio.Id)] = [new PortfolioMember(_apple.Id, 0, _now), new PortfolioMember(_cola.Id, 1, _now), new PortfolioMember(Guid.CreateVersion7(), 2, _now)];
        var market = new SnapshotMarket(Snap(_apple, PriceStatuses.Current, null, ("1D", 0.10m)), Snap(_cola, PriceStatuses.Current, null, ("1D", 0.02m)));

        PortfolioMetrics metrics = (await new GetPortfolioMetricsHandler(new User(_user), repo, market).Handle(new GetPortfolioMetricsQuery(portfolio.Id), CancellationToken.None)).Value;

        metrics.StockCount.Should().Be(3);
        metrics.Portfolio.ItemCount.Should().Be(3, "the portfolio and its metrics are read from the same members");
        metrics.PriceStatuses.Should().Be(new PriceStatusCounts(2, 0, 1, 0), "the stock the market could not answer for is pending, so the counts add up to the stocks held");
        metrics.Windows[0].Count.Should().Be(2, "the figures are over the stocks that have a number");
    }

    [Fact]
    public async Task Metrics_CountADelistedStockApart_KeepItsLastNumbers_AndItIsNeverStale()
    {
        (Repository repo, _) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        var gone = new StockSummary(Guid.CreateVersion7(), "XNAS", "GONE", "Gone Inc.", "USD", "equity");
        repo.Members[PortfolioId.Create(portfolio.Id)] = [new PortfolioMember(_apple.Id, 0, _now), new PortfolioMember(gone.Id, 1, _now)];
        var market = new SnapshotMarket(
            Snap(_apple, PriceStatuses.Current, null, ("1D", 0.10m)),
            Snap(gone, PriceStatuses.Delisted, null, ("1D", -0.50m)));

        PortfolioMetrics metrics = (await new GetPortfolioMetricsHandler(new User(_user), repo, market).Handle(new GetPortfolioMetricsQuery(portfolio.Id), CancellationToken.None)).Value;

        metrics.PriceStatuses.Should().Be(new PriceStatusCounts(1, 0, 0, 0, 1));
        metrics.StaleSince.Should().BeNull();
        metrics.Windows[0].Count.Should().Be(2, "a delisted stock keeps the numbers it had");
    }

    [Fact]
    public async Task Metrics_OfAnEmptyPortfolio_HaveNoNumbersAndNoStaleMoment_AndOfAnotherUsersAreNotFound()
    {
        (Repository repo, _) = World();
        PortfolioView portfolio = await CreateAsync(repo);
        var handler = new GetPortfolioMetricsHandler(new User(_user), repo, new SnapshotMarket());

        PortfolioMetrics empty = (await handler.Handle(new GetPortfolioMetricsQuery(portfolio.Id), CancellationToken.None)).Value;
        Result<PortfolioMetrics> theirs = await new GetPortfolioMetricsHandler(new User(Guid.CreateVersion7()), repo, new SnapshotMarket()).Handle(new GetPortfolioMetricsQuery(portfolio.Id), CancellationToken.None);
        Result<PortfolioMetrics> anonymous = await new GetPortfolioMetricsHandler(new User(null), repo, new SnapshotMarket()).Handle(new GetPortfolioMetricsQuery(portfolio.Id), CancellationToken.None);

        (empty.StockCount, empty.StaleSince, empty.PriceStatuses).Should().Be((0, (DateTimeOffset?)null, new PriceStatusCounts(0, 0, 0, 0)));
        empty.Windows.Should().HaveCount(8).And.OnlyContain(w => w.Count == 0 && w.Average == null);
        theirs.Error!.Code.Should().Be(PortfolioErrors.NotFound);
        anonymous.Error!.Type.Should().Be(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task EveryUseCase_RefusesAnAnonymousCaller()
    {
        (Repository repo, FakeTimeProvider clock) = World();
        var anon = new User(null);
        var id = Guid.CreateVersion7();

        Error?[] errors =
        [
            (await new CreatePortfolioHandler(anon, repo, new TimeProviderIdGenerator(clock), Options.Create(new WatchlistOptions()), clock).Handle(new CreatePortfolioCommand("A", null, Key()), CancellationToken.None)).Error,
            (await new UpdatePortfolioHandler(anon, repo, clock).Handle(new UpdatePortfolioCommand(id, "A", null), CancellationToken.None)).Error,
            (await new ReorderPortfoliosHandler(anon, repo).Handle(new ReorderPortfoliosCommand([]), CancellationToken.None)).Error,
            (await new ListPortfoliosHandler(anon, repo).Handle(new ListPortfoliosQuery(), CancellationToken.None)).Error,
            (await new GetPortfolioHandler(anon, repo, new Market()).Handle(new GetPortfolioQuery(id), CancellationToken.None)).Error,
            (await new PreviewPortfolioDeletionHandler(anon, repo, new Market()).Handle(new PreviewPortfolioDeletionQuery(id), CancellationToken.None)).Error,
            (await new DeletePortfolioHandler(anon, repo, new Market(_apple, _cola)).Handle(new DeletePortfolioCommand(id, true), CancellationToken.None)).Error,
            (await new PutPortfolioItemHandler(anon, new Directory(), repo, Options.Create(new WatchlistOptions()), clock).Handle(new PutPortfolioItemCommand(id, "XNAS", "AAPL"), CancellationToken.None)).Error,
            (await new RemovePortfolioItemHandler(anon, new Directory(), repo).Handle(new RemovePortfolioItemCommand(id, "XNAS", "AAPL"), CancellationToken.None)).Error,
            (await new ReorderPortfolioItemsHandler(anon, new Market(), repo).Handle(new ReorderPortfolioItemsCommand(id, []), CancellationToken.None)).Error,
        ];

        errors.Should().OnlyContain(e => e != null && e.Type == ErrorType.Unauthorized);
    }

    [Fact]
    public void TheValidators_NameTheFieldsThatAreWrong()
    {
        new CreatePortfolioValidator().Validate(new CreatePortfolioCommand(" ", null, Key())).Errors.Select(e => e.PropertyName).Should().Contain("Name");
        new UpdatePortfolioValidator().Validate(new UpdatePortfolioCommand(Guid.Empty, "", null)).Errors.Select(e => e.PropertyName).Should().Contain(["Id", "Name"]);
        new PutPortfolioItemValidator().Validate(new PutPortfolioItemCommand(Guid.Empty, "", "")).Errors.Select(e => e.PropertyName).Should().Contain(["PortfolioId", "Market", "Ticker"]);
        new ReorderPortfoliosValidator().Validate(new ReorderPortfoliosCommand([.. Enumerable.Range(0, 101).Select(_ => Guid.CreateVersion7())])).IsValid.Should().BeFalse();
        new ReorderPortfolioItemsValidator().Validate(new ReorderPortfolioItemsCommand(Guid.CreateVersion7(), [.. Enumerable.Range(0, 101).Select(i => new StockKey("XNAS", $"T{i}"))])).IsValid.Should().BeFalse();
        new CreatePortfolioValidator().Validate(new CreatePortfolioCommand("Ok", null, Key())).IsValid.Should().BeTrue();
    }

    [Fact]
    public void TheReorderValidators_AllowNoMoreThanTheCeilingOfWhatTheyOrder()
    {
        new ReorderPortfoliosValidator().Validate(new ReorderPortfoliosCommand([.. Enumerable.Range(0, LimitCeilings.Portfolios).Select(_ => Guid.CreateVersion7())])).IsValid.Should().BeTrue();
        new ReorderPortfoliosValidator().Validate(new ReorderPortfoliosCommand([.. Enumerable.Range(0, LimitCeilings.Portfolios + 1).Select(_ => Guid.CreateVersion7())])).IsValid.Should().BeFalse();
        new ReorderPortfolioItemsValidator().Validate(new ReorderPortfolioItemsCommand(Guid.CreateVersion7(), [.. Enumerable.Range(0, LimitCeilings.ItemsPerPortfolio + 1).Select(i => new StockKey("XNAS", $"T{i}"))])).IsValid.Should().BeFalse();
    }
}
