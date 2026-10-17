using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Domain.Calendars;

/// <summary>
/// The trading days and closing times of one market. Ingestion runs after a market's close in its own time zone and "stale" is judged per
/// market (ADR-0018), so both ask the calendar, never the weekday.
/// </summary>
public interface IExchangeCalendar
{
    /// <summary>The market the calendar describes.</summary>
    Mic Market { get; }

    /// <summary>The IANA time zone the market's sessions are expressed in.</summary>
    string TimeZoneId { get; }

    /// <summary>True when the market holds a session on <paramref name="date"/> (a date in the market's own time zone).</summary>
    bool IsTradingDay(DateOnly date);

    /// <summary>The instant the session of <paramref name="date"/> ends (early closes included), or <see langword="null"/> when there is no session.</summary>
    DateTimeOffset? CloseOf(DateOnly date);

    /// <summary>The last trading day strictly before <paramref name="date"/>.</summary>
    DateOnly PreviousTradingDay(DateOnly date);

    /// <summary>The first trading day strictly after <paramref name="date"/>.</summary>
    DateOnly NextTradingDay(DateOnly date);
}
