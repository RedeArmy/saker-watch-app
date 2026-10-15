using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SakerWatch.BuildingBlocks.Application.Jobs;
using SakerWatch.Retention.Application;

namespace SakerWatch.Retention.Infrastructure;

/// <summary>
/// Makes sure each UTC day has one retention job in the queue. The job key is deterministic (<c>retention:{mode}:{yyyyMMdd}</c>)
/// and enqueuing is <c>ON CONFLICT DO NOTHING</c>, so any number of workers, restarts or checks per day enqueue it once.
/// A failure to enqueue (for example the database is not migrated yet when the worker starts) is logged and retried after
/// <see cref="RetryDelay"/>, not after a whole check interval; it never stops the worker.
/// </summary>
/// <param name="scopes">Creates the scope the enqueuer lives in.</param>
/// <param name="options">Schedule and mode.</param>
/// <param name="timeProvider">Injected clock.</param>
/// <param name="logger">Logger.</param>
public sealed partial class RetentionScheduler(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, TimeProvider timeProvider, ILogger<RetentionScheduler> logger) : BackgroundService
{
    /// <summary>How soon to try again after a failure to enqueue (never longer than the check interval).</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>Job key of the run for <paramref name="day"/> in the given mode.</summary>
    public static string JobKey(DateTimeOffset day, bool dryRun) => $"retention:{(dryRun ? "dryrun" : "delete")}:{day.UtcDateTime:yyyyMMdd}";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RetentionOptions settings = options.Value;
        if (!settings.Enabled)
        {
            Disabled();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            RetentionScheduleOutcome outcome = await EnsureTodaysJobAsync(settings, stoppingToken);
            TimeSpan wait = outcome == RetentionScheduleOutcome.Failed ? TimeSpan.FromTicks(Math.Min(RetryDelay.Ticks, settings.CheckInterval.Ticks)) : settings.CheckInterval;
            try
            {
                await Task.Delay(wait, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Enqueues today's job if it is not there yet.</summary>
    public async Task<RetentionScheduleOutcome> EnsureTodaysJobAsync(RetentionOptions settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            var enqueuer = scope.ServiceProvider.GetRequiredService<IJobEnqueuer>();
            bool created = await enqueuer.EnqueueAsync(JobKey(timeProvider.GetUtcNow(), settings.DryRun), new RetentionRunJob(settings.DryRun), options: null, cancellationToken);
            if (created)
            {
                Enqueued(settings.DryRun ? "dry_run" : "delete");
            }

            return created ? RetentionScheduleOutcome.Created : RetentionScheduleOutcome.AlreadyScheduled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EnqueueFailed(ex);
            return RetentionScheduleOutcome.Failed;
        }
    }

    [LoggerMessage(EventId = 9702, Level = LogLevel.Information, Message = "Retention is disabled; no daily run will be scheduled")]
    private partial void Disabled();

    [LoggerMessage(EventId = 9703, Level = LogLevel.Information, Message = "Retention run enqueued for today ({Mode})")]
    private partial void Enqueued(string mode);

    [LoggerMessage(EventId = 9704, Level = LogLevel.Warning, Message = "Could not enqueue today's retention run; it will be retried shortly")]
    private partial void EnqueueFailed(Exception exception);
}

/// <summary>What a scheduler check found.</summary>
public enum RetentionScheduleOutcome
{
    /// <summary>Today's job was not there and has been enqueued.</summary>
    Created,

    /// <summary>Today's job already exists (another check or another worker enqueued it).</summary>
    AlreadyScheduled,

    /// <summary>The job could not be enqueued; the caller retries soon.</summary>
    Failed,
}
