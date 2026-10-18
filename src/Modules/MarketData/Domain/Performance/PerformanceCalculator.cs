using SakerWatch.MarketData.Domain.Calendars;

namespace SakerWatch.MarketData.Domain.Performance;

/// <summary>
/// The one implementation of the performance windows (ADR-0006, ADR-0030). It works on adjusted closes and does no I/O: the dates it needs are
/// asked for with <see cref="TargetDates"/>, whoever holds the prices looks up the last close on or before each, and <see cref="Calculate"/> turns
/// them into the windows. A window is a change or the reason it has none.
/// </summary>
public static class PerformanceCalculator
{
    /// <summary>Distance, in days, a baseline may lie before its target for every window but the first (the longest closures around holidays).</summary>
    public const int DefaultMaxGapDays = 10;

    /// <summary>Every window, in report order.</summary>
    public static IReadOnlyList<PerformanceWindow> Windows { get; } = Enum.GetValues<PerformanceWindow>();

    /// <summary>The date each window starts from, given the day of the newest price.</summary>
    public static IReadOnlyDictionary<PerformanceWindow, DateOnly> TargetDates(DateOnly latest, IExchangeCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return new Dictionary<PerformanceWindow, DateOnly>
        {
            [PerformanceWindow.OneDay] = calendar.PreviousTradingDay(latest),
            [PerformanceWindow.OneWeek] = latest.AddDays(-7),
            [PerformanceWindow.OneMonth] = latest.AddMonths(-1),
            [PerformanceWindow.ThreeMonths] = latest.AddMonths(-3),
            [PerformanceWindow.SixMonths] = latest.AddMonths(-6),
            [PerformanceWindow.YearToDate] = new DateOnly(latest.Year - 1, 12, 31),
            [PerformanceWindow.OneYear] = latest.AddYears(-1),
            [PerformanceWindow.FiveYears] = latest.AddYears(-5),
        };
    }

    /// <summary>
    /// Computes the windows. <paramref name="baselines"/> holds, for each date of <see cref="TargetDates"/>, the last close on or before it, or
    /// <see langword="null"/> when nothing is stored that early. A date missing from the dictionary counts as nothing stored.
    /// </summary>
    public static PerformanceReport Calculate(
        PriceSample? latest,
        IReadOnlyDictionary<DateOnly, PriceSample?> baselines,
        IExchangeCalendar calendar,
        int maxGapDays = DefaultMaxGapDays)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentOutOfRangeException.ThrowIfNegative(maxGapDays);

        if (latest is null)
        {
            return new PerformanceReport(null, [.. Windows.Select(window => WindowPerformance.Missing(window, PerformanceGap.NoData))]);
        }

        IReadOnlyDictionary<PerformanceWindow, DateOnly> targets = TargetDates(latest.Date, calendar);
        var windows = new List<WindowPerformance>(Windows.Count);
        foreach (PerformanceWindow window in Windows)
        {
            DateOnly target = targets[window];
            baselines.TryGetValue(target, out PriceSample? baseline);
            windows.Add(Measure(window, latest, baseline, target, window == PerformanceWindow.OneDay ? 0 : maxGapDays));
        }

        return new PerformanceReport(latest, windows);
    }

    private static WindowPerformance Measure(PerformanceWindow window, PriceSample latest, PriceSample? baseline, DateOnly target, int allowedGapDays)
    {
        if (baseline is null)
        {
            return WindowPerformance.Missing(window, PerformanceGap.InsufficientHistory);
        }

        return target.DayNumber - baseline.Date.DayNumber > allowedGapDays
            ? WindowPerformance.Missing(window, PerformanceGap.HistoryGap)
            : WindowPerformance.Of(window, latest.AdjustedClose / baseline.AdjustedClose - 1m, baseline);
    }
}
