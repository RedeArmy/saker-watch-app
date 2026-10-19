using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Application.Fx;

/// <summary>Storage of the daily exchange rates of the shared catalog (ADR-0028). Writes are idempotent: the same day written twice is one row.</summary>
public interface IFxRateStore
{
    /// <summary>Inserts or replaces the rates (unique by pair, day and source). Returns how many rows were written.</summary>
    Task<int> UpsertAsync(IReadOnlyList<FxRate> rates, DateTimeOffset ingestedAt, CancellationToken cancellationToken);

    /// <summary>
    /// The rate of <paramref name="baseCurrency"/> in <paramref name="quote"/> in force on <paramref name="date"/>: the newest one published on or
    /// before it, with its own date, or <see langword="null"/> when there is none. If two sources published the same day, the source name that
    /// sorts first wins, so the answer is stable.
    /// </summary>
    Task<FxRate?> OnOrBeforeAsync(CurrencyCode baseCurrency, CurrencyCode quote, DateOnly date, CancellationToken cancellationToken);

    /// <summary>The newest day stored for a pair and source, or <see langword="null"/> when none is.</summary>
    Task<DateOnly?> LatestDateAsync(CurrencyCode baseCurrency, CurrencyCode quote, string source, CancellationToken cancellationToken);
}
