namespace SakerWatch.Hosts.Worker;

/// <summary>
/// Logs start and graceful stop of the worker host so its lifecycle is observable. The job dispatcher and the outbox
/// processor are separate hosted services that release their leases in <c>StopAsync</c>.
/// </summary>
/// <param name="logger">Worker logger.</param>
internal sealed partial class WorkerLifecycleService(ILogger<WorkerLifecycleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkerStarted();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            WorkerStopping();
        }
    }

    [LoggerMessage(EventId = 9201, Level = LogLevel.Information, Message = "Worker started")]
    private partial void WorkerStarted();

    [LoggerMessage(EventId = 9202, Level = LogLevel.Information, Message = "Worker stopping gracefully")]
    private partial void WorkerStopping();
}
