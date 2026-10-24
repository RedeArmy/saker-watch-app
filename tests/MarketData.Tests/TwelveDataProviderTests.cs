using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure;
using SakerWatch.MarketData.Infrastructure.Providers.TwelveData;

namespace SakerWatch.MarketData.Tests;

/// <summary>
/// The Twelve Data adapter against responses recorded from the real API on 2026-10-16 and 2026-10-17 (files without a suffix), taken from its documentation
/// (<c>.docs</c>) or written by hand (<c>.synthetic</c>). Nothing here calls the network.
/// </summary>
public sealed class TwelveDataProviderTests
{
    private const string _key = "test-key-not-a-secret";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.twelvedata.com/") };
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "twelvedata", name));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ScriptedBudget(params BudgetDecision[] decisions) : IProviderCallBudget
    {
        private readonly Queue<BudgetDecision> _decisions = new(decisions);

        public List<(string Provider, BudgetLimits Limits, int Credits)> Spent { get; } = [];

        public List<(string Provider, int Credits)> Released { get; } = [];

        public Task ReleaseAsync(string provider, int credits, CancellationToken cancellationToken)
        {
            Released.Add((provider, credits));
            return Task.CompletedTask;
        }

        public Task<BudgetDecision> TryAcquireAsync(string provider, BudgetLimits limits, int credits, CancellationToken cancellationToken)
        {
            Spent.Add((provider, limits, credits));
            return Task.FromResult(_decisions.Count > 0 ? _decisions.Dequeue() : BudgetDecision.Allowed);
        }
    }

    private sealed class RecordingStore(bool fail = false) : IProviderPayloadStore
    {
        public List<ProviderPayload> Stored { get; } = [];

        public Task StoreAsync(ProviderPayload payload, CancellationToken cancellationToken)
        {
            if (fail)
            {
                throw new TimeoutException("mongo is down");
            }

            Stored.Add(payload);
            return Task.CompletedTask;
        }
    }

    private static readonly DateTimeOffset _now = new(2026, 10, 16, 21, 0, 0, TimeSpan.Zero);

    private static (TwelveDataProvider Provider, StubHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond, IProviderCallBudget? budget = null, IProviderPayloadStore? payloads = null)
    {
        var handler = new StubHandler(respond);
        var provider = new TwelveDataProvider(
            new SingleClientFactory(handler),
            Options.Create(new TwelveDataOptions { ApiKey = _key }),
            budget ?? new ScriptedBudget(),
            payloads ?? new RecordingStore(),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(_now),
            NullLogger<TwelveDataProvider>.Instance);
        return (provider, handler);
    }

    private static DailySeriesRequest Daily(string mic = "XNGS", string symbol = "AAPL", bool adjusted = true, DateOnly? from = null, DateOnly? to = null) =>
        new(new ProviderListingKey(mic, symbol), from, to, adjusted);

    [Fact]
    public async Task GetDaily_AdjustedAapl_ReturnsTheRecordedBarsInAscendingOrder()
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("time_series_aapl_adjusted.json")));

        DailySeries series = (await provider.GetDailyAsync(Daily(), CancellationToken.None)).Value;

        series.Adjusted.Should().BeTrue();
        series.Rejected.Should().Be(0);
        series.Bars.Should().HaveCount(8);
        series.Bars.Select(b => b.Date).Should().BeInAscendingOrder();
        series.Bars[0].Date.Should().Be(new DateOnly(2006, 11, 30));
        series.Bars[0].Close.Should().Be(2.78538m);
        series.Bars[0].Volume.Should().Be(870486400);
        series.Bars[^1].Date.Should().Be(new DateOnly(2026, 10, 16));
        series.Bars[^1].Close.Should().Be(343.415m);
    }

    [Fact]
    public async Task GetDaily_SendsTheKeyOnlyInTheAuthorizationHeader_AndAsksForTheRightParameters()
    {
        (TwelveDataProvider provider, StubHandler handler) = Build(_ => Json(Fixture("time_series_nvda_split.json")));

        await provider.GetDailyAsync(Daily("XNGS", "NVDA", from: new DateOnly(2024, 6, 1), to: new DateOnly(2024, 6, 20)), CancellationToken.None);

        HttpRequestMessage request = handler.Requests.Single();
        request.Headers.Authorization!.Scheme.Should().Be("apikey");
        request.Headers.Authorization.Parameter.Should().Be(_key);
        string url = request.RequestUri!.OriginalString;
        url.Should().NotContain(_key, "the key must never appear in a URL, which proxies and logs keep");
        url.Should().Contain("symbol=NVDA").And.Contain("mic_code=XNGS").And.Contain("interval=1day").And.Contain("adjust=all")
            .And.Contain("order=asc").And.Contain("outputsize=5000").And.Contain("start_date=2024-06-01").And.Contain("end_date=2024-06-21");
    }

    [Fact]
    public async Task GetDaily_RawPrices_AsksForNoAdjustment()
    {
        (TwelveDataProvider provider, StubHandler handler) = Build(_ => Json(Fixture("time_series_spy_etf.json")));

        DailySeries series = (await provider.GetDailyAsync(Daily("ARCX", "SPY", adjusted: false), CancellationToken.None)).Value;

        handler.Requests.Single().RequestUri!.OriginalString.Should().Contain("adjust=none").And.NotContain("start_date");
        series.Adjusted.Should().BeFalse();
    }

    [Fact]
    public async Task GetDaily_NvdaAroundItsSplit_IsContinuous()
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("time_series_nvda_split.json")));

        DailySeries series = (await provider.GetDailyAsync(Daily("XNGS", "NVDA"), CancellationToken.None)).Value;

        series.Bars.Should().HaveCount(12);
        series.Bars.Zip(series.Bars.Skip(1), (a, b) => Math.Abs((b.Close - a.Close) / a.Close)).Should().OnlyContain(change => change < 0.15m,
            "the split-adjusted series has no tenfold jump");
    }

    [Fact]
    public async Task GetDaily_KeepsOnlyTheRequestedRange_WhateverTheProviderReturns()
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("time_series_nvda_split.json")));

        DailySeries series = (await provider.GetDailyAsync(
            Daily("XNGS", "NVDA", from: new DateOnly(2024, 6, 5), to: new DateOnly(2024, 6, 12)), CancellationToken.None)).Value;

        series.Bars.Select(b => b.Date).Should().Equal(
            new DateOnly(2024, 6, 5), new DateOnly(2024, 6, 6), new DateOnly(2024, 6, 7), new DateOnly(2024, 6, 10), new DateOnly(2024, 6, 11), new DateOnly(2024, 6, 12));
        series.Rejected.Should().Be(0, "a bar outside the range is not a bad bar");
    }

    [Fact]
    public async Task GetDaily_ARangeWithNoSessions_IsAnEmptySeries_NotAFailure()
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("error_no_data.json"), HttpStatusCode.BadRequest));

        Result<DailySeries> result = await provider.GetDailyAsync(Daily("XNGS", "NVDA", from: new DateOnly(2024, 6, 19), to: new DateOnly(2024, 6, 19)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a holiday or a weekend has no bars, and that must not park the job as a permanent failure");
        result.Value.Bars.Should().BeEmpty();
        result.Value.Rejected.Should().Be(0);
        result.Value.Truncated.Should().BeFalse();
    }

    [Theory]
    [InlineData(5000, true)]
    [InlineData(4999, false)]
    [InlineData(1, false)]
    public async Task GetDaily_AsManyRowsAsOneRequestHolds_IsFlaggedAsPossiblyTruncated(int rows, bool truncated)
    {
        var day = new DateOnly(2000, 1, 3);
        string values = string.Join(",", Enumerable.Range(0, rows).Select(i =>
            $"{{\"datetime\":\"{day.AddDays(i):yyyy-MM-dd}\",\"open\":\"10\",\"high\":\"11\",\"low\":\"9\",\"close\":\"10\",\"volume\":\"1\"}}"));
        (TwelveDataProvider provider, _) = Build(_ => Json($"{{\"meta\":{{}},\"values\":[{values}],\"status\":\"ok\"}}"));

        DailySeries series = (await provider.GetDailyAsync(Daily(), CancellationToken.None)).Value;

        series.Bars.Should().HaveCount(rows);
        series.Truncated.Should().Be(truncated, "a full page means the newest days may be missing");
    }

    [Fact]
    public async Task GetDaily_DirtyRows_AreDroppedAndCounted_NeverRepaired()
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("time_series_dirty.synthetic.json")));

        DailySeries series = (await provider.GetDailyAsync(Daily("XNGS", "TST"), CancellationToken.None)).Value;

        // Valid: 06-03, 06-11 and 06-12 (the later row of a duplicated date wins). Refused: inverted range, text price, missing volume,
        // intraday timestamp, negative volume and a row that is not an object.
        series.Bars.Select(b => b.Date).Should().Equal(new DateOnly(2024, 6, 3), new DateOnly(2024, 6, 11), new DateOnly(2024, 6, 12));
        series.Rejected.Should().Be(6);
        series.Bars[^1].Close.Should().Be(13.0m);
    }

    [Fact]
    public async Task Search_Shop_ReturnsOneListingPerMarket_WithTheProvidersOwnCodes()
    {
        (TwelveDataProvider provider, StubHandler handler) = Build(_ => Json(Fixture("symbol_search_shop.json")));

        IReadOnlyList<ProviderListing> listings = (await provider.SearchAsync(" SHOP ", CancellationToken.None)).Value;

        handler.Requests.Single().RequestUri!.OriginalString.Should().Contain("symbol=SHOP").And.Contain("outputsize=30");
        listings.Select(l => (l.Key.ProviderMic, l.Currency)).Should().Equal(("XNGS", "USD"), ("XTSE", "CAD"), ("XBUE", "ARS"), ("NEOE", "CAD"), ("XWBO", "EUR"));
        listings[0].Name.Should().Be("Shopify Inc.");
        listings[0].TimeZoneId.Should().Be("America/New_York");
        listings[0].AssetType.Should().Be(AssetType.Equity);
        listings[2].AssetType.Should().Be(AssetType.Other, "a depositary receipt is not an equity listing");
    }

    [Fact]
    public async Task Search_AResultThatTheDomainCanTurnIntoAStock_IsValid()
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("symbol_search_shop.json")));
        ProviderListing listing = (await provider.SearchAsync("SHOP", CancellationToken.None)).Value[1];

        Result<Stock> stock = Stock.Create(StockId.Create(Guid.CreateVersion7()), listing.ToNewListing("XTSE"), DateTimeOffset.UnixEpoch);

        stock.IsSuccess.Should().BeTrue();
        stock.Value.Currency.Value.Should().Be("CAD");
    }

    [Fact]
    public async Task APlanRestriction_IsToldApartFromAnUnknownSymbol_EvenThoughBothAreHttp404()
    {
        (TwelveDataProvider restricted, _) = Build(_ => Json(Fixture("error_plan_restricted.json"), HttpStatusCode.NotFound));
        (TwelveDataProvider unknown, _) = Build(_ => Json(Fixture("error_invalid_symbol.json"), HttpStatusCode.NotFound));

        Result<DailySeries> plan = await restricted.GetDailyAsync(Daily("XTSE", "SHOP"), CancellationToken.None);
        Result<DailySeries> missing = await unknown.GetDailyAsync(Daily("XNGS", "NOPE"), CancellationToken.None);

        ProviderErrors.Classify(plan.Error!).Should().Be(ProviderFailureKind.PlanRestricted);
        ProviderErrors.Classify(missing.Error!).Should().Be(ProviderFailureKind.SymbolNotFound);
    }

    [Theory]
    [InlineData(400, "**symbol** or **figi** parameter is missing or invalid.")]
    [InlineData(400, "The symbol was not found.")]
    [InlineData(422, "symbol not found")]
    public async Task AnAnswerThatOnlyMentionsAMissingSymbol_IsNotAnUnknownSymbol_BecauseOnlyA404Counts(int status, string message)
    {
        (TwelveDataProvider provider, _) = Build(_ => Json($$"""{"code":{{status}},"message":"{{message}}","status":"error"}""", (HttpStatusCode)status));

        Result<DailySeries> result = await provider.GetDailyAsync(Daily("XNGS", "AAPL"), CancellationToken.None);

        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.Permanent, "counting it towards delisting would let a changed message delist a market");
    }

    [Fact]
    public async Task ARateLimit_IsClassified_AndCarriesTheWaitTheProviderSent()
    {
        (TwelveDataProvider provider, _) = Build(_ =>
        {
            HttpResponseMessage response = Json(Fixture("error_rate_limited.json"), HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        Result<DailySeries> result = await provider.GetDailyAsync(Daily(), CancellationToken.None);

        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.RateLimited);
        ProviderErrors.RetryAfter(result.Error!).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheRealRateLimit_HasNoRetryAfterHeader_SoTheWaitIsTheRestOfTheMinute()
    {
        // The recorded 429 has no Retry-After; the fixed clock says 21:00:00, so a full minute (plus a second of margin) remains.
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("error_rate_limited.json"), HttpStatusCode.TooManyRequests));

        Result<DailySeries> result = await provider.GetDailyAsync(Daily(), CancellationToken.None);

        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.RateLimited);
        ProviderErrors.RetryAfter(result.Error!).Should().Be(TimeSpan.FromSeconds(61));
    }

    [Fact]
    public async Task ARateLimitReportedInsideAnHttp200Body_IsStillARateLimit()
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("error_rate_limited.json")));

        Result<DailySeries> result = await provider.GetDailyAsync(Daily(), CancellationToken.None);

        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.RateLimited);
    }

    [Fact]
    public async Task ARefusedKey_IsAPermanentFailure_WithoutEchoingTheProvidersText()
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("error_unauthorized.json"), HttpStatusCode.Unauthorized));

        Result<DailySeries> result = await provider.GetDailyAsync(Daily(), CancellationToken.None);

        result.Error!.Code.Should().Be(ProviderErrors.UnauthorizedCode);
        ProviderErrors.Classify(result.Error).Should().Be(ProviderFailureKind.Permanent);
        result.Error.Message.Should().NotContain("apikey").And.NotContain("parameter");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task AServerError_IsTransient(HttpStatusCode status)
    {
        (TwelveDataProvider provider, _) = Build(_ => Json("<html>oops</html>", status));

        Result<DailySeries> result = await provider.GetDailyAsync(Daily(), CancellationToken.None);

        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.Transient);
    }

    [Fact]
    public async Task ATransportFailure_IsTransient_AndItsMessageIsNotCopied()
    {
        (TwelveDataProvider provider, _) = Build(_ => throw new HttpRequestException("Connection refused to https://api.twelvedata.com/?apikey=secret"));

        Result<DailySeries> result = await provider.GetDailyAsync(Daily(), CancellationToken.None);

        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.Transient);
        result.Error!.Message.Should().NotContain("secret").And.NotContain("refused");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"meta\":{}}")]
    [InlineData("{\"values\":\"nope\"}")]
    [InlineData("[]")]
    public async Task AResponseThatIsNotTheContract_IsInvalid(string body)
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(body));

        Result<DailySeries> result = await provider.GetDailyAsync(Daily(), CancellationToken.None);

        result.Error!.Code.Should().Be(ProviderErrors.InvalidResponseCode);
    }

    [Fact]
    public async Task ACancelledCall_Propagates_InsteadOfBecomingAProviderFailure()
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("time_series_spy_etf.json")));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Func<Task> act = () => provider.GetDailyAsync(Daily(), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ATimeSeriesCall_SpendsOneCreditOfTheConfiguredBudget_BeforeItIsSent()
    {
        var budget = new ScriptedBudget();
        (TwelveDataProvider provider, StubHandler handler) = Build(_ => Json(Fixture("time_series_spy_etf.json")), budget);

        await provider.GetDailyAsync(Daily(), CancellationToken.None);

        budget.Spent.Should().ContainSingle().Which.Should().Be(("twelvedata", new BudgetLimits(8, 800), 1));
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task AnOnDemandCall_SpendsTheAllowanceOfItsOwnFirst_AndThenThePlansBudget()
    {
        var budget = new ScriptedBudget();
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("time_series_spy_etf.json")), budget);

        await provider.GetDailyAsync(Daily() with { Lane = CallLane.OnDemand }, CancellationToken.None);

        budget.Spent.Should().Equal(
            ("twelvedata_ondemand", new BudgetLimits(8, 200), 1),
            ("twelvedata", new BudgetLimits(8, 800), 1));
    }

    [Fact]
    public async Task AScheduledCall_NeverTouchesTheOnDemandAllowance()
    {
        var budget = new ScriptedBudget();
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("time_series_spy_etf.json")), budget);

        await provider.GetDailyAsync(Daily() with { Lane = CallLane.Scheduled }, CancellationToken.None);

        budget.Spent.Select(spent => spent.Provider).Should().Equal("twelvedata");
    }

    [Fact]
    public async Task AnOnDemandCall_WhenTheAllowanceIsSpent_IsNeverSent_AndLeavesThePlansBudgetAlone()
    {
        var budget = new ScriptedBudget(new BudgetDecision(false, TimeSpan.FromHours(3)));
        (TwelveDataProvider provider, StubHandler handler) = Build(_ => Json(Fixture("time_series_spy_etf.json")), budget);

        Result<DailySeries> result = await provider.GetDailyAsync(Daily() with { Lane = CallLane.OnDemand }, CancellationToken.None);

        handler.Requests.Should().BeEmpty();
        budget.Spent.Select(spent => spent.Provider).Should().Equal("twelvedata_ondemand");
        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.RateLimited);
        ProviderErrors.RetryAfter(result.Error!).Should().Be(TimeSpan.FromHours(3), "the job waits for the allowance of the next day and does not burn its attempts");
    }

    [Fact]
    public async Task ASearch_ThatMetAServerError_IsTriedOnceMore_BecauseItCostsNoCredit()
    {
        int calls = 0;
        (TwelveDataProvider provider, StubHandler handler) = Build(_ => ++calls == 1 ? Json("{}", HttpStatusCode.ServiceUnavailable) : Json(Fixture("symbol_search_shop.json")));

        Result<IReadOnlyList<ProviderListing>> result = await provider.SearchAsync("AAPL", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ASearch_ThatFailsTwice_IsUnavailable_AndATimeSeriesIsNeverRetried()
    {
        (TwelveDataProvider search, StubHandler searches) = Build(_ => Json("{}", HttpStatusCode.ServiceUnavailable));
        (TwelveDataProvider series, StubHandler fetches) = Build(_ => Json("{}", HttpStatusCode.ServiceUnavailable));

        Result<IReadOnlyList<ProviderListing>> found = await search.SearchAsync("AAPL", CancellationToken.None);
        Result<DailySeries> daily = await series.GetDailyAsync(Daily(), CancellationToken.None);

        ProviderErrors.Classify(found.Error!).Should().Be(ProviderFailureKind.Transient);
        searches.Requests.Should().HaveCount(2);
        ProviderErrors.Classify(daily.Error!).Should().Be(ProviderFailureKind.Transient);
        fetches.Requests.Should().ContainSingle("a retried time_series would spend a credit that the budget never counted");
    }

    [Fact]
    public async Task ReservingTheTwoSeriesOfAListing_SpendsTwoCredits_Once_AndThePrepaidCallsSpendNone()
    {
        var budget = new ScriptedBudget();
        (TwelveDataProvider provider, StubHandler handler) = Build(_ => Json(Fixture("time_series_spy_etf.json")), budget);

        Error? refused = await provider.ReserveDailyAsync(2, CallLane.Scheduled, CancellationToken.None);
        await provider.GetDailyAsync(Daily() with { Prepaid = true }, CancellationToken.None);
        await provider.GetDailyAsync(Daily(adjusted: false) with { Prepaid = true }, CancellationToken.None);

        refused.Should().BeNull();
        budget.Spent.Should().Equal(("twelvedata", new BudgetLimits(8, 800), 2));
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task WhenThePlansBudgetHasRoomForOneSeriesAndNotTwo_NeitherIsSent_AndNothingIsSpent()
    {
        var budget = new ScriptedBudget(new BudgetDecision(false, TimeSpan.FromSeconds(30)));
        (TwelveDataProvider provider, StubHandler handler) = Build(_ => Json(Fixture("time_series_spy_etf.json")), budget);

        Error? refused = await provider.ReserveDailyAsync(2, CallLane.Scheduled, CancellationToken.None);

        ProviderErrors.Classify(refused!).Should().Be(ProviderFailureKind.RateLimited);
        handler.Requests.Should().BeEmpty();
        budget.Spent.Should().ContainSingle().Which.Credits.Should().Be(2, "one request for both, not two for one each");
    }

    [Fact]
    public async Task ReleasingReservedRequests_GivesBackThePlansCredits_AndTheOnDemandAllowanceToo()
    {
        var budget = new ScriptedBudget();
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("time_series_spy_etf.json")), budget);

        await provider.ReleaseDailyAsync(1, CallLane.Scheduled, CancellationToken.None);
        await provider.ReleaseDailyAsync(1, CallLane.OnDemand, CancellationToken.None);

        budget.Released.Should().Equal(("twelvedata", 1), ("twelvedata", 1), ("twelvedata_ondemand", 1));
    }

    [Fact]
    public async Task AnOnDemandCall_WhenThePlansBudgetRefuses_GivesTheAllowanceBack()
    {
        var budget = new ScriptedBudget(BudgetDecision.Allowed, new BudgetDecision(false, TimeSpan.FromSeconds(30)));
        (TwelveDataProvider provider, StubHandler handler) = Build(_ => Json(Fixture("time_series_spy_etf.json")), budget);

        Result<DailySeries> result = await provider.GetDailyAsync(Daily() with { Lane = CallLane.OnDemand }, CancellationToken.None);

        handler.Requests.Should().BeEmpty();
        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.RateLimited);
        budget.Released.Should().Equal(("twelvedata_ondemand", 1));
    }

    [Fact]
    public async Task AScheduledCall_ThatThePlansBudgetRefuses_ReleasesNothing()
    {
        var budget = new ScriptedBudget(new BudgetDecision(false, TimeSpan.FromSeconds(30)));
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("time_series_spy_etf.json")), budget);

        await provider.GetDailyAsync(Daily() with { Lane = CallLane.Scheduled }, CancellationToken.None);

        budget.Released.Should().BeEmpty();
    }

    [Fact]
    public void TheClient_IsNotRetriedByTheHttpPipeline_BecauseARetryCostsACreditTheBudgetNeverCounted()
    {
        var http = new SakerWatch.BuildingBlocks.Resilience.HttpClientResilienceOptions();

        TwelveDataProvider.ConfigureClient(http, new Uri("https://api.twelvedata.com/"));

        http.MaxRetries.Should().Be(0);
        http.AllowedHosts.Should().Equal("api.twelvedata.com");
        http.RateLimitIsTransient.Should().BeFalse();
        http.MaxResponseBytes.Should().Be(4 * 1024 * 1024);
    }

    [Theory]
    [InlineData(200, 800, true)]
    [InlineData(800, 800, true)]
    [InlineData(0, 800, true)]
    [InlineData(801, 800, false)]
    public void Options_TheOnDemandAllowanceIsAShareOfTheDay(int onDemand, int perDay, bool valid)
    {
        var options = new TwelveDataOptions { ApiKey = _key, OnDemandCreditsPerDay = onDemand, CreditsPerDay = perDay };

        new TwelveDataOptionsValidator(Options.Create(new MarketDataProvidersOptions())).Validate(null, options).Succeeded.Should().Be(valid);
    }

    [Theory]
    [InlineData(1, 800, 200, false)]
    [InlineData(2, 800, 200, true)]
    [InlineData(7, 800, 200, true)]
    [InlineData(8, 1, 0, false)]
    [InlineData(8, 2, 2, true)]
    [InlineData(8, 800, 1, false)]
    public void Options_ABudgetThatCannotPayAHistoryIsRefused(int perMinute, int perDay, int onDemand, bool valid)
    {
        var options = new TwelveDataOptions { ApiKey = _key, CreditsPerMinute = perMinute, CreditsPerDay = perDay, OnDemandCreditsPerDay = onDemand };

        // The range attributes are enforced by the options binder (ValidateDataAnnotations); here both layers are checked together.
        bool annotations = Validator.TryValidateObject(options, new ValidationContext(options), null, validateAllProperties: true);
        bool custom = new TwelveDataOptionsValidator(Options.Create(new MarketDataProvidersOptions())).Validate(null, options).Succeeded;

        (annotations && custom).Should().Be(valid);
    }

    [Theory]
    [InlineData(250, 800, 200, true)]   // the defaults: 500 credits of refresh against 600
    [InlineData(300, 800, 200, true)]   // exactly what the free plan carries
    [InlineData(301, 800, 200, false)]
    [InlineData(500, 800, 200, false)]   // the spec's 500 stocks need 1,000 credits a day
    [InlineData(500, 1200, 200, true)]   // a plan with more credits carries them
    [InlineData(500, 100000, 0, true)]   // a plan with no daily limit
    public void Options_TheCatalogCapMustFitTheCreditsThatRefreshIt(int cap, int perDay, int onDemand, bool valid)
    {
        var options = new TwelveDataOptions { ApiKey = _key, CreditsPerDay = perDay, OnDemandCreditsPerDay = onDemand };
        var routed = new MarketDataProvidersOptions { Markets = new Dictionary<string, string> { ["XNAS"] = TwelveDataOptions.ProviderName } };

        ValidateOptionsResult result = new TwelveDataOptionsValidator(Options.Create(routed), Options.Create(new StockLookupOptions { MaxCatalogListings = cap })).Validate(null, options);

        result.Succeeded.Should().Be(valid);
        if (!valid)
        {
            string.Join(' ', result.Failures!).Should().Contain("MaxCatalogListings").And.Contain("ADR-0051");
        }
    }

    [Theory]
    [InlineData("250", true)]
    [InlineData("500", false)]
    public void TheRegisteredHost_StopsAtStartWhenTheCapCannotBeRefreshed(string cap, bool starts)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TwelveData:ApiKey"] = _key,
            ["MarketData:Providers:Markets:XNAS"] = "twelvedata",
            ["MarketData:Lookup:MaxCatalogListings"] = cap,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMarketData(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Func<TwelveDataOptions> read = () => provider.GetRequiredService<IOptions<TwelveDataOptions>>().Value;

        if (starts)
        {
            read.Should().NotThrow();
        }
        else
        {
            read.Should().Throw<OptionsValidationException>().WithMessage("*MaxCatalogListings*");
        }
    }

    [Fact]
    public void Options_TheCatalogCapIsNotCheckedAgainstCreditsAHostDoesNotUse()
    {
        var options = new TwelveDataOptions { ApiKey = _key, CreditsPerDay = 800, OnDemandCreditsPerDay = 200 };

        new TwelveDataOptionsValidator(Options.Create(new MarketDataProvidersOptions()), Options.Create(new StockLookupOptions { MaxCatalogListings = 5000 })).Validate(null, options).Succeeded
            .Should().BeTrue("no market is routed to Twelve Data, so its credits carry nothing");
    }

    [Fact]
    public async Task AFailedTimeSeriesCall_StillSpendsItsCredit_BecauseTheProviderChargesIt()
    {
        var budget = new ScriptedBudget();
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("error_invalid_symbol.json"), HttpStatusCode.NotFound), budget);

        await provider.GetDailyAsync(Daily(symbol: "NOPE"), CancellationToken.None);

        budget.Spent.Should().ContainSingle().Which.Credits.Should().Be(1);
    }

    [Fact]
    public async Task ASymbolSearch_CostsNoCredit_SoItNeverTouchesTheBudget()
    {
        var budget = new ScriptedBudget(new BudgetDecision(false, TimeSpan.FromMinutes(5)));
        (TwelveDataProvider provider, StubHandler handler) = Build(_ => Json(Fixture("symbol_search_shop.json")), budget);

        Result<IReadOnlyList<ProviderListing>> result = await provider.SearchAsync("SHOP", CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a search is free, so an exhausted budget does not stop it");
        budget.Spent.Should().BeEmpty();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ADeniedCall_IsNeverSent_AndSaysHowLongToWait()
    {
        (TwelveDataProvider provider, StubHandler handler) = Build(
            _ => Json(Fixture("time_series_spy_etf.json")),
            new ScriptedBudget(new BudgetDecision(false, TimeSpan.FromSeconds(41))));

        Result<DailySeries> result = await provider.GetDailyAsync(Daily(), CancellationToken.None);

        handler.Requests.Should().BeEmpty("a call without budget would only earn a 429");
        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.RateLimited);
        ProviderErrors.RetryAfter(result.Error!).Should().Be(TimeSpan.FromSeconds(41));
    }

    [Fact]
    public async Task ASuccessfulResponse_IsKeptExactlyAsReceived_WithWhatWasAsked()
    {
        var store = new RecordingStore();
        string body = Fixture("time_series_nvda_split.json");
        (TwelveDataProvider provider, _) = Build(_ => Json(body), payloads: store);

        await provider.GetDailyAsync(Daily("XNGS", "NVDA", from: new DateOnly(2024, 6, 1), to: new DateOnly(2024, 6, 20)), CancellationToken.None);

        ProviderPayload kept = store.Stored.Single();
        kept.Body.Should().Be(body, "the raw text is stored, not a re-serialization");
        kept.Provider.Should().Be("twelvedata");
        kept.Operation.Should().Be("daily");
        kept.Key.Should().Be(new ProviderListingKey("XNGS", "NVDA"));
        kept.Query.Should().Be("2024-06-01..2024-06-20");
        kept.Adjusted.Should().BeTrue();
        kept.FetchedAt.Should().Be(_now);
    }

    [Fact]
    public async Task ASearchResponse_IsKeptToo()
    {
        var store = new RecordingStore();
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("symbol_search_shop.json")), payloads: store);

        await provider.SearchAsync(" SHOP ", CancellationToken.None);

        store.Stored.Single().Should().Match<ProviderPayload>(p => p.Operation == "search" && p.Query == "SHOP" && p.Key == null && p.Adjusted == null);
    }

    [Fact]
    public async Task AFailedCall_KeepsNothing()
    {
        var store = new RecordingStore();
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("error_plan_restricted.json"), HttpStatusCode.NotFound), payloads: store);

        await provider.GetDailyAsync(Daily("XTSE", "SHOP"), CancellationToken.None);

        store.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task AStoreThatFails_DoesNotFailTheFetch()
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("time_series_spy_etf.json")), payloads: new RecordingStore(fail: true));

        Result<DailySeries> result = await provider.GetDailyAsync(Daily("ARCX", "SPY"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the data was fetched and parsed; only keeping the raw copy failed");
        result.Value.Bars.Should().NotBeEmpty();
    }

    [Fact]
    public async Task EveryCall_IsCountedByOutcome()
    {
        var seen = new List<(string Operation, string Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "sakerwatch_provider_calls_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? op = null, outcome = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "operation")
                {
                    op = (string?)tag.Value;
                }
                else if (tag.Key == "outcome")
                {
                    outcome = (string?)tag.Value;
                }
            }

            lock (seen)
            {
                seen.Add((op!, outcome!));
            }
        });
        listener.Start();

        (TwelveDataProvider ok, _) = Build(_ => Json(Fixture("time_series_spy_etf.json")));
        (TwelveDataProvider limited, _) = Build(_ => Json(Fixture("error_rate_limited.json"), HttpStatusCode.TooManyRequests));
        await ok.GetDailyAsync(Daily(), CancellationToken.None);
        await limited.GetDailyAsync(Daily(), CancellationToken.None);

        lock (seen)
        {
            seen.Should().Contain(("daily", "success")).And.Contain(("daily", "ratelimited"));
        }
    }
}
