using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Domain.Calendars;

namespace SakerWatch.MarketData.Domain.Performance;

/// <summary>
/// The one implementation of the performance windows (ADR-0006, ADR-0030). It works on adjusted closes and does no I/O: the dates it needs are
/// asked for with <see cref="TargetDates"/>, whoever holds the prices looks up the last close on or before each, and <see cref="Calculate"/> turns
/// them into the windows. A window is a change or the reason it has none. A market with no calendar still gets every window but the first.
/// </summary>
public static class PerformanceCalculator
{
    /// <summary>
    /// A version for the rules the cache cannot see by itself (ADR-0047). The key of the performance cache holds <see cref="PerformanceRules.CacheTag"/>,
    /// which is this number and a fingerprint of what the calculator does with fixed inputs, so a change inside the calculator or the calendars makes new
    /// keys without anyone remembering. Raise this number when a change makes the same prices give another report <i>outside</i> the calculator: how the
    /// baseline close on or before a date is looked up in the database, or what is stored as the adjusted close.
    /// </summary>
    public const int Version = 1;

    /// <summary>Distance, in days, a baseline may lie before its target for every window but the first (the longest closures around holidays).</summary>
    public const int DefaultMaxGapDays = 10;

    /// <summary>Every window, in report order.</summary>
    public static IReadOnlyList<PerformanceWindow> Windows { get; } = Enum.GetValues<PerformanceWindow>();

    /// <summary>
    /// The date each window starts from, given the day of the newest price. Without a calendar the one-day window has no start (the previous
    /// trading day cannot be told) and is left out; the others are plain calendar offsets and do not need one.
    /// </summary>
    public static IReadOnlyDictionary<PerformanceWindow, DateOnly> TargetDates(DateOnly latest, IExchangeCalendar? calendar)
    {
        var targets = new Dictionary<PerformanceWindow, DateOnly>
        {
            [PerformanceWindow.OneWeek] = latest.AddDays(-7),
            [PerformanceWindow.OneMonth] = latest.AddMonths(-1),
            [PerformanceWindow.ThreeMonths] = latest.AddMonths(-3),
            [PerformanceWindow.SixMonths] = latest.AddMonths(-6),
            [PerformanceWindow.YearToDate] = new DateOnly(latest.Year - 1, 12, 31),
            [PerformanceWindow.OneYear] = latest.AddYears(-1),
            [PerformanceWindow.FiveYears] = latest.AddYears(-5),
        };
        if (calendar is not null)
        {
            targets[PerformanceWindow.OneDay] = calendar.PreviousTradingDay(latest);
        }

        return targets;
    }

    /// <summary>The report of a listing with no price: <see cref="PerformanceGap.NoData"/> in every window.</summary>
    public static PerformanceReport NoData() =>
        new(null, [.. Windows.Select(window => WindowPerformance.Missing(window, PerformanceGap.NoData))]);

    /// <summary>
    /// Computes the windows. <paramref name="baselines"/> holds, for each date of <see cref="TargetDates"/>, the last close on or before it, or
    /// <see langword="null"/> when nothing is stored that early. A date missing from the dictionary counts as nothing stored.
    /// </summary>
    public static PerformanceReport Calculate(
        PriceSample? latest,
        IReadOnlyDictionary<DateOnly, PriceSample?> baselines,
        IExchangeCalendar? calendar,
        int maxGapDays = DefaultMaxGapDays)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        ArgumentOutOfRangeException.ThrowIfNegative(maxGapDays);

        if (latest is null)
        {
            return NoData();
        }

        IReadOnlyDictionary<PerformanceWindow, DateOnly> targets = TargetDates(latest.Date, calendar);
        var windows = new List<WindowPerformance>(Windows.Count);
        foreach (PerformanceWindow window in Windows)
        {
            if (!targets.TryGetValue(window, out DateOnly target))
            {
                windows.Add(WindowPerformance.Missing(window, PerformanceGap.CalendarUnavailable));
                continue;
            }

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

        if (target.DayNumber - baseline.Date.DayNumber > allowedGapDays)
        {
            return WindowPerformance.Missing(window, PerformanceGap.HistoryGap);
        }

        // Both closes are positive by construction, so the change always exists; a baseline the kit refuses is treated as no usable history.
        Result<RelativeChange> change = RelativeChange.Between(baseline.AdjustedClose, latest.AdjustedClose);
        return change.IsSuccess
            ? WindowPerformance.Of(window, change.Value, baseline)
            : WindowPerformance.Missing(window, PerformanceGap.InsufficientHistory);
    }
}
