using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Companies;
using SakerWatch.MarketData.Infrastructure;

namespace SakerWatch.MarketData.Tests.Live;

/// <summary>
/// The EDGAR adapter against the real, public service, through the same registration the hosts use (allow-listed client included). It also checks the
/// shape the fixtures were written from (assumption A-028, verified 2026-10-20): run it again before turning the adapter on, and whenever the SEC may have changed.
/// </summary>
public sealed class EdgarLiveTests
{
    [EdgarLiveFact]
    public async Task TheAdapterWorksAgainstTheRealService()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Edgar:Enabled"] = "true",
                ["Edgar:UserAgent"] = Environment.GetEnvironmentVariable(EdgarLiveFactAttribute.IdentityVariable),
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProviderPayloadStore>(new NoProviderPayloadStore());
        services.AddEdgar(configuration);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var profiles = provider.GetRequiredService<IFundamentalsProvider>();
        var filings = provider.GetRequiredService<IFilingsProvider>();

        Result<CompanyProfile> apple = await profiles.GetProfileAsync("AAPL", CancellationToken.None);

        apple.IsSuccess.Should().BeTrue(apple.Error?.Code);
        (apple.Value.Cik, apple.Value.Tickers, apple.Value.Sic).Should().Be((Cik.Parse("320193"), apple.Value.Tickers, "3571"));
        apple.Value.Name.Should().Contain("Apple");
        apple.Value.Tickers.Should().Contain("AAPL");
        apple.Value.FiscalYearEnd.Should().NotBeNull();

        Result<IReadOnlyList<Filing>> annual = await filings.GetFilingsAsync(new FilingsRequest(apple.Value.Cik, ["10-K"], null, 5), CancellationToken.None);

        annual.IsSuccess.Should().BeTrue(annual.Error?.Code);
        annual.Value.Should().NotBeEmpty().And.OnlyContain(filing => filing.Form == "10-K");
        annual.Value.Select(filing => filing.FilingDate).Should().BeInDescendingOrder();
        annual.Value.Should().OnlyContain(filing => filing.Document != null && filing.Document.Host == "www.sec.gov");

        Result<CompanyProfile> unknown = await profiles.GetProfileAsync("ZZZZZZZZ", CancellationToken.None);
        ProviderErrors.Classify(unknown.Error!).Should().Be(ProviderFailureKind.SymbolNotFound);
    }
}
