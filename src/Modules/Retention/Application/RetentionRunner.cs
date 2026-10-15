using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.Retention.Domain;

namespace SakerWatch.Retention.Application;

/// <summary>Storage port of the retention rules; implemented over PostgreSQL in Infrastructure.</summary>
public interface IRetentionStore
{
    /// <summary>How many rows of <paramref name="target"/> are older than <paramref name="cutoff"/>.</summary>
    Task<long> CountEligibleAsync(RetentionTarget target, DateTimeOffset cutoff, CancellationToken cancellationToken);

    /// <summary>Deletes up to <paramref name="batchSize"/> rows older than <paramref name="cutoff"/> and returns how many went.</summary>
    Task<int> PurgeBatchAsync(RetentionTarget target, DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken);
}

/// <summary>What one target contributed to a run.</summary>
/// <param name="Target">The data set.</param>
/// <param name="Rows">Rows that would be removed (dry run) or were removed.</param>
/// <param name="MoreRemaining">True when the batch limit stopped the run before the backlog was cleared.</param>
public sealed record RetentionTargetResult(RetentionTarget Target, long Rows, bool MoreRemaining);

/// <summary>The outcome of a run.</summary>
/// <param name="DryRun">True when nothing was deleted.</param>
/// <param name="RanAt">The clock reading the cutoffs were derived from.</param>
/// <param name="Targets">One entry per target.</param>
public sealed record RetentionReport(bool DryRun, DateTimeOffset RanAt, IReadOnlyList<RetentionTargetResult> Targets);

/// <summary>
/// Applies the retention rules. Re-runnable and safe to run twice: deleting rows that are already gone changes nothing, every
/// statement is bounded by a batch size, and a dry run counts without touching data. Metrics and logs carry only the target and
/// the mode (low cardinality), never row contents.
/// </summary>
/// <param name="store">Storage.</param>
/// <param name="options">Ages and limits.</param>
/// <param name="timeProvider">Injected clock.</param>
/// <param name="logger">Logger.</param>
public sealed partial class RetentionRunner(IRetentionStore store, IOptions<RetentionOptions> options, TimeProvider timeProvider, ILogger<RetentionRunner> logger)
{
    private static readonly Meter _meter = new("SakerWatch");
    private static readonly Counter<long> _rows = _meter.CreateCounter<long>(
        "sakerwatch_retention_rows_total",
        description: "Rows removed by retention, or that a dry run found removable.");

    /// <summary>Runs every target.</summary>
    public async Task<RetentionReport> RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        RetentionOptions settings = options.Value;
        DateTimeOffset now = timeProvider.GetUtcNow();
        var results = new List<RetentionTargetResult>();

        foreach (RetentionTarget target in Enum.GetValues<RetentionTarget>())
        {
            DateTimeOffset cutoff = now - AgeOf(target, settings);
            RetentionTargetResult result = dryRun
                ? new RetentionTargetResult(target, await store.CountEligibleAsync(target, cutoff, cancellationToken), MoreRemaining: false)
                : await PurgeAsync(target, cutoff, settings, cancellationToken);

            string name = target.ToString();
            string mode = dryRun ? "dry_run" : "delete";
            _rows.Add(result.Rows, new KeyValuePair<string, object?>("target", name), new KeyValuePair<string, object?>("mode", mode));
            TargetProcessed(name, mode, result.Rows, result.MoreRemaining);
            results.Add(result);
        }

        return new RetentionReport(dryRun, now, results);
    }

    private async Task<RetentionTargetResult> PurgeAsync(RetentionTarget target, DateTimeOffset cutoff, RetentionOptions settings, CancellationToken cancellationToken)
    {
        long total = 0;
        for (int batch = 0; batch < settings.MaxBatchesPerTarget; batch++)
        {
            int removed = await store.PurgeBatchAsync(target, cutoff, settings.BatchSize, cancellationToken);
            total += removed;
            if (removed < settings.BatchSize)
            {
                return new RetentionTargetResult(target, total, MoreRemaining: false);
            }
        }

        return new RetentionTargetResult(target, total, MoreRemaining: true);
    }

    // Idempotency keys carry their own expiry, so their cutoff is "now".
    private static TimeSpan AgeOf(RetentionTarget target, RetentionOptions settings) => target switch
    {
        RetentionTarget.IdempotencyKeys => TimeSpan.Zero,
        RetentionTarget.OutboxProcessed => TimeSpan.FromDays(settings.OutboxProcessedDays),
        RetentionTarget.InboxProcessed => TimeSpan.FromDays(settings.InboxProcessedDays),
        RetentionTarget.JobsSucceeded => TimeSpan.FromDays(settings.JobsSucceededDays),
        RetentionTarget.JobsDead => TimeSpan.FromDays(settings.JobsDeadDays),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown retention target."),
    };

    [LoggerMessage(EventId = 9701, Level = LogLevel.Information, Message = "Retention {Target} ({Mode}): {Rows} rows, more remaining: {MoreRemaining}")]
    private partial void TargetProcessed(string target, string mode, long rows, bool moreRemaining);
}
