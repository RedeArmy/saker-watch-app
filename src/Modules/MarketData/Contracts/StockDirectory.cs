using SakerWatch.BuildingBlocks.Kernel.Results;

namespace SakerWatch.MarketData.Contracts;

/// <summary>A listing a user can add to a watchlist, as found at the provider and checked against what the catalog accepts.</summary>
/// <param name="Market">The canonical market code (<c>XNAS</c>).</param>
/// <param name="Ticker">The ticker on that market.</param>
/// <param name="Name">The instrument name.</param>
/// <param name="Currency">The trading currency.</param>
/// <param name="AssetType">The kind of instrument: <c>equity</c>, <c>etf</c> or <c>other</c>.</param>
public sealed record StockCandidate(string Market, string Ticker, string Name, string Currency, string AssetType);

/// <summary>A listing of the shared catalog.</summary>
/// <param name="Id">The catalog id.</param>
/// <param name="Market">The canonical market code.</param>
/// <param name="Ticker">The ticker on that market.</param>
/// <param name="Name">The instrument name.</param>
/// <param name="Currency">The trading currency.</param>
/// <param name="AssetType">The kind of instrument: <c>equity</c>, <c>etf</c> or <c>other</c>.</param>
public sealed record StockSummary(Guid Id, string Market, string Ticker, string Name, string Currency, string AssetType);

/// <summary>
/// The door other modules use to find stocks and to put one in the shared catalog (ADR-0032). Nothing a caller types ever reaches the
/// catalog: a stock is registered only from what the provider returned for the exact market and ticker.
/// </summary>
public interface IStockDirectory
{
    /// <summary>
    /// Finds listings matching <paramref name="query"/> in the enabled markets, exact ticker first. Failures: validation (a query that is not
    /// plain text), <c>TooManyRequests</c> when the shared search budget is spent, <c>Unavailable</c> when the provider cannot answer.
    /// </summary>
    Task<Result<IReadOnlyList<StockCandidate>>> SearchAsync(string query, CancellationToken cancellationToken);

    /// <summary>
    /// The catalog stock of <paramref name="market"/> and <paramref name="ticker"/> if the catalog has it. It never calls the provider and never
    /// registers anything; a stock the catalog does not have is a not-found failure.
    /// </summary>
    Task<Result<StockSummary>> FindAsync(string market, string ticker, CancellationToken cancellationToken);

    /// <summary>
    /// The catalog stock of <paramref name="market"/> and <paramref name="ticker"/>, registering it first (and queueing its history) when the
    /// catalog does not have it yet. An unknown market or ticker is a validation or not-found failure; a provider that cannot confirm the
    /// listing is <c>Unavailable</c> and nothing is registered.
    /// </summary>
    Task<Result<StockSummary>> EnsureAsync(string market, string ticker, CancellationToken cancellationToken);
}
