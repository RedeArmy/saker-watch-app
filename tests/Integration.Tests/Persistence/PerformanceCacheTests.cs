using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Performance;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Persistence;
using SakerWatch.MarketData.Infrastructure.Providers;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class PerformanceCacheTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset _loaded = new(2026, 10, 16, 22, 0, 0, TimeSpan.Zero);

    private readonly RedisContainer _redis = new RedisBuilder(ComposeImages.For("redis")).Build();
    private ConnectionMultiplexer? _connection;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        await _redis.DisposeAsync();
    }

    private sealed class CountingReader(IPerformanceReader inner) : IPerformanceReader
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyDictionary<StockId, PerformanceReport>> ReadAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken)
        {
            Calls++;
            return inner.ReadAsync(stocks, cancellationToken);
        }
    }

    private static async Task<Guid> SeedLoadedStockAsync(string admin, string ticker, DateOnly from, DateOnly to, DateTimeOffset stamp)
    {
        var id = Guid.CreateVersion7();
        await admin.ExecuteAsync(
            "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (@id, 'XNAS', @ticker, @ticker, 'USD', 'America/New_York', 'equity', true, now())",
            ("id", id), ("ticker", ticker));
        await SeedDaysAsync(admin, id, from, to);
        await admin.ExecuteAsync(
            "INSERT INTO marketdata.ingestion_state (stock_id, status, last_success_at, last_attempt_at) VALUES (@id, 'ok', @at, @at)",
            ("id", id), ("at", stamp));
        return id;
    }

    // Every weekday from `from` to `to`, with an adjusted close that moves every day.
    private static Task<int> SeedDaysAsync(string admin, Guid id, DateOnly from, DateOnly to) => admin.ExecuteAsync(
        """
        INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at)
        SELECT @id, d::date, a, a + 1, a - 1, a, a, 1000, 'twelvedata', true, now()
        FROM (SELECT d, 100 + 0.5 * (row_number() OVER (ORDER BY d) % 37) + 0.01 * row_number() OVER (ORDER BY d) AS a
              FROM generate_series(@from::date, @to::date, interval '1 day') AS g(d) WHERE extract(isodow FROM d) < 6) s
        ON CONFLICT DO NOTHING
        """,
        ("id", id), ("from", from), ("to", to));

    private async Task<(CachedPerformanceReader Reader, CountingReader Counter, DbSession Session)> BuildAsync(string connection, IPerformanceCache? cache = null)
    {
        var session = new DbSession(Options.Create(new PostgresOptions { Postgres = connection }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        var counter = new CountingReader(new PerformanceReader(session, ExchangeCalendars.Default(), Options.Create(new PerformanceOptions()), NullLogger<PerformanceReader>.Instance));
        var reader = new CachedPerformanceReader(
            counter,
            new IngestionStamps(session),
            cache ?? new RedisPerformanceCache(_connection!, NullLogger<RedisPerformanceCache>.Instance),
            Options.Create(new PerformanceOptions()),
            Options.Create(new PerformanceCacheOptions()));
        return (reader, counter, session);
    }

    [Fact]
    public async Task Read_TwiceAfterOneLoad_ComputesOnceAndServesTheSameNumbers()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid id = await SeedLoadedStockAsync(db.Admin, "AAPL", new DateOnly(2024, 1, 1), new DateOnly(2026, 10, 16), _loaded);
        (CachedPerformanceReader reader, CountingReader counter, DbSession session) = await BuildAsync(db.Api);
        await using (session)
        {
            PerformanceReport first = (await reader.ReadAsync([StockId.Create(id)], CancellationToken.None))[StockId.Create(id)];
            PerformanceReport second = (await reader.ReadAsync([StockId.Create(id)], CancellationToken.None))[StockId.Create(id)];

            counter.Calls.Should().Be(1, "the second read is served from Redis");
            second.Latest.Should().Be(first.Latest);
            second.Windows.Should().Equal(first.Windows, "a report read back is exactly the one computed: dates, closes, changes and gaps");
        }
    }

    [Fact]
    public async Task Read_AfterAnotherLoad_ComputesTheNewDayInsteadOfServingTheOldOne()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid id = await SeedLoadedStockAsync(db.Admin, "MSFT", new DateOnly(2024, 1, 1), new DateOnly(2026, 10, 16), _loaded);
        (CachedPerformanceReader reader, CountingReader counter, DbSession session) = await BuildAsync(db.Api);
        await using (session)
        {
            PerformanceReport before = (await reader.ReadAsync([StockId.Create(id)], CancellationToken.None))[StockId.Create(id)];

            // The daily refresh of the next session: a new price and the new stamp, as DailyRefresh writes them in one unit of work.
            await SeedDaysAsync(db.Admin, id, new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 19));
            await db.Admin.ExecuteAsync("UPDATE marketdata.ingestion_state SET last_success_at = @at WHERE stock_id = @id", ("id", id), ("at", _loaded.AddDays(3)));
            PerformanceReport after = (await reader.ReadAsync([StockId.Create(id)], CancellationToken.None))[StockId.Create(id)];

            counter.Calls.Should().Be(2);
            before.Latest!.Date.Should().Be(new DateOnly(2026, 10, 16));
            after.Latest!.Date.Should().Be(new DateOnly(2026, 10, 19));
        }
    }

    [Fact]
    public async Task Read_WhenRedisIsDown_StillAnswersFromTheDatabase()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Guid id = await SeedLoadedStockAsync(db.Admin, "NVDA", new DateOnly(2024, 1, 1), new DateOnly(2026, 10, 16), _loaded);
        ConnectionMultiplexer down = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=200,asyncTimeout=200");
        (CachedPerformanceReader reader, CountingReader counter, DbSession session) = await BuildAsync(db.Api, new RedisPerformanceCache(down, NullLogger<RedisPerformanceCache>.Instance));
        await using (session)
        {
            IReadOnlyDictionary<StockId, PerformanceReport> reports = await reader.ReadAsync([StockId.Create(id)], CancellationToken.None);

            reports[StockId.Create(id)].Latest!.Date.Should().Be(new DateOnly(2026, 10, 16));
            counter.Calls.Should().Be(1);
        }
    }

    [Fact]
    public async Task Get_AnEntryThatIsNotAReport_IsAMissAndNotAnError()
    {
        var cache = new RedisPerformanceCache(_connection!, NullLogger<RedisPerformanceCache>.Instance);
        var key = new PerformanceCacheKey(StockId.Create(Guid.CreateVersion7()), _loaded, 10);
        await _connection!.GetDatabase().StringSetAsync($"v1:perf:{key.Stock.Value}:{key.Stamp.UtcTicks}:10:{PerformanceRules.CacheTag}", "{\"Format\":1,\"LatestDate\":\"not a date\"}");
        await _connection.GetDatabase().StringSetAsync($"v1:perf:{key.Stock.Value}:{key.Stamp.UtcTicks}:11:{PerformanceRules.CacheTag}", "garbage");

        (await cache.GetManyAsync([key], CancellationToken.None)).Should().BeEmpty();
        (await cache.GetManyAsync([key with { MaxGapDays = 11 }], CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Set_KeepsTheReportWithAnExpiry()
    {
        var cache = new RedisPerformanceCache(_connection!, NullLogger<RedisPerformanceCache>.Instance);
        var key = new PerformanceCacheKey(StockId.Create(Guid.CreateVersion7()), _loaded, 10);
        PriceSample latest = PriceSample.Create(new DateOnly(2026, 10, 16), 120.5m).Value;
        var report = new PerformanceReport(latest, [.. Enum.GetValues<PerformanceWindow>().Select(window => WindowPerformance.Missing(window, PerformanceGap.HistoryGap))]);

        await cache.SetManyAsync(new Dictionary<PerformanceCacheKey, PerformanceReport> { [key] = report }, TimeSpan.FromHours(26), CancellationToken.None);

        TimeSpan? ttl = await _connection!.GetDatabase().KeyTimeToLiveAsync($"v1:perf:{key.Stock.Value}:{key.Stamp.UtcTicks}:10:{PerformanceRules.CacheTag}");
        ttl.Should().BeGreaterThan(TimeSpan.FromHours(25));
        (await cache.GetManyAsync([key], CancellationToken.None))[key.Stock].Windows.Should().Equal(report.Windows);
    }

    [Fact]
    public async Task Get_AReportMadeByOtherRulesOfTheCalculation_IsAMiss()
    {
        var cache = new RedisPerformanceCache(_connection!, NullLogger<RedisPerformanceCache>.Instance);
        var current = new PerformanceCacheKey(StockId.Create(Guid.CreateVersion7()), _loaded, 10);
        var older = new PerformanceCacheKey(StockId.Create(Guid.CreateVersion7()), _loaded, 10);
        PriceSample latest = PriceSample.Create(new DateOnly(2026, 10, 16), 120.5m).Value;
        var report = new PerformanceReport(latest, [.. Enum.GetValues<PerformanceWindow>().Select(window => WindowPerformance.Missing(window, PerformanceGap.HistoryGap))]);
        await cache.SetManyAsync(new Dictionary<PerformanceCacheKey, PerformanceReport> { [current] = report }, TimeSpan.FromHours(26), CancellationToken.None);
        // The very same entry, as a calculator with other rules (another fingerprint) would have left it.
        RedisValue stored = await _connection!.GetDatabase().StringGetAsync($"v1:perf:{current.Stock.Value}:{current.Stamp.UtcTicks}:10:{PerformanceRules.CacheTag}");
        await _connection.GetDatabase().StringSetAsync($"v1:perf:{older.Stock.Value}:{older.Stamp.UtcTicks}:10:c{PerformanceCalculator.Version}.00000000", stored);

        IReadOnlyDictionary<StockId, PerformanceReport> found = await cache.GetManyAsync([current, older], CancellationToken.None);

        found.Keys.Should().BeEquivalentTo([current.Stock], "a report made by other rules is never served as the current one");
    }
}
