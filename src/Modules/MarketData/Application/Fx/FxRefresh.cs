using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Monetary;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.BuildingBlocks.Kernel.Telemetry;
using SakerWatch.MarketData.Application.Ingestion;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Fx;
using SakerWatch.MarketData.Domain.Markets;

namespace SakerWatch.MarketData.Application.Fx;

/// <summary>The daily refresh of one exchange rate pair. Keyed by pair and day (a Guatemalan date), so the scheduler may ask any number of times.</summary>
/// <param name="Base">The currency being priced.</param>
/// <param name="Quote">The currency the price is in.</param>
/// <param name="Day">The day in Guatemala the refresh is for: the last day asked of the bank.</param>
[JobType("marketdata.fx-refresh.v1")]
public sealed record FxRefreshJob(string Base, string Quote, DateOnly Day)
{
    /// <summary>The deterministic job key of a pair's refresh for a day.</summary>
    public static string KeyFor(CurrencyCode baseCurrency, CurrencyCode quote, DateOnly day) => $"marketdata.fx-refresh:{baseCurrency.Value}:{quote.Value}:{day:yyyyMMdd}";
}

/// <summary>
/// Loads the rates of a pair. The first run (nothing stored) asks for ten years in one call; later runs ask for the last few days before the
/// newest stored one, which catches a correction, up to the day of the job. The upsert makes a repeat harmless. A failure that retrying can fix
/// throws, so the queue retries it with backoff; one that cannot is logged and the job ends, because the next day asks again (ADR-0028).
/// </summary>
public sealed partial class FxRefreshHandler(
    IFxRateProvider provider,
    IFxRateStore store,
    IOptions<IngestionOptions> options,
    TimeProvider clock,
    ILogger<FxRefreshHandler> logger) : IJobHandler<FxRefreshJob>
{
    private static readonly Counter<long> _refreshes = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_fx_refreshes_total",
        description: "Daily exchange rate refreshes by outcome (completed, retry, failed).");

    private static readonly Counter<long> _rows = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_fx_rows_total",
        description: "Daily exchange rates written by the refresh.");

    /// <inheritdoc />
    public async Task HandleAsync(FxRefreshJob payload, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Result<CurrencyCode> baseCurrency = CurrencyCode.Create(payload.Base);
        Result<CurrencyCode> quote = CurrencyCode.Create(payload.Quote);
        if (!baseCurrency.IsSuccess || !quote.IsSuccess)
        {
            Count("failed");
            LogFailed(payload.Base, payload.Quote, baseCurrency.Error?.Code ?? quote.Error!.Code);
            return;
        }

        IngestionOptions settings = options.Value;
        DateOnly? latest = await store.LatestDateAsync(baseCurrency.Value, quote.Value, provider.Name, cancellationToken);
        DateOnly from = latest is { } stored ? stored.AddDays(-settings.FxOverlapDays) : payload.Day.AddYears(-settings.BackfillYears);
        if (from > payload.Day)
        {
            from = payload.Day;
        }

        Result<IReadOnlyList<FxRate>> rates = await provider.GetDailyAsync(baseCurrency.Value, quote.Value, from, payload.Day, cancellationToken);
        if (!rates.IsSuccess)
        {
            Stop(payload, rates.Error!);
            return;
        }

        int written = await store.UpsertAsync(rates.Value, clock.GetUtcNow(), cancellationToken);
        _rows.Add(written);
        Count("completed");
        LogRefreshed(payload.Base, payload.Quote, written);
    }

    private static void Count(string outcome) => _refreshes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    private void Stop(FxRefreshJob payload, Error error)
    {
        if (ProviderErrors.Classify(error) is ProviderFailureKind.RateLimited or ProviderFailureKind.Transient)
        {
            Count("retry");
            throw new IngestionRetryException(error.Code);
        }

        Count("failed");
        LogFailed(payload.Base, payload.Quote, error.Code);
    }

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information, Message = "Exchange rates {Base}/{Quote} refreshed: {Rows} rows written")]
    private partial void LogRefreshed(string @base, string quote, int rows);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Warning, Message = "Refresh of the exchange rates {Base}/{Quote} failed for good with {Code}; the next day asks again")]
    private partial void LogFailed(string @base, string quote, string code);
}
