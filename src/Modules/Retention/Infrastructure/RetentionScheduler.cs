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
/// A failure to enqueue is logged and retried at the next check; it never stops the worker.
/// </summary>
/// <param name="scopes">Creates the scope the enqueuer lives in.</param>
/// <param name="options">Schedule and mode.</param>
/// <param name="timeProvider">Injected clock.</param>
/// <param name="logger">Logger.</param>
public sealed partial class RetentionScheduler(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, TimeProvider timeProvider, ILogger<RetentionScheduler> logger) : BackgroundService
{
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

        using var timer = new PeriodicTimer(settings.CheckInterval, timeProvider);
        do
        {
            await EnsureTodaysJobAsync(settings, stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>Enqueues today's job if it is not there yet; returns whether this call created it.</summary>
    public async Task<bool> EnsureTodaysJobAsync(RetentionOptions settings, CancellationToken cancellationToken)
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

            return created;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EnqueueFailed(ex);
            return false;
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 9702, Level = LogLevel.Information, Message = "Retention is disabled; no daily run will be scheduled")]
    private partial void Disabled();

    [LoggerMessage(EventId = 9703, Level = LogLevel.Information, Message = "Retention run enqueued for today ({Mode})")]
    private partial void Enqueued(string mode);

    [LoggerMessage(EventId = 9704, Level = LogLevel.Warning, Message = "Could not enqueue today's retention run; it will be retried at the next check")]
    private partial void EnqueueFailed(Exception exception);
}
