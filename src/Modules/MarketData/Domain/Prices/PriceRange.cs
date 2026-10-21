using SakerWatch.MarketData.Domain.Performance;

namespace SakerWatch.MarketData.Domain.Prices;

/// <summary>
/// Where a chart range starts (ADR-0038). The dates are the baselines of the performance windows (ADR-0030), so a chart of a range and the
/// change shown for the same window start from the same day: the first point is the baseline when it was a trading day.
/// </summary>
public static class PriceRange
{
    /// <summary>The first day of <paramref name="range"/> given the day of the newest price, or <see langword="null"/> for all the history.</summary>
    /// <param name="range">One of the codes of the contract (<c>1W</c>, <c>1M</c>, <c>3M</c>, <c>6M</c>, <c>YTD</c>, <c>1Y</c>, <c>5Y</c>, <c>MAX</c>).</param>
    /// <param name="latest">The day of the newest stored price.</param>
    /// <returns>The start date; <see langword="null"/> when the range is <c>MAX</c> or the code is not one of them.</returns>
    public static DateOnly? StartOf(string range, DateOnly latest)
    {
        ArgumentNullException.ThrowIfNull(range);
        IReadOnlyDictionary<PerformanceWindow, DateOnly> targets = PerformanceCalculator.TargetDates(latest, calendar: null);
        return range switch
        {
            "1W" => targets[PerformanceWindow.OneWeek],
            "1M" => targets[PerformanceWindow.OneMonth],
            "3M" => targets[PerformanceWindow.ThreeMonths],
            "6M" => targets[PerformanceWindow.SixMonths],
            "YTD" => targets[PerformanceWindow.YearToDate],
            "1Y" => targets[PerformanceWindow.OneYear],
            "5Y" => targets[PerformanceWindow.FiveYears],
            _ => null,
        };
    }

    /// <summary>The interval a range uses when the caller does not choose: days up to a year, weeks for five years, months for all the history.</summary>
    public static string DefaultInterval(string range)
    {
        ArgumentNullException.ThrowIfNull(range);
        return range switch
        {
            "5Y" => "week",
            "MAX" => "month",
            _ => "day",
        };
    }
}
