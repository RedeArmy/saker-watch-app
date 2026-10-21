using System.Net;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Companies;
using SakerWatch.MarketData.Infrastructure.Providers.Edgar;
using SakerWatch.MarketData.Infrastructure.Providers.Fake;

namespace SakerWatch.MarketData.Tests;

/// <summary>
/// What every provider of company profiles and filings must do (ADR-0040), whoever it is: the same assertions run against the fake and against the
/// EDGAR adapter reading recorded-shape fixtures, so the fake can stand in for the real one wherever a test needs a provider.
/// </summary>
public abstract class FundamentalsProviderContract
{
    private static readonly Cik _apple = Cik.Parse("320193");

    /// <summary>A provider that knows Apple (key 320193, ticker AAPL) with several filings and nobody else.</summary>
    protected abstract (IFundamentalsProvider Profiles, IFilingsProvider Filings) Create();

    [Fact]
    public async Task TheProfileOfAKnownTicker_NamesTheCompanyAndItsKey()
    {
        (IFundamentalsProvider profiles, _) = Create();

        CompanyProfile profile = (await profiles.GetProfileAsync("aapl", CancellationToken.None)).Value;

        (profile.Cik, profile.Name.Length > 0, profile.Tickers).Should().Be((_apple, true, profile.Tickers));
        profile.Tickers.Should().Contain("AAPL");
        profiles.Name.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AnUnknownTicker_IsSymbolNotFound_AndATickerThatIsNotOneIsAValidationFailure()
    {
        (IFundamentalsProvider profiles, _) = Create();

        Result<CompanyProfile> unknown = await profiles.GetProfileAsync("ZZZZ", CancellationToken.None);
        Result<CompanyProfile> nonsense = await profiles.GetProfileAsync("not a ticker", CancellationToken.None);

        (ProviderErrors.Classify(unknown.Error!), nonsense.Error!.Type).Should().Be((ProviderFailureKind.SymbolNotFound, ErrorType.Validation));
    }

    [Fact]
    public async Task TheFilings_AreNewestFirst_EachWithItsNumberFormAndDay()
    {
        (_, IFilingsProvider filings) = Create();

        IReadOnlyList<Filing> list = (await filings.GetFilingsAsync(new FilingsRequest(_apple, null, null, 100), CancellationToken.None)).Value;

        list.Should().NotBeEmpty();
        list.Select(filing => filing.FilingDate).Should().BeInDescendingOrder();
        list.Should().OnlyContain(filing => filing.AccessionNumber.Length == 20 && filing.Form.Length > 0);
        list.Select(filing => filing.AccessionNumber).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task TheLimitKeepsTheNewest_TheFormsNarrowIgnoringCase_AndSinceKeepsTheRecent()
    {
        (_, IFilingsProvider filings) = Create();
        IReadOnlyList<Filing> all = (await filings.GetFilingsAsync(new FilingsRequest(_apple, null, null, 100), CancellationToken.None)).Value;

        IReadOnlyList<Filing> two = (await filings.GetFilingsAsync(new FilingsRequest(_apple, null, null, 2), CancellationToken.None)).Value;
        IReadOnlyList<Filing> annual = (await filings.GetFilingsAsync(new FilingsRequest(_apple, ["10-k"], null, 100), CancellationToken.None)).Value;
        DateOnly cutoff = all[1].FilingDate;
        IReadOnlyList<Filing> recent = (await filings.GetFilingsAsync(new FilingsRequest(_apple, null, cutoff, 100), CancellationToken.None)).Value;

        two.Select(filing => filing.AccessionNumber).Should().Equal(all.Take(2).Select(filing => filing.AccessionNumber));
        annual.Should().NotBeEmpty().And.OnlyContain(filing => filing.Form == "10-K");
        recent.Should().OnlyContain(filing => filing.FilingDate >= cutoff).And.HaveCountGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ADocument_HasTheAddressOfItsFiling_AndAFilingWithoutOneHasNone()
    {
        (_, IFilingsProvider filings) = Create();

        IReadOnlyList<Filing> list = (await filings.GetFilingsAsync(new FilingsRequest(_apple, null, null, 100), CancellationToken.None)).Value;

        foreach (Filing filing in list.Where(filing => filing.Document is not null))
        {
            filing.Document!.AbsoluteUri.Should().Be($"https://www.sec.gov/Archives/edgar/data/320193/{filing.AccessionNumber.Replace("-", string.Empty, StringComparison.Ordinal)}/{filing.PrimaryDocument}");
        }

        list.Should().OnlyContain(filing => (filing.Document == null) == (filing.PrimaryDocument == null));
    }

    [Fact]
    public async Task ACompanyTheRegistryDoesNotKnow_IsSymbolNotFound()
    {
        (_, IFilingsProvider filings) = Create();

        Result<IReadOnlyList<Filing>> result = await filings.GetFilingsAsync(new FilingsRequest(Cik.Parse("999999"), null, null, 10), CancellationToken.None);

        ProviderErrors.Classify(result.Error!).Should().Be(ProviderFailureKind.SymbolNotFound);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(201, null)]
    [InlineData(10, "10-K; DROP")]
    [InlineData(10, "")]
    public async Task ABadLimitOrForm_IsAValidationFailure(int limit, string? form)
    {
        (_, IFilingsProvider filings) = Create();

        Result<IReadOnlyList<Filing>> result = await filings.GetFilingsAsync(new FilingsRequest(_apple, form is null ? null : [form], null, limit), CancellationToken.None);

        result.Error!.Type.Should().Be(ErrorType.Validation);
    }
}

/// <summary>The contract, against the fake.</summary>
public sealed class FakeFundamentalsProviderContractTests : FundamentalsProviderContract
{
    /// <inheritdoc />
    protected override (IFundamentalsProvider Profiles, IFilingsProvider Filings) Create()
    {
        var fake = new FakeFundamentalsProvider().WithCompany(
            new CompanyProfile(Cik.Parse("320193"), "Apple Inc.", ["AAPL"], ["Nasdaq"], "3571", "Electronic Computers", "0926"));
        return (fake, fake);
    }
}

/// <summary>The contract, against the EDGAR adapter reading fixtures that have the shape of the registry's answers.</summary>
public sealed class EdgarProviderContractTests : FundamentalsProviderContract
{
    /// <inheritdoc />
    protected override (IFundamentalsProvider Profiles, IFilingsProvider Filings) Create()
    {
        var handler = new EdgarFixtures.StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/files/company_tickers.json" => EdgarFixtures.Json(EdgarFixtures.Read("company-tickers.synthetic.json")),
            "/submissions/CIK0000320193.json" => EdgarFixtures.Json(EdgarFixtures.Read("submissions-aapl.synthetic.json")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") },
        });
        var provider = new EdgarProvider(
            new EdgarFixtures.SingleClientFactory(handler),
            new NoProviderPayloadStore(),
            Options.Create(new EdgarOptions { Enabled = true, UserAgent = "SakerWatchTests tests@example.com", MinRequestIntervalMilliseconds = 0 }),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<EdgarProvider>.Instance);
        return (provider, provider);
    }
}

/// <summary>Helpers the EDGAR tests share: the fixtures, the stub of the network and a factory that hands it out.</summary>
internal static class EdgarFixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "edgar", name));

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    public sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://www.sec.gov") };
    }
}
