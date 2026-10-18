using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Domain.Performance;

/// <summary>The windows a price change is reported over (ADR-0006, ADR-0030).</summary>
public enum PerformanceWindow
{
    /// <summary>Since the previous trading day.</summary>
    OneDay,

    /// <summary>Since seven days earlier.</summary>
    OneWeek,

    /// <summary>Since one calendar month earlier.</summary>
    OneMonth,

    /// <summary>Since three calendar months earlier.</summary>
    ThreeMonths,

    /// <summary>Since six calendar months earlier.</summary>
    SixMonths,

    /// <summary>Since the last close of the previous year.</summary>
    YearToDate,

    /// <summary>Since one calendar year earlier.</summary>
    OneYear,

    /// <summary>Since five calendar years earlier.</summary>
    FiveYears,
}

/// <summary>Why a window has no change. A window is a change or a reason, never an invented number (ADR-0030).</summary>
public enum PerformanceGap
{
    /// <summary>The listing has no price at all.</summary>
    NoData,

    /// <summary>Nothing is stored on or before the window's start: the listing is younger than the window.</summary>
    InsufficientHistory,

    /// <summary>The nearest earlier price is further before the window's start than allowed: a hole in the history, not a short history.</summary>
    HistoryGap,

    /// <summary>The one-day window needs the market's calendar to know the previous trading day, and the market has none (yet).</summary>
    CalendarUnavailable,
}

/// <summary>An adjusted close on a day.</summary>
public sealed record PriceSample
{
    private PriceSample(DateOnly date, decimal adjustedClose)
    {
        Date = date;
        AdjustedClose = adjustedClose;
    }

    /// <summary>The trading day.</summary>
    public DateOnly Date { get; }

    /// <summary>The close adjusted for splits and dividends, in the market's native currency. Always positive.</summary>
    public decimal AdjustedClose { get; }

    /// <summary>Creates a sample; a close that is not positive cannot be a price.</summary>
    public static Result<PriceSample> Create(DateOnly date, decimal adjustedClose) =>
        adjustedClose > 0
            ? new PriceSample(date, adjustedClose)
            : Error.Validation("marketdata.performance.non_positive_close", $"The adjusted close of {date:yyyy-MM-dd} is not positive.");
}

/// <summary>
/// The outcome of one window: the change as a fraction (<c>0.0123</c> is 1.23 %) with the baseline it was measured from, or the reason there is
/// none. Exactly one of the two is present.
/// </summary>
public sealed record WindowPerformance
{
    private WindowPerformance(PerformanceWindow window, decimal? change, PriceSample? baseline, PerformanceGap? gap)
    {
        Window = window;
        Change = change;
        Baseline = baseline;
        Gap = gap;
    }

    /// <summary>The window.</summary>
    public PerformanceWindow Window { get; }

    /// <summary>Newest close divided by the baseline close, minus one; <see langword="null"/> when there is a <see cref="Gap"/>.</summary>
    public decimal? Change { get; }

    /// <summary>The close the change is measured from; <see langword="null"/> when there is a <see cref="Gap"/>.</summary>
    public PriceSample? Baseline { get; }

    /// <summary>Why there is no change; <see langword="null"/> when there is one.</summary>
    public PerformanceGap? Gap { get; }

    /// <summary>A window with a change.</summary>
    public static WindowPerformance Of(PerformanceWindow window, decimal change, PriceSample baseline) => new(window, change, baseline, null);

    /// <summary>A window without a change and the reason.</summary>
    public static WindowPerformance Missing(PerformanceWindow window, PerformanceGap gap) => new(window, null, null, gap);
}

/// <summary>The newest price of a listing and its eight windows, always in the order of <see cref="PerformanceWindow"/>.</summary>
/// <param name="Latest">The newest stored adjusted close, or <see langword="null"/> when the listing has none.</param>
/// <param name="Windows">One entry per window.</param>
public sealed record PerformanceReport(PriceSample? Latest, IReadOnlyList<WindowPerformance> Windows);
