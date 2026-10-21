using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Contracts;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Performance;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Lookup;

/// <summary>
/// <see cref="IMarketSnapshots"/> over the catalog, the performance reader, the newest raw bars and the ingestion state: a handful of statements
/// for any number of stocks. Whether a price is current is decided by the one staleness rule (<see cref="Freshness"/>, ADR-0029); the windows come
/// from the one calculator (ADR-0030), so a list, a portfolio and a detail cannot disagree.
/// </summary>
public sealed class MarketSnapshots(
    IStockCatalog catalog,
    IPerformanceReader performance,
    ILatestBars bars,
    IIngestionStatuses statuses,
    ExchangeCalendars calendars,
    IOptions<IngestionOptions> ingestion,
    TimeProvider clock) : IMarketSnapshots
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, StockSummary>> FindManyAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stockIds);
        IReadOnlyList<CatalogListing> listings = await catalog.FindManyAsync(ToIds(stockIds), cancellationToken);
        return listings.ToDictionary(listing => listing.Id.Value, Summary);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, StockSnapshot>> SnapshotsAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stockIds);
        StockId[] ids = ToIds(stockIds);
        IReadOnlyList<CatalogListing> listings = await catalog.FindManyAsync(ids, cancellationToken);
        if (listings.Count == 0)
        {
            return new Dictionary<Guid, StockSnapshot>();
        }

        StockId[] found = [.. listings.Select(listing => listing.Id)];
        IReadOnlyDictionary<StockId, PerformanceReport> reports = await performance.ReadAsync(found, cancellationToken);
        IReadOnlyDictionary<Guid, StockPrice> prices = await PricesOfAsync(listings, found, cancellationToken);

        var snapshots = new Dictionary<Guid, StockSnapshot>(listings.Count);
        foreach (StockId id in found)
        {
            StockPrice price = prices[id.Value];
            snapshots[id.Value] = new StockSnapshot(price.Stock, price.PriceStatus, price.Price, price.StaleSince, [.. reports[id].Windows.Select(Window)], price.DelistedAt);
        }

        return snapshots;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, StockPrice>> PricesAsync(IReadOnlyCollection<Guid> stockIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stockIds);
        IReadOnlyList<CatalogListing> listings = await catalog.FindManyAsync(ToIds(stockIds), cancellationToken);
        return listings.Count == 0
            ? new Dictionary<Guid, StockPrice>()
            : await PricesOfAsync(listings, [.. listings.Select(listing => listing.Id)], cancellationToken);
    }

    // The one place a price and its status are decided, so a list and a calculation cannot disagree about either.
    private async Task<IReadOnlyDictionary<Guid, StockPrice>> PricesOfAsync(IReadOnlyList<CatalogListing> listings, StockId[] found, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<StockId, LatestBar> latest = await bars.ReadAsync(found, cancellationToken);
        IReadOnlySet<StockId> failed = await statuses.FailedAsync(found, cancellationToken);

        DateTimeOffset now = clock.GetUtcNow();
        TimeSpan settle = ingestion.Value.SettleDelay;
        var prices = new Dictionary<Guid, StockPrice>(listings.Count);
        foreach (CatalogListing listing in listings)
        {
            latest.TryGetValue(listing.Id, out LatestBar? bar);
            (string status, DateTimeOffset? staleSince) = PriceStatusRules.Of(calendars, listing.Market, bar, failed.Contains(listing.Id), listing.DelistedAt, now, settle);
            prices[listing.Id.Value] = new StockPrice(Summary(listing), status, bar is null ? null : new PriceSnapshot(bar.Close, bar.Date, bar.IsFinal), staleSince, listing.DelistedAt);
        }

        return prices;
    }

    private static StockId[] ToIds(IReadOnlyCollection<Guid> ids) => [.. ids.Distinct().Select(StockId.Create)];

    private static StockSummary Summary(CatalogListing listing) =>
        new(listing.Id.Value, listing.Market.Value, listing.Ticker.Value, listing.Name, listing.Currency.Value, listing.AssetType switch
        {
            AssetType.Equity => "equity",
            AssetType.Etf => "etf",
            _ => "other",
        });

    private static WindowSnapshot Window(WindowPerformance window) => new(Code(window.Window), window.Change?.Fraction, window.Baseline?.Date, window.Gap is { } gap ? Reason(gap) : null);

    private static string Code(PerformanceWindow window) => window switch
    {
        PerformanceWindow.OneDay => "1D",
        PerformanceWindow.OneWeek => "1W",
        PerformanceWindow.OneMonth => "1M",
        PerformanceWindow.ThreeMonths => "3M",
        PerformanceWindow.SixMonths => "6M",
        PerformanceWindow.YearToDate => "YTD",
        PerformanceWindow.OneYear => "1Y",
        _ => "5Y",
    };

    private static string Reason(PerformanceGap gap) => gap switch
    {
        PerformanceGap.NoData => "no_data",
        PerformanceGap.InsufficientHistory => "insufficient_history",
        PerformanceGap.HistoryGap => "history_gap",
        _ => "calendar_unavailable",
    };
}
