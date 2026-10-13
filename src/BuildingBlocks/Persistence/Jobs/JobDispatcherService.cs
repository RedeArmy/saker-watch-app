using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SakerWatch.BuildingBlocks.Persistence.Jobs;

/// <summary>
/// Worker loops that keep running due jobs: <see cref="JobOptions.MaxConcurrency"/> loops claim and run jobs, and one
/// loop samples the queue for metrics. On shutdown every running job is rolled back and its lease released.
/// </summary>
/// <param name="processor">Claims and runs one job at a time.</param>
/// <param name="metrics">Queue gauges.</param>
/// <param name="options">Polling and concurrency settings.</param>
/// <param name="postgres">Connection settings for the sampling query.</param>
/// <param name="timeProvider">Injected clock.</param>
/// <param name="logger">Dispatcher logger.</param>
public sealed partial class JobDispatcherService(
    JobProcessor processor,
    JobQueueMetrics metrics,
    IOptions<JobOptions> options,
    IOptions<PostgresOptions> postgres,
    TimeProvider timeProvider,
    ILogger<JobDispatcherService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IEnumerable<Task> loops = Enumerable.Range(0, options.Value.MaxConcurrency).Select(_ => RunLoopAsync(stoppingToken));
        return Task.WhenAll(loops.Append(SampleLoopAsync(stoppingToken)));
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool ran = false;
            try
            {
                ran = await processor.ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Infrastructure trouble (database down): log and back off instead of crashing the Worker.
                LoopFailed(ex);
            }

            if (!ran && !await DelayAsync(options.Value.PollInterval, stoppingToken))
            {
                return;
            }
        }
    }

    private async Task SampleLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await metrics.RefreshAsync(postgres.Value.Postgres, timeProvider.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SamplingFailed(ex);
            }

            if (!await DelayAsync(options.Value.StatsInterval, stoppingToken))
            {
                return;
            }
        }
    }

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, timeProvider, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(EventId = 9516, Level = LogLevel.Error, Message = "Job dispatcher iteration failed")]
    private partial void LoopFailed(Exception exception);

    [LoggerMessage(EventId = 9517, Level = LogLevel.Warning, Message = "Sampling the job queue for metrics failed")]
    private partial void SamplingFailed(Exception exception);
}
