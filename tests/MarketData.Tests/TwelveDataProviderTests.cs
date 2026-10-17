using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Stocks;
using SakerWatch.MarketData.Infrastructure.Providers.TwelveData;

namespace SakerWatch.MarketData.Tests;

/// <summary>
/// The Twelve Data adapter against responses recorded from the real API on 2026-10-16 (files without a suffix), taken from its documentation
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

    private static (TwelveDataProvider Provider, StubHandler Handler) Build(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var provider = new TwelveDataProvider(
            new SingleClientFactory(handler),
            Options.Create(new TwelveDataOptions { ApiKey = _key }),
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

    [Fact]
    public async Task ARateLimit_IsClassified_AndCarriesTheWaitTheProviderSent()
    {
        (TwelveDataProvider provider, _) = Build(_ =>
        {
            HttpResponseMessage response = Json(Fixture("error_rate_limited.docs.json"), HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        Result<DailySeries> result = await provider.GetDailyAsync(Daily(), CancellationToken.None);

        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.RateLimited);
        ProviderErrors.RetryAfter(result.Error!).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ARateLimitReportedInsideAnHttp200Body_IsStillARateLimit()
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("error_rate_limited.docs.json")));

        Result<DailySeries> result = await provider.GetDailyAsync(Daily(), CancellationToken.None);

        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.RateLimited);
    }

    [Fact]
    public async Task ARefusedKey_IsAPermanentFailure_WithoutEchoingTheProvidersText()
    {
        (TwelveDataProvider provider, _) = Build(_ => Json(Fixture("error_unauthorized.docs.json"), HttpStatusCode.Unauthorized));

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
        (TwelveDataProvider limited, _) = Build(_ => Json(Fixture("error_rate_limited.docs.json"), HttpStatusCode.TooManyRequests));
        await ok.GetDailyAsync(Daily(), CancellationToken.None);
        await limited.GetDailyAsync(Daily(), CancellationToken.None);

        lock (seen)
        {
            seen.Should().Contain(("daily", "success")).And.Contain(("daily", "ratelimited"));
        }
    }
}
