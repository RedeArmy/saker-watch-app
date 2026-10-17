using AwesomeAssertions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Domain.Prices;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class PriceStoreTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static readonly DateTimeOffset _now = new(2026, 10, 17, 3, 0, 0, TimeSpan.Zero);

    private static PricePoint Point(DateOnly day, decimal close, decimal adjusted, bool final = true, long volume = 1000) =>
        PricePoint.Create(DailyBar.Create(day, close - 1, close + 2, close - 2, close, volume).Value, adjusted, final).Value;

    private static async Task<(StockId Id, string Worker, string Admin)> SeedAsync(TimescalePostgresFixture postgres)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = StockId.Create(Guid.CreateVersion7());
        await db.Admin.ExecuteAsync(
            "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (@id, 'XNAS', 'AAPL', 'Apple', 'USD', 'America/New_York', 'equity', true, now())",
            ("id", id.Value));
        return (id, db.Worker, db.Admin);
    }

    private static async Task<T> InSessionAsync<T>(string connection, Func<PriceStore, Task<T>> action)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = connection }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        T result = await action(new PriceStore(session));
        await session.CommitAsync(CancellationToken.None);
        return result;
    }

    [Fact]
    public async Task Upsert_StoresEveryDay_AndTheSameBatchTwiceIsStillOneRowPerDay()
    {
        (StockId id, string worker, string admin) = await SeedAsync(postgres);
        PricePoint[] points = [Point(new DateOnly(2026, 10, 14), 100m, 99m), Point(new DateOnly(2026, 10, 15), 101m, 100m), Point(new DateOnly(2026, 10, 16), 102m, 101m)];

        await InSessionAsync(worker, store => store.UpsertAsync(id, "twelvedata", points, _now, CancellationToken.None));
        await InSessionAsync(worker, store => store.UpsertAsync(id, "twelvedata", points, _now, CancellationToken.None));

        (await admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily WHERE stock_id = @id", ("id", id.Value))).Should().Be(3);
    }

    [Fact]
    public async Task Upsert_KeepsEveryValueExactly()
    {
        (StockId id, string worker, string admin) = await SeedAsync(postgres);
        PricePoint point = PricePoint.Create(DailyBar.Create(new DateOnly(2024, 6, 7), 1204.1m, 1210.99999m, 1198.00001m, 1208.88m, 438392000).Value, 120.54137m, true).Value;

        await InSessionAsync(worker, store => store.UpsertAsync(id, "twelvedata", [point], _now, CancellationToken.None));

        (await admin.ScalarAsync<decimal>("SELECT high FROM marketdata.price_daily WHERE stock_id = @id", ("id", id.Value))).Should().Be(1210.99999m);
        (await admin.ScalarAsync<decimal>("SELECT adjusted_close FROM marketdata.price_daily WHERE stock_id = @id", ("id", id.Value))).Should().Be(120.54137m);
        (await admin.ScalarAsync<long>("SELECT volume FROM marketdata.price_daily WHERE stock_id = @id", ("id", id.Value))).Should().Be(438392000);
        (await admin.ScalarAsync<string>("SELECT source FROM marketdata.price_daily WHERE stock_id = @id", ("id", id.Value))).Should().Be("twelvedata");
    }

    [Fact]
    public async Task Upsert_ARewrittenAdjustedClose_ReplacesTheOldOne()
    {
        (StockId id, string worker, _) = await SeedAsync(postgres);
        var day = new DateOnly(2026, 10, 9);
        await InSessionAsync(worker, store => store.UpsertAsync(id, "twelvedata", [Point(day, 100m, 98m)], _now, CancellationToken.None));

        await InSessionAsync(worker, store => store.UpsertAsync(id, "twelvedata", [Point(day, 100m, 97.5m)], _now.AddDays(30), CancellationToken.None));

        (await InSessionAsync(worker, store => store.AdjustedCloseAsync(id, day, CancellationToken.None))).Should().Be(97.5m, "a dividend rewrites the history backwards");
    }

    [Fact]
    public async Task Upsert_AProvisionalDay_NeverReplacesAFinalOne()
    {
        (StockId id, string worker, _) = await SeedAsync(postgres);
        var day = new DateOnly(2026, 10, 16);
        await InSessionAsync(worker, store => store.UpsertAsync(id, "twelvedata", [Point(day, 102m, 101m, final: true, volume: 59_000_000)], _now, CancellationToken.None));

        int written = await InSessionAsync(worker, store => store.UpsertAsync(id, "twelvedata", [Point(day, 100m, 99m, final: false, volume: 1_500_000)], _now.AddHours(1), CancellationToken.None));

        written.Should().Be(0, "the row stayed as it was");
        (await InSessionAsync(worker, store => store.AdjustedCloseAsync(id, day, CancellationToken.None))).Should().Be(101m);
    }

    [Fact]
    public async Task Upsert_AFinalDay_ReplacesTheProvisionalOne()
    {
        (StockId id, string worker, _) = await SeedAsync(postgres);
        var day = new DateOnly(2026, 10, 16);
        await InSessionAsync(worker, store => store.UpsertAsync(id, "twelvedata", [Point(day, 100m, 99m, final: false)], _now, CancellationToken.None));

        await InSessionAsync(worker, store => store.UpsertAsync(id, "twelvedata", [Point(day, 102m, 101m, final: true)], _now.AddDays(1), CancellationToken.None));

        LatestPrice? latest = await InSessionAsync(worker, store => store.LatestAsync(id, CancellationToken.None));
        latest.Should().Be(new LatestPrice(day, true));
        (await InSessionAsync(worker, store => store.AdjustedCloseAsync(id, day, CancellationToken.None))).Should().Be(101m);
    }

    [Fact]
    public async Task Upsert_ATwentyYearHistory_IsOneBatchOfFiveThousandRows()
    {
        (StockId id, string worker, string admin) = await SeedAsync(postgres);
        PricePoint[] history = [.. Enumerable.Range(0, 5000).Select(i => Point(new DateOnly(2006, 11, 30).AddDays(i), 50m + (i % 100), 49m + (i % 100)))];

        int written = await InSessionAsync(worker, store => store.UpsertAsync(id, "twelvedata", history, _now, CancellationToken.None));

        written.Should().Be(5000);
        (await admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily WHERE stock_id = @id", ("id", id.Value))).Should().Be(5000);
        (await admin.ScalarAsync<long>("SELECT count(DISTINCT tableoid) FROM marketdata.price_daily")).Should().BeGreaterThan(10, "twenty years span a chunk a year");
    }

    [Fact]
    public async Task Upsert_MoreThanOneBatch_IsSplitAndStillComplete()
    {
        (StockId id, string worker, string admin) = await SeedAsync(postgres);
        PricePoint[] history = [.. Enumerable.Range(0, 5200).Select(i => Point(new DateOnly(2006, 1, 1).AddDays(i), 50m, 49m))];

        int written = await InSessionAsync(worker, store => store.UpsertAsync(id, "twelvedata", history, _now, CancellationToken.None));

        written.Should().Be(5200);
        (await admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily WHERE stock_id = @id", ("id", id.Value))).Should().Be(5200);
    }

    [Fact]
    public async Task Latest_IsTheNewestDay_OrNullWhenNothingIsStored()
    {
        (StockId id, string worker, _) = await SeedAsync(postgres);
        (await InSessionAsync(worker, store => store.LatestAsync(id, CancellationToken.None))).Should().BeNull();

        await InSessionAsync(worker, store => store.UpsertAsync(
            id, "twelvedata", [Point(new DateOnly(2026, 10, 14), 100m, 99m), Point(new DateOnly(2026, 10, 15), 101m, 100m, final: false)], _now, CancellationToken.None));

        (await InSessionAsync(worker, store => store.LatestAsync(id, CancellationToken.None))).Should().Be(new LatestPrice(new DateOnly(2026, 10, 15), false));
        (await InSessionAsync(worker, store => store.AdjustedCloseAsync(id, new DateOnly(2026, 10, 1), CancellationToken.None))).Should().BeNull();
    }

    [Fact]
    public async Task Upsert_ForAStockThatIsNotInTheCatalog_IsRefusedByTheForeignKey()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var unknown = StockId.Create(Guid.CreateVersion7());

        Func<Task> act = () => InSessionAsync(db.Worker, store => store.UpsertAsync(unknown, "twelvedata", [Point(new DateOnly(2026, 10, 14), 100m, 99m)], _now, CancellationToken.None));

        (await act.Should().ThrowAsync<Npgsql.PostgresException>()).Which.SqlState.Should().Be("23503");
    }

    [Fact]
    public async Task Upsert_WithNoPoints_WritesNothing()
    {
        (StockId id, string worker, _) = await SeedAsync(postgres);

        (await InSessionAsync(worker, store => store.UpsertAsync(id, "twelvedata", [], _now, CancellationToken.None))).Should().Be(0);
    }
}
