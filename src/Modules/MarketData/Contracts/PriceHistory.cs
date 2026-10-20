using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Contracts;

/// <summary>The ranges a price chart can show (ADR-0038): the same windows as the performance numbers, plus all the history.</summary>
public static class PriceRanges
{
    /// <summary>The last seven days.</summary>
    public const string OneWeek = "1W";

    /// <summary>The last calendar month.</summary>
    public const string OneMonth = "1M";

    /// <summary>The last three calendar months.</summary>
    public const string ThreeMonths = "3M";

    /// <summary>The last six calendar months.</summary>
    public const string SixMonths = "6M";

    /// <summary>Since the last close of the previous year.</summary>
    public const string YearToDate = "YTD";

    /// <summary>The last calendar year.</summary>
    public const string OneYear = "1Y";

    /// <summary>The last five calendar years.</summary>
    public const string FiveYears = "5Y";

    /// <summary>Everything stored.</summary>
    public const string Max = "MAX";

    /// <summary>Every range, in the order a selector shows them.</summary>
    public static readonly IReadOnlyList<string> All = [OneWeek, OneMonth, ThreeMonths, SixMonths, YearToDate, OneYear, FiveYears, Max];
}

/// <summary>How the days of a series are grouped (ADR-0038). A week or a month is shown by its last trading day.</summary>
public static class PriceIntervals
{
    /// <summary>One point per range: days up to a year, weeks for five years, months for all the history.</summary>
    public const string Auto = "auto";

    /// <summary>One point per trading day.</summary>
    public const string Day = "day";

    /// <summary>One point per week: its last trading day.</summary>
    public const string Week = "week";

    /// <summary>One point per month: its last trading day.</summary>
    public const string Month = "month";

    /// <summary>Every interval a caller may ask for.</summary>
    public static readonly IReadOnlyList<string> All = [Auto, Day, Week, Month];
}

/// <summary>One stored day of a stock.</summary>
/// <param name="Date">The trading day.</param>
/// <param name="Close">The close as traded.</param>
/// <param name="AdjustedClose">The close adjusted for splits and dividends: the one a chart should draw, because it is continuous.</param>
/// <param name="IsFinal">False while the day may still change.</param>
public sealed record ChartPoint(DateOnly Date, decimal Close, decimal AdjustedClose, bool IsFinal);

/// <summary>The price history of a stock over a range.</summary>
/// <param name="Stock">The catalog stock.</param>
/// <param name="Range">The range, one of <see cref="PriceRanges.All"/>.</param>
/// <param name="Interval">The interval used: <c>day</c>, <c>week</c> or <c>month</c> (what <c>auto</c> resolved to).</param>
/// <param name="From">The first day the range covers, or <see langword="null"/> for all the history.</param>
/// <param name="To">The day of the newest stored price, or <see langword="null"/> when there is none.</param>
/// <param name="Points">The days, oldest first. Empty when the stock has no price in the range.</param>
/// <param name="PriceStatus">How current the prices are (see <see cref="PriceStatuses"/>).</param>
/// <param name="StaleSince">When the newest price went stale, when it did.</param>
public sealed record PriceSeries(
    StockSummary Stock,
    string Range,
    string Interval,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<ChartPoint> Points,
    string PriceStatus,
    DateTimeOffset? StaleSince);

/// <summary>Read access to stored prices for the modules that draw them: the stock detail and the watchlist sparklines.</summary>
public interface IPriceHistory
{
    /// <summary>
    /// The series of the catalog stock of <paramref name="market"/> and <paramref name="ticker"/> over <paramref name="range"/>. A stock with no price
    /// yet is an empty series with its status, not a failure; a stock the catalog does not have is not found; a range or an interval that is not
    /// one of the known values is a validation failure.
    /// </summary>
    Task<Result<PriceSeries>> SeriesAsync(string market, string ticker, string range, string interval, CancellationToken cancellationToken);

    /// <summary>
    /// The adjusted closes of the last <paramref name="days"/> stored trading days of each stock, oldest first, for many stocks in a few
    /// statements. A stock with no price is left out.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<decimal>>> RecentAdjustedClosesAsync(IReadOnlyCollection<Guid> stockIds, int days, CancellationToken cancellationToken);
}
