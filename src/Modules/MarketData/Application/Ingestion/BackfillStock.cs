using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Calendars;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Ingestion;

/// <summary>Loads the history of one listing. One job per listing, keyed by the listing, so asking twice queues it once (ADR-0029).</summary>
/// <param name="StockId">The catalog id of the listing.</param>
[JobType("marketdata.backfill.v1")]
public sealed record BackfillStockJob(Guid StockId)
{
    /// <summary>The deterministic job key of a listing's first backfill.</summary>
    public static string KeyFor(StockId stock) => $"marketdata.backfill:{stock.Value:N}";

    /// <summary>
    /// The job key of a full reload because the provider rewrote the adjusted history (a dividend or a split). It carries the day, so a listing
    /// is reloaded at most once a day and the earlier, finished backfill does not block it.
    /// </summary>
    public static string ReloadKeyFor(StockId stock, DateOnly day) => $"marketdata.reload:{stock.Value:N}:{day:yyyyMMdd}";
}

/// <summary>A failure that retrying later may fix (a rate limit, an outage): the queue retries the job with backoff.</summary>
/// <param name="code">The stable code of the provider failure.</param>
public sealed class IngestionRetryException(string code) : Exception($"The provider answered '{code}'; the backfill will be retried.")
{
    /// <summary>The stable code of the provider failure.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Backfills one listing: the adjusted and the unadjusted daily series (two requests, two credits), merged by date and stored in one statement.
/// A failure that retrying can fix throws, so the queue retries it; one that cannot (unknown symbol, plan, missing alias or calendar) is recorded
/// against the listing and the job ends, because retrying it would only spend credits.
/// </summary>
public sealed partial class BackfillStockHandler(
    ListingIngestion ingestion,
    IPriceStore prices,
    IIngestionState state,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<BackfillStockHandler> logger) : IJobHandler<BackfillStockJob>
{
    /// <summary>Code recorded when the market has no provider configured.</summary>
    public const string MarketDisabledCode = ListingIngestion.MarketDisabledCode;

    /// <summary>Code recorded when no provider symbol was recorded for the listing.</summary>
    public const string NoAliasCode = ListingIngestion.NoAliasCode;

    /// <summary>Code recorded when the market has no calendar yet.</summary>
    public const string NoCalendarCode = ListingIngestion.NoCalendarCode;

    private static readonly Counter<long> _backfills = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_backfills_total",
        description: "Backfills of a listing by outcome (completed, retry, failed).");

    private static readonly Counter<long> _rows = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_price_rows_total",
        description: "Daily price rows written by ingestion, by operation.");

    /// <inheritdoc />
    public async Task HandleAsync(BackfillStockJob payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        DateTimeOffset now = clock.GetUtcNow();
        var id = StockId.Create(payload.StockId);

        Result<ResolvedListing> listing = await ingestion.ResolveAsync(id, cancellationToken);
        if (!listing.IsSuccess)
        {
            await StopAsync(id, listing.Error!, now, cancellationToken);
            return;
        }

        DateOnly from = DateOnly.FromDateTime(now.UtcDateTime).AddYears(-options.Value.BackfillYears);
        Result<MergedPrices> merged = await ingestion.FetchAsync(listing.Value, from, now, cancellationToken);
        if (!merged.IsSuccess)
        {
            await StopAsync(id, merged.Error!, now, cancellationToken);
            return;
        }

        int written = await prices.UpsertAsync(id, listing.Value.Provider.Name, merged.Value.Points, now, cancellationToken);
        await state.RecordSuccessAsync(id, now, cancellationToken);

        _backfills.Add(1, new KeyValuePair<string, object?>("outcome", "completed"));
        _rows.Add(written, new KeyValuePair<string, object?>("operation", "backfill"));
        LogBackfillCompleted(listing.Value.Stock.Market.Value, listing.Value.Stock.Ticker.Value, written, merged.Value.Unmatched);
    }

    // A listing that is gone is skipped; a failure that retrying can fix throws so the queue retries; the rest is recorded and the job ends.
    private async Task StopAsync(StockId id, Error error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (error.Code == ListingIngestion.StockMissingCode)
        {
            LogStockSkipped(id.Value);
            return;
        }

        if (ListingIngestion.IsRetryable(error))
        {
            _backfills.Add(1, new KeyValuePair<string, object?>("outcome", "retry"));
            throw new IngestionRetryException(error.Code);
        }

        await ingestion.RecordPermanentFailureAsync(id, error.Code, now, cancellationToken);
        _backfills.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
        LogBackfillFailed(id.Value, error.Code);
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Backfill of {Market} {Ticker} wrote {Rows} daily rows ({Unmatched} days were in only one of the two series)")]
    private partial void LogBackfillCompleted(string market, string ticker, int rows, int unmatched);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Warning, Message = "Backfill of listing {StockId} failed for good with {Code}; it is not retried until the cause is fixed")]
    private partial void LogBackfillFailed(Guid stockId, string code);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning, Message = "Backfill skipped: listing {StockId} is not in the catalog or is no longer active")]
    private partial void LogStockSkipped(Guid stockId);
}
