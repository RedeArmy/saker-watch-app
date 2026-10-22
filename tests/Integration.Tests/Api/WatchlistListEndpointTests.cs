extern alias HostApi;

using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using SakerWatch.Integration.Tests.Identity;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.TestSupport;

namespace SakerWatch.Integration.Tests.Api;

/// <summary>
/// <c>GET /v1/watchlist</c> through the real API host over a real database: the price as traded, how current it is, the performance windows,
/// the sorts, the filters, the paging and the ETag. Prices are seeded relative to the real clock, because the staleness rule runs on it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WatchlistListEndpointTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>, IAsyncLifetime
{

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

    private async Task<WatchlistRig.Rig> StartRigAsync(bool budgetSpent = false)
    {
        WatchlistRig.Rig rig = await WatchlistRig.StartAsync(postgres, budgetSpent);
        _rigs.Add(rig);
        return rig;
    }

    private static readonly UsEquityCalendar _calendar = new(Mic.Parse("XNYS"));

    private sealed record Seeded(DateOnly LastSettled, DateOnly KoLast);

    // AAPL is current, KO is two sessions behind (stale), MSFT has no history yet (pending) and NVDA failed for good (unavailable).
    // Two years of weekdays are enough for every window but the five-year one. The raw close is twice the adjusted one, to show which is which.
    private async Task<(WatchlistRig.Rig Rig, string Token, Seeded Seeded)> StartWithFourStocksAsync()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-list" });
        foreach (string ticker in new[] { "AAPL", "KO", "MSFT", "NVDA" })
        {
            string market = ticker == "KO" ? "XNYS" : "XNAS";
            (await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/watchlist/items/{market}/{ticker}", token, new { notes = $"nota {ticker}" }))).StatusCode
                .Should().Be(HttpStatusCode.Created);
        }

        DateOnly settled = SessionSchedule.LatestSettled(_calendar, rig.Clock.GetUtcNow(), new IngestionOptions().SettleDelay);
        DateOnly koLast = _calendar.PreviousTradingDay(_calendar.PreviousTradingDay(settled));
        await SeedBarsAsync(rig, "AAPL", settled, step: 0.5m, start: 100m);
        await SeedBarsAsync(rig, "KO", koLast, step: 0.1m, start: 50m);
        await rig.Api.Db.Admin.ExecuteAsync(
            "UPDATE marketdata.ingestion_state SET status = 'ok', last_success_at = now() WHERE stock_id IN (SELECT id FROM marketdata.stocks WHERE ticker IN ('AAPL', 'KO'))");
        await rig.Api.Db.Admin.ExecuteAsync(
            "UPDATE marketdata.ingestion_state SET status = 'failed', failure_code = 'marketdata.provider.symbol_not_found' WHERE stock_id IN (SELECT id FROM marketdata.stocks WHERE ticker = 'NVDA')");
        return (rig, token, new Seeded(settled, koLast));
    }

    private static Task<int> SeedBarsAsync(WatchlistRig.Rig rig, string ticker, DateOnly last, decimal step, decimal start) =>
        rig.Api.Db.Admin.ExecuteAsync(
            """
            INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at)
            SELECT s.id, x.d::date, x.a * 2, x.a * 2 + 1, x.a * 2 - 1, x.a * 2, x.a, 1000, 'twelvedata', true, now()
            FROM marketdata.stocks s
            CROSS JOIN LATERAL (SELECT d, @start + @step * row_number() OVER (ORDER BY d) AS a
                                FROM generate_series(@last::date - interval '730 days', @last::date, interval '1 day') AS g(d)
                                WHERE extract(isodow FROM d) < 6) x
            WHERE s.ticker = @ticker
            """,
            ("ticker", ticker), ("last", last), ("start", start), ("step", step));

    private static async Task<JsonElement> ListAsync(WatchlistRig.Rig rig, string token, string query = "")
    {
        HttpResponseMessage response = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist" + query, token));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await WatchlistRig.BodyAsync(response);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string[] Tickers(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("stock").GetProperty("ticker").GetString()!)];

    [Fact]
    public async Task TheList_ShowsEachStocksPriceAsTraded_ItsStatus_AndTheEightWindows()
    {
        (WatchlistRig.Rig rig, string token, Seeded seeded) = await StartWithFourStocksAsync();

        JsonElement page = await ListAsync(rig, token);

        Tickers(page).Should().Equal("AAPL", "KO", "MSFT", "NVDA");
        (page.GetProperty("total").GetInt32(), page.GetProperty("limit").GetInt32(), page.GetProperty("offset").GetInt32()).Should().Be((4, 50, 0));
        JsonElement aapl = page.GetProperty("items")[0];
        aapl.GetProperty("notes").GetString().Should().Be("nota AAPL");
        aapl.GetProperty("priceStatus").GetString().Should().Be("current");
        aapl.GetProperty("staleSince").ValueKind.Should().Be(JsonValueKind.Null);
        JsonElement price = aapl.GetProperty("price");
        price.GetProperty("date").GetString().Should().Be(Iso(seeded.LastSettled));
        price.GetProperty("value").ValueKind.Should().Be(JsonValueKind.String, "decimals travel as strings");
        decimal lastAdjusted = await rig.Api.Db.Admin.ScalarAsync<decimal>("SELECT adjusted_close FROM marketdata.price_daily p JOIN marketdata.stocks s ON s.id = p.stock_id WHERE s.ticker = 'AAPL' ORDER BY trade_date DESC LIMIT 1");
        decimal.Parse(price.GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(lastAdjusted * 2, "the raw close, which here is twice the adjusted one");
        price.GetProperty("isFinal").GetBoolean().Should().BeTrue();

        JsonElement[] windows = [.. aapl.GetProperty("windows").EnumerateArray()];
        windows.Select(w => w.GetProperty("window").GetString()).Should().Equal("1D", "1W", "1M", "3M", "6M", "YTD", "1Y", "5Y");
        windows.Take(7).Should().OnlyContain(w => w.GetProperty("change").ValueKind == JsonValueKind.String && w.GetProperty("reason").ValueKind == JsonValueKind.Null);
        windows[0].GetProperty("baselineDate").GetString().Should().Be(Iso(_calendar.PreviousTradingDay(seeded.LastSettled)));
        windows[7].GetProperty("change").ValueKind.Should().Be(JsonValueKind.Null);
        windows[7].GetProperty("reason").GetString().Should().Be("insufficient_history", "only two years of history were seeded");
    }

    [Fact]
    public async Task AStockMissingASettledSession_IsStale_WithSinceWhen_AndItsLastKnownPrice()
    {
        (WatchlistRig.Rig rig, string token, Seeded seeded) = await StartWithFourStocksAsync();

        JsonElement ko = (await ListAsync(rig, token, "?market=XNYS")).GetProperty("items")[0];

        ko.GetProperty("priceStatus").GetString().Should().Be("stale");
        ko.GetProperty("price").GetProperty("date").GetString().Should().Be(Iso(seeded.KoLast));
        DateTimeOffset since = ko.GetProperty("staleSince").GetDateTimeOffset();
        since.Should().BeAfter(_calendar.CloseOf(seeded.KoLast)!.Value);
        since.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task AStockWithoutHistory_IsPending_AndOneThatFailedForGoodIsUnavailable_BothWithNoPriceAndNoDataInEveryWindow()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartWithFourStocksAsync();

        JsonElement page = await ListAsync(rig, token);

        JsonElement msft = page.GetProperty("items")[2];
        JsonElement nvda = page.GetProperty("items")[3];
        msft.GetProperty("priceStatus").GetString().Should().Be("pending");
        nvda.GetProperty("priceStatus").GetString().Should().Be("unavailable");
        foreach (JsonElement item in new[] { msft, nvda })
        {
            item.GetProperty("price").ValueKind.Should().Be(JsonValueKind.Null);
            item.GetProperty("windows").EnumerateArray().Should().HaveCount(8).And.OnlyContain(w => w.GetProperty("reason").GetString() == "no_data");
        }
    }

    [Theory]
    [InlineData("?sort=ticker&order=desc", new[] { "NVDA", "MSFT", "KO", "AAPL" })]
    [InlineData("?sort=name", new[] { "AAPL", "KO", "MSFT", "NVDA" })]
    [InlineData("?sort=market&order=desc", new[] { "KO", "AAPL", "MSFT", "NVDA" })]
    [InlineData("?sort=price&order=desc", new[] { "AAPL", "KO", "MSFT", "NVDA" })]
    [InlineData("?sort=price&order=asc", new[] { "KO", "AAPL", "MSFT", "NVDA" })]
    [InlineData("?sort=1d&order=desc", new[] { "AAPL", "KO", "MSFT", "NVDA" })]
    [InlineData("?sort=1d&order=asc", new[] { "KO", "AAPL", "MSFT", "NVDA" })]
    [InlineData("?sort=5y", new[] { "AAPL", "KO", "MSFT", "NVDA" })]
    [InlineData("?sort=createdAt&order=asc", new[] { "AAPL", "KO", "MSFT", "NVDA" })]
    [InlineData("?sort=createdAt&order=desc", new[] { "NVDA", "MSFT", "KO", "AAPL" })]
    public async Task TheSorts_FollowTheirKey_AndAStockWithoutAValueIsAlwaysLast(string query, string[] expected)
    {
        (WatchlistRig.Rig rig, string token, _) = await StartWithFourStocksAsync();

        Tickers(await ListAsync(rig, token, query)).Should().Equal(expected);
    }

    [Theory]
    [InlineData("?market=XNYS", new[] { "KO" })]
    [InlineData("?market=xnas", new[] { "AAPL", "MSFT", "NVDA" })]
    [InlineData("?q=micro", new[] { "MSFT" })]
    [InlineData("?q=a", new[] { "AAPL", "KO", "NVDA" })]
    [InlineData("?assetType=etf", new string[0])]
    [InlineData("?assetType=equity&priceStatus=pending", new[] { "MSFT" })]
    [InlineData("?priceStatus=stale", new[] { "KO" })]
    [InlineData("?priceStatus=current", new[] { "AAPL" })]
    [InlineData("?priceStatus=unavailable", new[] { "NVDA" })]
    public async Task TheFilters_NarrowTheList_AndTheTotal(string query, string[] expected)
    {
        (WatchlistRig.Rig rig, string token, _) = await StartWithFourStocksAsync();

        JsonElement page = await ListAsync(rig, token, query);

        Tickers(page).Should().Equal(expected);
        page.GetProperty("total").GetInt32().Should().Be(expected.Length);
    }

    [Fact]
    public async Task Paging_CutsTheListAndKeepsTheTotal()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartWithFourStocksAsync();

        JsonElement second = await ListAsync(rig, token, "?limit=2&offset=2");
        JsonElement beyond = await ListAsync(rig, token, "?limit=2&offset=9");

        Tickers(second).Should().Equal("MSFT", "NVDA");
        (second.GetProperty("total").GetInt32(), second.GetProperty("limit").GetInt32(), second.GetProperty("offset").GetInt32()).Should().Be((4, 2, 2));
        Tickers(beyond).Should().BeEmpty();
        beyond.GetProperty("total").GetInt32().Should().Be(4);
    }

    [Theory]
    [InlineData("?sort=signal")]
    [InlineData("?sort=TICKER")]
    [InlineData("?order=sideways")]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    [InlineData("?offset=-1")]
    [InlineData("?limit=abc")]
    [InlineData("?assetType=stock")]
    [InlineData("?priceStatus=fresh")]
    public async Task ABadSortFilterOrPage_IsA400_NeverA500(string query)
    {
        (WatchlistRig.Rig rig, string token, _) = await StartWithFourStocksAsync();

        HttpResponseMessage response = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist" + query, token));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Exception").And.NotContain("   at ");
    }

    [Fact]
    public async Task TheAnswer_HasAnETag_AnUnchangedListGets304_AndAChangeGetsANewBody()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartWithFourStocksAsync();
        HttpResponseMessage first = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist", token));
        string tag = first.Headers.ETag!.Tag;

        HttpRequestMessage conditional = WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist", token);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", tag);
        HttpResponseMessage notModified = await rig.Client.SendAsync(conditional);
        await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", token, new { notes = "otra nota" }));
        HttpRequestMessage afterChange = WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist", token);
        afterChange.Headers.TryAddWithoutValidation("If-None-Match", tag);
        HttpResponseMessage changed = await rig.Client.SendAsync(afterChange);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.CacheControl!.ToString().Should().Be("no-cache, private");
        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await notModified.Content.ReadAsStringAsync()).Should().BeEmpty();
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        changed.Headers.ETag!.Tag.Should().NotBe(tag);
    }

    [Fact]
    public async Task TheList_IsPerUser_AnotherCallerSeesOnlyTheirOwnStocks()
    {
        (WatchlistRig.Rig rig, _, _) = await StartWithFourStocksAsync();
        string other = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-other" });
        await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", other, new { notes = "la mía" }));

        JsonElement page = await ListAsync(rig, other);
        JsonElement empty = await ListAsync(rig, rig.Api.Idp.Mint(new TokenSpec { Subject = "account-nobody" }));

        Tickers(page).Should().Equal("AAPL");
        page.GetProperty("items")[0].GetProperty("notes").GetString().Should().Be("la mía");
        page.GetProperty("items")[0].GetProperty("priceStatus").GetString().Should().Be("current", "the market data is shared, the list is not");
        (Tickers(empty), empty.GetProperty("total").GetInt32()).Should().Be((Array.Empty<string>(), 0));
    }

    [Fact]
    public async Task WithoutAToken_TheListIs401()
    {
        WatchlistRig.Rig rig = await StartRigAsync();

        HttpResponseMessage response = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist", null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheList_NeverCallsTheProvider_AndAStockRemovedFromTheWatchlistLeavesIt()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartWithFourStocksAsync();
        int calls = rig.Provider.Calls;

        await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, "/v1/watchlist/items/XNYS/KO", token));
        JsonElement page = await ListAsync(rig, token);

        Tickers(page).Should().Equal("AAPL", "MSFT", "NVDA");
        rig.Provider.Calls.Should().Be(calls, "reading the list is reading our own data");
    }
}
