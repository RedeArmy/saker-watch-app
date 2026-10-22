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
    IFxRateScreening screening,
    IOptions<IngestionOptions> options,
    IOptions<FxGuardOptions> guard,
    TimeProvider clock,
    ILogger<FxRefreshHandler> logger) : IJobHandler<FxRefreshJob>
{
    private static readonly Counter<long> _refreshes = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_fx_refreshes_total",
        description: "Daily exchange rate refreshes by outcome (completed, refused, retry, failed).");

    private static readonly Counter<long> _refused = SakerTelemetry.Meter.CreateCounter<long>(
        "sakerwatch_marketdata_fx_rejected_total",
        description: "Exchange rates kept apart instead of stored, by reason (out_of_band, jump, rewrite).");

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

        FxScreening vetted = await ScreenAsync(baseCurrency.Value, quote.Value, rates.Value, cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
        // The refused rates are kept first: if this step fails, the retry screens the same input again and nothing was half stored.
        if (vetted.Rejected.Count > 0)
        {
            await screening.RejectAsync(vetted.Rejected, now, cancellationToken);
            foreach (IGrouping<string, RejectedFxRate> reason in vetted.Rejected.GroupBy(rejected => rejected.Reason))
            {
                _refused.Add(reason.Count(), new KeyValuePair<string, object?>("reason", reason.Key));
            }

            LogRefused(payload.Base, payload.Quote, vetted.Rejected.Count, string.Join(',', vetted.Rejected.Select(rejected => rejected.Reason).Distinct()));
        }

        int written = await store.UpsertAsync(vetted.Accepted, now, cancellationToken);
        _rows.Add(written);

        // Nothing believed out of what the bank gave is not a refresh that completed: the rate in use stays the one of an earlier day.
        if (vetted.Accepted.Count == 0 && vetted.Rejected.Count > 0)
        {
            Count("refused");
            return;
        }

        Count("completed");
        LogRefreshed(payload.Base, payload.Quote, written);
    }

    // A rate the bank gives is believed only inside its band and within the daily limit of the days around it (ADR-0042). The rates already stored
    // for the days asked, and the week before them, are what it is compared with.
    private async Task<FxScreening> ScreenAsync(CurrencyCode baseCurrency, CurrencyCode quote, IReadOnlyList<FxRate> rates, CancellationToken cancellationToken)
    {
        if (rates.Count == 0)
        {
            return new FxScreening(rates, []);
        }

        DateOnly first = rates.Min(rate => rate.AsOfDate), last = rates.Max(rate => rate.AsOfDate);
        IReadOnlyList<FxRate> stored = await screening.StoredAsync(baseCurrency, quote, provider.Name, first.AddDays(-7), last, cancellationToken);
        return FxRateGuard.Screen(rates, stored, guard.Value.ToSettings());
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

    [LoggerMessage(EventId = 2303, Level = LogLevel.Warning, Message = "{Rows} exchange rates {Base}/{Quote} were not believed ({Reasons}) and were kept apart in marketdata.fx_rates_rejected; they are not used")]
    private partial void LogRefused(string @base, string quote, int rows, string reasons);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Warning, Message = "Refresh of the exchange rates {Base}/{Quote} failed for good with {Code}; the next day asks again")]
    private partial void LogFailed(string @base, string quote, string code);
}
