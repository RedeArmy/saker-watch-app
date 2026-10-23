using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.MarketData.Application.Lifecycle;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Ingestion;

/// <summary>The daily refresh of one market for one trading day. Keyed by market and day, so the scheduler may ask any number of times (ADR-0029).</summary>
/// <param name="Market">The canonical market code.</param>
/// <param name="Day">The trading day whose session has settled.</param>
[JobType("marketdata.ingest-daily.v1")]
public sealed record IngestMarketDayJob(string Market, DateOnly Day)
{
    /// <summary>The deterministic job key of a market's refresh for a day.</summary>
    public static string KeyFor(Mic market, DateOnly day) => $"marketdata.ingest-daily:{market.Value}:{day:yyyyMMdd}";
}

/// <summary>The refresh of one listing for one trading day, queued by <see cref="IngestMarketDayJob"/>.</summary>
/// <param name="StockId">The catalog id of the listing.</param>
/// <param name="Day">The trading day.</param>
/// <param name="Waits">How many times this refresh has put itself back in the queue to wait for the provider's call budget.</param>
[JobType("marketdata.refresh.v1")]
public sealed record RefreshStockJob(Guid StockId, DateOnly Day, int Waits = 0)
{
    /// <summary>The deterministic job key of a listing's refresh for a day.</summary>
    public static string KeyFor(StockId stock, DateOnly day) => $"marketdata.refresh:{stock.Value:N}:{day:yyyyMMdd}";
}

/// <summary>
/// Fans a market's daily refresh out into one short job per listing, so no single transaction waits on hundreds of provider calls and one slow
/// listing cannot hold up the others. Queueing a listing that is already queued for the day does nothing.
/// </summary>
public sealed partial class IngestMarketDayHandler(IStockCatalog catalog, IJobEnqueuer jobs, ILogger<IngestMarketDayHandler> logger) : IJobHandler<IngestMarketDayJob>
{
    /// <inheritdoc />
    public async Task HandleAsync(IngestMarketDayJob payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var market = Mic.Parse(payload.Market);

        IReadOnlyList<StockId> stocks = await catalog.ListIngestableAsync(market, cancellationToken);
        int queued = 0;
        foreach (StockId stock in stocks)
        {
            if (await jobs.EnqueueAsync(RefreshStockJob.KeyFor(stock, payload.Day), new RefreshStockJob(stock.Value, payload.Day), options: null, cancellationToken))
            {
                queued++;
            }
        }

        string day = payload.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        LogFanOut(payload.Market, day, stocks.Count, queued);
    }

    [LoggerMessage(EventId = 2111, Level = LogLevel.Information, Message = "Daily refresh of {Market} for {Day}: {Listings} listings, {Queued} newly queued")]
    private partial void LogFanOut(string market, string day, int listings, int queued);
}

/// <summary>
/// Refreshes one listing: the last days again (two requests, two credits), merged and stored. A listing with no history yet is handed to the
/// backfill instead. If a final day already stored comes back with a different adjusted close, the provider rewrote the history (a dividend or a
/// split), so the listing is queued for a full reload.
/// </summary>
public sealed partial class RefreshStockHandler(
    ListingIngestion ingestion,
    IPriceStore prices,
    FollowUpJobs followUps,
    ListingLifecycle lifecycle,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<RefreshStockHandler> logger) : IJobHandler<RefreshStockJob>
{
    private static readonly Counter<long> _refreshes = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_refreshes_total",
        description: "Daily refreshes of a listing by outcome (completed, backfill_queued, reload_queued, waiting, retry, failed, provider_fault, not_found, delisted).");

    private static readonly Counter<long> _rows = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_price_rows_total",
        description: "Daily price rows written by ingestion, by operation.");

    /// <inheritdoc />
    public async Task HandleAsync(RefreshStockJob payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        DateTimeOffset now = clock.GetUtcNow();
        var id = StockId.Create(payload.StockId);
        IngestionOptions settings = options.Value;

        Result<ResolvedListing> listing = await ingestion.ResolveAsync(id, cancellationToken);
        if (!listing.IsSuccess)
        {
            await StopAsync(payload, context, listing.Error!, now, cancellationToken);
            return;
        }

        if (await prices.LatestAsync(id, cancellationToken) is not { } latest)
        {
            await followUps.QueueBackfillAsync(id, payload.Day, cancellationToken);
            Count("backfill_queued");
            return;
        }

        DateOnly from = latest.Date.AddDays(-settings.RefreshOverlapDays);
        Result<MergedPrices> merged = await ingestion.FetchAsync(listing.Value, from, now, CallLane.Scheduled, cancellationToken);
        if (!merged.IsSuccess)
        {
            await StopAsync(payload, context, merged.Error!, now, cancellationToken);
            return;
        }

        IReadOnlyDictionary<DateOnly, decimal> stored = await prices.FinalAdjustedClosesAsync(id, from, cancellationToken);
        bool rewritten = merged.Value.Points.Any(point => stored.TryGetValue(point.Raw.Date, out decimal old) && HasChanged(old, point.AdjustedClose, settings.RewriteTolerance));

        if (rewritten)
        {
            // Nothing is written: the last days at the new scale beside older rows at the old one would make every window wrong. The reload
            // replaces the whole series, and if it dies tomorrow's refresh sees the rewrite again and queues another.
            await followUps.QueueReloadAsync(id, payload.Day, cancellationToken);
            Count("reload_queued");
            LogHistoryRewritten(listing.Value.Stock.Market.Value, listing.Value.Stock.Ticker.Value);
            return;
        }

        int written = await prices.UpsertAsync(id, listing.Value.Provider.Name, merged.Value.Points, now, cancellationToken);
        await ingestion.RecordSuccessAsync(id, now, cancellationToken);
        _rows.Add(written, new KeyValuePair<string, object?>("operation", "refresh"));
        await followUps.QueueWarmAsync(listing.Value.Stock.Market, now, cancellationToken);
        Count("completed");
    }

    private static bool HasChanged(decimal old, decimal fresh, double tolerance) =>
        old > 0 && (double)(Math.Abs(fresh - old) / old) > tolerance;

    private static void Count(string outcome) => _refreshes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    private async Task StopAsync(RefreshStockJob payload, JobContext context, Error error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var id = StockId.Create(payload.StockId);
        if (error.Code == ListingIngestion.StockMissingCode)
        {
            return;
        }

        // An exhausted call budget is not a failure: the refresh waits in the queue until the budget has room again.
        if (ProviderErrors.Classify(error) == ProviderFailureKind.RateLimited
            && await followUps.TryWaitAsync(payload with { Waits = payload.Waits + 1 }, payload.Waits, context.JobKey, error, maxAttempts: 5, cancellationToken))
        {
            Count("waiting");
            return;
        }

        if (ListingIngestion.IsRetryable(error))
        {
            Count("retry");
            throw new IngestionRetryException(error.Code);
        }

        // A listing that had prices and that the provider no longer knows is not failed for good on the first day: the provider may have had a bad
        // day. It is counted, and it is delisted when the days in a row reach the limit (ADR-0039); the next daily refresh tries again.
        if (ProviderErrors.Classify(error) == ProviderFailureKind.SymbolNotFound)
        {
            Count(await lifecycle.NoteSymbolNotFoundAsync(id, payload.Day, cancellationToken) ? "delisted" : "not_found");
            return;
        }

        // A refused key or a plan that lacks the market hits every listing: the listing stays as it is and tomorrow's refresh asks again.
        if (ListingIngestion.IsProviderFault(error))
        {
            Count("provider_fault");
            LogProviderFault(id.Value, error.Code);
            return;
        }

        await ingestion.RecordPermanentFailureAsync(id, error.Code, now, cancellationToken);
        Count("failed");
        LogRefreshFailed(id.Value, error.Code);
    }

    [LoggerMessage(EventId = 2112, Level = LogLevel.Information, Message = "The adjusted history of {Market} {Ticker} was rewritten by the provider; a full reload is queued")]
    private partial void LogHistoryRewritten(string market, string ticker);

    [LoggerMessage(EventId = 2114, Level = LogLevel.Warning, Message = "Refresh of listing {StockId} met a provider fault ({Code}); the listing is not marked and the next refresh asks again")]
    private partial void LogProviderFault(Guid stockId, string code);

    [LoggerMessage(EventId = 2113, Level = LogLevel.Warning, Message = "Refresh of listing {StockId} failed for good with {Code}")]
    private partial void LogRefreshFailed(Guid stockId, string code);
}
