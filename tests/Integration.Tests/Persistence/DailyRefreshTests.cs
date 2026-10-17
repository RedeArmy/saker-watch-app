using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Persistence;
using SakerWatch.MarketData.Infrastructure.Providers.Fake;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class DailyRefreshTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static readonly ProviderListingKey _providerKey = new("XNGS", "AAPL");

    private sealed class RecordingEnqueuer : IJobEnqueuer
    {
        public List<(string Key, object Payload, JobEnqueueOptions? Options)> Queued { get; } = [];

        public Task<bool> EnqueueAsync<TPayload>(string jobKey, TPayload payload, JobEnqueueOptions? options, CancellationToken cancellationToken)
            where TPayload : notnull
        {
            bool created = Queued.TrueForAll(q => q.Key != jobKey);
            if (created)
            {
                Queued.Add((jobKey, payload, options));
            }

            return Task.FromResult(created);
        }
    }

    private sealed record World(string Admin, string Worker, StockId Id, FakeMarketDataProvider Provider, FakeTimeProvider Time, RecordingEnqueuer Jobs);

    // Thursday 2026-10-15 23:30 UTC: Thursday closed at 20:00 UTC and settled at 23:00, so a backfill now stores Thursday as final.
    private static async Task<World> SetUpAsync(TimescalePostgresFixture postgres)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = StockId.Create(Guid.CreateVersion7());
        await db.Admin.ExecuteAsync(
            "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (@id, 'XNAS', 'AAPL', 'Apple', 'USD', 'America/New_York', 'equity', true, now())", ("id", id.Value));
        await db.Admin.ExecuteAsync(
            "INSERT INTO marketdata.symbol_aliases (provider, provider_mic, provider_symbol, stock_id, created_at) VALUES ('fake', 'XNGS', 'AAPL', @id, now())", ("id", id.Value));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 23, 30, 0, TimeSpan.Zero));
        var provider = new FakeMarketDataProvider(ExchangeCalendars.Default(), clock)
            .WithListing(new ProviderListing(_providerKey, "Apple", "USD", "America/New_York", AssetType.Equity), 100m);
        return new World(db.Admin, db.Worker, id, provider, clock, new RecordingEnqueuer());
    }

    private static MarketDataProviderRouter RouterFor(World world) =>
        new([world.Provider], Options.Create(new MarketDataProvidersOptions { Markets = new() { ["XNAS"] = "fake" } }));

    private static ListingIngestion IngestionFor(DbSession session, World world) =>
        new(new StockCatalog(session), new SymbolAliases(session), RouterFor(world), ExchangeCalendars.Default(), new IngestionState(session), Options.Create(new IngestionOptions()));

    private static async Task InSessionAsync(World world, Func<DbSession, Task> action)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = world.Worker }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        await action(session);
        await session.CommitAsync(CancellationToken.None);
    }

    private static Task BackfillAsync(World world) => InSessionAsync(world, session =>
        new BackfillStockHandler(IngestionFor(session, world), new PriceStore(session), new IngestionState(session), Options.Create(new IngestionOptions()), world.Time, NullLogger<BackfillStockHandler>.Instance)
            .HandleAsync(new BackfillStockJob(world.Id.Value), Context(), CancellationToken.None));

    private static Task RefreshAsync(World world, DateOnly day) => InSessionAsync(world, session =>
        new RefreshStockHandler(IngestionFor(session, world), new PriceStore(session), new IngestionState(session), world.Jobs, Options.Create(new IngestionOptions()), world.Time, NullLogger<RefreshStockHandler>.Instance)
            .HandleAsync(new RefreshStockJob(world.Id.Value, day), Context(), CancellationToken.None));

    private static JobContext Context() => new(Guid.CreateVersion7(), "k", 1, 5, null);

    private static async Task<long> RowsAsync(World world) =>
        await world.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily WHERE stock_id = @id", ("id", world.Id.Value));

    [Fact]
    public async Task Refresh_OfAListingWithNoHistory_HandsItToTheBackfillAndCallsNoProvider()
    {
        World world = await SetUpAsync(postgres);

        await RefreshAsync(world, new DateOnly(2026, 10, 15));

        world.Provider.Calls.Should().Be(0);
        (string key, object payload, JobEnqueueOptions? options) = world.Jobs.Queued.Should().ContainSingle().Subject;
        key.Should().Be(BackfillStockJob.KeyFor(world.Id));
        payload.Should().Be(new BackfillStockJob(world.Id.Value));
        options!.MaxAttempts.Should().Be(8);
        (await RowsAsync(world)).Should().Be(0);
    }

    [Fact]
    public async Task Refresh_AddsTheSessionThatSettled_AndKeepsTheHistory()
    {
        World world = await SetUpAsync(postgres);
        await BackfillAsync(world);
        long before = await RowsAsync(world);
        world.Time.Advance(TimeSpan.FromHours(36)); // Saturday noon UTC: Friday 2026-10-16 has settled

        await RefreshAsync(world, new DateOnly(2026, 10, 16));

        (await RowsAsync(world)).Should().Be(before + 1);
        (await world.Admin.ScalarAsync<DateOnly>("SELECT max(trade_date) FROM marketdata.price_daily")).Should().Be(new DateOnly(2026, 10, 16));
        (await world.Admin.ScalarAsync<bool>("SELECT is_final FROM marketdata.price_daily WHERE trade_date = '2026-10-16'")).Should().BeTrue();
        world.Jobs.Queued.Should().BeEmpty("nothing was rewritten, so no reload");
    }

    [Fact]
    public async Task Refresh_RunTwice_LeavesTheSameRows_AndQueuesNothing()
    {
        World world = await SetUpAsync(postgres);
        await BackfillAsync(world);
        world.Time.Advance(TimeSpan.FromHours(36));

        await RefreshAsync(world, new DateOnly(2026, 10, 16));
        long first = await RowsAsync(world);
        await RefreshAsync(world, new DateOnly(2026, 10, 16));

        (await RowsAsync(world)).Should().Be(first);
        world.Jobs.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_ADayStillSettling_IsStoredProvisionalAndBecomesFinalOnALaterRefresh()
    {
        World world = await SetUpAsync(postgres);
        await BackfillAsync(world);
        world.Time.Advance(TimeSpan.FromHours(21).Add(TimeSpan.FromMinutes(30))); // Friday 21:00 UTC: an hour after the close
        await RefreshAsync(world, new DateOnly(2026, 10, 15));
        (await world.Admin.ScalarAsync<bool>("SELECT is_final FROM marketdata.price_daily WHERE trade_date = '2026-10-16'")).Should().BeFalse();

        world.Time.Advance(TimeSpan.FromHours(15)); // Saturday noon
        await RefreshAsync(world, new DateOnly(2026, 10, 16));

        (await world.Admin.ScalarAsync<bool>("SELECT is_final FROM marketdata.price_daily WHERE trade_date = '2026-10-16'")).Should().BeTrue();
        world.Jobs.Queued.Should().BeEmpty("a provisional day settling is not a rewrite of the history");
    }

    [Fact]
    public async Task Refresh_WhenTheProviderRewroteTheAdjustedHistory_QueuesAFullReload_AndStoresTheFreshOverlap()
    {
        World world = await SetUpAsync(postgres);
        await BackfillAsync(world);
        decimal before = await world.Admin.ScalarAsync<decimal>("SELECT adjusted_close FROM marketdata.price_daily WHERE trade_date = '2026-10-13'");
        world.Provider.WithDividend(_providerKey, new DateOnly(2026, 10, 16), 0.99m);
        world.Time.Advance(TimeSpan.FromHours(36));

        await RefreshAsync(world, new DateOnly(2026, 10, 16));

        (string key, object payload, _) = world.Jobs.Queued.Should().ContainSingle().Subject;
        key.Should().Be(BackfillStockJob.ReloadKeyFor(world.Id, new DateOnly(2026, 10, 16)));
        payload.Should().Be(new BackfillStockJob(world.Id.Value));
        decimal after = await world.Admin.ScalarAsync<decimal>("SELECT adjusted_close FROM marketdata.price_daily WHERE trade_date = '2026-10-13'");
        after.Should().BeApproximately(before * 0.99m, 0.0001m, "the overlap days already carry the new adjusted close");
        (await world.Admin.ScalarAsync<decimal>("SELECT close FROM marketdata.price_daily WHERE trade_date = '2026-10-13'")).Should().Be(before, "the raw close never changes");
    }

    [Fact]
    public async Task Refresh_TheReloadKeyCarriesTheDay_SoTheFinishedFirstBackfillDoesNotBlockIt()
    {
        var stock = StockId.Create(Guid.CreateVersion7());

        BackfillStockJob.ReloadKeyFor(stock, new DateOnly(2026, 10, 16)).Should().NotBe(BackfillStockJob.KeyFor(stock));
        BackfillStockJob.ReloadKeyFor(stock, new DateOnly(2026, 10, 16)).Should().NotBe(BackfillStockJob.ReloadKeyFor(stock, new DateOnly(2026, 10, 17)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refresh_AFailure_RetriesWhenRetryingCanHelp_AndIsRecordedWhenItCannot(bool retryable)
    {
        World world = await SetUpAsync(postgres);
        await BackfillAsync(world);
        world.Time.Advance(TimeSpan.FromHours(36));
        world.Provider.FailNext(retryable ? ProviderErrors.RateLimited("fake") : ProviderErrors.PlanRestricted("fake"));

        Func<Task> act = () => RefreshAsync(world, new DateOnly(2026, 10, 16));

        if (retryable)
        {
            await act.Should().ThrowAsync<IngestionRetryException>();
        }
        else
        {
            await act();
            (await world.Admin.ScalarAsync<string>("SELECT status FROM marketdata.ingestion_state")).Should().Be("failed");
        }
    }

    [Fact]
    public async Task FanOut_QueuesOneRefreshPerIngestableListing_NotTheFailedOrInactiveOnes_AndNothingTheSecondTime()
    {
        World world = await SetUpAsync(postgres);
        var failed = Guid.CreateVersion7();
        var inactive = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        const string insert = "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (@id, @mic, @ticker, 'x', 'USD', 'America/New_York', 'equity', @active, now())";
        await world.Admin.ExecuteAsync(insert, ("id", failed), ("mic", "XNAS"), ("ticker", "FAIL"), ("active", true));
        await world.Admin.ExecuteAsync(insert, ("id", inactive), ("mic", "XNAS"), ("ticker", "GONE"), ("active", false));
        await world.Admin.ExecuteAsync(insert, ("id", other), ("mic", "XNYS"), ("ticker", "KO"), ("active", true));
        await world.Admin.ExecuteAsync("INSERT INTO marketdata.ingestion_state (stock_id, status, failure_code) VALUES (@id, 'failed', 'x')", ("id", failed));
        var day = new DateOnly(2026, 10, 16);

        for (int run = 0; run < 2; run++)
        {
            await InSessionAsync(world, session =>
                new IngestMarketDayHandler(new StockCatalog(session), world.Jobs, NullLogger<IngestMarketDayHandler>.Instance)
                    .HandleAsync(new IngestMarketDayJob("XNAS", day), Context(), CancellationToken.None));
        }

        world.Jobs.Queued.Select(q => q.Key).Should().Equal(RefreshStockJob.KeyFor(world.Id, day));
        world.Jobs.Queued[0].Payload.Should().Be(new RefreshStockJob(world.Id.Value, day));
    }

    [Fact]
    public async Task Refresh_ForAStockThatLeftTheCatalog_EndsQuietly()
    {
        World world = await SetUpAsync(postgres);
        var ghost = world with { Id = StockId.Create(Guid.CreateVersion7()) };

        await RefreshAsync(ghost, new DateOnly(2026, 10, 15));

        ghost.Provider.Calls.Should().Be(0);
        ghost.Jobs.Queued.Should().BeEmpty();
    }

    [Fact]
    public void TheJobKeys_AreDeterministicPerMarketDayAndListing()
    {
        var stock = StockId.Create(Guid.CreateVersion7());
        var day = new DateOnly(2026, 10, 16);

        IngestMarketDayJob.KeyFor(Mic.Parse("xnas"), day).Should().Be("marketdata.ingest-daily:XNAS:20261016");
        RefreshStockJob.KeyFor(stock, day).Should().Be(RefreshStockJob.KeyFor(stock, day)).And.EndWith(":20261016");
        RefreshStockJob.KeyFor(stock, day).Should().NotBe(RefreshStockJob.KeyFor(stock, day.AddDays(1)));
    }
}

[Trait("Category", "Integration")]
public sealed class FreshnessReaderTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    [Fact]
    public async Task LatestPerListing_ReportsTheNewestDayOfEachIngestableListing_AndNoneForOneWithoutHistory()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid[] ids = [Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()];
        const string insert = "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (@id, @mic, @ticker, 'x', 'USD', 'America/New_York', 'equity', @active, now())";
        await db.Admin.ExecuteAsync(insert, ("id", ids[0]), ("mic", "XNAS"), ("ticker", "WITH"), ("active", true));
        await db.Admin.ExecuteAsync(insert, ("id", ids[1]), ("mic", "XNAS"), ("ticker", "NONE"), ("active", true));
        await db.Admin.ExecuteAsync(insert, ("id", ids[2]), ("mic", "XNAS"), ("ticker", "GONE"), ("active", false));
        await db.Admin.ExecuteAsync(insert, ("id", ids[3]), ("mic", "XNAS"), ("ticker", "FAIL"), ("active", true));
        await db.Admin.ExecuteAsync(insert, ("id", ids[4]), ("mic", "XNYS"), ("ticker", "KO"), ("active", true));
        await db.Admin.ExecuteAsync("INSERT INTO marketdata.ingestion_state (stock_id, status, failure_code) VALUES (@id, 'failed', 'x')", ("id", ids[3]));
        foreach (string day in new[] { "2026-10-13", "2026-10-15", "2026-10-14" })
        {
            await db.Admin.ExecuteAsync(
                "INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at) VALUES (@id, @day::date, 10, 11, 9, 10, 10, 1, 'fake', true, now())",
                ("id", ids[0]), ("day", day));
        }

        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = db.Worker }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        IReadOnlyList<ListingLatest> listings = await new FreshnessReader(session).LatestPerListingAsync(Mic.Parse("XNAS"), CancellationToken.None);
        await session.CommitAsync(CancellationToken.None);

        listings.Should().HaveCount(2, "the inactive, the failed and the other market's listing are left out");
        listings.Single(l => l.Stock.Value == ids[0]).Latest.Should().Be(new DateOnly(2026, 10, 15));
        listings.Single(l => l.Stock.Value == ids[1]).Latest.Should().BeNull();
    }
}
