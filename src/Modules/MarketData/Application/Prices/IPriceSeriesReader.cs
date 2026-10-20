using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Prices;

/// <summary>How the days of a series are grouped when it is read.</summary>
public enum SeriesBucket
{
    /// <summary>Every trading day.</summary>
    Day,

    /// <summary>The last trading day of each week.</summary>
    Week,

    /// <summary>The last trading day of each month.</summary>
    Month,
}

/// <summary>One stored day.</summary>
/// <param name="Date">The trading day.</param>
/// <param name="Close">The close as traded.</param>
/// <param name="AdjustedClose">The close adjusted for splits and dividends.</param>
/// <param name="IsFinal">False while the day may still change.</param>
public sealed record SeriesBar(DateOnly Date, decimal Close, decimal AdjustedClose, bool IsFinal);

/// <summary>Reads stored prices for charts (ADR-0038).</summary>
public interface IPriceSeriesReader
{
    /// <summary>The days of a stock from <paramref name="from"/> (all of them when <see langword="null"/>), oldest first, grouped by <paramref name="bucket"/>.</summary>
    Task<IReadOnlyList<SeriesBar>> ReadAsync(StockId stock, DateOnly? from, SeriesBucket bucket, CancellationToken cancellationToken);

    /// <summary>The adjusted closes of the last <paramref name="days"/> stored days of each stock, oldest first, in batches; a stock with no price is left out.</summary>
    Task<IReadOnlyDictionary<StockId, IReadOnlyList<decimal>>> RecentAdjustedAsync(IReadOnlyCollection<StockId> stocks, int days, CancellationToken cancellationToken);
}
