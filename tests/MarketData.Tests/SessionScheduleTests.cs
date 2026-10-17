using AwesomeAssertions;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Tests;

public sealed class SessionScheduleTests
{
    private static readonly IExchangeCalendar _calendar = new UsEquityCalendar(Mic.Parse("XNYS"));
    private static readonly TimeSpan _settle = TimeSpan.FromHours(3);

    // 2026-10-16 is a Friday: the session closes at 16:00 EDT = 20:00 UTC and settles at 23:00 UTC.
    [Theory]
    [InlineData(2026, 10, 16, 12, 0, 2026, 10, 15)] // the morning of the session: yesterday is the settled one
    [InlineData(2026, 10, 16, 20, 30, 2026, 10, 15)] // just after the close, not yet settled
    [InlineData(2026, 10, 16, 22, 59, 2026, 10, 15)]
    [InlineData(2026, 10, 16, 23, 0, 2026, 10, 16)] // three hours after the close
    [InlineData(2026, 10, 17, 12, 0, 2026, 10, 16)] // Saturday
    [InlineData(2026, 10, 18, 23, 59, 2026, 10, 16)] // Sunday
    [InlineData(2026, 10, 19, 12, 0, 2026, 10, 16)] // Monday morning, the weekend in between
    public void LatestSettled_PointsAtTheNewestSessionThatHasSettled(int y, int mo, int d, int h, int mi, int ey, int emo, int ed) =>
        SessionSchedule.LatestSettled(_calendar, new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero), _settle).Should().Be(new DateOnly(ey, emo, ed));

    [Fact]
    public void LatestSettled_AfterALongWeekend_SkipsTheHolidays()
    {
        // Thanksgiving 2025 is Thursday November 27; Friday the 28th closed early (13:00 EST = 18:00 UTC), settled at 21:00 UTC.
        SessionSchedule.LatestSettled(_calendar, new DateTimeOffset(2025, 11, 27, 15, 0, 0, TimeSpan.Zero), _settle).Should().Be(new DateOnly(2025, 11, 26));
        SessionSchedule.LatestSettled(_calendar, new DateTimeOffset(2025, 11, 28, 20, 0, 0, TimeSpan.Zero), _settle).Should().Be(new DateOnly(2025, 11, 26));
        SessionSchedule.LatestSettled(_calendar, new DateTimeOffset(2025, 11, 28, 21, 0, 0, TimeSpan.Zero), _settle).Should().Be(new DateOnly(2025, 11, 28));
    }

    [Fact]
    public void LatestSettled_UsesTheMarketsOwnDate_NotUtc()
    {
        // 03:00 UTC on Saturday the 17th is still Friday evening in New York: Friday has settled by then.
        SessionSchedule.LatestSettled(_calendar, new DateTimeOffset(2026, 10, 17, 3, 0, 0, TimeSpan.Zero), _settle).Should().Be(new DateOnly(2026, 10, 16));
    }

    [Fact]
    public void LatestSettled_RefusesANullCalendar()
    {
        Func<DateOnly> act = () => SessionSchedule.LatestSettled(null!, DateTimeOffset.UtcNow, _settle);

        act.Should().Throw<ArgumentNullException>();
    }
}
