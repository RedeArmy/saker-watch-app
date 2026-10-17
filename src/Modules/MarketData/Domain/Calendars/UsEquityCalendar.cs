using System.Collections.Frozen;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Domain.Calendars;

/// <summary>
/// The NYSE and Nasdaq equity calendar, computed from the exchange rules rather than a list of years: weekends, the nine holidays with their
/// weekend observance, Good Friday, the early 13:00 closes, and the few extraordinary closures that no rule predicts (listed in
/// <see cref="ExtraordinaryClosures"/>). The rules are accurate from 1998, when Martin Luther King Jr. Day became a market holiday; a date
/// before that is outside the supported range and the calendar says so instead of guessing.
/// </summary>
public sealed class UsEquityCalendar : IExchangeCalendar
{
    /// <summary>First year the rules are valid for.</summary>
    public const int FirstSupportedYear = 1998;

    private static readonly TimeSpan _regularClose = new(16, 0, 0);
    private static readonly TimeSpan _earlyClose = new(13, 0, 0);

    // Days the exchanges closed outside their schedule: 9/11 attacks, national days of mourning and Hurricane Sandy.
    private static readonly FrozenSet<DateOnly> _extraordinaryClosures = new DateOnly[]
    {
        new(2001, 9, 11),
        new(2001, 9, 12),
        new(2001, 9, 13),
        new(2001, 9, 14),
        new(2004, 6, 11),
        new(2007, 1, 2),
        new(2012, 10, 29),
        new(2012, 10, 30),
        new(2018, 12, 5),
        new(2025, 1, 9),
    }.ToFrozenSet();

    // Early closes that no rule predicts: the eve of 2000 and the Friday after Christmas 2003, and the 2002 pair where the early close fell on
    // Friday July 5 instead of Wednesday July 3, which the July 3 rule would have shortened.
    private static readonly FrozenSet<DateOnly> _extraordinaryEarlyCloses = new DateOnly[]
    {
        new(1999, 12, 31),
        new(2002, 7, 5),
        new(2003, 12, 26),
    }.ToFrozenSet();

    private static readonly FrozenSet<DateOnly> _fullDaysDespiteTheRule = new DateOnly[]
    {
        new(2002, 7, 3),
    }.ToFrozenSet();

    private readonly TimeZoneInfo _zone;

    /// <summary>Creates the calendar for one of the US markets that share the schedule (<c>XNYS</c>, <c>XNAS</c>, <c>ARCX</c>...).</summary>
    public UsEquityCalendar(Mic market)
    {
        Market = market ?? throw new ArgumentNullException(nameof(market));
        _zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }

    /// <summary>The days the US markets closed outside their schedule, kept as data because no rule produces them.</summary>
    public static IReadOnlyCollection<DateOnly> ExtraordinaryClosures => _extraordinaryClosures;

    /// <summary>The days that closed early without a rule to say so, and the day a rule would have shortened but did not, kept as data.</summary>
    public static IReadOnlyCollection<DateOnly> ExtraordinaryEarlyCloses => _extraordinaryEarlyCloses;

    /// <inheritdoc />
    public Mic Market { get; }

    /// <inheritdoc />
    public string TimeZoneId => "America/New_York";

    /// <inheritdoc />
    public bool IsTradingDay(DateOnly date)
    {
        EnsureSupported(date);
        return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && !_extraordinaryClosures.Contains(date)
            && !IsHoliday(date);
    }

    /// <inheritdoc />
    public DateTimeOffset? CloseOf(DateOnly date)
    {
        if (!IsTradingDay(date))
        {
            return null;
        }

        TimeSpan close = IsEarlyClose(date) ? _earlyClose : _regularClose;
        var local = date.ToDateTime(TimeOnly.FromTimeSpan(close), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, _zone.GetUtcOffset(local));
    }

    /// <inheritdoc />
    public DateOnly PreviousTradingDay(DateOnly date)
    {
        DateOnly day = date.AddDays(-1);
        while (!IsTradingDay(day))
        {
            day = day.AddDays(-1);
        }

        return day;
    }

    /// <inheritdoc />
    public DateOnly NextTradingDay(DateOnly date)
    {
        DateOnly day = date.AddDays(1);
        while (!IsTradingDay(day))
        {
            day = day.AddDays(1);
        }

        return day;
    }

    private static void EnsureSupported(DateOnly date)
    {
        if (date.Year < FirstSupportedYear)
        {
            throw new ArgumentOutOfRangeException(nameof(date), date, $"The US calendar rules are valid from {FirstSupportedYear}.");
        }
    }

    private static bool IsHoliday(DateOnly date)
    {
        int year = date.Year;
        return date == ObservedNewYear(year)
            || date == NthWeekday(year, 1, DayOfWeek.Monday, 3)
            || date == NthWeekday(year, 2, DayOfWeek.Monday, 3)
            || date == GoodFriday(year)
            || date == LastWeekday(year, 5, DayOfWeek.Monday)
            || (year >= 2022 && date == Observed(new DateOnly(year, 6, 19)))
            || date == Observed(new DateOnly(year, 7, 4))
            || date == NthWeekday(year, 9, DayOfWeek.Monday, 1)
            || date == Thanksgiving(year)
            || date == Observed(new DateOnly(year, 12, 25));
    }

    private static bool IsEarlyClose(DateOnly date)
    {
        if (_extraordinaryEarlyCloses.Contains(date))
        {
            return true;
        }

        if (_fullDaysDespiteTheRule.Contains(date))
        {
            return false;
        }

        if (date == Thanksgiving(date.Year).AddDays(1))
        {
            return true;
        }

        // The day before Independence Day, when that day is itself a session and July 4 falls Tuesday to Friday.
        if (date is { Month: 7, Day: 3 } && new DateOnly(date.Year, 7, 4).DayOfWeek is >= DayOfWeek.Tuesday and <= DayOfWeek.Friday)
        {
            return true;
        }

        // Christmas Eve, when it is a weekday Monday to Thursday (a Friday Christmas Eve is the observed holiday).
        return date is { Month: 12, Day: 24 } && date.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Thursday;
    }

    // A fixed-date holiday on Saturday is observed on Friday, on Sunday on Monday.
    private static DateOnly Observed(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Saturday => date.AddDays(-1),
        DayOfWeek.Sunday => date.AddDays(1),
        _ => date,
    };

    // New Year's Day on a Saturday is not observed on the previous Friday (the exchanges stay open on December 31).
    private static DateOnly ObservedNewYear(int year)
    {
        var date = new DateOnly(year, 1, 1);
        return date.DayOfWeek == DayOfWeek.Sunday ? date.AddDays(1) : date;
    }

    private static DateOnly Thanksgiving(int year) => NthWeekday(year, 11, DayOfWeek.Thursday, 4);

    private static DateOnly NthWeekday(int year, int month, DayOfWeek weekday, int nth)
    {
        var first = new DateOnly(year, month, 1);
        int offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + ((nth - 1) * 7));
    }

    private static DateOnly LastWeekday(int year, int month, DayOfWeek weekday)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        int offset = ((int)last.DayOfWeek - (int)weekday + 7) % 7;
        return last.AddDays(-offset);
    }

    // Anonymous Gregorian algorithm (Meeus/Jones/Butcher) for Easter Sunday; Good Friday is two days before.
    private static DateOnly GoodFriday(int year)
    {
        int a = year % 19;
        int b = year / 100;
        int c = year % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = ((19 * a) + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        int m = (a + (11 * h) + (22 * l)) / 451;
        int month = (h + l - (7 * m) + 114) / 31;
        int day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day).AddDays(-2);
    }
}
