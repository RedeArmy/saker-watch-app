using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Infrastructure;
using SakerWatch.MarketData.Infrastructure.Providers.Edgar;

namespace SakerWatch.MarketData.Tests;

/// <summary>The identity the SEC requires, the addresses, and the flag that keeps the adapter off until the owner says who is calling (ADR-0040).</summary>
public sealed class EdgarOptionsTests
{
    private static ValidateOptionsResult Validate(EdgarOptions options) => new EdgarOptionsValidator().Validate(null, options);

    private static ServiceProvider Services(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProviderPayloadStore>(new NoProviderPayloadStore());
        services.AddEdgar(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ByDefault_TheAdapterIsOff_AndTheSettingsAreValid()
    {
        var options = new EdgarOptions();

        (options.Enabled, options.UserAgent, Validate(options).Succeeded).Should().Be((false, (string?)null, true));
    }

    [Theory]
    [InlineData("SakerWatch you@example.com")]
    [InlineData("Eder Garcia eder@example.org (personal project)")]
    public void WhenOn_AnIdentityWithAContactIsAccepted(string agent) =>
        Validate(new EdgarOptions { Enabled = true, UserAgent = agent }).Succeeded.Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SakerWatch")]
    [InlineData("SakerWatch you@example.com\r\nX-Injected: 1")]
    [InlineData("SakerWatch you@example.comé")]
    public void WhenOn_AnIdentityWithoutAContactOrWithControlCharacters_IsRefused(string? agent) =>
        Validate(new EdgarOptions { Enabled = true, UserAgent = agent }).Failed.Should().BeTrue();

    [Fact]
    public void AnIdentityTooLong_IsRefused() =>
        Validate(new EdgarOptions { Enabled = true, UserAgent = new string('a', 190) + " x@example.com" }).Failed.Should().BeTrue();

    [Theory]
    [InlineData("http://data.sec.gov", "https://www.sec.gov")]
    [InlineData("https://data.sec.gov", "ftp://www.sec.gov")]
    [InlineData("not an address", "https://www.sec.gov")]
    public void TheAddresses_MustBeHttps(string data, string www) =>
        Validate(new EdgarOptions { DataBaseUrl = data, WwwBaseUrl = www }).Failed.Should().BeTrue();

    [Fact]
    public void Off_NothingIsRegistered_SoNothingCanCallTheSec()
    {
        using ServiceProvider provider = Services([]);

        provider.GetService<IFundamentalsProvider>().Should().BeNull();
        provider.GetService<IFilingsProvider>().Should().BeNull();
        provider.GetService<IHttpClientFactory>().Should().BeNull("not even the named client exists");
    }

    [Fact]
    public void On_BothPortsAreTheSameAdapter_AndTheNamedClientOnlyReachesTheTwoSecHosts()
    {
        using ServiceProvider provider = Services(new() { ["Edgar:Enabled"] = "true", ["Edgar:UserAgent"] = "SakerWatchTests tests@example.com" });

        IFundamentalsProvider profiles = provider.GetRequiredService<IFundamentalsProvider>();
        IFilingsProvider filings = provider.GetRequiredService<IFilingsProvider>();

        profiles.Should().BeSameAs(filings);
        profiles.Name.Should().Be("edgar");
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("edgar").BaseAddress.Should().Be(new Uri("https://www.sec.gov"));
    }

    [Fact]
    public void On_WithoutAContact_TheHostRefusesToStart_InsteadOfFailingTheFirstCall()
    {
        using ServiceProvider provider = Services(new() { ["Edgar:Enabled"] = "true", ["Edgar:UserAgent"] = "SakerWatch" });

        Action read = () => _ = provider.GetRequiredService<IOptions<EdgarOptions>>().Value;

        read.Should().Throw<OptionsValidationException>().WithMessage("*contact address*");
    }

    [Theory]
    [InlineData("SakerWatch you@example.com\n")]
    [InlineData("SakerWatch you@example.com\r\nX-Injected: 1")]
    public void WhenOn_AnIdentityWithALineBreakIsRefused_EvenAtTheEnd(string agent) =>
        Validate(new EdgarOptions { Enabled = true, UserAgent = agent }).Succeeded.Should().BeFalse();
}
