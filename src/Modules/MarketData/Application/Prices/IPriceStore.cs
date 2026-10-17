using SakerWatch.MarketData.Domain.Prices;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Prices;

/// <summary>The newest day stored for a listing.</summary>
/// <param name="Date">The trading day.</param>
/// <param name="IsFinal">Whether that day is final or may still change.</param>
public sealed record LatestPrice(DateOnly Date, bool IsFinal);

/// <summary>
/// Storage of the daily prices of the shared catalog (ADR-0029). Writes are idempotent: the same day written twice is one row, and a final row
/// is never replaced by a provisional one.
/// </summary>
public interface IPriceStore
{
    /// <summary>
    /// Inserts or replaces the days of <paramref name="stock"/>. A day already stored as final is left alone unless the new point is final too.
    /// Returns how many rows were written.
    /// </summary>
    Task<int> UpsertAsync(StockId stock, string source, IReadOnlyList<PricePoint> points, DateTimeOffset ingestedAt, CancellationToken cancellationToken);

    /// <summary>The newest day stored for <paramref name="stock"/>, or <see langword="null"/> when none is.</summary>
    Task<LatestPrice?> LatestAsync(StockId stock, CancellationToken cancellationToken);

    /// <summary>The adjusted close stored for one day, or <see langword="null"/> when that day is not stored.</summary>
    Task<decimal?> AdjustedCloseAsync(StockId stock, DateOnly date, CancellationToken cancellationToken);
}
