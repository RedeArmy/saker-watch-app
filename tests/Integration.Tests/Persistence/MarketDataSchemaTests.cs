using AwesomeAssertions;
using Npgsql;
using SakerWatch.Hosts.Migrator;
using SakerWatch.Integration.Tests.Infrastructure;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class MarketDataSchemaTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private const string _insertStock =
        "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (@id, @mic, @ticker, @name, @currency, 'America/New_York', @type, true, now())";

    private static (string Name, object? Value)[] Stock(Guid id, string mic = "XNAS", string ticker = "AAPL", string name = "Apple Inc.", string currency = "USD", string type = "equity") =>
        [("id", id), ("mic", mic), ("ticker", ticker), ("name", name), ("currency", currency), ("type", type)];

    private static async Task ExpectAsync(Func<Task> action, string sqlState)
    {
        PostgresException error = (await action.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(sqlState);
    }

    [Fact]
    public async Task Migrate_OnAnEmptyDatabase_CreatesTheCatalogTables()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        (await db.Admin.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'marketdata' AND table_name IN ('stocks', 'symbol_aliases', 'corporate_actions')"))
            .Should().Be(3);
    }

    [Fact]
    public async Task Migrate_RunTwice_SecondRunAppliesNothing()
    {
        string admin = await postgres.CreateDatabaseAsync();
        await MigrationSteps.PlatformStep(admin).RunAsync(CancellationToken.None);
        var step = MigrationSteps.MarketDataStep(admin);

        int first = await step.RunAsync(CancellationToken.None);
        int second = await step.RunAsync(CancellationToken.None);

        first.Should().Be(1);
        second.Should().Be(0);
    }

    [Fact]
    public async Task Stocks_AreUniquePerMarketAndTicker_ButTheSameTickerMayLiveOnAnotherMarket()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await db.Worker.ExecuteAsync(_insertStock, Stock(Guid.CreateVersion7()));

        await db.Worker.ExecuteAsync(_insertStock, Stock(Guid.CreateVersion7(), mic: "XTSE", currency: "CAD"));
        await ExpectAsync(() => db.Worker.ExecuteAsync(_insertStock, Stock(Guid.CreateVersion7())), "23505");

        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks")).Should().Be(2);
    }

    [Theory]
    [InlineData("xnas", "AAPL", "Apple", "USD", "equity")]
    [InlineData("XNAS", "aapl", "Apple", "USD", "equity")]
    [InlineData("XNAS", "A PL", "Apple", "USD", "equity")]
    [InlineData("XNAS", "AAPL", "", "USD", "equity")]
    [InlineData("XNAS", "AAPL", "Apple", "usd", "equity")]
    [InlineData("XNAS", "AAPL", "Apple", "USDX", "equity")]
    [InlineData("XNAS", "AAPL", "Apple", "USD", "bond")]
    public async Task Stocks_RefuseValuesThatTheDomainWouldNeverProduce(string mic, string ticker, string name, string currency, string type)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        // 23514 (check violation), or 22001 when the value does not even fit the column.
        PostgresException error = (await ((Func<Task>)(() => db.Worker.ExecuteAsync(_insertStock, Stock(Guid.CreateVersion7(), mic, ticker, name, currency, type))))
            .Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().BeOneOf("23514", "22001");
    }

    [Fact]
    public async Task Api_CanReadTheCatalog_ButNotChangeIt()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = Guid.CreateVersion7();
        await db.Worker.ExecuteAsync(_insertStock, Stock(id));

        (await db.Api.AsUserScalarAsync<long>(null, "SELECT count(*) FROM marketdata.stocks")).Should().Be(1);
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(null, _insertStock, Stock(Guid.CreateVersion7(), ticker: "MSFT")), "42501");
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(null, "UPDATE marketdata.stocks SET is_active = false WHERE id = @id", ("id", id)), "42501");
        await ExpectAsync(() => db.Api.AsUserExecuteAsync(null, "DELETE FROM marketdata.stocks WHERE id = @id", ("id", id)), "42501");
    }

    [Fact]
    public async Task Worker_CanMaintainTheCatalog_ButNeverDeleteFromIt()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = Guid.CreateVersion7();
        await db.Worker.ExecuteAsync(_insertStock, Stock(id));

        (await db.Worker.ExecuteAsync("UPDATE marketdata.stocks SET is_active = false WHERE id = @id", ("id", id))).Should().Be(1);
        await ExpectAsync(() => db.Worker.ExecuteAsync("DELETE FROM marketdata.stocks WHERE id = @id", ("id", id)), "42501");
    }

    [Theory]
    [InlineData("stocks")]
    [InlineData("symbol_aliases")]
    [InlineData("corporate_actions")]
    public async Task Public_HasNoPrivilegeOnTheTables(string table)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        (await db.Admin.ScalarAsync<bool>(
            $"SELECT has_table_privilege('public', 'marketdata.{table}', 'SELECT, INSERT, UPDATE, DELETE')"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task SymbolAliases_MapAProviderListingToOneStock_AndAreUniquePerProviderMarketAndSymbol()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = Guid.CreateVersion7();
        await db.Worker.ExecuteAsync(_insertStock, Stock(id));
        const string insert = "INSERT INTO marketdata.symbol_aliases (provider, provider_mic, provider_symbol, stock_id, created_at) VALUES ('twelvedata', 'XNGS', 'AAPL', @id, now())";

        await db.Worker.ExecuteAsync(insert, ("id", id));

        await ExpectAsync(() => db.Worker.ExecuteAsync(insert, ("id", id)), "23505");
        await ExpectAsync(() => db.Worker.ExecuteAsync(insert.Replace("'AAPL'", "'MSFT'", StringComparison.Ordinal), ("id", Guid.CreateVersion7())), "23503");
    }

    [Fact]
    public async Task CorporateActions_NeedTheirFields_AndAreUniquePerKindAndDate()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = Guid.CreateVersion7();
        await db.Worker.ExecuteAsync(_insertStock, Stock(id));
        const string split = "INSERT INTO marketdata.corporate_actions (id, stock_id, kind, effective_date, split_ratio, source, ingested_at) VALUES (@aid, @id, 'split', '2024-06-10', @ratio, 'twelvedata', now())";
        const string dividend = "INSERT INTO marketdata.corporate_actions (id, stock_id, kind, effective_date, cash_amount, currency, source, ingested_at) VALUES (@aid, @id, 'dividend', '2024-06-11', @amount, @currency, 'twelvedata', now())";

        await db.Worker.ExecuteAsync(split, ("aid", Guid.CreateVersion7()), ("id", id), ("ratio", 10m));
        await db.Worker.ExecuteAsync(dividend, ("aid", Guid.CreateVersion7()), ("id", id), ("amount", 0.01m), ("currency", "USD"));

        await ExpectAsync(() => db.Worker.ExecuteAsync(split, ("aid", Guid.CreateVersion7()), ("id", id), ("ratio", 10m)), "23505");
        await ExpectAsync(() => db.Worker.ExecuteAsync(split.Replace("'2024-06-10'", "'2024-07-01'", StringComparison.Ordinal), ("aid", Guid.CreateVersion7()), ("id", id), ("ratio", 0m)), "23514");
        await ExpectAsync(() => db.Worker.ExecuteAsync(dividend.Replace("'2024-06-11'", "'2024-07-02'", StringComparison.Ordinal), ("aid", Guid.CreateVersion7()), ("id", id), ("amount", -1m), ("currency", "USD")), "23514");
        await ExpectAsync(() => db.Worker.ExecuteAsync(dividend.Replace("'2024-06-11'", "'2024-07-03'", StringComparison.Ordinal), ("aid", Guid.CreateVersion7()), ("id", id), ("amount", 1m), ("currency", "us")), "23514");
    }

    [Fact]
    public async Task ACatalogRowThatIsReferenced_CannotBeDeleted()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = Guid.CreateVersion7();
        await db.Worker.ExecuteAsync(_insertStock, Stock(id));
        await db.Worker.ExecuteAsync(
            "INSERT INTO marketdata.symbol_aliases (provider, provider_mic, provider_symbol, stock_id, created_at) VALUES ('twelvedata', 'XNGS', 'AAPL', @id, now())", ("id", id));

        await ExpectAsync(() => db.Admin.ExecuteAsync("DELETE FROM marketdata.stocks WHERE id = @id", ("id", id)), "23503");
    }
}
