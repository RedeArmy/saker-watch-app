extern alias HostApi;

using System.Globalization;
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
/// <c>GET /v1/portfolios/{id}/metrics</c> and <c>GET /v1/watchlist?portfolio=</c> through the real API host over a real database. The expected
/// figures are derived from the watchlist list of the same stocks, so the two views must agree.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PortfolioMetricsEndpointTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>, IAsyncLifetime
{
    private static readonly UsEquityCalendar _calendar = new(Mic.Parse("XNYS"));
    private static int _keys;

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

    private async Task<WatchlistRig.Rig> StartRigAsync()
    {
        WatchlistRig.Rig rig = await WatchlistRig.StartAsync(postgres);
        _rigs.Add(rig);
        return rig;
    }

    // AAPL is current, KO is two sessions behind (stale), MSFT has no history yet (pending) and NVDA failed for good (unavailable).
    private async Task<(WatchlistRig.Rig Rig, string Token)> StartWithFourStocksAsync()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-metrics" });
        foreach (string ticker in new[] { "AAPL", "KO", "MSFT", "NVDA" })
        {
            string market = ticker == "KO" ? "XNYS" : "XNAS";
            (await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/watchlist/items/{market}/{ticker}", token))).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        DateOnly settled = SessionSchedule.LatestSettled(_calendar, rig.Clock.GetUtcNow(), new IngestionOptions().SettleDelay);
        DateOnly koLast = _calendar.PreviousTradingDay(_calendar.PreviousTradingDay(settled));
        await SeedBarsAsync(rig, "AAPL", settled, step: 0.5m, start: 100m);
        await SeedBarsAsync(rig, "KO", koLast, step: 0.1m, start: 50m);
        await rig.Api.Db.Admin.ExecuteAsync(
            "UPDATE marketdata.ingestion_state SET status = 'ok', last_success_at = now() WHERE stock_id IN (SELECT id FROM marketdata.stocks WHERE ticker IN ('AAPL', 'KO'))");
        await rig.Api.Db.Admin.ExecuteAsync(
            "UPDATE marketdata.ingestion_state SET status = 'failed', failure_code = 'marketdata.provider.symbol_not_found' WHERE stock_id IN (SELECT id FROM marketdata.stocks WHERE ticker = 'NVDA')");
        return (rig, token);
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

    private static HttpRequestMessage Post(string path, string token, object body)
    {
        HttpRequestMessage request = WatchlistRig.Send(HttpMethod.Post, path, token, body);
        request.Headers.Add("Idempotency-Key", $"metrics-key-{Interlocked.Increment(ref _keys):D10}");
        return request;
    }

    private static async Task<Guid> CreatePortfolioAsync(WatchlistRig.Rig rig, string token, string name, params (string Market, string Ticker)[] stocks)
    {
        HttpResponseMessage created = await rig.Client.SendAsync(Post("/v1/portfolios", token, new { name }));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        Guid id = (await WatchlistRig.BodyAsync(created)).GetProperty("id").GetGuid();
        foreach ((string market, string ticker) in stocks)
        {
            HttpResponseMessage put = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{id}/items/{market}/{ticker}", token));
            put.IsSuccessStatusCode.Should().BeTrue(await put.Content.ReadAsStringAsync());
        }

        return id;
    }

    private static async Task<JsonElement> MetricsAsync(WatchlistRig.Rig rig, string token, Guid id)
    {
        HttpResponseMessage response = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/portfolios/{id}/metrics", token));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await WatchlistRig.BodyAsync(response);
    }

    private static decimal Parse(JsonElement value) => decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture);

    private static JsonElement Window(JsonElement metrics, string code) =>
        metrics.GetProperty("windows").EnumerateArray().Single(w => w.GetProperty("window").GetString() == code);

    [Fact]
    public async Task TheMetrics_CountEachPriceState_AndAverageOnlyTheStocksThatHaveANumber()
    {
        (WatchlistRig.Rig rig, string token) = await StartWithFourStocksAsync();
        Guid id = await CreatePortfolioAsync(rig, token, "Mezcla", ("XNAS", "AAPL"), ("XNYS", "KO"), ("XNAS", "MSFT"), ("XNAS", "NVDA"));
        HttpResponseMessage listResponse = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist", token));
        JsonElement items = (await WatchlistRig.BodyAsync(listResponse)).GetProperty("items");
        decimal[] oneDay = [.. items.EnumerateArray()
            .Select(i => i.GetProperty("windows")[0].GetProperty("change"))
            .Where(c => c.ValueKind == JsonValueKind.String)
            .Select(Parse)];

        JsonElement metrics = await MetricsAsync(rig, token, id);

        metrics.GetProperty("portfolio").GetProperty("name").GetString().Should().Be("Mezcla");
        metrics.GetProperty("stockCount").GetInt32().Should().Be(4);
        JsonElement statuses = metrics.GetProperty("priceStatuses");
        (statuses.GetProperty("current").GetInt32(), statuses.GetProperty("stale").GetInt32(), statuses.GetProperty("pending").GetInt32(), statuses.GetProperty("unavailable").GetInt32())
            .Should().Be((1, 1, 1, 1));
        metrics.GetProperty("staleSince").ValueKind.Should().Be(JsonValueKind.String);
        metrics.GetProperty("windows").EnumerateArray().Select(w => w.GetProperty("window").GetString())
            .Should().Equal("1D", "1W", "1M", "3M", "6M", "YTD", "1Y", "5Y");

        oneDay.Should().HaveCount(2, "AAPL and the stale KO have a number; the pending and the unavailable stock have none");
        JsonElement day = Window(metrics, "1D");
        day.GetProperty("count").GetInt32().Should().Be(2);
        day.GetProperty("average").ValueKind.Should().Be(JsonValueKind.String, "decimals travel as strings");
        Parse(day.GetProperty("average")).Should().Be(oneDay.Average());
        Parse(day.GetProperty("median")).Should().Be(oneDay.Average(), "the median of two is their mean");
        Parse(day.GetProperty("best").GetProperty("change")).Should().Be(oneDay.Max());
        Parse(day.GetProperty("worst").GetProperty("change")).Should().Be(oneDay.Min());
        day.GetProperty("best").GetProperty("stock").GetProperty("ticker").GetString().Should().Be("AAPL");
        day.GetProperty("worst").GetProperty("stock").GetProperty("ticker").GetString().Should().Be("KO");

        JsonElement fiveYears = Window(metrics, "5Y");
        fiveYears.GetProperty("count").GetInt32().Should().Be(0, "only two years of history were seeded");
        fiveYears.GetProperty("average").ValueKind.Should().Be(JsonValueKind.Null, "no number is not zero");
        fiveYears.GetProperty("best").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheMetrics_OfAnEmptyPortfolio_AreZeroCountsAndNoNumbers()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-metrics-empty" });
        Guid id = await CreatePortfolioAsync(rig, token, "Vacía");

        JsonElement metrics = await MetricsAsync(rig, token, id);

        metrics.GetProperty("stockCount").GetInt32().Should().Be(0);
        metrics.GetProperty("staleSince").ValueKind.Should().Be(JsonValueKind.Null);
        metrics.GetProperty("windows").EnumerateArray().Should().HaveCount(8)
            .And.OnlyContain(w => w.GetProperty("count").GetInt32() == 0 && w.GetProperty("average").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task TheMetrics_OfAnotherUsersOrAnUnknownPortfolio_Are404_AndWithoutAToken401()
    {
        (WatchlistRig.Rig rig, string token) = await StartWithFourStocksAsync();
        Guid id = await CreatePortfolioAsync(rig, token, "Mía", ("XNAS", "AAPL"));
        string other = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-metrics-other" });

        HttpResponseMessage foreign = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/portfolios/{id}/metrics", other));
        HttpResponseMessage unknown = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/portfolios/{Guid.NewGuid()}/metrics", token));
        HttpResponseMessage anonymous = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/portfolios/{id}/metrics", null));

        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreign.Content.ReadAsStringAsync()).Should().NotContain("Mía", "nothing about a portfolio of someone else leaks");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheMetrics_HaveAnETag_AnUnchangedAnswerGets304_AndAddingAStockChangesIt()
    {
        (WatchlistRig.Rig rig, string token) = await StartWithFourStocksAsync();
        Guid id = await CreatePortfolioAsync(rig, token, "Etiqueta", ("XNAS", "AAPL"));
        string path = $"/v1/portfolios/{id}/metrics";
        HttpResponseMessage first = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, path, token));
        string tag = first.Headers.ETag!.Tag;

        HttpRequestMessage conditional = WatchlistRig.Send(HttpMethod.Get, path, token);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", tag);
        HttpResponseMessage notModified = await rig.Client.SendAsync(conditional);
        await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{id}/items/XNYS/KO", token));
        HttpRequestMessage afterChange = WatchlistRig.Send(HttpMethod.Get, path, token);
        afterChange.Headers.TryAddWithoutValidation("If-None-Match", tag);
        HttpResponseMessage changed = await rig.Client.SendAsync(afterChange);

        first.Headers.CacheControl!.ToString().Should().Be("no-cache, private");
        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        changed.Headers.ETag!.Tag.Should().NotBe(tag);
    }

    [Fact]
    public async Task TheWatchlist_FilteredByPortfolio_ListsOnlyItsStocks_AndKeepsTheOtherFilters()
    {
        (WatchlistRig.Rig rig, string token) = await StartWithFourStocksAsync();
        Guid id = await CreatePortfolioAsync(rig, token, "Dos", ("XNAS", "AAPL"), ("XNAS", "MSFT"));

        JsonElement all = await WatchlistRig.BodyAsync(await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/watchlist?portfolio={id}", token)));
        JsonElement pending = await WatchlistRig.BodyAsync(await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/watchlist?portfolio={id}&priceStatus=pending", token)));

        all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("stock").GetProperty("ticker").GetString()).Should().Equal("AAPL", "MSFT");
        all.GetProperty("total").GetInt32().Should().Be(2);
        pending.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("stock").GetProperty("ticker").GetString()).Should().Equal("MSFT");
    }

    [Fact]
    public async Task TheWatchlist_FilteredByAForeignOrUnknownPortfolio_Is404_AndByABadId400()
    {
        (WatchlistRig.Rig rig, string token) = await StartWithFourStocksAsync();
        Guid id = await CreatePortfolioAsync(rig, token, "Mía", ("XNAS", "AAPL"));
        string other = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-filter-other" });

        HttpResponseMessage foreign = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/watchlist?portfolio={id}", other));
        HttpResponseMessage unknown = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/watchlist?portfolio={Guid.NewGuid()}", token));
        HttpResponseMessage bad = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist?portfolio=not-a-guid", token));

        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
