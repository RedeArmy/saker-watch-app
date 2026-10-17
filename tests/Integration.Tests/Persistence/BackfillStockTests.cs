using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Persistence;
using SakerWatch.MarketData.Infrastructure.Providers.Fake;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class BackfillStockTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static readonly ProviderListingKey _providerKey = new("XNGS", "AAPL");

    // Saturday 2026-10-17 12:00 UTC: Friday the 16th closed at 20:00 UTC and its settle delay (3 h) has long passed.
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));

    private sealed record World(string Admin, string Worker, StockId Id, FakeMarketDataProvider Provider, FakeTimeProvider Time);

    private static async Task<World> SetUpAsync(TimescalePostgresFixture postgres, bool withAlias = true, string market = "XNAS", DateTimeOffset? at = null)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = StockId.Create(Guid.CreateVersion7());
        await db.Admin.ExecuteAsync(
            "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (@id, @mic, 'AAPL', 'Apple', 'USD', 'America/New_York', 'equity', true, now())",
            ("id", id.Value), ("mic", market));
        if (withAlias)
        {
            await db.Admin.ExecuteAsync(
                "INSERT INTO marketdata.symbol_aliases (provider, provider_mic, provider_symbol, stock_id, created_at) VALUES ('fake', 'XNGS', 'AAPL', @id, now())", ("id", id.Value));
        }

        FakeTimeProvider clock = at is { } start ? new FakeTimeProvider(start) : Clock();
        var provider = new FakeMarketDataProvider(ExchangeCalendars.Default(), clock)
            .WithListing(new ProviderListing(_providerKey, "Apple", "USD", "America/New_York", AssetType.Equity), 100m);
        return new World(db.Admin, db.Worker, id, provider, clock);
    }

    private static BackfillStockHandler HandlerFor(DbSession session, IMarketDataProviderRouter router, TimeProvider time)
    {
        var options = Options.Create(new IngestionOptions());
        var ingestion = new ListingIngestion(new StockCatalog(session), new SymbolAliases(session), router, ExchangeCalendars.Default(), new IngestionState(session), options);
        return new BackfillStockHandler(ingestion, new PriceStore(session), new IngestionState(session), options, time, NullLogger<BackfillStockHandler>.Instance);
    }

    private static async Task RunAsync(World world, Dictionary<string, string>? markets = null)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = world.Worker }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        var router = new MarketDataProviderRouter(
            [world.Provider],
            Options.Create(new MarketDataProvidersOptions { Markets = markets ?? new() { ["XNAS"] = "fake" } }));
        BackfillStockHandler handler = HandlerFor(session, router, world.Time);
        await handler.HandleAsync(new BackfillStockJob(world.Id.Value), new JobContext(Guid.CreateVersion7(), "k", 1, 8, null), CancellationToken.None);
        await session.CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Backfill_StoresTenYearsOfSessions_WithTheRawAndTheAdjustedClose()
    {
        World world = await SetUpAsync(postgres);

        await RunAsync(world);

        long rows = await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily WHERE stock_id = @id", ("id", world.Id.Value));
        rows.Should().BeInRange(2500, 2540, "ten years hold about 252 sessions each");
        (await world.Admin.ScalarAsync<string>("SELECT min(source) FROM marketdata.price_daily WHERE stock_id = @id", ("id", world.Id.Value))).Should().Be("fake");
        (await world.Admin.ScalarAsync<DateOnly>("SELECT max(trade_date) FROM marketdata.price_daily WHERE stock_id = @id", ("id", world.Id.Value))).Should().Be(new DateOnly(2026, 10, 16));
        (await world.Admin.ScalarAsync<string>("SELECT status FROM marketdata.ingestion_state WHERE stock_id = @id", ("id", world.Id.Value))).Should().Be("ok");
    }

    [Fact]
    public async Task Backfill_RunTwice_LeavesTheSameRows()
    {
        World world = await SetUpAsync(postgres);

        await RunAsync(world);
        long first = await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily");
        await RunAsync(world);

        (await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily")).Should().Be(first);
        (await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.ingestion_state")).Should().Be(1);
    }

    [Fact]
    public async Task Backfill_AllDaysBeforeTheLastClosedSessionAreFinal_AndSoIsThatSessionOnceSettled()
    {
        World world = await SetUpAsync(postgres);

        await RunAsync(world);

        (await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily WHERE NOT is_final")).Should().Be(0);
    }

    [Fact]
    public async Task Backfill_TheSessionThatClosedAMomentAgo_IsStoredAsProvisional()
    {
        // An hour after the Friday close (20:00 UTC), inside the 3 h settle delay.
        World world = await SetUpAsync(postgres, at: new DateTimeOffset(2026, 10, 16, 21, 0, 0, TimeSpan.Zero));

        await RunAsync(world);

        (await world.Admin.ScalarAsync<DateOnly>("SELECT max(trade_date) FROM marketdata.price_daily")).Should().Be(new DateOnly(2026, 10, 16));
        (await world.Admin.ScalarAsync<bool>("SELECT is_final FROM marketdata.price_daily WHERE trade_date = '2026-10-16'")).Should().BeFalse();
        (await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily WHERE NOT is_final")).Should().Be(1);
    }

    [Fact]
    public async Task Backfill_AdjustedAndRawCloses_AgreeOnTheLastDay()
    {
        World world = await SetUpAsync(postgres);

        await RunAsync(world);

        (await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily WHERE trade_date = '2026-10-16' AND close = adjusted_close")).Should().Be(1);
    }

    [Theory]
    [InlineData("RateLimited")]
    [InlineData("Unavailable")]
    public async Task Backfill_ARateLimitOrAnOutage_ThrowsSoTheQueueRetries_AndWritesNothing(string failure)
    {
        World world = await SetUpAsync(postgres);
        world.Provider.FailNext(failure == "RateLimited" ? ProviderErrors.RateLimited("fake", TimeSpan.FromSeconds(30)) : ProviderErrors.Unavailable("fake"));

        Func<Task> act = () => RunAsync(world);

        (await act.Should().ThrowAsync<IngestionRetryException>()).Which.Code.Should().StartWith("marketdata.provider.");
        (await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily")).Should().Be(0);
        (await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.ingestion_state")).Should().Be(0, "a failure that will be retried leaves no mark");
    }

    [Fact]
    public async Task Backfill_TheRawSeriesFailingAfterTheAdjustedOne_StillRetriesAndWritesNothing()
    {
        World world = await SetUpAsync(postgres);
        // The first call (adjusted) works; the second (raw) is rate limited.
        var skipFirst = new FailSecondCall(world.Provider);

        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = world.Worker }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        BackfillStockHandler handler = HandlerFor(
            session,
            new MarketDataProviderRouter([skipFirst], Options.Create(new MarketDataProvidersOptions { Markets = new() { ["XNAS"] = "fake" } })),
            world.Time);

        Func<Task> act = () => handler.HandleAsync(new BackfillStockJob(world.Id.Value), new JobContext(Guid.CreateVersion7(), "k", 1, 8, null), CancellationToken.None);

        await act.Should().ThrowAsync<IngestionRetryException>();
        await session.RollbackAsync(CancellationToken.None);
        (await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily")).Should().Be(0);
    }

    [Theory]
    [InlineData("SymbolNotFound")]
    [InlineData("PlanRestricted")]
    [InlineData("Permanent")]
    public async Task Backfill_AFailureRetryingCannotFix_IsRecordedWithItsCode_AndTheJobEnds(string failure)
    {
        World world = await SetUpAsync(postgres);
        world.Provider.FailNext(failure switch
        {
            "SymbolNotFound" => ProviderErrors.SymbolNotFound("fake", "AAPL"),
            "PlanRestricted" => ProviderErrors.PlanRestricted("fake"),
            _ => ProviderErrors.InvalidResponse("fake"),
        });

        await RunAsync(world);

        (await world.Admin.ScalarAsync<string>("SELECT status FROM marketdata.ingestion_state")).Should().Be("failed");
        (await world.Admin.ScalarAsync<string>("SELECT failure_code FROM marketdata.ingestion_state")).Should().StartWith("marketdata.provider.");
        (await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily")).Should().Be(0);
    }

    [Fact]
    public async Task Backfill_WithoutASymbolAlias_IsRecordedAndNeverCallsTheProvider()
    {
        World world = await SetUpAsync(postgres, withAlias: false);

        await RunAsync(world);

        (await world.Admin.ScalarAsync<string>("SELECT failure_code FROM marketdata.ingestion_state")).Should().Be(BackfillStockHandler.NoAliasCode);
        world.Provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Backfill_ForAMarketWithNoProvider_IsRecordedAsDisabled()
    {
        World world = await SetUpAsync(postgres);

        await RunAsync(world, markets: []);

        (await world.Admin.ScalarAsync<string>("SELECT failure_code FROM marketdata.ingestion_state")).Should().Be(BackfillStockHandler.MarketDisabledCode);
        world.Provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Backfill_ForAMarketWithoutACalendar_IsRecordedAndNeverCallsTheProvider()
    {
        World world = await SetUpAsync(postgres, market: "XTSE");

        await RunAsync(world, new() { ["XTSE"] = "fake" });

        (await world.Admin.ScalarAsync<string>("SELECT failure_code FROM marketdata.ingestion_state")).Should().Be(BackfillStockHandler.NoCalendarCode);
        world.Provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Backfill_AfterAPermanentFailureIsFixed_ClearsTheFailure()
    {
        World world = await SetUpAsync(postgres);
        world.Provider.FailNext(ProviderErrors.PlanRestricted("fake"));
        await RunAsync(world);

        await RunAsync(world);

        (await world.Admin.ScalarAsync<string>("SELECT status FROM marketdata.ingestion_state")).Should().Be("ok");
        (await world.Admin.ScalarAsync<bool>("SELECT failure_code IS NULL FROM marketdata.ingestion_state")).Should().BeTrue();
    }

    [Fact]
    public async Task Backfill_ForAStockNotInTheCatalog_EndsWithoutAFailureOrARow()
    {
        World world = await SetUpAsync(postgres);
        var ghost = new World(world.Admin, world.Worker, StockId.Create(Guid.CreateVersion7()), world.Provider, world.Time);

        await RunAsync(ghost);

        (await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.ingestion_state")).Should().Be(0);
        world.Provider.Calls.Should().Be(0);
    }

    [Fact]
    public void TheJobKey_IsDeterministicPerListing()
    {
        var id = StockId.Create(Guid.CreateVersion7());

        BackfillStockJob.KeyFor(id).Should().Be(BackfillStockJob.KeyFor(id)).And.StartWith("marketdata.backfill:");
        BackfillStockJob.KeyFor(id).Should().NotBe(BackfillStockJob.KeyFor(StockId.Create(Guid.CreateVersion7())));
    }

    // A provider whose second call fails with a rate limit, to exercise the raw request failing after the adjusted one worked.
    private sealed class FailSecondCall(FakeMarketDataProvider inner) : IMarketDataProvider
    {
        private int _daily;

        public string Name => inner.Name;

        public Task<Result<IReadOnlyList<ProviderListing>>> SearchAsync(string symbol, CancellationToken cancellationToken) =>
            inner.SearchAsync(symbol, cancellationToken);

        public Task<Result<DailySeries>> GetDailyAsync(DailySeriesRequest request, CancellationToken cancellationToken) =>
            ++_daily == 2
                ? Task.FromResult<Result<DailySeries>>(ProviderErrors.RateLimited("fake"))
                : inner.GetDailyAsync(request, cancellationToken);
    }
}
