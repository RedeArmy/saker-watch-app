using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Lookup;
using SakerWatch.MarketData.Contracts;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Prices;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Prices;

/// <summary>
/// <see cref="IPriceHistory"/> over the catalog and the stored days (ADR-0038). The range starts where the performance window of the same name
/// does, the points are the days (or the last day of each week or month) and the status of the prices is the one the watchlist shows.
/// </summary>
public sealed class PriceHistory(
    IStockCatalog catalog,
    IPriceSeriesReader reader,
    ILatestBars bars,
    IIngestionStatuses statuses,
    ExchangeCalendars calendars,
    IOptions<IngestionOptions> ingestion,
    TimeProvider clock) : IPriceHistory
{
    /// <summary>Code of the failure for a range that is not one of the known ones.</summary>
    public const string RangeInvalidCode = "marketdata.prices.range_invalid";

    /// <summary>Code of the failure for an interval that is not one of the known ones.</summary>
    public const string IntervalInvalidCode = "marketdata.prices.interval_invalid";

    /// <summary>Code of the failure for a stock the catalog does not have.</summary>
    public const string StockNotFoundCode = "marketdata.stock.not_found";

    /// <summary>Largest number of days a sparkline may ask for.</summary>
    public const int MaxRecentDays = 250;

    /// <inheritdoc />
    public async Task<Result<PriceSeries>> SeriesAsync(string market, string ticker, string range, string interval, CancellationToken cancellationToken)
    {
        string wantedRange = (range ?? string.Empty).Trim().ToUpperInvariant();
        string wantedInterval = (interval ?? string.Empty).Trim().ToLowerInvariant();
        if (!PriceRanges.All.Contains(wantedRange, StringComparer.Ordinal))
        {
            return Error.Validation(RangeInvalidCode, $"The range is one of: {string.Join(", ", PriceRanges.All)}.");
        }

        if (!PriceIntervals.All.Contains(wantedInterval, StringComparer.Ordinal))
        {
            return Error.Validation(IntervalInvalidCode, $"The interval is one of: {string.Join(", ", PriceIntervals.All)}.");
        }

        Result<Mic> mic = Mic.Create(market);
        Result<Ticker> symbol = Ticker.Create(ticker);
        if (!mic.IsSuccess || !symbol.IsSuccess)
        {
            return (mic.IsSuccess ? symbol.Error : mic.Error)!;
        }

        if (await catalog.FindByKeyAsync(mic.Value, symbol.Value, cancellationToken) is not { } listing)
        {
            return Error.NotFound(StockNotFoundCode, $"{symbol.Value} is not in the catalog of {mic.Value}.");
        }

        StockId[] one = [listing.Id];
        IReadOnlyDictionary<StockId, LatestBar> latest = await bars.ReadAsync(one, cancellationToken);
        IReadOnlySet<StockId> failed = await statuses.FailedAsync(one, cancellationToken);
        latest.TryGetValue(listing.Id, out LatestBar? newest);
        (string status, DateTimeOffset? staleSince) = PriceStatusRules.Of(calendars, listing.Market, newest, failed.Contains(listing.Id), listing.DelistedAt, clock.GetUtcNow(), ingestion.Value.SettleDelay);

        string used = wantedInterval == PriceIntervals.Auto ? PriceRange.DefaultInterval(wantedRange) : wantedInterval;
        StockSummary stock = new(listing.Id.Value, listing.Market.Value, listing.Ticker.Value, listing.Name, listing.Currency.Value, AssetTypeText(listing.AssetType));
        if (newest is null)
        {
            return new PriceSeries(stock, wantedRange, used, null, null, [], status, staleSince, listing.DelistedAt);
        }

        DateOnly? from = PriceRange.StartOf(wantedRange, newest.Date);
        IReadOnlyList<SeriesBar> days = await reader.ReadAsync(listing.Id, from, BucketOf(used), cancellationToken);
        return new PriceSeries(
            stock,
            wantedRange,
            used,
            from,
            newest.Date,
            [.. days.Select(day => new ChartPoint(day.Date, day.Close, day.AdjustedClose, day.IsFinal))],
            status,
            staleSince,
            listing.DelistedAt);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<decimal>>> RecentAdjustedClosesAsync(IReadOnlyCollection<Guid> stockIds, int days, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stockIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(days, MaxRecentDays);

        IReadOnlyDictionary<StockId, IReadOnlyList<decimal>> recent = await reader.RecentAdjustedAsync([.. stockIds.Distinct().Select(StockId.Create)], days, cancellationToken);
        return recent.ToDictionary(pair => pair.Key.Value, pair => pair.Value);
    }

    private static SeriesBucket BucketOf(string interval) => interval switch
    {
        PriceIntervals.Week => SeriesBucket.Week,
        PriceIntervals.Month => SeriesBucket.Month,
        _ => SeriesBucket.Day,
    };

    private static string AssetTypeText(AssetType type) => type switch
    {
        AssetType.Equity => "equity",
        AssetType.Etf => "etf",
        _ => "other",
    };
}
