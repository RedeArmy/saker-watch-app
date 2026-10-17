using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Tests;

public sealed class MarketDataProviderRouterTests
{
    private sealed class NamedProvider(string name) : IMarketDataProvider
    {
        public string Name => name;

        public Task<Result<IReadOnlyList<ProviderListing>>> SearchAsync(string symbol, CancellationToken cancellationToken) =>
            Task.FromResult<Result<IReadOnlyList<ProviderListing>>>(ProviderErrors.Unavailable(name));

        public Task<Result<DailySeries>> GetDailyAsync(DailySeriesRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<Result<DailySeries>>(ProviderErrors.Unavailable(name));
    }

    private static MarketDataProviderRouter Router(Dictionary<string, string> markets, params string[] providers) =>
        new(providers.Select(name => (IMarketDataProvider)new NamedProvider(name)), Options.Create(new MarketDataProvidersOptions { Markets = markets }));

    [Fact]
    public void For_AConfiguredMarket_ReturnsItsProvider()
    {
        MarketDataProviderRouter router = Router(new() { ["XNAS"] = "twelvedata", ["XTSE"] = "eodhd" }, "twelvedata", "eodhd");

        router.For(Mic.Parse("XNAS")).Value.Name.Should().Be("twelvedata");
        router.For(Mic.Parse("xtse")).Value.Name.Should().Be("eodhd");
    }

    [Fact]
    public void For_ProviderNames_AreComparedIgnoringCase() =>
        Router(new() { ["XNAS"] = " TwelveData " }, "twelvedata").For(Mic.Parse("XNAS")).IsSuccess.Should().BeTrue();

    [Fact]
    public void For_AMarketThatIsNotConfigured_IsDisabled()
    {
        Result<IMarketDataProvider> result = Router(new() { ["XNAS"] = "twelvedata" }, "twelvedata").For(Mic.Parse("XTSE"));

        result.Error!.Code.Should().Be(MarketDataProviderRouter.MarketDisabledCode);
        result.Error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public void For_AProviderThatIsNotRegistered_FailsLoudlyInsteadOfPickingAnother()
    {
        Result<IMarketDataProvider> result = Router(new() { ["XNAS"] = "twelvedata" }, "eodhd").For(Mic.Parse("XNAS"));

        result.Error!.Code.Should().Be(MarketDataProviderRouter.NotRegisteredCode);
        result.Error.Type.Should().Be(ErrorType.Failure);
    }

    [Fact]
    public void Validator_AcceptsAWellFormedTableAndRefusesTypos()
    {
        var validator = new MarketDataProvidersOptionsValidator();

        validator.Validate(null, new MarketDataProvidersOptions { Markets = new() { ["XNAS"] = "twelvedata" } }).Succeeded.Should().BeTrue();
        validator.Validate(null, new MarketDataProvidersOptions()).Succeeded.Should().BeTrue("no market enabled is a valid, if idle, table");
        validator.Validate(null, new MarketDataProvidersOptions { Markets = new() { ["NASDAQ"] = "twelvedata" } }).Failed.Should().BeTrue();
        validator.Validate(null, new MarketDataProvidersOptions { Markets = new() { ["XNAS"] = "  " } }).Failed.Should().BeTrue();
        validator.Validate(null, new MarketDataProvidersOptions { Markets = new() { ["XNAS"] = "twelvedata", ["xnas"] = "eodhd" } }).Failed.Should().BeTrue("the same market twice, differing only in case, would crash the router");
    }

    [Fact]
    public void AddMarketDataApplication_RegistersTheRouterWithItsValidatedTable()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMarketDataProvider>(new NamedProvider("twelvedata"));
        services.AddMarketDataApplication();
        services.Configure<MarketDataProvidersOptions>(options => options.Markets["XNYS"] = "twelvedata");
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMarketDataProviderRouter>().For(Mic.Parse("XNYS")).Value.Name.Should().Be("twelvedata");
    }

    [Fact]
    public void AddMarketDataApplication_CanBeRepeatedWithoutDuplicatingAnything()
    {
        var once = new ServiceCollection();
        once.AddMarketDataApplication();
        var twice = new ServiceCollection();
        twice.AddMarketDataApplication();
        twice.AddMarketDataApplication();

        twice.Count.Should().Be(once.Count);
    }
}
