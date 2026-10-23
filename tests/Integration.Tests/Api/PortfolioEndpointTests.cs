extern alias HostApi;

using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using SakerWatch.Integration.Tests.Identity;
using SakerWatch.Integration.Tests.Infrastructure;
using SakerWatch.TestSupport;

namespace SakerWatch.Integration.Tests.Api;

/// <summary>
/// The portfolios through the real API host over a real database (ADR-0026): create, rename, order, put and take out stocks, and delete with the
/// confirmation that protects the watchlist. A fake provider stands in for Twelve Data when a stock is first added to the watchlist.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PortfolioEndpointTests(TimescalePostgresFixture postgres) : IClassFixture<TimescalePostgresFixture>, IAsyncLifetime
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

    private static int _keys;

    private static HttpRequestMessage Post(string path, string token, object body)
    {
        HttpRequestMessage request = WatchlistRig.Send(HttpMethod.Post, path, token, body);
        request.Headers.Add("Idempotency-Key", $"portfolio-key-{Interlocked.Increment(ref _keys):D10}");
        return request;
    }

    private static async Task<JsonElement> CreateAsync(WatchlistRig.Rig rig, string token, string name, string? description = null)
    {
        HttpResponseMessage response = await rig.Client.SendAsync(Post("/v1/portfolios", token, new { name, description }));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await WatchlistRig.BodyAsync(response);
    }

    private static async Task Follow(WatchlistRig.Rig rig, string token, string market, string ticker) =>
        (await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/watchlist/items/{market}/{ticker}", token, new { notes = $"nota {ticker}" }))).StatusCode
            .Should().Be(HttpStatusCode.Created);

    private static Task<HttpResponseMessage> PutItem(WatchlistRig.Rig rig, string token, string portfolio, string market, string ticker) =>
        rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{portfolio}/items/{market}/{ticker}", token));

    private static async Task<string[]> TickersOf(WatchlistRig.Rig rig, string token, string portfolio)
    {
        HttpResponseMessage response = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/portfolios/{portfolio}", token));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return [.. (await WatchlistRig.BodyAsync(response)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("stock").GetProperty("ticker").GetString()!)];
    }

    private static async Task<string[]> WatchlistTickers(WatchlistRig.Rig rig, string token) =>
        [.. (await WatchlistRig.BodyAsync(await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/watchlist", token)))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("stock").GetProperty("ticker").GetString()!)];

    [Fact]
    public async Task Create_TrimsTheName_Answers201WithALocation_AndStartsEmptyAtTheEnd()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p1" });

        HttpResponseMessage first = await rig.Client.SendAsync(Post("/v1/portfolios", token, new { name = "   Petróleo  ", description = "  Energía  " }));
        JsonElement second = await CreateAsync(rig, token, "Imanes");

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        JsonElement body = await WatchlistRig.BodyAsync(first);
        (body.GetProperty("name").GetString(), body.GetProperty("description").GetString(), body.GetProperty("position").GetInt32(), body.GetProperty("itemCount").GetInt32())
            .Should().Be(("Petróleo", "Energía", 0, 0));
        first.Headers.Location!.ToString().Should().Be($"/v1/portfolios/{body.GetProperty("id").GetGuid()}");
        second.GetProperty("position").GetInt32().Should().Be(1);
        (await rig.Api.Db.Admin.ScalarAsync<string>("SELECT name FROM watchlist.portfolios WHERE position = 0")).Should().Be("Petróleo", "the stored name has no spaces at its ends");
    }

    [Fact]
    public async Task Create_WithTheSameIdempotencyKey_ReplaysTheFirstAnswer_AndCreatesOne()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p2" });
        HttpRequestMessage Same() { HttpRequestMessage r = WatchlistRig.Send(HttpMethod.Post, "/v1/portfolios", token, new { name = "Una" }); r.Headers.Add("Idempotency-Key", "portfolio-replay-0000001"); return r; }

        HttpResponseMessage first = await rig.Client.SendAsync(Same());
        HttpResponseMessage replay = await rig.Client.SendAsync(Same());

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.StatusCode.Should().Be(HttpStatusCode.Created);
        replay.Headers.GetValues("Idempotent-Replayed").Should().Equal("true");
        (await WatchlistRig.BodyAsync(replay)).GetProperty("id").GetGuid().Should().Be((await WatchlistRig.BodyAsync(first)).GetProperty("id").GetGuid());
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.portfolios")).Should().Be(1);
    }

    [Fact]
    public async Task Create_WithoutAKey_OrWithABadName_IsA400_AndAReusedNameIsA409()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p3" });
        await CreateAsync(rig, token, "Tecnología");

        HttpResponseMessage noKey = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Post, "/v1/portfolios", token, new { name = "X" }));
        HttpResponseMessage empty = await rig.Client.SendAsync(Post("/v1/portfolios", token, new { name = "   " }));
        HttpResponseMessage tooLong = await rig.Client.SendAsync(Post("/v1/portfolios", token, new { name = new string('x', 61) }));
        HttpResponseMessage longDescription = await rig.Client.SendAsync(Post("/v1/portfolios", token, new { name = "Ok", description = new string('d', 501) }));
        HttpResponseMessage noBody = await rig.Client.SendAsync(Post("/v1/portfolios", token, new { }));
        HttpResponseMessage taken = await rig.Client.SendAsync(Post("/v1/portfolios", token, new { name = "  TECNOLOGÍA " }));

        new[] { noKey, empty, tooLong, longDescription, noBody }.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.BadRequest);
        taken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await WatchlistRig.BodyAsync(taken)).GetProperty("code").GetString().Should().Be("watchlist.portfolio.name_taken");
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.portfolios")).Should().Be(1);
    }

    [Fact]
    public async Task TheTwentyFirstPortfolio_Is409_AndAnotherUserIsNotAffected()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p4" });
        for (int i = 0; i < 20; i++)
        {
            await CreateAsync(rig, token, $"Cartera {i}");
        }

        HttpResponseMessage over = await rig.Client.SendAsync(Post("/v1/portfolios", token, new { name = "Una más" }));
        HttpResponseMessage others = await rig.Client.SendAsync(Post("/v1/portfolios", rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p4-other" }), new { name = "Una más" }));

        over.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await WatchlistRig.BodyAsync(over)).GetProperty("code").GetString().Should().Be("watchlist.portfolio.limit_reached");
        others.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task TwoParallelCreatesOfTheSameName_EndInOneOfThem()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p5" });

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => rig.Client.SendAsync(Post("/v1/portfolios", token, new { name = "Carrera" }))));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Where(r => r.StatusCode != HttpStatusCode.Created).Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.Conflict);
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.portfolios")).Should().Be(1);
    }

    [Fact]
    public async Task List_ShowsThePortfoliosByPosition_WithTheirCounts_AnETag_AndOnlyTheCallers()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p6" });
        JsonElement petroleo = await CreateAsync(rig, token, "Petróleo");
        await CreateAsync(rig, token, "Imanes");
        await Follow(rig, token, "XNAS", "AAPL");
        await PutItem(rig, token, petroleo.GetProperty("id").GetString()!, "XNAS", "AAPL");
        await CreateAsync(rig, rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p6-other" }), "Ajena");

        HttpResponseMessage response = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/portfolios", token));
        JsonElement[] items = [.. (await WatchlistRig.BodyAsync(response)).GetProperty("items").EnumerateArray()];
        HttpRequestMessage conditional = WatchlistRig.Send(HttpMethod.Get, "/v1/portfolios", token);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag!.Tag);

        items.Select(i => (i.GetProperty("name").GetString(), i.GetProperty("itemCount").GetInt32())).Should().Equal(("Petróleo", 1), ("Imanes", 0));
        (await rig.Client.SendAsync(conditional)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Update_ReplacesTheNameAndTheDescription_ANameTakenIs409_AndAnotherUsersPortfolioIs404()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p7" });
        JsonElement one = await CreateAsync(rig, token, "Uno");
        await CreateAsync(rig, token, "Dos");
        string id = one.GetProperty("id").GetString()!;

        HttpResponseMessage renamed = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{id}", token, new { name = "  Tres  ", description = "Con texto" }));
        HttpResponseMessage again = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{id}", token, new { name = "Tres", description = "Con texto" }));
        HttpResponseMessage taken = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{id}", token, new { name = "dos" }));
        HttpResponseMessage theirs = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{id}", rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p7-other" }), new { name = "Robada" }));
        HttpResponseMessage blank = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{id}", token, new { name = "  " }));

        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await WatchlistRig.BodyAsync(renamed)).GetProperty("name").GetString().Should().Be("Tres");
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (taken.StatusCode, theirs.StatusCode, blank.StatusCode).Should().Be((HttpStatusCode.Conflict, HttpStatusCode.NotFound, HttpStatusCode.BadRequest));
        (await rig.Api.Db.Admin.ScalarAsync<string>("SELECT name FROM watchlist.portfolios WHERE id = @id", ("id", Guid.Parse(id)))).Should().Be("Tres");
    }

    [Fact]
    public async Task PuttingAStockInAPortfolio_NeedsItOnTheWatchlist_Is201ThenOkWhenRepeated_AndTakesItOutWithoutLeavingTheWatchlist()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p8" });
        string id = (await CreateAsync(rig, token, "Mix")).GetProperty("id").GetString()!;

        HttpResponseMessage notFollowed = await PutItem(rig, token, id, "XNAS", "AAPL");
        await Follow(rig, token, "XNAS", "AAPL");
        await Follow(rig, token, "XNYS", "KO");
        HttpResponseMessage first = await PutItem(rig, token, id, "xnas", "aapl");
        HttpResponseMessage second = await PutItem(rig, token, id, "XNYS", "KO");
        HttpResponseMessage again = await PutItem(rig, token, id, "XNAS", "AAPL");
        HttpResponseMessage removed = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{id}/items/XNAS/AAPL", token));
        HttpResponseMessage removedAgain = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{id}/items/XNAS/AAPL", token));

        notFollowed.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await WatchlistRig.BodyAsync(notFollowed)).GetProperty("code").GetString().Should().Be("watchlist.portfolio.item_not_on_watchlist");
        (first.StatusCode, second.StatusCode, again.StatusCode).Should().Be((HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.OK));
        (await WatchlistRig.BodyAsync(first)).GetProperty("position").GetInt32().Should().Be(0);
        (await WatchlistRig.BodyAsync(second)).GetProperty("position").GetInt32().Should().Be(1);
        (removed.StatusCode, removedAgain.StatusCode).Should().Be((HttpStatusCode.NoContent, HttpStatusCode.NoContent));
        (await TickersOf(rig, token, id)).Should().Equal("KO");
        (await WatchlistTickers(rig, token)).Should().Equal("AAPL", "KO");
    }

    [Fact]
    public async Task PortfolioRoutes_AreScopedToTheCaller_AnotherUserGets404_AndNothingLeaks()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string alice = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p9-alice" });
        string bob = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p9-bob" });
        string id = (await CreateAsync(rig, alice, "De Alice")).GetProperty("id").GetString()!;
        await Follow(rig, alice, "XNAS", "AAPL");
        await PutItem(rig, alice, id, "XNAS", "AAPL");
        await Follow(rig, bob, "XNAS", "AAPL");

        HttpResponseMessage read = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/portfolios/{id}", bob));
        HttpResponseMessage put = await PutItem(rig, bob, id, "XNAS", "AAPL");
        HttpResponseMessage remove = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{id}/items/XNAS/AAPL", bob));
        HttpResponseMessage preview = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/portfolios/{id}/deletion-preview", bob));
        HttpResponseMessage delete = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{id}?confirm=true", bob));

        new[] { read, put, remove, preview }.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent, "a portfolio that is not yours is simply not there");
        (await TickersOf(rig, alice, id)).Should().Equal("AAPL");
        (await rig.Api.Db.Admin.ScalarAsync<long>("SELECT count(*) FROM watchlist.portfolios")).Should().Be(1);
    }

    [Fact]
    public async Task ReorderingThePortfoliosAndTheirStocks_NeedsTheExactSet_AndTheSameRequestTwiceIsTheSameOrder()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p10" });
        string a = (await CreateAsync(rig, token, "A")).GetProperty("id").GetString()!;
        string b = (await CreateAsync(rig, token, "B")).GetProperty("id").GetString()!;
        await Follow(rig, token, "XNAS", "AAPL");
        await Follow(rig, token, "XNYS", "KO");
        await PutItem(rig, token, a, "XNAS", "AAPL");
        await PutItem(rig, token, a, "XNYS", "KO");

        HttpResponseMessage incomplete = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, "/v1/portfolios/order", token, new { portfolioIds = new[] { a } }));
        HttpResponseMessage done = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, "/v1/portfolios/order", token, new { portfolioIds = new[] { b, a } }));
        HttpResponseMessage twice = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, "/v1/portfolios/order", token, new { portfolioIds = new[] { b, a } }));
        HttpResponseMessage noBody = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, "/v1/portfolios/order", token));
        HttpResponseMessage itemsBad = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{a}/items/order", token, new { items = new[] { new { market = "XNYS", ticker = "KO" } } }));
        HttpResponseMessage itemsDone = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{a}/items/order", token,
            new { items = new[] { new { market = "XNYS", ticker = "KO" }, new { market = "XNAS", ticker = "AAPL" } } }));

        incomplete.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await WatchlistRig.BodyAsync(incomplete)).GetProperty("code").GetString().Should().Be("watchlist.order_mismatch");
        (done.StatusCode, twice.StatusCode, noBody.StatusCode, itemsBad.StatusCode, itemsDone.StatusCode)
            .Should().Be((HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.NoContent));
        JsonElement[] list = [.. (await WatchlistRig.BodyAsync(await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/portfolios", token)))).GetProperty("items").EnumerateArray()];
        list.Select(p => p.GetProperty("name").GetString()).Should().Equal("B", "A");
        (await TickersOf(rig, token, a)).Should().Equal("KO", "AAPL");
    }

    // A holds AAPL and KO; B holds KO and MSFT; MSFT is followed and in no portfolio besides B... AAPL only in A, KO in both, NVDA in none.
    private async Task<(WatchlistRig.Rig Rig, string Token, string A, string B)> DeletionWorldAsync()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-delete" });
        string a = (await CreateAsync(rig, token, "A")).GetProperty("id").GetString()!;
        string b = (await CreateAsync(rig, token, "B")).GetProperty("id").GetString()!;
        foreach ((string market, string ticker) in new[] { ("XNAS", "AAPL"), ("XNYS", "KO"), ("XNAS", "NVDA") })
        {
            await Follow(rig, token, market, ticker);
        }

        await PutItem(rig, token, a, "XNAS", "AAPL");
        await PutItem(rig, token, a, "XNYS", "KO");
        await PutItem(rig, token, b, "XNYS", "KO");
        return (rig, token, a, b);
    }

    [Fact]
    public async Task TheDeletionPreview_ListsTheStocksOnlyThatPortfolioHolds_AndCountsTheOthers()
    {
        (WatchlistRig.Rig rig, string token, string a, _) = await DeletionWorldAsync();

        HttpResponseMessage response = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/portfolios/{a}/deletion-preview", token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement preview = await WatchlistRig.BodyAsync(response);
        preview.GetProperty("stocksToRemove").EnumerateArray().Select(s => s.GetProperty("ticker").GetString()).Should().Equal("AAPL");
        preview.GetProperty("stocksKept").GetInt32().Should().Be(1);
        preview.GetProperty("portfolio").GetProperty("name").GetString().Should().Be("A");
    }

    [Fact]
    public async Task Delete_WithoutConfirmationWhenStocksWouldLeave_Is409_AndChangesNothing_ThenWithItRemovesThemAndOnlyThem()
    {
        (WatchlistRig.Rig rig, string token, string a, string b) = await DeletionWorldAsync();

        HttpResponseMessage unconfirmed = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{a}", token));
        HttpResponseMessage falsy = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{a}?confirm=false", token));
        string[] before = await WatchlistTickers(rig, token);
        HttpResponseMessage noToken = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{a}?confirm=true", token));
        HttpResponseMessage badToken = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{a}?confirm=true&removalToken=not-a-token", token));
        // The refusal itself says what would leave and carries the token that confirms exactly that: no request to the preview is needed.
        JsonElement named = await WatchlistRig.BodyAsync(noToken);
        string removalToken = named.GetProperty("removalToken").GetString()!;
        HttpResponseMessage confirmed = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{a}?confirm=true&removalToken={removalToken}", token));
        HttpResponseMessage again = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{a}?confirm=true&removalToken={removalToken}", token));

        noToken.StatusCode.Should().Be(HttpStatusCode.Conflict);
        named.GetProperty("code").GetString().Should().Be("watchlist.portfolio.removal_token_required");
        named.GetProperty("stocksToRemove").EnumerateArray().Select(stock => stock.GetProperty("ticker").GetString()).Should().Equal("AAPL");
        badToken.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await WatchlistRig.BodyAsync(unconfirmed)).GetProperty("removalToken").GetString().Should().Be(removalToken, "the first refusal carries the same token");
        unconfirmed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await WatchlistRig.BodyAsync(unconfirmed)).GetProperty("code").GetString().Should().Be("watchlist.portfolio.delete_requires_confirmation");
        falsy.StatusCode.Should().Be(HttpStatusCode.Conflict);
        before.Should().Equal("AAPL", "KO", "NVDA");
        (confirmed.StatusCode, again.StatusCode).Should().Be((HttpStatusCode.NoContent, HttpStatusCode.NoContent));
        (await WatchlistTickers(rig, token)).Should().Equal("KO", "NVDA");
        (await TickersOf(rig, token, b)).Should().Equal("KO");
    }

    [Fact]
    public async Task Delete_ConfirmedWithTheTokenOfAnOlderPreview_IsAConflict_AndRemovesNothing()
    {
        (WatchlistRig.Rig rig, string token, string a, _) = await DeletionWorldAsync();
        string seen = (await WatchlistRig.BodyAsync(await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/portfolios/{a}/deletion-preview", token))))
            .GetProperty("removalToken").GetString()!;
        // A stock is put on the watchlist and in the portfolio after the caller looked.
        (await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, "/v1/watchlist/items/XNAS/MSFT", token, new { }))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Put, $"/v1/portfolios/{a}/items/XNAS/MSFT", token))).StatusCode.Should().Be(HttpStatusCode.Created);
        string[] before = await WatchlistTickers(rig, token);

        HttpResponseMessage stale = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{a}?confirm=true&removalToken={seen}", token));

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonElement fresh = await WatchlistRig.BodyAsync(stale);
        fresh.GetProperty("code").GetString().Should().Be("watchlist.portfolio.removal_changed");
        fresh.GetProperty("removalToken").GetString().Should().NotBe(seen, "the answer has the token of what would leave now");
        fresh.GetProperty("stocksToRemove").EnumerateArray().Select(stock => stock.GetProperty("ticker").GetString()).Should().Equal("AAPL", "MSFT");
        (await WatchlistTickers(rig, token)).Should().Equal(before);
    }

    [Fact]
    public async Task Delete_WhenEveryStockIsHeldElsewhere_OrThePortfolioIsEmpty_NeedsNoConfirmation()
    {
        (WatchlistRig.Rig rig, string token, _, string b) = await DeletionWorldAsync();
        string empty = (await CreateAsync(rig, token, "Vacía")).GetProperty("id").GetString()!;

        HttpResponseMessage emptyDeleted = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{empty}", token));
        HttpResponseMessage sharedDeleted = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{b}", token));

        (emptyDeleted.StatusCode, sharedDeleted.StatusCode).Should().Be((HttpStatusCode.NoContent, HttpStatusCode.NoContent));
        (await WatchlistTickers(rig, token)).Should().Equal("AAPL", "KO", "NVDA");
    }

    [Fact]
    public async Task APortfolioNeedsAToken_OnEveryRoute()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        var id = Guid.CreateVersion7();
        (HttpMethod Method, string Path)[] routes =
        [
            (HttpMethod.Post, "/v1/portfolios"), (HttpMethod.Get, "/v1/portfolios"), (HttpMethod.Put, "/v1/portfolios/order"),
            (HttpMethod.Get, $"/v1/portfolios/{id}"), (HttpMethod.Put, $"/v1/portfolios/{id}"), (HttpMethod.Delete, $"/v1/portfolios/{id}"),
            (HttpMethod.Get, $"/v1/portfolios/{id}/deletion-preview"), (HttpMethod.Put, $"/v1/portfolios/{id}/items/XNAS/AAPL"),
            (HttpMethod.Delete, $"/v1/portfolios/{id}/items/XNAS/AAPL"), (HttpMethod.Put, $"/v1/portfolios/{id}/items/order"),
        ];

        foreach ((HttpMethod method, string path) in routes)
        {
            (await rig.Client.SendAsync(WatchlistRig.Send(method, path, null))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path}");
        }
    }

    [Fact]
    public async Task ARouteWithAnIdThatIsNotAGuid_IsNotFound_AndTheRemovedPortfolioIsGoneFromTheList()
    {
        WatchlistRig.Rig rig = await StartRigAsync();
        string token = rig.Api.Idp.Mint(new TokenSpec { Subject = "account-p11" });
        string id = (await CreateAsync(rig, token, "Efímera")).GetProperty("id").GetString()!;

        HttpResponseMessage badId = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/portfolios/not-a-guid", token));
        await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Delete, $"/v1/portfolios/{id}", token));
        HttpResponseMessage gone = await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, $"/v1/portfolios/{id}", token));

        badId.StatusCode.Should().Be(HttpStatusCode.NotFound);
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await WatchlistRig.BodyAsync(await rig.Client.SendAsync(WatchlistRig.Send(HttpMethod.Get, "/v1/portfolios", token)))).GetProperty("items").GetArrayLength().Should().Be(0);
    }
}
