using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lifecycle;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

/// <summary>The life of a listing against a real database: delisting, relisting, a change of ticker and the days the provider does not know a symbol (ADR-0039).</summary>
[Trait("Category", "Integration")]
public sealed class ListingLifecycleStoreTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static readonly DateTimeOffset _now = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly Mic _market = Mic.Parse("XNAS");

    private static async Task<StockId> SeedStockAsync(string admin, string ticker)
    {
        var id = StockId.Create(Guid.CreateVersion7());
        await admin.ExecuteAsync(
            "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (@id, 'XNAS', @ticker, @name, 'USD', 'America/New_York', 'equity', true, now())",
            ("id", id.Value), ("ticker", ticker), ("name", ticker + " Inc."));
        await admin.ExecuteAsync("INSERT INTO marketdata.symbol_aliases (provider, provider_mic, provider_symbol, stock_id, created_at) VALUES ('twelvedata', 'XNAS', @ticker, @id, now())", ("id", id.Value), ("ticker", ticker));
        return id;
    }

    private static async Task<T> InSessionAsync<T>(string connection, Func<DbSession, Task<T>> action)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = connection }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        T result = await action(session);
        await session.CommitAsync(CancellationToken.None);
        return result;
    }

    private static Task<bool> DelistAsync(MigratedDatabase db, StockId id, string source = "operator") =>
        InSessionAsync(db.Worker, session => new ListingLifecycleStore(session).DelistAsync(id, _now, new DateOnly(2026, 10, 1), source, CancellationToken.None));

    private static Task<RenameOutcome> RenameAsync(MigratedDatabase db, StockId id, string from, string to) =>
        InSessionAsync(db.Worker, session => new ListingLifecycleStore(session).RenameAsync(id, _market, Ticker.Parse(from), Ticker.Parse(to), new DateOnly(2026, 10, 1), to, _now, CancellationToken.None));

    [Fact]
    public async Task Delist_MarksTheStockInactiveWithTheMoment_RecordsTheAction_AndDoingItAgainChangesNothing()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId id = await SeedStockAsync(db.Admin, "OLD");

        bool first = await DelistAsync(db, id);
        bool again = await DelistAsync(db, id);

        (first, again).Should().Be((true, false));
        (await db.Admin.ScalarAsync<bool>("SELECT is_active FROM marketdata.stocks")).Should().BeFalse();
        (await db.Admin.ScalarAsync<DateTime>("SELECT delisted_at FROM marketdata.stocks")).Should().Be(_now.UtcDateTime);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.corporate_actions WHERE kind = 'delisting' AND effective_date = '2026-10-01' AND source = 'operator'")).Should().Be(1);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.corporate_actions")).Should().Be(1);
    }

    [Fact]
    public async Task Relist_BringsTheStockBack_ForgetsWhatStoppedIngestion_AndIsOnlyForADelistedStock()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId id = await SeedStockAsync(db.Admin, "BACK");
        await db.Admin.ExecuteAsync("INSERT INTO marketdata.ingestion_state (stock_id, status, failure_code, not_found_days, last_not_found_on) VALUES (@id, 'failed', 'marketdata.provider.symbol_not_found', 3, '2026-10-18')", ("id", id.Value));
        await DelistAsync(db, id);

        bool back = await InSessionAsync(db.Worker, session => new ListingLifecycleStore(session).RelistAsync(id, _now, CancellationToken.None));
        bool again = await InSessionAsync(db.Worker, session => new ListingLifecycleStore(session).RelistAsync(id, _now, CancellationToken.None));

        (back, again).Should().Be((true, false));
        (await db.Admin.ScalarAsync<bool>("SELECT is_active AND delisted_at IS NULL FROM marketdata.stocks")).Should().BeTrue();
        (await db.Admin.ScalarAsync<string>("SELECT status FROM marketdata.ingestion_state")).Should().Be("pending");
        (await db.Admin.ScalarAsync<int>("SELECT not_found_days FROM marketdata.ingestion_state")).Should().Be(0);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.ingestion_state WHERE failure_code IS NOT NULL OR last_not_found_on IS NOT NULL")).Should().Be(0);
    }

    [Fact]
    public async Task Rename_GivesTheStockItsNewTicker_KeepsTheOldInItsHistory_MovesTheProviderSymbol_AndRecordsTheAction()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId id = await SeedStockAsync(db.Admin, "FB");

        RenameOutcome outcome = await RenameAsync(db, id, "FB", "META");

        outcome.Should().Be(RenameOutcome.Done);
        (await db.Admin.ScalarAsync<string>("SELECT ticker FROM marketdata.stocks WHERE id = @id", ("id", id.Value))).Should().Be("META");
        (await db.Admin.ScalarAsync<Guid>("SELECT stock_id FROM marketdata.ticker_history WHERE market_mic = 'XNAS' AND ticker = 'FB'")).Should().Be(id.Value);
        (await db.Admin.ScalarAsync<string>("SELECT provider_symbol FROM marketdata.symbol_aliases WHERE stock_id = @id", ("id", id.Value))).Should().Be("META");
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.corporate_actions WHERE kind = 'symbol_change'")).Should().Be(1);
    }

    [Fact]
    public async Task ARetiredTicker_FindsTheStock_WithItsCurrentTicker_AndATickerInUseAlwaysWinsOverTheHistory()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId renamed = await SeedStockAsync(db.Admin, "FB");
        await RenameAsync(db, renamed, "FB", "META");

        var catalogFinds = async (string ticker) => await InSessionAsync(db.Api, session => new StockCatalog(session).FindByKeyAsync(_market, Ticker.Parse(ticker), CancellationToken.None));
        var byOld = await catalogFinds("FB");
        var byNew = await catalogFinds("META");
        StockId newcomer = await SeedStockAsync(db.Admin, "FB");
        var afterReuse = await catalogFinds("FB");

        (byOld.Id, byOld.Ticker.Value).Should().Be((renamed, "META"));
        byNew.Id.Should().Be(renamed);
        afterReuse.Id.Should().Be(newcomer, "a company that takes a ticker another stock retired is the stock of that ticker now");
        (await catalogFinds("NOPE")).Should().BeNull();
    }

    [Fact]
    public async Task Rename_BackToAnOldTicker_TakesItOutOfTheHistory_AndATakenTickerOrAnUnknownStockIsRefused()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId id = await SeedStockAsync(db.Admin, "AAA");
        StockId other = await SeedStockAsync(db.Admin, "BBB");

        RenameOutcome taken = await RenameAsync(db, id, "AAA", "BBB");
        await RenameAsync(db, id, "AAA", "CCC");
        RenameOutcome back = await RenameAsync(db, id, "CCC", "AAA");
        RenameOutcome stale = await RenameAsync(db, id, "CCC", "DDD");

        (taken, back, stale).Should().Be((RenameOutcome.TickerTaken, RenameOutcome.Done, RenameOutcome.StockNotFound));
        (await db.Admin.ScalarAsync<string>("SELECT ticker FROM marketdata.stocks WHERE id = @id", ("id", id.Value))).Should().Be("AAA");
        (await db.Admin.ScalarAsync<string>("SELECT ticker FROM marketdata.stocks WHERE id = @id", ("id", other.Value))).Should().Be("BBB", "the taken ticker was left alone");
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.ticker_history WHERE ticker = 'AAA'")).Should().Be(0, "a ticker in use is not a retired one");
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.ticker_history WHERE ticker = 'CCC'")).Should().Be(1);
    }

    [Fact]
    public async Task TheDaysTheProviderDidNotKnowTheSymbol_CountOncePerDay_KeepTheStatus_AndASuccessStartsThemAgain()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId id = await SeedStockAsync(db.Admin, "NOTF");
        await db.Admin.ExecuteAsync("INSERT INTO marketdata.ingestion_state (stock_id, status) VALUES (@id, 'ok')", ("id", id.Value));
        Task<int> Note(string day) => InSessionAsync(db.Worker, session => new ListingLifecycleStore(session).NoteSymbolNotFoundAsync(id, DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), _now, CancellationToken.None));

        int[] counts = [await Note("2026-10-15"), await Note("2026-10-15"), await Note("2026-10-16"), await Note("2026-10-19")];
        await InSessionAsync(db.Worker, async session =>
        {
            await new IngestionState(session).RecordSuccessAsync(id, _now, CancellationToken.None);
            return true;
        });
        int afterSuccess = await Note("2026-10-20");

        counts.Should().Equal(1, 1, 2, 3);
        afterSuccess.Should().Be(1, "a success in between started the count again");
        (await db.Admin.ScalarAsync<string>("SELECT status FROM marketdata.ingestion_state")).Should().Be("ok");
    }

    [Fact]
    public async Task TheDaysTheProviderDidNotKnowTheSymbol_OfAStockNeverTried_AreCountedFromAFreshPendingRow()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId id = await SeedStockAsync(db.Admin, "FRESH");

        int days = await InSessionAsync(db.Worker, session => new ListingLifecycleStore(session).NoteSymbolNotFoundAsync(id, new DateOnly(2026, 10, 19), _now, CancellationToken.None));

        days.Should().Be(1);
        (await db.Admin.ScalarAsync<string>("SELECT status FROM marketdata.ingestion_state")).Should().Be("pending");
    }

    [Fact]
    public async Task TheRunner_CommitsWhatItDid_AndAFailedRequestLeavesNothingBehind()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId id = await SeedStockAsync(db.Admin, "RUN");
        await SeedStockAsync(db.Admin, "TAKEN");

        async Task<bool> RunAsync(params string[] args) => await InSessionlessAsync(db.Worker, async session =>
        {
            var lifecycle = new ListingLifecycle(new StockCatalog(session), new ListingLifecycleStore(session), Options.Create(new IngestionOptions()), new FakeTimeProvider(_now), NullLogger<ListingLifecycle>.Instance);
            return (await new LifecycleCommandRunner(lifecycle, session).RunAsync(LifecycleCommand.Parse(args).Value, CancellationToken.None)).IsSuccess;
        });

        bool refused = await RunAsync("rename", "XNAS", "RUN", "TAKEN");
        bool renamed = await RunAsync("rename", "XNAS", "RUN", "RAN", "--on", "2026-10-01");
        bool delisted = await RunAsync("delist", "XNAS", "RAN");

        (refused, renamed, delisted).Should().Be((false, true, true));
        (await db.Admin.ScalarAsync<string>("SELECT ticker FROM marketdata.stocks WHERE id = @id", ("id", id.Value))).Should().Be("RAN");
        (await db.Admin.ScalarAsync<bool>("SELECT NOT is_active FROM marketdata.stocks WHERE id = @id", ("id", id.Value))).Should().BeTrue();
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.corporate_actions")).Should().Be(2, "one rename and one delisting; the refused request wrote none");
    }

    [Fact]
    public async Task ARenameIntoAProviderSymbolAnotherStockUses_IsRefused_NotAnException_AndLeavesNothingBehind()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId id = await SeedStockAsync(db.Admin, "AAA");
        StockId other = await SeedStockAsync(db.Admin, "BBB");
        await db.Admin.ExecuteAsync("UPDATE marketdata.symbol_aliases SET provider_symbol = 'CCC' WHERE stock_id = @id", ("id", other.Value));

        async Task<(bool Succeeded, string? Code)> RunAsync(params string[] args) => await InSessionlessAsync(db.Worker, async session =>
        {
            var lifecycle = new ListingLifecycle(new StockCatalog(session), new ListingLifecycleStore(session), Options.Create(new IngestionOptions()), new FakeTimeProvider(_now), NullLogger<ListingLifecycle>.Instance);
            Result<LifecycleChange> result = await new LifecycleCommandRunner(lifecycle, session).RunAsync(LifecycleCommand.Parse(args).Value, CancellationToken.None);
            return (result.IsSuccess, result.Error?.Code);
        });

        (bool succeeded, string? code) = await RunAsync("rename", "XNAS", "AAA", "NEWA", "--provider-symbol", "CCC");

        (succeeded, code).Should().Be((false, ListingLifecycle.SymbolTakenCode));
        (await db.Admin.ScalarAsync<string>("SELECT ticker FROM marketdata.stocks WHERE id = @id", ("id", id.Value))).Should().Be("AAA", "the whole rename was undone");
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.ticker_history")).Should().Be(0);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.corporate_actions")).Should().Be(0);
    }

    [Fact]
    public async Task TheApiRole_CanReadTheHistory_ButCannotWriteItNorChangeTheLifeOfAStock()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        StockId id = await SeedStockAsync(db.Admin, "SAFE");

        (await db.Api.ScalarAsync<long>("SELECT count(*) FROM marketdata.ticker_history")).Should().Be(0);
        Func<Task> insert = () => db.Api.ExecuteAsync("INSERT INTO marketdata.ticker_history (market_mic, ticker, stock_id, changed_on, created_at) VALUES ('XNAS', 'EVIL', @id, '2026-10-01', now())", ("id", id.Value));
        Func<Task> delist = () => db.Api.ExecuteAsync("UPDATE marketdata.stocks SET is_active = false, delisted_at = now()");
        Func<Task> count = () => db.Api.ExecuteAsync("UPDATE marketdata.ingestion_state SET not_found_days = 99");

        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await delist.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await count.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    // The runner owns the unit of work, so the session is handed over without having begun one.
    private static async Task<T> InSessionlessAsync<T>(string connection, Func<DbSession, Task<T>> action)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = connection }), new AnonymousCurrentUser());
        return await action(session);
    }
}
