using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SakerWatch.BuildingBlocks.Persistence.Outbox;

/// <summary>
/// Worker loop that keeps delivering due outbox messages and sleeps when none is due. On shutdown the message being
/// delivered is rolled back and stays pending, so no work is lost and nothing is half applied.
/// </summary>
/// <param name="processor">Delivers one message at a time.</param>
/// <param name="options">Polling settings.</param>
/// <param name="timeProvider">Injected clock for the idle delay.</param>
/// <param name="logger">Dispatcher logger.</param>
public sealed partial class OutboxDispatcherService(
    OutboxProcessor processor,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool delivered = false;
            try
            {
                delivered = await processor.ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Infrastructure trouble (database down): log and back off instead of crashing the Worker.
                LoopFailed(ex);
            }

            if (!delivered)
            {
                try
                {
                    await Task.Delay(options.Value.PollInterval, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    [LoggerMessage(EventId = 9504, Level = LogLevel.Error, Message = "Outbox dispatcher iteration failed")]
    private partial void LoopFailed(Exception exception);
}
