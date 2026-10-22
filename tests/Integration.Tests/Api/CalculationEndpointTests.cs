extern alias HostApi;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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
/// <c>POST /v1/calculations/partial-purchase</c> through the real API host over a real database: the price of a catalog stock or a given one, the
/// exchange rate with its path, day and source, the rounding, and every refusal. Prices and rates are seeded relative to the real clock.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CalculationEndpointTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>, IAsyncLifetime
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

    // AAPL has two years of closes up to the latest settled session; MSFT is registered and has none; the bank has USD to GTQ (yesterday) and USD to CAD.
    private async Task<(WatchlistRig.Rig Rig, string Token, decimal LastClose, DateOnly LastDay)> StartAsync()
    {
        WatchlistRig.Rig rig = await WatchlistRig.StartAsync(postgres);
        _rigs.Add(rig);
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-calc" });
        foreach (string ticker in new[] { "AAPL", "MSFT" })
        {
            (await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/watchlist/items/XNAS/{ticker}", token))).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        DateOnly settled = SessionSchedule.LatestSettled(_calendar, rig.Clock.GetUtcNow(), new IngestionOptions().SettleDelay);
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
        await rig.Api.Db.Admin.ExecuteAsync(
            "UPDATE marketdata.ingestion_state SET status = 'ok', last_success_at = now() WHERE stock_id IN (SELECT id FROM marketdata.stocks WHERE ticker = 'AAPL')");
        await rig.Api.Db.Admin.ExecuteAsync(
            "INSERT INTO marketdata.fx_rates (base, quote, as_of_date, source, rate, ingested_at) VALUES ('USD', 'GTQ', current_date - 1, 'banguat', 7.75, now()), ('USD', 'CAD', current_date - 1, 'banguat', 1.4283, now())");
        decimal close = await rig.Api.Db.Admin.ScalarAsync<decimal>("SELECT close FROM marketdata.price_daily p JOIN marketdata.stocks s ON s.id = p.stock_id WHERE s.ticker = 'AAPL' ORDER BY trade_date DESC LIMIT 1");
        return (rig, token, close, settled);
    }

    private static Task<HttpResponseMessage> PostAsync(WatchlistRig.Rig rig, string? token, object body)
    {
        HttpRequestMessage request = WatchlistRig.Send(HttpMethod.Post, "/v1/calculations/partial-purchase", token);
        request.Content = JsonContent.Create(body);
        return rig.Client.SendAsync(request);
    }

    private static async Task<JsonElement> OkAsync(WatchlistRig.Rig rig, string token, object body)
    {
        HttpResponseMessage response = await PostAsync(rig, token, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await WatchlistRig.BodyAsync(response);
    }

    private static decimal Num(JsonElement element) => decimal.Parse(element.GetString()!, CultureInfo.InvariantCulture);

    [Fact]
    public async Task AnAmountOnACatalogStock_UsesItsNewestClose_AsTraded_AndTheRateWithItsPathAndSource()
    {
        (WatchlistRig.Rig rig, string token, decimal close, DateOnly day) = await StartAsync();

        JsonElement answer = await OkAsync(rig, token, new { market = "XNAS", ticker = "AAPL", amount = "500" });

        (answer.GetProperty("mode").GetString(), answer.GetProperty("priceSource").GetString(), answer.GetProperty("quoteCurrency").GetString()).Should().Be(("amount", "catalog", "USD"));
        answer.GetProperty("stock").GetProperty("ticker").GetString().Should().Be("AAPL");
        (answer.GetProperty("priceStatus").GetString(), answer.GetProperty("priceDate").GetString()).Should().Be(("current", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        answer.GetProperty("priceNative").ValueKind.Should().Be(JsonValueKind.String, "decimals travel as strings");
        Num(answer.GetProperty("priceNative")).Should().Be(close, "the raw close, not the adjusted one the windows use");
        answer.GetProperty("fxPath").EnumerateArray().Select(c => c.GetString()).Should().Equal("USD", "GTQ");
        (Num(answer.GetProperty("fxRate")), answer.GetProperty("fxSource").GetString(), answer.GetProperty("fxStale").GetBoolean()).Should().Be((7.75m, "banguat", false));
        answer.GetProperty("fxLegs").GetArrayLength().Should().Be(1);
        Num(answer.GetProperty("sharesFraction")).Should().Be(Math.Round(500m / close, 6, MidpointRounding.ToZero));
        Num(answer.GetProperty("amountNative")).Should().Be(500m);
        Num(answer.GetProperty("amountGtq")).Should().Be(3875m);
        Num(answer.GetProperty("costOneShareGtq")).Should().Be(Math.Round(close * 7.75m, 2, MidpointRounding.ToEven));
        answer.GetProperty("disclaimerKey").GetString().Should().Be("marketdata.fx.reference_only");
    }

    [Fact]
    public async Task SharesOnACatalogStock_CostTheirProduct()
    {
        (WatchlistRig.Rig rig, string token, decimal close, _) = await StartAsync();

        JsonElement answer = await OkAsync(rig, token, new { market = "XNAS", ticker = "AAPL", shares = "2.5" });

        answer.GetProperty("mode").GetString().Should().Be("shares");
        Num(answer.GetProperty("sharesFraction")).Should().Be(2.5m);
        Num(answer.GetProperty("amountNative")).Should().Be(Math.Round(2.5m * close, 2, MidpointRounding.ToEven));
        Num(answer.GetProperty("amountGtq")).Should().Be(Math.Round(2.5m * close * 7.75m, 2, MidpointRounding.ToEven));
    }

    [Fact]
    public async Task AGivenPrice_IsTheSpecExample_FiftyDollarsOnASixHundredDollarShare()
    {
        (WatchlistRig.Rig rig, string token, _, _) = await StartAsync();

        JsonElement answer = await OkAsync(rig, token, new { price = "600", currency = "USD", amount = "50" });

        answer.GetProperty("priceSource").GetString().Should().Be("manual");
        answer.GetProperty("stock").ValueKind.Should().Be(JsonValueKind.Null);
        (Num(answer.GetProperty("sharesFraction")), Num(answer.GetProperty("amountNative")), Num(answer.GetProperty("costOneShareGtq")), Num(answer.GetProperty("amountGtq")))
            .Should().Be((0.083333m, 50m, 4650m, 387.5m));
    }

    [Fact]
    public async Task ACommission_IsPartOfTheTotalPaid_AndShownApart()
    {
        (WatchlistRig.Rig rig, string token, _, _) = await StartAsync();

        JsonElement invested = await OkAsync(rig, token, new { price = "600", currency = "USD", amount = "101", feePercent = "1" });
        JsonElement bought = await OkAsync(rig, token, new { price = "50", currency = "USD", shares = "2", feePercent = "1" });

        (Num(invested.GetProperty("feePercent")), Num(invested.GetProperty("amountNative")), Num(invested.GetProperty("feeNative")), Num(invested.GetProperty("amountGtq")), Num(invested.GetProperty("feeGtq")), Num(invested.GetProperty("sharesFraction")))
            .Should().Be((1m, 101m, 1m, 782.75m, 7.75m, 0.166666m));
        (Num(bought.GetProperty("amountNative")), Num(bought.GetProperty("feeNative")), Num(bought.GetProperty("amountGtq")), Num(bought.GetProperty("feeGtq")))
            .Should().Be((101m, 1m, 782.75m, 7.75m));
    }

    [Fact]
    public async Task WithoutACommission_ItIsZero_AndZeroGivesTheSameAnswer()
    {
        (WatchlistRig.Rig rig, string token, _, _) = await StartAsync();

        JsonElement without = await OkAsync(rig, token, new { price = "600", currency = "USD", amount = "50" });
        JsonElement zero = await OkAsync(rig, token, new { price = "600", currency = "USD", amount = "50", feePercent = "0" });

        (Num(without.GetProperty("feePercent")), Num(without.GetProperty("feeNative")), Num(without.GetProperty("feeGtq"))).Should().Be((0m, 0m, 0m));
        zero.GetRawText().Should().Be(without.GetRawText());
    }

    [Fact]
    public async Task APriceInCanadianDollars_GoesThroughTheDollar_ReturningEachStep()
    {
        (WatchlistRig.Rig rig, string token, _, _) = await StartAsync();

        JsonElement answer = await OkAsync(rig, token, new { price = "100", currency = "cad", shares = "2" });

        answer.GetProperty("fxPath").EnumerateArray().Select(c => c.GetString()).Should().Equal("CAD", "USD", "GTQ");
        JsonElement[] legs = [.. answer.GetProperty("fxLegs").EnumerateArray()];
        legs.Select(leg => (leg.GetProperty("from").GetString(), leg.GetProperty("to").GetString(), leg.GetProperty("inverted").GetBoolean()))
            .Should().Equal(("CAD", "USD", true), ("USD", "GTQ", false));
        answer.GetProperty("fxSource").GetString().Should().Be("banguat", "one source for both steps is named once");
        Num(answer.GetProperty("fxRate")).Should().BeApproximately(7.75m / 1.4283m, 0.0000000001m);
        Num(answer.GetProperty("amountNative")).Should().Be(200m);
        Num(answer.GetProperty("amountGtq")).Should().Be(Math.Round(200m * 7.75m / 1.4283m, 2, MidpointRounding.ToEven));
    }

    [Fact]
    public async Task APriceInQuetzales_NeedsNoConversion()
    {
        (WatchlistRig.Rig rig, string token, _, _) = await StartAsync();

        JsonElement answer = await OkAsync(rig, token, new { price = "40", currency = "GTQ", amount = "100" });

        answer.GetProperty("fxPath").EnumerateArray().Select(c => c.GetString()).Should().Equal("GTQ");
        (Num(answer.GetProperty("fxRate")), answer.GetProperty("fxSource").ValueKind, answer.GetProperty("fxLegs").GetArrayLength()).Should().Be((1m, JsonValueKind.Null, 0));
        Num(answer.GetProperty("sharesFraction")).Should().Be(2.5m);
    }

    [Fact]
    public async Task AnOldRate_IsUsedAndFlaggedStale()
    {
        (WatchlistRig.Rig rig, string token, _, _) = await StartAsync();
        await rig.Api.Db.Admin.ExecuteAsync("DELETE FROM marketdata.fx_rates WHERE quote = 'GTQ'");
        await rig.Api.Db.Admin.ExecuteAsync("INSERT INTO marketdata.fx_rates (base, quote, as_of_date, source, rate, ingested_at) VALUES ('USD', 'GTQ', current_date - 20, 'banguat', 7.6, now())");

        JsonElement answer = await OkAsync(rig, token, new { price = "600", currency = "USD", amount = "50" });

        answer.GetProperty("fxStale").GetBoolean().Should().BeTrue();
        Num(answer.GetProperty("fxRate")).Should().Be(7.6m, "the newest rate there is, with its own day, never a day it was not published for");
    }

    [Fact]
    public async Task AStockWithoutAPriceYet_Is422_AndAnUnknownStock_Or_ACurrencyWithoutARate_Is404()
    {
        (WatchlistRig.Rig rig, string token, _, _) = await StartAsync();

        HttpResponseMessage pending = await PostAsync(rig, token, new { market = "XNAS", ticker = "MSFT", amount = "50" });
        HttpResponseMessage unknown = await PostAsync(rig, token, new { market = "XNAS", ticker = "ZZZZ", amount = "50" });
        HttpResponseMessage noRate = await PostAsync(rig, token, new { price = "10", currency = "EUR", amount = "5" });

        pending.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await WatchlistRig.BodyAsync(pending)).GetProperty("code").GetString().Should().Be("calculator.price_unavailable");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        noRate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await WatchlistRig.BodyAsync(noRate)).GetProperty("code").GetString().Should().Be("marketdata.fx.rate_unavailable");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"market\":\"XNAS\",\"ticker\":\"AAPL\"}")]
    [InlineData("{\"market\":\"XNAS\",\"ticker\":\"AAPL\",\"amount\":\"5\",\"shares\":\"1\"}")]
    [InlineData("{\"market\":\"XNAS\",\"ticker\":\"AAPL\",\"price\":\"5\",\"currency\":\"USD\",\"amount\":\"5\"}")]
    [InlineData("{\"price\":\"5\",\"amount\":\"5\"}")]
    [InlineData("{\"price\":\"5\",\"currency\":\"US\",\"amount\":\"5\"}")]
    [InlineData("{\"price\":\"0\",\"currency\":\"USD\",\"amount\":\"5\"}")]
    [InlineData("{\"price\":\"600\",\"currency\":\"USD\",\"amount\":\"-5\"}")]
    [InlineData("{\"price\":\"600\",\"currency\":\"USD\",\"shares\":\"0\"}")]
    [InlineData("{\"price\":\"600\",\"currency\":\"USD\",\"shares\":\"1.2345678\"}")]
    [InlineData("{\"price\":\"0.0000001\",\"currency\":\"USD\",\"amount\":\"5\"}")]
    [InlineData("{\"price\":600,\"currency\":\"USD\",\"amount\":\"5\"}")]
    [InlineData("{\"price\":\"abc\",\"currency\":\"USD\",\"amount\":\"5\"}")]
    [InlineData("{\"price\":\"600\",\"currency\":\"USD\",\"amount\":\"5\",\"feePercent\":\"10.01\"}")]
    [InlineData("{\"price\":\"600\",\"currency\":\"USD\",\"amount\":\"5\",\"feePercent\":\"-1\"}")]
    [InlineData("{\"price\":\"600\",\"currency\":\"USD\",\"amount\":\"5\",\"feePercent\":1}")]
    [InlineData("not json")]
    public async Task ABadRequest_Is400_WithTheFieldsToFix_NeverA500(string body)
    {
        (WatchlistRig.Rig rig, string token, _, _) = await StartAsync();
        HttpRequestMessage request = WatchlistRig.Send(HttpMethod.Post, "/v1/calculations/partial-purchase", token);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await rig.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Exception").And.NotContain("   at ");
    }

    [Fact]
    public async Task TheSameRequestTwice_GivesTheSameAnswer_WithoutAnIdempotencyKey()
    {
        (WatchlistRig.Rig rig, string token, _, _) = await StartAsync();
        object body = new { market = "XNAS", ticker = "AAPL", amount = "123.45" };

        string first = await (await PostAsync(rig, token, body)).Content.ReadAsStringAsync();
        string second = await (await PostAsync(rig, token, body)).Content.ReadAsStringAsync();

        second.Should().Be(first);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.idempotency_keys")).Should().Be(0, "a calculation stores nothing");
    }

    [Fact]
    public async Task WithoutAToken_ItIs401()
    {
        (WatchlistRig.Rig rig, _, _, _) = await StartAsync();

        (await PostAsync(rig, null, new { price = "600", currency = "USD", amount = "50" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ACalculation_NeverCallsTheProvider_AndChangesNothing()
    {
        (WatchlistRig.Rig rig, string token, _, _) = await StartAsync();
        int calls = rig.Provider.Calls;
        long items = await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items");

        await OkAsync(rig, token, new { market = "XNAS", ticker = "AAPL", amount = "50" });
        await PostAsync(rig, token, new { market = "XNAS", ticker = "ZZZZ", amount = "50" });

        rig.Provider.Calls.Should().Be(calls, "an unknown stock is looked up in the catalog and never registered from here");
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(items);
    }
}
