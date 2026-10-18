using System.ComponentModel.DataAnnotations;
using SakerWatch.MarketData.Domain.Performance;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Prices;

/// <summary>How the performance windows are read. Bound from <c>MarketData:Performance</c>.</summary>
public sealed class PerformanceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "MarketData:Performance";

    /// <summary>
    /// Days a baseline may lie before the start of a window (all but the one-day window, which needs the previous session) before the window
    /// is a <c>HistoryGap</c> instead of a number (ADR-0030).
    /// </summary>
    [Range(0, 60)]
    public int MaxGapDays { get; set; } = PerformanceCalculator.DefaultMaxGapDays;
}

/// <summary>
/// Reads the performance windows of many listings at once from the stored adjusted closes (ADR-0030). It is the only way the watchlist, the
/// portfolio metrics and the stock detail get their numbers, so they cannot disagree.
/// </summary>
public interface IPerformanceReader
{
    /// <summary>
    /// The newest price and the eight windows of every listing in <paramref name="stocks"/>. Every listing asked for is in the answer: one with no
    /// price (or not in the catalog) has <see cref="PerformanceGap.NoData"/> in every window.
    /// </summary>
    Task<IReadOnlyDictionary<StockId, PerformanceReport>> ReadAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken);
}
