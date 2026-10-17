using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Domain.Prices;

/// <summary>
/// One trading day as the product keeps it: the raw bar as traded, the close adjusted for splits and dividends, and whether the day is final.
/// The raw bar never changes once the day is final; the adjusted close is rewritten backwards whenever a dividend or a split is paid, which
/// is why the two are stored side by side (ADR-0029).
/// </summary>
public sealed record PricePoint
{
    private PricePoint(DailyBar raw, decimal adjustedClose, bool isFinal)
    {
        Raw = raw;
        AdjustedClose = adjustedClose;
        IsFinal = isFinal;
    }

    /// <summary>The bar as traded, in the market's native currency.</summary>
    public DailyBar Raw { get; }

    /// <summary>The close adjusted for splits and dividends, as of the last time the history was ingested.</summary>
    public decimal AdjustedClose { get; }

    /// <summary>False for the bar of a session that may still be settling; a final row is never overwritten by a provisional one.</summary>
    public bool IsFinal { get; }

    /// <summary>Validates the adjusted close and creates the point.</summary>
    public static Result<PricePoint> Create(DailyBar raw, decimal adjustedClose, bool isFinal)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return adjustedClose > 0
            ? new PricePoint(raw, adjustedClose, isFinal)
            : Error.Validation("marketdata.price.non_positive_adjusted_close", $"The adjusted close of {raw.Date:yyyy-MM-dd} is not positive.");
    }
}
