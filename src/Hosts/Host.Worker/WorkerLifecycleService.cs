namespace SakerWatch.Hosts.Worker;

/// <summary>
/// Placeholder worker loop: logs start and graceful stop so the lifecycle is observable. The job dispatcher with
/// leases that replaces it arrives in milestone 1.3; it must release its leases in <c>StopAsync</c>.
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
