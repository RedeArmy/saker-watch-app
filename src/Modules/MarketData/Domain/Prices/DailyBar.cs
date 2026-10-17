using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Domain.Prices;

/// <summary>
/// One trading day of a listing in its native currency. Provider data is untrusted, so a bar only exists if it is internally consistent:
/// positive prices, the high is the highest and the low the lowest of the day, and the volume is not negative. A bar that fails is refused
/// with the reason instead of being repaired.
/// </summary>
public sealed record DailyBar
{
    private DailyBar(DateOnly date, decimal open, decimal high, decimal low, decimal close, long volume)
    {
        Date = date;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
    }

    /// <summary>The trading day, in the market's own time zone.</summary>
    public DateOnly Date { get; }

    /// <summary>First price of the session.</summary>
    public decimal Open { get; }

    /// <summary>Highest price of the session.</summary>
    public decimal High { get; }

    /// <summary>Lowest price of the session.</summary>
    public decimal Low { get; }

    /// <summary>Last price of the session. Adjusted or not depends on the series that holds the bar.</summary>
    public decimal Close { get; }

    /// <summary>Shares traded. Informational only: some feeds cover a fraction of the market's volume.</summary>
    public long Volume { get; }

    /// <summary>Validates the values and creates the bar.</summary>
    public static Result<DailyBar> Create(DateOnly date, decimal open, decimal high, decimal low, decimal close, long volume)
    {
        if (open <= 0 || high <= 0 || low <= 0 || close <= 0)
        {
            return Error.Validation("marketdata.bar.non_positive_price", $"The bar of {date:yyyy-MM-dd} has a price that is not positive.");
        }

        if (high < Math.Max(Math.Max(open, close), low) || low > Math.Min(open, close))
        {
            return Error.Validation("marketdata.bar.inconsistent_range", $"The bar of {date:yyyy-MM-dd} has a high below, or a low above, its open or close.");
        }

        return volume < 0
            ? Error.Validation("marketdata.bar.negative_volume", $"The bar of {date:yyyy-MM-dd} has a negative volume.")
            : new DailyBar(date, open, high, low, close, volume);
    }
}
