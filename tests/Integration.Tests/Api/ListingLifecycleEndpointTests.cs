extern alias HostApi;

using System.Globalization;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Security;
using SakerWatch.BuildingBlocks.Persistence;
using SakerWatch.Integration.Tests.Identity;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lifecycle;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Persistence;
using SakerWatch.TestSupport;

namespace SakerWatch.Integration.Tests.Api;

/// <summary>
/// What a user sees when a stock stops trading or changes its ticker (ADR-0039), through the real API host over a real database: the watchlist,
/// the price series, the portfolio metrics, the sparklines and the calculator, and the old ticker that keeps finding the stock.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ListingLifecycleEndpointTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>, IAsyncLifetime
{
    private static readonly UsEquityCalendar _calendar = new(Mic.Parse("XNYS"));
    private readonly List<WatchlistRig.Rig> _rigs = [];

    public Task InitializeAsync() => Task.CompletedTask;

    // Every host is stopped when its test ends: a host left running keeps file watchers open, and the machine allows a small number of them.
    public async Task DisposeAsync()
    {
        foreach (WatchlistRig.Rig rig in _rigs)
        {
            await rig.DisposeAsync();
        }
    }

    // AAPL is on the watchlist with two months of prices and in a portfolio; the bank has USD to GTQ.
    private async Task<(WatchlistRig.Rig Rig, string Token, Guid Portfolio)> StartAsync()
    {
        WatchlistRig.Rig rig = await WatchlistRig.StartAsync(postgres);
        _rigs.Add(rig);
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-lifecycle" });
        (await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", token))).StatusCode.Should().Be(HttpStatusCode.Created);

        DateOnly settled = SessionSchedule.LatestSettled(_calendar, DateTimeOffset.UtcNow, new IngestionOptions().SettleDelay);
        await rig.Api.Db.Admin.ExecuteAsync(
            """
            INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at)
            SELECT s.id, x.d::date, x.a * 2, x.a * 2 + 1, x.a * 2 - 1, x.a * 2, x.a, 1000, 'twelvedata', true, now()
            FROM marketdata.stocks s
            CROSS JOIN LATERAL (SELECT d, 100 + 0.5 * row_number() OVER (ORDER BY d) AS a
                                FROM generate_series(@last::date - interval '60 days', @last::date, interval '1 day') AS g(d)
                                WHERE extract(isodow FROM d) < 6) x
            WHERE s.ticker = 'AAPL'
            """,
            ("last", settled));
        await rig.Api.Db.Admin.ExecuteAsync("UPDATE marketdata.ingestion_state SET status = 'ok', last_success_at = now()");
        await rig.Api.Db.Admin.ExecuteAsync("INSERT INTO marketdata.fx_rates (base, quote, as_of_date, source, rate, ingested_at) VALUES ('USD', 'GTQ', current_date - 1, 'banguat', 7.75, now())");

        HttpRequestMessage create = WatchlistRig.Send(HttpMethod.Post, "/v1/portfolios", token, new { name = "Tecnología" });
        create.Headers.Add("Idempotency-Key", "lifecycle-portfolio-0000001");
        Guid portfolio = (await WatchlistRig.BodyAsync(await rig.Client.SendAsync(create))).GetProperty("id").GetGuid();
        await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{portfolio}/items/XNAS/AAPL", token));
        return (rig, token, portfolio);
    }

    private static async Task<JsonElement> OkAsync(WatchlistRig.Rig rig, string token, string path)
    {
        HttpResponseMessage response = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, path, token));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await WatchlistRig.BodyAsync(response);
    }

    private static async Task<T> AsWorkerAsync<T>(WatchlistRig.Rig rig, Func<ListingLifecycleStore, Task<T>> action)
    {
        await using var session = new DbSession(Options.Create(new PostgresOptions { Postgres = rig.Api.Db.Worker }), new AnonymousCurrentUser());
        await session.BeginAsync(CancellationToken.None);
        T result = await action(new ListingLifecycleStore(session));
        await session.CommitAsync(CancellationToken.None);
        return result;
    }

    private static Task<Guid> AppleAsync(WatchlistRig.Rig rig) => rig.Api.Db.Admin.ScalarAsync<Guid>("SELECT id FROM marketdata.stocks WHERE ticker = 'AAPL'");

    [Fact]
    public async Task ADelistedStock_IsDelisted_InTheWatchlist_WithItsLastPrice_NeverStale_AndTheFilterFindsIt()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartAsync();
        JsonElement before = (await OkAsync(rig, token, "/v1/watchlist")).GetProperty("items")[0];
        before.GetProperty("priceStatus").GetString().Should().Be("current");
        var delistedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await AsWorkerAsync(rig, async store => await store.DelistAsync(StockId.Create(await AppleAsync(rig)), delistedAt, new DateOnly(2026, 10, 1), "operator", CancellationToken.None));

        JsonElement page = await OkAsync(rig, token, "/v1/watchlist");
        JsonElement filtered = await OkAsync(rig, token, "/v1/watchlist?priceStatus=delisted");
        JsonElement none = await OkAsync(rig, token, "/v1/watchlist?priceStatus=current");

        JsonElement item = page.GetProperty("items")[0];
        (item.GetProperty("priceStatus").GetString(), item.GetProperty("staleSince").ValueKind, item.GetProperty("price").ValueKind).Should().Be(("delisted", JsonValueKind.Null, JsonValueKind.Object));
        item.GetProperty("delistedAt").GetDateTimeOffset().Should().Be(delistedAt);
        filtered.GetProperty("total").GetInt32().Should().Be(1);
        none.GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ADelistedStock_KeepsItsChart_ItsSparkline_AndShowsUpInThePortfolioMetricsApart()
    {
        (WatchlistRig.Rig rig, string token, Guid portfolio) = await StartAsync();
        await AsWorkerAsync(rig, async store => await store.DelistAsync(StockId.Create(await AppleAsync(rig)), DateTimeOffset.UtcNow, new DateOnly(2026, 10, 1), "operator", CancellationToken.None));

        JsonElement series = await OkAsync(rig, token, "/v1/stocks/XNAS/AAPL/prices?range=1M");
        JsonElement lines = await OkAsync(rig, token, "/v1/watchlist/sparklines?days=10");
        JsonElement metrics = await OkAsync(rig, token, $"/v1/portfolios/{portfolio}/metrics");

        (series.GetProperty("priceStatus").GetString(), series.GetProperty("count").GetInt32() > 0, series.GetProperty("delistedAt").ValueKind).Should().Be(("delisted", true, JsonValueKind.String));
        lines.GetProperty("items")[0].GetProperty("points").GetArrayLength().Should().Be(10);
        JsonElement counts = metrics.GetProperty("priceStatuses");
        (counts.GetProperty("delisted").GetInt32(), counts.GetProperty("current").GetInt32(), counts.GetProperty("stale").GetInt32()).Should().Be((1, 0, 0));
        metrics.GetProperty("staleSince").ValueKind.Should().Be(JsonValueKind.Null);
        metrics.GetProperty("windows")[0].GetProperty("count").GetInt32().Should().Be(1, "a delisted stock keeps the numbers it had");
    }

    [Fact]
    public async Task ARelistedStock_IsCurrentAgain()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartAsync();
        var apple = StockId.Create(await AppleAsync(rig));
        await AsWorkerAsync(rig, store => store.DelistAsync(apple, DateTimeOffset.UtcNow, new DateOnly(2026, 10, 1), "operator", CancellationToken.None));
        await AsWorkerAsync(rig, store => store.RelistAsync(apple, DateTimeOffset.UtcNow, CancellationToken.None));

        JsonElement item = (await OkAsync(rig, token, "/v1/watchlist")).GetProperty("items")[0];

        (item.GetProperty("priceStatus").GetString(), item.GetProperty("delistedAt").ValueKind).Should().Be(("current", JsonValueKind.Null));
    }

    [Fact]
    public async Task AfterATickerChange_TheWatchlistShowsTheNewTicker_AndTheOldOneKeepsFindingTheStock_EverywhereWithoutRegisteringADuplicate()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartAsync();
        Guid apple = await AppleAsync(rig);
        int providerCalls = rig.Provider.Calls;
        (await AsWorkerAsync(rig, store => store.RenameAsync(new TickerChange(StockId.Create(apple), Mic.Parse("XNAS"), Ticker.Parse("AAPL"), Ticker.Parse("APLE")), new DateOnly(2026, 10, 1), "APLE", DateTimeOffset.UtcNow, CancellationToken.None)))
            .Should().Be(SakerWatch.MarketData.Application.Lifecycle.RenameOutcome.Done);

        JsonElement list = await OkAsync(rig, token, "/v1/watchlist");
        JsonElement byOld = await OkAsync(rig, token, "/v1/stocks/XNAS/AAPL/prices?range=1M");
        JsonElement byNew = await OkAsync(rig, token, "/v1/stocks/XNAS/APLE/prices?range=1M");
        HttpResponseMessage put = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", token, new { notes = "con el ticker viejo" }));
        HttpRequestMessage calc = WatchlistRig.Send(HttpMethod.Post, "/v1/calculations/partial-purchase", token);
        calc.Content = System.Net.Http.Json.JsonContent.Create(new { market = "XNAS", ticker = "AAPL", amount = "100" });
        JsonElement calculation = await WatchlistRig.BodyAsync(await rig.Client.SendAsync(calc));

        list.GetProperty("items")[0].GetProperty("stock").GetProperty("ticker").GetString().Should().Be("APLE");
        byOld.GetProperty("stock").GetProperty("id").GetGuid().Should().Be(apple);
        byNew.GetProperty("count").GetInt32().Should().Be(byOld.GetProperty("count").GetInt32());
        put.StatusCode.Should().Be(HttpStatusCode.OK, "the stock is already on the watchlist: it is updated, not added again");
        (await WatchlistRig.BodyAsync(put)).GetProperty("stock").GetProperty("ticker").GetString().Should().Be("APLE");
        calculation.GetProperty("stock").GetProperty("ticker").GetString().Should().Be("APLE");
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks WHERE ticker IN ('AAPL', 'APLE')")).Should().Be(1);
        rig.Provider.Calls.Should().Be(providerCalls, "the old ticker is resolved from our own history, never from the provider");
    }
}
