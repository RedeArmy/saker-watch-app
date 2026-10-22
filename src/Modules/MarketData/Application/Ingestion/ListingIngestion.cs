using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Ingestion;

/// <summary>Everything ingestion needs to talk about one listing: the catalog entry, its provider, the provider's name for it and its calendar.</summary>
/// <param name="Stock">The catalog entry.</param>
/// <param name="Provider">The provider that serves its market.</param>
/// <param name="Key">The provider's own market code and symbol for the listing.</param>
/// <param name="Calendar">The calendar of its market.</param>
public sealed record ResolvedListing(CatalogStock Stock, IMarketDataProvider Provider, ProviderListingKey Key, IExchangeCalendar Calendar);

/// <summary>
/// The steps the backfill and the daily refresh share: resolve a listing to what is needed to fetch it, fetch the adjusted and the unadjusted series
/// and merge them, and tell a failure that retrying can fix from one that it cannot.
/// </summary>
public sealed class ListingIngestion(
    IStockCatalog catalog,
    ISymbolAliases aliases,
    IMarketDataProviderRouter router,
    ExchangeCalendars calendars,
    IIngestionState state,
    IOptions<IngestionOptions> options)
{
    /// <summary>Code returned when the listing is not in the catalog or is no longer active: there is nothing to ingest.</summary>
    public const string StockMissingCode = "marketdata.ingestion.stock_missing";

    /// <summary>Code recorded when the market has no provider configured.</summary>
    public const string MarketDisabledCode = "market_disabled";

    /// <summary>Code recorded when no provider symbol was recorded for the listing.</summary>
    public const string NoAliasCode = "no_symbol_alias";

    /// <summary>Code recorded when the market has no calendar yet.</summary>
    public const string NoCalendarCode = "no_calendar";

    /// <summary>Resolves a listing, or fails with the code of what is missing.</summary>
    public async Task<Result<ResolvedListing>> ResolveAsync(StockId id, CancellationToken cancellationToken)
    {
        if (await catalog.FindAsync(id, cancellationToken) is not { IsActive: true } stock)
        {
            return Error.NotFound(StockMissingCode, "The listing is not in the catalog or is no longer active.");
        }

        Result<IMarketDataProvider> provider = router.For(stock.Market);
        if (!provider.IsSuccess)
        {
            return Error.Failure(MarketDisabledCode, provider.Error!.Message);
        }

        ProviderListingKey? key = await aliases.FindAsync(id, provider.Value.Name, cancellationToken);
        if (key is null)
        {
            return Error.Failure(NoAliasCode, "No provider symbol was recorded for the listing.");
        }

        Result<IExchangeCalendar> calendar = calendars.TryGet(stock.Market);
        return calendar.IsSuccess
            ? new ResolvedListing(stock, provider.Value, key, calendar.Value)
            : Error.Failure(NoCalendarCode, calendar.Error!.Message);
    }

    /// <summary>
    /// Fetches the adjusted and the unadjusted daily series from <paramref name="from"/> (two requests, two credits) and merges them by date.
    /// The unadjusted one is not asked for when the adjusted one failed.
    /// </summary>
    public async Task<Result<MergedPrices>> FetchAsync(ResolvedListing listing, DateOnly from, DateTimeOffset now, CallLane lane, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listing);

        Result<DailySeries> adjusted = await listing.Provider.GetDailyAsync(new DailySeriesRequest(listing.Key, from, null, Adjusted: true, lane), cancellationToken);
        if (!adjusted.IsSuccess)
        {
            return adjusted.Error!;
        }

        Result<DailySeries> raw = await listing.Provider.GetDailyAsync(new DailySeriesRequest(listing.Key, from, null, Adjusted: false, lane), cancellationToken);
        return raw.IsSuccess
            ? PriceSeriesMerger.Merge(raw.Value, adjusted.Value, listing.Calendar, now, options.Value.SettleDelay)
            : raw.Error!;
    }

    /// <summary>True for a failure that retrying later may fix: a rate limit or an outage.</summary>
    public static bool IsRetryable(Error error) =>
        ProviderErrors.Classify(error) is ProviderFailureKind.RateLimited or ProviderFailureKind.Transient;

    /// <summary>Records a completed ingestion and clears any earlier failure and any count of days the provider did not know the symbol.</summary>
    public Task RecordSuccessAsync(StockId id, DateTimeOffset now, CancellationToken cancellationToken) =>
        state.RecordSuccessAsync(id, now, cancellationToken);

    /// <summary>Records a failure that retrying cannot fix against the listing, with its stable code.</summary>
    public Task RecordPermanentFailureAsync(StockId id, string code, DateTimeOffset now, CancellationToken cancellationToken) =>
        state.RecordFailureAsync(id, code, permanent: true, now, cancellationToken);
}
