using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Polly.Timeout;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Infrastructure.Providers.Banguat;

namespace SakerWatch.MarketData.Tests;

/// <summary>
/// The Banco de Guatemala adapter against responses recorded from the real service on 2026-10-17 (files named after what they show) or written by
/// hand (<c>.synthetic</c> inside the test). Nothing here calls the network.
/// </summary>
public sealed class BanguatFxProviderTests
{
    private static readonly CurrencyCode _usd = CurrencyCode.Parse("USD");
    private static readonly CurrencyCode _gtq = CurrencyCode.Parse("GTQ");
    private static readonly DateOnly _from = new(2026, 10, 10);
    private static readonly DateOnly _to = new(2026, 10, 17);
    private static readonly DateTimeOffset _now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://www.banguat.gob.gt/variables/ws/TipoCambio.asmx") };
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

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "banguat", name));

    private static HttpResponseMessage Xml(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };

    private static (BanguatFxProvider Provider, StubHandler Handler, RecordingStore Payloads) Build(Func<HttpRequestMessage, HttpResponseMessage> respond, bool failStore = false)
    {
        var handler = new StubHandler(respond);
        var payloads = new RecordingStore(failStore);
        return (new BanguatFxProvider(new SingleClientFactory(handler), payloads, new FakeTimeProvider(_now), NullLogger<BanguatFxProvider>.Instance), handler, payloads);
    }

    private static string Answer(params string[] rows) =>
        $"""<?xml version="1.0" encoding="utf-8"?><soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body><TipoCambioRangoResponse xmlns="http://www.banguat.gob.gt/variables/ws/"><TipoCambioRangoResult><Vars>{string.Concat(rows)}</Vars><TotalItems>{rows.Length}</TotalItems></TipoCambioRangoResult></TipoCambioRangoResponse></soap:Body></soap:Envelope>""";

    private static string Row(string currency, string date, string rate) =>
        $"<Var><moneda>{currency}</moneda><fecha>{date}</fecha><venta>{rate}</venta><compra>{rate}</compra></Var>";

    private static Task<Result<IReadOnlyList<FxRate>>> AskAsync(BanguatFxProvider provider) =>
        provider.GetDailyAsync(_usd, _gtq, _from, _to, CancellationToken.None);

    [Fact]
    public async Task GetDaily_ARealWeek_ReturnsOneExactRatePerDay_WeekendsIncluded()
    {
        (BanguatFxProvider provider, _, _) = Build(_ => Xml(Fixture("range-week.xml")));

        Result<IReadOnlyList<FxRate>> result = await AskAsync(provider);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(r => (r.AsOfDate.Day, r.Rate)).Should().Equal(
            (10, 7.64157m), (11, 7.64157m), (12, 7.64157m), (13, 7.63381m), (14, 7.63988m), (15, 7.6414m), (16, 7.64572m), (17, 7.64572m));
        result.Value.Should().OnlyContain(r => r.Base == _usd && r.Quote == _gtq && r.Source == "banguat");
    }

    [Fact]
    public async Task GetDaily_SendsASoapRequestWithTheDatesTheServiceExpects()
    {
        (BanguatFxProvider provider, StubHandler handler, _) = Build(_ => Xml(Fixture("range-week.xml")));

        await AskAsync(provider);

        (HttpRequestMessage request, string body) = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Headers.GetValues("SOAPAction").Should().Equal("\"http://www.banguat.gob.gt/variables/ws/TipoCambioRango\"");
        request.Content!.Headers.ContentType!.MediaType.Should().Be("text/xml");
        body.Should().Contain("<fechainit>10/10/2026</fechainit>").And.Contain("<fechafin>17/10/2026</fechafin>");
    }

    [Fact]
    public async Task GetDaily_KeepsTheRawAnswerForLineage()
    {
        (BanguatFxProvider provider, _, RecordingStore payloads) = Build(_ => Xml(Fixture("range-week.xml")));

        await AskAsync(provider);

        ProviderPayload stored = payloads.Stored.Should().ContainSingle().Subject;
        (stored.Provider, stored.Operation, stored.Key, stored.FetchedAt).Should().Be(("banguat", "fx_daily", null, _now));
        stored.Body.Should().Be(Fixture("range-week.xml"));
    }

    [Fact]
    public async Task GetDaily_WhenTheRawAnswerCannotBeKept_StillReturnsTheRates()
    {
        (BanguatFxProvider provider, _, _) = Build(_ => Xml(Fixture("range-week.xml")), failStore: true);

        (await AskAsync(provider)).Value.Should().HaveCount(8);
    }

    [Fact]
    public async Task GetDaily_AnEmptyAnswer_IsAnEmptyListNotAnError()
    {
        (BanguatFxProvider provider, _, _) = Build(_ => Xml(Fixture("range-empty.xml")));

        Result<IReadOnlyList<FxRate>> result = await AskAsync(provider);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDaily_ARateAfterTheRange_IsLeftOut()
    {
        // The bank publishes tomorrow's rate ahead of time (recorded: 20/10/2026 answered on the 17th); ingestion never stores a future date.
        (BanguatFxProvider provider, _, _) = Build(_ => Xml(Fixture("range-future.xml")));

        (await AskAsync(provider)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDaily_RowsThatAreNotAUsdRate_AreDroppedAndTheRestIsKept()
    {
        string body = Answer(
            Row("2", "15/10/2026", "7.6414"),
            Row("7", "15/10/2026", "1.4188"),
            Row("2", "2026-10-16", "7.64572"),
            Row("2", "16/10/2026", "seven"),
            Row("2", "16/10/2026", "-1"),
            Row("2", "16/10/2026", "0"),
            Row("2", "17/10/2026", "7.64572"));
        (BanguatFxProvider provider, _, _) = Build(_ => Xml(body));

        Result<IReadOnlyList<FxRate>> result = await AskAsync(provider);

        result.Value.Select(r => r.AsOfDate.Day).Should().Equal(15, 17);
    }

    [Fact]
    public async Task GetDaily_ARepeatedDay_KeepsTheLastRow()
    {
        (BanguatFxProvider provider, _, _) = Build(_ => Xml(Answer(Row("2", "15/10/2026", "7.6"), Row("2", "15/10/2026", "7.7"))));

        (await AskAsync(provider)).Value.Should().ContainSingle().Which.Rate.Should().Be(7.7m);
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("")]
    [InlineData("<html><body>maintenance</body></html>")]
    [InlineData("""<?xml version="1.0"?><soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body><Other/></soap:Body></soap:Envelope>""")]
    public async Task GetDaily_AnAnswerThatIsNotTheContract_IsInvalid(string body)
    {
        (BanguatFxProvider provider, _, _) = Build(_ => Xml(body));

        Result<IReadOnlyList<FxRate>> result = await AskAsync(provider);

        result.Error!.Code.Should().Be(ProviderErrors.InvalidResponseCode);
    }

    [Fact]
    public async Task GetDaily_AnAnswerWithADtd_IsRefusedAndNothingIsResolved()
    {
        const string hostile = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body>&e;</soap:Body></soap:Envelope>""";
        (BanguatFxProvider provider, _, _) = Build(_ => Xml(hostile));

        Result<IReadOnlyList<FxRate>> result = await AskAsync(provider);

        result.Error!.Code.Should().Be(ProviderErrors.InvalidResponseCode);
    }

    [Fact]
    public async Task GetDaily_ASoapFaultFromTheClientSide_IsPermanent()
    {
        (BanguatFxProvider provider, _, _) = Build(_ => Xml(Fixture("fault-client.xml"), HttpStatusCode.InternalServerError));

        Result<IReadOnlyList<FxRate>> result = await AskAsync(provider);

        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.Permanent);
        result.Error!.Message.Should().NotContain("SOAPAction", "the body of an error is classified, never copied into a message");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetDaily_AServerFailureWithoutAFault_IsTransient(HttpStatusCode status)
    {
        (BanguatFxProvider provider, _, _) = Build(_ => Xml("<html>down</html>", status));

        ProviderErrors.Classify((await AskAsync(provider)).Error!).Should().Be(ProviderFailureKind.Transient);
    }

    [Fact]
    public async Task GetDaily_ARateLimit_CarriesTheWaitTheServiceAsked()
    {
        (BanguatFxProvider provider, _, _) = Build(_ =>
        {
            HttpResponseMessage response = Xml("slow down", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return response;
        });

        Error error = (await AskAsync(provider)).Error!;

        ProviderErrors.Classify(error).Should().Be(ProviderFailureKind.RateLimited);
        ProviderErrors.RetryAfter(error).Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task GetDaily_ARefusedCredential_IsPermanent()
    {
        (BanguatFxProvider provider, _, _) = Build(_ => Xml("no", HttpStatusCode.Forbidden));

        (await AskAsync(provider)).Error!.Code.Should().Be(ProviderErrors.UnauthorizedCode);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutRejectedException))]
    public async Task GetDaily_ATransportFailure_IsTransient_AndTheMessageIsNotCopied(Type exceptionType)
    {
        (BanguatFxProvider provider, _, _) = Build(_ => throw (Exception)Activator.CreateInstance(exceptionType, "https://www.banguat.gob.gt/secret-path")!);

        Error error = (await AskAsync(provider)).Error!;

        ProviderErrors.Classify(error).Should().Be(ProviderFailureKind.Transient);
        error.Message.Should().NotContain("secret-path");
    }

    [Fact]
    public async Task GetDaily_WhenTheCallerCancels_ThrowsInsteadOfReportingAFailure()
    {
        (BanguatFxProvider provider, _, _) = Build(_ => Xml(Fixture("range-week.xml")));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Func<Task> act = () => provider.GetDailyAsync(_usd, _gtq, _from, _to, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("GTQ", "USD")]
    [InlineData("USD", "CAD")]
    [InlineData("EUR", "GTQ")]
    public async Task GetDaily_AnotherPair_IsRefusedWithoutCallingTheService(string baseCode, string quoteCode)
    {
        (BanguatFxProvider provider, StubHandler handler, _) = Build(_ => Xml(Fixture("range-week.xml")));

        Result<IReadOnlyList<FxRate>> result = await provider.GetDailyAsync(CurrencyCode.Parse(baseCode), CurrencyCode.Parse(quoteCode), _from, _to, CancellationToken.None);

        result.Error!.Code.Should().Be("marketdata.fx.pair_not_supported");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDaily_ARangeThatEndsBeforeItStarts_OrSpansTooLong_IsRefusedWithoutCallingTheService()
    {
        (BanguatFxProvider provider, StubHandler handler, _) = Build(_ => Xml(Fixture("range-week.xml")));

        Result<IReadOnlyList<FxRate>> reversed = await provider.GetDailyAsync(_usd, _gtq, _to, _from, CancellationToken.None);
        Result<IReadOnlyList<FxRate>> tooLong = await provider.GetDailyAsync(_usd, _gtq, new DateOnly(1990, 1, 1), _to, CancellationToken.None);

        reversed.Error!.Code.Should().Be("marketdata.fx.range_invalid");
        tooLong.Error!.Code.Should().Be("marketdata.fx.range_invalid");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDaily_CountsTheCallByOutcome()
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
            string? provider = null, operation = null, outcome = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                (tag.Key switch { "provider" => (Action)(() => provider = (string?)tag.Value), "operation" => () => operation = (string?)tag.Value, _ => () => outcome = (string?)tag.Value })();
            }

            if (provider == "banguat")
            {
                lock (seen)
                {
                    seen.Add((operation!, outcome!));
                }
            }
        });
        listener.Start();
        (BanguatFxProvider ok, _, _) = Build(_ => Xml(Fixture("range-week.xml")));
        (BanguatFxProvider down, _, _) = Build(_ => Xml("down", HttpStatusCode.ServiceUnavailable));

        await AskAsync(ok);
        await AskAsync(down);

        seen.Should().Contain(("fx_daily", "success")).And.Contain(("fx_daily", "transient"));
    }
}
