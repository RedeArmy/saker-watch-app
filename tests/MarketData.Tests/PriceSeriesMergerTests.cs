using AwesomeAssertions;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Prices;

namespace SakerWatch.MarketData.Tests;

public sealed class PriceSeriesMergerTests
{
    private static readonly IExchangeCalendar _calendar = new UsEquityCalendar(Mic.Parse("XNYS"));
    private static readonly ProviderListingKey _key = new("XNGS", "AAPL");
    private static readonly TimeSpan _settle = TimeSpan.FromHours(3);

    private static DailyBar Bar(DateOnly day, decimal close) => DailyBar.Create(day, close - 1, close + 1, close - 2, close, 1000).Value;

    private static DailySeries Series(bool adjusted, params DailyBar[] bars) => new(_key, bars, adjusted, 0);

    [Fact]
    public void Merge_JoinsByDate_RawBarWithTheAdjustedClose()
    {
        var friday = new DateOnly(2026, 10, 16);
        DailySeries raw = Series(false, Bar(friday, 343m));
        DailySeries adjusted = Series(true, Bar(friday, 340m));

        MergedPrices merged = PriceSeriesMerger.Merge(raw, adjusted, _calendar, new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero), _settle);

        merged.Points.Should().ContainSingle();
        merged.Points[0].Raw.Close.Should().Be(343m);
        merged.Points[0].AdjustedClose.Should().Be(340m);
        merged.Unmatched.Should().Be(0);
    }

    [Fact]
    public void Merge_ADayInOnlyOneSeries_IsSkippedAndCounted_NeverFilledIn()
    {
        DailySeries raw = Series(false, Bar(new DateOnly(2026, 10, 14), 100m), Bar(new DateOnly(2026, 10, 15), 101m));
        DailySeries adjusted = Series(true, Bar(new DateOnly(2026, 10, 15), 100m), Bar(new DateOnly(2026, 10, 16), 102m));

        MergedPrices merged = PriceSeriesMerger.Merge(raw, adjusted, _calendar, new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), _settle);

        merged.Points.Select(p => p.Raw.Date).Should().Equal(new DateOnly(2026, 10, 15));
        merged.Unmatched.Should().Be(2, "the 14th only in the raw series and the 16th only in the adjusted one");
    }

    [Fact]
    public void Merge_WithAnEmptySeries_HasNothingToStore()
    {
        MergedPrices merged = PriceSeriesMerger.Merge(Series(false), Series(true), _calendar, DateTimeOffset.UnixEpoch.AddYears(56), _settle);

        merged.Points.Should().BeEmpty();
        merged.Unmatched.Should().Be(0);
    }

    [Theory]
    [InlineData(20, 59, false)] // before the 16:00 EDT close (20:00 UTC)
    [InlineData(21, 0, false)] // an hour after the close
    [InlineData(22, 59, false)] // still inside the three-hour delay
    [InlineData(23, 0, true)] // exactly three hours after
    [InlineData(23, 30, true)]
    public void IsFinal_OnlyOnceTheSessionClosedAndSettled(int utcHour, int minute, bool expected) =>
        PriceSeriesMerger.IsFinal(_calendar, new DateOnly(2026, 10, 16), new DateTimeOffset(2026, 10, 16, utcHour, minute, 0, TimeSpan.Zero), _settle).Should().Be(expected);

    [Fact]
    public void IsFinal_AnEarlyCloseDay_SettlesFromTheEarlyClose()
    {
        // 2025-11-28 closed at 13:00 EST = 18:00 UTC; three hours later is 21:00 UTC.
        var day = new DateOnly(2025, 11, 28);

        PriceSeriesMerger.IsFinal(_calendar, day, new DateTimeOffset(2025, 11, 28, 20, 59, 0, TimeSpan.Zero), _settle).Should().BeFalse();
        PriceSeriesMerger.IsFinal(_calendar, day, new DateTimeOffset(2025, 11, 28, 21, 0, 0, TimeSpan.Zero), _settle).Should().BeTrue();
    }

    [Fact]
    public void IsFinal_ADayThatWasNotASession_IsFinal() =>
        PriceSeriesMerger.IsFinal(_calendar, new DateOnly(2026, 7, 3), new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero), _settle).Should().BeTrue();

    [Fact]
    public void IngestionOptions_DefaultToTenYearsAndThreeHours()
    {
        var options = new IngestionOptions();

        options.BackfillYears.Should().Be(10);
        options.SettleDelay.Should().Be(TimeSpan.FromHours(3));
        options.BackfillMaxAttempts.Should().Be(8);
    }
}
