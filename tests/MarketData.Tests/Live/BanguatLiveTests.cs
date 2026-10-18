using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Fx;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Infrastructure;

namespace SakerWatch.MarketData.Tests.Live;

/// <summary>The Banco de Guatemala adapter against the real, public service, through the same registration the hosts use (allow-listed client included).</summary>
public sealed class BanguatLiveTests
{
    private sealed class NoBudget : IProviderCallBudget
    {
        public Task<BudgetDecision> TryAcquireAsync(string provider, BudgetLimits limits, int credits, CancellationToken cancellationToken) =>
            Task.FromResult(BudgetDecision.Allowed);
    }

    [BanguatLiveFact]
    public async Task TheAdapterWorksAgainstTheRealService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProviderCallBudget>(new NoBudget());
        services.AddSingleton<IProviderPayloadStore>(new NoProviderPayloadStore());
        services.AddMarketData(new ConfigurationBuilder().Build());
        await using ServiceProvider provider = services.BuildServiceProvider();
        var fx = provider.GetRequiredService<IFxRateProvider>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var usd = CurrencyCode.Parse("USD");
        var gtq = CurrencyCode.Parse("GTQ");

        Result<IReadOnlyList<FxRate>> tenYears = await fx.GetDailyAsync(usd, gtq, today.AddYears(-10), today, CancellationToken.None);

        tenYears.IsSuccess.Should().BeTrue(tenYears.Error?.Code);
        IReadOnlyList<FxRate> rates = tenYears.Value;
        rates.Count.Should().BeInRange(3600, 3700, "the bank fills weekends, so there is one rate for every calendar day");
        rates.Select(r => r.AsOfDate).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        rates.Should().OnlyContain(r => r.Rate > 5m && r.Rate < 10m);
        rates[^1].AsOfDate.Should().BeOnOrBefore(today).And.BeOnOrAfter(today.AddDays(-5));

        Result<IReadOnlyList<FxRate>> reversed = await fx.GetDailyAsync(usd, gtq, today, today.AddDays(-1), CancellationToken.None);
        reversed.Error!.Code.Should().Be("marketdata.fx.range_invalid");

        Result<IReadOnlyList<FxRate>> future = await fx.GetDailyAsync(usd, gtq, today.AddDays(30), today.AddDays(40), CancellationToken.None);
        future.IsSuccess.Should().BeTrue();
        future.Value.Should().BeEmpty("the bank does not publish a month ahead");
    }
}
