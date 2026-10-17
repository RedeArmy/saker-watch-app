using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Prices;

namespace SakerWatch.MarketData.Application.Ingestion;

/// <summary>The days both series agree on, ready to store, and how many days only one of them had.</summary>
/// <param name="Points">The merged days in ascending order.</param>
/// <param name="Unmatched">Days present in only one of the two series, which are left out.</param>
public sealed record MergedPrices(IReadOnlyList<PricePoint> Points, int Unmatched);

/// <summary>
/// Joins the unadjusted series with the adjusted one by date (two requests, because the provider answers either one or the other) and decides
/// which days are final. Pure: no clock of its own, no storage.
/// </summary>
public static class PriceSeriesMerger
{
    /// <summary>A session is final once its close, plus the settle delay, has passed.</summary>
    public static bool IsFinal(IExchangeCalendar calendar, DateOnly day, DateTimeOffset now, TimeSpan settleDelay)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return calendar.CloseOf(day) is not { } close || now >= close + settleDelay;
    }

    /// <summary>Merges the two series. A day missing from either is skipped and counted, never filled in.</summary>
    public static MergedPrices Merge(DailySeries raw, DailySeries adjusted, IExchangeCalendar calendar, DateTimeOffset now, TimeSpan settleDelay)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(adjusted);

        Dictionary<DateOnly, DailyBar> adjustedByDay = adjusted.Bars.ToDictionary(bar => bar.Date);
        var points = new List<PricePoint>(raw.Bars.Count);
        foreach (DailyBar bar in raw.Bars)
        {
            if (adjustedByDay.TryGetValue(bar.Date, out DailyBar? match)
                && PricePoint.Create(bar, match.Close, IsFinal(calendar, bar.Date, now, settleDelay)) is { IsSuccess: true } point)
            {
                points.Add(point.Value);
            }
        }

        return new MergedPrices(points, raw.Bars.Count + adjusted.Bars.Count - (2 * points.Count));
    }
}
