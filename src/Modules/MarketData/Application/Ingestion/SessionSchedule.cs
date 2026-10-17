using SakerWatch.MarketData.Domain.Calendars;

namespace SakerWatch.MarketData.Application.Ingestion;

/// <summary>Which trading day a market's daily refresh is due for, from its calendar and the clock. Pure.</summary>
public static class SessionSchedule
{
    // Longer than any run of closed days in a calendar (a long holiday weekend), so the search always finds a session.
    private const int _lookBackDays = 14;

    /// <summary>
    /// The newest session whose close, plus <paramref name="settleDelay"/>, has passed: the day to refresh now. The session of today is not
    /// due until its settle delay has gone by, and a weekend or a holiday keeps pointing at the last session before it.
    /// </summary>
    public static DateOnly LatestSettled(IExchangeCalendar calendar, DateTimeOffset now, TimeSpan settleDelay)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId);
        DateOnly day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (int back = 0; back < _lookBackDays; back++, day = day.AddDays(-1))
        {
            if (calendar.CloseOf(day) is { } close && now >= close + settleDelay)
            {
                return day;
            }
        }

        throw new InvalidOperationException($"No settled session of {calendar.Market} was found in the last {_lookBackDays} days.");
    }
}
