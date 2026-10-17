using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SakerWatch.MarketData.Application.Fx;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Infrastructure;
using SakerWatch.MarketData.Infrastructure.Providers.Banguat;
using SakerWatch.MarketData.Infrastructure.Providers.TwelveData;

namespace SakerWatch.MarketData.Tests;

public sealed class MarketDataRegistrationTests
{
    // The hosts register Redis (AddSakerRedis) and Mongo (AddSakerMongo) and with them the real budget and payload store; stand-ins take their place here.
    private sealed class AllowEverything : IProviderCallBudget
    {
        public Task<BudgetDecision> TryAcquireAsync(string provider, BudgetLimits limits, int credits, CancellationToken cancellationToken) =>
            Task.FromResult(BudgetDecision.Allowed);
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProviderCallBudget>(new AllowEverything());
        services.AddSingleton<IProviderPayloadStore>(new NoProviderPayloadStore());
        services.AddMarketData(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddMarketData_RoutesAnUsMarketToTwelveData_WhenTheKeyIsSet()
    {
        using ServiceProvider provider = Build(new()
        {
            ["MarketData:Providers:Markets:XNAS"] = "twelvedata",
            ["MarketData:Providers:Markets:XNYS"] = "TwelveData",
            ["TwelveData:ApiKey"] = "not-a-real-key",
        });

        var router = provider.GetRequiredService<IMarketDataProviderRouter>();

        router.For(Mic.Parse("XNAS")).Value.Should().BeOfType<TwelveDataProvider>();
        router.For(Mic.Parse("XNYS")).Value.Name.Should().Be("twelvedata");
        router.For(Mic.Parse("XTSE")).Error!.Code.Should().Be(MarketDataProviderRouter.MarketDisabledCode);
    }

    [Fact]
    public void AddMarketData_ServesTheBankRatesWithoutAnyConfiguration()
    {
        using ServiceProvider provider = Build(new());

        provider.GetRequiredService<IFxRateProvider>().Should().BeOfType<BanguatFxProvider>().Which.Name.Should().Be("banguat");
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(BanguatFxProvider.ClientName).BaseAddress
            .Should().Be(new Uri("https://www.banguat.gob.gt/variables/ws/TipoCambio.asmx"));
    }

    [Theory]
    [InlineData("http://www.banguat.gob.gt/variables/ws/TipoCambio.asmx")]
    [InlineData("not a url")]
    public void TheBankAddress_ThatIsNotHttps_IsRefused(string baseUrl)
    {
        using ServiceProvider provider = Build(new() { ["Banguat:BaseUrl"] = baseUrl });

        Action act = () => _ = provider.GetRequiredService<IOptions<BanguatOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void TheApiKey_IsRequired_OnlyWhenAMarketIsRoutedToTheProvider()
    {
        using ServiceProvider routedWithoutKey = Build(new() { ["MarketData:Providers:Markets:XNAS"] = "twelvedata" });
        using ServiceProvider notRouted = Build(new());

        Action routed = () => _ = routedWithoutKey.GetRequiredService<IOptions<TwelveDataOptions>>().Value;
        Action idle = () => _ = notRouted.GetRequiredService<IOptions<TwelveDataOptions>>().Value;

        routed.Should().Throw<OptionsValidationException>().WithMessage("*TwelveData:ApiKey is required*");
        idle.Should().NotThrow("a host that does not use the provider needs no key");
    }

    [Fact]
    public void TheErrorAboutAMissingKey_NeverContainsAKeyValue()
    {
        using ServiceProvider provider = Build(new() { ["MarketData:Providers:Markets:XNAS"] = "twelvedata" });

        Action act = () => _ = provider.GetRequiredService<IOptions<TwelveDataOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().Which.Failures.Should().OnlyContain(f => !f.Contains("apikey=", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("http://api.twelvedata.com/")]
    [InlineData("api.twelvedata.com")]
    public void ABaseAddress_ThatIsNotHttps_IsRefused(string baseUrl)
    {
        using ServiceProvider provider = Build(new() { ["TwelveData:BaseUrl"] = baseUrl, ["TwelveData:ApiKey"] = "k" });

        Action act = () => _ = provider.GetRequiredService<IOptions<TwelveDataOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AMarketCodeTypo_FailsAtStartupInsteadOfDisablingTheMarketSilently()
    {
        using ServiceProvider provider = Build(new() { ["MarketData:Providers:Markets:NASDAQ"] = "twelvedata", ["TwelveData:ApiKey"] = "k" });

        Action act = () => _ = provider.GetRequiredService<IOptions<MarketDataProvidersOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddMarketData_CanBeRepeated()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var once = new ServiceCollection();
        once.AddMarketData(configuration);
        var twice = new ServiceCollection();
        twice.AddMarketData(configuration);
        twice.AddMarketData(configuration);

        twice.Count.Should().Be(once.Count);
    }
}
