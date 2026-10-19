using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Persistence;

namespace SakerWatch.Integration.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class StockRegistryTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>
{
    private static readonly DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private const string _call =
        "SELECT marketdata.register_stock(@id, @market, @ticker, @name, @currency, @tz, @type, @provider, @pmic, @psymbol, now())";

    private static (string Name, object? Value)[] Args(
        Guid? id = null, string market = "XNAS", string ticker = "AAPL", string name = "Apple Inc.", string currency = "USD", string type = "equity",
        string provider = "twelvedata", string pmic = "XNGS", string psymbol = "AAPL") =>
        [("id", id ?? Guid.CreateVersion7()), ("market", market), ("ticker", ticker), ("name", name), ("currency", currency), ("tz", "America/New_York"), ("type", type), ("provider", provider), ("pmic", pmic), ("psymbol", psymbol)];

    private static Stock NewStock(string ticker = "AAPL", string market = "XNAS") =>
        Stock.Create(StockId.Create(Guid.CreateVersion7()), new NewListing(market, ticker, "Apple Inc.", "USD", "America/New_York", AssetType.Equity), _now).Value;

    private static async Task ExpectAsync(Func<Task> action, string sqlState)
    {
        PostgresException error = (await action.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(sqlState);
    }

    private static async Task<T> InSessionAsync<T>(string connection, Func<DbSession, Task<T>> action)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = connection }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        T result = await action(session);
        await session.CommitAsync(CancellationToken.None);
        return result;
    }

    [Fact]
    public async Task TheApiRole_RegistersAStockWithItsAliasAndAPendingIngestionState()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = Guid.CreateVersion7();

        Guid? returned = await db.Api.AsUserScalarAsync<Guid>(Guid.CreateVersion7(), _call, Args(id));

        returned.Should().Be(id);
        (await db.Admin.ScalarAsync<string>("SELECT concat_ws('|', market_mic, ticker, name, currency, time_zone, asset_type, is_active) FROM marketdata.stocks"))
            .Should().Be("XNAS|AAPL|Apple Inc.|USD|America/New_York|equity|t");
        (await db.Admin.ScalarAsync<string>("SELECT concat_ws('|', provider, provider_mic, provider_symbol) FROM marketdata.symbol_aliases WHERE stock_id = @id", ("id", id)))
            .Should().Be("twelvedata|XNGS|AAPL");
        (await db.Admin.ScalarAsync<string>("SELECT status FROM marketdata.ingestion_state WHERE stock_id = @id", ("id", id))).Should().Be("pending");
    }

    [Fact]
    public async Task RegisteringTheSameStockAgain_ReturnsTheFirstId_AndChangesNothing()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var first = Guid.CreateVersion7();
        await db.Api.AsUserScalarAsync<Guid>(Guid.CreateVersion7(), _call, Args(first));

        Guid? again = await db.Api.AsUserScalarAsync<Guid>(Guid.CreateVersion7(), _call, Args(Guid.CreateVersion7(), name: "Renamed by an attacker", currency: "EUR"));

        again.Should().Be(first);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks")).Should().Be(1);
        (await db.Admin.ScalarAsync<string>("SELECT name || '|' || currency FROM marketdata.stocks")).Should().Be("Apple Inc.|USD", "an existing stock is never rewritten");
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.symbol_aliases")).Should().Be(1);
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.ingestion_state")).Should().Be(1);
    }

    [Fact]
    public async Task ASecondProvidersName_IsAddedAsAnotherAlias_AndAnIngestionStateAlreadyThereIsKept()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        var id = Guid.CreateVersion7();
        await db.Api.AsUserScalarAsync<Guid>(Guid.CreateVersion7(), _call, Args(id));
        await db.Admin.ExecuteAsync("UPDATE marketdata.ingestion_state SET status = 'ok', last_success_at = now()");

        await db.Api.AsUserScalarAsync<Guid>(Guid.CreateVersion7(), _call, Args(Guid.CreateVersion7(), provider: "iTick", pmic: "US", psymbol: "AAPL.US"));

        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.symbol_aliases WHERE stock_id = @id", ("id", id))).Should().Be(2);
        (await db.Admin.ScalarAsync<string>("SELECT status FROM marketdata.ingestion_state")).Should().Be("ok", "registering again never resets a listing's ingestion state");
    }

    [Fact]
    public async Task TwoRequestsRegisteringTheSameStockAtOnce_EndUpWithOneStock()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        Task<Guid>[] calls = [.. Enumerable.Range(0, 8).Select(async _ => await db.Api.AsUserScalarAsync<Guid>(Guid.CreateVersion7(), _call, Args(Guid.CreateVersion7())))];
        Guid[] ids = await Task.WhenAll(calls);

        ids.Distinct().Should().ContainSingle();
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks")).Should().Be(1);
    }

    [Theory]
    [InlineData("xnas", "AAPL", "USD", "equity")]
    [InlineData("XNAS", "a b", "USD", "equity")]
    [InlineData("XNAS", "AAPL", "usd", "equity")]
    [InlineData("XNAS", "AAPL", "USD", "warrant")]
    public async Task WhatTheFunctionInserts_StillPassesTheChecksOfTheTable(string market, string ticker, string currency, string type)
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        await ExpectAsync(() => db.Api.AsUserScalarAsync<Guid>(Guid.CreateVersion7(), _call, Args(market: market, ticker: ticker, currency: currency, type: type)), "23514");
        (await db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks")).Should().Be(0);
    }

    [Fact]
    public async Task OnlyTheApiRole_MayCallTheFunction()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        await ExpectAsync(() => db.Worker.AsUserScalarAsync<Guid>(null, _call, Args()), "42501");
        (await db.Admin.ScalarAsync<bool>("SELECT has_function_privilege('public', 'marketdata.register_stock(uuid, text, text, text, text, text, text, text, text, text, timestamptz)', 'EXECUTE')"))
            .Should().BeFalse();
        (await db.Admin.ScalarAsync<bool>("SELECT has_table_privilege('api_role', 'marketdata.stocks', 'INSERT')")).Should().BeFalse("the function is the only way in, not a table grant");
    }

    [Fact]
    public async Task TheRegistry_RegistersADomainStock_AndFindsItByItsKey()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        Stock stock = NewStock();

        StockId id = await InSessionAsync(db.Api, session => new StockRegistry(session).RegisterAsync(stock, "twelvedata", new ProviderListingKey("XNGS", "AAPL"), _now, CancellationToken.None));
        CatalogListing? found = await InSessionAsync(db.Api, session => new StockCatalog(session).FindByKeyAsync(Mic.Parse("XNAS"), Ticker.Parse("AAPL"), CancellationToken.None));

        id.Should().Be(stock.Id);
        found.Should().Be(new CatalogListing(stock.Id, Mic.Parse("XNAS"), Ticker.Parse("AAPL"), "Apple Inc.", CurrencyCode.Parse("USD"), AssetType.Equity, true));
        (await db.Admin.ScalarAsync<DateTime>("SELECT listed_at FROM marketdata.stocks")).Should().Be(_now.UtcDateTime);
    }

    [Fact]
    public async Task FindByKey_ForAStockThatIsNotThere_IsNull()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();

        CatalogListing? found = await InSessionAsync(db.Api, session => new StockCatalog(session).FindByKeyAsync(Mic.Parse("XNAS"), Ticker.Parse("NOPE"), CancellationToken.None));

        found.Should().BeNull();
    }

    [Fact]
    public async Task FindByKey_MapsTheStoredAssetTypesBack()
    {
        MigratedDatabase db = await postgres.CreateMigratedDatabaseAsync();
        await db.Api.AsUserScalarAsync<Guid>(Guid.CreateVersion7(), _call, Args(ticker: "SPY", psymbol: "SPY", type: "etf", pmic: "ARCX", market: "ARCX", name: "SPDR S&P 500"));
        await db.Api.AsUserScalarAsync<Guid>(Guid.CreateVersion7(), _call, Args(ticker: "ADR1", psymbol: "ADR1", type: "other"));

        CatalogListing? etf = await InSessionAsync(db.Api, session => new StockCatalog(session).FindByKeyAsync(Mic.Parse("ARCX"), Ticker.Parse("SPY"), CancellationToken.None));
        CatalogListing? other = await InSessionAsync(db.Api, session => new StockCatalog(session).FindByKeyAsync(Mic.Parse("XNAS"), Ticker.Parse("ADR1"), CancellationToken.None));

        (etf!.AssetType, other!.AssetType).Should().Be((AssetType.Etf, AssetType.Other));
    }
}
