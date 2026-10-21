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
/// <c>GET /v1/stocks/{market}/{ticker}/prices</c> and <c>GET /v1/watchlist/sparklines</c> through the real API host over a real database
/// (ADR-0038). Prices are seeded relative to the real clock, because the status of the prices runs on it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PriceHistoryEndpointTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>, IAsyncLifetime
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

    // AAPL has two years of weekdays up to the latest settled session (the adjusted close grows by a half a point a day, the raw one is twice it);
    // MSFT is registered and has none. Both are on the watchlist of the token.
    private async Task<(WatchlistRig.Rig Rig, string Token, DateOnly Last)> StartAsync()
    {
        WatchlistRig.Rig rig = await WatchlistRig.StartAsync(postgres);
        _rigs.Add(rig);
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-history" });
        foreach (string ticker in new[] { "AAPL", "MSFT" })
        {
            (await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/watchlist/items/XNAS/{ticker}", token))).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        DateOnly settled = SessionSchedule.LatestSettled(_calendar, DateTimeOffset.UtcNow, new IngestionOptions().SettleDelay);
        await rig.Api.Db.Admin.ExecuteAsync(
            """
            INSERT INTO marketdata.price_daily (stock_id, trade_date, open, high, low, close, adjusted_close, volume, source, is_final, ingested_at)
            SELECT s.id, x.d::date, x.a * 2, x.a * 2 + 1, x.a * 2 - 1, x.a * 2, x.a, 1000, 'twelvedata', true, now()
            FROM marketdata.stocks s
            CROSS JOIN LATERAL (SELECT d, 100 + 0.5 * row_number() OVER (ORDER BY d) AS a
                                FROM generate_series(@last::date - interval '730 days', @last::date, interval '1 day') AS g(d)
                                WHERE extract(isodow FROM d) < 6) x
            WHERE s.ticker = 'AAPL'
            """,
            ("last", settled));
        await rig.Api.Db.Admin.ExecuteAsync("UPDATE marketdata.ingestion_state SET status = 'ok', last_success_at = now() WHERE stock_id IN (SELECT id FROM marketdata.stocks WHERE ticker = 'AAPL')");
        return (rig, token, settled);
    }

    private static async Task<JsonElement> OkAsync(WatchlistRig.Rig rig, string token, string path)
    {
        HttpResponseMessage response = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, path, token));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await WatchlistRig.BodyAsync(response);
    }

    private static DateOnly Day(JsonElement element) => DateOnly.Parse(element.GetString()!, CultureInfo.InvariantCulture);

    private static decimal Num(JsonElement element) => decimal.Parse(element.GetString()!, CultureInfo.InvariantCulture);

    private static DateOnly[] Dates(JsonElement series) => [.. series.GetProperty("points").EnumerateArray().Select(point => Day(point.GetProperty("date")))];

    [Fact]
    public async Task AMonth_StartsWhereTheOneMonthWindowDoes_WithBothClosesAsStrings_AndTheStatus()
    {
        (WatchlistRig.Rig rig, string token, DateOnly last) = await StartAsync();

        JsonElement series = await OkAsync(rig, token, "/v1/stocks/XNAS/AAPL/prices?range=1M");

        series.GetProperty("stock").GetProperty("ticker").GetString().Should().Be("AAPL");
        (series.GetProperty("range").GetString(), series.GetProperty("interval").GetString(), series.GetProperty("priceStatus").GetString()).Should().Be(("1M", "day", "current"));
        (Day(series.GetProperty("from")), Day(series.GetProperty("to"))).Should().Be((last.AddMonths(-1), last));
        DateOnly[] dates = Dates(series);
        dates.Should().BeInAscendingOrder().And.OnlyContain(date => date >= last.AddMonths(-1) && date <= last);
        series.GetProperty("count").GetInt32().Should().Be(dates.Length);
        dates[^1].Should().Be(last);
        JsonElement newest = series.GetProperty("points").EnumerateArray().Last();
        newest.GetProperty("close").ValueKind.Should().Be(JsonValueKind.String, "decimals travel as strings");
        Num(newest.GetProperty("close")).Should().Be(Num(newest.GetProperty("adjustedClose")) * 2, "the fixture's raw close is twice the adjusted one");
        newest.GetProperty("isFinal").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TheDefaultRange_IsAYear_AndTheMarketAndTheRangeIgnoreCase()
    {
        (WatchlistRig.Rig rig, string token, DateOnly last) = await StartAsync();

        JsonElement series = await OkAsync(rig, token, "/v1/stocks/xnas/aapl/prices");
        JsonElement lower = await OkAsync(rig, token, "/v1/stocks/XNAS/AAPL/prices?range=ytd&interval=DAY");

        (series.GetProperty("range").GetString(), Day(series.GetProperty("from"))).Should().Be(("1Y", last.AddYears(-1)));
        (lower.GetProperty("range").GetString(), Day(lower.GetProperty("from"))).Should().Be(("YTD", new DateOnly(last.Year - 1, 12, 31)));
    }

    [Fact]
    public async Task FiveYears_AreWeeks_AndAllTheHistoryIsMonths_EachShownByItsLastTradingDay()
    {
        (WatchlistRig.Rig rig, string token, DateOnly last) = await StartAsync();

        JsonElement weeks = await OkAsync(rig, token, "/v1/stocks/XNAS/AAPL/prices?range=5Y");
        JsonElement months = await OkAsync(rig, token, "/v1/stocks/XNAS/AAPL/prices?range=MAX");

        weeks.GetProperty("interval").GetString().Should().Be("week");
        months.GetProperty("interval").GetString().Should().Be("month");
        months.GetProperty("from").ValueKind.Should().Be(JsonValueKind.Null);
        DateOnly[] weekDates = Dates(weeks);
        weekDates.Should().BeInAscendingOrder().And.HaveCount(weekDates.Select(date => System.Globalization.ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)) + (date.Year * 100)).Distinct().Count());
        weekDates[^1].Should().Be(last);
        weekDates.Length.Should().BeInRange(100, 110, "two years of weeks");
        DateOnly[] monthDates = Dates(months);
        monthDates.Select(date => (date.Year, date.Month)).Should().OnlyHaveUniqueItems();
        monthDates.Length.Should().BeInRange(24, 26);
        monthDates[^1].Should().Be(last);
    }

    [Fact]
    public async Task AStockWithoutPrices_IsAnEmptySeriesWithItsStatus_AndAnUnknownOneIs404()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartAsync();

        JsonElement pending = await OkAsync(rig, token, "/v1/stocks/XNAS/MSFT/prices?range=1Y");
        HttpResponseMessage unknown = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/stocks/XNAS/ZZZZ/prices", token));

        (pending.GetProperty("priceStatus").GetString(), pending.GetProperty("count").GetInt32(), pending.GetProperty("to").ValueKind).Should().Be(("pending", 0, JsonValueKind.Null));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("?range=2Y")]
    [InlineData("?range=")]
    [InlineData("?interval=hourly")]
    [InlineData("?range=1M&interval=")]
    public async Task ABadRangeOrInterval_Is400_NeverA500(string query)
    {
        (WatchlistRig.Rig rig, string token, _) = await StartAsync();

        HttpResponseMessage response = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/stocks/XNAS/AAPL/prices" + query, token));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Exception").And.NotContain("   at ");
    }

    [Fact]
    public async Task TheSeries_HasAnETag_AnUnchangedOneGets304_AndWithoutATokenItIs401()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartAsync();
        HttpResponseMessage first = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/stocks/XNAS/AAPL/prices?range=1M", token));
        string tag = first.Headers.ETag!.Tag;
        HttpRequestMessage conditional = WatchlistRig.Send(HttpMethod.Get, "/v1/stocks/XNAS/AAPL/prices?range=1M", token);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", tag);

        HttpResponseMessage notModified = await rig.Client.SendAsync(conditional);
        HttpResponseMessage anonymous = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/stocks/XNAS/AAPL/prices", null));

        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        first.Headers.CacheControl!.ToString().Should().Be("no-cache, private");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ThePricesAreReadFromTheCatalogOnly_TheProviderIsNeverCalled()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartAsync();
        int calls = rig.Provider.Calls;

        await OkAsync(rig, token, "/v1/stocks/XNAS/AAPL/prices?range=MAX");
        await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/stocks/XNAS/ZZZZ/prices", token));
        await OkAsync(rig, token, "/v1/watchlist/sparklines");

        rig.Provider.Calls.Should().Be(calls);
    }

    [Fact]
    public async Task TheSparklines_HaveALinePerStock_OrderedByTicker_OldestFirst_AndAnEmptyOneForAStockWithoutPrices()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartAsync();
        long rows = await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.price_daily p JOIN marketdata.stocks s ON s.id = p.stock_id WHERE s.ticker = 'AAPL'");
        decimal[] expected = [.. Enumerable.Range(0, 10).Select(i => 100m + (0.5m * (rows - 9 + i)))];

        JsonElement answer = await OkAsync(rig, token, "/v1/watchlist/sparklines?days=10");

        answer.GetProperty("days").GetInt32().Should().Be(10);
        JsonElement[] lines = [.. answer.GetProperty("items").EnumerateArray()];
        lines.Select(line => line.GetProperty("stock").GetProperty("ticker").GetString()).Should().Equal("AAPL", "MSFT");
        decimal[] apple = [.. lines[0].GetProperty("points").EnumerateArray().Select(Num)];
        apple.Should().Equal(expected);
        lines[0].GetProperty("points")[0].ValueKind.Should().Be(JsonValueKind.String);
        lines[1].GetProperty("points").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("?days=4")]
    [InlineData("?days=251")]
    [InlineData("?days=abc")]
    [InlineData("?portfolio=not-a-guid")]
    public async Task BadSparklineParameters_Are400(string query)
    {
        (WatchlistRig.Rig rig, string token, _) = await StartAsync();

        (await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist/sparklines" + query, token))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheSparklines_ArePerUser_NarrowedByPortfolio_AndAForeignPortfolioIs404()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartAsync();
        string other = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-history-other" });
        await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", other));
        HttpRequestMessage create = WatchlistRig.Send(HttpMethod.Post, "/v1/portfolios", token, new { name = "Solo MSFT" });
        create.Headers.Add("Idempotency-Key", "history-portfolio-0000001");
        Guid portfolio = (await WatchlistRig.BodyAsync(await rig.Client.SendAsync(create))).GetProperty("id").GetGuid();
        await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{portfolio}/items/XNAS/MSFT", token));

        JsonElement mine = await OkAsync(rig, token, $"/v1/watchlist/sparklines?portfolio={portfolio}");
        JsonElement theirs = await OkAsync(rig, other, "/v1/watchlist/sparklines");
        HttpResponseMessage foreign = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/watchlist/sparklines?portfolio={portfolio}", other));
        HttpResponseMessage anonymous = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist/sparklines", null));

        mine.GetProperty("items").EnumerateArray().Select(line => line.GetProperty("stock").GetProperty("ticker").GetString()).Should().Equal("MSFT");
        theirs.GetProperty("items").EnumerateArray().Select(line => line.GetProperty("stock").GetProperty("ticker").GetString()).Should().Equal("AAPL");
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheSparklines_HaveAnETag_AndAnUnchangedAnswerGets304()
    {
        (WatchlistRig.Rig rig, string token, _) = await StartAsync();
        HttpResponseMessage first = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist/sparklines", token));
        HttpRequestMessage conditional = WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist/sparklines", token);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.Tag);

        (await rig.Client.SendAsync(conditional)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }
}
