using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Ingestion;

/// <summary>A listing of the catalog as ingestion needs it.</summary>
/// <param name="Id">Catalog id.</param>
/// <param name="Market">Canonical market of the listing.</param>
/// <param name="Ticker">Ticker on that market.</param>
/// <param name="IsActive">False once the instrument is delisted.</param>
public sealed record CatalogStock(StockId Id, Mic Market, Ticker Ticker, bool IsActive);

/// <summary>Read access to the shared catalog for the worker.</summary>
public interface IStockCatalog
{
    /// <summary>The listing with this id, or <see langword="null"/> when it is not in the catalog.</summary>
    Task<CatalogStock?> FindAsync(StockId id, CancellationToken cancellationToken);

    /// <summary>The listing of <paramref name="market"/> and <paramref name="ticker"/> with everything a caller shows, or <see langword="null"/> when the catalog does not have it.</summary>
    Task<CatalogListing?> FindByKeyAsync(Mic market, Ticker ticker, CancellationToken cancellationToken);

    /// <summary>The listings with these ids that the catalog has.</summary>
    Task<IReadOnlyList<CatalogListing>> FindManyAsync(IReadOnlyCollection<StockId> ids, CancellationToken cancellationToken);

    /// <summary>The active listings of a market that ingestion has not given up on (those whose ingestion did not fail for good).</summary>
    Task<IReadOnlyList<StockId>> ListIngestableAsync(Mic market, CancellationToken cancellationToken);
}

/// <summary>How a provider names a catalog listing (<c>symbol_aliases</c>).</summary>
public interface ISymbolAliases
{
    /// <summary>The provider's own market code and symbol for the listing, or <see langword="null"/> when none was recorded.</summary>
    Task<ProviderListingKey?> FindAsync(StockId stock, string provider, CancellationToken cancellationToken);
}

/// <summary>Where each listing stands in ingestion: when it last worked, and why it stopped when it did.</summary>
public interface IIngestionState
{
    /// <summary>Records a completed ingestion and clears any earlier failure.</summary>
    Task RecordSuccessAsync(StockId stock, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failure with its stable code. A <paramref name="permanent"/> failure is one retrying cannot fix (unknown symbol, plan, missing
    /// alias or calendar); the listing is not tried again until someone changes the cause.
    /// </summary>
    Task RecordFailureAsync(StockId stock, string code, bool permanent, DateTimeOffset at, CancellationToken cancellationToken);
}
