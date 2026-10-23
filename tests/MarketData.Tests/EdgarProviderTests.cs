using System.Diagnostics.Metrics;
using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Polly.Timeout;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Companies;
using SakerWatch.MarketData.Infrastructure.Providers.Edgar;

namespace SakerWatch.MarketData.Tests;

/// <summary>
/// The EDGAR adapter against fixtures that have the shape of the registry's answers, written by hand (<c>.synthetic</c>): the shape was checked once
/// against the real service with the live check (assumption A-028, 2026-10-20), and these files keep it. Nothing here
/// calls the network.
/// </summary>
public sealed class EdgarProviderTests
{
    private const string _agent = "SakerWatchTests tests@example.com";
    private static readonly DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Cik _apple = Cik.Parse("320193");
    private static readonly string[] _essentialColumns = ["accessionNumber", "form", "filingDate"];

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

    private static HttpResponseMessage Serve(HttpRequestMessage request, string submissions = "submissions-aapl.synthetic.json") => request.RequestUri!.AbsolutePath switch
    {
        "/files/company_tickers.json" => EdgarFixtures.Json(EdgarFixtures.Read("company-tickers.synthetic.json")),
        "/submissions/CIK0000320193.json" => EdgarFixtures.Json(EdgarFixtures.Read(submissions)),
        _ => EdgarFixtures.Json("{}", HttpStatusCode.NotFound),
    };

    // The map of tickers from the fixture and, for any other address, the submissions given.
    private static Func<HttpRequestMessage, HttpResponseMessage> WithSubmissions(string submissions) => request =>
        request.RequestUri!.AbsolutePath == "/files/company_tickers.json" ? EdgarFixtures.Json(EdgarFixtures.Read("company-tickers.synthetic.json")) : EdgarFixtures.Json(submissions);

    // A fake clock that says when something started waiting on it, so a test advances time only once the wait is armed and not after a guess.
    private sealed class SignallingClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            TimerCreated.TrySetResult();
            return timer;
        }
    }

    private static (EdgarProvider Provider, EdgarFixtures.StubHandler Handler, RecordingStore Payloads, FakeTimeProvider Clock) Build(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null, bool failStore = false, int intervalMilliseconds = 0, int mapHours = 24, FakeTimeProvider? clock = null)
    {
        var handler = new EdgarFixtures.StubHandler(respond ?? (request => Serve(request)));
        var payloads = new RecordingStore(failStore);
        clock ??= new FakeTimeProvider(_now);
        var options = Options.Create(new EdgarOptions { Enabled = true, UserAgent = _agent, MinRequestIntervalMilliseconds = intervalMilliseconds, TickerMapHours = mapHours });
        return (new EdgarProvider(new EdgarFixtures.SingleClientFactory(handler), payloads, options, clock, NullLogger<EdgarProvider>.Instance), handler, payloads, clock);
    }

    [Fact]
    public async Task TheProfile_IsReadFromTheSubmissionsOfTheKeyTheTickerMapsTo()
    {
        (EdgarProvider provider, EdgarFixtures.StubHandler handler, _, _) = Build();

        CompanyProfile profile = (await provider.GetProfileAsync("AAPL", CancellationToken.None)).Value;

        (profile.Cik, profile.Name, profile.Sic, profile.SicDescription, profile.FiscalYearEnd).Should().Be((_apple, "Apple Inc.", "3571", "Electronic Computers", "0926"));
        (profile.Tickers, profile.Exchanges).Should().Be((profile.Tickers, profile.Exchanges));
        profile.Tickers.Should().Equal("AAPL");
        profile.Exchanges.Should().Equal("Nasdaq");
        handler.Requests.Select(request => request.RequestUri!.AbsoluteUri).Should().Equal(
            "https://www.sec.gov/files/company_tickers.json", "https://data.sec.gov/submissions/CIK0000320193.json");
    }

    [Fact]
    public async Task EveryRequest_DeclaresWhoIsCalling_AndAsksForJson()
    {
        (EdgarProvider provider, EdgarFixtures.StubHandler handler, _, _) = Build();

        await provider.GetProfileAsync("AAPL", CancellationToken.None);
        await provider.GetFilingsAsync(new FilingsRequest(_apple, null, null, 10), CancellationToken.None);

        handler.Requests.Should().HaveCount(3).And.OnlyContain(request => request.Method == HttpMethod.Get);
        foreach (HttpRequestMessage request in handler.Requests)
        {
            request.Headers.GetValues("User-Agent").Should().Equal(_agent);
            request.Headers.Accept.Should().ContainSingle().Which.MediaType.Should().Be("application/json");
        }
    }

    [Fact]
    public async Task TheMapOfTickers_IsReadOnce_ThenKeptUntilItIsOld()
    {
        (EdgarProvider provider, EdgarFixtures.StubHandler handler, _, FakeTimeProvider clock) = Build(mapHours: 24);

        await provider.GetProfileAsync("AAPL", CancellationToken.None);
        await provider.GetProfileAsync("AAPL", CancellationToken.None);
        int before = handler.Requests.Count(request => request.RequestUri!.AbsolutePath == "/files/company_tickers.json");
        clock.Advance(TimeSpan.FromHours(25));
        await provider.GetProfileAsync("AAPL", CancellationToken.None);
        int after = handler.Requests.Count(request => request.RequestUri!.AbsolutePath == "/files/company_tickers.json");

        (before, after).Should().Be((1, 2));
    }

    [Fact]
    public async Task TheMapOfTickers_KeepsTheFirstRowOfARepeatedTicker_DropsTheRowsThatAreNotValid_AndFindsATickerWithADash()
    {
        (EdgarProvider provider, _, _, _) = Build(request => request.RequestUri!.AbsolutePath switch
        {
            "/files/company_tickers.json" => EdgarFixtures.Json(EdgarFixtures.Read("company-tickers.synthetic.json")),
            _ => EdgarFixtures.Json("""{ "cik": "0001067983", "name": "BERKSHIRE HATHAWAY INC", "tickers": ["BRK-B"], "exchanges": ["NYSE"] }"""),
        });

        Result<CompanyProfile> berkshire = await provider.GetProfileAsync("brk-b", CancellationToken.None);
        Result<CompanyProfile> negative = await provider.GetProfileAsync("NEGA", CancellationToken.None);
        Result<CompanyProfile> text = await provider.GetProfileAsync("TEXT", CancellationToken.None);
        Result<CompanyProfile> ibm = await provider.GetProfileAsync("IBM", CancellationToken.None);

        berkshire.Value.Cik.Should().Be(Cik.Parse("1067983"));
        (ProviderErrors.Classify(negative.Error!), ProviderErrors.Classify(text.Error!)).Should().Be((ProviderFailureKind.SymbolNotFound, ProviderFailureKind.SymbolNotFound));
        ibm.IsSuccess.Should().BeFalse("its submissions are not about its key");
    }

    [Fact]
    public async Task AnAnswerAboutAnotherFiler_IsNotTrusted()
    {
        (EdgarProvider provider, _, _, _) = Build(request => Serve(request, "submissions-other-key.synthetic.json"));

        Result<CompanyProfile> profile = await provider.GetProfileAsync("AAPL", CancellationToken.None);
        Result<IReadOnlyList<Filing>> filings = await provider.GetFilingsAsync(new FilingsRequest(_apple, null, null, 10), CancellationToken.None);

        (profile.Error!.Code, filings.Error!.Code).Should().Be((ProviderErrors.InvalidResponseCode, ProviderErrors.InvalidResponseCode));
    }

    [Fact]
    public async Task TheFilings_KeepOnlyTheRowsThatAreValid_CountTheOthers_AndBuildTheAddressFromCheckedParts()
    {
        (EdgarProvider provider, _, _, _) = Build();

        IReadOnlyList<Filing> filings = (await provider.GetFilingsAsync(new FilingsRequest(_apple, null, null, 100), CancellationToken.None)).Value;

        filings.Select(filing => (filing.AccessionNumber, filing.Form)).Should().Equal(
            ("0000320193-25-000079", "10-K"),
            ("0000320193-25-000073", "10-Q"),
            ("0000320193-25-000071", "8-K"),
            ("0000320193-24-000123", "10-K"),
            ("0000320193-24-000069", "10-Q"),
            ("0000320193-24-000068", "10-K/A"));
        filings[0].Should().Be(new Filing("0000320193-25-000079", "10-K", new DateOnly(2025, 10, 31), new DateOnly(2025, 9, 27), "aapl-20250927.htm", new Uri("https://www.sec.gov/Archives/edgar/data/320193/000032019325000079/aapl-20250927.htm")));
        filings[2].ReportDate.Should().BeNull("an empty report date is none, not a failure");
    }

    [Fact]
    public async Task ADocumentNameThatCouldEscapeItsFolder_IsNotUsed_AndTheFilingKeepsNoAddress()
    {
        const string body = """
            { "cik": "0000320193", "name": "Apple Inc.", "filings": { "recent": {
              "accessionNumber": ["0000320193-25-000001", "0000320193-25-000002", "0000320193-25-000003"],
              "filingDate": ["2025-01-01", "2025-01-02", "2025-01-03"],
              "reportDate": ["", "", ""],
              "form": ["8-K", "8-K", "8-K"],
              "primaryDocument": ["../../etc/passwd", "a b.htm", "https://evil.example/x.htm"] } } }
            """;
        (EdgarProvider provider, _, _, _) = Build(WithSubmissions(body));

        IReadOnlyList<Filing> filings = (await provider.GetFilingsAsync(new FilingsRequest(_apple, null, null, 100), CancellationToken.None)).Value;

        filings.Should().HaveCount(3).And.OnlyContain(filing => filing.PrimaryDocument == null && filing.Document == null);
    }

    [Fact]
    public async Task AFilingsAnswerWithoutTheRecentList_OrNotJson_OrWithAHostileName_IsInvalid()
    {
        (EdgarProvider hostile, _, _, _) = Build(request => Serve(request, "submissions-hostile.synthetic.json"));
        (await hostile.GetProfileAsync("AAPL", CancellationToken.None)).Error!.Code.Should().Be(ProviderErrors.InvalidResponseCode, "a name with markup is not a name");

        (EdgarProvider noProfileNeeded, _, _, _) = Build(request => Serve(request, "submissions-no-filings.synthetic.json"));
        (await noProfileNeeded.GetProfileAsync("AAPL", CancellationToken.None)).IsSuccess.Should().BeTrue("a profile does not need the list of filings");

        (EdgarProvider noFilings, _, _, _) = Build(request => Serve(request, "submissions-no-filings.synthetic.json"));
        (EdgarProvider notJson, _, _, _) = Build(request => request.RequestUri!.AbsolutePath == "/files/company_tickers.json" ? EdgarFixtures.Json("<html>oops</html>") : Serve(request));
        (await noFilings.GetFilingsAsync(new FilingsRequest(_apple, null, null, 10), CancellationToken.None)).Error!.Code.Should().Be(ProviderErrors.InvalidResponseCode);
        (await notJson.GetProfileAsync("AAPL", CancellationToken.None)).Error!.Code.Should().Be(ProviderErrors.InvalidResponseCode);
    }

    [Theory]
    [InlineData("accessionNumber")]
    [InlineData("form")]
    [InlineData("filingDate")]
    public async Task ARecentListMissingAnEssentialColumn_IsInvalid_NotAnEmptyList(string missing)
    {
        string columns = string.Join(
            ",",
            _essentialColumns.Where(column => column != missing).Select(column => $"\"{column}\": []"));
        (EdgarProvider provider, _, _, _) = Build(WithSubmissions($$"""{ "cik": "0000320193", "name": "Apple Inc.", "filings": { "recent": { {{columns}} } } }"""));

        Result<IReadOnlyList<Filing>> result = await provider.GetFilingsAsync(new FilingsRequest(_apple, null, null, 10), CancellationToken.None);

        result.Error!.Code.Should().Be(ProviderErrors.InvalidResponseCode);
    }

    [Fact]
    public async Task OddFieldsOfAProfile_AreDroppedOneByOne_AndTheProfileStillComes()
    {
        (EdgarProvider provider, _, _, _) = Build(request => Serve(request, "submissions-odd-profile.synthetic.json"));

        CompanyProfile profile = (await provider.GetProfileAsync("AAPL", CancellationToken.None)).Value;

        (profile.Name, profile.Sic, profile.SicDescription, profile.FiscalYearEnd).Should().Be(("Apple Inc.", null, null, null));
        profile.Tickers.Should().Equal("AAPL");
        profile.Exchanges.Should().Equal("Nasdaq");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.Forbidden, ProviderFailureKind.Permanent)]
    [InlineData(HttpStatusCode.Unauthorized, ProviderFailureKind.Permanent)]
    [InlineData(HttpStatusCode.NotFound, ProviderFailureKind.SymbolNotFound)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderFailureKind.Transient)]
    [InlineData(HttpStatusCode.BadGateway, ProviderFailureKind.Transient)]
    public async Task EachStatusOfTheService_IsToldApart_AndItsBodyIsNeverCopied(HttpStatusCode status, ProviderFailureKind kind)
    {
        (EdgarProvider provider, _, _, _) = Build(request =>
        {
            HttpResponseMessage response = EdgarFixtures.Json("secret detail: https://data.sec.gov/private?token=abc", status);
            if (status == HttpStatusCode.TooManyRequests)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            }

            return response;
        });

        Result<CompanyProfile> result = await provider.GetProfileAsync("AAPL", CancellationToken.None);

        ProviderErrors.Classify(result.Error!).Should().Be(kind);
        result.Error!.Message.Should().NotContain("secret").And.NotContain("token");
    }

    [Fact]
    public async Task ARateLimit_CarriesHowLongTheServiceAskedToWait()
    {
        (EdgarProvider provider, _, _, _) = Build(_ =>
        {
            HttpResponseMessage response = EdgarFixtures.Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return response;
        });

        Result<CompanyProfile> result = await provider.GetProfileAsync("AAPL", CancellationToken.None);

        ProviderErrors.RetryAfter(result.Error!).Should().Be(TimeSpan.FromSeconds(45));
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutRejectedException))]
    public async Task ATransportFailure_IsUnavailable_NotAnException(Type failure)
    {
        (EdgarProvider provider, _, _, _) = Build(_ => throw (Exception)(failure == typeof(HttpRequestException) ? new HttpRequestException("https://www.sec.gov/secret") : new TimeoutRejectedException()));

        Result<CompanyProfile> result = await provider.GetProfileAsync("AAPL", CancellationToken.None);

        (ProviderErrors.Classify(result.Error!), result.Error!.Message).Should().Be((ProviderFailureKind.Transient, result.Error.Message));
        result.Error.Message.Should().NotContain("secret");
    }

    [Fact]
    public async Task TheRawProfileAndFilings_AreKept_TheHugeMapIsNot_AndAStoreThatIsDownFailsNothing()
    {
        (EdgarProvider provider, _, RecordingStore payloads, _) = Build();
        (EdgarProvider broken, _, _, _) = Build(failStore: true);

        await provider.GetProfileAsync("AAPL", CancellationToken.None);
        await provider.GetFilingsAsync(new FilingsRequest(_apple, null, null, 10), CancellationToken.None);

        payloads.Stored.Select(payload => (payload.Provider, payload.Operation, payload.Query, payload.FetchedAt)).Should().Equal(
            ("edgar", "profile", "CIK0000320193", _now), ("edgar", "filings", "CIK0000320193", _now));
        payloads.Stored[0].Body.Should().Be(EdgarFixtures.Read("submissions-aapl.synthetic.json"));
        (await broken.GetProfileAsync("AAPL", CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData("BRK.B")]
    [InlineData("BRK-B")]
    public async Task AShareClassWrittenWithAStopOrADash_FindsTheCompany_WhichTheRegistryWritesWithADash(string ticker)
    {
        (EdgarProvider provider, EdgarFixtures.StubHandler handler, _, _) = Build(request => request.RequestUri!.AbsolutePath switch
        {
            "/files/company_tickers.json" => EdgarFixtures.Json("{\"0\":{\"cik_str\":320193,\"ticker\":\"AAPL\",\"title\":\"Apple Inc.\"},\"1\":{\"cik_str\":1067983,\"ticker\":\"BRK-B\",\"title\":\"BERKSHIRE HATHAWAY INC\"}}"),
            "/submissions/CIK0001067983.json" => EdgarFixtures.Json(EdgarFixtures.Read("submissions-aapl.synthetic.json").Replace("\"cik\": \"0000320193\"", "\"cik\": \"0001067983\"", StringComparison.Ordinal)),
            _ => EdgarFixtures.Json("{}", HttpStatusCode.NotFound),
        });

        Result<CompanyProfile> result = await provider.GetProfileAsync(ticker, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        handler.Requests.Select(request => request.RequestUri!.AbsolutePath).Should().Contain("/submissions/CIK0001067983.json");
    }

    [Fact]
    public async Task ATickerTheRegistryDoesNotHaveInEitherSpelling_IsNotFound()
    {
        (EdgarProvider provider, _, _, _) = Build();

        Result<CompanyProfile> result = await provider.GetProfileAsync("ZZZ.Z", CancellationToken.None);

        result.Error!.Code.Should().Be(ProviderErrors.SymbolNotFoundCode);
    }

    [Fact]
    public async Task TheRequests_KeepTheMinimumTimeApart_WaitingOnTheClock()
    {
        var clock = new SignallingClock(_now);
        (EdgarProvider provider, EdgarFixtures.StubHandler handler, _, _) = Build(intervalMilliseconds: 500, clock: clock);
        var patience = TimeSpan.FromSeconds(30); // a bound so that a broken test fails, it is never waited out

        Task<Result<CompanyProfile>> profile = provider.GetProfileAsync("AAPL", CancellationToken.None);
        await clock.TimerCreated.Task.WaitAsync(patience); // the second request is now waiting for the half second
        int afterFirst = handler.Requests.Count;
        clock.Advance(TimeSpan.FromMilliseconds(499));
        int stillWaiting = handler.Requests.Count;
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Result<CompanyProfile> done = await profile.WaitAsync(patience);

        (afterFirst, stillWaiting, handler.Requests.Count).Should().Be((1, 1, 2), "the second request of the profile waited for the half second");
        done.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ACancellation_IsNotAnOutcome()
    {
        (EdgarProvider provider, _, _, _) = Build();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Func<Task> act = () => provider.GetProfileAsync("AAPL", cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EveryCall_IsCountedByOperationAndOutcome()
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

            if (provider == "edgar")
            {
                lock (seen)
                {
                    seen.Add((operation!, outcome!));
                }
            }
        });
        listener.Start();
        (EdgarProvider ok, _, _, _) = Build();
        (EdgarProvider limited, _, _, _) = Build(_ => EdgarFixtures.Json("{}", HttpStatusCode.TooManyRequests));

        await ok.GetProfileAsync("AAPL", CancellationToken.None);
        await ok.GetFilingsAsync(new FilingsRequest(_apple, null, null, 5), CancellationToken.None);
        await limited.GetProfileAsync("AAPL", CancellationToken.None);

        lock (seen)
        {
            seen.Should().Equal(("tickers", "success"), ("profile", "success"), ("filings", "success"), ("tickers", "ratelimited"));
        }
    }

    [Fact]
    public void TheProvider_HasItsOwnName() => Build().Provider.Name.Should().Be("edgar");
}
