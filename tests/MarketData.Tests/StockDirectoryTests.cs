using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Kernel.Time;
using SakerWatch.BuildingBlocks.Kernel.Transactions;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Contracts;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Tests;

public sealed class StockDirectoryTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class Provider(Func<string, Result<IReadOnlyList<ProviderListing>>> answer) : IMarketDataProvider
    {
        public List<string> Searched { get; } = [];

        public string Name => "fake";

        public Task<Result<IReadOnlyList<ProviderListing>>> SearchAsync(string symbol, CancellationToken cancellationToken)
        {
            Searched.Add(symbol);
            return Task.FromResult(answer(symbol));
        }

        public Task<Result<DailySeries>> GetDailyAsync(DailySeriesRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Catalog : IStockCatalog
    {
        public List<CatalogListing> Listings { get; } = [];

        public Task<CatalogStock?> FindAsync(StockId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CatalogListing?> FindByKeyAsync(Mic market, Ticker ticker, CancellationToken cancellationToken) =>
            Task.FromResult(Listings.FirstOrDefault(l => l.Market == market && l.Ticker == ticker));

        public Task<IReadOnlyList<CatalogListing>> FindManyAsync(IReadOnlyCollection<StockId> ids, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<StockId>> ListIngestableAsync(Mic market, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Registry(Guid? existing = null) : IStockRegistry
    {
        public List<(Stock Stock, string Provider, ProviderListingKey Key)> Registered { get; } = [];

        public bool Throw { get; set; }

        public bool ListingTaken { get; set; }

        public Task<StockId> RegisterAsync(Stock stock, string provider, ProviderListingKey providerKey, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new InvalidOperationException("the database is down");
            }

            if (ListingTaken)
            {
                throw new ProviderListingTakenException();
            }

            Registered.Add((stock, provider, providerKey));
            return Task.FromResult(existing is { } id ? StockId.Create(id) : stock.Id);
        }
    }

    private sealed class Budget(bool grant = true, bool unreachable = false) : IProviderCallBudget
    {
        public List<(string Provider, BudgetLimits Limits, int Credits)> Spent { get; } = [];

        public List<(string Provider, int Credits)> Released { get; } = [];

        public Task<BudgetDecision> TryAcquireAsync(string provider, BudgetLimits limits, int credits, CancellationToken cancellationToken)
        {
            Spent.Add((provider, limits, credits));
            if (unreachable)
            {
                return Task.FromResult(BudgetDecision.AllowedUncounted);
            }

            return Task.FromResult(grant ? BudgetDecision.Allowed : new BudgetDecision(false, TimeSpan.FromSeconds(41.2)));
        }

        public Task ReleaseAsync(string provider, int credits, CancellationToken cancellationToken)
        {
            Released.Add((provider, credits));
            return Task.CompletedTask;
        }
    }

    private sealed class Cache : ISearchCache
    {
        public Dictionary<string, IReadOnlyList<CachedListing>> Stored { get; } = [];

        public List<TimeSpan> Ttls { get; } = [];

        public Task<IReadOnlyList<CachedListing>?> GetAsync(string provider, string query, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.TryGetValue(query.ToLowerInvariant(), out IReadOnlyList<CachedListing>? found) ? found : null);

        public Task SetAsync(string provider, string query, IReadOnlyList<CachedListing> listings, TimeSpan ttl, CancellationToken cancellationToken)
        {
            Stored[query.ToLowerInvariant()] = listings;
            Ttls.Add(ttl);
            return Task.CompletedTask;
        }
    }

    private sealed class Enqueuer : IJobEnqueuer
    {
        public List<(string Key, object Payload, JobEnqueueOptions? Options)> Queued { get; } = [];

        public Task<bool> EnqueueAsync<TPayload>(string jobKey, TPayload payload, JobEnqueueOptions? options, CancellationToken cancellationToken)
            where TPayload : notnull
        {
            Queued.Add((jobKey, payload, options));
            return Task.FromResult(true);
        }
    }

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

    private sealed class User(Guid? id) : ICurrentUser
    {
        public Guid? UserId => id;
    }

    private static readonly Guid _user = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    private sealed record Rig(StockDirectory Directory, Provider Provider, Catalog Catalog, Registry Registry, Budget Budget, Cache Cache, Enqueuer Jobs, Budget Quota, Unit Unit);

    private static ProviderListing Listing(string mic, string symbol, string? name = "Apple Inc.", string? currency = "USD", AssetType type = AssetType.Equity) =>
        new(new ProviderListingKey(mic, symbol), name, currency, "Asia/Tokyo", type);

    private static Result<IReadOnlyList<ProviderListing>> Found(params ProviderListing[] listings) => listings;

    private static Rig Build(
        Func<string, Result<IReadOnlyList<ProviderListing>>> answer,
        bool grant = true,
        Guid? existingId = null,
        StockLookupOptions? lookup = null,
        string[]? markets = null,
        bool quotaLeft = true,
        Guid? user = null,
        bool quotaUnreachable = false,
        RegistrationFallback? fallback = null)
    {
        var provider = new Provider(answer);
        var catalog = new Catalog();
        var registry = new Registry(existingId);
        var budget = new Budget(grant);
        var cache = new Cache();
        var jobs = new Enqueuer();
        var clock = new FakeTimeProvider(_now);
        var router = new MarketDataProviderRouter(
            [provider],
            Options.Create(new MarketDataProvidersOptions { Markets = (markets ?? ["XNAS", "XNYS"]).ToDictionary(m => m, _ => "fake") }));
        var search = new ProviderSearch(budget, cache, Options.Create(lookup ?? new StockLookupOptions()), NullLogger<ProviderSearch>.Instance);
        var quota = new Budget(quotaLeft, quotaUnreachable);
        var unit = new Unit();
        var registration = new StockRegistration(
            registry,
            jobs,
            Options.Create(new IngestionOptions()),
            new RegistrationQuota(quota, new User(user ?? _user), Options.Create(lookup ?? new StockLookupOptions()), fallback ?? new RegistrationFallback(), clock),
            unit,
            NullLogger<StockRegistration>.Instance);
        var directory = new StockDirectory(catalog, search, registration, router, ExchangeCalendars.Default(), new TimeProviderIdGenerator(clock), clock);
        return new Rig(directory, provider, catalog, registry, budget, cache, jobs, quota, unit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AAPL; DROP TABLE stocks")]
    [InlineData("<script>")]
    [InlineData("AA\nPL")]
    [InlineData("a/b")]
    [InlineData("a?x=1&apikey=2")]
    [InlineData("12345678901234567890123456789012345678901")]
    public async Task Search_AQueryThatIsNotPlainText_IsRefusedWithoutSpendingASearch(string query)
    {
        Rig rig = Build(_ => Found());

        Result<IReadOnlyList<StockCandidate>> result = await rig.Directory.SearchAsync(query, CancellationToken.None);

        result.Error!.Code.Should().Be(StockDirectory.QueryInvalidCode);
        rig.Budget.Spent.Should().BeEmpty();
        rig.Provider.Searched.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_PutsTheExactTickerFirst_ThenTheTickersThatStartWithTheText_AndMapsNasdaqSegmentsToTheMarket()
    {
        Rig rig = Build(_ => Found(
            Listing("XNAS", "AAPLDXX", "Structured note"),
            Listing("XNGS", "AAPL"),
            Listing("XNYS", "AAP", "Advance Auto Parts"),
            Listing("XNMS", "AAPB", "Leveraged ETF", type: AssetType.Etf)));

        IReadOnlyList<StockCandidate> candidates = (await rig.Directory.SearchAsync(" aapl ", CancellationToken.None)).Value;

        candidates.Select(c => (c.Market, c.Ticker)).Should().Equal(("XNAS", "AAPL"), ("XNAS", "AAPLDXX"), ("XNYS", "AAP"), ("XNAS", "AAPB"));
        candidates[0].Should().Be(new StockCandidate("XNAS", "AAPL", "Apple Inc.", "USD", "equity"));
        candidates[3].AssetType.Should().Be("etf");
    }

    [Fact]
    public async Task Search_LeavesOutWhatTheCatalogWouldNotAccept()
    {
        Rig rig = Build(_ => Found(
            Listing("XNGS", "AAPL"),
            Listing("XTSE", "AAPL", currency: "CAD"), // a market that is not enabled here
            Listing("XNAS", "AAPL.TO", currency: "CAD"), // not the currency of the market
            Listing("XNAS", "AAPX", name: "  "), // no name
            Listing("XNAS", "AAPY", name: new string('n', 300)), // a name over the limit
            Listing("XNAS", "AA PL"), // not a ticker
            Listing("XNAS", "AAPZ", currency: null),
            Listing("XNAS", "AAPQ", currency: "usd"))); // case is normalized, so this one is fine

        IReadOnlyList<StockCandidate> candidates = (await rig.Directory.SearchAsync("AAP", CancellationToken.None)).Value;

        candidates.Select(c => c.Ticker).Should().Equal("AAPL", "AAPQ");
    }

    [Fact]
    public async Task Search_ListsEachStockOnce_AndNoMoreThanTheMaximum()
    {
        Rig rig = Build(
            _ => Found([Listing("XNGS", "AAPL"), Listing("XNMS", "AAPL"), .. Enumerable.Range(0, 40).Select(i => Listing("XNYS", $"T{i:D2}"))]),
            lookup: new StockLookupOptions { MaxResults = 5 });

        IReadOnlyList<StockCandidate> candidates = (await rig.Directory.SearchAsync("T", CancellationToken.None)).Value;

        candidates.Should().HaveCount(5);
        candidates.Count(c => c.Ticker == "AAPL").Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task Search_SpendsOneSearchOfTheSharedBudget_AndKeepsTheAnswerForTheConfiguredTime()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")), lookup: new StockLookupOptions { SearchesPerMinute = 7, SearchesPerDay = 70, CacheMinutes = 3 });

        await rig.Directory.SearchAsync("AAPL", CancellationToken.None);

        rig.Budget.Spent.Should().ContainSingle().Which.Should().Be(("fake_search", new BudgetLimits(7, 70), 1));
        rig.Cache.Ttls.Should().Equal(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task Search_RepeatedWithinTheCacheTime_DoesNotCallTheProviderOrSpendASearch()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")));
        await rig.Directory.SearchAsync("AAPL", CancellationToken.None);

        Result<IReadOnlyList<StockCandidate>> again = await rig.Directory.SearchAsync("aapl", CancellationToken.None);

        again.Value.Should().ContainSingle();
        rig.Provider.Searched.Should().ContainSingle();
        rig.Budget.Spent.Should().ContainSingle();
    }

    [Fact]
    public async Task Search_WithTheCacheOff_StoresNothing()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")), lookup: new StockLookupOptions { CacheMinutes = 0 });

        await rig.Directory.SearchAsync("AAPL", CancellationToken.None);
        await rig.Directory.SearchAsync("AAPL", CancellationToken.None);

        rig.Cache.Stored.Should().BeEmpty();
        rig.Provider.Searched.Should().HaveCount(2);
    }

    [Fact]
    public async Task Search_WhenTheSharedBudgetIsSpent_AsksTheCallerToWait_AndNeverCallsTheProvider()
    {
        Rig rig = Build(_ => Found(), grant: false);

        Error error = (await rig.Directory.SearchAsync("AAPL", CancellationToken.None)).Error!;

        error.Type.Should().Be(ErrorType.TooManyRequests);
        error.Code.Should().Be(StockDirectory.SearchBusyCode);
        error.Details![Error.RetryAfterSecondsKey].Should().Equal("42");
        rig.Provider.Searched.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_WhenTheProviderRefusesForRate_IsBusy_AndForAnythingElse_Unavailable_WithoutItsMessage()
    {
        Rig limited = Build(_ => ProviderErrors.RateLimited("fake", TimeSpan.FromSeconds(12)));
        Rig down = Build(_ => ProviderErrors.Unavailable("fake"));
        Rig refused = Build(_ => ProviderErrors.Unauthorized("fake"));
        Rig strange = Build(_ => ProviderErrors.InvalidResponse("fake"));

        Error busy = (await limited.Directory.SearchAsync("AAPL", CancellationToken.None)).Error!;
        Error[] others = [.. await Task.WhenAll(new[] { down, refused, strange }.Select(async r => (await r.Directory.SearchAsync("AAPL", CancellationToken.None)).Error!))];

        busy.Type.Should().Be(ErrorType.TooManyRequests);
        busy.Details![Error.RetryAfterSecondsKey].Should().Equal("12");
        others.Should().OnlyContain(e => e.Type == ErrorType.Unavailable && e.Code == StockDirectory.ProviderUnavailableCode);
        others.Should().OnlyContain(e => !e.Message.Contains("fake", StringComparison.OrdinalIgnoreCase) && !e.Message.Contains("key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_WithNoEnabledMarket_ReturnsNothing()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")), markets: []);

        (await rig.Directory.SearchAsync("AAPL", CancellationToken.None)).Value.Should().BeEmpty();
        rig.Provider.Searched.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "AAPL", "marketdata.mic.invalid")]
    [InlineData("XN", "AAPL", "marketdata.mic.invalid")]
    [InlineData("XNAS", "", "marketdata.ticker.invalid")]
    [InlineData("XNAS", "A B", "marketdata.ticker.invalid")]
    public async Task Ensure_AMarketOrTickerThatIsNotValid_IsRefusedBeforeAnythingElse(string market, string ticker, string code)
    {
        Rig rig = Build(_ => Found());

        Result<StockSummary> result = await rig.Directory.EnsureAsync(market, ticker, CancellationToken.None);

        result.Error!.Code.Should().Be(code);
        rig.Provider.Searched.Should().BeEmpty();
    }

    [Fact]
    public async Task Ensure_ACataloguedStock_IsReturnedWithoutCallingTheProvider()
    {
        Rig rig = Build(_ => Found());
        var id = StockId.Create(Guid.CreateVersion7());
        rig.Catalog.Listings.Add(new CatalogListing(id, Mic.Parse("XNAS"), Ticker.Parse("AAPL"), "Apple Inc.", CurrencyCode.Parse("USD"), AssetType.Equity, true));

        StockSummary summary = (await rig.Directory.EnsureAsync("xnas", " aapl ", CancellationToken.None)).Value;

        summary.Should().Be(new StockSummary(id.Value, "XNAS", "AAPL", "Apple Inc.", "USD", "equity"));
        rig.Provider.Searched.Should().BeEmpty();
        rig.Registry.Registered.Should().BeEmpty();
        rig.Jobs.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task Ensure_AStockNotInTheCatalog_IsRegisteredFromTheProvidersAnswer_AndItsHistoryIsQueued()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPLDXX", "Note"), Listing("XNGS", "AAPL", "  Apple Inc.  "), Listing("XNYS", "AAPL", "Other")));

        StockSummary summary = (await rig.Directory.EnsureAsync("XNAS", "aapl", CancellationToken.None)).Value;

        (Stock stock, string provider, ProviderListingKey key) = rig.Registry.Registered.Should().ContainSingle().Subject;
        (stock.Market.Value, stock.Ticker.Value, stock.Name, stock.Currency.Value, stock.TimeZoneId, stock.AssetType, stock.ListedAt)
            .Should().Be(("XNAS", "AAPL", "Apple Inc.", "USD", "America/New_York", AssetType.Equity, _now), "the time zone is ours, not the provider's Asia/Tokyo");
        (provider, key).Should().Be(("fake", new ProviderListingKey("XNGS", "AAPL")));
        summary.Should().Be(new StockSummary(stock.Id.Value, "XNAS", "AAPL", "Apple Inc.", "USD", "equity"));
        (string jobKey, object payload, JobEnqueueOptions? options) = rig.Jobs.Queued.Should().ContainSingle().Subject;
        jobKey.Should().Be(BackfillStockJob.KeyFor(stock.Id));
        payload.Should().Be(new BackfillStockJob(stock.Id.Value));
        options!.MaxAttempts.Should().Be(8);
    }

    [Fact]
    public async Task Ensure_ASymbolTheProviderGivesWithSpaces_IsRegisteredTrimmed_BecauseTheStoredAliasForbidsSpaces()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL ")));

        (await rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None)).IsSuccess.Should().BeTrue();

        rig.Registry.Registered.Should().ContainSingle().Which.Key.Should().Be(new ProviderListingKey("XNGS", "AAPL"));
    }

    [Fact]
    public async Task Ensure_WhenAnotherRequestRegisteredItFirst_UsesTheIdThatWon()
    {
        var winner = Guid.CreateVersion7();
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")), existingId: winner);

        StockSummary summary = (await rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None)).Value;

        summary.Id.Should().Be(winner);
        rig.Jobs.Queued.Single().Key.Should().Be(BackfillStockJob.KeyFor(StockId.Create(winner)));
    }

    [Fact]
    public async Task Ensure_AMarketThatIsNotEnabledOrHasNoCalendar_IsNotAvailable_WithoutCallingTheProvider()
    {
        Rig rig = Build(_ => Found(Listing("XTSE", "SHOP", currency: "CAD")), markets: ["XNAS", "XTSE"]);

        Error notRouted = (await rig.Directory.EnsureAsync("XNYS", "KO", CancellationToken.None)).Error!;
        Error noCalendar = (await rig.Directory.EnsureAsync("XTSE", "SHOP", CancellationToken.None)).Error!;

        notRouted.Code.Should().Be(StockDirectory.MarketNotAvailableCode);
        noCalendar.Code.Should().Be(StockDirectory.MarketNotAvailableCode);
        rig.Provider.Searched.Should().BeEmpty();
    }

    [Fact]
    public async Task Ensure_WhenTheProviderDoesNotListTheExactMarketAndTicker_IsNotFound_AndNothingIsRegistered()
    {
        Rig rig = Build(_ => Found(Listing("XNYS", "AAPL"), Listing("XNGS", "AAPLX"), Listing("XNGS", "AAPL", currency: "EUR")));

        Result<StockSummary> result = await rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None);

        result.Error!.Code.Should().Be(StockDirectory.StockNotFoundCode);
        rig.Registry.Registered.Should().BeEmpty();
        rig.Jobs.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task Ensure_WhenTheProviderCannotAnswer_RegistersNothing()
    {
        Rig down = Build(_ => ProviderErrors.Unavailable("fake"));
        Rig busy = Build(_ => Found(), grant: false);

        Error unavailable = (await down.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None)).Error!;
        Error wait = (await busy.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None)).Error!;

        (unavailable.Type, wait.Type).Should().Be((ErrorType.Unavailable, ErrorType.TooManyRequests));
        down.Registry.Registered.Should().BeEmpty();
        busy.Registry.Registered.Should().BeEmpty();
    }

    [Fact]
    public async Task Ensure_UsesTheSameCachedAnswerAsASearch()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")));
        await rig.Directory.SearchAsync("AAPL", CancellationToken.None);

        await rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None);

        rig.Provider.Searched.Should().ContainSingle("the search the user just made already answered it");
    }

    [Fact]
    public async Task Lookups_AreCountedByOperationAndOutcome()
    {
        var seen = new List<(string Operation, string Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "sakerwatch_marketdata_stock_lookups_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string operation = "", outcome = "";
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "operation")
                {
                    operation = (string)tag.Value!;
                }
                else
                {
                    outcome = (string)tag.Value!;
                }
            }

            lock (seen)
            {
                seen.Add((operation, outcome));
            }
        });
        listener.Start();
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")));

        await rig.Directory.SearchAsync("AAPL", CancellationToken.None);
        await rig.Directory.SearchAsync("bad;", CancellationToken.None);
        await rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None);
        await rig.Directory.EnsureAsync("XNYS", "ZZZ", CancellationToken.None);
        rig.Catalog.Listings.Add(new CatalogListing(StockId.Create(Guid.CreateVersion7()), Mic.Parse("XNAS"), Ticker.Parse("MSFT"), "Microsoft", CurrencyCode.Parse("USD"), AssetType.Equity, true));
        await rig.Directory.EnsureAsync("XNAS", "MSFT", CancellationToken.None);

        lock (seen)
        {
            seen.Should().Contain([("search", "success"), ("search", "invalid"), ("ensure", "registered"), ("ensure", "not_found"), ("ensure", "catalogued")]);
        }
    }

    [Fact]
    public async Task Ensure_RegisteringANewStock_TakesOneOfTheUsersRegistrationsOfTheDay()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")), lookup: new StockLookupOptions { RegistrationsPerUserPerDay = 7 });

        await rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None);

        rig.Quota.Spent.Should().ContainSingle().Which.Should().Be((RegistrationQuota.BudgetKey(_user), new BudgetLimits(7, 7), 1));
        RegistrationQuota.BudgetKey(_user).Should().Be("register_000000000000000000000000000000f1");
    }

    [Fact]
    public async Task Ensure_WhenTheUserHasUsedTheirRegistrationsOfTheDay_IsRefusedWithTheWait_AndNothingIsRegistered()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")), quotaLeft: false);

        Error error = (await rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None)).Error!;

        (error.Code, error.Type).Should().Be((StockDirectory.RegistrationLimitCode, ErrorType.TooManyRequests));
        rig.Registry.Registered.Should().BeEmpty();
        rig.Jobs.Queued.Should().BeEmpty("a refused registration queues no history, which is what spends the provider's credits");
    }

    [Fact]
    public async Task Ensure_ACataloguedStockOrATickerTheProviderDoesNotKnow_DoesNotTakeARegistration()
    {
        Rig known = Build(_ => Found());
        known.Catalog.Listings.Add(new CatalogListing(StockId.Create(Guid.CreateVersion7()), Mic.Parse("XNAS"), Ticker.Parse("AAPL"), "Apple Inc.", CurrencyCode.Parse("USD"), AssetType.Equity, true));
        Rig unknown = Build(_ => Found());

        await known.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None);
        await unknown.Directory.EnsureAsync("XNAS", "ZZZZ", CancellationToken.None);

        known.Quota.Spent.Should().BeEmpty();
        unknown.Quota.Spent.Should().BeEmpty("a wrong ticker costs a search and not a registration");
    }

    [Fact]
    public async Task Ensure_WhenAnotherRequestRegisteredTheSameStockFirst_GivesTheRegistrationBack()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")), existingId: Guid.CreateVersion7());

        await rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None);

        rig.Quota.Spent.Should().ContainSingle();
        rig.Quota.Released.Should().Equal((RegistrationQuota.BudgetKey(_user), 1));
    }

    [Fact]
    public async Task Ensure_WhenTheProvidersListingBelongsToAnotherStock_GivesTheRegistrationBack()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")));
        rig.Registry.ListingTaken = true;

        Error error = (await rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None)).Error!;

        error.Code.Should().Be(StockDirectory.ProviderListingTakenCode);
        rig.Quota.Released.Should().Equal((RegistrationQuota.BudgetKey(_user), 1));
    }

    [Fact]
    public async Task Ensure_ANewStock_KeepsTheRegistration()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")));

        await rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None);

        rig.Quota.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task Reserve_WhileTheSharedBudgetIsDown_CountsInTheProcess_AndHoldsTheCap()
    {
        var lookup = new StockLookupOptions { RegistrationsPerUserPerDay = 2 };
        var fallback = new RegistrationFallback();
        Rig first = Build(_ => Found(Listing("XNGS", "AAPL")), lookup: lookup, quotaUnreachable: true, fallback: fallback);
        Rig second = Build(_ => Found(Listing("XNGS", "AAPL")), lookup: lookup, quotaUnreachable: true, fallback: fallback);
        Rig third = Build(_ => Found(Listing("XNGS", "AAPL")), lookup: lookup, quotaUnreachable: true, fallback: fallback);

        Error? one = (await first.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None)).Error;
        Error? two = (await second.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None)).Error;
        Error three = (await third.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None)).Error!;

        (one, two).Should().Be(((Error?)null, (Error?)null));
        (three.Code, three.Type).Should().Be((StockDirectory.RegistrationLimitCode, ErrorType.TooManyRequests), "the third registration of the day is over the cap although Redis counted none");
        third.Registry.Registered.Should().BeEmpty();
    }

    [Fact]
    public void Fallback_CountsPerUserAndDay_ReleasesOne_AndForgetsAtMidnight()
    {
        var fallback = new RegistrationFallback();
        var ana = Guid.CreateVersion7();
        var luis = Guid.CreateVersion7();
        var noon = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        fallback.TryTake(ana, 1, noon).Granted.Should().BeTrue();
        BudgetDecision over = fallback.TryTake(ana, 1, noon);
        fallback.TryTake(luis, 1, noon).Granted.Should().BeTrue("another user has their own count");
        fallback.Release(ana, noon);
        bool afterRelease = fallback.TryTake(ana, 1, noon).Granted;
        BudgetDecision nextDay = fallback.TryTake(ana, 1, noon.AddDays(1));

        over.Granted.Should().BeFalse();
        over.RetryAfter.Should().Be(TimeSpan.FromHours(12));
        afterRelease.Should().BeTrue();
        nextDay.Granted.Should().BeTrue("the count of a day is not the count of the next");
    }

    [Fact]
    public async Task Search_WhenTheProviderIsRateLimitedForAnHour_TellsTheUserAtMostFiveMinutes()
    {
        Rig rig = Build(_ => ProviderErrors.RateLimited("fake", TimeSpan.FromHours(1)));

        Error error = (await rig.Directory.SearchAsync("aapl", CancellationToken.None)).Error!;

        error.Code.Should().Be(StockDirectory.SearchBusyCode);
        error.Details!["retry_after_seconds"].Should().Equal("300");
    }

    [Fact]
    public async Task Reserve_ForACallWithNoUser_IsNotLimited_AndSpendsNothing()
    {
        var quota = new Budget(grant: false);
        var registration = new RegistrationQuota(quota, new User(null), Options.Create(new StockLookupOptions()), new RegistrationFallback(), TimeProvider.System);

        (await registration.ReserveAsync(CancellationToken.None)).Should().BeNull();
        quota.Spent.Should().BeEmpty();
    }

    [Fact]
    public async Task Ensure_RegisteringAStock_WritesInOneShortUnitOfWorkOfItsOwn()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")));

        await rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None);

        (rig.Unit.Begun, rig.Unit.Committed, rig.Unit.RolledBack).Should().Be((1, 1, 0), "the stock and its first load are one atomic write, and nothing is held while the provider is asked");
    }

    [Fact]
    public async Task Ensure_WhenTheCatalogWriteFails_TheUnitIsRolledBack_AndTheFailureIsNotHidden()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")));
        rig.Registry.Throw = true;

        Func<Task> act = () => rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (rig.Unit.Committed, rig.Unit.RolledBack).Should().Be((0, 1));
        rig.Jobs.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task Ensure_WhenTheProvidersListingIsAlreadyAnotherStocksAlias_IsAConflict_NothingIsQueued_AndTheUnitIsRolledBack()
    {
        Rig rig = Build(_ => Found(Listing("XNGS", "AAPL")));
        rig.Registry.ListingTaken = true;

        Error error = (await rig.Directory.EnsureAsync("XNAS", "AAPL", CancellationToken.None)).Error!;

        (error.Code, error.Type).Should().Be((StockDirectory.ProviderListingTakenCode, ErrorType.Conflict));
        rig.Jobs.Queued.Should().BeEmpty();
        (rig.Unit.Committed, rig.Unit.RolledBack).Should().Be((0, 1));
    }
}
