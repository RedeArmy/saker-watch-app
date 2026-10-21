using AwesomeAssertions;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Contracts;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Tests;

/// <summary>The one rule that says how current the prices of a stock are (ADR-0033, ADR-0039).</summary>
public sealed class PriceStatusRulesTests
{
    private static readonly Mic _market = Mic.Parse("XNAS");

    // Saturday 17 October 2026: Friday the 16th is the latest settled session.
    private static readonly DateTimeOffset _now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _settle = TimeSpan.FromHours(3);

    private static (string Status, DateTimeOffset? StaleSince) Of(LatestBar? bar, bool failed = false, DateTimeOffset? delistedAt = null) =>
        PriceStatusRules.Of(ExchangeCalendars.Default(), _market, bar, failed, delistedAt, _now, _settle);

    [Fact]
    public void WithoutAPrice_ItIsPending_OrUnavailableOnceLoadingFailedForGood()
    {
        Of(bar: null).Status.Should().Be(PriceStatuses.Pending);
        Of(bar: null, failed: true).Status.Should().Be(PriceStatuses.Unavailable);
    }

    [Fact]
    public void WithAPrice_ItIsCurrentUntilASessionSettlesAfterIt_ThenStale()
    {
        Of(new LatestBar(new DateOnly(2026, 10, 16), 100m, true)).Should().Be((PriceStatuses.Current, (DateTimeOffset?)null));

        (string status, DateTimeOffset? since) = Of(new LatestBar(new DateOnly(2026, 10, 14), 100m, true));

        status.Should().Be(PriceStatuses.Stale);
        since.Should().NotBeNull();
    }

    [Fact]
    public void ADelistedStock_IsDelisted_AheadOfEverythingElse()
    {
        var delistedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        Of(new LatestBar(new DateOnly(2026, 9, 30), 100m, true), delistedAt: delistedAt).Should().Be((PriceStatuses.Delisted, (DateTimeOffset?)null), "its last price is old for good, which is not a stale price");
        Of(bar: null, failed: true, delistedAt: delistedAt).Status.Should().Be(PriceStatuses.Delisted);
        Of(bar: null, delistedAt: delistedAt).Status.Should().Be(PriceStatuses.Delisted);
    }
}
