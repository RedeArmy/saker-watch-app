extern alias HostApi;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.Integration.Tests.Identity;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Providers.Fake;
using SakerWatch.TestSupport;

namespace SakerWatch.Integration.Tests.Api;

/// <summary>
/// The stock search and the watchlist through the real API host over a real PostgreSQL: authentication, the pipeline, row-level security, the
/// registration of a stock from the provider's answer (a fake provider stands in for Twelve Data) and the first load queued for the worker.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WatchlistEndpointTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>, IAsyncLifetime
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

    private Task<WatchlistRig.Rig> StartAsync(bool budgetSpent = false) => StartRigAsync(budgetSpent);

    private static HttpRequestMessage Send(HttpMethod method, string path, string? token, object? body = null) => WatchlistRig.Send(method, path, token, body);

    private static Task<JsonElement> BodyAsync(HttpResponseMessage response) => WatchlistRig.BodyAsync(response);

    [Fact]
    public async Task Search_ReturnsTheMatchesOfTheEnabledMarkets_WithTheCanonicalMarket()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-search" });

        HttpResponseMessage response = await rig.Client.SendAsync(Send(HttpMethod.Get, "/v1/stocks/search?q=aapl", token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement items = (await BodyAsync(response)).GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("market").GetString().Should().Be("XNAS", "XNGS is a tier of Nasdaq");
        items[0].GetProperty("ticker").GetString().Should().Be("AAPL");
        items[0].GetProperty("name").GetString().Should().Be("Apple Inc.");
        items[0].GetProperty("currency").GetString().Should().Be("USD");
        items[0].GetProperty("assetType").GetString().Should().Be("equity");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a;b")]
    [InlineData("%3Cscript%3E")]
    public async Task Search_ForTextThatIsNotAQuery_IsA400_AndNothingReachesTheProvider(string query)
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-search" });

        HttpResponseMessage response = await rig.Client.SendAsync(Send(HttpMethod.Get, $"/v1/stocks/search?q={query}", token));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("code").GetString().Should().Be("marketdata.search.query_invalid");
        rig.Provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Search_WhenTheSharedBudgetIsSpent_Is429_WithRetryAfter()
    {
        WatchlistRig.Rig rig = await StartAsync(budgetSpent: true);
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-search" });

        HttpResponseMessage response = await rig.Client.SendAsync(Send(HttpMethod.Get, "/v1/stocks/search?q=AAPL", token));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(37));
        (await BodyAsync(response)).GetProperty("code").GetString().Should().Be("marketdata.search.busy");
        rig.Provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Search_WhenTheProviderIsDown_Is503_WithRetryAfter_AndNeverItsMessage()
    {
        WatchlistRig.Rig rig = await StartAsync();
        rig.Provider.FailNext(ProviderErrors.Unavailable("twelvedata"));
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-search" });

        HttpResponseMessage response = await rig.Client.SendAsync(Send(HttpMethod.Get, "/v1/stocks/search?q=AAPL", token));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
        (await response.Content.ReadAsStringAsync()).Should().NotContainEquivalentOf("twelvedata");
    }

    [Fact]
    public async Task Search_IsLimitedPerUser_LikeTheOtherCostlyEndpoints()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-spammer" });

        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 12; i++)
        {
            statuses.Add((await rig.Client.SendAsync(Send(HttpMethod.Get, "/v1/stocks/search?q=AAPL", token))).StatusCode);
        }

        statuses.Take(10).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        statuses.Skip(10).Should().AllBeEquivalentTo(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task TheEndpoints_NeedAToken()
    {
        WatchlistRig.Rig rig = await StartAsync();

        HttpResponseMessage search = await rig.Client.SendAsync(Send(HttpMethod.Get, "/v1/stocks/search?q=AAPL", null));
        HttpResponseMessage put = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", null, new { }));
        HttpResponseMessage delete = await rig.Client.SendAsync(Send(HttpMethod.Delete, "/v1/watchlist/items/XNAS/AAPL", null));

        new[] { search, put, delete }.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.Unauthorized);
        rig.Provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Put_ANewStock_RegistersItFromTheProvider_QueuesItsHistory_AndAnswers201()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-put" });

        HttpResponseMessage response = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/xnas/aapl", token, new { notes = "  Esperar resultados  ", targetPrice = "180.5" }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        JsonElement item = await BodyAsync(response);
        item.GetProperty("notes").GetString().Should().Be("Esperar resultados");
        item.GetProperty("targetPrice").GetString().Should().Be("180.5", "decimals travel as strings");
        JsonElement stock = item.GetProperty("stock");
        (stock.GetProperty("market").GetString(), stock.GetProperty("ticker").GetString(), stock.GetProperty("name").GetString(), stock.GetProperty("currency").GetString())
            .Should().Be(("XNAS", "AAPL", "Apple Inc.", "USD"));
        Guid stockId = stock.GetProperty("id").GetGuid();

        (await rig.Api.Db.Admin.ScalarAsync<string>("SELECT concat_ws('|', market_mic, ticker, time_zone) FROM marketdata.stocks WHERE id = @id", ("id", stockId)))
            .Should().Be("XNAS|AAPL|America/New_York", "the time zone comes from our calendar, not from the provider's Asia/Tokyo");
        (await rig.Api.Db.Admin.ScalarAsync<string>("SELECT concat_ws('|', provider, provider_mic, provider_symbol) FROM marketdata.symbol_aliases WHERE stock_id = @id", ("id", stockId)))
            .Should().Be("fake|XNGS|AAPL");
        (await rig.Api.Db.Admin.ScalarAsync<string>("SELECT status FROM marketdata.ingestion_state WHERE stock_id = @id", ("id", stockId))).Should().Be("pending");
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue WHERE job_key = @key", ("key", $"marketdata.backfill:{stockId:N}"))).Should().Be(1);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(1);
    }

    [Fact]
    public async Task Put_TheSameStockAgain_Is200_ReplacesTheValues_AndStoresNothingNew()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-put-twice" });
        HttpResponseMessage first = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", token, new { notes = "antes", targetPrice = "100.50" }));
        int providerCalls = rig.Provider.Calls;

        HttpResponseMessage second = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", token, new { notes = "después" }));
        HttpResponseMessage third = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", token, new { notes = "después" }));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        third.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement item = await BodyAsync(second);
        item.GetProperty("notes").GetString().Should().Be("después");
        item.GetProperty("targetPrice").ValueKind.Should().Be(JsonValueKind.Null, "a PUT replaces: leaving the target out clears it");
        item.GetProperty("id").GetGuid().Should().Be((await BodyAsync(first)).GetProperty("id").GetGuid());
        rig.Provider.Calls.Should().Be(providerCalls, "the stock was already in the catalog, so the provider is not asked again");
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(1);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks")).Should().Be(1);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue")).Should().Be(1);
    }

    [Fact]
    public async Task Decimals_AreAStringWithoutTrailingZeros()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-decimal" });
        await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", token, new { targetPrice = "100.50" }));

        HttpResponseMessage again = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", token, new { targetPrice = "100.50" }));

        JsonElement target = (await BodyAsync(again)).GetProperty("targetPrice");
        target.ValueKind.Should().Be(JsonValueKind.String);
        target.GetString().Should().Be("100.5", "the column keeps eight decimals, the API does not show the zeros");
    }

    [Fact]
    public async Task Put_WithAnEmptyBody_IsFine_AndClearsNotesAndTarget()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-put-empty" });

        HttpResponseMessage response = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNYS/KO", token));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BodyAsync(response)).GetProperty("notes").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Put_ForAStockTheProviderDoesNotList_Is404_AndRegistersNothing()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-unknown" });

        HttpResponseMessage response = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/ZZZZ", token, new { }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await BodyAsync(response)).GetProperty("code").GetString().Should().Be("marketdata.stock.not_found");
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks")).Should().Be(0);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(0);
    }

    [Fact]
    public async Task Put_ForAStockOnAMarketThatIsNotEnabled_IsA400()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-market" });

        HttpResponseMessage notRouted = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/ARCX/SPY", token, new { }));
        HttpResponseMessage noCalendar = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XTSE/SHOP", token, new { }));

        new[] { notRouted, noCalendar }.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.BadRequest);
        (await BodyAsync(notRouted)).GetProperty("code").GetString().Should().Be("marketdata.stock.market_not_available");
        rig.Provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("/v1/watchlist/items/XN/AAPL", "{}")]
    [InlineData("/v1/watchlist/items/XNAS/A%20B", "{}")]
    [InlineData("/v1/watchlist/items/XNAS/AAPL", "{\"targetPrice\": \"0\"}")]
    [InlineData("/v1/watchlist/items/XNAS/AAPL", "{\"targetPrice\": \"-4\"}")]
    [InlineData("/v1/watchlist/items/XNAS/AAPL", "{\"targetPrice\": \"1.123456789\"}")]
    [InlineData("/v1/watchlist/items/XNAS/AAPL", "{\"targetPrice\": \"abc\"}")]
    [InlineData("/v1/watchlist/items/XNAS/AAPL", "{\"targetPrice\": \"1e3\"}")]
    [InlineData("/v1/watchlist/items/XNAS/AAPL", "{\"targetPrice\": 12.5}")]
    [InlineData("/v1/watchlist/items/XNAS/AAPL", "{\"notes\": 12}")]
    [InlineData("/v1/watchlist/items/XNAS/AAPL", "not json")]
    public async Task Put_WithAnInvalidRequest_IsA4xxProblem_AndStoresNothing(string path, string body)
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-invalid" });
        HttpRequestMessage request = Send(HttpMethod.Put, path, token);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage response = await rig.Client.SendAsync(request);

        ((int)response.StatusCode).Should().BeInRange(400, 499);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Exception").And.NotContain("   at ");
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(0);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks")).Should().Be(0);
    }

    [Fact]
    public async Task Put_NotesOverTheLimit_AreA400WithTheFieldNamed()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-notes" });

        HttpResponseMessage response = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", token, new { notes = new string('x', 2001) }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Contain("notes");
        rig.Provider.Calls.Should().Be(0, "a bad note must not cost a provider search");
    }

    [Fact]
    public async Task Put_WhenTheProviderCannotConfirmAnewStock_Is503_AndNothingIsStored()
    {
        WatchlistRig.Rig rig = await StartAsync();
        rig.Provider.FailNext(ProviderErrors.Unavailable("twelvedata"));
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-503" });

        HttpResponseMessage response = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", token, new { }));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull();
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks")).Should().Be(0);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue")).Should().Be(0);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(0);
    }

    [Fact]
    public async Task TwoUsers_AddingTheSameNewStockAtOnce_EndWithOneStockAndTwoItems()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string alice = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-alice" });
        string bob = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-bob" });

        HttpResponseMessage[] responses = await Task.WhenAll(
            rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", alice, new { notes = "alice" })),
            rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", bob, new { notes = "bob" })),
            rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", alice, new { notes = "alice" })));

        responses.Select(r => r.StatusCode).Should().OnlyContain(status => status == HttpStatusCode.Created || status == HttpStatusCode.OK);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks")).Should().Be(1);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(2);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(DISTINCT user_id) FROM watchlist.watchlist_items")).Should().Be(2);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM platform.job_queue")).Should().Be(1, "one first load for the stock, whoever registered it");
    }

    [Fact]
    public async Task Delete_TakesTheStockOffOnlyForTheCaller_AndRepeatingItIsStill204()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string alice = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-alice" });
        string bob = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-bob" });
        await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", alice, new { }));
        await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", bob, new { }));

        HttpResponseMessage first = await rig.Client.SendAsync(Send(HttpMethod.Delete, "/v1/watchlist/items/XNAS/AAPL", alice));
        HttpResponseMessage again = await rig.Client.SendAsync(Send(HttpMethod.Delete, "/v1/watchlist/items/XNAS/AAPL", alice));

        (first.StatusCode, again.StatusCode).Should().Be((HttpStatusCode.NoContent, HttpStatusCode.NoContent));
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.watchlist_items")).Should().Be(1, "Bob keeps his");
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks")).Should().Be(1, "the shared catalog is untouched");
    }

    [Fact]
    public async Task Delete_OfAStockNobodyEverAdded_Is204_WithoutCallingTheProviderOrRegisteringAnything()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-delete" });

        HttpResponseMessage response = await rig.Client.SendAsync(Send(HttpMethod.Delete, "/v1/watchlist/items/XNAS/AAPL", token));
        HttpResponseMessage malformed = await rig.Client.SendAsync(Send(HttpMethod.Delete, "/v1/watchlist/items/XN/AAPL", token));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        rig.Provider.Calls.Should().Be(0);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks")).Should().Be(0);
    }

    [Fact]
    public async Task AFullWatchlist_RefusesAnotherStock_With409_ButStillAllowsEditingOne()
    {
        WatchlistRig.Rig rig = await StartAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-full" });
        HttpResponseMessage first = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", token, new { }));
        Guid user = Guid.Parse((await BodyAsync(await rig.Client.SendAsync(ApiTestHost.Get("/v1/me", token)))).GetProperty("id").GetString()!);
        await rig.Api.Db.Admin.ExecuteAsync(
            "INSERT INTO watchlist.watchlist_items (id, user_id, stock_id, created_at, updated_at) SELECT gen_random_uuid(), @user, gen_random_uuid(), now(), now() FROM generate_series(1, 499)",
            ("user", user));

        HttpResponseMessage extra = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNYS/KO", token, new { }));
        HttpResponseMessage edit = await rig.Client.SendAsync(Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/AAPL", token, new { notes = "sigue" }));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        extra.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BodyAsync(extra)).GetProperty("code").GetString().Should().Be("watchlist.limit_reached");
        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM marketdata.stocks WHERE ticker = 'KO'")).Should().Be(0, "the refused add left no catalog row behind");
    }

    [Fact]
    public async Task TheApiRole_CanNeverWriteTheCatalogDirectly_OnlyThroughTheFunction()
    {
        WatchlistRig.Rig rig = await StartAsync();

        Func<Task> act = () => rig.Api.Db.Api.AsUserExecuteAsync(
            Guid.CreateVersion7(),
            "INSERT INTO marketdata.stocks (id, market_mic, ticker, name, currency, time_zone, asset_type, is_active, listed_at) VALUES (gen_random_uuid(), 'XNAS', 'EVIL', 'Evil', 'USD', 'UTC', 'equity', true, now())");

        (await act.Should().ThrowAsync<Npgsql.PostgresException>()).Which.SqlState.Should().Be("42501");
    }
}
