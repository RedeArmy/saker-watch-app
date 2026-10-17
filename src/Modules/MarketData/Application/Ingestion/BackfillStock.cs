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
    /// <summary>The deterministic job key of a listing's backfill.</summary>
    public static string KeyFor(StockId stock) => $"marketdata.backfill:{stock.Value:N}";
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
    IStockCatalog catalog,
    ISymbolAliases aliases,
    IMarketDataProviderRouter router,
    ExchangeCalendars calendars,
    IPriceStore prices,
    IIngestionState state,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<BackfillStockHandler> logger) : IJobHandler<BackfillStockJob>
{
    /// <summary>Code recorded when the market has no provider configured.</summary>
    public const string MarketDisabledCode = "market_disabled";

    /// <summary>Code recorded when no provider symbol was recorded for the listing.</summary>
    public const string NoAliasCode = "no_symbol_alias";

    /// <summary>Code recorded when the market has no calendar yet.</summary>
    public const string NoCalendarCode = "no_calendar";

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

        if (await catalog.FindAsync(id, cancellationToken) is not { IsActive: true } stock)
        {
            LogStockSkipped(payload.StockId);
            return;
        }

        string? refusal = null;
        Result<IMarketDataProvider> provider = router.For(stock.Market);
        ProviderListingKey? key = provider.IsSuccess ? await aliases.FindAsync(id, provider.Value.Name, cancellationToken) : null;
        Result<IExchangeCalendar> calendar = calendars.TryGet(stock.Market);
        if (!provider.IsSuccess)
        {
            refusal = MarketDisabledCode;
        }
        else if (key is null)
        {
            refusal = NoAliasCode;
        }
        else if (!calendar.IsSuccess)
        {
            refusal = NoCalendarCode;
        }

        if (refusal is not null)
        {
            await FailPermanentlyAsync(id, refusal, now, cancellationToken);
            return;
        }

        IngestionOptions settings = options.Value;
        DateOnly from = DateOnly.FromDateTime(now.UtcDateTime).AddYears(-settings.BackfillYears);
        Result<DailySeries> adjusted = await provider.Value.GetDailyAsync(new DailySeriesRequest(key!, from, null, Adjusted: true), cancellationToken);
        Result<DailySeries> raw = adjusted.IsSuccess
            ? await provider.Value.GetDailyAsync(new DailySeriesRequest(key!, from, null, Adjusted: false), cancellationToken)
            : adjusted;
        if (!adjusted.IsSuccess || !raw.IsSuccess)
        {
            await HandleProviderFailureAsync(id, (adjusted.IsSuccess ? raw : adjusted).Error!, now, cancellationToken);
            return;
        }

        MergedPrices merged = PriceSeriesMerger.Merge(raw.Value, adjusted.Value, calendar.Value, now, settings.SettleDelay);
        int written = await prices.UpsertAsync(id, provider.Value.Name, merged.Points, now, cancellationToken);
        await state.RecordSuccessAsync(id, now, cancellationToken);

        _backfills.Add(1, new KeyValuePair<string, object?>("outcome", "completed"));
        _rows.Add(written, new KeyValuePair<string, object?>("operation", "backfill"));
        LogBackfillCompleted(stock.Market.Value, stock.Ticker.Value, written, merged.Unmatched);
    }

    private async Task HandleProviderFailureAsync(StockId id, Error error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ProviderFailureKind kind = ProviderErrors.Classify(error);
        if (kind is ProviderFailureKind.RateLimited or ProviderFailureKind.Transient)
        {
            _backfills.Add(1, new KeyValuePair<string, object?>("outcome", "retry"));
            throw new IngestionRetryException(error.Code);
        }

        await FailPermanentlyAsync(id, error.Code, now, cancellationToken);
    }

    private async Task FailPermanentlyAsync(StockId id, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await state.RecordFailureAsync(id, code, permanent: true, now, cancellationToken);
        _backfills.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
        LogBackfillFailed(id.Value, code);
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "Backfill of {Market} {Ticker} wrote {Rows} daily rows ({Unmatched} days were in only one of the two series)")]
    private partial void LogBackfillCompleted(string market, string ticker, int rows, int unmatched);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Warning, Message = "Backfill of listing {StockId} failed for good with {Code}; it is not retried until the cause is fixed")]
    private partial void LogBackfillFailed(Guid stockId, string code);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Warning, Message = "Backfill skipped: listing {StockId} is not in the catalog or is no longer active")]
    private partial void LogStockSkipped(Guid stockId);
}
