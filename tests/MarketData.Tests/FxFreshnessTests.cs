using AwesomeAssertions;
using SakerWatch.MarketData.Application.Fx;

namespace SakerWatch.MarketData.Tests;

public sealed class FxFreshnessTests
{
    // Guatemala is UTC-6 all year: its midnight is 06:00 UTC.
    [Theory]
    [InlineData(2026, 10, 17, 5, 59, 16)]
    [InlineData(2026, 10, 17, 6, 0, 17)]
    [InlineData(2026, 7, 1, 5, 59, 30)]
    [InlineData(2026, 12, 31, 23, 0, 31)]
    public void TodayInGuatemala_FollowsTheLocalMidnight(int y, int mo, int d, int h, int mi, int expectedDay) =>
        FxFreshness.TodayInGuatemala(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero)).Day.Should().Be(expectedDay);

    [Theory]
    [InlineData(17, 17, 0)]
    [InlineData(14, 17, 3)]
    [InlineData(20, 17, 0)]
    public void AgeDays_CountsWholeDays_AndNeverGoesNegative(int latest, int today, int expected) =>
        FxFreshness.AgeDays(new DateOnly(2026, 10, latest), new DateOnly(2026, 10, today)).Should().Be(expected);

    [Theory]
    [InlineData(14, false)]
    [InlineData(13, true)]
    [InlineData(17, false)]
    public void IsStale_IsOlderThanTheMaximumAge(int latest, bool stale) =>
        FxFreshness.IsStale(new DateOnly(2026, 10, latest), new DateOnly(2026, 10, 17), maxAgeDays: 3).Should().Be(stale);

    [Fact]
    public void IsStale_WithNoRate_IsStale() =>
        FxFreshness.IsStale(null, new DateOnly(2026, 10, 17), maxAgeDays: 3).Should().BeTrue();

    [Fact]
    public void TheAnchor_IsUsdToGtq() =>
        FxFreshness.Anchor.Should().Be((SakerWatch.MarketData.Domain.Markets.CurrencyCode.Parse("USD"), SakerWatch.MarketData.Domain.Markets.CurrencyCode.Parse("GTQ")));
}
