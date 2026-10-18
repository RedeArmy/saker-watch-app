using System.ComponentModel.DataAnnotations;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Lookup;

/// <summary>How stocks are searched and registered on demand. Bound from <c>MarketData:Lookup</c> (ADR-0032).</summary>
public sealed class StockLookupOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "MarketData:Lookup";

    /// <summary>Provider searches per minute, shared by every user and every API replica. Searches cost no credit, but they are not unlimited.</summary>
    [Range(1, 10_000)]
    public int SearchesPerMinute { get; set; } = 30;

    /// <summary>Provider searches per UTC day, shared by every user and every API replica.</summary>
    [Range(1, 1_000_000)]
    public int SearchesPerDay { get; set; } = 2000;

    /// <summary>Minutes an answer of the provider is kept, so typing the same query again does not spend a search.</summary>
    [Range(0, 1440)]
    public int CacheMinutes { get; set; } = 10;

    /// <summary>Most candidates a search returns.</summary>
    [Range(1, 50)]
    public int MaxResults { get; set; } = 20;
}

/// <summary>A listing found at a provider, kept as the cache stores it: the provider's own code and symbol plus the text it reported.</summary>
/// <param name="ProviderMic">The market code as the provider writes it.</param>
/// <param name="Symbol">The symbol as the provider writes it.</param>
/// <param name="Name">The instrument name.</param>
/// <param name="Currency">The trading currency.</param>
/// <param name="AssetType">The kind of instrument.</param>
public sealed record CachedListing(string ProviderMic, string Symbol, string? Name, string? Currency, AssetType AssetType);

/// <summary>A short-lived copy of what a provider answered to a search. A miss, a failure or a stale entry only costs a search; it never fails a request.</summary>
public interface ISearchCache
{
    /// <summary>The cached listings for <paramref name="query"/> at <paramref name="provider"/>, or <see langword="null"/> when there are none.</summary>
    Task<IReadOnlyList<CachedListing>?> GetAsync(string provider, string query, CancellationToken cancellationToken);

    /// <summary>Keeps the listings for <paramref name="ttl"/>.</summary>
    Task SetAsync(string provider, string query, IReadOnlyList<CachedListing> listings, TimeSpan ttl, CancellationToken cancellationToken);
}

/// <summary>A listing of the catalog with everything a caller shows.</summary>
/// <param name="Id">Catalog id.</param>
/// <param name="Market">Canonical market.</param>
/// <param name="Ticker">Ticker on that market.</param>
/// <param name="Name">Instrument name.</param>
/// <param name="Currency">Trading currency.</param>
/// <param name="AssetType">Kind of instrument.</param>
/// <param name="IsActive">False once the instrument is delisted.</param>
public sealed record CatalogListing(StockId Id, Mic Market, Ticker Ticker, string Name, CurrencyCode Currency, AssetType AssetType, bool IsActive);

/// <summary>
/// Puts a validated stock in the shared catalog. The implementation is idempotent on <c>(market, ticker)</c> and never changes a stock that
/// already exists: it returns the id of whichever row holds the key.
/// </summary>
public interface IStockRegistry
{
    /// <summary>Registers <paramref name="stock"/>, the provider's name for it and its pending ingestion state; returns the catalog id.</summary>
    Task<StockId> RegisterAsync(Stock stock, string provider, ProviderListingKey providerKey, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>The newest stored bar of a listing, as traded.</summary>
/// <param name="Date">The trading day.</param>
/// <param name="Close">The close as traded.</param>
/// <param name="IsFinal">False while the day may still change.</param>
public sealed record LatestBar(DateOnly Date, decimal Close, bool IsFinal);

/// <summary>Reads the newest raw bar of many listings at once.</summary>
public interface ILatestBars
{
    /// <summary>The newest bar of each listing that has one; the others are left out.</summary>
    Task<IReadOnlyDictionary<StockId, LatestBar>> ReadAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken);
}

/// <summary>Where ingestion stands for many listings at once.</summary>
public interface IIngestionStatuses
{
    /// <summary>The ids, among <paramref name="stocks"/>, whose ingestion failed for good (a permanent failure is recorded for them).</summary>
    Task<IReadOnlySet<StockId>> FailedAsync(IReadOnlyCollection<StockId> stocks, CancellationToken cancellationToken);
}
