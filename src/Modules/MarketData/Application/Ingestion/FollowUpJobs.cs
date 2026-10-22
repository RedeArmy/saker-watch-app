using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.BuildingBlocks.Kernel.Results;
using SakerWatch.MarketData.Application.Prices;
using SakerWatch.MarketData.Application.Providers;
using SakerWatch.MarketData.Domain.Markets;
using SakerWatch.MarketData.Domain.Stocks;

namespace SakerWatch.MarketData.Application.Ingestion;

/// <summary>
/// The jobs an ingestion job leaves behind: a continuation when the provider's call budget is spent, or a backfill when a listing has no history
/// or its adjusted history was rewritten.
///
/// A continuation puts a job back in the queue, to run once the provider's call budget has room again, instead of failing it. The budget (eight credits a
/// minute on the free plan) is far smaller than the number of listings a night can have, so most jobs meet an exhausted budget; a failed attempt
/// per refusal would use up a job's few attempts in the first minute and park almost all of them as dead. A wait is not an attempt: the job ends
/// successfully and a continuation, with its own key, becomes due when the exhausted window ends.
/// </summary>
public sealed partial class FollowUpJobs(IJobEnqueuer jobs, IOptions<IngestionOptions> options, TimeProvider clock, IOptions<PerformanceCacheOptions>? cache = null)
{
    [GeneratedRegex(":w\\d+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex WaitSuffix();

    /// <summary>The key of the continuation after <paramref name="waits"/> waits: the job's own key with a <c>:w{n}</c> suffix.</summary>
    public static string ContinuationKey(string currentJobKey, int waits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentJobKey);
        return $"{WaitSuffix().Replace(currentJobKey, string.Empty)}:w{waits}";
    }

    /// <summary>
    /// Queues <paramref name="continuation"/> to run after the wait the provider asked for. Returns false, and queues nothing, when the job has
    /// already waited as often as allowed, so the caller fails it normally.
    /// </summary>
    public async Task<bool> TryWaitAsync<TPayload>(TPayload continuation, int waitsSoFar, string currentJobKey, Error error, int maxAttempts, CancellationToken cancellationToken)
        where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(error);
        IngestionOptions settings = options.Value;
        if (waitsSoFar >= settings.MaxRateLimitWaits)
        {
            return false;
        }

        string key = ContinuationKey(currentJobKey, waitsSoFar + 1);
        // A spread that depends on the job, not on chance: the same job always waits the same extra seconds, and different jobs differ.
        double jitter = settings.RateLimitJitterSeconds == 0 ? 0 : (uint)HashCode.Combine(key) % (settings.RateLimitJitterSeconds + 1);
        TimeSpan wait = (ProviderErrors.RetryAfter(error) ?? TimeSpan.FromSeconds(60)) + TimeSpan.FromSeconds(jitter);
        await jobs.EnqueueAsync(key, continuation, new JobEnqueueOptions(AvailableAt: clock.GetUtcNow() + wait, MaxAttempts: maxAttempts), cancellationToken);
        return true;
    }

    /// <summary>Queues the first backfill of a listing that has no history yet. Asking twice queues it once.</summary>
    public Task<bool> QueueBackfillAsync(StockId id, CancellationToken cancellationToken) =>
        jobs.EnqueueAsync(BackfillStockJob.KeyFor(id), new BackfillStockJob(id.Value), new JobEnqueueOptions(MaxAttempts: options.Value.BackfillMaxAttempts), cancellationToken);

    /// <summary>
    /// Queues the backfill of a listing that still has no history on <paramref name="day"/>, once a day. The first backfill's key is taken for good
    /// once it was queued (a dead job keeps it), so a listing whose first load died would otherwise stay empty for ever.
    /// </summary>
    public Task<bool> QueueBackfillAsync(StockId id, DateOnly day, CancellationToken cancellationToken) =>
        jobs.EnqueueAsync(BackfillStockJob.RetryKeyFor(id, day), new BackfillStockJob(id.Value), new JobEnqueueOptions(MaxAttempts: options.Value.BackfillMaxAttempts), cancellationToken);

    /// <summary>
    /// Queues the computation of the performance windows of the listings that load in the same slice of time as this one (<see cref="WarmPerformanceHandler"/>),
    /// unless the cache is off or warming is. The loads of a market in a slice share one job, which becomes due a minute after the slice ends, when they have
    /// committed. A follow-up that has no cache options (a tool, a test) warms nothing.
    /// </summary>
    public async Task<bool> QueueWarmAsync(Mic market, DateTimeOffset loadedAt, CancellationToken cancellationToken)
    {
        if (cache?.Value is not { Enabled: true, WarmAfterLoad: true } settings)
        {
            return false;
        }

        (DateTimeOffset from, DateTimeOffset to) = WarmPerformanceJob.SliceOf(loadedAt, settings.WarmBucketMinutes);
        return await jobs.EnqueueAsync(
            WarmPerformanceJob.KeyFor(market, from),
            new WarmPerformanceJob(market.Value, from, to),
            new JobEnqueueOptions(AvailableAt: to + TimeSpan.FromMinutes(1), MaxAttempts: 3),
            cancellationToken);
    }

    /// <summary>Queues a full reload of a listing whose adjusted history was rewritten, once per day of detection.</summary>
    public Task<bool> QueueReloadAsync(StockId id, DateOnly day, CancellationToken cancellationToken) =>
        jobs.EnqueueAsync(BackfillStockJob.ReloadKeyFor(id, day), new BackfillStockJob(id.Value), new JobEnqueueOptions(MaxAttempts: options.Value.BackfillMaxAttempts), cancellationToken);
}
